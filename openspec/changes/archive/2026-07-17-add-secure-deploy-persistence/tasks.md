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
      *(звуження за T1: у цьому change — двигун (WAL/retry/міграції/integrity/backup) + таблиці identity/bindings;
      таблиці token/device-секретів (authn) та бюджету (admission) додають їхні changes міграціями v2+ —
      кожен споживач вносить свій артефакт у СПІЛЬНУ схему через той самий migration-runner)*
- [x] 3.2 Стор `account → owning-identity` + private-флаг (R3.5)
- [x] 3.3 G4: backup (шифрований, інший том) + restore-док + `user_version`/migration + integrity-check на старті

## 4. Транспорт
- [x] 4.1 Kestrel wss TLS 1.3 (1.2 floor) + HSTS; приватний ключ `0600` на томі
- [x] 4.2 D5/U1: L4-передумова публічного біндингу задокументована (nftables/SG)
- [x] 4.3 D3-trust docs: ACME / internal-CA trust-store; «голий IP без CA» не підтримується

## 5. Тести (DoD)
- [x] 5.1 Другий процес на тому ж data-dir → refuse-start (G1)
      *(DoD-тест через реальний шлях: повний Host + AddSecureDeployment — `SecureDeploymentHostStartupTests`)*
- [x] 5.2 Чистий `docker compose up` без секрет-env → робочий wss + згенеровані секрети; нуль секретів у env/logs/шарах (R3.7)
      *(перевірено наживо 2026-07-16: ізольований compose-проєкт, свіжий том, from-source образ;
      TLS1.3 + strict-verify (python 3.14) + JSON-RPC ping/handshake через wss-proxy OK; секрети 0600/0700;
      env/логи/шари — нуль секретів. Знайдений і виправлений interop-баг: leaf-cert без AKI →
      VERIFY_X509_STRICT (python 3.13+) відхиляв ланцюг; додано AKI + self-heal переоформлення старих leaf-ів)*
- [x] 5.3 Durable survive-restart: identity/token/прив'язки/бюджет валідні після рестарту
      *(звуження за T1 як у 3.1: пінимо identity/bindings + CA-reuse; token/бюджет — у їхніх changes)*
- [x] 5.4 Corrupt DB → fail-start; restore з backup працює; migration проходить (G4)
