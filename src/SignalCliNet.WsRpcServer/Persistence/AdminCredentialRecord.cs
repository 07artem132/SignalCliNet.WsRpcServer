namespace SignalCliNet.WsRpcServer.Persistence;

/// <summary>
/// A durable mapping <c>admin client-cert SPKI → admin identity</c> (add-invites-admin, task 2.2). The
/// mTLS admin port resolves the presented client certificate's SPKI-SHA-256 against this table to bind a
/// connection to the admin identity; a missing or <see cref="Revoked"/> row means "not an admin" (deny).
/// </summary>
/// <remarks>
/// Зберігаємо лише SPKI-SHA-256 сертифіката (hex) — не сам матеріал ключа (G5: жодного секрету у сторі).
/// SPKI стабільний при переоформленні cert-а з тим самим ключем, тож mapping переживає renewal leaf-а.
/// <see cref="Revoked"/> дає durable-важіль відкликання admin-креденшелу без CRL (task 2.3).
/// </remarks>
/// <param name="SpkiSha256">SHA-256 від <c>SubjectPublicKeyInfo</c> admin-cert-а (hex, primary key).</param>
/// <param name="IdentityId">Admin identity id, до якого прив'язаний cert (opaque; ніколи не номер).</param>
/// <param name="Revoked">Чи відкликано цей admin-креденшел (revoked ⇒ конект відхиляється).</param>
/// <param name="CreatedAt">Час першої прив'язки (UTC).</param>
public sealed record AdminCredentialRecord(
    string SpkiSha256,
    string IdentityId,
    bool Revoked,
    DateTimeOffset CreatedAt);
