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
- [ ] 2.2 Перший старт: admin-identity ідемпотентно; admin cert-bundle у `0600`-файл на томі, не stdout (G5)
- [ ] 2.3 mTLS: cert від авто-ген CA → principal `role=admin`; lifecycle/revoke admin-cert (CRL або стор)
- [ ] 2.4 G10: legacy Phase-1 акаунт → bind до admin-identity на першому старті

## 3. Admin-поверхня + audit
- [ ] 3.1 Admin-методи через chokepoint; не-admin → `-32001`
- [ ] 3.2 Audit-trail: tamper-evident, без токенів/номерів; admin listAccounts(all) аудитований (R3.6)
- [x] 3.3 Abuse-log: per-record salt / callerIdentityId у pre-image (W24); residual — у shared-bot.md

> **Task 3.3 (виконано).** AbuseLogService: `HMAC-SHA256(pepper_abuse, salt‖caller‖subject)` з
> per-record CSPRNG-salt (W24); сирий subject не зберігається/не логується. **Residual** (abuse-log не
> tamper-evident без hash-chain) задокументовано у `docs/shared-bot.md`. **Tamper-evident audit-trail із
> hash-chain — секція 2** (task 3.2, M8), окремо від abuse-логу.
- [ ] 3.4 Reference admin-CLI (bootstrap/invite/revoke/audit-dump)

## 4. Тести (DoD)
- [ ] 4.1 Per-code cap блокує підбір; паралельні guesses не обходять cap гонкою (G12)
- [ ] 4.2 Admin cert-bundle (bootstrap) НЕ у `docker logs`/journald (G5)
- [ ] 4.3 Не-admin cert/токен на admin-метод → `-32001`
- [ ] 4.4 Upgraded інстанс: legacy account видно лише admin (G10)
- [ ] 4.5 Audit-події лягають у лог (лінк бот-девайсу / інвайт / промоушн / revoke)
