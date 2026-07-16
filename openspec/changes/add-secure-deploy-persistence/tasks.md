# Tasks: add-secure-deploy-persistence

## 1. Startup-гарантії
- [x] 1.1 G1: data-dir lockfile → refuse-start при held; `replicas=1` у compose/docs; downtime-window задокументований (W15)
- [x] 1.2 D4: startup-assertion (не-loopback + (no-auth АБО no-TLS) → відмова стартувати)
- [x] 1.3 A3: `ValidateOnStart` + `[OptionsValidator]` для auth/TLS/rate-limit конфіги
- [x] 1.4 D17: `LimitCORE=0`; секрети не потрапляють у dump-шляхи

## 2. Auto-gen секретів (R3.4/R3.7)
- [x] 2.1 First-boot: CA + server-cert + пеппери → encrypted-том, `0600`, persist-once
- [x] 2.2 Рестарт переюзає той самий CA (тест: client-trust не ламається)
- [x] 2.3 Renewal прострочених cert на старті; CA — довший термін
- [x] 2.4 BuildKit `--secret` для feed-токена (не в шарах образу)

## 3. Durable стор
- [x] 3.1 Embedded DB (WAL, retry `SQLITE_BUSY`) на томі: identity/token/прив'язки/device-секрети/бюджет
- [x] 3.2 Стор `account → owning-identity` + private-флаг (R3.5)
- [x] 3.3 G4: backup (шифрований, інший том) + restore-док + `user_version`/migration + integrity-check на старті

## 4. Транспорт
- [ ] 4.1 Kestrel wss TLS 1.3 (1.2 floor) + HSTS; приватний ключ `0600` на томі
- [ ] 4.2 D5/U1: L4-передумова публічного біндингу задокументована (nftables/SG)
- [ ] 4.3 D3-trust docs: ACME / internal-CA trust-store; «голий IP без CA» не підтримується

## 5. Тести (DoD)
- [ ] 5.1 Другий процес на тому ж data-dir → refuse-start (G1)
- [ ] 5.2 Чистий `docker compose up` без секрет-env → робочий wss + згенеровані секрети; нуль секретів у env/logs/шарах (R3.7)
- [ ] 5.3 Durable survive-restart: identity/token/прив'язки/бюджет валідні після рестарту
- [ ] 5.4 Corrupt DB → fail-start; restore з backup працює; migration проходить (G4)
