using Microsoft.Extensions.Logging;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Devices;
using SignalCliNet.WsRpcServer.Interfaces;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalDevicesRpcAdapter(ISignalDevices signalDevices, ILogger<SignalDevicesRpcAdapter> logger)
    : ISignalDevicesRpc
{
    private readonly ISignalDevices _signalDevices =
        signalDevices ?? throw new ArgumentNullException(nameof(signalDevices));

    private readonly ILogger<SignalDevicesRpcAdapter> _logger =
        logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<StartLinkResponse> StartLink(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Request to start device linking");
        try
        {
            return await _signalDevices.StartLinkAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting device linking");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error starting device linking", ex);
        }
    }

    public async Task<FinishLinkResponse> FinishLink(string deviceLinkUri, string deviceName,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("RPC: Request to finish device linking");

        // Валідація на межі RPC: порожній URI/ім'я → InvalidParams, не InvocationError від демона
        if (string.IsNullOrWhiteSpace(deviceLinkUri))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "deviceLinkUri cannot be empty");

        if (string.IsNullOrWhiteSpace(deviceName))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "deviceName cannot be empty");

        try
        {
            return await _signalDevices.FinishLinkAsync(deviceLinkUri, deviceName, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (RpcErrorException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error finishing device linking");
            throw new RpcErrorException(JsonRpcErrorCode.InvocationError, "Error finishing device linking", ex);
        }
    }
}