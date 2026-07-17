using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

/// <summary>
/// Пінить server-heartbeat (task 3.2): EmitOnce шле app-level <c>heartbeat</c>-нотіф з монотонним seq,
/// timestamp (з <see cref="TimeProvider"/>) і поточним pause-станом (C5, БЕЗ PII).
/// </summary>
[Collection("AppDiagnosticsMeter")]
public sealed class HeartbeatHostedServiceTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);

    private static HeartbeatHostedService NewService(CapturingNotifier notifier, bool paused, TimeProvider time) =>
        new(
            notifier,
            new StubGate(paused),
            time,
            Options.Create(new HeartbeatOptions { Enabled = true, IntervalSeconds = 20 }),
            NullLogger<HeartbeatHostedService>.Instance);

    [Fact]
    public void EmitOnce_BroadcastsHeartbeat_WithIncrementingSeq_AndTimestamp()
    {
        var notifier = new CapturingNotifier();
        var svc = NewService(notifier, paused: false, new TestTimeProvider(FixedTime));

        svc.EmitOnce();
        svc.EmitOnce();

        Assert.Equal(2, notifier.Calls.Count);
        Assert.All(notifier.Calls, c => Assert.Equal("heartbeat", c.Method));

        var first = Assert.IsType<HeartbeatNotification>(notifier.Calls[0].Payload);
        var second = Assert.IsType<HeartbeatNotification>(notifier.Calls[1].Payload);
        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);
        Assert.Equal(FixedTime.ToUnixTimeMilliseconds(), first.Timestamp);
        Assert.False(first.Paused);
    }

    [Fact]
    public void EmitOnce_ReflectsPausedState()
    {
        var notifier = new CapturingNotifier();
        var svc = NewService(notifier, paused: true, new TestTimeProvider(FixedTime));

        svc.EmitOnce();

        var payload = Assert.IsType<HeartbeatNotification>(Assert.Single(notifier.Calls).Payload);
        Assert.True(payload.Paused);
    }

    private sealed class CapturingNotifier : IAppNotifier
    {
        public List<(string Method, object Payload)> Calls { get; } = [];

        public void Broadcast(string method, object payload) => Calls.Add((method, payload));
    }

    private sealed class StubGate(bool paused) : IGlobalPauseGate
    {
        public bool IsPaused => paused;
        public int RetryAfterSeconds => paused ? 10 : 0;
        public void PauseFor(TimeSpan minimumDuration, string reason) { }
    }
}
