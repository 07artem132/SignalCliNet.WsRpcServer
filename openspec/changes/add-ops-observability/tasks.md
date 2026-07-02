# Tasks: add-ops-observability

## 1. Метрики/логи
- [ ] 1.1 `WsRpcServerDiagnostics`; експорт loopback/auth-порт; tag-allowlist (D13)
- [ ] 1.2 Таксономія помилок + redact-allowlist; тест лог-виходу на секрети

## 2. Fail-path
- [ ] 2.1 Catch-матриця V10 (типізовані + non-RPC винятки; без obsolete-типів)
- [ ] 2.2 Global-pause бота на -5/-6; стан сурфейситься клієнту/ops
- [ ] 2.3 W10: restart-cancel → окремий тип/кореляція (нотіф не губиться мовчки)
- [ ] 2.4 Ops-докс V11: blast-radius health-restart

## 3. Клієнтський контракт + сервер-половини
- [ ] 3.1 Docs: connect-on-demand, burst-reuse (W14), jittered backoff + одиниці (G13), C6-нотатка
- [ ] 3.2 Server-heartbeat (<25с, app-level notification) — implementation (C5-серверна, S8)
- [ ] 3.3 Idempotency-key/`messageId` + dedup-стор із TTL; ретрай не декрементить бюджет двічі (C2-серверна, S8)

## 4. Perf/CI/docs
- [ ] 4.1 Perf-gate: load-smoke; budget-persist writes/sec під навантаженням (N3/G11)
- [ ] 4.2 CI + docker publish
- [ ] 4.3 `docs/ops-runbook.md` + API-reference (генеричний контракт R3.3; MV3 — приклад споживача)

## 5. Тести (DoD)
- [ ] 5.1 Вбити signal-cli посеред прогону → сервер відновлюється
- [ ] 5.2 Proof-required/captcha → бот паузиться, без долбання
- [ ] 5.3 Дубль-ретрай з тим самим idempotency-key → 1 send, 1 декремент бюджету
- [ ] 5.4 Persistent-WS без трафіку отримує heartbeat <25с; серія пропусків → реконект (C5)
