using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

// Юніт-тести декларативного реєстру політик (класифікація методів + default-deny lookup).
public class SignalRpcPolicyRegistryTests
{
    private readonly IRpcPolicyRegistry _registry = new SignalRpcPolicyRegistry();

    [Theory]
    [InlineData("ping", RpcPolicyKind.Public)]
    [InlineData("startLink", RpcPolicyKind.IdentityOnboarding)]
    [InlineData("finishLink", RpcPolicyKind.IdentityOnboarding)]
    [InlineData("listAccounts", RpcPolicyKind.AccountQuery)]
    [InlineData("listGroups", RpcPolicyKind.AccountQuery)]
    [InlineData("syncAccount", RpcPolicyKind.AccountCommand)]
    [InlineData("sendTextMessage", RpcPolicyKind.AccountCommand)]
    [InlineData("subscribe", RpcPolicyKind.AccountCommand)]
    [InlineData("unsubscribe", RpcPolicyKind.AccountCommand)]
    [InlineData("updateSubscription", RpcPolicyKind.AccountCommand)]
    public void Resolve_KnownMethod_ReturnsExpectedKind(string method, RpcPolicyKind expected)
    {
        var policy = _registry.Resolve(method);
        Assert.NotNull(policy);
        Assert.Equal(expected, policy!.Kind);
    }

    [Theory]
    [InlineData("deleteEverything")]
    [InlineData("")]
    [InlineData("SendTextMessage")] // case-sensitive: PascalCase не збігається з camelCase реєстрацією
    public void Resolve_UnknownMethod_ReturnsNull(string method)
    {
        Assert.Null(_registry.Resolve(method));
    }

    [Fact]
    public void SendTextMessage_CarriesAccountRecipientsAndTextArgs()
    {
        var p = _registry.Resolve("sendTextMessage")!;
        Assert.Equal("account", p.AccountArgName);
        Assert.Equal(0, p.AccountArgIndex);
        Assert.Equal("recipients", p.RecipientsArgName);
        Assert.Equal(1, p.RecipientsArgIndex);
        Assert.Equal("message", p.TextArgName);
        Assert.Equal(2, p.TextArgIndex);
    }

    [Fact]
    public void ListGroups_IsAccountGuardedAndGroupFiltered()
    {
        var p = _registry.Resolve("listGroups")!;
        Assert.Equal("account", p.AccountArgName);
        Assert.Equal(ReadOutputKind.Groups, p.Output);
    }

    [Fact]
    public void ListAccounts_FiltersAccountsOutput()
    {
        Assert.Equal(ReadOutputKind.Accounts, _registry.Resolve("listAccounts")!.Output);
    }
}
