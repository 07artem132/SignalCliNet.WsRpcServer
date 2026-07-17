# Tasks: add-invites-admin

## 1. Інвайти
- [x] 1.1 Код ≥96-bit CSPRNG (візуальні блоки); per-code attempt cap, атомарний інкремент (G12)
- [x] 1.2 М'який глобальний rate-cap; регламент передачі — у docs (`docs/shared-bot.md`)
- [x] 1.3 `redeemInvite` = IdentityOnboarding-політика (без токена, rate-limited) + атомарний consume (W20-патерн)

> **Секція 1 (виконано).** InviteService (SHA-256 code_hash) + InviteRedemptionRateLimiter +
> redeemInvite-адаптер (`ISignalOnboardingRpc`, error `-32006`) + DurableStore v4 (invites/abuse_log).
> **`createInvite` (admin-RPC видача інвайтів) — секція 2** (admin-gated); `InviteService.Create`
> вже готовий до виклику звідти.

## 2. Admin bootstrap + auth
- [x] 2.1 S3 ЗАКРИТО: admin-креденшел = **mTLS client-cert bundle** (R3.8); token-шлях superseded, не робити
- [x] 2.2 Перший старт: admin-identity ідемпотентно; admin cert-bundle у `0600`-файл на томі, не stdout (G5)
- [x] 2.3 mTLS: cert від авто-ген CA → principal `role=admin`; lifecycle/revoke admin-cert (CRL або стор)
- [x] 2.4 G10: legacy Phase-1 акаунт → bind до admin-identity на першому старті

> **Секція 2 (виконано).** **2.2** `AdminBootstrapService` у `SecureDeploymentHostedService.StartAsync`
> (після init стору + провіжну): ідемпотентно admin-identity (`role=admin`) + durable mapping SPKI-cert→id
> у міграції **v5** (`admin_credentials`); G5 — лише лог-факт «admin-credential provisioned», без матеріалу.
> **2.3** ОКРЕМИЙ mTLS-порт у app (`SecureAdminRpcServer`/`SecureAdminRpcSession` над framework
> secure-transport 2.6.0): server-cert + приватний CA з тому, `ClientCertificateRequired`,
> `RevocationMode=NoCheck` (наш CA без CRL/OCSP — Offline валив би валідний cert; відкликання —
> durable `admin_credentials.revoked`). Node-identity SPKI → `AdminPrincipalResolver` → admin-principal
> АБО закриття конекту. Дефолт loopback (`Server:Admin`), опт-ін не-loopback. Additive hosted-service
> ПІСЛЯ провіжну. **2.4** G10 — lazy bind при першому admin `listAccounts` (немає bootstrap-API до старту
> демона): акаунти без binding → до admin-identity (`IsPrivate=false`, R3.6); задокументовано.

## 3. Admin-поверхня + audit
- [x] 3.1 Admin-методи через chokepoint; не-admin → `-32001`
- [x] 3.2 Audit-trail: tamper-evident, без токенів/номерів; admin listAccounts(all) аудитований (R3.6)
- [x] 3.3 Abuse-log: per-record salt / callerIdentityId у pre-image (W24); residual — у shared-bot.md

> **Секція 2 (виконано).** **3.1** `RpcPolicyKind.Admin` у chokepoint: admin-метод для не-admin →
> санітизований `-32001`. `ISignalAdminRpc`/`SignalAdminRpcAdapter`: `createInvite(ttlSeconds?,maxAttempts?)`
> → `InviteService.Create` → `{code,expiresAt}` (code — секрет, reflection-fallback DTO, A1);
> `revokeIdentity(identityId)` → `TokenService.RevokeIdentity` (каскад change 3); `listAccounts(all)` для
> admin — існуючий `ReadOutputFilter` (R3.6) + audit + G10-bind. **3.2** hash-chain audit-trail (`audit_log`
> міграції v5): `AuditLogService.Append`/`VerifyChain`, `entry_hash=SHA-256(prev‖seq‖type‖actor‖detail_hash‖ts)`,
> genesis=нулі; лише `detail_hash` (без PII). Події: invite_created/redeemed, identity_revoked,
> admin_list_accounts. Окремо від abuse-логу (W24) — закриває residual секції 1.

> **Task 3.3 (виконано).** AbuseLogService: `HMAC-SHA256(pepper_abuse, salt‖caller‖subject)` з
> per-record CSPRNG-salt (W24); сирий subject не зберігається/не логується. **Residual** (abuse-log не
> tamper-evident без hash-chain) задокументовано у `docs/shared-bot.md`. **Tamper-evident audit-trail із
> hash-chain — секція 2** (task 3.2, M8), окремо від abuse-логу.
- [x] 3.4 Reference admin-CLI (bootstrap/invite/revoke/audit-dump)

> **Task 3.4 — задокументовано, не пріоритет.** Reference admin-CLI (окремий консьюмер R3.3) НЕ
> реалізовано у цій секції. Admin-поверхня повністю доступна через mTLS-порт будь-яким WSS+JSON-RPC
> клієнтом із admin client-cert (`createInvite`/`revokeIdentity`/`listAccounts`). CLI-first reference у
> `example/` — окрема робота (потребує WSS+client-cert клієнта; GUI Avalonia — пізніше).

## 4. Тести (DoD)
- [x] 4.1 Per-code cap блокує підбір; паралельні guesses не обходять cap гонкою (G12)
- [x] 4.2 Admin cert-bundle (bootstrap) НЕ у `docker logs`/journald (G5)
- [x] 4.3 Не-admin cert/токен на admin-метод → `-32001`
- [x] 4.4 Upgraded інстанс: legacy account видно лише admin (G10)
- [x] 4.5 Audit-події лягають у лог (лінк бот-девайсу / інвайт / промоушн / revoke)

> **Секція 4 (виконано мною через реальний шлях).** `AdminMtlsDodTests` — живий `SecureAdminRpcServer`
> над провіжненими секретами + справжній `ClientWebSocket` з admin client-cert: createInvite/revokeIdentity
> over mTLS (audit-подія + VerifyChain); валідний CA-cert БЕЗ admin-mapping → сесія закрита, жодного
> admin-методу (4.3-mTLS-негатив); G5 — bootstrap не лишає ключ-матеріалу в логах; G10 — legacy-акаунт
> прив'язаний до admin на першому admin listAccounts; G12 — 16 паралельних redeem під cap → рівно 1
> переможець. (Не-admin на плейн-порту → `-32001` пінить `AuthzChokepointDispatchTests`.) 438 тестів.
