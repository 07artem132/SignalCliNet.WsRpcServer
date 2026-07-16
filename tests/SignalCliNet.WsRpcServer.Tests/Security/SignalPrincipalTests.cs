using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

// Юніт-тести чистої логіки видимості/оперування SignalPrincipal (не DoD-диспетч-тести розділу 4).
public class SignalPrincipalTests
{
    private static SignalPrincipal User(params string[] accounts) =>
        new("user", isAdmin: false, hasFullAccess: false, allowedAccounts: accounts, allowedGroupIds: []);

    [Fact]
    public void LoopbackFullAccess_OperatesAndSeesEverything()
    {
        var p = SignalPrincipal.LoopbackFullAccess;

        Assert.True(p.CanOperateAccount("+10000000001"));
        Assert.True(p.CanSeeAccount("+19999999999"));
        Assert.True(p.CanSeeGroup("any-group"));
    }

    [Fact]
    public void RestrictedUser_OnlyOwnAccounts()
    {
        var p = User("+10000000001");

        Assert.True(p.CanOperateAccount("+10000000001"));
        Assert.False(p.CanOperateAccount("+10000000002"));
        Assert.True(p.CanSeeAccount("+10000000001"));
        Assert.False(p.CanSeeAccount("+10000000002"));
    }

    [Fact]
    public void Admin_SeesAllAccounts_ButDoesNotOperateOthers()
    {
        // R3.6: visibility ≠ operation.
        var admin = new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false,
            allowedAccounts: ["+10000000001"], allowedGroupIds: []);

        Assert.True(admin.CanSeeAccount("+10000000002"));   // бачить чужий
        Assert.False(admin.CanOperateAccount("+10000000002")); // але не оперує ним
        Assert.False(admin.CanSeeGroup("someone-else-group")); // групи — без спец-видимості (R3.2)
    }

    [Fact]
    public void Account_ComparisonIgnoresSurroundingWhitespace()
    {
        var p = User("+10000000001");
        Assert.True(p.CanOperateAccount("  +10000000001 "));
    }
}
