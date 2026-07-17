using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Groups;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Events;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Security;

/// <summary>
/// DoD секції 4 (add-group-claim-receive) через РЕАЛЬНІ компоненти: DurableStore + GroupClaimService +
/// GroupSendGate + GroupMemberCache (мок ISignalGroups для member-list) + PerAccountReceiveRouter.
/// 4.1 claim→send/без claim→відмова; 4.2 one-time/re-request; 4.5/4.7 router-ізоляція; 4.6 anchor=U churn;
/// 4.8 proxy-anchor churn; 4.9 T3 group-fix + revokeMyBinding. (4.3 scanner-non-block, 4.4 listGroups —
/// юніт-покриття виконавця + store-рівень тут.)
/// </summary>
public sealed class GroupClaimDodTests : IDisposable
{
    private const string Account = "+15550009999";        // shared-bot акаунт (без binding → shared-bot)
    private const string GroupG = "Z3JvdXAtZy1pZA==";      // base64 group-id
    private const string GroupH = "Z3JvdXAtaC1pZA==";
    private const string AciU = "aci-user-u";
    private const string AciP = "aci-proxy-p";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-groupclaim-dod-" + Guid.NewGuid().ToString("N"));
    private readonly DurableStore _store;
    private readonly Mock<ISignalGroups> _groupsMock = new();
    private readonly GroupClaimService _claims;
    private readonly GroupSendGate _gate;
    private readonly GroupMemberCache _memberCache;
    private readonly IOptions<GroupClaimOptions> _options =
        Options.Create(new GroupClaimOptions { Enabled = true });

    public GroupClaimDodTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();

        var time = TimeProvider.System;
        _claims = new GroupClaimService(
            _store, new GroupClaimRateLimiter(_options, time), _options, time,
            NullLogger<GroupClaimService>.Instance);
        _memberCache = new GroupMemberCache(
            _groupsMock.Object, _options, time, NullLogger<GroupMemberCache>.Instance);
        _gate = new GroupSendGate(_store, _memberCache, _options, TimeProvider.System, NullLogger<GroupSendGate>.Instance);

