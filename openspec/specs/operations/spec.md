# operations Specification

## Purpose
TBD - created by archiving change add-ops-observability. Update Purpose after archive.
## Requirements
### Requirement: Реактивний анти-бан
Сервер SHALL детектити типізовані Signal-помилки (-4/-5/-6) на send-шляху,
паузити спільного бота на rate-limit/captcha та сурфейсити стан клієнту/ops.

#### Scenario: captcha від Signal
- **WHEN** signal-cli повертає proof-required/captcha
- **THEN** наступні send через бот отримують `-32005`/pause-стан; ретраїв «наосліп» немає

### Requirement: Idempotency send-шляху
Сервер SHALL приймати idempotency-key (`messageId`) і дедуплікувати ретраї в межах TTL;
дубль SHALL NOT повторно декрементувати бюджет чи слати повторно, а SHALL повертати
оригінальний результат. Dedup-стор SHALL бути durable (та сама SQLite) з ключем
`(identityId, messageId)` — дедуплікація переживає рестарт і не колізіює між тенантами (T6).
Запис `pending` і декремент бюджету SHALL відбуватись однією транзакцією ДО send;
після send запис SHALL переходити у `done` з результатом. Канон при краші — **at-most-once**:
ретрай, що знаходить `pending`, SHALL отримувати типізовану помилку «outcome unknown»
і SHALL NOT тригерити авто-повтор (over-send заборонений; недосил допустимий — симетрія W13).
Dedup-lookup SHALL передувати admission-функції.

#### Scenario: дубль-ретрай у межах TTL
- **WHEN** клієнт повторює `sendTextMessage` із тим самим idempotency-key у межах TTL
  (зокрема після рестарту сервера)
- **THEN** фактичний send і декремент бюджету відбуваються рівно раз; повтор отримує
  збережений результат першого виклику без проходження admission

#### Scenario: краш між pending і send
- **WHEN** процес падає після транзакції `pending`+декремент, але до реального send
- **THEN** ретрай після рестарту отримує «outcome unknown»; send не виконується повторно,
  бюджет не декрементується вдруге (форфейт, як W13)

### Requirement: Server-heartbeat (persistent-WS)
Для persistent-WS сервер SHALL слати app-level heartbeat-нотифікацію з інтервалом <25с,
щоб ефемерні клієнти (MV3 Service Worker) тримали з'єднання й відрізняли живий сокет
від завислого (C5-серверна половина R3.3). За connect-on-demand MVP heartbeat не потрібен.

#### Scenario: idle persistent-з'єднання
- **WHEN** persistent-WS простоює без RPC-трафіку
- **THEN** клієнт отримує heartbeat раніше ніж за 25с; відсутність кількох поспіль → реконект

### Requirement: Redact-дисципліна логів
Структуровані логи SHALL проходити redact-allowlist; токен-несні params, заголовки auth,
device-link URI, номери й тіла SHALL NOT потрапляти в лог-вихід (перевіряється тестом).

#### Scenario: спроба залогувати чутливе поле
- **WHEN** будь-який лог-виклик намагається записати токен-несний param, auth-заголовок, device-link URI, номер чи тіло повідомлення
- **THEN** redact-allowlist відфільтровує значення до запису — у лог-виході цих даних немає (перевіряється тестом)

### Requirement: Метрики без PII
Метрики SHALL експортуватись лише на loopback/окремому auth-порту;
tag-keys — з allowlist без identity/recipient.

#### Scenario: метрика з недозволеним тегом
- **WHEN** код намагається експортувати метрику з tag-key поза allowlist (напр. identity чи recipient)
- **THEN** метрика експортується лише на loopback/auth-порту, і недозволений tag-key відкидається/відхиляється — identity/recipient не потрапляють у теги

