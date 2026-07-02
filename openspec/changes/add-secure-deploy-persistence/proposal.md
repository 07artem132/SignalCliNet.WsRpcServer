# add-secure-deploy-persistence

## Чому
Durable-стан (identity/token-хеші/прив'язки/device-секрети/бот-бюджет) — єдина копія;
in-process locks безглузді при double-start (G1); секрети в env течуть (`/proc`,
`docker inspect`, dump); LUKS у контейнері потребує `--privileged` — гірша безпека (R3.7).

## Що змінюється
- **At-rest = платформний encrypted volume** (KMS-disk / host dm-crypt), НЕ app-LUKS
  (рішення R3.7 — КАНОНІЧНЕ, supersede LUKS-формулювань табл./task-9/DoD старого плану; GAPS S2).
- **Auto-gen + persist-once на томі** (R3.4/R3.7): TLS CA + server-cert + admin-cert,
  `pepper_token`/`pepper_abuse` — CSPRNG на first-boot, `0600`, переюз на рестартах
  (fresh CA щоразу = зламаний client-trust); renewal прострочених cert.
- **G1 (keystone):** exclusive data-dir lockfile (`flock`/`O_EXCL`) → refuse-start;
  `replicas=1` hard-constraint; deploy = свідомий downtime-window (W15).
- **G4:** шифрований DB-backup НЕ на той самий том + документований restore;
  `PRAGMA user_version` + migration-runner; integrity-check на старті (corrupt → fail-start).
- **D4 startup fail-closed:** відмова стартувати, якщо `Host != loopback` і (auth вимкнено
  АБО TLS не сконфігуровано); `ValidateOnStart` + `[OptionsValidator]` для нової конфіги (A3).
- **Durable стор:** embedded DB (SQLite WAL + retry `SQLITE_BUSY`) на encrypted-томі;
  identity record `{id, role, linkedAccounts[], createdAt}`; стор прив'язок
  `account → owning-identity` + private-флаг (R3.5).
- D17: `LimitCORE=0`/`ulimit -c 0` для сервіс-юзера; dump-и не на незашифрований том.
- D3-trust: docs-розгалуження (домен → ACME; без домену → авто-CA + інструкція trust-store;
  «голий IP без CA» не підтримується).
- e2e-harness читає згенеровані секрети з тому; `test == prod` code-path, без no-auth-флагів
  (R3.7, зі scope-застереженням: e2e не доводить прод-експозицію/scale).

## Вплив
- Код: `Program.cs` startup (auto-gen, lockfile, assertions, валідатори), `Persistence/`,
  `deploy/*`.
- Specs: `secure-persistence` (нова capability).
- Прогалини: закриває S2 (єдине формулювання at-rest), S7-частини (D17, A3).
