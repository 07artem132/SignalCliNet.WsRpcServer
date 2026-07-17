# Tasks: add-ops-observability

## 1. Метрики/логи
- [x] 1.1 App-level `AppDiagnostics` Meter (окремо від фреймворкового); tag-allowlist `{result}` (D13); privacy-guard тест
- [x] 1.2 Таксономія помилок (catch-матриця) + redact: санітизовані коди БЕЗ PII (тест мапінгу/лог-виходу)

## 2. Fail-path
- [x] 2.1 Catch-матриця V10 (`UpstreamSendFailureMapper`: типізовані -4/-5/-6/base + non-RPC cancel/IO/timeout/disposed; без obsolete-типів)
- [x] 2.2 Реальний `GlobalPauseGate` (замінює Noop у DI); global-pause на -5/-6; стан сурфейситься (bot.paused/resumed + метрика + heartbeat)
- [x] 2.3 W10: restart-cancel → `-32009` (окремо від client-cancel; кореляція request-id, нотіф не губиться мовчки)
- [x] 2.4 Ops-докс V11: blast-radius health-restart *(базу закладено у `docs/ops-runbook.md`; повний runbook — окремо)*

## 3. Клієнтський контракт + сервер-половини
- [x] 3.1 Docs: connect-on-demand, burst-reuse (W14), jittered backoff + одиниці (G13), C6-нотатка *(базу закладено у `docs/ops-runbook.md`; повне — окремо)*
- [x] 3.2 Server-heartbeat (`HeartbeatHostedService`, <25с, app-level notification; C5-серверна, S8)
- [x] 3.3 Idempotency-key/`messageId` + **durable** dedup-стор (v7, ключ `(identityId, messageId)`, C2/S8/T6): dedup-lookup ПЕРЕД admission; miss → admission(durable budget) + атомарний `pending`-claim ДО send → `done` після; `pending` після рестарту → `-32011` «outcome unknown», БЕЗ авто-повтору (at-most-once)

## 4. Perf/CI/docs
- [x] 4.1 Perf-gate: load-smoke; budget-persist writes/sec під навантаженням (N3/G11)
- [x] 4.2 CI + docker publish
- [x] 4.3 `docs/ops-runbook.md` + API-reference (генеричний контракт R3.3; MV3 — приклад споживача)

## 5. Тести (DoD)
- [x] 5.1 Вбити signal-cli посеред прогону → сервер відновлюється
- [x] 5.2 Proof-required/captcha → бот паузиться, без долбання
- [x] 5.3 Дубль-ретрай з тим самим idempotency-key → 1 send, 1 декремент бюджету (вкл. через рестарт: durable-стор)
- [x] 5.4 Persistent-WS без трафіку отримує heartbeat <25с; серія пропусків → реконект (C5)
- [x] 5.5 T6: краш між `pending` і send → після рестарту ретрай отримує «outcome unknown», send НЕ повторюється, бюджет НЕ декрементується вдруге; hit `done` → результат без admission
