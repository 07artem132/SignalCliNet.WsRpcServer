using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using SignalCliNet.WsRpcServer.Tests.Security;
using Xunit;

namespace SignalCliNet.WsRpcServer.Tests.Teams;

/// <summary>
/// Пінить командну модель (add-team-notify): team-admin видає інвайти лише у свою команду, multi-use інвайт
/// погашається не більше N разів (і конкурентно), команда не лишається без team-admin (-32013), break-glass
/// адміна шлюзу після компрометації останнього team-admin.
/// </summary>
public sealed class TeamServiceTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(1);

    private readonly string _dataDir =
        Path.Combine(Path.GetTempPath(), "wsrpc-team-tests-" + Guid.NewGuid().ToString("N"));

    private readonly DurableStore _store;
    private readonly TestTimeProvider _time = new(Start);
    private readonly InviteService _invites;
    private readonly TeamService _teams;

    public TeamServiceTests()
    {
        Directory.CreateDirectory(_dataDir);
        _store = new DurableStore(Path.Combine(_dataDir, "durable.db"), NullLogger<DurableStore>.Instance);
        _store.Initialize();
        _store.UpsertIdentity(new IdentityRecord("admin", "admin", [], Start));
        _invites = new InviteService(_store, _time, NullLogger<InviteService>.Instance);
        _teams = new TeamService(_store, _invites, _time, NullLogger<TeamService>.Instance);
    }

    private string Join(string teamId, string identityId, string role)
    {
        _store.UpsertIdentity(new IdentityRecord(identityId, "user", [], Start));
        _store.InsertTeamMember(new TeamMemberRecord(identityId, teamId, role, false, Start));
        return identityId;
    }

    private int ActiveAdmins(string teamId) =>
        _store.GetTeamMembers(teamId).Count(m => !m.Revoked && m.TeamRole == TeamRoles.TeamAdmin);

    [Fact]
    public void Member_CannotCreateTeamInvite()
    {
        var team = _teams.CreateTeam("A");
        var member = Join(team.Id, "m-1", TeamRoles.Member);

        var (status, invite) = _teams.CreateTeamInvite(member, Ttl, 5, 5, TeamRoles.Member);

        Assert.Equal(TeamOperationStatus.Forbidden, status);
        Assert.Null(invite);
    }

    [Fact]
    public void TeamAdmin_MultiUseInvite_RedeemsUpToN_IntoOwnTeam()
    {
        var team = _teams.CreateTeam("A");
        var admin = Join(team.Id, "ta-1", TeamRoles.TeamAdmin);

        var (status, invite) = _teams.CreateTeamInvite(admin, Ttl, 5, 5, TeamRoles.Member);
        Assert.Equal(TeamOperationStatus.Ok, status);

        for (var i = 0; i < 5; i++)
        {
            var redemption = _invites.TryRedeem(invite!.Code, $"new-{i}", Start);
            Assert.NotNull(redemption);
            Assert.Equal(team.Id, redemption!.TeamId);
            Assert.Equal(TeamRoles.Member, redemption.TeamRole);
        }

        Assert.Null(_invites.TryRedeem(invite!.Code, "new-6", Start));   // 6-те погашення → відмова
    }

    [Fact]
    public async Task MultiUseInvite_ConcurrentRedemptions_AtBoundary_ExactlyN()
    {
        var team = _teams.CreateTeam("A");
        var (_, invite) = _teams.CreateInviteForTeam("admin", team.Id, Ttl, 5, maxRedemptions: 2, TeamRoles.Member);

        var results = await Task.WhenAll(Enumerable.Range(0, 3)
            .Select(i => Task.Run(() => _invites.TryRedeem(invite!.Code, $"dev-{i}", Start))));

        Assert.Equal(2, results.Count(r => r is not null));
    }

    [Fact]
    public void OneTimeInvite_DefaultBehaviourUnchanged()
    {
        var invite = _invites.Create("admin", Ttl, 5);

        Assert.NotNull(_invites.TryRedeem(invite.Code, "u-1", Start));
        Assert.Null(_invites.TryRedeem(invite.Code, "u-2", Start));
    }

    [Fact]
    public void LastTeamAdmin_CannotLeaveOrDemoteSelf()
    {
        var team = _teams.CreateTeam("A");
        var admin = Join(team.Id, "ta-1", TeamRoles.TeamAdmin);

        Assert.Equal(TeamOperationStatus.LastTeamAdmin, _teams.RemoveMember(admin, admin));
        Assert.Equal(TeamOperationStatus.LastTeamAdmin, _teams.SetTeamRole(admin, admin, TeamRoles.Member));
        Assert.Equal(TeamOperationStatus.LastTeamAdmin, _teams.AdminSetTeamRole(admin, TeamRoles.Member));

        Assert.Equal(1, ActiveAdmins(team.Id));
        Assert.Equal(TeamRoles.TeamAdmin, _store.GetTeamMember(admin)!.TeamRole);
    }

    [Fact]
    public async Task TwoTeamAdmins_DemoteEachOther_Concurrently_ExactlyOneWins()
    {
        for (var round = 0; round < 20; round++)
        {
            var team = _teams.CreateTeam($"A{round}");
            var a = Join(team.Id, $"ta-a-{round}", TeamRoles.TeamAdmin);
            var b = Join(team.Id, $"ta-b-{round}", TeamRoles.TeamAdmin);

            var results = await Task.WhenAll(
                Task.Run(() => _teams.SetTeamRole(a, b, TeamRoles.Member)),
                Task.Run(() => _teams.SetTeamRole(b, a, TeamRoles.Member)));

            Assert.Equal(1, results.Count(r => r == TeamOperationStatus.Ok));
            Assert.Equal(1, ActiveAdmins(team.Id));
        }
    }

    [Fact]
    public void TeamAdmin_CannotTouchOtherTeam()
    {
        var teamA = _teams.CreateTeam("A");
        var teamB = _teams.CreateTeam("B");
        var adminA = Join(teamA.Id, "ta-a", TeamRoles.TeamAdmin);
        var memberB = Join(teamB.Id, "m-b", TeamRoles.Member);

        Assert.Equal(TeamOperationStatus.NotFound, _teams.SetTeamRole(adminA, memberB, TeamRoles.TeamAdmin));
        Assert.Equal(TeamOperationStatus.NotFound, _teams.RemoveMember(adminA, memberB));
    }

    [Fact]
    public void PromoteThenDemote_KeepsInvariant()
    {
        var team = _teams.CreateTeam("A");
        var admin = Join(team.Id, "ta-1", TeamRoles.TeamAdmin);
        var member = Join(team.Id, "m-1", TeamRoles.Member);

        Assert.Equal(TeamOperationStatus.Ok, _teams.SetTeamRole(admin, member, TeamRoles.TeamAdmin));
        Assert.Equal(TeamOperationStatus.Ok, _teams.SetTeamRole(member, admin, TeamRoles.Member));
        Assert.Equal(1, ActiveAdmins(team.Id));
    }

    [Fact]
    public void CompromisedLastTeamAdmin_GatewayRevoke_LeavesTeamWithoutAdmin_BreakGlassInviteWorks()
    {
        var team = _teams.CreateTeam("A");
        var admin = Join(team.Id, "ta-1", TeamRoles.TeamAdmin);

        // Адмін шлюзу revoke-ить identity (каскад) — інваріант тут свідомо НЕ блокує.
        _store.RevokeIdentityCredentials(admin);
        Assert.Equal(0, ActiveAdmins(team.Id));
        Assert.Null(_teams.GetActiveMembership(admin));

        // Break-glass: новий team-admin інвайт у ту саму команду.
        var (status, invite) = _teams.CreateInviteForTeam("admin", team.Id, Ttl, 5, 1, TeamRoles.TeamAdmin);
        Assert.Equal(TeamOperationStatus.Ok, status);
        var redemption = _invites.TryRedeem(invite!.Code, "ta-2", Start);
        Assert.Equal((team.Id, TeamRoles.TeamAdmin), (redemption!.TeamId, redemption.TeamRole));
    }

    [Fact]
    public void CreateInviteForTeam_UnknownTeam_NotFound()
    {
        var (status, invite) = _teams.CreateInviteForTeam("admin", "no-such-team", Ttl, 5, 1, TeamRoles.TeamAdmin);
        Assert.Equal(TeamOperationStatus.NotFound, status);
        Assert.Null(invite);
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
}
