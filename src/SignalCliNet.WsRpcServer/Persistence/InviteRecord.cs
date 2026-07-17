namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable one-time invite record — the Sybil-gate for identity onboarding (add-invites-admin, task 1.1).
/// Persists only the at-rest <see cref="CodeHash"/>; the high-entropy plaintext code never is.
/// </summary>
/// <remarks>
/// Attempt cap (G12): кожна спроба redeem АТОМАРНО інкрементує <see cref="AttemptCount"/> (зокрема
/// невдала), тож паралельні guesses не обходять cap гонкою. Consume-and-bind (W20): успішний redeem
/// виставляє <see cref="Consumed"/>=1 і <see cref="ConsumedBy"/> у тій самій транзакції — рівно один
/// клієнт виграє. <see cref="CreatedBy"/> — identityId адміна-емітента (звичайний TEXT, без FK: на
/// bootstrap-шляху admin-identity може ще не існувати).
/// </remarks>
/// <param name="CodeHash">At-rest <c>SHA-256(нормалізований код)</c>, base64 (primary key). Не секрет — код сам ≥96-bit секрет.</param>
/// <param name="CreatedBy">Identity id адміна, що видав інвайт (opaque; ніколи не номер телефону).</param>
/// <param name="Consumed">Чи вже погашено інвайт (one-time: після consume redeem відмовляє).</param>
/// <param name="AttemptCount">Кількість спроб redeem (кожна, зокрема невдала, рахується — G12).</param>
/// <param name="MaxAttempts">Стеля спроб redeem; перевищення → відмова (per-code brute-force cap).</param>
/// <param name="ExpiresAt">Абсолютний термін дії (created + TTL); після нього redeem відмовляє.</param>
/// <param name="ConsumedBy">Identity id, що погасив інвайт (<c>null</c> до consume).</param>
/// <param name="CreatedAt">Час видачі (UTC).</param>
public sealed record InviteRecord(
    string CodeHash,
    string CreatedBy,
    bool Consumed,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset ExpiresAt,
    string? ConsumedBy,
    DateTimeOffset CreatedAt);
