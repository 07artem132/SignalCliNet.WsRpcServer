using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>Чому командна операція відмовила.</summary>
public enum TeamOperationStatus
{
    /// <summary>Виконано.</summary>
    Ok,

    /// <summary>Caller не має прав (не активний учасник / не team-admin цієї команди).</summary>
    Forbidden,

    /// <summary>Ціль не знайдено в команді caller-а (анти-oracle: чужа й неіснуюча — однаково).</summary>
    NotFound,

    /// <summary>Зміна залишила б команду без активного team-admin.</summary>
    LastTeamAdmin,
}

/// <summary>
/// Команди й ролі (add-team-notify): створення команди, інвайти team-admin у свою команду, зміна ролей і
/// видалення учасників з інваріантом «≥1 активний team-admin». Права перевіряються тут, у сервісному шарі,
/// а не лише в адаптері.
/// </summary>
/// <remarks>
/// Admin шлюзу (mTLS) — break-glass: створює команду, видає інвайт <c>team-admin</c> у будь-яку команду й
/// підвищує identity. Знімати останнього team-admin не може ніхто через ці методи; revoke identity адміном
/// шлюзу (компрометація) обходить інваріант і лишає команду «без адміна» — це видно в <c>listTeams</c>.
/// </remarks>
public sealed class TeamService(
    IDurableStore store,
    InviteService inviteService,
    TimeProvider timeProvider,
    ILogger<TeamService> logger)
{
    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly InviteService _inviteService = inviteService ?? throw new ArgumentNullException(nameof(inviteService));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<TeamService> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <summary>Створює команду (admin шлюзу).</summary>
    public TeamRecord CreateTeam(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var team = new TeamRecord(NewId(), name.Trim(), _timeProvider.GetUtcNow());
        _store.InsertTeam(team);
        _logger.LogInformation("Створено команду {TeamId}.", team.Id);
        return team;
    }

    /// <summary>Активне членство caller-а, або <c>null</c>.</summary>
    public TeamMemberRecord? GetActiveMembership(string identityId)
    {
        var member = _store.GetTeamMember(identityId);
        return member is { Revoked: false } ? member : null;
    }

    /// <summary>Чи <paramref name="identityId"/> — активний team-admin.</summary>
    public bool IsActiveTeamAdmin(string identityId) =>
        GetActiveMembership(identityId) is { TeamRole: TeamRoles.TeamAdmin };

    /// <summary>
    /// Team-admin видає інвайт у СВОЮ команду. Роль <c>team-admin</c> в інвайті може видати лише team-admin
    /// (тут це завжди так); <c>member</c> на цей метод отримує <see cref="TeamOperationStatus.Forbidden"/>.
    /// </summary>
    public (TeamOperationStatus Status, InviteCreation? Invite) CreateTeamInvite(
        string actorIdentityId, TimeSpan ttl, int maxAttempts, int maxRedemptions, string teamRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorIdentityId);
        var actor = GetActiveMembership(actorIdentityId);
        if (actor is not { TeamRole: TeamRoles.TeamAdmin })
            return (TeamOperationStatus.Forbidden, null);

        var invite = _inviteService.Create(actorIdentityId, ttl, maxAttempts, actor.TeamId, teamRole, maxRedemptions);
        return (TeamOperationStatus.Ok, invite);
    }

    /// <summary>Break-glass: admin шлюзу видає інвайт у будь-яку існуючу команду.</summary>
    public (TeamOperationStatus Status, InviteCreation? Invite) CreateInviteForTeam(
        string adminIdentityId, string teamId, TimeSpan ttl, int maxAttempts, int maxRedemptions, string teamRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adminIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(teamId);
        if (_store.GetTeam(teamId) is null)
            return (TeamOperationStatus.NotFound, null);

        var invite = _inviteService.Create(adminIdentityId, ttl, maxAttempts, teamId, teamRole, maxRedemptions);
        return (TeamOperationStatus.Ok, invite);
    }

    /// <summary>Team-admin змінює роль учасника СВОЄЇ команди (з інваріантом ≥1 team-admin).</summary>
    public TeamOperationStatus SetTeamRole(string actorIdentityId, string targetIdentityId, string teamRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentityId);
        if (!TeamRoles.IsValid(teamRole))
            throw new ArgumentException("Unknown team role.", nameof(teamRole));

        var actor = GetActiveMembership(actorIdentityId);
        if (actor is not { TeamRole: TeamRoles.TeamAdmin })
            return TeamOperationStatus.Forbidden;

        var target = GetActiveMembership(targetIdentityId);
        if (target is null || target.TeamId != actor.TeamId)
            return TeamOperationStatus.NotFound;

        return Map(_store.TrySetTeamRole(targetIdentityId, teamRole, enforceLastAdmin: true));
    }

    /// <summary>Break-glass: admin шлюзу змінює роль identity в її команді (інваріант діє й тут).</summary>
    public TeamOperationStatus AdminSetTeamRole(string targetIdentityId, string teamRole)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentityId);
        if (!TeamRoles.IsValid(teamRole))
            throw new ArgumentException("Unknown team role.", nameof(teamRole));

        return Map(_store.TrySetTeamRole(targetIdentityId, teamRole, enforceLastAdmin: true));
    }

    /// <summary>
    /// Видаляє учасника з команди: себе — будь-який учасник, іншого — лише team-admin тієї ж команди.
    /// Останнього team-admin видалити не можна (зокрема self-leave).
    /// </summary>
    public TeamOperationStatus RemoveMember(string actorIdentityId, string targetIdentityId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorIdentityId);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentityId);

        var actor = GetActiveMembership(actorIdentityId);
        if (actor is null)
            return TeamOperationStatus.Forbidden;

        var self = string.Equals(actorIdentityId, targetIdentityId, StringComparison.Ordinal);
        if (!self && actor.TeamRole != TeamRoles.TeamAdmin)
            return TeamOperationStatus.Forbidden;

        var target = GetActiveMembership(targetIdentityId);
        if (target is null || target.TeamId != actor.TeamId)
            return TeamOperationStatus.NotFound;

        return Map(_store.TryRemoveTeamMember(targetIdentityId, enforceLastAdmin: true));
    }

    /// <summary>Team-admin відкликає командну alias-прив'язку своєї команди.</summary>
    public TeamOperationStatus RevokeBinding(string actorIdentityId, string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actorIdentityId);
        var actor = GetActiveMembership(actorIdentityId);
        if (actor is not { TeamRole: TeamRoles.TeamAdmin })
            return TeamOperationStatus.Forbidden;

        var binding = TeamAlias.IsValid(alias) ? _store.GetTeamBinding(actor.TeamId, alias) : null;
        if (binding is null || binding.Revoked)
            return TeamOperationStatus.NotFound;

        _store.RevokeTeamBinding(actor.TeamId, alias);
        return TeamOperationStatus.Ok;
    }

    private static TeamOperationStatus Map(TeamMutationResult result) => result switch
    {
        TeamMutationResult.Applied => TeamOperationStatus.Ok,
        TeamMutationResult.LastTeamAdmin => TeamOperationStatus.LastTeamAdmin,
        _ => TeamOperationStatus.NotFound,
    };

    // Opaque id — 128-bit CSPRNG у hex.
    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
