using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreServer;
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
public sealed class SignalRpcSession : AbstractJsonRpcSession
{
    private readonly IRpcServiceRegistry _serviceRegistry;
    private readonly IEventProcessor _eventProcessor;
    private readonly Channel<RpcNotification> _notificationChannel;
    private readonly CancellationTokenSource _cts = new();
    private readonly WebSocketMessageHandler _messageHandler;
    private readonly JsonRpcServerConfig _config;
    private Task? _processingTask;

    public SignalRpcSession(
        WsServer server,
        IServiceProvider serviceProvider,
        ILogger<SignalRpcSession> logger,
        IRpcServiceRegistry serviceRegistry,
        IEventProcessor eventProcessor,
        JsonRpcServerConfig config)
        : base(server, logger, config)
    {
        _eventProcessor = eventProcessor ?? throw new ArgumentNullException(nameof(eventProcessor));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _serviceRegistry = serviceRegistry ?? throw new ArgumentNullException(nameof(serviceRegistry));

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
    /// Called by NetCoreServer when WebSocket connection is established
    /// </summary>
    public override void OnWsConnected(HttpRequest request)
    {
        Logger.LogInformation("WebSocket connection established: {ClientId}", Id);

        try
        {
            // Create and configure JsonRpc instance
            JsonRpc = new JsonRpc(_messageHandler);

            // Configure JSON-RPC
            JsonRpc.CancelLocallyInvokedMethodsWhenConnectionIsClosed = true;

            // ВАЖЛИВО (privacy, CLAUDE rule #4): НЕ вмикати ConsoleTraceListener на TraceSource —
            // StreamJsonRpc на рівні Information трейсить повні тіла RPC (включно з params/номерами/
            // майбутніми токенами) у консоль (= docker logs). Діагностику вмикати лише вибірково
            // через структуроване логування, не цей firehose.

            // Register RPC methods
            RegisterRpcMethods(JsonRpc);

            // Register client for receiving events
            _eventProcessor.RegisterClient(Id, SendNotificationAsync);

            // Start listening for JSON-RPC
            JsonRpc.StartListening();
            Logger.LogDebug("JSON-RPC started listening for client {ClientId}", Id);

            // Start notification processing
            _processingTask = ProcessNotificationsAsync(_cts.Token);
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

    /// <summary>
    /// Called by NetCoreServer when WebSocket disconnects
    /// </summary>
    public override async void OnWsDisconnected()
    {
        try
        {
            Logger.LogInformation("WebSocket client disconnected: {ClientId}", Id);
            await DisconnectAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error during WebSocket disconnect for client {ClientId}", Id);
        }
    }

    /// <summary>
    /// Asynchronously cleans up resources on disconnect
    /// </summary>
    private async Task DisconnectAsync()
    {
        if (IsDisposed)
            return;

        Logger.LogDebug("Cleaning up resources for client {ClientId}", Id);

        try
        {
            // Cancel all operations
            _cts.Cancel();

            // Unregister client from event system
            _eventProcessor.UnregisterClient(Id);
            Logger.LogDebug("Client {ClientId} unregistered from event system", Id);

            // Complete notification channel
            _notificationChannel.Writer.TryComplete();
            Logger.LogDebug("Notification channel completed for client {ClientId}", Id);

            // Dispose JsonRpc (which will also dispose the message handler)
            if (JsonRpc != null)
            {
                await Task.Run(() => JsonRpc.Dispose());
                JsonRpc = null;
                Logger.LogDebug("JsonRpc disposed for client {ClientId}", Id);
            }

            // Wait for notification processing to complete with timeout
            if (_processingTask != null)
            {
                await Task.WhenAny(_processingTask, Task.Delay(1000));
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
    /// Processes incoming WebSocket data using NetCoreServer's built-in functionality
    /// This method is called by NetCoreServer when a complete text or binary message is received
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

    /// <summary>
    /// Handles WebSocket connection closure
    /// Called by NetCoreServer when a close frame is received
    /// </summary>
    public override void OnWsClose(byte[] buffer, long offset, long size, int status = 1000)
    {
        Logger.LogInformation("Received WebSocket close frame with status {Status} for client {ClientId}", status, Id);

        // Allow NetCoreServer to handle the close handshake
        base.OnWsClose(buffer, offset, size, status);

        // Ensure cleanup is performed
        _ = DisconnectAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (!IsDisposed)
        {
            if (disposing)
            {
                Logger.LogDebug("Disposing resources for client {ClientId}", Id);
                _cts.Dispose();

                // Other resources are disposed in the Disconnect process
            }

            base.Dispose(disposing);
        }
    }
}