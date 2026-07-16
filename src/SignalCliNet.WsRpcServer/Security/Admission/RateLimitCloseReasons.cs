namespace SignalCliNet.WsRpcServer.Security.Admission;

/// <summary>
/// Machine-readable WebSocket close reasons carried in the close-frame payload for transport
/// rate/admission limits (task 3.2 / 4.1). These sit alongside the auth close-reason vocabulary
/// (<c>AuthCloseReasons</c>) but describe rate/idle events, not auth failures.
/// </summary>
/// <remarks>
/// <b>Словник reason-ів (клієнтський контракт):</b>
/// <list type="bullet">
///   <item><see cref="TooManyConnections"/> — close <c>4429</c>: перевищено per-identity cap одночасних
///     конектів (<c>Server:Admission:MaxConnectionsPerIdentity</c>). Клієнт має закрити зайві сесії.</item>
///   <item><c>message-rate:&lt;sec&gt;</c> (префікс <see cref="MessageRate"/>) — close <c>4429</c>:
///     per-connection msg-rate флуд (<c>MessagesPerMinutePerConnection</c>). Суфікс <c>&lt;sec&gt;</c> —
///     машиночитний retry_after у секундах (in-band, S9 — 4429-код сам по собі секунд не несе).</item>
///   <item><see cref="IdleTimeout"/> — close <c>1000</c> (штатний, НЕ <c>4429</c>): з'єднання мовчало
///     довше за <c>IdleTimeoutMinutes</c>. Клієнт може перепідключитись без backoff.</item>
/// </list>
/// </remarks>
public static class RateLimitCloseReasons
{
    /// <summary>Per-identity concurrent-connection cap exceeded (close <c>4429</c>).</summary>
    public const string TooManyConnections = "too-many-connections";

    /// <summary>
    /// Per-connection message-rate exceeded (close <c>4429</c>). The wire reason is
    /// <c>message-rate:&lt;sec&gt;</c> — see <see cref="FormatMessageRate"/>; this constant is the prefix.
    /// </summary>
    public const string MessageRate = "message-rate";

    /// <summary>Idle timeout — connection silent too long (close <c>1000</c> normal).</summary>
    public const string IdleTimeout = "idle-timeout";

    /// <summary>
    /// Builds the <c>message-rate:&lt;sec&gt;</c> close reason with the in-band retry hint appended
    /// (machine-readable — the <c>4429</c> code alone carries no seconds).
    /// </summary>
    /// <param name="retryAfterSeconds">Seconds until the message-rate bucket refills one token (≥ 1).</param>
    /// <returns>The formatted close reason, e.g. <c>message-rate:12</c>.</returns>
    public static string FormatMessageRate(int retryAfterSeconds) =>
        $"{MessageRate}:{retryAfterSeconds}";
}
