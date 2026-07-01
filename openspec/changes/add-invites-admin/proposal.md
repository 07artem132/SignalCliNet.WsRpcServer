# add-invites-admin

## Чому
Sybil-гейт (хто взагалі юзає бота) — інвайти; перша identity — bootstrap-admin;
адмін-операції потребують власного auth-шляху та аудиту.

## Що змінюється
- **Інвайти:** код ≥96-bit CSPRNG; per-code attempt cap з атомарним інкрементом (G12);
  м'який глобальний rate-cap (не жорсткий → shared-fate DoS); видача через admin-RPC/CLI.
- **Admin bootstrap:** перший старт призначає identity `role=admin` ідемпотентно; креденшел —
  у `0600`-файл на encrypted-томі, НІКОЛИ stdout (G5: stdout у контейнері = `docker logs`);
  G10: pre-existing Phase-1 акаунт bind-иться до admin-identity (не orphan).
- **Admin auth = mTLS client-cert** (рішення R3.8): cert від авто-ген CA сервера (R3.4) →
  `NodeIdentity` → `role=admin`; token/PoP-стек адміну не потрібен. ⚠ УЗГОДИТИ з
  «admin-token» формулюваннями G5/R3.7 — одна історія креденшела (GAPS S3).
- **Admin-поверхня** через той самий chokepoint (R3.8): `listAccounts(all)` (R3.6, read-only
  нагляд), identities (promote/revoke-каскад), invites, group-claims/abuse-log, destructive ops
  (за `EnableDestructiveOperations`); не-admin на admin-метод → `-32001`.
- **Audit-trail (M8):** tamper-evident лог security-подій (лінк бот-девайсу, redeem інвайту,
  промоушн admin, revoke, admin listAccounts(all)); окремо від abuse-логу; без токенів/номерів.
- **Abuse-log:** домен-розділений `HMAC(pepper_abuse, …)`; додати per-record salt або
  callerIdentityId у pre-image — інакше equality-oracle на спільному боті (W24, GAPS S7).
- CLI-first admin-клієнт (reference у `example/`, окремий консьюмер R3.3); GUI (Avalonia) — пізніше.

## Вплив
- Specs: `admin-and-invites` (нова capability).
- Прогалини: закриває S3 (після узгодження), S7-частину (W24).
