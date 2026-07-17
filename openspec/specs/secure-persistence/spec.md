# secure-persistence Specification

## Purpose
TBD - created by archiving change add-secure-deploy-persistence. Update Purpose after archive.
## Requirements
### Requirement: Single-instance інваріант
Сервер SHALL захоплювати exclusive lockfile на data-dir при старті і відмовлятись
стартувати, якщо lock утримується іншим процесом.

#### Scenario: double-start
- **WHEN** другий процес стартує на тому ж data-dir
- **THEN** він завершується з помилкою, не торкнувшись БД/бюджету

### Requirement: Fail-closed startup
Сервер SHALL відмовлятись стартувати при небезпечній конфігурації:
не-loopback bind без увімкненого auth ТА валідного TLS; конфіг-опції валідуються
на старті (`ValidateOnStart`).

#### Scenario: non-loopback bind без TLS
- **WHEN** сервер конфігурується на не-loopback адресу без увімкненого auth і без валідного TLS (D4)
- **THEN** старт відмовляє одразу на `ValidateOnStart`, сервіс не піднімається

### Requirement: Auto-gen persist-once секретів
За відсутності явної конфігурації сервер SHALL згенерувати CA/cert/пеппери на
first-boot, персистити їх (`0600`) на encrypted-томі та переюзати на наступних
стартах; секрети SHALL NOT потрапляти в env, логи чи шари образу.

#### Scenario: first-boot і рестарт
- **WHEN** сервер стартує вперше без явно заданих секретів
- **THEN** він генерує CA/cert/пеппери, персистить їх `0600` на encrypted-томі; наступний рестарт переюзає ті самі секрети без повторної генерації

### Requirement: Відновлюваність durable-стану
Embedded DB SHALL мати integrity-check на старті (corrupt → fail-start),
версіоновану схему з migration-runner та документований backup/restore
на окремий том.

#### Scenario: пошкоджена БД на старті
- **WHEN** integrity-check виявляє пошкоджену embedded DB на старті
- **THEN** сервер відмовляє стартувати замість роботи з пошкодженими даними

#### Scenario: відновлення з backup
- **WHEN** оператор відновлює том даних із документованого backup
- **THEN** сервер стартує з відновленого стану, проходить integrity-check і migration-runner без ручного втручання

