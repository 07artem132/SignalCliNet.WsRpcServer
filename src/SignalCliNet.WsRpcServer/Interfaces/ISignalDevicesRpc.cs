using SignalCli.Models.Signal.Devices;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

public interface ISignalDevicesRpc : IRpcService
{
    Task<StartLinkResponse> StartLink(CancellationToken cancellationToken = default);

    Task<FinishLinkResponse> FinishLink(string deviceLinkUri, string deviceName,
        CancellationToken cancellationToken = default);
}
