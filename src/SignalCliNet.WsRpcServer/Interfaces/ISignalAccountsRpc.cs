using SignalCli.Models.Signal.Accounts;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalAccountsRpc : IRpcService
{
    Task<ListAccountsResponse> ListAccounts(CancellationToken cancellationToken = default);

    Task<SyncAccountsResponse> SyncAccount(CancellationToken cancellationToken = default);
}
