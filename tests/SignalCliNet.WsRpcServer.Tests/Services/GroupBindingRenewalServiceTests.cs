using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Accounts;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Services;

/// <summary>
/// Пінить автоподовження прив'язок (add-binding-auto-renew): у вікні + anchor член → TTL поновлено; поза вікном,
/// anchor вийшов, транзієнт, revoked, перепривʼязка між читанням і записом — НЕ поновлено; вимкнено → inert.
/// </summary>
public sealed class GroupBindingRenewalServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private const string Bot = "+15550009999";
    private const string PrivateAccount = "+15551112222";
    private const string GroupId = "grp-1";
    private const string Caller = "id-u";
    private const string Anchor = "aci-anchor";
    private const string TeamId = "team-1";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-renewal-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(Start);
    private readonly Mock<ISignalGroups> _groups = new();
    private readonly Mock<ISignalAccounts> _accounts = new();

    public GroupBindingRenewalServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord(Caller, "user", [], Start));
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], Start));
        _store.UpsertIdentity(new IdentityRecord("owner", "user", [], Start));
        _store.UpsertAccountBinding(new AccountBinding(Bot, "admin", IsPrivate: false, Start));
        _store.UpsertAccountBinding(new AccountBinding(PrivateAccount, "owner", IsPrivate: true, Start));
        _store.InsertTeam(new TeamRecord(TeamId, "Team", Start));
        _accounts.Setup(a => a.ListAccountsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListAccountsResponse([new Account(Bot), new Account(PrivateAccount)]));
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private GroupBindingRenewalService NewService(GroupClaimOptions? options = null)
    {
        var opts = Options.Create(options ?? new GroupClaimOptions { Enabled = true });
        var cache = new GroupMemberCache(_groups.Object, opts, _time, NullLogger<GroupMemberCache>.Instance);
        var gate = new GroupSendGate(_store, cache, opts, _time, NullLogger<GroupSendGate>.Instance);
        return new GroupBindingRenewalService(
            _store, _accounts.Object, cache, gate, opts, _time, NullLogger<GroupBindingRenewalService>.Instance);
    }

    private void SetMembers(string account, params string[] acis) =>
        _groups.Setup(g => g.ListGroupsAsync(account, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListGroupsResponse([
                new Group(GroupId, "G", null, true, false, 0,
                    acis.Select(a => new Member(null, a)).ToList(), [], [], [], [],
                    "EVERY_MEMBER", "EVERY_MEMBER", "EVERY_MEMBER", null)]));

    private void InsertBinding(string id, TimeSpan expiresIn, bool revoked = false, DateTimeOffset? createdAt = null) =>
        _store.InsertGroupBinding(new GroupBindingRecord(
            id, Caller, GroupId, Anchor, Start + expiresIn, revoked, createdAt ?? Start));

    [Fact]
    public async Task Renews_BindingInsideWindow_WhenAnchorIsMember()
    {
        InsertBinding("b-1", TimeSpan.FromDays(3));
        SetMembers(Bot, Anchor);

        var result = await NewService().RenewOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BindingRenewalResult(1, 0, 0), result);
        Assert.Equal(Start.AddHours(720), _store.GetGroupBinding(Caller, GroupId)!.ExpiresAt);
    }

    [Fact]
    public async Task Skips_BindingOutsideWindow()
    {
        InsertBinding("b-1", TimeSpan.FromDays(20));
        SetMembers(Bot, Anchor);

        var result = await NewService().RenewOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(default, result);
        Assert.Equal(Start.AddDays(20), _store.GetGroupBinding(Caller, GroupId)!.ExpiresAt);
        _accounts.Verify(a => a.ListAccountsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task DoesNotRenew_NorRevoke_WhenAnchorLeftGroup()
    {
        InsertBinding("b-1", TimeSpan.FromDays(3));
        SetMembers(Bot, "aci-someone-else");

        var result = await NewService().RenewOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BindingRenewalResult(0, 1, 0), result);
        var binding = _store.GetGroupBinding(Caller, GroupId)!;
        Assert.Equal(Start.AddDays(3), binding.ExpiresAt);
        Assert.False(binding.Revoked);
    }

    [Fact]
    public async Task Defers_WhenMembershipUnknown()
    {
        InsertBinding("b-1", TimeSpan.FromDays(3));
        _groups.Setup(g => g.ListGroupsAsync(Bot, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("signal-cli"));

        var result = await NewService().RenewOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BindingRenewalResult(0, 0, 1), result);
        Assert.Equal(Start.AddDays(3), _store.GetGroupBinding(Caller, GroupId)!.ExpiresAt);
    }

    [Fact]
    public async Task OnlyChecksSharedBotAccounts()
    {
        InsertBinding("b-1", TimeSpan.FromDays(3));
        SetMembers(Bot, Anchor);

        await NewService().RenewOnceAsync(TestContext.Current.CancellationToken);

        _groups.Verify(g => g.ListGroupsAsync(PrivateAccount, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Ignores_RevokedExpiredAndSupersededRows()
    {
        InsertBinding("revoked", TimeSpan.FromDays(3), revoked: true, createdAt: Start.AddMinutes(-2));
        InsertBinding("old", TimeSpan.FromDays(2), createdAt: Start.AddMinutes(-1));
        InsertBinding("expired", TimeSpan.FromMinutes(-1), createdAt: Start.AddMinutes(-3));
        SetMembers(Bot, Anchor);

        Assert.Single(_store.GetGroupBindingsExpiringBefore(Start, Start.AddDays(7)), b => b.BindingId == "old");

        // Новіший re-claim робить "old" неактуальним: send-gate читає лише найновіший рядок.
        InsertBinding("newest", TimeSpan.FromDays(25), createdAt: Start);
        var result = await NewService().RenewOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(default, result);
    }

    [Fact]
    public void TryExtend_RefusesRevokedOrEarlierExpiry()
    {
        InsertBinding("b-1", TimeSpan.FromDays(3));

        Assert.False(_store.TryExtendGroupBinding("b-1", Start, Start.AddDays(2))); // не скорочуємо
        _store.RevokeGroupBinding("b-1");
        Assert.False(_store.TryExtendGroupBinding("b-1", Start, Start.AddDays(30))); // revoke виграє
        Assert.True(_store.GetGroupBinding(Caller, GroupId)!.Revoked);
    }

    [Fact]
    public async Task Renews_TeamBinding_AndRefusesStaleRebind()
    {
        _store.UpsertTeamBinding(new TeamBindingRecord(
            TeamId, "all", GroupId, Anchor, "admin", Start.AddDays(3), Revoked: false, Start));
        SetMembers(Bot, Anchor);

        var result = await NewService().RenewOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new BindingRenewalResult(1, 0, 0), result);
        Assert.Equal(Start.AddHours(720), _store.GetTeamBinding(TeamId, "all")!.ExpiresAt);

        // Перепривʼязка до іншої групи між читанням і записом: застаріле поновлення не застосовується.
        _store.UpsertTeamBinding(new TeamBindingRecord(
            TeamId, "all", "grp-2", "aci-2", "admin", Start.AddDays(1), Revoked: false, Start));
        Assert.False(_store.TryExtendTeamBinding(TeamId, "all", GroupId, Anchor, Start, Start.AddDays(30)));
        Assert.Equal(Start.AddDays(1), _store.GetTeamBinding(TeamId, "all")!.ExpiresAt);
    }

    [Fact]
    public async Task RenewedBinding_IsNotRenewedAgainUntilItReentersWindow()
    {
        InsertBinding("b-1", TimeSpan.FromDays(3));
        SetMembers(Bot, Anchor);
        var service = NewService();

        await service.RenewOnceAsync(TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromHours(1));
        var second = await service.RenewOnceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(default, second);
    }

    [Fact]
    public void Window_IsCappedAtHalfTtl_AndIntervalAtOneHour()
    {
        var service = NewService(new GroupClaimOptions { Enabled = true, BindingTtlHours = 48, BindingRenewWindowHours = 168 });
        Assert.Equal(TimeSpan.FromHours(24), service.RenewWindow);
        Assert.Equal(TimeSpan.FromHours(1), service.PassInterval);

        var shortTtl = NewService(new GroupClaimOptions { Enabled = true, BindingTtlHours = 1, BindingRenewWindowHours = 1 });
        Assert.Equal(TimeSpan.FromMinutes(30), shortTtl.RenewWindow);
        Assert.Equal(TimeSpan.FromMinutes(15), shortTtl.PassInterval);
    }

    [Fact]
    public void Inactive_WhenGroupClaimOrAutoRenewDisabled()
    {
        Assert.True(NewService().Active);
        Assert.False(NewService(new GroupClaimOptions { Enabled = false }).Active);
        Assert.False(NewService(new GroupClaimOptions { Enabled = true, AutoRenewBindings = false }).Active);
    }
}
