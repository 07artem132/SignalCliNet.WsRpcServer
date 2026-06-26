using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

/// <summary>
/// System / health RPC surface. Unauthenticated and stateless by design — exposes no
/// account, number, or configuration data, so it is safe as a container health probe.
/// </summary>
public interface ISystemRpc : IRpcService
{
    /// <summary>
    /// Liveness probe. Returns a constant token; carries no internal state.
    /// </summary>
    Task<string> Ping(CancellationToken cancellationToken = default);
}
