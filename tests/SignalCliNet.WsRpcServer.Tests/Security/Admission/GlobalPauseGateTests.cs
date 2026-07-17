using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Diagnostics;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить реальний <see cref="GlobalPauseGate"/> (task 2.2): пауза виставляє <see cref="IGlobalPauseGate.IsPaused"/>
/// + retry_after; auto-resume на монотонному deadline (D15); транзиції сурфейсяться <c>bot.paused</c>/
/// <c>bot.resumed</c> нотіфами й метрикою <c>wsrpc.app.global_pause</c>.
/// </summary>
[Collection("AppDiagnosticsMeter")]
public sealed class GlobalPauseGateTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private static GlobalPauseGate NewGate(TestTimeProvider time, CapturingNotifier? notifier = null) =>
        new(
            Options.Create(new GlobalPauseOptions { BasePauseSeconds = 60, MaxPauseSeconds = 900 }),
            time,
            NullLogger<GlobalPauseGate>.Instance,
            notifier);

    [Fact]
    public void PauseFor_SetsPaused_WithRetryAfter()
    {
        var time = new TestTimeProvider(FixedTime);
        var gate = NewGate(time);

        Assert.False(gate.IsPaused);
        Assert.Equal(0, gate.RetryAfterSeconds);

        gate.PauseFor(TimeSpan.FromSeconds(60), "rate-limit");

        Assert.True(gate.IsPaused);
        Assert.True(gate.RetryAfterSeconds >= 60, $"retry_after={gate.RetryAfterSeconds}");
    }

    [Fact]
    public void AutoResume_AfterMonotonicDeadline()
    {
        var time = new TestTimeProvider(FixedTime);
        var gate = NewGate(time);

        gate.PauseFor(TimeSpan.FromSeconds(60), "captcha");
        var retryAfter = gate.RetryAfterSeconds;
        Assert.True(gate.IsPaused);

        // Ще не минув deadline → лишається паузнутим.
        time.Advance(TimeSpan.FromSeconds(retryAfter - 1));
        Assert.True(gate.IsPaused);

        // Перейшли deadline → ленивий auto-resume.
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(gate.IsPaused);
        Assert.Equal(0, gate.RetryAfterSeconds);
    }

    [Fact]
    public void Notifier_ReceivesPausedThenResumed_WithReasonNoPii()
    {
        var time = new TestTimeProvider(FixedTime);
        var notifier = new CapturingNotifier();
        var gate = NewGate(time, notifier);

        gate.PauseFor(TimeSpan.FromSeconds(60), "rate-limit");

        var paused = Assert.Single(notifier.Calls, c => c.Method == "bot.paused");
        var pausedPayload = Assert.IsType<PauseStateNotification>(paused.Payload);
        Assert.Equal("rate-limit", pausedPayload.Reason);
        Assert.True(pausedPayload.RetryAfterSeconds > 0);

        // Перетинаємо deadline і читаємо стан → ленивий resume фаєрить bot.resumed.
        time.Advance(TimeSpan.FromSeconds(gate.RetryAfterSeconds + 1));
        _ = gate.IsPaused;

        Assert.Contains(notifier.Calls, c => c.Method == "bot.resumed");
    }

    [Fact]
    public void Metric_GaugeGoesOneThenZero_AcrossPauseResume()
    {
        var time = new TestTimeProvider(FixedTime);
        var gate = NewGate(time);

        var captured = new List<long>();
        using (var listener = new MeterListener())
        {
            listener.InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == AppDiagnostics.MeterName && instrument.Name == "wsrpc.app.global_pause")
                    l.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
            {
                lock (captured)
                    captured.Add(value);
            });
            listener.Start();

            gate.PauseFor(TimeSpan.FromSeconds(60), "rate-limit");
            time.Advance(TimeSpan.FromSeconds(gate.RetryAfterSeconds + 1));
            _ = gate.IsPaused;   // тригерить ленивий resume (метрика -1)
        }

        Assert.Contains(1L, captured);
        Assert.Contains(-1L, captured);
    }

    private sealed class CapturingNotifier : IAppNotifier
    {
        public List<(string Method, object Payload)> Calls { get; } = [];

        public void Broadcast(string method, object payload)
        {
            lock (Calls)
                Calls.Add((method, payload));
        }
    }
}