        // Дефолт: member-list групи G містить обидва ACI (U і P — член).
        SetMembers(GroupG, AciU, AciP);
        SetMembers(GroupH, AciU);
    }

    // Готує identity + активний binding через реальний claim-флоу (request→confirm), anchor задається.
    private void ClaimGroup(string identityId, string groupId, string anchorAci)
    {
        _store.UpsertIdentity(new IdentityRecord(identityId, "user", [], DateTimeOffset.UtcNow));
        var result = _claims.RequestClaim(identityId);
        Assert.Equal(GroupClaimStatus.Created, result.Status);
        // Сканер побачив би код у групі й викликав Confirm із anchor-ACI вставника.
        Assert.True(_claims.Confirm(GroupClaimService.ComputeCodeHash(Normalize(result.Code!)), groupId, anchorAci));
    }

    // ---------------------------------------------------------------- 4.1

    [Fact]
    public async Task ClaimedIdentity_SendsToGroup_Unclaimed_Denied()
    {
        ClaimGroup("U", GroupG, AciU);

        var allowed = await _gate.EvaluateAsync("U", Account, GroupG);
        Assert.True(allowed.Allowed);

        // B без claim на G → NoBinding.
        _store.UpsertIdentity(new IdentityRecord("B", "user", [], DateTimeOffset.UtcNow));
        var denied = await _gate.EvaluateAsync("B", Account, GroupG);
        Assert.False(denied.Allowed);
        Assert.Equal(GroupSendDenyReason.NoBinding, denied.Reason);
    }

    // ---------------------------------------------------------------- 4.2

    [Fact]
    public void Code_IsOneTime_ReRequestWorksAfterBurn()
    {
        _store.UpsertIdentity(new IdentityRecord("U", "user", [], DateTimeOffset.UtcNow));
        var first = _claims.RequestClaim("U");
        var hash = GroupClaimService.ComputeCodeHash(Normalize(first.Code!));

        Assert.True(_claims.Confirm(hash, GroupG, AciU));      // перше гашення виграє
        Assert.False(_claims.Confirm(hash, GroupH, AciU));     // повторний/перехоплений код → відмова (one-time)

        // Griefing-burn стався → U перевипускає й отримує РОБОЧИЙ новий код.
        var second = _claims.RequestClaim("U");
        Assert.NotEqual(first.Code, second.Code);
        Assert.True(_claims.Confirm(GroupClaimService.ComputeCodeHash(Normalize(second.Code!)), GroupG, AciU));
    }

    // ---------------------------------------------------------------- 4.6 (anchor = U churn)

    [Fact]
    public async Task AnchorSelf_KickedFromGroup_SendRejected_BindingInvalidated()
    {
        ClaimGroup("U", GroupG, AciU);
        Assert.True((await _gate.EvaluateAsync("U", Account, GroupG)).Allowed);

        // U (anchor) вигнаний із G — member-list більше не містить AciU.
        SetMembers(GroupG, AciP);

        var decision = await _gate.EvaluateAsync("U", Account, GroupG);
        Assert.False(decision.Allowed);
        Assert.Equal(GroupSendDenyReason.AnchorNotMember, decision.Reason);

        // Binding інвалідований (revoked) — навіть якщо member-list знову оновиться, право не воскресає.
        var binding = _store.GetGroupBinding("U", GroupG);
        Assert.True(binding is null || binding.Revoked);
    }

    // ---------------------------------------------------------------- 4.8 (proxy-anchor churn)

    [Fact]
    public async Task ProxyAnchor_LeavesGroup_SendOfURejected_BindingInvalidated()
    {
        // Код U вставив поручитель P → binding U, anchor=P (право живе з поручителем, T2).
        ClaimGroup("U", GroupG, AciP);
        Assert.True((await _gate.EvaluateAsync("U", Account, GroupG)).Allowed);

        // P (anchor) вийшов із G.
        SetMembers(GroupG, AciU);   // U сам у групі, але anchor=P — його немає

        var decision = await _gate.EvaluateAsync("U", Account, GroupG);
        Assert.False(decision.Allowed);
        Assert.Equal(GroupSendDenyReason.AnchorNotMember, decision.Reason);
    }

    // ---------------------------------------------------------------- 4.9 (T3 group-fix + revokeMyBinding)

    [Fact]
    public async Task GroupFixedByCodeLocation_SecondGroupDenied_RevokeMyBindingRemoves()
    {
        _store.UpsertIdentity(new IdentityRecord("U", "user", [], DateTimeOffset.UtcNow));
        var claim = _claims.RequestClaim("U");
        var hash = GroupClaimService.ComputeCodeHash(Normalize(claim.Code!));

        // Consume-and-bind фіксує ГРУПУ за місцем появи коду (G).
        Assert.True(_claims.Confirm(hash, GroupG, AciU));
        // Той самий код у другій групі після consume → відмова (T3, one-time).
        Assert.False(_claims.Confirm(hash, GroupH, AciU));

        Assert.True((await _gate.EvaluateAsync("U", Account, GroupG)).Allowed);

        // revokeMyBinding прибирає небажаний binding → send більше не проходить.
        var binding = _store.GetGroupBinding("U", GroupG);
        Assert.NotNull(binding);
        _store.RevokeGroupBinding(binding!.BindingId);

        var afterRevoke = await _gate.EvaluateAsync("U", Account, GroupG);
        Assert.False(afterRevoke.Allowed);
        Assert.Equal(GroupSendDenyReason.NoBinding, afterRevoke.Reason);
    }

    // ---------------------------------------------------------------- 4.5 / 4.7 (router-ізоляція)

    [Fact]
    public void Router_SharedBot_ToScannerOnly_OwnLinked_ToOwner()
    {
        var router = new PerAccountReceiveRouter(_store, _options);

        // Own-linked персональний номер (IsPrivate=true) → доставка власнику.
        _store.UpsertIdentity(new IdentityRecord("alice", "user", ["+15551110001"], DateTimeOffset.UtcNow));
        _store.UpsertAccountBinding(new AccountBinding("+15551110001", "alice", IsPrivate: true, DateTimeOffset.UtcNow));
        var own = router.Route("+15551110001");
        Assert.Equal(ReceiveScope.OwnLinked, own.Scope);
        Assert.Equal("alice", own.OwningIdentityId);

        // Shared-bot акаунт (без binding) → ЛИШЕ сканеру, нікому з клієнтів (R3.5/W25-inbound).
        var shared = router.Route(Account);
        Assert.Equal(ReceiveScope.SharedBot, shared.Scope);
        Assert.Null(shared.OwningIdentityId);

        // Admin-owned комунальний (IsPrivate=false) → теж shared-bot (сканер), не власнику.
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], DateTimeOffset.UtcNow));
        _store.UpsertAccountBinding(new AccountBinding("+15550002222", "admin", IsPrivate: false, DateTimeOffset.UtcNow));
        Assert.Equal(ReceiveScope.SharedBot, router.Route("+15550002222").Scope);
    }

    [Fact]
    public void Router_Disabled_IsPassthrough()
    {
        var disabled = new PerAccountReceiveRouter(_store, Options.Create(new GroupClaimOptions { Enabled = false }));
        _store.UpsertIdentity(new IdentityRecord("alice", "user", [], DateTimeOffset.UtcNow));
        _store.UpsertAccountBinding(new AccountBinding("+15551110001", "alice", IsPrivate: true, DateTimeOffset.UtcNow));

        // Вимкнено → усе passthrough (поточна поведінка, без сканера/звуження).
        Assert.Equal(ReceiveScope.Passthrough, disabled.Route("+15551110001").Scope);
        Assert.Equal(ReceiveScope.Passthrough, disabled.Route(Account).Scope);
    }

    // ---------------------------------------------------------------- 4.4 (store-рівень: binding→видимість)

    [Fact]
    public void ClaimedGroup_AppearsInIdentityBindings_ForListGroupsVisibility()
    {
        ClaimGroup("U", GroupG, AciU);

        // Активний binding U несе G → SignalRpcSession.ActiveGroupBindingIds віддасть G у allowedGroupIds,
        // тож listGroups-фільтр (R3.2) покаже саме claim'нуту групу (і лише її).
        var bindings = _store.GetGroupBindingsForIdentity("U");
        Assert.Contains(bindings, b => b.GroupId == GroupG && !b.Revoked);
    }

    // ================================================================ helpers

    private void SetMembers(string groupId, params string[] memberAcis)
    {
        var members = memberAcis.Select(aci => new Member(Number: null, Uuid: aci)).ToList();
        var group = new Group(
            groupId, "G", null, IsMember: true, IsBlocked: false, MessageExpirationTime: 0,
            Members: members, PendingMembers: [], RequestingMembers: [], Admins: [], Banned: [],
            PermissionAddMember: "EVERY_MEMBER", PermissionEditDetails: "EVERY_MEMBER",
            PermissionSendMessage: "EVERY_MEMBER", GroupInviteLink: null);
        // Мок повертає групу(и) для будь-якого account; кеш зчитує member-list за groupId.
        _groupsMock.Setup(g => g.ListGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ListGroupsResponse([BuildGroup(GroupG), BuildGroup(GroupH)]));
        _groupMembers[groupId] = members;
        // Зміна членства у проді супроводжується group-update-подією, що інвалідує кеш member-list.
        _memberCache?.Invalidate(groupId);
    }

    private readonly Dictionary<string, List<Member>> _groupMembers = new(StringComparer.Ordinal);

    private Group BuildGroup(string groupId) => new(
        groupId, "G", null, true, false, 0,
        _groupMembers.TryGetValue(groupId, out var m) ? m : [],
        [], [], [], [], "EVERY_MEMBER", "EVERY_MEMBER", "EVERY_MEMBER", null);

    private static string Normalize(string code) => GroupClaimService.LooksLikeCode(code, out var n) ? n : code;

    public void Dispose()
    {
        _store.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (Directory.Exists(_dataDir))
                Directory.Delete(_dataDir, recursive: true);
        }
        catch (IOException)
        {
            // best-effort прибирання темп-каталогу
        }
    }
}
