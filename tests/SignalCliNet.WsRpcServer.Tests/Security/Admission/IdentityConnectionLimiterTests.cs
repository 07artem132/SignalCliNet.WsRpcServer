using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security.Admission;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить <see cref="IdentityConnectionLimiter"/> (task 3.2): per-identity cap одночасних конектів,
/// звільнення слота і per-identity ізоляцію лічильників (дзеркало UnauthenticatedConnectionLimiter).
/// </summary>
public class IdentityConnectionLimiterTests
{
    private static IdentityConnectionLimiter Create(int max) =>
        new(Options.Create(new AdmissionOptions { MaxConnectionsPerIdentity = max }));

    private const string IdA = "identity-a";
    private const string IdB = "identity-b";

    [Fact]
    public void TryAcquire_UpToCap_ThenRefuses()
    {
        var limiter = Create(2);

        Assert.True(limiter.TryAcquire(IdA));
        Assert.True(limiter.TryAcquire(IdA));
        Assert.False(limiter.TryAcquire(IdA)); // cap досягнуто
    }

    [Fact]
    public void Release_FreesSlot_AllowingReacquire()
    {
        var limiter = Create(1);

        Assert.True(limiter.TryAcquire(IdA));
        Assert.False(limiter.TryAcquire(IdA));

        limiter.Release(IdA);

        Assert.True(limiter.TryAcquire(IdA)); // слот звільнився
    }

    [Fact]
    public void Counters_AreIsolatedPerIdentity()
    {
        var limiter = Create(1);

        Assert.True(limiter.TryAcquire(IdA));
        Assert.False(limiter.TryAcquire(IdA)); // A на cap

        // B має власний бюджет, незалежний від A.
        Assert.True(limiter.TryAcquire(IdB));
    }

    [Fact]
    public void Release_UnknownIdentity_IsNoOp()
    {
        var limiter = Create(1);

        limiter.Release(IdA); // не кидає, не робить лічильник від'ємним
        Assert.True(limiter.TryAcquire(IdA));
    }

    [Fact]
    public void Acquire_Release_Roundtrips_ManyTimes()
    {
        var limiter = Create(3);

        for (var i = 0; i < 100; i++)
        {
            Assert.True(limiter.TryAcquire(IdA));
            limiter.Release(IdA);
        }

        // Після симетричних acquire/release весь бюджет знову доступний.
        Assert.True(limiter.TryAcquire(IdA));
        Assert.True(limiter.TryAcquire(IdA));
        Assert.True(limiter.TryAcquire(IdA));
        Assert.False(limiter.TryAcquire(IdA));
    }

    [Fact]
    public void ConcurrentAcquire_NeverExceedsCap()
    {
        const int cap = 8;
        var limiter = Create(cap);
        var granted = 0;

        Parallel.For(0, 64, _ =>
        {
            if (limiter.TryAcquire(IdA))
                Interlocked.Increment(ref granted);
        });

        Assert.Equal(cap, granted); // рівно cap слотів видано, попри гонку
    }
}
