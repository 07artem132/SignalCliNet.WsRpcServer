using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Accounts;
using SignalCliNet.WsRpcServer.Interfaces;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalAccountsRpcAdapter(ISignalAccounts signalAccounts, ILogger<SignalAccountsRpcAdapter> logger)
    : ISignalAccountsRpc
{
    private readonly ISignalAccounts _signalAccounts =
        signalAccounts ?? throw new ArgumentNullException(nameof(signalAccounts));

    private readonly ILogger<SignalAccountsRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<ListAccountsResponse> ListAccounts(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Request to list accounts");
        try
        {
            return await _signalAccounts.ListAccountsAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing accounts");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error listing accounts", ex);
        }
    }

    public async Task<SyncAccountsResponse> SyncAccount(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Request to sync accounts");
        try
        {
            return await _signalAccounts.SyncAccountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error syncing accounts");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error syncing accounts", ex);
        }
    }
}