# Tasks: add-admission-rate-limits

## 1. Admission-функція (W16)
- [x] 1.1 Один упорядкований `admit(principal, account, recipient)` на chokepoint; калібровка Σ(per_user_floor) ≤ aggregate
      (admission-ядро: `AdmissionControl.Admit` + крос-валідатор floor ≤ aggregate. **Chokepoint-виклик тепер підключено:**
      `AuthorizingSignalJsonRpc` кличе `admitSends` замикання ЛИШЕ для send-like методів (політика з `RecipientsArgName`),
      ПІСЛЯ default-deny/guard-ів, ДО dispatch; deny → `-32005`. Сесія передає замикання
      `recipients => AdmissionControl.Admit(identityId, recipients)` при активації (token → реальна identity;
      loopback+Admission:Enabled → ключ "loopback"). Активне лише при `Admission:Enabled`.)
- [x] 1.2 Per-user floor / fair-share під агрегатом (G2 — без FIFO-starvation)
- [x] 1.3 New-recipient window counter (D2), throttle нових контактів окремо від обсягу
- [x] 1.4 Global-pause gate (Phase-3 реакція на -5/-6) — заглушка з інтерфейсом

## 2. Бот-бюджет reserve-then-send (W13)
- [x] 2.1 In-memory атомарний RMW (lock / `UPDATE…WHERE n>0` + rows-affected; G3)
- [x] 2.2 Durable блок-резервація K одиниць; cold-start: залишок форфейтиться (ніколи не re-spend)
- [x] 2.3 Монотонний годинник для refill (D15)

## 3. Транспортні ліміти
- [x] 3.1 `MaxMessageSizeBytes` → 64KB у конфігу (V3)
      (`appsettings.json` Server:MaxMessageSizeBytes=65536 + `Program.ApplyServerConfig` passthrough +
      docker-compose env + docs; framework-дефолт 100MB у JSON-RPC.NET не чіпаємо. Пін: `ServerConfigTests`)
- [x] 3.2 Per-token cap конектів + msg-rate (~100/хв) + auth-timeout + idle-timeout + per-IP cap неавтентиф. сокетів
      (per-identity cap: `IdentityConnectionLimiter` → close 4429 `too-many-connections`; msg-rate:
      per-connection token-bucket `MessageRateLimiter` (монотонний рефіл, D15) → close 4429 `message-rate:<sec>`;
      idle-timeout: Timer у сесії → close 1000 `idle-timeout`. **auth-timeout НЕ дублюємо** — він уже є
      (`AuthOptions.AuthTimeoutSeconds`/4408); **per-IP cap неавтентиф.** уже є (`UnauthenticatedConnectionLimiter`,
      change 3). Нові опції у `AdmissionOptions` (+[Range]-валідатор). Активні лише при `Admission:Enabled`.)
- [x] 3.3 Per-conn in-flight cap (D12) + server-wide semaphore до signal-cli, понад → `-32005` (G6)
      (in-flight cap: Interlocked-лічильник у `AuthorizingSignalJsonRpc` (inc на вході dispatch / dec у finally),
      понад `MaxInFlightPerConnection` → `-32005` retry_after=1; server-wide гейт: `SignalCliGate`
      SemaphoreSlim(SignalCliConcurrencyLimit), неблокуючий `Wait(0)`, зайнято → `-32005` retry_after=1.
      **Відхилення від proposal-формулювання:** реалізовано як chokepoint-гейт у `AuthorizingSignalJsonRpc`
      (services-рівень), а НЕ декоратор над `ISignalCliClient` — досягає G6 (pending не росте unbounded),
      не тягнучи upstream-інтерфейс і не форкаючи бібліотечну поведінку. Обидва — лише не-Public методи;
      ping/handshake повз. Тести: `AuthzChokepointDispatchTests` (in-flight cap + gate), `SignalCliGateTests`.)
- [x] 3.4 D5/U1: L4-передумова (nftables/SG) задокументована; frame-тріада — accepted-risk, НЕ app-задача
      (docs/self-host.md §"L4-передумова та frame-assembly": L4-фаєрвол як передумова не-loopback експозиції;
      frame-assembly-тріада — accepted-risk поза app; permessage-deflate вимкнено (V6), MaxMessageSizeBytes 64KB.)

## 4. Контракт відмов
- [x] 4.1 `-32005` + `data.retry_after` (секунди) / WS `4429` з retry_after у close-reason — in-band для браузерного клієнта (S9)
      (`AdmissionErrorCodes.RateLimited=-32005` + `RateLimitErrorData([JsonPropertyName("retry_after")])`,
      зареєстрований у source-gen контексті; `AuthCloseCodes.RateLimited=4429` + `RateLimitCloseReasons`
      (`too-many-connections`, `message-rate:<sec>`, `idle-timeout`). Wire-shape пінить
      `AuthzChokepointDispatchTests.RateLimitErrorData_WireShape_IsSnakeCaseRetryAfter`. A1: у data лише секунди.)
- [x] 4.2 Jittered backoff у клієнтському контракті (G13)
      (docs/self-host.md §"Контракт відмов": base 1s, factor 2, jitter ±50%, cap 60s + reason-словник.)

## 5. Тести (DoD)
- [ ] 5.1 G3: N паралельних send під бюджетом=1 → рівно 1 проходить
- [ ] 5.2 G2: один юзер не голодоморить решту (floor спрацьовує)
- [ ] 5.3 Бюджет переживає рестарт; краш форфейтить ≤K, ніколи не over-send (W13)
- [ ] 5.4 G6: флуд повільних send → cap тримає, pending не росте unbounded
- [ ] 5.5 Msg >64KB → `MessageTooBig`; auth-timeout закриває неавтентиф. сокет
- [ ] 5.6 Перевищення квоти → `-32005 data.retry_after` (in-band) / WS-close `4429`; браузерний клієнт бачить retry_after (S9)
