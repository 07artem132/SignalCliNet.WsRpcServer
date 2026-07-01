# Tasks: add-invites-admin

## 1. Інвайти
- [ ] 1.1 Код ≥96-bit CSPRNG (візуальні блоки); per-code attempt cap, атомарний інкремент (G12)
- [ ] 1.2 М'який глобальний rate-cap; регламент передачі — у docs
- [ ] 1.3 `redeemInvite` = IdentityOnboarding-політика (без токена, rate-limited) + атомарний consume (W20-патерн)

## 2. Admin bootstrap + auth
- [ ] 2.1 РІШЕННЯ S3: єдина історія admin-креденшела (mTLS-cert R3.8 vs one-time token G5/R3.7)
- [ ] 2.2 Перший старт: admin-identity ідемпотентно; креденшел у `0600`-файл на томі, не stdout (G5)
- [ ] 2.3 mTLS: cert від авто-ген CA → principal `role=admin`; lifecycle/revoke admin-cert (CRL або стор)
- [ ] 2.4 G10: legacy Phase-1 акаунт → bind до admin-identity на першому старті

## 3. Admin-поверхня + audit
- [ ] 3.1 Admin-методи через chokepoint; не-admin → `-32001`
- [ ] 3.2 Audit-trail: tamper-evident, без токенів/номерів; admin listAccounts(all) аудитований (R3.6)
- [ ] 3.3 Abuse-log: per-record salt / callerIdentityId у pre-image (W24); residual — у shared-bot.md
- [ ] 3.4 Reference admin-CLI (bootstrap/invite/revoke/audit-dump)

## 4. Тести (DoD)
- [ ] 4.1 Per-code cap блокує підбір; паралельні guesses не обходять cap гонкою (G12)
- [ ] 4.2 Admin one-time креденшел НЕ у `docker logs`/journald (G5)
- [ ] 4.3 Не-admin cert/токен на admin-метод → `-32001`
- [ ] 4.4 Upgraded інстанс: legacy account видно лише admin (G10)
- [ ] 4.5 Audit-події лягають у лог (лінк бот-девайсу / інвайт / промоушн / revoke)
