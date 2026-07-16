using SignalCliNet.WsRpcServer.Model;
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

    /// <summary>
    /// Contract handshake / capability negotiation (A5). The client sends the contract version it
    /// speaks; the server replies with its own version + capabilities, or fails with a typed
    /// "upgrade required" error if the versions are incompatible (never a silent method-not-found).
    /// </summary>
    /// <param name="apiVersion">The contract version the client speaks.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The server's contract version and capabilities.</returns>
    Task<HandshakeResponse> Handshake(int apiVersion, CancellationToken cancellationToken = default);
}
