using SignalCliNet.WsRpcServer.Model;
using WsRpcServer.Services;

namespace SignalCliNet.WsRpcServer.Interfaces;

/// <summary>
/// Onboarding RPC surface (add-invites-admin): redeeming a one-time invite to bootstrap a brand-new
/// identity. Runs BEFORE any account/token exists (policy <c>IdentityOnboarding</c>, unauthenticated,
/// rate-limited) — kept separate from the devices surface because onboarding is not a devices-domain op.
/// </summary>
public interface ISignalOnboardingRpc : IRpcService
{
    /// <summary>
    /// Redeems a one-time invite <paramref name="code"/> and, on success, provisions a fresh identity:
    /// enrolls <paramref name="devicePublicKey"/> (TOFU) and issues that identity's first access token.
    /// </summary>
    /// <remarks>
    /// Anti-oracle: будь-яка невдача (невідомий / прострочений / уже погашений / понад cap код) повертає
    /// ОДНАКОВУ помилку — клієнт не дізнається причину. Плейнтекст-токен у відповіді повертається клієнту
    /// рівно один раз і НІКОЛИ не логується.
    /// </remarks>
    /// <param name="code">The one-time invite code (any block formatting; normalized server-side).</param>
    /// <param name="devicePublicKey">The device public key as base64 SPKI (ECDSA P-256 / ES256).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The new identity id and its first access token.</returns>
    Task<RedeemInviteResponse> RedeemInvite(
        string code, string devicePublicKey, CancellationToken cancellationToken = default);
}
