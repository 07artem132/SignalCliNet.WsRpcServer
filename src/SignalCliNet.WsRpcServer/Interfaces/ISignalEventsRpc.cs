using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Core;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalEventsRpc : IClientAwareRpcService
{
    Task<int> Subscribe(string account, SignalEventTypes eventTypes, CancellationToken cancellationToken = default);
    Task<bool> Unsubscribe(int subscriptionId, CancellationToken cancellationToken = default);

    Task<bool> UpdateSubscription(int subscriptionId, SignalEventTypes eventTypes,
        CancellationToken cancellationToken = default);
}