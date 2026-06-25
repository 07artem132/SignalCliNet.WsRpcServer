using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

// Інтеграційні тести адаптера ISignalMessageRpc: підкладаємо ISignalMessage через NSubstitute.
public class SignalMessageRpcAdapterTests
{
    private readonly ISignalMessage _signalMessage = Substitute.For<ISignalMessage>();

    private SignalMessageRpcAdapter CreateAdapter() =>
        new(_signalMessage, NullLogger<SignalMessageRpcAdapter>.Instance);

    [Fact]
    public async Task SendTextMessage_HappyPath_ReturnsResponseFromUnderlyingService()
    {
        // happy: валідний акаунт + один отримувач + текст → адаптер віддає відповідь фасаду.
        var expected = new SendMessageResponse(Results: null, TimeStamp: 1234567890L);
        _signalMessage
            .SendTextMessageAsync(Arg.Any<TextMessageOptions>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var adapter = CreateAdapter();
        var actual = await adapter.SendTextMessage(
            "+380501234567",
            ["+380507654321"],
            "Привіт, світ!");

        Assert.Same(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendTextMessage_WhenAccountEmpty_ThrowsInvalidParamsAndDoesNotCallUnderlying(string account)
    {
        // negative: порожній/пробільний акаунт → InvalidParams, кинуто ДО виклику фасаду.
        var adapter = CreateAdapter();

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage(account, ["+380507654321"], "Привіт"));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        // Перевіряємо, що фасад НЕ викликали взагалі.
        await _signalMessage.DidNotReceiveWithAnyArgs()
            .SendTextMessageAsync(default!, default);
    }

    [Fact]
    public async Task SendTextMessage_WhenRecipientsEmpty_ThrowsInvalidParamsAndDoesNotCallUnderlying()
    {
        // negative: порожній перелік отримувачів → InvalidParams, фасад не викликаний.
        var adapter = CreateAdapter();

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage("+380501234567", [], "Привіт"));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        await _signalMessage.DidNotReceiveWithAnyArgs()
            .SendTextMessageAsync(default!, default);
    }

    [Fact]
    public async Task SendTextMessage_WhenAllRecipientsWhitespace_ThrowsInvalidParamsAndDoesNotCallUnderlying()
    {
        // negative: усі отримувачі — пробіли → після фільтрації список порожній → InvalidParams.
        var adapter = CreateAdapter();

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage("+380501234567", ["", "   "], "Привіт"));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        await _signalMessage.DidNotReceiveWithAnyArgs()
            .SendTextMessageAsync(default!, default);
    }

    [Fact]
    public async Task SendTextMessage_WhenUnderlyingThrows_ThrowsRpcErrorWithInvocationError()
    {
        // negative: фасад кидає під час відправки → InvocationError.
        _signalMessage
            .SendTextMessageAsync(Arg.Any<TextMessageOptions>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("збій"));

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage("+380501234567", ["+380507654321"], "Привіт"));

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }
}
