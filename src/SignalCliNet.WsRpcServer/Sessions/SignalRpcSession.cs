using System.Buffers;
using System.Buffers.Text;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetCoreServer;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Serialization;
using StreamJsonRpc;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Services;
using WsRpcServer.Sessions;
using WebSocketMessageHandler = WsRpcServer.Transport.WebSocketMessageHandler;

namespace SignalCliNet.WsRpcServer.Sessions;

/// <summary>
/// Manages WebSocket connection with JSON-RPC protocol support.
/// Uses NetCoreServer's built-in capabilities for WebSocket protocol handling.
/// </summary>
/// <remarks>
/// Auth-стан-машина (section 2): з <c>Server:Auth:Enabled=false</c> (дефолт) сесія одразу активна з
/// <see cref="SignalPrincipal.LoopbackFullAccess"/> — поведінка Фази 1 незмінна. З увімкненим auth сесія
/// автентифікується токеном (субпротокол на upgrade або fallback <c>authenticate</c>); token-відмови
/// закриваються in-band close-кодом <c>4401</c> (T4), auth-таймаут — <c>4408</c>.
/// </remarks>
public sealed class SignalRpcSession : AbstractJsonRpcSession
{
    /// <summary>Upper bound on a pre-auth message (bytes) — the fallback <c>authenticate</c> is tiny.</summary>
    private const int PreAuthMaxBytes = 4096;

    private readonly IRpcServiceRegistry _serviceRegistry;
    private readonly IEventProcessor _eventProcessor;
    private readonly IRpcPolicyRegistry _policyRegistry;
    private readonly Channel<RpcNotification> _notificationChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly WebSocketMessageHandler _messageHandler;
    private readonly JsonRpcServerConfig _config;

    // Section 2/3 auth-залежності (усі singletons/options з DI).
    private readonly AuthOptions _authOptions;
    private readonly TokenService _tokenService;
    private readonly IDurableStore _store;
    private readonly UnauthenticatedConnectionLimiter _unauthLimiter;
    private readonly RevocationBroadcaster _revocationBroadcaster;
    private readonly RenewalRateLimiter _renewalRateLimiter;

    // Section 3/4 admission-залежності (singletons/options з DI). Активні лише коли Admission:Enabled.
    private readonly AdmissionControl _admissionControl;
    private readonly IdentityConnectionLimiter _identityConnLimiter;
    private readonly SignalCliGate _signalCliGate;
    private readonly AdmissionOptions _admissionOptions;
    private readonly TimeProvider _timeProvider;

    // Group-claim send-gate (add-group-claim-receive). Активний лише коли GroupClaim:Enabled (сам gate
    // гейтить прапорцем); передається у chokepoint, який нулить його, якщо фіча вимкнена. Опційний:
    // мінімальні DI-провайдери (деякі DoD-тести без AddGroupClaimCore) → null (gate неактивний).
    private readonly GroupSendGate? _groupSendGate;

    private int _identitySlotHeld;              // 0/1 — тримаємо per-identity conn-слот (release рівно раз)
    private MessageRateLimiter? _msgRateLimiter; // per-connection token-bucket (msg-rate); null коли вимкнено
    private Timer? _idleTimer;                   // idle-timeout таймер (переармовується на кожен data-кадр)
    private TimeSpan _idleTimeout;               // тривалість idle-вікна (з AdmissionOptions)

    // Чиста стан-машина переходів; доступ серіалізуємо через _authLock (сесію смикають кілька потоків:
    // accept-шлях, IO-потік, callback таймера, callback revoke).
    private readonly SessionAuthStateMachine _authMachine;
    private readonly object _authLock = new();

    // Токен-кандидат із upgrade — REDACT: ніколи не логувати; чистимо одразу після автентифікації.
    private string? _pendingToken;
    private string? _remoteIp;
    private int _unauthSlotHeld;           // 0/1 — тримаємо unauth-слот у per-IP cap (release рівно раз)
    private string? _tokenHash;            // at-rest hash активної token-сесії (для IsLive-перевірки)
    private string? _identityId;
    private IDisposable? _revocationSubscription;
    private Timer? _authTimer;

    // PoP-стан (section 3): живе лише в пам'яті сесії між pop.challenge і pop.prove. Nonce one-time
    // (одна спроба на challenge), TTL = загальний auth-таймаут (4408 покриває, окремого таймера не треба).
    private string? _popNonce;             // base64url-рядок nonce, як надіслано у challenge
    private string? _popConnId;            // connId, до якого прив'язаний підпис (session Id)
    private IdentityRecord? _popIdentity;  // identity, що проходить PoP
    private string? _popTokenHash;         // at-rest hash токена (для normal — свій; для renewal — старий)
    private bool _popPendingRenewal;       // true ⇒ T5: успішний PoP ротує прострочений токен

    private Task? _processingTask;

    // A4: teardown має відпрацювати рівно один раз і детерміновано (без гонки OnWsClose ↔ OnWsDisconnected).
    private int _teardownStarted;

