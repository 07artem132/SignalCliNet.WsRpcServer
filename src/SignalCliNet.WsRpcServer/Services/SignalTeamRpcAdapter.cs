using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SignalCliNet.WsRpcServer.Deployment;
using SignalCliNet.WsRpcServer.Interfaces;
using SignalCliNet.WsRpcServer.Model;
using SignalCliNet.WsRpcServer.Persistence;
using SignalCliNet.WsRpcServer.Security;
using StreamJsonRpc.Protocol;
using WsRpcServer.Exceptions;

namespace SignalCliNet.WsRpcServer.Services;

/// <summary>
/// Адаптер командної поверхні (add-team-notify). Тонкий: caller — з principal, права й інваріанти — у
/// <see cref="TeamService"/>; тут лише валідація аргументів на межі RPC і мапінг статусів у коди помилок.
/// </summary>
public sealed class SignalTeamRpcAdapter(
    TeamService teamService,
    IDurableStore store,
    AuditLogService auditLog,
    IOptions<AdminOptions> adminOptions,
    TimeProvider timeProvider,
    ILogger<SignalTeamRpcAdapter> logger)
    : ISignalTeamRpc
{
    /// <summary>Верхня межа погашень одного командного інвайту (захист від «вічного» інвайту).</summary>
    public const int MaxRedemptionsCap = 1000;

    // Та сама стеля TTL, що й у AdminOptions.DefaultInviteTtlSeconds (30 діб).
    private static readonly TimeSpan MaxInviteTtl = TimeSpan.FromDays(30);

    private readonly TeamService _teamService = teamService ?? throw new ArgumentNullException(nameof(teamService));
    private readonly IDurableStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly AuditLogService _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    private readonly AdminOptions _adminOptions = (adminOptions ?? throw new ArgumentNullException(nameof(adminOptions))).Value;
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger<SignalTeamRpcAdapter> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    /// <inheritdoc />
    public Task<CreateInviteResponse> CreateTeamInvite(
        int maxRedemptions = 1, int? ttlSeconds = null, string? teamRole = null,
        CancellationToken cancellationToken = default)
    {
        var actor = RequireIdentity();
        var role = teamRole ?? TeamRoles.Member;
        if (!TeamRoles.IsValid(role))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "teamRole must be 'team-admin' or 'member'.");
        if (maxRedemptions is < 1 or > MaxRedemptionsCap)
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, $"maxRedemptions must be 1..{MaxRedemptionsCap}.");

        var ttl = TimeSpan.FromSeconds(ttlSeconds ?? _adminOptions.DefaultInviteTtlSeconds);
        if (ttl <= TimeSpan.Zero || ttl > MaxInviteTtl)
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "ttlSeconds must be 1..2592000.");

        var (status, invite) = _teamService.CreateTeamInvite(
            actor, ttl, _adminOptions.DefaultInviteMaxAttempts, maxRedemptions, role);
        ThrowOnFailure(status);

        _auditLog.Append("team_invite_created", actor, invite!.Record.CodeHash);
        return Task.FromResult(new CreateInviteResponse(invite.Code, invite.Record.ExpiresAt));
    }

    /// <inheritdoc />
    public Task<ListTeamMembersResponse> ListTeamMembers(CancellationToken cancellationToken = default)
    {
        var actor = RequireIdentity();
        var membership = _teamService.GetActiveMembership(actor);
        if (membership is not { TeamRole: TeamRoles.TeamAdmin })
            ThrowOnFailure(TeamOperationStatus.Forbidden);

        var members = _store.GetTeamMembers(membership!.TeamId)
            .Where(static m => !m.Revoked)
            .Select(static m => new TeamMemberInfo(m.IdentityId, m.TeamRole))
            .ToList();
        return Task.FromResult(new ListTeamMembersResponse(membership.TeamId, members));
    }

    /// <inheritdoc />
    public Task<TeamMemberInfo> SetTeamRole(string identityId, string teamRole, CancellationToken cancellationToken = default)
    {
        var actor = RequireIdentity();
        RequireArg(identityId, nameof(identityId));
        if (!TeamRoles.IsValid(teamRole))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "teamRole must be 'team-admin' or 'member'.");

        ThrowOnFailure(_teamService.SetTeamRole(actor, identityId, teamRole));
        _auditLog.Append("team_role_changed", actor, $"{identityId}:{teamRole}");
        _logger.LogInformation("Team: {Actor} змінив роль {IdentityId} → {Role}.", actor, identityId, teamRole);
        return Task.FromResult(new TeamMemberInfo(identityId, teamRole));
    }

    /// <inheritdoc />
    public Task<RevokeIdentityResponse> RemoveTeamMember(string identityId, CancellationToken cancellationToken = default)
    {
        var actor = RequireIdentity();
        RequireArg(identityId, nameof(identityId));

        ThrowOnFailure(_teamService.RemoveMember(actor, identityId));
        _auditLog.Append("team_member_removed", actor, identityId);
        _logger.LogInformation("Team: {Actor} видалив {IdentityId} з команди.", actor, identityId);
        return Task.FromResult(new RevokeIdentityResponse(identityId, Revoked: true));
    }

    /// <inheritdoc />
    public Task<ListTeamBindingsResponse> ListTeamBindings(CancellationToken cancellationToken = default)
    {
        var actor = RequireIdentity();
        var membership = _teamService.GetActiveMembership(actor);
        if (membership is null)
            ThrowOnFailure(TeamOperationStatus.Forbidden);

        var now = _timeProvider.GetUtcNow();
        var bindings = _store.GetTeamBindings(membership!.TeamId)
            .Where(b => !b.Revoked && b.ExpiresAt > now)
            .Select(static b => new TeamBindingInfo(b.Alias, b.GroupId, b.ExpiresAt))
            .ToList();
        return Task.FromResult(new ListTeamBindingsResponse(membership.TeamId, bindings));
    }

    /// <inheritdoc />
    public Task<RevokeBindingResponse> RevokeTeamBinding(string alias, CancellationToken cancellationToken = default)
    {
        var actor = RequireIdentity();
        RequireArg(alias, nameof(alias));

        ThrowOnFailure(_teamService.RevokeBinding(actor, alias));
        _auditLog.Append("team_binding_revoked", actor, alias);
        return Task.FromResult(new RevokeBindingResponse(alias, Revoked: true));
    }

    // Статус сервісу → детермінований код: Forbidden → -32001, NotFound → -32602 (анти-oracle),
    // LastTeamAdmin → -32013.
    internal static void ThrowOnFailure(TeamOperationStatus status)
    {
        switch (status)
        {
            case TeamOperationStatus.Ok:
                return;
            case TeamOperationStatus.Forbidden:
                throw new RpcErrorException((JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, "Access denied.");
            case TeamOperationStatus.LastTeamAdmin:
                throw new RpcErrorException((JsonRpcErrorCode)TeamErrorCodes.LastTeamAdmin, TeamErrorCodes.LastTeamAdminMessage);
            default:
                throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, "Not found.");
        }
    }

    private static void RequireArg(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new RpcErrorException(JsonRpcErrorCode.InvalidParams, $"{name} cannot be empty.");
    }

    // Caller identity — з call-context principal'а (V8/anti-IDOR), НІКОЛИ з клієнтського аргумента.
    private static string RequireIdentity()
    {
        var principal = SignalCallContext.Principal;
        if (principal is null || string.IsNullOrWhiteSpace(principal.Name))
            throw new RpcErrorException((JsonRpcErrorCode)AccountIsolationGuard.UnauthorizedErrorCode, "Access denied.");

        return principal.Name;
    }
}
