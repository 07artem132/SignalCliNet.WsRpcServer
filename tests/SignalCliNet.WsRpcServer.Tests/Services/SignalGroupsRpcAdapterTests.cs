using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

// Інтеграційні тести адаптера ISignalGroupsRpc: підкладаємо ISignalGroups через NSubstitute.
public class SignalGroupsRpcAdapterTests
{
    private readonly ISignalGroups _signalGroups = Substitute.For<ISignalGroups>();

    private SignalGroupsRpcAdapter CreateAdapter() =>
        new(_signalGroups, NullLogger<SignalGroupsRpcAdapter>.Instance);

    [Fact]
    public async Task ListGroups_HappyPath_ReturnsResponseFromUnderlyingService()
    {
        // happy: фасад повертає список груп — адаптер віддає його без змін.
        var expected = new ListGroupsResponse([]);
        _signalGroups.ListGroupsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var adapter = CreateAdapter();
        var actual = await adapter.ListGroups("+380501234567");

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ListGroups_WhenAccountEmpty_ThrowsInvalidParamsAndDoesNotCallUnderlying(string account)
    {
        // negative: порожній/пробільний акаунт → InvalidParams, кинуто ДО виклику фасаду.
        var adapter = CreateAdapter();

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.ListGroups(account));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        await _signalGroups.DidNotReceiveWithAnyArgs().ListGroupsAsync(default!, default);
    }

    [Fact]
    public async Task ListGroups_WhenUnderlyingThrows_ThrowsRpcErrorWithInvocationError()
    {
        // negative: фасад кидає → InvocationError.
        _signalGroups.ListGroupsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("збій"));

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.ListGroups("+380501234567"));

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }
}
