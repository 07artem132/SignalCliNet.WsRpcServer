using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// Пінить GroupSendGate (add-group-claim-receive, tasks 2.3/2.5): є binding + anchor-член → Allow; немає
/// binding → Deny(NoBinding); anchor не член → Deny(AnchorNotMember) + інвалідизація binding; класифікація
/// shared-bot vs own-linked акаунта.
/// </summary>
public sealed class GroupSendGateTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 7, 17, 12, 0, 0, TimeSpan.Zero);
    private const string SharedBotAccount = "+15550009999";
    private const string GroupId = "grp-1";
    private const string Caller = "id-u";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-sendgate-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time;
    private readonly Mock<ISignalGroups> _groups = new();

    public GroupSendGateTests()
    {
        Directory.CreateDirectory(_dataDir);
        _time = new TestTimeProvider(Start);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord(Caller, "user", [], Start));
        // Shared-bot акаунт: admin-owned, IsPrivate=false.
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], Start));
        _store.UpsertAccountBinding(new AccountBinding(SharedBotAccount, "admin", IsPrivate: false, Start));
    }

    private GroupSendGate NewGate()
    {
        var opts = Options.Create(new GroupClaimOptions { Enabled = true, MemberCacheTtlSeconds = 60 });
        var cache = new GroupMemberCache(_groups.Object, opts, _time, NullLogger<GroupMemberCache>.Instance);
        return new GroupSendGate(_store, cache, opts, _time, NullLogger<GroupSendGate>.Instance);
    }

    private void SetGroupMembers(params string[] acis) =>
        _groups.Setup(g => g.ListGroupsAsync(SharedBotAccount, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ListGroupsResponse([
                new Group(GroupId, "G", null, true, false, 0,
                    acis.Select(a => new Member(null, a)).ToList(), [], [], [], [],
                    "EVERY_MEMBER", "EVERY_MEMBER", "EVERY_MEMBER", null)]));

    private void InsertBinding(string bindingId, string anchor, bool revoked = false, DateTimeOffset? expiresAt = null) =>
        _store.InsertGroupBinding(new GroupBindingRecord(
            bindingId, Caller, GroupId, anchor, expiresAt ?? Start.AddHours(720), revoked, Start));

    [Fact]
    public void IsSharedBotAccount_Classification()
    {
        var gate = NewGate();
        _store.UpsertIdentity(new IdentityRecord("owner", "user", [], Start));
        _store.UpsertAccountBinding(new AccountBinding("+15551112222", "owner", IsPrivate: true, Start));

        Assert.True(gate.IsSharedBotAccount(SharedBotAccount));   // admin-owned non-private
        Assert.True(gate.IsSharedBotAccount("+15550000000"));     // без binding → shared-bot
        Assert.False(gate.IsSharedBotAccount("+15551112222"));    // own-linked private
    }

    [Fact]
    public async Task Allowed_WhenBindingAndAnchorMember()
    {
        var gate = NewGate();
        InsertBinding("b-1", "aci-anchor");
        SetGroupMembers("aci-anchor", "aci-other");

        var decision = await gate.EvaluateAsync(Caller, SharedBotAccount, GroupId);

        Assert.True(decision.Allowed);
    }

    [Fact]
    public async Task Denied_WhenNoBinding()
    {
        var gate = NewGate();
        SetGroupMembers("aci-anchor");

        var decision = await gate.EvaluateAsync(Caller, SharedBotAccount, GroupId);

        Assert.False(decision.Allowed);
        Assert.Equal(GroupSendDenyReason.NoBinding, decision.Reason);
    }

    [Fact]
    public async Task Denied_AndRevoked_WhenAnchorNotMember()
    {
        var gate = NewGate();
        InsertBinding("b-1", "aci-anchor");
        SetGroupMembers("aci-someone-else"); // anchor вигнаний

        var decision = await gate.EvaluateAsync(Caller, SharedBotAccount, GroupId);

        Assert.False(decision.Allowed);
        Assert.Equal(GroupSendDenyReason.AnchorNotMember, decision.Reason);
        Assert.True(_store.GetGroupBinding(Caller, GroupId)!.Revoked); // інвалідовано
    }

    [Fact]
    public async Task Denied_WhenBindingExpired()
    {
        var gate = NewGate();
        InsertBinding("b-1", "aci-anchor", expiresAt: Start.AddMinutes(-1)); // прострочений backstop-TTL
        SetGroupMembers("aci-anchor");

        var decision = await gate.EvaluateAsync(Caller, SharedBotAccount, GroupId);

        Assert.False(decision.Allowed);
        Assert.Equal(GroupSendDenyReason.NoBinding, decision.Reason);
    }

    public void Dispose()
    {
        _store.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort
        }
    }
}
