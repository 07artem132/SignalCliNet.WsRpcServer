using System.Buffers;
using System.Net;
using System.Net.WebSockets;
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

    // Section 2 auth-залежності (усі singletons/options з DI).
    private readonly AuthOptions _authOptions;
    private readonly TokenService _tokenService;
    private readonly IDurableStore _store;
    private readonly UnauthenticatedConnectionLimiter _unauthLimiter;
    private readonly RevocationBroadcaster _revocationBroadcaster;

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
        RevocationBroadcaster revocationBroadcaster)
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
    /// NetCoreServer seam invoked AFTER the 101 response is prepared but BEFORE it is sent (V4/W11). We
    /// parse the bearer subprotocol + Origin here, echo the non-auth subprotocol (C4), enforce the Origin
    /// allowlist (C3) and decide whether to complete the upgrade. Returning <c>false</c> cancels the
    /// upgrade — we must then send the HTTP response ourselves and disconnect.
    /// </summary>
    public override bool OnWsConnecting(HttpRequest request, HttpResponse response)
    {
        // Auth вимкнено → нічого не робимо: 101 іде як є (поведінка Фази 1 незмінна).
        if (!_authOptions.Enabled)
            return true;

        _remoteIp = (Socket?.RemoteEndPoint as IPEndPoint)?.Address.ToString();

        var (subprotocols, origin) = ReadHandshakeHeaders(request);

        // Токен-кандидат = субпротокол із префіксом ptzh_; решта — звичайні субпротоколи.
        string? tokenCandidate = null;
        string? firstNonTokenProtocol = null;
        foreach (var proto in subprotocols)
        {
            if (proto.StartsWith(TokenFormat.Prefix, StringComparison.Ordinal))
                tokenCandidate ??= proto;
            else
                firstNonTokenProtocol ??= proto;
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

        // C4/W11: явний echo ПЕРШОГО не-токен субпротоколу. НІКОЛИ не копіювати весь client-list і
        // НІКОЛИ не echo-ити токен-елемент — інакше WHATWG-клієнт не досягне open (або витік токена).
        if (firstNonTokenProtocol is not null)
            response.SetHeader("Sec-WebSocket-Protocol", firstNonTokenProtocol);

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
            return true;
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

        return true;
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
            // Токен був на upgrade → автентифікуємо тут (in-band, T4).
            var result = _tokenService.Authenticate(_pendingToken);
            _pendingToken = null;   // REDACT: більше не тримаємо plaintext
            ResolveToken(result, ackId: null);
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

        try
        {
            // D9/1.5: для token-сесій передаємо замикання re-чеку живучості (по збереженому tokenHash,
            // без повторного HMAC). Для loopback — null (поведінка незмінна).
            Func<bool>? isCredentialLive = _tokenHash is null ? null : () => _tokenService.IsLive(_tokenHash);

            // V9: pre-dispatch chokepoint замість голого JsonRpc — кожен виклик проходить герметичний
            // default-deny + IDOR-guard + санітизацію помилок. Principal сесії читається per-invocation.
            JsonRpc = new AuthorizingSignalJsonRpc(_messageHandler, principal, _policyRegistry, Logger, isCredentialLive);

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

    // Розв'язує presented-токен (з upgrade АБО з fallback authenticate) у активацію чи close-4401.
    // ackId != null ⇒ це fallback-шлях, треба надіслати JSON-RPC result {"status":"ok"} для того id.
    private void ResolveToken(TokenAuthenticationResult result, JsonElement? ackId)
    {
        // Downgrade Valid→Invalid, якщо identity відсутня у сторі (токен трактуємо як невалідний).
        var status = result.Status;
        IdentityRecord? identity = null;
        if (status == TokenAuthenticationStatus.Valid)
        {
            identity = _store.GetIdentity(result.IdentityId!);
            if (identity is null)
                status = TokenAuthenticationStatus.Invalid;
        }

        // TODO(task 3.5 / T5): у fallback-шляху (ackId != null) прострочений (НЕ revoked) токен + успішний
        // PoP device-ключем має видавати НОВИЙ токен (ротація W21), а не закриватись 4401. Секція 2 (без
        // PoP) поки що закриває expired як 4401 `expired`.
        AuthDecision decision;
        lock (_authLock)
        {
            decision = _authMachine.OnTokenValidated(status);
            if (decision.NextState == SessionAuthState.PoPPending)
                decision = _authMachine.CompletePoP();   // section 2: PoP — транзитний no-op
        }

        if (decision.NextState == SessionAuthState.Active)
        {
            var principal = new SignalPrincipal(
                name: identity!.Id,
                isAdmin: string.Equals(identity.Role, "admin", StringComparison.Ordinal),
                hasFullAccess: false,
                allowedAccounts: identity.LinkedAccounts,
                allowedGroupIds: []);

            _tokenHash = result.TokenHash;
            _identityId = identity.Id;

            // Підписка на in-process revoke: revoke identity → close 4401 `revoked` живого конекту (1.4).
            _revocationSubscription = _revocationBroadcaster.Subscribe(identity.Id, OnIdentityRevoked);

            if (ackId is not null)
                SendAuthenticateAck(ackId.Value);

            ActivateSession(principal);
        }
        else if (decision.ShouldClose)
        {
            CloseAuth(decision.CloseCode, decision.Reason ?? AuthCloseReasons.Invalid);
        }
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
        StopAuthTimer();
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

    // Обробник pre-auth повідомлення: приймаємо РІВНО {"jsonrpc":"2.0","id":N,"method":"authenticate",
    // "params":{"token":"..."}}. Будь-який інший метод / малформ / завеликий розмір → close 4401.
    // REDACT: тіло цього повідомлення містить токен у params.token — його НІКОЛИ не логувати.
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
                if (root.TryGetProperty("params", out var p) && p.ValueKind == JsonValueKind.Object &&
                    p.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String)
                    token = t.GetString();
            }
        }
        catch (JsonException)
        {
            CloseAuth(AuthCloseCodes.TokenRejected, AuthCloseReasons.Invalid);
            return;
        }

        if (!string.Equals(method, "authenticate", StringComparison.Ordinal))
        {
            // Будь-який інший метод у pre-auth стані → close 4401 invalid.
            AuthDecision d;
            lock (_authLock)
            {
                d = _authMachine.OnUnknownPreAuthMethod();
            }

            if (d.ShouldClose)
                CloseAuth(d.CloseCode, d.Reason ?? AuthCloseReasons.Invalid);
            return;
        }

        var result = _tokenService.Authenticate(token);
        // token більше не потрібен — не логуємо і не зберігаємо plaintext.
        ResolveToken(result, ackId);
    }

    // Надсилає JSON-RPC result {"status":"ok"} для authenticate-запиту (лише fallback-шлях).
    private void SendAuthenticateAck(JsonElement id)
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
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        SendTextAsync(buffer.WrittenSpan);
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
                _revocationSubscription?.Dispose();

                // Other resources are disposed in the Disconnect process
            }

            base.Dispose(disposing);
        }
    }
}
