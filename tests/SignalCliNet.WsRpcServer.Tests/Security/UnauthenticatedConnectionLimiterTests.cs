using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить <see cref="UnauthenticatedConnectionLimiter"/> (tasks 2.4/2.6): cap на IP, звільнення слота і
/// per-IP ізоляцію лічильників.
/// </summary>
public class UnauthenticatedConnectionLimiterTests
{
    private static UnauthenticatedConnectionLimiter Create(int max) =>
        new(Options.Create(new AuthOptions { MaxUnauthenticatedPerIp = max }));

    private const string IpA = "203.0.113.1";
    private const string IpB = "203.0.113.2";

    [Fact]
    public void TryAcquire_UpToCap_ThenRefuses()
    {
        var limiter = Create(2);

        Assert.True(limiter.TryAcquire(IpA));
        Assert.True(limiter.TryAcquire(IpA));
        Assert.False(limiter.TryAcquire(IpA)); // cap досягнуто
    }

    [Fact]
    public void Release_FreesSlot_AllowingReacquire()
    {
        var limiter = Create(1);

        Assert.True(limiter.TryAcquire(IpA));
        Assert.False(limiter.TryAcquire(IpA));

        limiter.Release(IpA);

        Assert.True(limiter.TryAcquire(IpA)); // слот звільнився
    }

    [Fact]
    public void Counters_AreIsolatedPerIp()
    {
        var limiter = Create(1);

        Assert.True(limiter.TryAcquire(IpA));
        Assert.False(limiter.TryAcquire(IpA)); // A на cap

        // B має власний бюджет, незалежний від A.
        Assert.True(limiter.TryAcquire(IpB));
    }

    [Fact]
    public void Release_UnknownIp_IsNoOp()
    {
        var limiter = Create(1);

        // Не кидає і не робить лічильник від'ємним.
        limiter.Release(IpA);
        Assert.True(limiter.TryAcquire(IpA));
    }

    [Fact]
    public void Acquire_Release_Roundtrips_ManyTimes()
    {
        var limiter = Create(3);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire(IpA));
            limiter.Release(IpA);
        }

        // Після симетричних acquire/release весь бюджет знову доступний.
        Assert.True(limiter.TryAcquire(IpA));
        Assert.True(limiter.TryAcquire(IpA));
        Assert.True(limiter.TryAcquire(IpA));
        Assert.False(limiter.TryAcquire(IpA));
    }
}
