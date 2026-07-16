namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable <c>account → owning-identity</c> binding with a private flag (R3.5). Later capabilities
/// (group-claim receive-router, admission, incoming isolation) read this to route/filter per account.
/// </summary>
/// <param name="Account">The Signal account (E.164) this binding is keyed by.</param>
/// <param name="OwningIdentityId">The identity that owns the account.</param>
/// <param name="IsPrivate">Whether the account is private to its owner (R3.5 — never surfaced to others).</param>
/// <param name="CreatedAt">Creation timestamp (UTC).</param>
public sealed record AccountBinding(
    string Account,
    string OwningIdentityId,
    bool IsPrivate,
    DateTimeOffset CreatedAt);
