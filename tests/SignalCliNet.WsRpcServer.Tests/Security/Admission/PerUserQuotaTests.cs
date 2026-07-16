using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security.Admission;

/// <summary>
/// Пінить <see cref="PerUserQuota"/> (G2): гарантований floor кожній identity під агрегатом, fair-share
/// понад floor (один юзер не голодоморить floor іншого) і монотонний скид вікна.
/// </summary>
public class PerUserQuotaTests
{
    private static readonly DateTimeOffset FixedTime = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    private static PerUserQuota New(int aggregate, int floor, TimeProvider time, int windowMinutes = 60) =>
        new(
            Options.Create(new AdmissionOptions
            {
                AggregateBudgetPerWindow = aggregate,
                PerUserFloorPerWindow = floor,
                BudgetWindowMinutes = windowMinutes,
            }),
            time);

    [Fact]
    public void Floor_IsGuaranteed_WhenAnotherExhaustsFairShare()
    {
        // aggregate=10, floor=3, дві активні identity. A виїдає fair-share, але floor B лишається.
        var quota = New(aggregate: 10, floor: 3, new TestTimeProvider(FixedTime));

        // B зʼявляється першим (стає активною, резервує floor).
        Assert.True(quota.TryAdmit("B"));

        // A шле, скільки може: floor(3) + above-floor поки committed < aggregate.
        var aGranted = 0;
        while (quota.TryAdmit("A"))
            aGranted++;

        // floor 3 + above-floor 4 (used_A 3→7, committed=used_A+max(1,3)) = 7.
        Assert.Equal(7, aGranted);

        // Незважаючи на «жадібність» A, B добирає свій гарантований floor (used_B 1→3).
        Assert.True(quota.TryAdmit("B"));
        Assert.True(quota.TryAdmit("B"));
        // Понад floor B вже нема запасу (committed=7+3=10 ≥ aggregate).
        Assert.False(quota.TryAdmit("B"));
    }

    [Fact]
    public void BelowFloor_AlwaysAdmitted_UpToFloor()
    {
        // Одна identity, aggregate==floor: гарантовано рівно floor, далі — deny.
        var quota = New(aggregate: 3, floor: 3, new TestTimeProvider(FixedTime));

        Assert.True(quota.TryAdmit("A"));
        Assert.True(quota.TryAdmit("A"));
        Assert.True(quota.TryAdmit("A"));   // floor вичерпано
        Assert.False(quota.TryAdmit("A"));  // понад floor і committed=3 ≥ aggregate
    }

    [Fact]
    public void WindowReset_RestoresQuota()
    {
        var time = new TestTimeProvider(FixedTime);
        var quota = New(aggregate: 1, floor: 1, time, windowMinutes: 60);

        Assert.True(quota.TryAdmit("A"));   // floor
        Assert.False(quota.TryAdmit("A"));  // понад floor, committed=1 ≥ aggregate

        time.Advance(TimeSpan.FromMinutes(61));   // монотонний скид вікна (D15)
        Assert.True(quota.TryAdmit("A"));   // нове вікно → floor знову
    }

    [Fact]
    public void RetryAfterSeconds_IsPositive()
    {
        var quota = New(aggregate: 1, floor: 1, new TestTimeProvider(FixedTime));

        Assert.True(quota.RetryAfterSeconds("A") > 0);
    }
}
