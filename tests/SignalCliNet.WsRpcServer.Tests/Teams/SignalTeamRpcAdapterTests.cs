using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Services;
using SignalCliNet.WsRpcServer.Tests.Security;
using WsRpcServer.Exceptions;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Teams;

/// <summary>
/// Пінить RPC-межу команд (add-team-notify): member на createTeamInvite → -32001 без інвайту, останній
/// team-admin → -32013, team-admin видає інвайт лише у свою команду, admin listTeams показує команду без адміна.
/// </summary>
public sealed class SignalTeamRpcAdapterTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-team-adapter-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(Start);
    private readonly InviteService _invites;
    private readonly TeamService _teams;
    private readonly AuditLogService _audit;
    private readonly SignalTeamRpcAdapter _adapter;

    public SignalTeamRpcAdapterTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], Start));
        _invites = new InviteService(_store, _time, NullLogger<InviteService>.Instance);
        _teams = new TeamService(_store, _invites, _time, NullLogger<TeamService>.Instance);
        _audit = new AuditLogService(_store, _time, NullLogger<AuditLogService>.Instance);
        _adapter = new SignalTeamRpcAdapter(
            _teams, _store, _audit, Options.Create(new AdminOptions()), _time, NullLogger<SignalTeamRpcAdapter>.Instance);
    }

    private string Join(string teamId, string identityId, string role)
    {
        _store.UpsertIdentity(new IdentityRecord(identityId, "user", [], Start));
        _store.InsertTeamMember(new TeamMemberRecord(identityId, teamId, role, false, Start));
        return identityId;
    }

    private static SignalCallContext.Scope As(string identityId) =>
        SignalCallContext.Enter(new SignalPrincipal(identityId, isAdmin: false, hasFullAccess: false, [], []));

    [Fact]
    public async Task Member_CreateTeamInvite_Unauthorized_NoInviteCreated()
    {
        var team = _teams.CreateTeam("A");
        Join(team.Id, "m-1", TeamRoles.Member);
        using var _ = As("m-1");

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => _adapter.CreateTeamInvite(maxRedemptions: 5));

        Assert.Equal(AccountIsolationGuard.UnauthorizedErrorCode, (int)ex.ErrorCode);
        Assert.DoesNotContain(_store.GetAuditEntries(), e => e.EventType == "team_invite_created");
    }

    [Fact]
    public async Task TeamAdmin_CreateTeamInvite_FiveRedemptions_IntoOwnTeam()
    {
        var team = _teams.CreateTeam("A");
        Join(team.Id, "ta-1", TeamRoles.TeamAdmin);
        using var _ = As("ta-1");

        var response = await _adapter.CreateTeamInvite(maxRedemptions: 5);

        var invite = _store.GetInvite(InviteService.ComputeCodeHash(response.Code))!;
        Assert.Equal((team.Id, TeamRoles.Member, 5), (invite.TeamId, invite.TeamRole, invite.MaxRedemptions));
    }

    [Fact]
    public async Task LastTeamAdmin_SelfRemove_LastTeamAdminError()
    {
        var team = _teams.CreateTeam("A");
        Join(team.Id, "ta-1", TeamRoles.TeamAdmin);
        using var _ = As("ta-1");

        var ex = await Assert.ThrowsAsync<RpcErrorException>(() => _adapter.RemoveTeamMember("ta-1"));

        Assert.Equal(TeamErrorCodes.LastTeamAdmin, (int)ex.ErrorCode);
        Assert.Equal(TeamRoles.TeamAdmin, _store.GetTeamMember("ta-1")!.TeamRole);
        Assert.False(_store.GetTeamMember("ta-1")!.Revoked);
    }

    [Fact]
    public async Task InvalidArguments_AreInvalidParams()
    {
        var team = _teams.CreateTeam("A");
        Join(team.Id, "ta-1", TeamRoles.TeamAdmin);
        using var _ = As("ta-1");

        await Assert.ThrowsAsync<RpcErrorException>(() => _adapter.CreateTeamInvite(maxRedemptions: 0));
        await Assert.ThrowsAsync<RpcErrorException>(() => _adapter.CreateTeamInvite(teamRole: "owner"));
        await Assert.ThrowsAsync<RpcErrorException>(() => _adapter.SetTeamRole("ta-1", "owner"));
    }

    [Fact]
    public async Task AdminListTeams_ShowsTeamWithoutAdmin_AfterRevoke()
    {
        var team = _teams.CreateTeam("A");
        Join(team.Id, "ta-1", TeamRoles.TeamAdmin);
        Join(team.Id, "m-1", TeamRoles.Member);
        _store.RevokeIdentityCredentials("ta-1");

        var admin = new SignalAdminRpcAdapter(
            _invites, _teams, new TokenService(new StaticPepper(), _store, _time, NullLogger<TokenService>.Instance),
            _audit, _store, Options.Create(new AdminOptions()), _time, NullLogger<SignalAdminRpcAdapter>.Instance);
        using var _ = SignalCallContext.Enter(new SignalPrincipal("admin", isAdmin: true, hasFullAccess: false, [], []));

        var listed = Assert.Single((await admin.ListTeams()).Teams);
        Assert.Equal((1, 0), (listed.ActiveMembers, listed.ActiveTeamAdmins));

        // Break-glass: новий team-admin інвайт у команду + break-glass підвищення наявного member.
        var invite = await admin.CreateInvite(teamId: team.Id, teamRole: TeamRoles.TeamAdmin);
        Assert.Equal(team.Id, _store.GetInvite(InviteService.ComputeCodeHash(invite.Code))!.TeamId);
        await admin.AdminSetTeamRole("m-1", TeamRoles.TeamAdmin);
        Assert.Equal(1, (await admin.ListTeams()).Teams[0].ActiveTeamAdmins);
    }

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

    private sealed class StaticPepper : IPepperProvider
    {
        public byte[] GetPepper(int version) => new byte[32];
    }
}
