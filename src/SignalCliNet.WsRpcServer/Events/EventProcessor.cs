using System.Reactive.Disposables;
using WsRpcServer.Core;
using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Events;

namespace SignalCliNet.WsRpcServer.Events;

/// <summary>
/// Processes Signal events and distributes them to subscribed clients.
/// Uses an efficient scheduling and notification mechanism.
/// </summary>
public class EventProcessor : AbstractEventProcessor, IAppNotifier
{
    private readonly ISignalEventService _eventService;
    private readonly ISubscriptionManager<SignalEventTypes, BaseSignalEventArgs> _subscriptionManager;
    private readonly PerAccountReceiveRouter? _receiveRouter;
    private readonly GroupClaimScanner? _scanner;

    public EventProcessor(
        ISignalEventService eventService,
        ISubscriptionManager<SignalEventTypes, BaseSignalEventArgs> subscriptionManager,
        ILogger<EventProcessor> logger,
        PerAccountReceiveRouter? receiveRouter = null,
        GroupClaimScanner? scanner = null)
        : base(logger)
    {
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _subscriptionManager = subscriptionManager ?? throw new ArgumentNullException(nameof(subscriptionManager));

        // S11 per-account receive-роутер + shared-bot сканер (add-group-claim-receive). Обидва опційні:
        // якщо GroupClaim не зареєстровано (напр. у юніт-тестах прямого конструювання) → passthrough.
        _receiveRouter = receiveRouter;
        _scanner = scanner;

        // Register handlers for each event type
        RegisterEventHandlers();
    }

    /// <inheritdoc />
    /// <remarks>
    /// App-level fan-out (heartbeat / bot.paused / bot.resumed) через ту саму per-client чергу, що й події
    /// (reuse fire-and-forget + M1 авто-відписки). Payload — БЕЗ PII (A1). Знімок ключів — щоб не тримати
    /// enumerator під час доставки (ClientHandlers — ConcurrentDictionary).
    /// </remarks>
    public void Broadcast(string method, object payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(payload);

        foreach (var clientId in ClientHandlers.Keys)
            NotifyClient(clientId, method, payload);
    }

    /// <summary>
    /// Registers handlers for all Signal event types
    /// </summary>
    private void RegisterEventHandlers()
    {
        // Register handlers for all Signal event types
        Subscriptions.Add(RegisterTypeHandler(_eventService.TextMessages));
        Subscriptions.Add(RegisterTypeHandler(_eventService.Reaction));
        Subscriptions.Add(RegisterTypeHandler(_eventService.Attachments));
        Subscriptions.Add(RegisterTypeHandler(_eventService.Sticker));
        Subscriptions.Add(RegisterTypeHandler(_eventService.TypingNotifications));
        Subscriptions.Add(RegisterTypeHandler(_eventService.Receipts));
        Subscriptions.Add(RegisterTypeHandler(_eventService.Syncs));
    }

    /// <summary>
    /// Registers a handler for a specific event type, with appropriate error handling
    /// </summary>
    private IDisposable RegisterTypeHandler<T>(IObservable<T> observable) where T : BaseSignalEventArgs
    {
        var eventInfo = EventTypeMapping.GetEventInfo<T>();

        if (eventInfo.EventType == SignalEventTypes.None)
        {
            Logger.LogWarning("Unknown event type: {EventType}", typeof(T).Name);
            return Disposable.Empty;
        }

        return observable.Subscribe(eventArgs => { ProcessEvent(eventArgs, eventInfo); });
    }

    private void ProcessEvent<T>(T eventArgs, EventTypeInfo eventInfo) where T : BaseSignalEventArgs
    {
        try
        {
            var eventType = eventInfo.EventType;
            var methodName = eventInfo.MethodName;

            // privacy (CLAUDE rule #4): не логувати account(=E.164)
            Logger.LogDebug("Processing event of type {EventType}", typeof(T).Name);

            // S11 (task 1.4): класифікуємо scope події ЧЕРЕЗ per-account роутер ДО фан-ауту клієнтам.
            // Passthrough (роутер вимкнено/відсутній) → поточна поведінка. SharedBot → лише сканеру
            // (scan-and-drop; клієнтам НЕ доставляємо — R3.5/W25-inbound). OwnLinked → власнику як раніше.
            var route = _receiveRouter?.Route(eventArgs.Account) ?? default;
            if (route.Scope == ReceiveScope.SharedBot)
            {
                _scanner?.Enqueue(eventArgs);
                Logger.LogDebug(
                    "Event {EventType} спрямовано shared-bot сканеру (без доставки клієнтам).", typeof(T).Name);
                return;
            }

            // Get clients for the event
            var clientIds = _subscriptionManager.GetClientsForEvent(eventArgs, eventType);

            foreach (var clientId in clientIds)
            {
                NotifyClient(clientId, methodName, eventArgs);
            }

            Logger.LogDebug("Event {EventType} processed and sent to {ClientCount} clients",
                typeof(T).Name, clientIds.Count);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error processing event {EventType}", typeof(T).Name);
        }
    }
}