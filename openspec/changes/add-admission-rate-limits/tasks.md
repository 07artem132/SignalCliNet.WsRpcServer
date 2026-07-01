# Tasks: add-admission-rate-limits

## 1. Admission-функція (W16)
- [ ] 1.1 Один упорядкований `admit(principal, account, recipient)` на chokepoint; калібровка Σ(per_user_floor) ≤ aggregate
- [ ] 1.2 Per-user floor / fair-share під агрегатом (G2 — без FIFO-starvation)
- [ ] 1.3 New-recipient window counter (D2), throttle нових контактів окремо від обсягу
- [ ] 1.4 Global-pause gate (Phase-3 реакція на -5/-6) — заглушка з інтерфейсом

## 2. Бот-бюджет reserve-then-send (W13)
- [ ] 2.1 In-memory атомарний RMW (lock / `UPDATE…WHERE n>0` + rows-affected; G3)
- [ ] 2.2 Durable блок-резервація K одиниць; cold-start: залишок форфейтиться (ніколи не re-spend)
- [ ] 2.3 Монотонний годинник для refill (D15)

## 3. Транспортні ліміти
- [ ] 3.1 `MaxMessageSizeBytes` → 64KB у конфігу (V3)
- [ ] 3.2 Per-token cap конектів + msg-rate (~100/хв) + auth-timeout + idle-timeout + per-IP cap неавтентиф. сокетів
- [ ] 3.3 Per-conn in-flight cap (D12) + server-wide semaphore до signal-cli, понад → `-32005` (G6)
- [ ] 3.4 D5/U1: L4-передумова (nftables/SG) задокументована; frame-тріада — accepted-risk, НЕ app-задача

## 4. Контракт відмов
- [ ] 4.1 `-32005` + `data.retry_after` (секунди) / WS `4429` з retry_after у close-reason — in-band для браузерного клієнта (S9)
- [ ] 4.2 Jittered backoff у клієнтському контракті (G13)

## 5. Тести (DoD)
- [ ] 5.1 G3: N паралельних send під бюджетом=1 → рівно 1 проходить
- [ ] 5.2 G2: один юзер не голодоморить решту (floor спрацьовує)
- [ ] 5.3 Бюджет переживає рестарт; краш форфейтить ≤K, ніколи не over-send (W13)
- [ ] 5.4 G6: флуд повільних send → cap тримає, pending не росте unbounded
- [ ] 5.5 Msg >64KB → `MessageTooBig`; auth-timeout закриває неавтентиф. сокет
