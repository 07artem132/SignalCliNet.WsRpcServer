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
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plaintext invite code and its absolute expiry.</returns>
    Task<CreateInviteResponse> CreateInvite(
        int? ttlSeconds = null, int? maxAttempts = null, CancellationToken cancellationToken = default);

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
