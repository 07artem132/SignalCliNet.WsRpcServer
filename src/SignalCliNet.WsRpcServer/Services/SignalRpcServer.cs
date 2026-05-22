using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using SignalCliNet.WsRpcServer.Sessions;
using WsRpcServer.Core;

namespace SignalCliNet.WsRpcServer.Services;

public class SignalRpcServer(
    IPAddress address,
    int port,
    IServiceProvider serviceProvider,
    ILogger<SignalRpcServer> logger)
    : AbstractJsonRpcServer(address, port, serviceProvider, logger)
{
    protected override WsSession CreateJsonRpcSession()
    {
        return ActivatorUtilities.CreateInstance<SignalRpcSession>(ServiceProvider, this);
    }
}