using SignalCli.Models;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

/// <summary>
/// Пінить передумову send-only MVP: signal-cli стартує у <c>--receive-mode=manual</c> з коробки,
/// бо <see cref="SignalCliOptions.UseManualReceiveMode"/> default = <c>true</c>, а
/// <c>Program.cs</c> його НЕ перевизначає. Якщо майбутній bump SignalCli.NET інвертує дефолт —
/// цей тест впаде, нагадавши, що демон почне eager-receive за всі акаунти.
/// (Phase-2 group-claim згодом свідомо вмикає авто-receive — R3.1.)
/// </summary>
public class ReceiveModeDefaultTests
{
    [Fact]
    public void SignalCliOptions_DefaultsToManualReceiveMode()
    {
        var options = new SignalCliOptions();

        Assert.True(options.UseManualReceiveMode);
    }
}
