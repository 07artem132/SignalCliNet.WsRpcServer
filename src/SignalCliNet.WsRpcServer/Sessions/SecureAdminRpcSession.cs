using System.Net.WebSockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Serialization;
using StreamJsonRpc;
using WsRpcServer.Core;
using WsRpcServer.Events;
using WsRpcServer.Security;
using WsRpcServer.Services;
using WsRpcServer.Sessions;
using WebSocketMessageHandler = WsRpcServer.Transport.WebSocketMessageHandler;

namespace SignalCliNet.WsRpcServer.Sessions;

/// <summary>
/// The per-connection session of the mTLS admin port (add-invites-admin, task 2.3). On connect it
/// resolves the validated client certificate's SPKI to the admin identity via
/// <see cref="AdminPrincipalResolver"/>; on success it stands up the SAME dispatch chain as the plain
/// session (authorizing chokepoint + RPC registration + notification loop) carrying an admin principal —
/// on failure it closes the connection before any RPC runs. No token/PoP: the certificate IS the credential.
/// </summary>
/// <remarks>
/// NetCoreServer розводить плейн (<c>WsSession</c>) і TLS (<c>WssSession</c>) у роздільні ієрархії без
/// спільного WS-базового типу (framework rule #11), тож активацію диспетчу тут навмисно ДУБЛЬОВАНО з
/// <c>SignalRpcSession.ActivateSession</c> — але лише мінімальний шматок (chokepoint + register + loop),
/// БЕЗ auth-стан-машини / admission (admin-порт їх не має: cert = креденшел, admission адміну не діє).
/// Спільний лише pure-хелпер форматтера (<see cref="SignalRpcFormatter"/>).
/// </remarks>
public sealed class SecureAdminRpcSession : AbstractSecureJsonRpcSession
{
    private readonly IServiceProvider _serviceProvider;
    private readonly IRpcServiceRegistry _serviceRegistry;
    private readonly IEventProcessor _eventProcessor;
    private readonly IRpcPolicyRegistry _policyRegistry;
    private readonly IDurableStore _store;

    private WebSocketMessageHandler? _messageHandler;

    public SecureAdminRpcSession(
        AbstractSecureJsonRpcServer server,
        ILogger<SecureAdminRpcSession> logger,
        JsonRpcServerConfig config,
        IServiceProvider serviceProvider,
        IRpcServiceRegistry serviceRegistry,
        IEventProcessor eventProcessor,
        IRpcPolicyRegistry policyRegistry,
        IDurableStore store)
        : base(server, logger, config)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _serviceRegistry = serviceRegistry ?? throw new ArgumentNullException(nameof(serviceRegistry));
        _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));
        _policyRegistry = policyRegistry ?? throw new ArgumentNullException(nameof(policyRegistry));
        _store = store ?? throw new ArgumentNullException(nameof(store));

        Logger.LogInformation("Створено mTLS admin-сесію: {Id}", Id);
    }

    /// <summary>
    /// Called after the WebSocket upgrade completed (mTLS handshake already done): resolve the client
    /// cert → admin principal, then either activate the dispatch chain or close the connection.
    /// </summary>
    public override void OnWsConnected(HttpRequest request)
    {
        // Валідована mTLS-ідентичність (заповнена у RemoteCertificateValidationCallback під час
        // рукостискання). Немає ідентичності → cert не пройшов валідацію ланцюга/EKU → відмова конекту.
        if (!SecureServer.TryResolveNodeIdentity(this, out var nodeIdentity))
        {
            Logger.LogWarning("Admin-сесія {Id}: немає валідованої mTLS-ідентичності — close.", Id);
            Close(WebSocketCloseStatus.PolicyViolation, "mTLS client certificate required");
            return;
        }

        // SPKI → admin identity (durable admin_credentials). Немає mapping / revoked → не admin → відмова.
        var principal = AdminPrincipalResolver.Resolve(_store, nodeIdentity.SpkiThumbprint);
        if (principal is null)
        {
            Logger.LogWarning(
                "Admin-сесія {Id}: cert-SPKI не є (діючим) admin-креденшелом — close (жодного RPC).", Id);
            Close(WebSocketCloseStatus.PolicyViolation, "not an admin credential");
            return;
        }

        try
        {
            _messageHandler = ActivatorUtilities.CreateInstance<WebSocketMessageHandler>(
                _serviceProvider, this, SignalRpcFormatter.Create(), Config);

            // Той самий герметичний chokepoint, що й на плейн-порту, але з admin-principal і БЕЗ admission
            // (cert-креденшел, admin-канал: admitSends/gate/in-flight cap не передаємо).
            JsonRpc = new AuthorizingSignalJsonRpc(_messageHandler, principal, _policyRegistry, Logger);
            JsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;

            // ВАЖЛИВО (privacy, CLAUDE rule #4): НЕ вмикати ConsoleTraceListener на TraceSource —
            // StreamJsonRpc трейсив би повні тіла RPC у docker logs.

            _serviceRegistry.RegisterServices(JsonRpc, Id);
            _eventProcessor.RegisterClient(Id, SendNotificationAsync);

            JsonRpc.StartListening();
            NotificationProcessingTask = ProcessNotificationsAsync(Cts.Token);

            Logger.LogInformation("Admin-сесія {Id} активована (admin {Principal}).", Id, principal.Name);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Помилка ініціалізації admin JSON-RPC для сесії {Id}", Id);
            Close(WebSocketCloseStatus.InternalServerError, "Failed to initialize admin session");
        }
    }

    /// <summary>Feeds an incoming WebSocket message to the JSON-RPC stack (size-guarded).</summary>
    public override void OnWsReceived(byte[] buffer, long offset, long size)
    {
        try
        {
            if (IsDisposed || JsonRpc is null || _messageHandler is null)
            {
                Logger.LogWarning("Admin-сесія {Id}: повідомлення після закриття/до активації — ігнор.", Id);
                return;
            }

            if (size > Config.MaxMessageSizeBytes)
            {
                Logger.LogWarning(
                    "Admin-сесія {Id}: повідомлення завелике ({Size} > {Max}) — close.", Id, size, Config.MaxMessageSizeBytes);
                Close(WebSocketCloseStatus.MessageTooBig, "Message exceeds size limit");
                return;
            }

            var segment = new ReadOnlyMemory<byte>(buffer, (int)offset, (int)size);
            _ = _messageHandler.ProcessReceivedDataAsync(segment);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Помилка обробки повідомлення admin-сесії {Id}", Id);
        }
    }

    /// <inheritdoc />
    public override void OnWsDisconnected()
    {
        Logger.LogInformation("Admin-сесія {Id} відключена.", Id);

        try
        {
            _eventProcessor.UnregisterClient(Id);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Помилка зняття admin-клієнта {Id} з обліку подій", Id);
        }

        base.OnWsDisconnected();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposingManagedResources)
    {
        if (disposingManagedResources)
        {
            try
            {
                JsonRpc?.Dispose();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Помилка dispose JsonRpc admin-сесії {Id}", Id);
            }

            JsonRpc = null;
        }

        // База: скасування Cts → дренаж notification-loop → Cts.Dispose (H4).
        base.Dispose(disposingManagedResources);
    }
}
