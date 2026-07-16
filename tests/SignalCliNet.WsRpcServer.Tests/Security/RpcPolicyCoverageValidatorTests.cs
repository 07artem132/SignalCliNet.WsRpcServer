using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

// Юніт-тести startup coverage guard (W23, task 3.3).
public class RpcPolicyCoverageValidatorTests
{
    [Fact]
    public void ValidateCoverage_RealRegistry_CoversEveryDiscoverableMethod()
    {
        // Кожен RPC-метод збірки має явну політику → без винятку.
        RpcPolicyCoverageValidator.ValidateCoverage(new SignalRpcPolicyRegistry());
    }

    [Fact]
    public void FindUncovered_RealRegistry_IsEmpty()
    {
        var uncovered = RpcPolicyCoverageValidator.FindUncoveredMethods(
            new SignalRpcPolicyRegistry(),
            typeof(SignalRpcPolicyRegistry).Assembly);

        Assert.Empty(uncovered);
    }

    [Fact]
    public void ValidateCoverage_RegistryMissingAMethod_ThrowsLoudly()
    {
        // Реєстр, що навмисно НЕ знає жодного методу → guard валить старт із переліком.
        var ex = Assert.Throws<InvalidOperationException>(
            () => RpcPolicyCoverageValidator.ValidateCoverage(new EmptyRegistry()));

        Assert.Contains("sendTextMessage", ex.Message);
        Assert.Contains("handshake", ex.Message);
    }

    private sealed class EmptyRegistry : IRpcPolicyRegistry
    {
        public RpcMethodPolicy? Resolve(string method) => null;
        public IReadOnlyCollection<RpcMethodPolicy> All => [];
    }
}