    public SignalRpcSession(
        WsServer server,
        IServiceProvider serviceProvider,
        ILogger<SignalRpcSession> logger,
        IRpcServiceRegistry serviceRegistry,
        IEventProcessor eventProcessor,
        IRpcPolicyRegistry policyRegistry,
        JsonRpcServerConfig config,
        IOptions<AuthOptions> authOptions,
        TokenService tokenService,
        IDurableStore store,
        UnauthenticatedConnectionLimiter unauthLimiter,
        RevocationBroadcaster revocationBroadcaster,
        RenewalRateLimiter renewalRateLimiter,
        AdmissionControl admissionControl,
        IdentityConnectionLimiter identityConnLimiter,
        SignalCliGate signalCliGate,
        IOptions<AdmissionOptions> admissionOptions,
        TimeProvider timeProvider,
        GroupSendGate? groupSendGate = null)
        : base(server, logger, config)
    {
        _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _serviceRegistry = serviceRegistry ?? throw new ArgumentNullException(nameof(serviceRegistry));
        _policyRegistry = policyRegistry ?? throw new ArgumentNullException(nameof(policyRegistry));
        _authOptions = (authOptions ?? throw new ArgumentNullException(nameof(authOptions))).Value;
        _tokenService = tokenService ?? throw new ArgumentNullException(nameof(tokenService));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _unauthLimiter = unauthLimiter ?? throw new ArgumentNullException(nameof(unauthLimiter));
        _revocationBroadcaster = revocationBroadcaster ?? throw new ArgumentNullException(nameof(revocationBroadcaster));
        _renewalRateLimiter = renewalRateLimiter ?? throw new ArgumentNullException(nameof(renewalRateLimiter));
        _admissionControl = admissionControl ?? throw new ArgumentNullException(nameof(admissionControl));
        _identityConnLimiter = identityConnLimiter ?? throw new ArgumentNullException(nameof(identityConnLimiter));
        _signalCliGate = signalCliGate ?? throw new ArgumentNullException(nameof(signalCliGate));
        _admissionOptions = (admissionOptions ?? throw new ArgumentNullException(nameof(admissionOptions))).Value;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _groupSendGate = groupSendGate;   // опційний (null у мінімальних DI-провайдерах)

        _authMachine = new SessionAuthStateMachine(_authOptions.Enabled);

        // Create message handler that will pass data to StreamJsonRpc
        _messageHandler = ActivatorUtilities.CreateInstance<WebSocketMessageHandler>(
            serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider)), this, CreateJsonFormatter(),
            _config);

        // Configure channel for asynchronous notification processing with queue size limit
        _notificationChannel = Channel.CreateBounded<RpcNotification>(
            new BoundedChannelOptions(_config.NotificationQueueSize)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropOldest
            });

        Logger.LogInformation("Created new WebSocket session: {Id}", Id);
    }

    /// <summary>
    /// Creates and configures JSON formatter with optimized parameters
    /// </summary>
    private IJsonRpcMessageFormatter CreateJsonFormatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;

        // Configure serialization context for better performance and AOT support
        // Source-gen context provides fast-path metadata for the registered Signal types;
        // the reflection-based fallback resolves any other SignalCli payload type (e.g.
        // types carrying their own custom [JsonConverter], like ListAccountsResponse, whose
        // internal converter the source generator cannot reference).
        formatter.JsonSerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
            SignalCliSerializerContext.Default,
            new DefaultJsonTypeInfoResolver()
        );

        Logger.LogDebug("Configured JSON formatter for session {Id}", Id);
        return formatter;
    }

    /// <summary>
    /// NetCoreServer seam invoked during the 101 upgrade (V4/W11). We capture the bearer subprotocol +
    /// Origin here, enforce the Origin allowlist (C3) and decide whether to complete the upgrade. When the
    /// upgrade is allowed we delegate to <c>base.OnWsConnecting</c>, which calls
    /// <see cref="NegotiateSubprotocol"/> and correctly rebuilds the 101 with the echoed subprotocol (C4).
    /// Returning <c>false</c> cancels the upgrade — we then send the HTTP rejection ourselves and disconnect.
    /// </summary>
    public override bool OnWsConnecting(HttpRequest request, HttpResponse response)
    {
        // Auth вимкнено → нічого не робимо: 101 іде як є (поведінка Фази 1 незмінна).
        if (!_authOptions.Enabled)
            return true;

        _remoteIp = (Socket?.RemoteEndPoint as IPEndPoint)?.Address.ToString();

        var (subprotocols, origin) = ReadHandshakeHeaders(request);

        // Токен-кандидат = ПЕРШИЙ субпротокол із префіксом ptzh_. Захоплюємо його тут (ДО апгрейду);
        // рішення про echo лишається за NegotiateSubprotocol (токен НІКОЛИ не echo-иться).
        string? tokenCandidate = null;
        foreach (var proto in subprotocols)
        {
            if (proto.StartsWith(TokenFormat.Prefix, StringComparison.Ordinal))
            {
                tokenCandidate = proto;
                break;
            }
        }

        // REDACT: логуємо лише факти/кількості — НІКОЛИ значення субпротоколу/токена/Origin.
        Logger.LogDebug(
            "Сесія {ClientId}: upgrade (субпротоколів={Count}, токен={HasToken}, origin={HasOrigin}).",
            Id, subprotocols.Count, tokenCandidate is not null, origin is not null);

        // C3 Origin-allowlist (defense-in-depth, НЕ автентифікація): якщо Origin присутній — він МУСИТЬ
        // збігтися (OrdinalIgnoreCase) з allowlist, інакше відмова ДО апгрейду. Порожній allowlist =
        // будь-який Origin відхиляється (fail-closed). Запити без Origin (не-браузери) — пропускаємо.
        if (origin is not null && !IsOriginAllowed(origin))
        {
            Logger.LogWarning("Сесія {ClientId}: Origin не в allowlist — відмова 403 до апгрейду.", Id);
            RejectBeforeUpgrade(response, 403, "Forbidden");
            return false;
        }

        if (tokenCandidate is not null)
        {
            // Токен поданий: НЕ вирішуємо тут (T4 — рішення про 4401 у OnWsConnected, щоб браузер побачив
            // close-code, а не HTTP-фейл). Навіть якщо TryParse провалиться — теж T4-кейс (4401 invalid).
            if (!TryAcquireUnauthSlot())
            {
                RejectBeforeUpgrade(response, 429, "Too Many Requests");
                return false;
            }

            _pendingToken = tokenCandidate;   // REDACT — не логувати; чистимо в OnWsConnected

            // Дозволяємо апгрейд через базу (2.8.0 seam): вона викличе NegotiateSubprotocol і коректно
            // перебудує 101 з echo не-токен субпротоколу (C4/W11) — інкапсулює обхід дефекту NetCoreServer.
            return base.OnWsConnecting(request, response);
        }

        // Токена немає взагалі:
        if (origin is null)
        {
            // Не-браузерний клієнт без токена → голий HTTP 401 ДО апгрейду.
            RejectBeforeUpgrade(response, 401, "Unauthorized");
            return false;
        }

        // Браузер (Origin пройшов allowlist), але без токена → дозволяємо upgrade і чекаємо fallback
        // authenticate під auth-таймаутом.
        if (!TryAcquireUnauthSlot())
        {
            RejectBeforeUpgrade(response, 429, "Too Many Requests");
            return false;
        }

        return base.OnWsConnecting(request, response);
    }

    /// <summary>
    /// Subprotocol-negotiation hook (2.8.0 seam) called by <c>base.OnWsConnecting</c> while it rebuilds the
    /// 101 response. Echoes the FIRST non-token subprotocol (C4/W11); the token element (prefixed with
    /// <see cref="TokenFormat.Prefix"/>) is NEVER echoed — echoing it would leak the bearer token into the
    /// upgrade response. Returns <c>null</c> when only a token (or nothing) was offered.
    /// </summary>
    protected override string? NegotiateSubprotocol(IReadOnlyList<string> offered)
    {
        foreach (var proto in offered)
        {
            if (!proto.StartsWith(TokenFormat.Prefix, StringComparison.Ordinal))
                return proto;
        }

        return null;
    }

    /// <summary>
    /// Called by NetCoreServer when the WebSocket upgrade completed. Resolves the auth outcome: no auth →
    /// loopback activation; token supplied → authenticate now and either activate or close <c>4401</c>;
    /// no token (browser) → await the fallback <c>authenticate</c> under the auth timeout.
    /// </summary>
    public override void OnWsConnected(HttpRequest request)
    {
        Logger.LogInformation("WebSocket connection established: {ClientId}", Id);

        if (!_authOptions.Enabled)
        {
            // Фаза 1: auth вимкнено → одразу активна сесія з повним loopback-доступом.
            ActivateSession(SignalPrincipal.LoopbackFullAccess);
            return;
        }

        if (_pendingToken is not null)
        {
            // Токен був на upgrade → автентифікуємо тут (in-band, T4). Валідний → pop.challenge (PoPPending).
            var result = _tokenService.Authenticate(_pendingToken);
            _pendingToken = null;   // REDACT: більше не тримаємо plaintext
            ResolveToken(result, fromFallback: false);
            return;
        }

        // Браузерний fallback: чекаємо перше повідомлення authenticate + запускаємо auth-таймер.
        StartAuthTimer();
        Logger.LogInformation(
            "Сесія {ClientId}: очікування fallback authenticate (таймаут {Timeout}с).",
            Id, _authOptions.AuthTimeoutSeconds);
    }

    /// <summary>
    /// Wires up the JSON-RPC dispatch stack for an authenticated (or loopback) session: the authorizing
    /// chokepoint carrying <paramref name="principal"/>, RPC method registration, event registration and
    /// the notification loop. Extracted so the auth state machine has one activation seam.
    /// </summary>
    private void ActivateSession(SignalPrincipal principal)
    {
        // Знімаємо сокет з обліку неавтентифікованих і глушимо auth-таймер — сесія стала активною.
        ReleaseUnauthSlot();
        StopAuthTimer();

        // Транспортні ліміти (admission, tasks 3.2/3.3) — лише коли Admission:Enabled. Вимкнено ⇒ усі
        // нові параметри chokepoint'а null/0, тож поведінка ідентична попередній (опт-ін, як auth).
        Func<IReadOnlyList<string>, AdmissionDecision>? admitSends = null;
        SignalCliGate? gate = null;
        var maxInFlight = 0;
        if (_admissionOptions.Enabled)
        {
            // Per-identity conn cap: слот бере ЛИШЕ token-principal; loopback (Disabled) поза cap —
            // у нього немає identity. Перевищення cap → close 4429 (too-many-connections), без активації.
            if (_identityId is not null)
            {
                if (!_identityConnLimiter.TryAcquire(_identityId))
                {
                    Logger.LogWarning(
                        "Сесія {ClientId}: перевищено per-identity cap конектів ({Max}) — close 4429.",
                        Id, _identityConnLimiter.Max);
                    CloseRateLimited(AuthCloseCodes.RateLimited, RateLimitCloseReasons.TooManyConnections);
                    return;
                }

                Volatile.Write(ref _identitySlotHeld, 1);
            }

            // Admission-identity: token → реальна identity; loopback (auth вимкнено, admission увімкнено)
            // → спільний ключ "loopback" (спільний бюджет/floor на одну identity — задокументовано).
            var admissionIdentity = _identityId ?? "loopback";
            admitSends = recipients => _admissionControl.Admit(admissionIdentity, recipients);
            gate = _signalCliGate;
            maxInFlight = _admissionOptions.MaxInFlightPerConnection;
        }

        try
        {
            // D9/1.5: для token-сесій передаємо замикання re-чеку живучості (по збереженому tokenHash,
            // без повторного HMAC). Для loopback — null (поведінка незмінна).
            Func<bool>? isCredentialLive = _tokenHash is null ? null : () => _tokenService.IsLive(_tokenHash);

            // V9: pre-dispatch chokepoint замість голого JsonRpc — кожен виклик проходить герметичний
            // default-deny + IDOR-guard + санітизацію помилок. Principal сесії читається per-invocation.
            // admitSends/gate/maxInFlight — send-admission (W16) + G6-гейт + in-flight cap (лише коли увімкнено).
            // groupSendGate — claim-гейт групових send'ів (сам нулиться в chokepoint, якщо GroupClaim вимкнено).
            JsonRpc = new AuthorizingSignalJsonRpc(
                _messageHandler, principal, _policyRegistry, Logger, isCredentialLive, admitSends, gate, maxInFlight,
                _groupSendGate);

            // Configure JSON-RPC
            JsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;

            // ВАЖЛИВО (privacy, CLAUDE rule #4): НЕ вмикати ConsoleTraceListener на TraceSource —
            // StreamJsonRpc на рівні Information трейсить повні тіла RPC (включно з params/токенами)
            // у консоль (= docker logs). Діагностику — лише вибірково через структуроване логування.

            // Register RPC methods
            RegisterRpcMethods(JsonRpc);

            // Register client for receiving events
            _eventProcessor.RegisterClient(Id, SendNotificationAsync);

            // Start listening for JSON-RPC
            JsonRpc.StartListening();
            Logger.LogDebug("JSON-RPC started listening for client {ClientId}", Id);

            // Start notification processing
            _processingTask = ProcessNotificationsAsync(_cts.Token);

            // Per-connection msg-rate bucket + idle-таймер (task 3.2) — лише коли admission увімкнено.
            // Bucket: ємність = MessagesPerMinutePerConnection, рефіл 1хв монотонним годинником (D15).
            if (_admissionOptions.Enabled)
            {
                _msgRateLimiter = new MessageRateLimiter(
                    _admissionOptions.MessagesPerMinutePerConnection, TimeSpan.FromMinutes(1), _timeProvider);
                _idleTimeout = TimeSpan.FromMinutes(_admissionOptions.IdleTimeoutMinutes);
                _idleTimer = new Timer(_ => OnIdleTimeout(), null, _idleTimeout, Timeout.InfiniteTimeSpan);
            }

            Logger.LogInformation("Сесія {ClientId} активована (principal {Principal}).", Id, principal.Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error initializing JSON-RPC for session {ClientId}", Id);
            Close(WebSocketCloseStatus.InternalServerError, "Failed to initialize session");
            throw;
        }
    }

    /// <summary>
    /// Registers RPC methods through registry and direct interface registration
    /// </summary>
    private void RegisterRpcMethods(JsonRpc jsonRpc)
    {
        Logger.LogDebug("Registering RPC methods for client {ClientId}", Id);

        _serviceRegistry.RegisterServices(jsonRpc, Id);

        Logger.LogDebug("RPC methods registered for client {ClientId}", Id);
    }

    // Розв'язує presented-токен (з upgrade АБО з fallback authenticate). Валідний токен → pop.challenge
    // (PoPPending); прострочений у fallback-шляху з живим device-ключем і в межах rate-limit → pop.challenge
    // на renewal (T5); інакше — close 4401 з машиночитним reason. fromFallback=true ⇒ це повідомлення
    // authenticate (не upgrade-субпротокол) — renewal доступний ЛИШЕ звідси (upgrade expired → 4401 expired).
    private void ResolveToken(TokenAuthenticationResult result, bool fromFallback)
    {
        var status = result.Status;

        // Valid: identity мусить існувати у сторі, інакше токен трактуємо як невалідний.
        IdentityRecord? identity = null;
        if (status == TokenAuthenticationStatus.Valid)
        {
            identity = _store.GetIdentity(result.IdentityId!);
            if (identity is null)
                status = TokenAuthenticationStatus.Invalid;
        }

        // T5 renewal: прострочений (НЕ revoked — revoke виграє ще в Authenticate) токен через fallback
        // authenticate, за наявності живого device-секрету й у межах rate-limit → замість close видаємо
        // pop.challenge із прапорцем pending-renewal. Інакше (нема ключа / rate-limit) → close 4401 expired.
        var pendingRenewal = false;
        if (status == TokenAuthenticationStatus.Expired && fromFallback)
        {
            identity = _store.GetIdentity(result.IdentityId!);
            if (identity is not null && HasLiveDeviceSecret(identity.Id) &&
                _renewalRateLimiter.TryAcquire(identity.Id))
            {
                pendingRenewal = true;
            }
            else
            {
                identity = null;   // не кваліфікувались → close 4401 expired нижче
                Logger.LogInformation(
                    "Сесія {ClientId}: renewal недоступний (нема живого device-ключа або перевищено rate-limit) — close 4401 expired.",
                    Id);
            }
        }

        // Валідний токен АБО кваліфікований renewal → PoPPending; інакше — close-рішення за статусом.
        AuthDecision decision;
        lock (_authLock)
        {
            decision = status == TokenAuthenticationStatus.Valid || pendingRenewal
                ? _authMachine.OnTokenValidated(TokenAuthenticationStatus.Valid)   // → PoPPending
                : _authMachine.OnTokenValidated(status);                            // → close 4401 reason
        }

        if (decision.NextState == SessionAuthState.PoPPending)
            BeginPopChallenge(identity!, result.TokenHash!, pendingRenewal);
        else if (decision.ShouldClose)
            CloseAuth(decision.CloseCode, decision.Reason ?? AuthCloseReasons.Invalid);
    }

    // Готує PoP-стан і надсилає pop.challenge (nonce ‖ connId). Стартує auth-таймер, якщо він ще не йде
    // (upgrade-шлях: таймера не було; fallback-шлях: таймер уже тикає з OnWsConnected — TTL той самий, 4408).
    private void BeginPopChallenge(IdentityRecord identity, string tokenHash, bool pendingRenewal)
    {
        // Nonce: 32 байти CSPRNG (256-bit, ≥128-bit за G8) у base64url — саме цей рядок увійде в pre-image
        // підпису. Прив'язка до connId (session Id) робить підпис bound до з'єднання (релей ламається).
        var nonce = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var connId = Id.ToString();

        _popNonce = nonce;
        _popConnId = connId;
        _popIdentity = identity;
        _popTokenHash = tokenHash;
        _popPendingRenewal = pendingRenewal;

        EnsureAuthTimer();
        SendPopChallenge(nonce, connId);

        Logger.LogInformation(
            "Сесія {ClientId}: видано pop.challenge (identity {IdentityId}, renewal={Renewal}).",
            Id, identity.Id, pendingRenewal);
    }

    // Обробляє pop.prove: верифікує підпис device-ключем над (nonce ‖ connId), re-чек liveness токена й
    // активує сесію (для renewal — ротує прострочений токен у W21-транзакції). Nonce one-time: будь-який
    // провал → close (pop-failed/revoked), без re-issue challenge.
    private void HandlePopProve(string? signature, JsonElement? ackId)
    {
        // 1. Верифікація підпису: потрібен зареєстрований, НЕзреволкнутий device-ключ + валідний підпис.
        var deviceSecret = _popIdentity is null ? null : _store.GetDeviceSecret(_popIdentity.Id);
        var verified =
            deviceSecret is not null &&
            !deviceSecret.IsRevoked &&
            DevicePopVerifier.Verify(
                deviceSecret.PublicKey, _popNonce ?? string.Empty, _popConnId ?? string.Empty,
                signature ?? string.Empty);

        if (!verified)
        {
            AuthDecision failed;
            lock (_authLock)
            {
                failed = _authMachine.OnPopFailed();
            }

            if (failed.ShouldClose)
                CloseAuth(failed.CloseCode, failed.Reason ?? AuthCloseReasons.PopFailed);
            return;
        }

        // 2. Re-чек liveness ПЕРЕД активацією: revoke міг статись під час handshake (D7 — revoke виграє).
        //    Ротацію відкладаємо до виграшу переходу (крок 3), щоб гонка з timeout не лишила клієнта без
        //    нового токена (старий revoked, новий загублений).
        if (_popPendingRenewal)
        {
            var old = _store.GetToken(_popTokenHash!);
            if (old is null || old.IsRevoked)
            {
                CloseAuth(AuthCloseCodes.TokenRejected, AuthCloseReasons.Revoked);
                return;
            }
        }
        else if (!_tokenService.IsLive(_popTokenHash!))
        {
            // Normal-шлях: токен був Valid — мертвий (revoked/протух під час handshake) → 4401 revoked.
            CloseAuth(AuthCloseCodes.TokenRejected, AuthCloseReasons.Revoked);
            return;
        }

        // 3. Виграємо перехід PoPPending → Active атомарно; лише переможець ротує/активує.
        AuthDecision decision;
        lock (_authLock)
        {
            decision = _authMachine.OnPopSucceeded();
        }

        if (decision.NextState != SessionAuthState.Active)
            return;   // гонка: timeout/teardown уже закрив сесію — токен не чіпаємо

        // 4. Renewal-ротація (лише після виграшу): W21 — старий revoked + новий у одній транзакції.
        string? renewedToken = null;
        if (_popPendingRenewal)
        {
            var (newToken, newRecord) = _tokenService.Rotate(
                _popTokenHash!, _popIdentity!.Id, TimeSpan.FromHours(_authOptions.TokenTtlHours));
            _tokenHash = newRecord.TokenHash;
            renewedToken = newToken;
        }
        else
        {
            _tokenHash = _popTokenHash;
        }

        // 5. Активація: principal, revoke-підписка, ack pop.prove, старт диспетч-стеку.
        var identity = _popIdentity!;
        _identityId = identity.Id;

        var principal = new SignalPrincipal(
            name: identity.Id,
            isAdmin: string.Equals(identity.Role, "admin", StringComparison.Ordinal),
            hasFullAccess: false,
            allowedAccounts: identity.LinkedAccounts,
            // R3.2: видимість груп = активні group-claim binding-и identity (add-group-claim-receive).
            // Коли claim вимкнено — binding-таблиця порожня → allowedGroupIds порожній (поведінка незмінна).
            allowedGroupIds: ActiveGroupBindingIds(identity.Id));

        // Підписка на in-process revoke: revoke identity → close 4401 `revoked` живого конекту (1.4).
        _revocationSubscription = _revocationBroadcaster.Subscribe(identity.Id, OnIdentityRevoked);

        // Ack pop.prove: {"status":"ok"} (+ новий token для renewal), лише якщо клієнт надіслав id.
        if (ackId is not null)
            SendPopAck(ackId.Value, renewedToken);

        ClearPopState();
        ActivateSession(principal);
    }

    // Наявність живого (НЕзреволкнутого) device-секрету — передумова renewal.
    private bool HasLiveDeviceSecret(string identityId)
    {
        var secret = _store.GetDeviceSecret(identityId);
        return secret is not null && !secret.IsRevoked;
    }

    // Group-id-и активних (не revoked, не прострочених) claim-binding-ів identity — видимість груп для
    // listGroups-фільтра (R3.2). Порожньо, коли group-claim вимкнено (нема binding-ів).
    private string[] ActiveGroupBindingIds(string identityId)
    {
        var now = DateTimeOffset.UtcNow;
        return _store.GetGroupBindingsForIdentity(identityId)
            .Where(b => !b.Revoked && b.ExpiresAt > now)
            .Select(b => b.GroupId)
            .ToArray();
    }

    /// <summary>
    /// Called by NetCoreServer when the WebSocket disconnects. This is the authoritative, fully-awaited
    /// teardown path.
    /// </summary>
    /// <remarks>
    /// A4: сигнатура <c>async void</c> нав'язана NetCoreServer (перекриваємо void-callback), АЛЕ тіло
    /// повністю чекає на впорядкований <see cref="TeardownAsync"/> — це не fire-and-forget.
    /// </remarks>
    public override async void OnWsDisconnected()
    {
        try
        {
            Logger.LogInformation("WebSocket client disconnected: {ClientId}", Id);
            await TeardownAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during WebSocket disconnect for client {ClientId}", Id);
        }
    }

    /// <summary>
    /// Ordered, run-once session teardown: cancel → unregister → complete channel → dispose JsonRpc →
    /// drain the notification loop. Idempotent (A4).
    /// </summary>
    private async Task TeardownAsync()
    {
        // Ідемпотентність: рівно один виклик проходить далі (гонка OnWsClose ↔ OnWsDisconnected ↔ Dispose).
        if (Interlocked.Exchange(ref _teardownStarted, 1) != 0)
            return;

        if (IsDisposed)
            return;

        Logger.LogDebug("Cleaning up resources for client {ClientId}", Id);

        // Auth-cleanup: позначаємо стан-машину термінальною, звільняємо unauth-слот, глушимо таймер,
        // відписуємось від revoke. Усі кроки ідемпотентні.
        lock (_authLock)
        {
            _authMachine.OnClosed();
        }

        ReleaseUnauthSlot();
        ReleaseIdentitySlot();   // звільняємо per-identity conn-слот (ідемпотентно; loopback — no-op)
        StopAuthTimer();
        StopIdleTimer();
        ClearPopState();   // nonce one-time — не лишаємо в пам'яті після teardown
        _revocationSubscription?.Dispose();
        _revocationSubscription = null;

        try
        {
            // 1. Сигналізуємо скасування всіх фонових операцій.
            _cts.Cancel();

            // 2. Знімаємо клієнта з системи подій (більше жодних нотифікацій йому не адресується).
            _eventProcessor.UnregisterClient(Id);
            Logger.LogDebug("Client {ClientId} unregistered from event system", Id);

            // 3. Завершуємо канал сповіщень (цикл обробки вийде природно).
            _notificationChannel.Writer.TryComplete();
            Logger.LogDebug("Notification channel completed for client {ClientId}", Id);

            // 4. Диспоузимо JsonRpc (разом із message handler) — інлайн, у визначеному порядку.
            if (JsonRpc != null)
            {
                JsonRpc.Dispose();
                JsonRpc = null;
                Logger.LogDebug("JsonRpc disposed for client {ClientId}", Id);
            }

            // 5. Дренуємо фонову задачу обробки сповіщень із таймаутом.
            if (_processingTask != null)
            {
                await Task.WhenAny(_processingTask, Task.Delay(1000)).ConfigureAwait(false);
                _processingTask = null;
                Logger.LogDebug("Notification processing completed for client {ClientId}", Id);
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during session cleanup {ClientId}", Id);
        }
    }

    /// <summary>
    /// Processes incoming WebSocket data. In the pre-auth states only the fallback <c>authenticate</c> is
    /// accepted (handled here, not forwarded to the RPC stack); once active/disabled the message is passed
    /// to the JSON-RPC handler as before.
    /// </summary>
    public override void OnWsReceived(byte[] buffer, long offset, long size)
    {
        try
        {
            if (IsDisposed)
            {
                Logger.LogWarning("Received message after session disposal {ClientId}", Id);
                return;
            }

            // Check message size limit for DOS protection
            if (size > _config.MaxMessageSizeBytes)
            {
                Logger.LogWarning("Message exceeds maximum allowed size ({Size} > {MaxSize}) for client {ClientId}",
                    size, _config.MaxMessageSizeBytes, Id);

                Close(WebSocketCloseStatus.MessageTooBig, "Message exceeds size limit");
                return;
            }

            SessionAuthState state;
            lock (_authLock)
            {
                state = _authMachine.State;
            }

            // Pre-auth: усе, що не активна/disabled-сесія, йде в окремий обробник (НЕ в _messageHandler).
            if (state is not (SessionAuthState.Active or SessionAuthState.Disabled))
            {
                HandlePreAuthMessage(buffer, offset, size);
                return;
            }

            // Msg-rate + idle (admission, task 3.2): лише активні/loopback-сесії при Enabled. Сюди доходять
            // ЛИШЕ data-кадри (ping/pong обробляє OnWsPing у базі), тож token-bucket рахує саме RPC-трафік.
            if (_admissionOptions.Enabled)
            {
                var limiter = _msgRateLimiter;
                if (limiter is not null && !limiter.TryConsume())
                {
                    var retry = limiter.RetryAfterSeconds();
                    Logger.LogWarning(
                        "Сесія {ClientId}: перевищено msg-rate — close 4429 (retry {Retry}s).", Id, retry);
                    CloseRateLimited(AuthCloseCodes.RateLimited, RateLimitCloseReasons.FormatMessageRate(retry));
                    return;
                }

                // Прийнятий кадр — переармовуємо idle-таймер (мовчання довше за вікно → close 1000).
                ArmIdleTimer();
            }

            // NetCoreServer has already handled WebSocket fragmentation, pass the complete message to the handler
            var messageData = new ReadOnlyMemory<byte>(buffer, (int)offset, (int)size);

            Logger.LogDebug("Received message of size {Size} bytes for client {ClientId}", size, Id);

            // Asynchronously process message through WebSocketMessageHandler
            _ = _messageHandler.ProcessReceivedDataAsync(messageData);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing WebSocket message of size {Size} for client {ClientId}", size, Id);
        }
    }

    // Обробник pre-auth повідомлення. Один допустимий метод на стан: у AwaitingAuthenticate — authenticate
    // (params.token); у PoPPending — pop.prove (params.signature). Будь-що інше (в т.ч. authenticate вдруге
    // у PoPPending) / малформ / завеликий розмір → close 4401. REDACT: тіло містить token/signature —
    // його НІКОЛИ не логувати.
    private void HandlePreAuthMessage(byte[] buffer, long offset, long size)
    {
        if (size > PreAuthMaxBytes)
        {
            Logger.LogWarning("Сесія {ClientId}: pre-auth повідомлення завелике ({Size}Б) — close 4401.", Id, size);
            CloseAuth(AuthCloseCodes.TokenRejected, AuthCloseReasons.Invalid);
            return;
        }

        string? method = null;
        string? token = null;
        string? signature = null;
        JsonElement? ackId = null;
        try
        {
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buffer, (int)offset, (int)size));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String)
                    method = m.GetString();
                if (root.TryGetProperty("id", out var idEl))
                    ackId = idEl.Clone();   // detach: doc диспоузиться в кінці using
                if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object)
                {
                    if (p.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                        token = t.GetString();
                    if (p.TryGetProperty("signature", out var s) && s.ValueKind == JsonValueKind.String)
                        signature = s.GetString();
                }
            }
        }
        catch (JsonException)
        {
            CloseAuth(AuthCloseCodes.TokenRejected, AuthCloseReasons.Invalid);
            return;
        }

        SessionAuthState state;
        lock (_authLock)
        {
            state = _authMachine.State;
        }

        // PoPPending: єдиний допустимий метод — pop.prove. authenticate вдруге / будь-що інше → close.
        if (state == SessionAuthState.PoPPending)
        {
            if (string.Equals(method, "pop.prove", StringComparison.Ordinal))
                HandlePopProve(signature, ackId);
            else
                CloseUnknownPreAuthMethod();
            return;
        }

        // AwaitingAuthenticate: єдиний допустимий метод — authenticate (з токеном у params).
        if (string.Equals(method, "authenticate", StringComparison.Ordinal))
        {
            var result = _tokenService.Authenticate(token);
            // token більше не потрібен — не логуємо і не зберігаємо plaintext.
            ResolveToken(result, fromFallback: true);
            return;
        }

        CloseUnknownPreAuthMethod();
    }

    // Спільний close-шлях для неочікуваного pre-auth методу (через стан-машину → 4401 invalid).
    private void CloseUnknownPreAuthMethod()
    {
        AuthDecision decision;
        lock (_authLock)
        {
            decision = _authMachine.OnUnknownPreAuthMethod();
        }

        if (decision.ShouldClose)
            CloseAuth(decision.CloseCode, decision.Reason ?? AuthCloseReasons.Invalid);
    }

    // Надсилає JSON-RPC notification pop.challenge (без id): {"jsonrpc":"2.0","method":"pop.challenge",
    // "params":{"nonce":"<base64url>","connId":"<Guid>"}}. Сирий pre-auth кадр (як існуючі ack) —
    // будуємо вручну Utf8JsonWriter-ом, не через загальний серіалізатор.
    private void SendPopChallenge(string nonce, string connId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WriteString("method", "pop.challenge");
            writer.WritePropertyName("params");
            writer.WriteStartObject();
            writer.WriteString("nonce", nonce);
            writer.WriteString("connId", connId);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        SendTextAsync(buffer.WrittenSpan);
    }

    // Надсилає result-ack pop.prove: {"status":"ok"} (+ "token":"<новий plaintext>" для renewal, T5).
    // Будуємо ВРУЧНУ Utf8JsonWriter-ом — plaintext-токен НЕ проходить через загальний серіалізатор і
    // НІКОЛИ не логується.
    private void SendPopAck(JsonElement id, string? renewedToken)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("jsonrpc", "2.0");
            writer.WritePropertyName("id");
            id.WriteTo(writer);
            writer.WritePropertyName("result");
            writer.WriteStartObject();
            writer.WriteString("status", "ok");
            if (renewedToken is not null)
                writer.WriteString("token", renewedToken);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        SendTextAsync(buffer.WrittenSpan);
    }

    // Чистить PoP-стан (nonce one-time — після завершення handshake більше не потрібен).
    private void ClearPopState()
    {
        _popNonce = null;
        _popConnId = null;
        _popIdentity = null;
        _popTokenHash = null;
        _popPendingRenewal = false;
    }

    // Закриває сесію auth-close-кодом (4401 token-відмова / 4408 таймаут) із машиночитним reason у payload.
    private void CloseAuth(int statusCode, string reason)
    {
        lock (_authLock)
        {
            _authMachine.OnClosed();
        }

        ReleaseUnauthSlot();
        StopAuthTimer();

        Logger.LogInformation("Сесія {ClientId}: auth-відмова, close {Code} ({Reason}).", Id, statusCode, reason);

        // Штатний close-frame зі статусом (NetCoreServer WsSession.Close(int status, string text)):
        // 2-байтовий код + reason у payload. Браузер бачить close-code (T4), а не generic 1006.
        Close(statusCode, reason);
    }

    // Callback in-process revoke: активний token-конект рвемо close 4401 `revoked` (1.4).
    private void OnIdentityRevoked()
    {
        AuthDecision decision;
        lock (_authLock)
        {
            decision = _authMachine.OnRevoked();
        }

        if (decision.ShouldClose)
            CloseAuth(decision.CloseCode, decision.Reason ?? AuthCloseReasons.Revoked);
    }

    private void StartAuthTimer()
    {
        var timeout = TimeSpan.FromSeconds(_authOptions.AuthTimeoutSeconds);
        _authTimer = new Timer(_ => OnAuthTimeout(), null, timeout, Timeout.InfiniteTimeSpan);
    }

    // Стартує auth-таймер лише якщо він ще не йде. Fallback-шлях уже запустив його в OnWsConnected (TTL
    // покриває і очікування authenticate, і подальший PoP); upgrade-шлях запускає його тут — при видачі
    // challenge. Викликається на handshake-потоці, тож перевірка-і-старт без гонки.
    private void EnsureAuthTimer()
    {
        if (_authTimer is null)
            StartAuthTimer();
    }

    private void OnAuthTimeout()
    {
        AuthDecision decision;
        lock (_authLock)
        {
            decision = _authMachine.OnAuthTimeout();
        }

        if (decision.ShouldClose)
            CloseAuth(decision.CloseCode, decision.Reason ?? AuthCloseReasons.AuthTimeout);
    }

    private void StopAuthTimer()
    {
        var timer = Interlocked.Exchange(ref _authTimer, null);
        timer?.Dispose();
    }

    private bool TryAcquireUnauthSlot()
    {
        if (Volatile.Read(ref _unauthSlotHeld) == 1)
            return true;

        // Немає remote-IP (не мало б бути на встановленому TCP) → не блокуємо (defensive), але й не рахуємо.
        if (string.IsNullOrEmpty(_remoteIp))
        {
            Volatile.Write(ref _unauthSlotHeld, 1);
            return true;
        }

        if (_unauthLimiter.TryAcquire(_remoteIp))
        {
            Volatile.Write(ref _unauthSlotHeld, 1);
            return true;
        }

        Logger.LogWarning("Сесія {ClientId}: перевищено per-IP cap неавтентифікованих — відмова 429.", Id);
        return false;
    }

    private void ReleaseUnauthSlot()
    {
        // Ідемпотентно: рівно один release на успішний acquire.
        if (Interlocked.Exchange(ref _unauthSlotHeld, 0) == 1 && !string.IsNullOrEmpty(_remoteIp))
            _unauthLimiter.Release(_remoteIp);
    }

    // Закриває сесію транспортним rate/admission close-кодом (4429 conn-cap/msg-rate; 1000 idle) із
    // машиночитним reason. НЕ auth-подія: не проганяємо через token/pop-переходи, лише позначаємо
    // стан-машину термінальною й глушимо таймери; per-identity conn-слот звільнить teardown (ідемпотентно).
    private void CloseRateLimited(int code, string reason)
    {
        lock (_authLock)
        {
            _authMachine.OnClosed();
        }

        ReleaseUnauthSlot();
        StopAuthTimer();
        StopIdleTimer();

        Logger.LogInformation("Сесія {ClientId}: rate-limit close {Code} ({Reason}).", Id, code, reason);
        Close(code, reason);
    }

    // Idle-timeout: сесія мовчала довше за вікно → штатний close 1000 (НЕ 4429 — це не rate-подія).
    private void OnIdleTimeout()
    {
        if (IsDisposed)
            return;

        Logger.LogInformation("Сесія {ClientId}: idle-timeout — close 1000.", Id);
        CloseRateLimited((int)WebSocketCloseStatus.NormalClosure, RateLimitCloseReasons.IdleTimeout);
    }

    // Переармовує idle-таймер на кожен прийнятий data-кадр (той самий патерн, що auth-таймер; тут через
    // Change, бо таймер уже створено при активації). Гонка з teardown-диспоузом — benign (ловимо).
    private void ArmIdleTimer()
    {
        var timer = _idleTimer;
        if (timer is null)
            return;

        try
        {
            timer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // teardown уже диспоузнув таймер між читанням поля й Change — ігноруємо.
        }
    }

    private void StopIdleTimer()
    {
        var timer = Interlocked.Exchange(ref _idleTimer, null);
        timer?.Dispose();
    }

    private void ReleaseIdentitySlot()
    {
        // Ідемпотентно: рівно один release на успішний acquire (патерн unauth-слота). Loopback-сесії
        // слота не брали (_identitySlotHeld=0) → no-op.
        if (Interlocked.Exchange(ref _identitySlotHeld, 0) == 1 && _identityId is not null)
            _identityConnLimiter.Release(_identityId);
    }

    private bool IsOriginAllowed(string origin) =>
        _authOptions.AllowedOrigins is { Length: > 0 } allowed &&
        allowed.Any(o => string.Equals(o, origin, StringComparison.OrdinalIgnoreCase));

    // Формує HTTP-відмову ДО апгрейду і надсилає її сам (return false = NetCoreServer нічого не шле),
    // потім розриває сокет. Синхронний SendResponse гарантує, що байти пішли до FIN.
    private void RejectBeforeUpgrade(HttpResponse response, int status, string reasonPhrase)
    {
        response.Clear();
        response.MakeErrorResponse(status, reasonPhrase);
        SendResponse(response);
        Disconnect();
    }

    // Читає субпротоколи (кілька заголовків АБО comma-separated) і Origin. Значень не логуємо (redact).
    private static (List<string> Subprotocols, string? Origin) ReadHandshakeHeaders(HttpRequest request)
    {
        var subprotocols = new List<string>();
        string? origin = null;

        var count = request.Headers;
        for (long i = 0; i < count; i++)
        {
            var header = request.Header((int)i);
            var key = header.Item1;
            var value = header.Item2;

            if (string.Equals(key, "Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var part in value.Split(','))
                {
                    var trimmed = part.Trim();
                    if (trimmed.Length > 0)
                        subprotocols.Add(trimmed);
                }
            }
            else if (string.Equals(key, "Origin", StringComparison.OrdinalIgnoreCase))
            {
                var trimmed = value?.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    origin = trimmed;
            }
        }

        return (subprotocols, origin);
    }

    /// <summary>
    /// Handles WebSocket connection closure
    /// Called by NetCoreServer when a close frame is received
    /// </summary>
    public override void OnWsClose(byte[] buffer, long offset, long size, int status = 1000)
    {
        Logger.LogInformation("Received WebSocket close frame with status {Status} for client {ClientId}", status, Id);

        // Дозволяємо NetCoreServer завершити close-handshake; це призводить до OnWsDisconnected, де
        // й відпрацьовує єдиний упорядкований teardown (A4: без окремого fire-and-forget тут).
        base.OnWsClose(buffer, offset, size, status);
    }

    protected override void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            if (disposing)
            {
                Logger.LogDebug("Disposing resources for client {ClientId}", Id);
                _cts.Dispose();
                StopAuthTimer();
                StopIdleTimer();
                _revocationSubscription?.Dispose();

                // Other resources are disposed in the Disconnect process
            }

            base.Dispose(disposing);
        }
    }
}
