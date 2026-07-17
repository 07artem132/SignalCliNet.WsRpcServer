using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Security.Fail;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

public class SignalMessageRpcAdapterTests
{
    private static UpstreamSendFailureMapper CreateMapper(IGlobalPauseGate? gate = null) =>
        new(
            gate ?? new NoopGlobalPauseGate(),
            Options.Create(new GlobalPauseOptions()),
            NullLogger<UpstreamSendFailureMapper>.Instance);

    private static SignalMessageRpcAdapter CreateAdapter(Mock<ISignalMessage> facade)
        => new(facade.Object, CreateMapper(), NullLogger<SignalMessageRpcAdapter>.Instance);

    [Fact]
    public async Task SendTextMessage_ValidInput_ReturnsFacadeResponse()
    {
        var expected = new SendMessageResponse(null, 1234567890L);
        var facade = new Mock<ISignalMessage>();
        facade.Setup(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var adapter = CreateAdapter(facade);

        var result = await adapter.SendTextMessage("+10000000001", ["+10000000002"], "hi");

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
            () => adapter.SendTextMessage(account!, ["+10000000002"], "hi"));

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
            () => adapter.SendTextMessage("+10000000001", [" ", ""], "hi"));

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
            () => adapter.SendTextMessage("+10000000001", ["+10000000002"], "hi"));

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }

    [Fact]
    public async Task SendTextMessage_UpstreamRateLimit_MapsTo32005_AndTriggersPause()
    {
        var facade = new Mock<ISignalMessage>();
        facade.Setup(m => m.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new SignalCli.Exceptions.RateLimitException(
                new SignalCli.Models.Rpc.JsonRpcError { Code = -5, Message = "rate limited" }));

        var gate = new RecordingGate();
        var adapter = new SignalMessageRpcAdapter(facade.Object, CreateMapper(gate), NullLogger<SignalMessageRpcAdapter>.Instance);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.SendTextMessage("+10000000001", ["+10000000002"], "hi"));

        Assert.Equal(-32005, (int)ex.ErrorCode);
        Assert.Equal(["rate-limit"], gate.Pauses);
    }

    private sealed class RecordingGate : SignalCliNet.WsRpcServer.Security.Admission.IGlobalPauseGate
    {
        public List<string> Pauses { get; } = [];
        public bool IsPaused { get; private set; }
        public int RetryAfterSeconds { get; private set; }

        public void PauseFor(TimeSpan minimumDuration, string reason)
        {
            Pauses.Add(reason);
            IsPaused = true;
            RetryAfterSeconds = Math.Max(1, (int)minimumDuration.TotalSeconds);
        }
    }
}
