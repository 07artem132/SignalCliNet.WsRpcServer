using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Health / liveness + handshake adapter. Intentionally trivial — no dependencies, no state, no PII.
/// </summary>
public sealed class SystemRpcAdapter : ISystemRpc
{
    /// <inheritdoc />
    public Task<string> Ping(CancellationToken cancellationToken = default)
        => Task.FromResult("pong");

    /// <inheritdoc />
    public Task<HandshakeResponse> Handshake(int apiVersion, CancellationToken cancellationToken = default)
    {
        // A5: несумісна версія → типізована upgrade-required (не тихий -32601). Author-controlled
        // ErrorData несе потрібну версію серверу; санітизація її зберігає (author вважає безпечною).
        if (!SignalRpcApiVersion.IsCompatible(apiVersion))
        {
            throw new RpcErrorException(
                (JsonRpcErrorCode)SignalRpcApiVersion.UpgradeRequiredErrorCode,
                "Client API version is not supported; upgrade required.",
                new ApiVersionRequirement(SignalRpcApiVersion.Current, SignalRpcApiVersion.MinimumSupported));
        }

        return Task.FromResult(new HandshakeResponse(
            SignalRpcApiVersion.Current,
            SignalRpcApiVersion.MinimumSupported,
            SignalRpcApiVersion.Capabilities));
    }
}
