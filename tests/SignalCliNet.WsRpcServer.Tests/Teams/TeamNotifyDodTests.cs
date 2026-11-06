using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Nerdbank.Streams;
using SignalCli.Interfaces.Signal;
using SignalCli.Models.Signal.Groups;
using SignalCli.Models.Signal.Message;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Security.Admission;
using SignalCliNet.WsRpcServer.Security.Fail;
using SignalCliNet.WsRpcServer.Security.Idempotency;
using SignalCliNet.WsRpcServer.Serialization;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using StreamJsonRpc;
using StreamJsonRpc.Protocol;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Teams;

/// <summary>
/// DoD add-team-notify на реальному шляху StreamJsonRpc: клієнт ↔ <see cref="AuthorizingSignalJsonRpc"/> ↔
/// справжній <see cref="SignalMessageRpcAdapter"/> (signal-cli замокано). Пінить: alias лише своєї команди
/// (-32007 anti-oracle), інший пристрій тієї ж команди шле через той самий binding, dedup за чатом
/// (дві вкладки, два alias на один чат) і coalescing у chokepoint'і.
/// </summary>
public sealed class TeamNotifyDodTests : IDisposable
{
    private const string Bot = "+15550009999";              // shared-bot (без account-binding → shared)
    private const string GroupG = "Z3JvdXAtZy1pZA==";
    private const string GroupH = "Z3JvdXAtaC1pZA==";
    private const string AciAnchor = "aci-anchor";

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-team-notify-dod-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly Mock<ISignalGroups> _groups = new();
    private readonly Mock<ISignalMessage> _signal = new();
    private readonly List<TextMessageOptions> _sent = [];
    private readonly GroupClaimService _claims;
    private readonly GroupSendGate _gate;
    private readonly TeamService _teams;
    private readonly List<IDisposable> _cleanup = [];

    public TeamNotifyDodTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();

        var options = Options.Create(new GroupClaimOptions { Enabled = true });
        _claims = new GroupClaimService(
            _store, new GroupClaimRateLimiter(options, _time), options, _time, NullLogger<GroupClaimService>.Instance);
        var cache = new GroupMemberCache(_groups.Object, options, _time, NullLogger<GroupMemberCache>.Instance);
        _gate = new GroupSendGate(_store, cache, options, _time, NullLogger<GroupSendGate>.Instance);
        _teams = new TeamService(
            _store, new InviteService(_store, _time, NullLogger<InviteService>.Instance), _time,
            NullLogger<TeamService>.Instance);

