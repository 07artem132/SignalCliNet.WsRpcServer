using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using StreamJsonRpc.Protocol;
using WsRpcServer.Core;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalEventsRpcAdapter(
    ISubscriptionManager subscriptionManager,
    ILogger<SignalEventsRpcAdapter> logger,
    Guid clientId)
    : ISignalEventsRpc
{
    private readonly ISubscriptionManager _subscriptionManager =
        subscriptionManager ?? throw new ArgumentNullException(nameof(subscriptionManager));

    private readonly ILogger<SignalEventsRpcAdapter>
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<int> Subscribe(string account, SignalEventTypes eventTypes,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Client {ClientId} requesting subscription to {EventTypes} for account {Account}",
            clientId, eventTypes, account);
        try
        {
            return await _subscriptionManager.Subscribe(clientId, account, eventTypes, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error subscribing to events");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error subscribing to events", ex);
        }
    }

    public async Task<bool> Unsubscribe(int subscriptionId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Client {ClientId} requesting unsubscribe from subscription {SubscriptionId}",
            clientId, subscriptionId);
        try
        {
            return await _subscriptionManager.Unsubscribe(clientId, subscriptionId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unsubscribing from events");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error subscribing to events", ex);
        }
    }

    public async Task<bool> UpdateSubscription(int subscriptionId, SignalEventTypes eventTypes,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "RPC: Client {ClientId} requesting update of subscription {SubscriptionId} to {EventTypes}",
            clientId, subscriptionId, eventTypes);

        try
        {
            return await _subscriptionManager.UpdateSubscription(clientId, subscriptionId, eventTypes,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error updating subscription");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error updating subscription", ex);
        }
    }
}