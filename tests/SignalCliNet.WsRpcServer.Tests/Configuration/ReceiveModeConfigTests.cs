using Microsoft.Extensions.Configuration;
using SignalCli.Models;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Configuration;

/// <summary>
/// Пінить розв'язку receive-mode від GroupClaim (fix-receive-mode-default). Демон мусить тримати
/// authenticated receive-websocket до Signal-серверів, інакше акаунт офлайн для вхідних і протокол
/// деградує (виснаження prekey, немає delivery-receipts, linked-device не синкає групи/контакти).
/// Тому <c>SignalCli:ContinuousReceive</c> дефолтить у <c>true</c> (<c>on-start</c>) НЕЗАЛЕЖНО від
/// GroupClaim; явний <c>false</c> дає opt-in send-only (<c>manual</c>); а <c>Server:GroupClaim:Enabled</c>
/// (<c>forceContinuousReceive</c>) форсує continuous попри ключ — сканеру потрібен вхідний потік (R3.1).
/// </summary>
public class ReceiveModeConfigTests
{
    private static SignalCliOptions Apply(
        bool forceContinuousReceive, params (string Key, string? Value)[] config)
    {
        var values = config.ToDictionary(p => p.Key, p => p.Value);
        var built = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var options = new SignalCliOptions();
        Program.ApplySignalCliConfig(built.GetSection("SignalCli"), options, forceContinuousReceive);
        return options;
    }

    [Fact]
    public void Default_NoConfig_EnablesContinuousReceive()
    {
        // Дефолт (без ключа, GroupClaim вимкнено) — on-start. Виправлення manual-протокол-деградації:
        // раніше цей шлях лишав UseManualReceiveMode=true (send-only) і акаунт був офлайн для вхідних.
        Assert.False(Apply(forceContinuousReceive: false).UseManualReceiveMode);
    }

    [Fact]
    public void ContinuousReceiveFalse_OptsIntoSendOnlyManual()
    {
        Assert.True(Apply(forceContinuousReceive: false,
            ("SignalCli:ContinuousReceive", "false")).UseManualReceiveMode);
    }

    [Fact]
    public void ContinuousReceiveTrue_EnablesContinuousReceive()
    {
        Assert.False(Apply(forceContinuousReceive: false,
            ("SignalCli:ContinuousReceive", "true")).UseManualReceiveMode);
    }

    [Fact]
    public void GroupClaimForce_OverridesSendOnlyOptOut()
    {
        // GroupClaim:Enabled форсує continuous навіть коли ключ просить send-only —
        // сканер claim-кодів не працює без вхідного потоку.
        Assert.False(Apply(forceContinuousReceive: true,
            ("SignalCli:ContinuousReceive", "false")).UseManualReceiveMode);
    }
}
