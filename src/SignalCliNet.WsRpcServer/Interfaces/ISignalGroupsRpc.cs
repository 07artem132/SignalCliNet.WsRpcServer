using SignalCli.Models.Signal.Groups;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalGroupsRpc : IRpcService
{
    Task<ListGroupsResponse> ListGroups(string account, CancellationToken cancellationToken = default);
}