        _groups.Setup(g => g.ListGroupsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ListGroupsResponse([Group(GroupG), Group(GroupH)]));
        _signal.Setup(s => s.SendTextMessageAsync(It.IsAny<TextMessageOptions>(), It.IsAny<CancellationToken>()))
            .Callback<TextMessageOptions, CancellationToken>((o, _) =>
            {
                lock (_sent)
                    _sent.Add(o);
            })
            .ReturnsAsync(() => new SendMessageResponse([], 42));
    }

    // ---------------------------------------------------------------- alias лише своєї команди

    [Fact]
    public async Task OtherTeamsAlias_And_UnknownAlias_GetSameDenial()
    {
        var teamA = Team("A", "ta-a");
        var teamB = Team("B", "ta-b");
        Bind("ta-b", "dir:1", GroupG);
        _ = teamA;
        _ = teamB;

        var client = Pair("ta-a");
        var foreign = await Assert.ThrowsAsync<RemoteInvocationException>(() => Send(client, "alias:dir:1", "x"));
        var unknown = await Assert.ThrowsAsync<RemoteInvocationException>(() => Send(client, "alias:dir:2", "x"));

        Assert.Equal(GroupClaimErrorCodes.SendDenied, foreign.ErrorCode);
        Assert.Equal(GroupClaimErrorCodes.SendDenied, unknown.ErrorCode);
        Assert.Equal(foreign.Message, unknown.Message);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task OtherDeviceOfSameTeam_SendsThroughTeamBinding()
    {
        var team = Team("A", "ta-x");
        Member(team, "m-y");
        Bind("ta-x", "dir:1", GroupG);

        await Send(Pair("m-y"), "alias:dir:1", "alarm");

        var options = Assert.Single(_sent);
        var recipient = Assert.Single(options.Recipients);
        Assert.True(recipient.IsGroup);
        Assert.Equal(GroupG, recipient.Identifier);
    }

    [Fact]
    public async Task MalformedAlias_RejectedBeforeDispatch()
    {
        Team("A", "ta-x");
        Bind("ta-x", "dir:1", GroupG);
        var client = Pair("ta-x");

        // Поряд із валідним alias (gated send) малформований → -32602 (формат), не -32007.
        var mixed = await Assert.ThrowsAsync<RemoteMethodNotFoundException>(() => client.InvokeWithParameterObjectAsync<SendMessageResponse>(
            "sendTextMessage", new { account = Bot, recipients = new[] { "alias:dir:1", "alias:Bad Alias!" }, message = "x" },
            TestContext.Current.CancellationToken));
        Assert.Equal(JsonRpcErrorCode.InvalidParams, mixed.ErrorCode);

        // Сам по собі — не gated send: shared-bot акаунт не належить caller-у → -32001.
        var alone = await Assert.ThrowsAsync<RemoteInvocationException>(() => Send(client, "alias:Bad Alias!", "x"));
        Assert.Equal(-32001, alone.ErrorCode);
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task AnchorLeftGroup_TeamBindingRevoked_SendDenied()
    {
        Team("A", "ta-x");
        Bind("ta-x", "dir:1", GroupG);
        _members[GroupG] = ["aci-someone-else"];

        var ex = await Assert.ThrowsAsync<RemoteInvocationException>(() => Send(Pair("ta-x"), "alias:dir:1", "x"));

        Assert.Equal(GroupClaimErrorCodes.SendDenied, ex.ErrorCode);
        Assert.True(_store.GetTeamBinding(_store.GetTeamMember("ta-x")!.TeamId, "dir:1")!.Revoked);
    }

    // ---------------------------------------------------------------- dedup за чатом

    [Fact]
    public async Task TwoTabsOfSameTeam_SameMessageId_OneMessage_SameResult()
    {
        var team = Team("A", "ta-x");
        Member(team, "m-y");
        Bind("ta-x", "dir:1", GroupG);

        var first = await Send(Pair("ta-x"), "alias:dir:1", "alarm", "msg-1");
        var second = await Send(Pair("m-y"), "alias:dir:1", "alarm", "msg-1");

        Assert.Single(_sent);
        Assert.Equal(first.TimeStamp, second.TimeStamp);
    }

    [Fact]
    public async Task TwoAliasesOnSameChat_SameMessageId_OneMessage()
    {
        Team("A", "ta-x");
        Bind("ta-x", "all", GroupG);
        Bind("ta-x", "dir:1", GroupG);

        var client = Pair("ta-x");
        await Send(client, "alias:all", "alarm", "msg-1");
        await Send(client, "alias:dir:1", "alarm", "msg-1");

        Assert.Single(_sent);
    }

    [Fact]
    public async Task SameMessageId_DifferentChats_BothSent()
    {
        Team("A", "ta-x");
        Bind("ta-x", "dir:1", GroupG);
        Bind("ta-x", "dir:2", GroupH);

        var client = Pair("ta-x");
        await Send(client, "alias:dir:1", "alarm", "msg-1");
        await Send(client, "alias:dir:2", "alarm", "msg-1");

        Assert.Equal(2, _sent.Count);
    }

    // ---------------------------------------------------------------- coalescing у chokepoint'і

    [Fact]
    public async Task Coalescing_SecondMessageQueued_FlushedAtWindowEnd()
    {
        Team("A", "ta-x");
        Bind("ta-x", "dir:1", GroupG);
        var coalescer = new ChatCoalescer(
            _signal.Object, Options.Create(new NotifyOptions { CoalesceWindowSeconds = 300 }), _time,
            NullLogger<ChatCoalescer>.Instance);

        var client = Pair("ta-x", coalescer);
        var first = await Send(client, "alias:dir:1", "one", "m-1");
        var queued = await Send(client, "alias:dir:1", "two", "m-2");
        var queued2 = await Send(client, "alias:dir:1", "three", "m-3");

        Assert.Equal(42, first.TimeStamp);
        Assert.Equal(0, queued.TimeStamp);           // «поставлено в чергу»
        Assert.Empty(queued.Results!);
        Assert.Equal(0, queued2.TimeStamp);
        Assert.Single(_sent);

        // Повтор того самого messageId у черзі не дублює: dedup повертає збережений «queued».
        var retry = await Send(client, "alias:dir:1", "two", "m-2");
        Assert.Equal(0, retry.TimeStamp);

        _time.Advance(TimeSpan.FromMinutes(5));
        await coalescer.FlushDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, _sent.Count);
        Assert.Equal("two" + ChatCoalescer.Separator + "three", _sent[1].Message);
    }

    // ================================================================ helpers

    private readonly Dictionary<string, List<string>> _members = new(StringComparer.Ordinal)
    {
        [GroupG] = [AciAnchor],
        [GroupH] = [AciAnchor],
    };

    private Group Group(string groupId) => new(
        groupId, "G", null, true, false, 0,
        _members[groupId].Select(a => new Member(null, a)).ToList(),
        [], [], [], [], "EVERY_MEMBER", "EVERY_MEMBER", "EVERY_MEMBER", null);

    private string Team(string name, string teamAdmin)
    {
        var team = _teams.CreateTeam(name);
        _store.UpsertIdentity(new IdentityRecord(teamAdmin, "user", [], _time.GetUtcNow()));
        _store.InsertTeamMember(new TeamMemberRecord(teamAdmin, team.Id, TeamRoles.TeamAdmin, false, _time.GetUtcNow()));
        return team.Id;
    }

    private void Member(string teamId, string identityId)
    {
        _store.UpsertIdentity(new IdentityRecord(identityId, "user", [], _time.GetUtcNow()));
        _store.InsertTeamMember(new TeamMemberRecord(identityId, teamId, TeamRoles.Member, false, _time.GetUtcNow()));
    }

    // Командний binding через реальний claim-флоу: team-admin просить код з alias → сканер підтверджує.
    private void Bind(string teamAdmin, string alias, string groupId)
    {
        var claim = _claims.RequestClaim(teamAdmin, alias);
        Assert.Equal(GroupClaimStatus.Created, claim.Status);
        Assert.True(GroupClaimService.LooksLikeCode(claim.Code, out var normalized));
        Assert.True(_claims.Confirm(GroupClaimService.ComputeCodeHash(normalized), groupId, AciAnchor));
    }

    private static Task<SendMessageResponse> Send(JsonRpc client, string recipient, string text, string? messageId = null) =>
        client.InvokeWithParameterObjectAsync<SendMessageResponse>(
            "sendTextMessage", new { account = Bot, recipients = new[] { recipient }, message = text, messageId });

    private JsonRpc Pair(string identityId, ChatCoalescer? coalescer = null)
    {
        var principal = new SignalPrincipal(identityId, isAdmin: false, hasFullAccess: false, [], []);
        var (clientStream, serverStream) = FullDuplexStream.CreatePair();

        // Dedup без admission: той самий ключ, що й IdempotentSendCoordinator (scope ?? identity).
        var server = new AuthorizingSignalJsonRpc(
            new HeaderDelimitedMessageHandler(serverStream, serverStream, Formatter()),
            principal,
            new SignalRpcPolicyRegistry(),
            groupSendGate: _gate,
            beginSend: (messageId, _, scope) => BeginSend(scope ?? identityId, messageId),
            completeSend: (messageId, json, scope) => _store.CompleteSend(scope ?? identityId, messageId, json),
            chatCoalescer: coalescer);
        var adapter = new SignalMessageRpcAdapter(
            _signal.Object,
            new UpstreamSendFailureMapper(
                new NoopGlobalPauseGate(), Options.Create(new GlobalPauseOptions()),
                NullLogger<UpstreamSendFailureMapper>.Instance),
            NullLogger<SignalMessageRpcAdapter>.Instance);
        server.AddLocalRpcTarget(adapter, new JsonRpcTargetOptions { MethodNameTransform = CommonMethodNameTransforms.CamelCase });
        server.StartListening();

        var client = new JsonRpc(new HeaderDelimitedMessageHandler(clientStream, clientStream, Formatter()));
        client.StartListening();
        _cleanup.Add(server);
        _cleanup.Add(client);
        return client;
    }

    private IdempotentSendOutcome BeginSend(string key, string? messageId)
    {
        if (string.IsNullOrEmpty(messageId))
            return IdempotentSendOutcome.AdmittedFresh;

        var existing = _store.GetSendDedup(key, messageId);
        if (existing is not null)
        {
            return existing.Status == SendDedupRecord.StatusDone
                ? IdempotentSendOutcome.FromDone(existing.ResultJson)
                : IdempotentSendOutcome.OutcomeUnknown;
        }

        return _store.TryBeginSend(key, messageId, _time.GetUtcNow())
            ? IdempotentSendOutcome.AdmittedPending
            : IdempotentSendOutcome.OutcomeUnknown;
    }

    private static SystemTextJsonFormatter Formatter()
    {
        var formatter = new SystemTextJsonFormatter();
        formatter.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        formatter.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        formatter.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        formatter.JsonSerializerOptions.TypeInfoResolver = JsonTypeInfoResolver.Combine(
            SignalCliSerializerContext.Default, new DefaultJsonTypeInfoResolver());
        return formatter;
    }

    public void Dispose()
    {
        foreach (var d in _cleanup)
            d.Dispose();
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
