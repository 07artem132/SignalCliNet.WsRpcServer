using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Accounts;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

public class SignalAccountsRpcAdapterTests
{
    private static SignalAccountsRpcAdapter CreateAdapter(Mock<ISignalAccounts> facade)
        => new(facade.Object, NullLogger<SignalAccountsRpcAdapter>.Instance);

    [Fact]
    public async Task ListAccounts_ReturnsFacadeResponse()
    {
        var expected = new ListAccountsResponse([]);
        var facade = new Mock<ISignalAccounts>();
        facade.Setup(a => a.ListAccountsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var adapter = CreateAdapter(facade);

        var result = await adapter.ListAccounts();

        Assert.Same(expected, result);
        facade.Verify(a => a.ListAccountsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ListAccounts_FacadeThrows_WrappedAsInvocationError()
    {
        var facade = new Mock<ISignalAccounts>();
        facade.Setup(a => a.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.ListAccounts());

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }
}
