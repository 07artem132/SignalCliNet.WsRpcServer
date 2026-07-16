using Microsoft.Extensions.Logging;
using SignalCliNet.WsRpcServer.Persistence;

namespace SignalCliNet.WsRpcServer.Security;

/// <summary>
/// Issues, authenticates and rotates opaque access tokens against the durable store — the single
/// source of truth for token lifecycle (authn/T1). The store is consulted on every authentication;
/// there is no cache of revoke/expiry state (spec requirement).
/// </summary>
/// <remarks>
/// Plaintext-токен існує лише в момент видачі/ротації (значення, що повертається) — його НІКОЛИ не
/// логуємо і не зберігаємо; у сторі живе тільки версіонований HMAC (pre-image = decoded payload, W18).
/// </remarks>
public sealed class TokenService
{
    private readonly IPepperProvider _pepperProvider;
    private readonly IDurableStore _store;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TokenService> _logger;

    /// <summary>Creates the token service.</summary>
    public TokenService(
        IPepperProvider pepperProvider,
        IDurableStore store,
        TimeProvider timeProvider,
        ILogger<TokenService> logger)
    {
        _pepperProvider = pepperProvider ?? throw new ArgumentNullException(nameof(pepperProvider));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Issues a fresh token for <paramref name="identityId"/> with the given <paramref name="ttl"/>,
    /// persists its at-rest hash and returns the plaintext (the sole point where plaintext exists).
    /// </summary>
    /// <param name="identityId">The identity the token authenticates.</param>
    /// <param name="ttl">Time-to-live from now until expiry.</param>
    /// <returns>The plaintext token and its persisted record.</returns>
    public (string Token, TokenRecord Record) IssueToken(string identityId, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var record = Mint(identityId, ttl, out var token);
        _store.InsertToken(record);

        // НЕ логуємо plaintext-токен — лише факт видачі, identity й термін.
        _logger.LogInformation(
            "Видано токен для identity {IdentityId} (діє до {ExpiresAt:O}).", identityId, record.ExpiresAt);
        return (token, record);
    }

    /// <summary>
    /// Authenticates <paramref name="token"/> against the store: malformed / unknown ⇒ <c>Invalid</c>,
    /// revoked ⇒ <c>Revoked</c> (wins over expiry, D7), past expiry ⇒ <c>Expired</c>, else <c>Valid</c>.
    /// </summary>
    /// <param name="token">The candidate plaintext token.</param>
    /// <returns>The authentication result.</returns>
    public TokenAuthenticationResult Authenticate(string? token)
    {
        if (!TokenFormat.TryParse(token, out var version, out var payload))
            return Fail();

        byte[] pepper;
        try
        {
            pepper = _pepperProvider.GetPepper(version);
        }
        catch (InvalidOperationException)
        {
            // Невідома версія пеппера — трактуємо як невалідний токен (fast-reject).
            return Fail();
        }

        var hash = TokenFormat.ComputeHash(payload, pepper);
        var record = _store.GetToken(hash);
        if (record is null)
            return Fail();

        // IsRevoked перемагає над expired: revocation лишається головною гарантією (D7).
        if (record.IsRevoked)
            return new TokenAuthenticationResult(TokenAuthenticationStatus.Revoked, record.IdentityId, record.TokenHash);

        if (record.ExpiresAt < _timeProvider.GetUtcNow())
            return new TokenAuthenticationResult(TokenAuthenticationStatus.Expired, record.IdentityId, record.TokenHash);

        return new TokenAuthenticationResult(TokenAuthenticationStatus.Valid, record.IdentityId, record.TokenHash);

        static TokenAuthenticationResult Fail() =>
            new(TokenAuthenticationStatus.Invalid, null, null);
    }

    /// <summary>
    /// Rotates a token: issues a new one for <paramref name="identityId"/> and revokes
    /// <paramref name="oldTokenHash"/> in the SAME store transaction (W21 zero-overlap).
    /// </summary>
    /// <param name="oldTokenHash">The at-rest hash of the token being replaced.</param>
    /// <param name="identityId">The identity the new token authenticates.</param>
    /// <param name="ttl">Time-to-live for the new token.</param>
    /// <returns>The new plaintext token and its persisted record.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="oldTokenHash"/> is absent (no predecessor).</exception>
    public (string Token, TokenRecord Record) Rotate(string oldTokenHash, string identityId, TimeSpan ttl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldTokenHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(identityId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(ttl, TimeSpan.Zero);

        var record = Mint(identityId, ttl, out var token);
        _store.RotateToken(oldTokenHash, record);

        _logger.LogInformation(
            "Ротовано токен identity {IdentityId} (старий revoked, новий діє до {ExpiresAt:O}).",
            identityId, record.ExpiresAt);
        return (token, record);
    }

    // Генерує plaintext-токен + відповідний TokenRecord (ще НЕ у сторі) під поточну версію пеппера.
    private TokenRecord Mint(string identityId, TimeSpan ttl, out string token)
    {
        var version = _pepperProvider.CurrentVersion;
        token = TokenFormat.Create(version, out var payload);
        var hash = TokenFormat.ComputeHash(payload, _pepperProvider.GetPepper(version));
        var now = _timeProvider.GetUtcNow();
        return new TokenRecord(hash, version, identityId, now + ttl, IsRevoked: false, now);
    }
}
