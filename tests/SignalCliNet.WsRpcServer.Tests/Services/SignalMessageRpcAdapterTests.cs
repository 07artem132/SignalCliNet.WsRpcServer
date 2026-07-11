using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

public class SignalMessageRpcAdapterTests
{
    private static SignalMessageRpcAdapter CreateAdapter(Mock<ISignalMessage> facade)
        => new(facade.Object, NullLogger<SignalMessageRpcAdapter>.Instance);

    [Fact]
    public async Task SendTextMessage_ValidInput_ReturnsFacadeResponse()
    {
        var expected = new SendMessageResponse(null, 1234567890L);
        var facade = new Mock<ISignalMessage>();
        facade.Setup(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var adapter = CreateAdapter(facade);

        var result = await adapter.SendTextMessage("+10000000001", "hi", ["+10000000002"]);

        Assert.Same(expected, result);
        facade.Verify(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task SendTextMessage_EmptyAccount_ThrowsInvalidParams(string? account)
    {
        var facade = new Mock<ISignalMessage>();
        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage(account!, "hi", ["+10000000002"]));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        facade.Verify(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendTextMessage_NoRecipients_ThrowsInvalidParams()
    {
        var facade = new Mock<ISignalMessage>();
        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage("+10000000001", "hi", [" ", ""]));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        facade.Verify(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SendTextMessage_FacadeThrows_WrappedAsInvocationError()
    {
        var facade = new Mock<ISignalMessage>();
        facade.Setup(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage("+10000000001", "hi", ["+10000000002"]));

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }

    [Fact]
    public async Task SendTextMessage_GroupOnly_MapsToGroupRecipient()
    {
        var expected = new SendMessageResponse(null, 1234567890L);
        TextMessageOptions? captured = null;
        var facade = new Mock<ISignalMessage>();
        facade.Setup(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()))
            .Callback<TextMessageOptions, CancellationToken>((o, _) => captured = o)
            .ReturnsAsync(expected);

        var adapter = CreateAdapter(facade);

        var result = await adapter.SendTextMessage(
            "+10000000001", "hi", groups: ["GROUP-ID-BASE64=="]);

        Assert.Same(expected, result);
        var recipient = Assert.Single(captured!.Recipients);
        Assert.True(recipient.IsGroup);
        Assert.Equal("GROUP-ID-BASE64==", recipient.Identifier);
    }

    [Fact]
    public async Task SendTextMessage_MixedUserAndGroup_ThrowsInvalidParams()
    {
        // Правило змішування живе у SignalCli.NET (ArgumentException) — адаптер має
        // транслювати його в InvalidParams, а не в загальний InvocationError.
        var facade = new Mock<ISignalMessage>();
        facade.Setup(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ArgumentException(
                "Не можна змішувати отримувачів-користувачів і отримувачів-груп у одному повідомленні",
                "recipients"));

        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage(
                "+10000000001", "hi", ["+10000000002"], groups: ["GROUP-ID-BASE64=="]));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
    }
}
