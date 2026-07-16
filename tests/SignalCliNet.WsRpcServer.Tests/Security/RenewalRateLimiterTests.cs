using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="RenewalRateLimiter"/> (task 3.5): cap на годину, ковзне вікно (звільнення після
/// виходу позначок за годину) та ізоляція бюджету per-identity.
/// </summary>
public class RenewalRateLimiterTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static RenewalRateLimiter New(int cap, TimeProvider time) =>
        new(Options.Create(new AuthOptions { RenewalPerIdentityPerHour = cap }), time);

    [Fact]
    public void TryAcquire_WithinCap_AllowsThenBlocks()
    {
        var limiter = New(3, new TestTimeProvider(FixedTime));

        Assert.True(limiter.TryAcquire("id-1"));
        Assert.True(limiter.TryAcquire("id-1"));
        Assert.True(limiter.TryAcquire("id-1"));
        Assert.False(limiter.TryAcquire("id-1"));   // 4-та за годину при cap 3
    }

    [Fact]
    public void TryAcquire_SlidingWindow_FreesAfterHour()
    {
        var time = new TestTimeProvider(FixedTime);
        var limiter = New(2, time);

        Assert.True(limiter.TryAcquire("id-1"));
        Assert.True(limiter.TryAcquire("id-1"));
        Assert.False(limiter.TryAcquire("id-1"));

        time.Advance(TimeSpan.FromHours(1));   // обидві позначки випадають із вікна
        Assert.True(limiter.TryAcquire("id-1"));
    }

    [Fact]
    public void TryAcquire_PartialSlide_FreesOnlyExpired()
    {
        var time = new TestTimeProvider(FixedTime);
        var limiter = New(2, time);

        Assert.True(limiter.TryAcquire("id-1"));        // t=0
        time.Advance(TimeSpan.FromMinutes(30));
        Assert.True(limiter.TryAcquire("id-1"));        // t=30m
        Assert.False(limiter.TryAcquire("id-1"));       // обидві у вікні → блок

        time.Advance(TimeSpan.FromMinutes(31));         // t=61m: t=0 випала, t=30m лишилась
        Assert.True(limiter.TryAcquire("id-1"));        // 1 у вікні → дозвіл
        Assert.False(limiter.TryAcquire("id-1"));       // знову 2 → блок
    }

    [Fact]
    public void TryAcquire_PerIdentityIsolation()
    {
        var limiter = New(1, new TestTimeProvider(FixedTime));

        Assert.True(limiter.TryAcquire("id-1"));
        Assert.False(limiter.TryAcquire("id-1"));   // бюджет id-1 вичерпано
        Assert.True(limiter.TryAcquire("id-2"));    // у id-2 власний бюджет
    }
}
