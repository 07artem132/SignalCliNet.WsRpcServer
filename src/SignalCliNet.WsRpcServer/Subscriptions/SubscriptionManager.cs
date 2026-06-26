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
/// <remarks>
/// Implements the template-method <c>*Core</c> contract of
/// <see cref="AbstractSubscriptionManager{TEventType,TEventArgs}"/> (JSON-RPC.NET 2.x): the base
/// serializes mutations through <c>OperationLock</c>, so the <c>*Core</c> methods here run already
/// under the lock and MUST NOT re-acquire it. <see cref="SignalEventTypes"/> is a <c>[Flags]</c>
/// enum, so the collection that the base passes is folded back into a single bitmask at the boundary.
/// </remarks>
public class SubscriptionManager(
    ISignalEventService signalEventService,
    ILogger<SubscriptionManager> logger)
    : AbstractSubscriptionManager<SignalEventTypes, BaseSignalEventArgs>(logger)
{
    private readonly ISignalEventService _signalEventService =
        signalEventService ?? throw new ArgumentNullException(nameof(signalEventService));

    private readonly SubscriptionStore _store = new();

    /// <summary>
    /// Згортає колекцію типів подій у єдиний bitmask (модель сховища — <c>[Flags]</c>).
    /// </summary>
    private static SignalEventTypes Combine(IReadOnlyCollection<SignalEventTypes> eventTypes)
        => eventTypes.Aggregate(SignalEventTypes.None, static (acc, e) => acc | e);

    /// <summary>
    /// Creates a new subscription for a client with proper validation.
    /// Runs under the base <c>OperationLock</c> — no manual locking here.
    /// </summary>
    protected override async Task<int> SubscribeCore(
        Guid clientId,
        string topic,
        IReadOnlyCollection<SignalEventTypes> eventTypes,
        CancellationToken cancellationToken)
    {
        var typedEventTypes = Combine(eventTypes);

        // Validate inputs
        if (string.IsNullOrEmpty(topic))
            throw new ArgumentException("Account cannot be empty", nameof(topic));

        if (typedEventTypes == SignalEventTypes.None)
            throw new ArgumentException("At least one event type must be selected", nameof(eventTypes));

        // Check subscription limit per client
        if (ClientSubscriptionCounts.GetValueOrDefault(clientId) >= MaxSubscriptionsPerClient)
        {
            throw new RpcErrorException(
                JsonRpcErrorCode.InvalidRequest,
                $"Maximum number of subscriptions ({MaxSubscriptionsPerClient}) reached for this client");
        }

        // privacy (CLAUDE rule #4): не логувати topic(=account/E.164)
        Logger.LogInformation(
            "Client {ClientId} requesting subscription to {EventTypes}",
            clientId, typedEventTypes);

        int clientSubscriptionId = _store.GenerateSubscriptionId();
        var clientSubscription = new ClientSubscription(clientId, topic, clientSubscriptionId, typedEventTypes);

        var (existingAccount, existingSignalId) = _store.GetSubscriptionInfo(clientSubscriptionId);

        int signalSubscriptionId;
        if (existingSignalId.HasValue)
        {
            signalSubscriptionId = existingSignalId.Value;
            Logger.LogInformation(
                "Using existing Signal subscription {SignalSubscriptionId}",
                signalSubscriptionId);
        }
        else
        {
            try
            {
                // Apply a timeout to the subscription operation
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

                var response = await _signalEventService.SubscribeAsync(topic, timeoutCts.Token).ConfigureAwait(false);
                signalSubscriptionId = response.Id;

                Logger.LogInformation(
                    "Created Signal subscription {SignalSubscriptionId}",
                    signalSubscriptionId);
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
                Logger.LogError(ex, "Error creating Signal subscription");
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
            "Client {ClientId} subscribed to {EventTypes} with subscription ID {SubscriptionId}",
            clientId, typedEventTypes, clientSubscriptionId);

        return clientSubscriptionId;
    }

    /// <summary>
    /// Removes a subscription with proper resource cleanup.
    /// Runs under the base <c>OperationLock</c> — no manual locking here.
    /// </summary>
    protected override async Task<bool> UnsubscribeCore(
        Guid clientId,
        int subscriptionId,
        CancellationToken cancellationToken)
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
                    "Removing Signal subscription {SignalSubscriptionId} as no clients left",
                    signalSubscriptionId);

                // Apply a timeout to the unsubscription operation
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

                await _signalEventService.UnsubscribeAsync(signalSubscriptionId, timeoutCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex,
                    "Error unsubscribing Signal subscription {SignalSubscriptionId}",
                    signalSubscriptionId);
                // We continue anyway since we've already removed the client subscription
            }
        }

        Logger.LogInformation("Client {ClientId} unsubscribed from subscription {SubscriptionId}",
            clientId, subscriptionId);

        return true;
    }

    /// <summary>
    /// Hot read path — must stay lock-free (the thread-safe store handles concurrency).
    /// </summary>
    public override List<Guid> GetClientsForEvent(BaseSignalEventArgs args, SignalEventTypes eventType)
    {
        return _store.GetClientsForEvent(args, eventType);
    }

    /// <summary>
    /// Updates a subscription's event types.
    /// Runs under the base <c>OperationLock</c> — no manual locking here.
    /// </summary>
    protected override Task<bool> UpdateSubscriptionCore(
        Guid clientId,
        int subscriptionId,
        IReadOnlyCollection<SignalEventTypes> eventTypes,
        CancellationToken cancellationToken)
    {
        var typedEventTypes = Combine(eventTypes);

        if (typedEventTypes == SignalEventTypes.None)
            throw new ArgumentException("At least one event type must be selected", nameof(eventTypes));

        Logger.LogInformation(
            "Client {ClientId} requesting update of subscription {SubscriptionId} to {EventTypes}",
            clientId, subscriptionId, typedEventTypes);

        // Get current subscription
        var subscription = _store.GetSubscription(subscriptionId);
        if (subscription == null)
        {
            Logger.LogWarning("Subscription {SubscriptionId} not found", subscriptionId);
            return Task.FromResult(false);
        }

        if (subscription.ClientId != clientId)
        {
            Logger.LogWarning("Subscription {SubscriptionId} does not belong to client {ClientId}",
                subscriptionId, clientId);
            return Task.FromResult(false);
        }

        // Create updated subscription with new event types
        var updatedSubscription = subscription with { EventTypes = typedEventTypes };

        // Update subscription in store
        _store.UpdateSubscription(updatedSubscription);

        Logger.LogInformation("Client {ClientId} updated subscription {SubscriptionId} to {EventTypes}",
            clientId, subscriptionId, typedEventTypes);

        return Task.FromResult(true);
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
