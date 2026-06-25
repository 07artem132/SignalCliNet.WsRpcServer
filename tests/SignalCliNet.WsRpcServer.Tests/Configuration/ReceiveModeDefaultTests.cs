using SignalCli.Models;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

// Phase-1 DoD: пінуємо припущення "send-only / manual receive" (task-7 no-op підтвердження).
public class ReceiveModeDefaultTests
{
    [Fact]
    public void SignalCliOptions_ByDefault_UsesManualReceiveMode()
    {
        // За замовчуванням SignalCliOptions має бути у ручному режимі отримання.
        var options = new SignalCliOptions();

        Assert.True(options.UseManualReceiveMode);
    }
}
