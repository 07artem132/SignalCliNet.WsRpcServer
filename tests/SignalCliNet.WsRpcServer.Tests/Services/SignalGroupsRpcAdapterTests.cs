using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

public class SignalGroupsRpcAdapterTests
{
    private static SignalGroupsRpcAdapter CreateAdapter(Mock<ISignalGroups> facade)
        => new(facade.Object, NullLogger<SignalGroupsRpcAdapter>.Instance);

    [Fact]
    public async Task ListGroups_ValidAccount_ReturnsFacadeResponse()
    {
        var expected = new ListGroupsResponse([]);
        var facade = new Mock<ISignalGroups>();
        facade.Setup(g => g.ListGroupsAsync("+10000000001", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var adapter = CreateAdapter(facade);

        var result = await adapter.ListGroups("+10000000001");

        Assert.Same(expected, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task ListGroups_EmptyAccount_ThrowsInvalidParams(string? account)
    {
        var facade = new Mock<ISignalGroups>();
        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.ListGroups(account!));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        facade.Verify(g => g.ListGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ListGroups_FacadeThrows_WrappedAsInvocationError()
    {
        var facade = new Mock<ISignalGroups>();
        facade.Setup(g => g.ListGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.ListGroups("+10000000001"));

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }
}
