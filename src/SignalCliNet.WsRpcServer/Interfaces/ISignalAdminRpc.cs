using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

/// <summary>
/// Admin RPC surface (add-invites-admin, task 3.1). Every method is <c>RpcPolicyKind.Admin</c> in the
/// policy registry, so the chokepoint refuses a non-admin principal with <c>-32001</c> — these are only
/// reachable over the separate mTLS admin port (cert = credential), never the plain token port.
/// </summary>
/// <remarks>
/// Кожен виклик пише подію у tamper-evident audit-trail (task 3.2). Актор — admin identity id, виведений
/// із валідованого mTLS-cert-а (з call-context principal'а), НІКОЛИ з клієнтського аргумента.
/// </remarks>
public interface ISignalAdminRpc : IRpcService
{
    /// <summary>
    /// Mints a one-time invite code (Sybil-gate seed for <c>redeemInvite</c>). Returns the plaintext code
    /// once, for out-of-band delivery to the new user; only its at-rest hash is persisted.
    /// </summary>
    /// <param name="ttlSeconds">Optional TTL in seconds (defaults to the configured admin default).</param>
    /// <param name="maxAttempts">Optional per-code redeem-attempt cap (defaults to the configured admin default).</param>
    /// <param name="teamId">
    /// Optional team (add-team-notify): each redemption adds a new identity to it with <paramref name="teamRole"/>.
    /// Break-glass path for a team left without a team-admin.
    /// </param>
    /// <param name="teamRole">Team role for redeemed identities (required with <paramref name="teamId"/>).</param>
    /// <param name="maxRedemptions">How many times the invite can be redeemed (≥1; &gt;1 only with a team).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plaintext invite code and its absolute expiry.</returns>
    Task<CreateInviteResponse> CreateInvite(
        int? ttlSeconds = null, int? maxAttempts = null, string? teamId = null, string? teamRole = null,
        int maxRedemptions = 1, CancellationToken cancellationToken = default);

    /// <summary>Creates a team (add-team-notify). Follow with <c>createInvite(teamId, "team-admin")</c>.</summary>
    /// <param name="name">Human-readable team name (not PII).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new team id and name.</returns>
    Task<CreateTeamResponse> CreateTeam(string name, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists teams with their active member / team-admin counts; <c>activeTeamAdmins = 0</c> marks a team
    /// left without an admin (e.g. after <c>revokeIdentity</c>) that needs a break-glass invite.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>All teams.</returns>
    Task<ListTeamsResponse> ListTeams(CancellationToken cancellationToken = default);

    /// <summary>
    /// Break-glass role change for any identity in a team. The ≥1 team-admin invariant still holds
    /// (<c>-32013</c>); promote first, then demote.
    /// </summary>
    /// <param name="identityId">Target identity.</param>
    /// <param name="teamRole"><c>team-admin</c> or <c>member</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The member's new role.</returns>
    Task<TeamMemberInfo> AdminSetTeamRole(string identityId, string teamRole, CancellationToken cancellationToken = default);

    /// <summary>
    /// Revokes ALL of an identity's credentials (access tokens + device secret, cascade) and tears down its
    /// live connections (change 3 <c>TokenService.RevokeIdentity</c>).
    /// </summary>
    /// <param name="identityId">The identity to revoke (opaque; never a phone number).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An acknowledgement of the revoke.</returns>
    Task<RevokeIdentityResponse> RevokeIdentity(string identityId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Surgically revokes ONE group-binding of another identity (add-invites-admin task 2.4) — the admin
    /// counterpart of the user's <c>revokeMyBinding</c>. Unlike <see cref="RevokeIdentity"/> (which nukes
    /// every credential), this invalidates a single <c>(identity, group)</c> claim; the binding MUST belong
    /// to <paramref name="identityId"/> or the call fails <c>InvalidParams</c> (anti-oracle: unknown and
    /// foreign binding return the same error). Audited as <c>admin_binding_revoked</c>.
    /// </summary>
    /// <param name="identityId">The identity that owns the binding (opaque; never a phone number).</param>
    /// <param name="bindingId">The group-binding to revoke (opaque).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>An acknowledgement of the revoke.</returns>
    Task<RevokeBindingResponse> RevokeBinding(
        string identityId, string bindingId, CancellationToken cancellationToken = default);
}
