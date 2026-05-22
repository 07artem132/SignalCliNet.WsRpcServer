using Microsoft.Extensions.Logging;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Services;

public sealed class RpcServiceRegistry(
    IServiceProvider serviceProvider,
    ILogger<RpcServiceRegistry> logger)
    : AbstractRpcServiceRegistry(serviceProvider, logger)
{
    protected override IEnumerable<string> GetAdditionalAssemblyPrefixes()
    {
        yield return "SignalCliNet.WsRpcServer";
    }
}