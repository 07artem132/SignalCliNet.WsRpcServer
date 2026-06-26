using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Devices;
using SignalCliNet.WsRpcServer.Services;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

public class SignalDevicesRpcAdapterTests
{
    private static SignalDevicesRpcAdapter CreateAdapter(Mock<ISignalDevices> facade)
        => new(facade.Object, NullLogger<SignalDevicesRpcAdapter>.Instance);

    [Fact]
    public async Task StartLink_ReturnsFacadeResponse()
    {
        var expected = new StartLinkResponse("sgnl://linkdevice?uuid=abc&pub_key=def");
        var facade = new Mock<ISignalDevices>();
        facade.Setup(d => d.StartLinkAsync(It.IsAny<CancellationToken>())).ReturnsAsync(expected);

        var adapter = CreateAdapter(facade);

        var result = await adapter.StartLink();

        Assert.Same(expected, result);
    }

    [Fact]
    public async Task FinishLink_PassesArgsAndReturnsFacadeResponse()
    {
        var expected = new FinishLinkResponse("+10000000003");
        var facade = new Mock<ISignalDevices>();
        facade.Setup(d => d.FinishLinkAsync("uri", "device", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var adapter = CreateAdapter(facade);

        var result = await adapter.FinishLink("uri", "device");

        Assert.Same(expected, result);
        facade.Verify(d => d.FinishLinkAsync("uri", "device", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task StartLink_FacadeThrows_WrappedAsInvocationError()
    {
        var facade = new Mock<ISignalDevices>();
        facade.Setup(d => d.StartLinkAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var adapter = CreateAdapter(facade);

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => adapter.StartLink());

        Assert.Equal(JsonRpcErrorCode.InvocationError, ex.ErrorCode);
    }
}
