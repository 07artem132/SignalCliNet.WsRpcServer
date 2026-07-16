using Microsoft.Extensions.Logging.Abstractions;
using SignalCli.Models.Signal.Accounts;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

// Юніт-тести чистого ReadOutputFilter (видимість + fail-closed) — не DoD-диспетч-тест 4.3.
public class ReadOutputFilterTests
{
    private static ListAccountsResponse Accounts(params string[] numbers) =>
        new(numbers.Select(n => new Account(n)).ToList());

    private static ListGroupsResponse Groups(params string[] ids) =>
        new(ids.Select(id => new Group(id, null, null, true, false, 0, [], [], [], [], [],
            "EVERY_MEMBER", "EVERY_MEMBER", "EVERY_MEMBER", null)).ToList());

    private static SignalPrincipal User(string[] accounts, string[] groups) =>
        new("user", isAdmin: false, hasFullAccess: false, allowedAccounts: accounts, allowedGroupIds: groups);

    [Fact]
    public void FilterAccounts_RestrictedUser_SeesOnlyOwn()
    {
        var p = User(["+10000000001"], []);
        var filtered = ReadOutputFilter.FilterAccounts(p, Accounts("+10000000001", "+10000000002"));

        Assert.Single(filtered.Items);
        Assert.Equal("+10000000001", filtered.Items[0].Number);
    }

    [Fact]
    public void FilterAccounts_Admin_SeesAll()
    {
        var admin = new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false,
            allowedAccounts: ["+10000000001"], allowedGroupIds: []);
        var filtered = ReadOutputFilter.FilterAccounts(admin, Accounts("+10000000001", "+10000000002"));

        Assert.Equal(2, filtered.Items.Count);
    }

    [Fact]
    public void FilterGroups_RestrictedUser_SeesOnlyClaimed()
    {
        // R3.2: лише claim'нуті/власні; admin спец-видимості груп не має.
        var admin = new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false,
            allowedAccounts: [], allowedGroupIds: ["g-own"]);
        var filtered = ReadOutputFilter.FilterGroups(admin, Groups("g-own", "g-other"));

        Assert.Single(filtered.Items);
        Assert.Equal("g-own", filtered.Items[0].Id);
    }

    [Fact]
    public void Filter_NullResult_FailsClosedToEmpty()
    {
        var p = User([], []);
        var accounts = ReadOutputFilter.Filter(ReadOutputKind.Accounts, p, result: null, NullLogger.Instance);
        var groups = ReadOutputFilter.Filter(ReadOutputKind.Groups, p, result: null, NullLogger.Instance);

        Assert.Empty(Assert.IsType<ListAccountsResponse>(accounts).Items);
        Assert.Empty(Assert.IsType<ListGroupsResponse>(groups).Items);
    }

    [Fact]
    public void Filter_UnexpectedType_FailsClosedToEmpty()
    {
        // W22: несподівана форма результату → порожньо, ніколи raw.
        var p = SignalPrincipal.LoopbackFullAccess;
        var result = ReadOutputFilter.Filter(ReadOutputKind.Accounts, p, result: "not-a-response", NullLogger.Instance);

        Assert.Empty(Assert.IsType<ListAccountsResponse>(result).Items);
    }
}
