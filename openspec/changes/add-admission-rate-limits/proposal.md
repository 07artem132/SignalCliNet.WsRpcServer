# add-admission-rate-limits

## Чому
Фреймворк дає лише глобальний `MaxConcurrentConnections`; per-token/per-IP cap, msg-rate,
auth/idle-timeout, бот-бюджет — net-new (звірка №5). Чотири лімітери без єдиного правила
голодоморять одне одного (W16); декремент бюджету без атомарності = over-send → бан (G3).

## Що змінюється
- **Єдина admission-функція на chokepoint** (рішення W16), short-circuit на першому deny:
  global_pause → per-user quota (floor, G2) → new-recipient window (D2) → aggregate budget.
- **Бот-бюджет: reserve-then-send** (рішення W13): race-safety — in-memory атомарний лічильник;
  crash-safety — durable-резервація блоками K; невитрачений залишок форфейтиться на рестарті
  (нуль over-send, знімає G11-bottleneck).
- Лічильник унікальних НОВИХ отримувачів/вікно (D2 — Signal банить за first-contact fan-out,
  не обсяг).
- Per-connection in-flight cap (D12) + server-wide semaphore до signal-cli (G6 —
  `_pendingRequests` upstream без cap).
- `MaxMessageSizeBytes` → 64KB (V3); frame-assembly-тріада — НЕ app-side, accepted-risk
  D5 + зовнішній L4 (рішення U1); permessage-deflate відсутній → zip-bomb неможливий (V6).
- Перевищення: `-32005` + `data.retry_after` / WS `4429`; HTTP 429 — лише до апгрейду
  (⚠ невидимий браузерному клієнту — GAPS S9: retry_after МУСИТЬ бути in-band).
- Refill/вікна — на монотонному годиннику (D15); wall-clock лише для `ExpiresAt`.

## Вплив
- Код: `Security/AdmissionControl`, budget store (durable), декоратор над `ISignalCliClient` (G6).
- Specs: `admission-control` (нова capability).
- Прогалини: закриває S6 (D15 без власника), S9 (in-band retry_after); фіксує S5
  (frame-тріада винесена з app-задач — єдине канонічне формулювання).
