namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable send-idempotency (dedup) row (migration v7, task 3.3). Keyed by
/// <c>(identityId, messageId)</c> — no cross-tenant collisions (T6). Survives restart so a retry (even
/// after a crash) is deduplicated at-most-once.
/// </summary>
/// <param name="IdentityId">The sending identity (part of the composite primary key).</param>
/// <param name="MessageId">The client-supplied idempotency key (part of the composite primary key).</param>
/// <param name="Status"><c>pending</c> (send begun, outcome unknown) or <c>done</c> (result stored).</param>
/// <param name="ResultJson">The saved first-call result JSON when <see cref="Status"/> is <c>done</c>; else <c>null</c>.</param>
/// <param name="BudgetReserved">Whether this row owns a reserved aggregate-budget unit (crash-forfeit bookkeeping).</param>
/// <param name="CreatedAt">When the <c>pending</c> row was first written.</param>
public sealed record SendDedupRecord(
    string IdentityId,
    string MessageId,
    string Status,
    string? ResultJson,
    bool BudgetReserved,
    DateTimeOffset CreatedAt)
{
    /// <summary><c>pending</c> status literal — send begun, outcome unknown (at-most-once forfeit on crash).</summary>
    public const string StatusPending = "pending";

    /// <summary><c>done</c> status literal — result durably stored; a retry returns it without re-sending.</summary>
    public const string StatusDone = "done";
}
