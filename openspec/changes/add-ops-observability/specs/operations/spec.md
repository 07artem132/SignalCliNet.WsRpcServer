# operations — delta

## ADDED Requirements

### Requirement: Реактивний анти-бан
Сервер SHALL детектити типізовані Signal-помилки (-4/-5/-6) на send-шляху,
паузити спільного бота на rate-limit/captcha та сурфейсити стан клієнту/ops.

#### Scenario: captcha від Signal
- **WHEN** signal-cli повертає proof-required/captcha
- **THEN** наступні send через бот отримують `-32005`/pause-стан; ретраїв «наосліп» немає

### Requirement: Idempotency send-шляху
Сервер SHALL приймати idempotency-key (`messageId`) і дедуплікувати ретраї в межах TTL;
дубль SHALL NOT повторно декрементувати бюджет чи слати повторно, а SHALL повертати
оригінальний результат.

#### Scenario: дубль-ретрай у межах TTL
- **WHEN** клієнт повторює `sendTextMessage` із тим самим idempotency-key у межах TTL
- **THEN** фактичний send і декремент бюджету відбуваються рівно раз; повтор отримує
  збережений результат першого виклику

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

### Requirement: Метрики без PII
Метрики SHALL експортуватись лише на loopback/окремому auth-порту;
tag-keys — з allowlist без identity/recipient.
