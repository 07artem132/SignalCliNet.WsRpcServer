# admission-control Specification

## Purpose
TBD - created by archiving change add-admission-rate-limits. Update Purpose after archive.
## Requirements
### Requirement: Єдине admission-правило
Кожен send SHALL проходити одну впорядковану admission-функцію
(global_pause → per-user quota → new-recipient window → aggregate budget)
із short-circuit на першому deny; лімітери НЕ приймають рішення незалежно.
За наявності idempotency-шару (`add-ops-observability`, T6) dedup-перевірка SHALL
передувати всьому ланцюжку: hit по завершеному idempotency-key повертає збережений
результат без admission і без декременту бюджету.

#### Scenario: short-circuit на першому deny
- **WHEN** send не проходить global_pause (перший крок ланцюжка)
- **THEN** admission відмовляє одразу; per-user quota, new-recipient window і aggregate budget не перевіряються (W16)

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

#### Scenario: один юзер вичерпав агрегат
- **WHEN** користувач A вичерпує агрегатний бюджет власними send
- **THEN** користувач B досі отримує гарантований per-user floor — вичерпання A не голодоморить B (G2)

### Requirement: In-band retry-контракт
Відмова за лімітом SHALL нести `retry_after` (секунди) каналом, який читає WebSocket-клієнт
(JSON-RPC error data або close-reason `4429`); HTTP-заголовки до апгрейду не є контрактом.

#### Scenario: відмова за лімітом через JSON-RPC error
- **WHEN** send відхилено лімітером після встановленого WS-з'єднання
- **THEN** клієнт отримує помилку `-32005` з `data.retry_after` (секунди) або закриття `4429` — retry_after доступний in-band, без HTTP-заголовків

