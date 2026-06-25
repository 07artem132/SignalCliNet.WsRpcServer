using SignalCliNet.WsRpcServer.Services;
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
}
