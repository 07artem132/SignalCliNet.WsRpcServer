namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable identity record — the foundation record consumed by later capabilities (authn,
/// admission, group-claim, invites): <c>{ id, role, linkedAccounts[], createdAt }</c> (T1/D17).
/// </summary>
/// <param name="Id">Stable identity id (opaque; never a phone number).</param>
/// <param name="Role">Identity role (e.g. admin / user) — an opaque string owned by the authn layer.</param>
/// <param name="LinkedAccounts">Signal accounts (E.164) linked to this identity.</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
public sealed record IdentityRecord(
    string Id,
    string Role,
    IReadOnlyList<string> LinkedAccounts,
    DateTimeOffset CreatedAt);
