using SignalCliNet.WsRpcServer.Interfaces;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Health / liveness adapter. Intentionally trivial — no dependencies, no state, no PII.
/// </summary>
public sealed class SystemRpcAdapter : ISystemRpc
{
    /// <inheritdoc />
    public Task<string> Ping(CancellationToken cancellationToken = default)
        => Task.FromResult("pong");
}
