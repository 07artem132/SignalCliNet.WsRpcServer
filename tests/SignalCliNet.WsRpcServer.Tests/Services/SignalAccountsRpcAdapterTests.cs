using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Accounts;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

// Інтеграційні тести адаптера ISignalAccountsRpc: підкладаємо ISignalAccounts через NSubstitute.
public class SignalAccountsRpcAdapterTests
{
    private readonly ISignalAccounts _signalAccounts = Substitute.For<ISignalAccounts>();

    private SignalAccountsRpcAdapter CreateAdapter() =>
        new(_signalAccounts, NullLogger<SignalAccountsRpcAdapter>.Instance);

    [Fact]
    public async Task ListAccounts_HappyPath_ReturnsResponseFromUnderlyingService()
    {
        // happy: фасад повертає список акаунтів — адаптер віддає його без змін.
        var expected = new ListAccountsResponse([new Account("+380501234567")]);
        _signalAccounts.ListAccountsAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var adapter = CreateAdapter();
        var actual = await adapter.ListAccounts();

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task ListAccounts_WhenUnderlyingThrows_ThrowsRpcErrorWithInvocationError()
    {
        // negative: фасад кидає InvalidOperationException → адаптер мапить на InvocationError.
        _signalAccounts.ListAccountsAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("збій"));

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.ListAccounts());

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }

    [Fact]
    public async Task SyncAccount_HappyPath_ReturnsResponseFromUnderlyingService()
    {
        // happy: фасад повертає порожню SyncAccountsResponse — адаптер віддає її як є.
        var expected = new SyncAccountsResponse();
        _signalAccounts.SyncAccountAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var adapter = CreateAdapter();
        var actual = await adapter.SyncAccount();

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task SyncAccount_WhenUnderlyingThrows_ThrowsRpcErrorWithInvocationError()
    {
        // negative: фасад кидає → InvocationError.
        _signalAccounts.SyncAccountAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("збій"));

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.SyncAccount());

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }
}
