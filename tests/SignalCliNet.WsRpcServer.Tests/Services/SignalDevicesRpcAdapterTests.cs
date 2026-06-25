using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Devices;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

// Інтеграційні тести адаптера ISignalDevicesRpc: підкладаємо ISignalDevices через NSubstitute.
public class SignalDevicesRpcAdapterTests
{
    private readonly ISignalDevices _signalDevices = Substitute.For<ISignalDevices>();

    private SignalDevicesRpcAdapter CreateAdapter() =>
        new(_signalDevices, NullLogger<SignalDevicesRpcAdapter>.Instance);

    [Fact]
    public async Task StartLink_HappyPath_ReturnsResponseFromUnderlyingService()
    {
        // happy: фасад повертає URI для QR-коду — адаптер віддає його без змін.
        var expected = new StartLinkResponse("sgnl://linkdevice?uuid=test");
        _signalDevices.StartLinkAsync(Arg.Any<CancellationToken>()).Returns(expected);

        var adapter = CreateAdapter();
        var actual = await adapter.StartLink();

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task StartLink_WhenUnderlyingThrows_ThrowsRpcErrorWithInvocationError()
    {
        // negative: фасад кидає → InvocationError.
        _signalDevices.StartLinkAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("збій"));

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.StartLink());

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }

    [Fact]
    public async Task FinishLink_HappyPath_ReturnsResponseFromUnderlyingService()
    {
        // happy: фасад завершує зв'язування й повертає номер — адаптер віддає його як є.
        var expected = new FinishLinkResponse("+380501234567");
        _signalDevices
            .FinishLinkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(expected);

        var adapter = CreateAdapter();
        var actual = await adapter.FinishLink("sgnl://linkdevice?uuid=test", "Мій пристрій");

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task FinishLink_WhenUnderlyingThrows_ThrowsRpcErrorWithInvocationError()
    {
        // negative: фасад кидає → InvocationError.
        _signalDevices
            .FinishLinkAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("збій"));

        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.FinishLink("sgnl://linkdevice?uuid=test", "Мій пристрій"));

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FinishLink_WhenDeviceLinkUriEmpty_ThrowsInvalidParamsAndDoesNotCallUnderlying(string uri)
    {
        // negative: порожній URI → InvalidParams ще до виклику фасада.
        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.FinishLink(uri, "Мій пристрій"));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        await _signalDevices.DidNotReceiveWithAnyArgs()
            .FinishLinkAsync(default!, default!, default);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task FinishLink_WhenDeviceNameEmpty_ThrowsInvalidParamsAndDoesNotCallUnderlying(string name)
    {
        // negative: порожнє ім'я пристрою → InvalidParams ще до виклику фасада.
        var adapter = CreateAdapter();
        var ex = await Assert.ThrowsAsync<RpcErrorException>(
            () => adapter.FinishLink("sgnl://linkdevice?uuid=test", name));

        Assert.Equal(JsonRpcErrorCode.InvalidParams, ex.ErrorCode);
        await _signalDevices.DidNotReceiveWithAnyArgs()
            .FinishLinkAsync(default!, default!, default);
    }
}
