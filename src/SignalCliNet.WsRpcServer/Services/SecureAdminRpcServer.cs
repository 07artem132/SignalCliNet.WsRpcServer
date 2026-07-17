using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetCoreServer;
using SignalCliNet.WsRpcServer.Sessions;
using WsRpcServer.Core;
using WsRpcServer.Security;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// The mTLS admin JSON-RPC server (add-invites-admin, task 2.3): a <c>wss://</c> listener on a SEPARATE
/// port from the plain token port. Every connection presents a client certificate; the session resolves
/// it to the admin identity (or refuses the connection). Mirrors <see cref="SignalRpcServer"/> on the
/// secure (<see cref="AbstractSecureJsonRpcServer"/>) hierarchy.
/// </summary>
public sealed class SecureAdminRpcServer(
    SecureTransport transport,
    IPAddress address,
    int port,
    IServiceProvider serviceProvider,
    ILogger<SecureAdminRpcServer> logger)
    : AbstractSecureJsonRpcServer(transport, address, port, serviceProvider, logger)
{
    /// <inheritdoc />
    protected override WssSession CreateJsonRpcSession() =>
        ActivatorUtilities.CreateInstance<SecureAdminRpcSession>(ServiceProvider, this);
}
