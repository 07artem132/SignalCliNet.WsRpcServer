using Microsoft.Extensions.Configuration;
using SignalCli.Models;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

/// <summary>
/// Пінить R3.1-гейтинг receive-mode (add-group-claim-receive, task 1.1): без прапорця
/// (<c>continuousReceive=false</c>) лишається бібліотечний дефолт <c>UseManualReceiveMode=true</c>
/// (send-only, поведінка Фази-1); з прапорцем — безперервний авто-receive.
/// </summary>
public class GroupClaimReceiveModeTests
{
    private static SignalCliOptions Apply(bool continuousReceive)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([]).Build();
        var options = new SignalCliOptions();
        Program.ApplySignalCliConfig(config.GetSection("SignalCli"), options, continuousReceive);
        return options;
    }

    [Fact]
    public void GroupClaimDisabled_KeepsManualReceive()
    {
        Assert.True(Apply(continuousReceive: false).UseManualReceiveMode);
    }

    [Fact]
    public void GroupClaimEnabled_EnablesContinuousReceive()
    {
        Assert.False(Apply(continuousReceive: true).UseManualReceiveMode);
    }
}
