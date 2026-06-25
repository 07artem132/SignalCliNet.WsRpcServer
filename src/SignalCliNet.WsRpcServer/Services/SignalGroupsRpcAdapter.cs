using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Interfaces;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalGroupsRpcAdapter(ISignalGroups signalGroups, ILogger<SignalGroupsRpcAdapter> logger)
    : ISignalGroupsRpc
{
    private readonly ISignalGroups _signalGroups =
        signalGroups ?? throw new ArgumentNullException(nameof(signalGroups));

    private readonly ILogger<SignalGroupsRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<ListGroupsResponse> ListGroups(string account, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Request to list groups");

        if (string.IsNullOrWhiteSpace(account))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Account cannot be empty");

        try
        {
            return await _signalGroups.ListGroupsAsync(account, cancellationToken).ConfigureAwait(false);
        }
        catch (RpcErrorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing groups");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error listing groups", ex);
        }
    }
}
