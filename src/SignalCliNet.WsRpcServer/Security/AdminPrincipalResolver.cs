using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Resolves a validated mTLS node identity (its SPKI-SHA-256 thumbprint) to an admin
/// <see cref="SignalPrincipal"/> via the durable <c>admin_credentials</c> mapping (add-invites-admin,
/// task 2.3). No match, or a revoked mapping, yields <c>null</c> — the caller then refuses the connection.
/// </summary>
/// <remarks>
/// Extracted from the secure admin session so the SPKI→principal decision is unit-testable without a live
/// TLS handshake. The resolved principal is <c>isAdmin: true, hasFullAccess: false</c> — admin has
/// read-only oversight of all accounts (R3.6) via the IsAdmin filter, NOT operational full access
/// (visibility ≠ operation). Ім'я principal'а = admin identity id (актор для audit/invite).
/// </remarks>
public static class AdminPrincipalResolver
{
    /// <summary>
    /// Resolves <paramref name="spkiThumbprint"/> to an admin principal, or <c>null</c> if the SPKI is not
    /// a bound (non-revoked) admin credential.
    /// </summary>
    /// <param name="store">The durable store (admin_credentials mapping).</param>
    /// <param name="spkiThumbprint">The validated client cert's SPKI-SHA-256 (hex).</param>
    /// <returns>The admin principal on success; otherwise <c>null</c> (deny).</returns>
    public static SignalPrincipal? Resolve(IDurableStore store, string? spkiThumbprint)
    {
        ArgumentNullException.ThrowIfNull(store);

        if (string.IsNullOrWhiteSpace(spkiThumbprint))
            return null;

        var credential = store.GetAdminCredential(spkiThumbprint);
        if (credential is null || credential.Revoked)
            return null;

        // Admin: read-only нагляд УСІХ акаунтів (IsAdmin), без операційного full-access (R3.6).
        return new SignalPrincipal(
            name: credential.IdentityId,
            isAdmin: true,
            hasFullAccess: false,
            allowedAccounts: [],
            allowedGroupIds: []);
    }
}
