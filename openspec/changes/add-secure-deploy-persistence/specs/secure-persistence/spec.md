# secure-persistence — delta

## ADDED Requirements

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

### Requirement: Auto-gen persist-once секретів
За відсутності явної конфігурації сервер SHALL згенерувати CA/cert/пеппери на
first-boot, персистити їх (`0600`) на encrypted-томі та переюзати на наступних
стартах; секрети SHALL NOT потрапляти в env, логи чи шари образу.

### Requirement: Відновлюваність durable-стану
Embedded DB SHALL мати integrity-check на старті (corrupt → fail-start),
версіоновану схему з migration-runner та документований backup/restore
на окремий том.
