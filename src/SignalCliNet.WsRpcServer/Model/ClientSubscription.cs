namespace SignalCliNet.WsRpcServer.Model;

public record ClientSubscription(
    Guid ClientId,
    string Account,
    int SubscriptionId,
    SignalEventTypes EventTypes
);