namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable one-time group-claim record (add-group-claim-receive, task 2.1). Persists only the at-rest
/// <see cref="CodeHash"/>; the high-entropy plaintext claim code never is (the code itself is the secret,
/// mirror of <see cref="InviteRecord"/>).
/// </summary>
/// <remarks>
/// T3 (group-agnostic при видачі): claim прив'язаний ЛИШЕ до <see cref="CreatedBy"/> (identity U) — групу
/// та anchor фіксує сканер при confirm, тож у самому claim-і їх немає. One-time: успішний confirm атомарно
/// виставляє <see cref="Consumed"/>=1 (<c>UPDATE … WHERE consumed=0</c>, rows-affected — патерн W20).
/// <see cref="CreatedBy"/> — identityId (opaque; ніколи не номер телефону); без FK (claim переживає до
/// consume незалежно від FK-станів, як <see cref="InviteRecord"/>).
/// </remarks>
/// <param name="CodeHash">At-rest <c>SHA-256(нормалізований код)</c>, base64 (primary key). Не секрет — код сам ≥96-bit секрет.</param>
/// <param name="CreatedBy">Identity id, що запросив claim (U); стане <c>identity_id</c> binding-у при confirm.</param>
/// <param name="Consumed">Чи вже погашено claim (one-time: після consume confirm відмовляє).</param>
/// <param name="ExpiresAt">Абсолютний термін дії (created + TTL); після нього confirm відмовляє.</param>
/// <param name="CreatedAt">Час видачі (UTC).</param>
public sealed record GroupClaimRecord(
    string CodeHash,
    string CreatedBy,
    bool Consumed,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt);
