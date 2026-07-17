namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A single entry in the tamper-evident audit-trail (add-invites-admin, task 3.2 / M8). Entries form a
/// hash-chain: each <see cref="EntryHash"/> commits to the previous entry's hash, so editing or deleting
/// any past record breaks the chain (detectable by <c>AuditLogService.VerifyChain</c>).
/// </summary>
/// <remarks>
/// <b>Hash-chain.</b> <see cref="EntryHash"/> = <c>SHA-256(prevHash ‖ seq ‖ eventType ‖ actorIdentity ‖
/// detailHash ‖ loggedAt)</c> (hex). Genesis <see cref="PrevHash"/> = 64 нулі. <see cref="Seq"/> —
/// монотонний (1-based), присвоюється сервісом і зберігається явно (не покладаємось на autoincrement для
/// pre-image, щоб verify був детермінований).
///
/// <b>Privacy.</b> Зберігаємо лише <see cref="DetailHash"/> = <c>SHA-256(деталей)</c> — самі деталі
/// (навіть ідентифікатори) не лежать у відкритому вигляді; у лог НІКОЛИ не потрапляють токени/номери
/// (CLAUDE rule #4). Окремий домен від abuse-логу (W24 HMAC) — це різні логи з різними цілями.
/// </remarks>
/// <param name="Seq">Монотонний порядковий номер запису (1-based, primary key).</param>
/// <param name="EventType">Тип security-події (напр. <c>invite_created</c>) — машиночитний тег, без PII.</param>
/// <param name="ActorIdentity">Identity id актора (opaque; ніколи не номер телефону).</param>
/// <param name="DetailHash">SHA-256 деталей події (hex) — самі деталі не зберігаються.</param>
/// <param name="PrevHash">Hash попереднього запису ланцюга (hex); genesis — 64 нулі.</param>
/// <param name="EntryHash">Hash цього запису (hex) — зобов'язання до всього ланцюга до нього включно.</param>
/// <param name="LoggedAt">Час запису події (UTC).</param>
public sealed record AuditLogRecord(
    long Seq,
    string EventType,
    string ActorIdentity,
    string DetailHash,
    string PrevHash,
    string EntryHash,
    DateTimeOffset LoggedAt);
