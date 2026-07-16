namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable device-key (proof-of-possession) record, one per identity (enrollment-rooted, D1).
/// Holds only the public key; the private key never leaves the client device.
/// </summary>
/// <param name="IdentityId">The identity that enrolled this device key (primary key).</param>
/// <param name="PublicKey">The enrolled device public key (opaque, algorithm-specific encoding).</param>
/// <param name="Algorithm">The signature algorithm identifier the public key is used with.</param>
/// <param name="IsRevoked">Whether the device secret has been revoked (cascaded from identity revoke).</param>
/// <param name="CreatedAt">Enrollment timestamp (UTC).</param>
public sealed record DeviceSecretRecord(
    string IdentityId,
    string PublicKey,
    string Algorithm,
    bool IsRevoked,
    DateTimeOffset CreatedAt);
