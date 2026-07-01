# operations — delta

## ADDED Requirements

### Requirement: Реактивний анти-бан
Сервер SHALL детектити типізовані Signal-помилки (-4/-5/-6) на send-шляху,
паузити спільного бота на rate-limit/captcha та сурфейсити стан клієнту/ops.

#### Scenario: captcha від Signal
- **WHEN** signal-cli повертає proof-required/captcha
- **THEN** наступні send через бот отримують `-32005`/pause-стан; ретраїв «наосліп» немає

### Requirement: Idempotency send-шляху
Сервер SHALL приймати idempotency-key і дедуплікувати ретраї в межах TTL;
дубль SHALL NOT повторно декрементувати бюджет чи слати повторно.

### Requirement: Redact-дисципліна логів
Структуровані логи SHALL проходити redact-allowlist; токен-несні params, заголовки auth,
device-link URI, номери й тіла SHALL NOT потрапляти в лог-вихід (перевіряється тестом).

### Requirement: Метрики без PII
Метрики SHALL експортуватись лише на loopback/окремому auth-порту;
tag-keys — з allowlist без identity/recipient.
