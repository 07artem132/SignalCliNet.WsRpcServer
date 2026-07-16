using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

public class SystemRpcAdapterTests
{
    [Fact]
    public async Task Ping_ReturnsPong()
    {
        var adapter = new SystemRpcAdapter();

        var result = await adapter.Ping();

        Assert.Equal("pong", result);
    }

    [Fact]
    public async Task Handshake_CompatibleVersion_ReturnsServerContract()
    {
        var adapter = new SystemRpcAdapter();

        var result = await adapter.Handshake(SignalRpcApiVersion.Current);

        Assert.Equal(SignalRpcApiVersion.Current, result.ApiVersion);
        Assert.Equal(SignalRpcApiVersion.MinimumSupported, result.MinimumApiVersion);
        Assert.Contains("authz-chokepoint", result.Capabilities);
    }

    [Fact]
    public async Task Handshake_IncompatibleVersion_ThrowsTypedUpgradeRequired()
    {
        var adapter = new SystemRpcAdapter();

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.Handshake(SignalRpcApiVersion.Current + 1));

        Assert.Equal(SignalRpcApiVersion.UpgradeRequiredErrorCode, (int)ex.ErrorCode);
        // Типізований ErrorData несе потрібну версію серверу (не тихий -32601).
        var requirement = Assert.IsType<ApiVersionRequirement>(ex.ErrorData);
        Assert.Equal(SignalRpcApiVersion.Current, requirement.ServerApiVersion);
    }
}
