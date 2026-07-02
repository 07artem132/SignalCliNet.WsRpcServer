# admission-control — delta

## ADDED Requirements

### Requirement: Єдине admission-правило
Кожен send SHALL проходити одну впорядковану admission-функцію
(global_pause → per-user quota → new-recipient window → aggregate budget)
із short-circuit на першому deny; лімітери НЕ приймають рішення незалежно.

### Requirement: Анти-бан бюджет без over-send
Декремент агрегатного бюджету SHALL бути атомарним (RMW під lock або умовний UPDATE);
durable-резервація SHALL передувати send на гранулярності блоку K; після краху/рестарту
невитрачений залишок блоку SHALL форфейтитись (недосил допустимий, over-send — ні).

#### Scenario: конкурентний бюджет
- **WHEN** N паралельних send при бюджеті = 1
- **THEN** рівно один проходить, N-1 отримують `-32005`/`4429`

#### Scenario: краш між резервацією і send
- **WHEN** процес падає після durable-резервації блоку
- **THEN** після рестарту залишок блоку не витрачається повторно

### Requirement: Fairness під агрегатом
Per-user квота SHALL мати гарантований floor під агрегатним бюджетом; вичерпання
агрегату одним користувачем SHALL NOT блокувати floor інших.

### Requirement: In-band retry-контракт
Відмова за лімітом SHALL нести `retry_after` (секунди) каналом, який читає WebSocket-клієнт
(JSON-RPC error data або close-reason `4429`); HTTP-заголовки до апгрейду не є контрактом.
