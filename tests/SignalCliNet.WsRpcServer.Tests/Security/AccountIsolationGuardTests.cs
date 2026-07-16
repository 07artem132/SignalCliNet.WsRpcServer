using SignalCliNet.WsRpcServer.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

// Юніт-тести чистого AccountIsolationGuard (не DoD-диспетч-тест 4.2).
public class AccountIsolationGuardTests
{
    [Fact]
    public void AssertAccountAllowed_ForeignAccount_ThrowsUnauthorized()
    {
        var a = new SignalPrincipal("a", isAdmin: false, hasFullAccess: false,
            allowedAccounts: ["+10000000001"], allowedGroupIds: []);

        var ex = Assert.Throws<RpcErrorException>(
            () => AccountIsolationGuard.AssertAccountAllowed(a, "+10000000002"));

        Assert.Equal(-32001, (int)ex.ErrorCode);
        // Generic-повідомлення не розкриває існування чужого акаунта.
        Assert.DoesNotContain("+10000000002", ex.Message);
    }

    [Fact]
    public void AssertAccountAllowed_OwnAccount_DoesNotThrow()
    {
        var a = new SignalPrincipal("a", isAdmin: false, hasFullAccess: false,
            allowedAccounts: ["+10000000001"], allowedGroupIds: []);

        AccountIsolationGuard.AssertAccountAllowed(a, "+10000000001");
    }

    [Fact]
    public void AssertAccountAllowed_FullAccess_AllowsAny()
    {
        AccountIsolationGuard.AssertAccountAllowed(SignalPrincipal.LoopbackFullAccess, "+19999999999");
    }
}
