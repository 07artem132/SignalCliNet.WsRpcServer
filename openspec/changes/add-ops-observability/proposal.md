# add-ops-observability

## Чому
Production-готовність: метрики, структуровані логи з redact-дисципліною, fail-path
(вкл. реактивний Signal-rate-limit — анти-бан), клієнтський контракт, CI/publish.

## Що змінюється
- **Метрики:** `WsRpcServerDiagnostics` (2.7.0 — доступний після task-0); експозиція лише
  loopback/окремий порт за auth; tag-keys allowlist без identity/recipient (D13).
- **Логи:** таксономія помилок (`RpcErrorException`); redact-allowlist: `authenticate`-params,
  `Sec-WebSocket-Protocol`/`Authorization`, device-link URI, номери, тіла.
- **Fail-path:** catch-матриця V10 (`RateLimitException` -5, `CaptchaRequiredException` -6,
  `UntrustedIdentityException` -4 + non-RPC: Timeout/OperationCanceled/ObjectDisposed/
  InvalidOperation; НЕ ловити obsolete `IdentityChangedException`); global-pause бота на
  -5/-6 (живить admission-gate 1); restart-cancel відрізняти від client-cancel (W10);
  ops-докс: health-restart рве in-flight усіх тенантів (V11).
- **Клієнтський контракт (docs):** connect-on-demand + burst-reuse (W14); jittered backoff +
  одиниці retry_after (G13); server-heartbeat <25с для persistent-WS (C5-серверна);
  idempotency-key/`messageId` (C2-серверна — ⚠ потребує implementation-task, GAPS S8);
  wake→connect латентність необмежена (C6).
- **Perf-gate** перед оптимізацією IPC: load-test; budget-persist 1 запис/K send (N3/G11).
- **(Опційно) Receive/events RPC-surface** (`ISignalEventsRpc`, W5-асиметрія discovery);
  receive-loop уже є з `add-group-claim-receive`.
- **CI:** + docker publish.

## Вплив
- Specs: `operations` (нова capability).
- Прогалини: закриває S7-частини (V10/W10 DoD — N5), S8 (якщо додати idempotency-task).
