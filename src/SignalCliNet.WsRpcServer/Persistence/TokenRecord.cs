namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable access-token record — the store is the single source of truth for token lifecycle
/// (authn/T1). Only the versioned at-rest HMAC is persisted; the plaintext opaque token never is.
/// </summary>
/// <param name="TokenHash">At-rest <c>HMAC-SHA256(decoded random payload, pepper[version])</c>, base64 (primary key).</param>
/// <param name="PepperVersion">Pepper version the hash was computed with (version-hint from the token prefix, D10).</param>
/// <param name="IdentityId">The identity this token authenticates.</param>
/// <param name="ExpiresAt">Expiry timestamp (UTC); a token past this is <c>Expired</c> unless revoked first.</param>
/// <param name="IsRevoked">Whether the token has been revoked (always wins over expiry, D7).</param>
/// <param name="CreatedAt">Issue timestamp (UTC).</param>
public sealed record TokenRecord(
    string TokenHash,
    int PepperVersion,
    string IdentityId,
    DateTimeOffset ExpiresAt,
    bool IsRevoked,
    DateTimeOffset CreatedAt);
