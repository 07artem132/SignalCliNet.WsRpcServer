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
public class EventProcessor : AbstractEventProcessor
{
    private readonly ISignalEventService _eventService;
    private readonly ISubscriptionManager<SignalEventTypes, BaseSignalEventArgs> _subscriptionManager;

    public EventProcessor(
        ISignalEventService eventService,
        ISubscriptionManager<SignalEventTypes, BaseSignalEventArgs> subscriptionManager,
        ILogger<EventProcessor> logger)
        : base(logger)
    {
        _eventService = eventService ?? throw new ArgumentNullException(nameof(eventService));
        _subscriptionManager = subscriptionManager ?? throw new ArgumentNullException(nameof(subscriptionManager));

        // Register handlers for each event type
        RegisterEventHandlers();
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