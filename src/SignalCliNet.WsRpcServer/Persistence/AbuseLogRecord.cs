namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable abuse-log record for shared-bot events (add-invites-admin, task 3.3). Stores only a
/// domain-separated HMAC of the subject — never the raw subject/number (privacy, CLAUDE rule #4).
/// </summary>
/// <remarks>
/// W24 (equality-oracle): pre-image хешу містить per-record <see cref="Salt"/> І callerIdentityId, тож
/// той самий subject, залогований різними записами, дає РІЗНІ hmac — спостерігач не може звірити
/// рівність суб'єктів між записами. <see cref="SubjectHmac"/> = <c>base64(HMAC-SHA256(pepper_abuse,
/// UTF8(salt‖callerIdentityId‖subject)))</c>.
///
/// Residual: цей лог поки НЕ e2e-tamper-proof (без hash-chain) — tamper-evident audit-trail із ланцюгом
/// хешів приходить окремою секцією (audit-trail, M8). Див. <c>docs/shared-bot.md</c>.
/// </remarks>
/// <param name="EventType">Тип події (напр. <c>invite_redeemed</c>) — машиночитний тег, без PII.</param>
/// <param name="SubjectHmac">Домен-розділений HMAC суб'єкта, base64 (сирий суб'єкт НЕ зберігається).</param>
/// <param name="Salt">Per-record CSPRNG-сіль (base64), частина pre-image хешу (W24).</param>
/// <param name="LoggedAt">Час запису події (UTC).</param>
public sealed record AbuseLogRecord(
    string EventType,
    string SubjectHmac,
    string Salt,
    DateTimeOffset LoggedAt);
