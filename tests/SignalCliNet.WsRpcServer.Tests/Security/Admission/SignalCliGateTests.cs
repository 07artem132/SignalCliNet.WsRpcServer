using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security.Admission;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить <see cref="SignalCliGate"/> (G6, task 3.3): неблокуючий <c>Wait(0)</c> — до ліміту слотів
/// <see cref="SignalCliGate.TryEnter"/> дає <c>true</c>, понад — одразу <c>false</c> (без блокування);
/// <see cref="SignalCliGate.Exit"/> звільняє слот.
/// </summary>
public class SignalCliGateTests
{
    private static SignalCliGate Create(int limit) =>
        new(Options.Create(new AdmissionOptions { SignalCliConcurrencyLimit = limit }));

    [Fact]
    public void TryEnter_UpToLimit_ThenRefusesWithoutBlocking()
    {
        using var gate = Create(2);

        Assert.True(gate.TryEnter());
        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter()); // повний — Wait(0) повертає одразу
    }

    [Fact]
    public void Exit_FreesSlot_AllowingReenter()
    {
        using var gate = Create(1);

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());

        gate.Exit();

        Assert.True(gate.TryEnter()); // слот звільнився
    }

    [Fact]
    public async Task TryEnter_IsNonBlocking_WhenFull()
    {
        using var gate = Create(1);
        Assert.True(gate.TryEnter());

        // Якби Wait(0) блокував — тест би завис; має повернутись миттєво false.
        var task = Task.Run(gate.TryEnter);
        var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(task, completed); // TryEnter завершився сам (неблокуючий), не по таймауту
        Assert.False(await task);
    }
}
