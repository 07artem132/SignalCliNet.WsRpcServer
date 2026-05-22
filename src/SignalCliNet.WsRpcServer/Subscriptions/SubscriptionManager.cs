using WsRpcServer.Core;
using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Model;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using WsRpcServer.Subscriptions;

namespace SignalCliNet.WsRpcServer.Subscriptions;

/// <summary>
/// Manages client subscriptions to Signal events with improved resource control
/// and exception handling.
/// </summary>
public class SubscriptionManager(
    ISignalEventService signalEventService,
    ILogger<SubscriptionManager> logger)
    : AbstractSubscriptionManager(logger)
{
    private readonly ISignalEventService _signalEventService =
        signalEventService ?? throw new ArgumentNullException(nameof(signalEventService));

    private readonly SubscriptionStore _store = new();


    /// <summary>
    /// Creates a new subscription for a client with proper validation
    /// </summary>
    public override async Task<int> Subscribe(
        Guid clientId,
        string account,
        object eventTypes,
        CancellationToken cancellationToken = default)
    {
        var typedEventTypes = (SignalEventTypes)eventTypes;

        // Validate inputs
        if (string.IsNullOrEmpty(account))
            throw new ArgumentException("Account cannot be empty", nameof(account));

        if (typedEventTypes == SignalEventTypes.None)
            throw new ArgumentException("At least one event type must be selected", nameof(eventTypes));

        // Check subscription limit per client
        if (ClientSubscriptionCounts.GetValueOrDefault(clientId) >= MaxSubscriptionsPerClient)
        {
            throw new RpcErrorException(
                JsonRpcErrorCode.InvalidRequest,
                $"Maximum number of subscriptions ({MaxSubscriptionsPerClient}) reached for this client");
        }

        await OperationLock.WaitAsync(cancellationToken);
        try
        {
            Logger.LogInformation(
                "Client {ClientId} requesting subscription to {EventTypes} for account {Account}",
                clientId, typedEventTypes, account);

            int clientSubscriptionId = _store.GenerateSubscriptionId();
            var clientSubscription = new ClientSubscription(clientId, account, clientSubscriptionId, typedEventTypes);

            var (existingAccount, existingSignalId) = _store.GetSubscriptionInfo(clientSubscriptionId);

            int signalSubscriptionId;
            if (existingSignalId.HasValue)
            {
                signalSubscriptionId = existingSignalId.Value;
                Logger.LogInformation(
                    "Using existing Signal subscription {SignalSubscriptionId} for account {Account}",
                    signalSubscriptionId, account);
            }
            else
            {
                try
                {
                    // Apply a timeout to the subscription operation
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                    var response = await _signalEventService.SubscribeAsync(account, timeoutCts.Token);
                    signalSubscriptionId = response.id;

                    Logger.LogInformation(
                        "Created Signal subscription {SignalSubscriptionId} for account {Account}",
                        signalSubscriptionId, account);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new RpcErrorException(
                        JsonRpcErrorCode.InternalError,
                        "Subscription operation timed out");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error creating Signal subscription for account {Account}", account);
                    throw new RpcErrorException(
                        JsonRpcErrorCode.InternalError,
                        "Error creating Signal subscription", ex);
                }
            }

            _store.AddSubscription(clientSubscription, signalSubscriptionId);

            // Update client subscription count
            ClientSubscriptionCounts.AddOrUpdate(
                clientId,
                1,
                (_, count) => count + 1);

            Logger.LogInformation(
                "Client {ClientId} subscribed to {EventTypes} for account {Account} with subscription ID {SubscriptionId}",
                clientId, typedEventTypes, account, clientSubscriptionId);

            return clientSubscriptionId;
        }
        finally
        {
            OperationLock.Release();
        }
    }

    /// <summary>
    /// Removes a subscription with proper resource cleanup
    /// </summary>
    public override async Task<bool> Unsubscribe(
        Guid clientId,
        int subscriptionId,
        CancellationToken cancellationToken = default)
    {
        await OperationLock.WaitAsync(cancellationToken);
        try
        {
            Logger.LogInformation("Client {ClientId} requesting to unsubscribe from subscription {SubscriptionId}",
                clientId, subscriptionId);

            var (subscription, remainingClients, signalSubscriptionId) =
                _store.RemoveSubscription(clientId, subscriptionId);

            if (subscription == null)
            {
                Logger.LogWarning("Subscription {SubscriptionId} not found for client {ClientId}",
                    subscriptionId, clientId);
                return false;
            }

            // Update client subscription count
            ClientSubscriptionCounts.AddOrUpdate(
                clientId,
                0,
                (_, count) => Math.Max(0, count - 1));

            if (remainingClients?.Count == 0 && signalSubscriptionId > 0)
            {
                try
                {
                    Logger.LogInformation(
                        "Removing Signal subscription {SignalSubscriptionId} for account {Account} as no clients left",
                        signalSubscriptionId, subscription.Account);

                    // Apply a timeout to the unsubscription operation
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                    await _signalEventService.UnsubscribeAsync(signalSubscriptionId, timeoutCts.Token);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex,
                        "Error unsubscribing Signal subscription {SignalSubscriptionId} for account {Account}",
                        signalSubscriptionId, subscription.Account);
                    // We continue anyway since we've already removed the client subscription
                }
            }

            Logger.LogInformation("Client {ClientId} unsubscribed from subscription {SubscriptionId}",
                clientId, subscriptionId);

            return true;
        }
        finally
        {
            OperationLock.Release();
        }
    }

    public override List<Guid> GetClientsForEvent(object args, object eventType)
    {
        return _store.GetClientsForEvent((BaseSignalEventArgs)args, (SignalEventTypes)eventType);
    }

    public override async Task<bool> UpdateSubscription(
        Guid clientId,
        int subscriptionId,
        object eventTypes,
        CancellationToken cancellationToken = default)
    {
        var typedEventTypes = (SignalEventTypes)eventTypes;

        if (typedEventTypes == SignalEventTypes.None)
            throw new ArgumentException("At least one event type must be selected", nameof(eventTypes));

        await OperationLock.WaitAsync(cancellationToken);
        try
        {
            Logger.LogInformation(
                "Client {ClientId} requesting update of subscription {SubscriptionId} to {EventTypes}",
                clientId, subscriptionId, typedEventTypes);

            // Get current subscription
            var subscription = _store.GetSubscription(subscriptionId);
            if (subscription == null)
            {
                Logger.LogWarning("Subscription {SubscriptionId} not found", subscriptionId);
                return false;
            }

            if (subscription.ClientId != clientId)
            {
                Logger.LogWarning("Subscription {SubscriptionId} does not belong to client {ClientId}",
                    subscriptionId, clientId);
                return false;
            }

            // Create updated subscription with new event types
            var updatedSubscription = subscription with { EventTypes = typedEventTypes };

            // Update subscription in store
            _store.UpdateSubscription(updatedSubscription);

            Logger.LogInformation("Client {ClientId} updated subscription {SubscriptionId} to {EventTypes}",
                clientId, subscriptionId, typedEventTypes);

            return true;
        }
        finally
        {
            OperationLock.Release();
        }
    }

    public override void Dispose()
    {
        if (!IsDisposed)
        {
            _store.Dispose();
            base.Dispose();
        }
    }
}