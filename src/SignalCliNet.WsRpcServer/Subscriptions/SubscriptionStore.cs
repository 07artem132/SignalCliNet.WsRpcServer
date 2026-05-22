using SignalCli.Models.Signal.Events;
using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Core;

namespace SignalCliNet.WsRpcServer.Subscriptions;

public class SubscriptionStore : AbstractSubscriptionStore<ClientSubscription, BaseSignalEventArgs, SignalEventTypes>
{
    private readonly Dictionary<string, (int SignalId, HashSet<Guid> ClientIds)> _accountSubscriptions = new();
    private readonly Dictionary<Guid, Dictionary<int, ClientSubscription>> _clientSubscriptions = new();
    private readonly Dictionary<int, ClientSubscription> _subscriptions = new();

    protected override void AddSubscriptionCore(ClientSubscription subscription, int providerSubscriptionId)
    {
        var account = subscription.Account;
        var clientId = subscription.ClientId;
        var subId = subscription.SubscriptionId;

        if (!_accountSubscriptions.TryGetValue(account, out var accountSub))
        {
            accountSub = (providerSubscriptionId, new HashSet<Guid>());
            _accountSubscriptions[account] = accountSub;
        }

        accountSub.ClientIds.Add(clientId);

        if (!_clientSubscriptions.TryGetValue(clientId, out var clientSubs))
        {
            clientSubs = new Dictionary<int, ClientSubscription>();
            _clientSubscriptions[clientId] = clientSubs;
        }

        clientSubs[subId] = subscription;
        _subscriptions[subId] = subscription;
    }

    protected override ClientSubscription? GetSubscriptionCore(int subscriptionId)
    {
        return _subscriptions.GetValueOrDefault(subscriptionId);
    }

    protected override (ClientSubscription? Subscription, HashSet<Guid>? RemainingClients, int ProviderSubscriptionId)
        RemoveSubscriptionCore(Guid clientId, int subscriptionId)
    {
        if (!_clientSubscriptions.TryGetValue(clientId, out var clientSubs) ||
            !clientSubs.TryGetValue(subscriptionId, out var subscription))
        {
            return (null, null, 0);
        }

        var account = subscription.Account;

        clientSubs.Remove(subscriptionId);
        if (clientSubs.Count == 0)
        {
            _clientSubscriptions.Remove(clientId);
        }

        _subscriptions.Remove(subscriptionId);

        if (!_accountSubscriptions.TryGetValue(account, out var accountSub))
        {
            return (subscription, null, 0);
        }

        accountSub.ClientIds.Remove(clientId);

        var remainingClients = accountSub.ClientIds;
        var signalSubscriptionId = accountSub.SignalId;

        if (remainingClients.Count == 0)
        {
            _accountSubscriptions.Remove(account);
        }

        return (subscription, remainingClients, signalSubscriptionId);
    }

    protected override void UpdateSubscriptionCore(ClientSubscription subscription)
    {
        var subId = subscription.SubscriptionId;
        var clientId = subscription.ClientId;

        if (_clientSubscriptions.TryGetValue(clientId, out var clientSubs))
        {
            clientSubs[subId] = subscription;
        }

        _subscriptions[subId] = subscription;
    }

    protected override List<int> GetClientSubscriptionIdsCore(Guid clientId)
    {
        if (_clientSubscriptions.TryGetValue(clientId, out var subs))
        {
            return subs.Keys.ToList();
        }

        return new List<int>();
    }

    protected override Dictionary<int, string> GetClientSubscriptionsInfoCore(Guid clientId)
    {
        var result = new Dictionary<int, string>();

        if (_clientSubscriptions.TryGetValue(clientId, out var subs))
        {
            foreach (var (id, sub) in subs)
            {
                result[id] = $"{sub.Account} ({sub.EventTypes})";
            }
        }

        return result;
    }

    protected override (string? Account, int? ProviderSubscriptionId) GetSubscriptionInfoCore(int subscriptionId)
    {
        if (!_subscriptions.TryGetValue(subscriptionId, out var subscription))
        {
            return (null, null);
        }

        var account = subscription.Account;
        if (!_accountSubscriptions.TryGetValue(account, out var accountSub))
        {
            return (account, null);
        }

        return (account, accountSub.SignalId);
    }

    protected override List<Guid> GetClientsForEventCore(BaseSignalEventArgs args, SignalEventTypes eventType)
    {
        var result = new List<Guid>();
        string account = args.Account ?? string.Empty;

        // Если событие имеет ID подписки, сначала проверяем по нему
        if (args.SubscriptionId > 0)
        {
            if (_accountSubscriptions.TryGetValue(account, out var accountSub) &&
                accountSub.SignalId == args.SubscriptionId)
            {
                foreach (var clientId in accountSub.ClientIds)
                {
                    if (ShouldReceiveEvent(clientId, account, eventType))
                    {
                        result.Add(clientId);
                    }
                }
            }
        }
        // Если подписка не найдена по ID или ID не указан, ищем по аккаунту
        else if (!string.IsNullOrEmpty(account))
        {
            if (_accountSubscriptions.TryGetValue(account, out var accountSub))
            {
                foreach (var clientId in accountSub.ClientIds)
                {
                    if (ShouldReceiveEvent(clientId, account, eventType))
                    {
                        result.Add(clientId);
                    }
                }
            }
        }

        return result;
    }

    private bool ShouldReceiveEvent(Guid clientId, string account, SignalEventTypes eventType)
    {
        if (!_clientSubscriptions.TryGetValue(clientId, out var subs))
        {
            return false;
        }

        foreach (var sub in subs.Values)
        {
            if (sub.Account == account && (sub.EventTypes & eventType) == eventType)
            {
                return true;
            }
        }

        return false;
    }
}