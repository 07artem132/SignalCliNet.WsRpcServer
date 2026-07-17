# Журнал змін

Усі помітні зміни цього проєкту документуються в цьому файлі.

Формат базується на [Keep a Changelog](https://keepachangelog.com/uk/1.1.0/),
а нумерація версій дотримується [семантичного версіонування](https://semver.org/lang/uk/).

## [2.0.1] — 2026-07-17

### Added

- **Admin per-binding revoke** — `revokeBinding(identityId, bindingId)` (admin RPC, mTLS-порт):
  хірургічно анулює ОДИН group-binding чужої identity (на відміну від `revokeIdentity`, що нукає всю
  identity). Ownership-guard: binding мусить належати вказаній identity, інакше `InvalidParams`
  (анти-oracle); подія `admin_binding_revoked` у tamper-evident audit-trail. Закриває опційний
  пункт `add-invites-admin` task 2.4.

## [2.0.0] — 2026-07-17

Хвиля безпеки Фази 2/3: із loopback-only мосту (Фаза 1) сервер стає придатним для
**shared-bot** розгортання (кілька identity за одним привʼязаним номером) — з default-deny
авторизацією, автентифікацією, admission-контролем та durable-станом на шифрованому томі.

> **Сумісність за замовчуванням.** Усі нові поверхні **gated і вимкнені за замовчуванням**
> (`Server:Auth:Enabled`, `Server:Admission:Enabled`, `Server:GroupClaim:Enabled` = `false`).
> За дефолтної конфігурації поведінка Фази 1 — **loopback, full-access — незмінна**.
> Винятки: `Server:Heartbeat` (адитивні нотіфи, увімкнено) і mTLS admin-порт `Server:Admin`
> (loopback-only `127.0.0.1:9443`). Про увімкнення shared-bot-фіч — `docs/self-host.md`,
> `docs/shared-bot.md`, `deploy/DEPLOYMENT.md`.

### Added

- **Авторизація — default-deny chokepoint** (`add-authz-chokepoint`, capability `authz-isolation`).
  Pre-dispatch фільтр `AuthorizingSignalJsonRpc` із декларативним реєстром політик
  (`Public` / `IdentityOnboarding` / `AccountCommand` / `AccountQuery`); метод без явної політики →
  `-32601` ще до виконання. `SignalPrincipal` виводить набір дозволених акаунтів з identity, ніколи
  з аргументів (анти-IDOR); outbound `ReadOutputFilter` fail-closed (виняток → error, ніколи raw);
  `RecipientValidator` (нормалізація E.164/group-ID на chokepoint); `RpcErrorSanitizer` (generic-текст
  клієнту, деталі — лише в серверний лог); `apiVersion` + capability-negotiation у handshake.
- **Secure-deploy та durable-persistence** (`add-secure-deploy-persistence`, capability `secure-persistence`).
  G1 exclusive data-dir lockfile (другий процес на тому ж томі не стартує); авто-генерація й
  persist-once (CSPRNG, `0600`) TLS CA + server-cert + admin-cert + пепперів на first-boot; durable
  SQLite-стор (WAL, `integrity_check` на старті, `PRAGMA user_version` міграції, шифрований
  backup/restore); D4 fail-closed startup (відмова стартувати на не-loopback без auth/TLS,
  `[OptionsValidator]` + `ValidateOnStart`); публічна експозиція — через reverse-proxy з TLS 1.3.
- **Автентифікація — opaque-токен + Proof-of-Possession** (`add-authn-token-pop`, capability `authn`).
  Opaque bearer `ptzh_<pepperVer>_…_<checksum>` (≥256-bit CSPRNG; at-rest `HMAC-SHA256` з окремим
  пеппером; серверний стор — єдине джерело правди, жодних claims у токені); PoP device-ключем
  ECDSA P-256 (nonce bound до conn-id) раз на зʼєднання; bearer у handshake через
  `Sec-WebSocket-Protocol`-субпротокол + fallback `authenticate`; in-band close `4401`
  (`invalid`/`expired`/`revoked`) та `4408` (auth-timeout); T5 token-renewal через PoP;
  enrollment device-pubkey (TOFU під one-time інвайтом).
- **Admission-контроль і rate-limits** (`add-admission-rate-limits`, capability `admission-control`).
  Єдина admission-функція на chokepoint (global-pause → per-user floor → new-recipient window →
  aggregate budget, short-circuit на першому deny); бот-бюджет reserve-then-send із durable-резервацією
  блоками (zero over-send, форфейт залишку на рестарті); per-connection msg-rate й in-flight cap,
  per-identity connection cap, idle-timeout, server-wide gate до signal-cli; відмови **in-band**
  `-32005` (`retry_after`) / WS close `4429`.
- **Link-сесії** (`add-link-sessions`, capability `account-linking`). Link-session state-machine
  (256-bit `sessionId`, TTL 120с, one-time, rate-limit 3/хв/identity); атомарний anti-griefing consume
  (гашення лише якщо identity ініціатора збіглася); takeover-захист (target = спільний бот → лише
  admin); приватний bind нового власного номера до caller (R3.5).
- **Інвайти та admin-поверхня** (`add-invites-admin`, capability `admin-and-invites`). Інвайти
  (≥96-bit код, per-code attempt cap G12 з атомарним інкрементом); admin bootstrap (перша identity →
  `role=admin`, ідемпотентно; pre-existing Phase-1 акаунт bind-иться до admin, не orphan);
  **окремий mTLS admin-порт** (client-cert = креденшел, дефолт `wss://127.0.0.1:9443`);
  admin-поверхня (`createInvite` / `revokeIdentity` каскад / `listAccounts` all — read-only нагляд);
  tamper-evident audit hash-chain (без токенів/номерів). Reference-клієнт — `example/AdminCli`.
- **Group-claim та per-account receive** (`add-group-claim-receive`, capability `group-claim`).
  Per-account receive-роутер (S11, R3.5-ізоляція: own-linked incoming → лише власнику, shared-bot-потік →
  лише сканеру); group-claim флоу (`requestGroupClaim` → one-time код TTL ~10 хв → вставлення в чат →
  атомарний consume-and-bind; anchor-модель T2: перевірка `anchorAci ∈ members(G)` на кожен send);
  безперервний авто-receive за прапорцем; send-gate групових повідомлень по активному binding.
- **Ops та observability** (`add-ops-observability`, capability `operations`). App-`Meter`
  (`AppDiagnostics`, tag-allowlist без identity/recipient); V10 catch-матриця
  (`RateLimitException` → `-5`, `CaptchaRequiredException` → `-6`, `UntrustedIdentityException` → `-4`,
  + non-RPC Timeout/OperationCanceled/…); реактивний global-pause бота на `-5`/`-6` (живить
  admission-gate); server-heartbeat нотіфи `{seq, timestamp, paused}` `<25с`; durable idempotency-dedup
  (ключ `(identityId, messageId)`, канон **at-most-once**, dedup-lookup ПЕРЕД admission-функцією).

### Changed

#### ⚠️ BREAKING (wire-протокол)

- **`finishLink`** тепер приймає `{ sessionId, deviceName }` замість `{ deviceLinkUri, deviceName }`:
  клієнт передає one-time `sessionId`, отриманий зі `startLink`, а **не** сирий device-link URI. Сирий
  URI живе лише в серверному сторі (in-memory, TTL 120с) і ніколи не логується.
- **`startLink`** тепер повертає `{ sessionId, deviceLinkUri }` (раніше — лише `{ deviceLinkUri }`) і
  приймає опційний `targetAccount` (takeover-guard: лінк на вже привʼязаний спільний бот → лише admin).
- Handshake несе клієнтський **`apiVersion`**; version-mismatch → типізована «upgrade required»,
  а не тихий `-32601` чи мовчазний behavioral-break.

#### Інше

- Усі нові фічі gated і **вимкнені за замовчуванням** (`Server:Auth`, `Server:Admission`,
  `Server:GroupClaim` = `Enabled:false`) — Фаза-1 loopback-full-access за дефолту незмінна. Винятки:
  `Server:Heartbeat:Enabled = true` (адитивні нотіфи, send-поведінка незмінна) і mTLS admin-порт
  `Server:Admin:Enabled = true`, обмежений loopback (`127.0.0.1:9443`, client-cert).
- `Server:MaxMessageSizeBytes` дефолт — **64KB** (замість framework-default 100MB; менша стеля пам'яті
  на кадр, кадр понад ліміт → close `1009`).
- Durable-стан живе у `Persistence:DataDirectory` (дефолт `data/`, у контейнері `/data`) і **МУСИТЬ**
  бути на платформно-шифрованому томі (R3.7) — at-rest шифрування є відповідальністю платформи, не застосунку.
- Секрети більше не передаються через env — авто-провіжняться на томі (`0600`, persist-once на first-boot).

### Dependencies

- **SignalCli.NET** 4.10.1 → **4.10.2**.
- **JSON-RPC.NET** 2.7.0 → **2.8.0**.
- Додано (durable-стор, токен-checksum, options-валідатори): `Microsoft.Data.Sqlite` 10.0.10,
  `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 (explicit override транзитиву — GHSA-2m69-gcr7-jv3q),
  `System.IO.Hashing` 10.0.10, `Microsoft.Extensions.Options` / `Microsoft.Extensions.Configuration.Binder` 10.0.9.
- **SignalCli.Runtime** 0.14.3.1 і вимога **JDK 25** (signal-cli 0.14.3) — без змін.

## [1.1.0] — Фаза 1 (baseline, as-built)

Базовий стан до хвилі безпеки: loopback-only WebSocket JSON-RPC 2.0 міст між **SignalCli.NET**
(supervised `signal-cli`) і клієнтами. Керування акаунтами/групами, лінкування пристрою
(`startLink`/`finishLink` зі старою `deviceLinkUri`-сигнатурою), надсилання text/attachment/sticker,
підписка на події, health-check і авто-рестарт `signal-cli`. Bind — тільки `127.0.0.1` (D4);
без auth/admission/persistence-шарів (див. `## [2.0.0]`).

[2.0.0]: https://github.com/07artem132/SignalCliNet.WsRpcServer/releases/tag/v2.0.0
[1.1.0]: https://github.com/07artem132/SignalCliNet.WsRpcServer/releases/tag/v1.1.0
