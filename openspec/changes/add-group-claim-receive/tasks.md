# Tasks: add-group-claim-receive

## 0. Рішення (S10 ЗАКРИТО; механізм уточнено T2/T3)
- [x] 0.1 Claim-код = **≥96-bit CSPRNG** (візуальні блоки для ручного вводу, як інвайти)
- [x] 0.2 Membership churn (T2 anchor-модель): binding несе **anchorAci** (ACI вставника коду, фіксує сканер); перевірка при кожному send = **anchorAci ∈ members(G)** (primary) + **TTL binding'у** (backstop); anchor вийшов/вигнаний із G → send-відмова + інвалідизація binding'у; member-list кешується (~60с TTL) з інвалідизацією по group-update подіях
- [x] 0.3 Delegation-by-proxy — **accepted-risk із anchor-семантикою (T2), док у `docs/shared-bot.md`**: код секретний до U, one-time, TTL ~10 хв; вставлення коду будь-ким активує binding самого U (не грантить прав вставнику), anchor = вставник → право U живе, поки вставник член G
- [x] 0.4 Bootstrap group-ID (T3): claim-код **group-agnostic при видачі** — `requestGroupClaim()` без аргументу; групу фіксує сканер за місцем появи коду; discovery-поверхня не потрібна (R3.2/G9 не послаблюються)

## 1. Receive
- [x] 1.1 `UseManualReceiveMode=false`; інвертувати Phase-1 task-7-тезу в докс (R3.1 скасовує V2)
- [x] 1.2 Окремий non-blocking споживач для сканера + drop-policy (head-of-line, Уточнення-3)
- [x] 1.3 Сканер scoped до shared-bot потоку; own-linked incoming не обробляється (R3.5)
- [x] 1.4 **Per-account receive-роутер** (S11): єдиний вузол, що фанаутить спільний notification-потік демона за `account → owning-identity` (стор із `add-secure-deploy-persistence`); own-linked → лише власнику, shared-bot → лише сканер. Будь-який майбутній consumer (events subscribe Phase-3) підписується ЧЕРЕЗ роутер, не на сирий потік — інваріант R3.5 має механізм, не «поверхні ще нема»
- [x] 1.5 W25-рішення: daemon-внутрішній cross-account стан (contact-merge/profile-key) — **accepted-risk**, задокументувати в `docs/shared-bot.md`; app-фільтр тримає ізоляцію на поверхні, daemon-internal leak — відомий residual (own-number linking у спільний демон не рекомендувати)

## 2. Claim-флоу
- [x] 2.1 `requestGroupClaim()` (T3: без аргументу-групи): one-time код, TTL ~10 хв, bound лише (U), rate-limit + per-identity quota (вкл. cap активних bindings)
- [x] 2.2 Verify (T2/T3): код у TTL → атомарний consume-and-bind (W20-патерн); binding = (U, G-де-з'явився-код, anchorAci вставника)
- [x] 2.3 `sendTextMessage` у групу через shared-bot гейтиться claim'ом (не лише account-guard)
- [x] 2.4 Revoke identity → каскад на bindings; admin-revoke binding'у
- [x] 2.5 Membership churn (T2): перед send через бот перевірити anchorAci ∈ members(G) (кеш ~60с + інвалідизація по group-update подіях); не член → відмова + інвалідизація binding'у; binding має TTL як backstop
- [x] 2.6 Самокерування bindings (T3): `listMyBindings` (лише власні, без чужих даних) + `revokeMyBinding` — юзер прибирає binding, створений вставленням його коду в небажану групу

## 3. Privacy
- [x] 3.1 Скан-і-викинь: тіла/номери/вкладення не логуються/не зберігаються (тест лог-виходу)

## 4. Тести (DoD плану)
- [x] 4.1 A claim'ить G → send проходить; B без claim → відмова
- [x] 4.2 Повторний/перехоплений код → відмова (one-time, bound U+G); griefing-burn → re-request працює
- [x] 4.3 Сканер не блокує RPC-шлях (повільний споживач ≠ повільні RPC-відповіді)
- [x] 4.4 `listGroups` віддає виключно claim'нуті/власні (R3.2)
- [x] 4.5 Own-account incoming юзера A не видно юзеру B (W25-inbound, R3.5)
- [x] 4.6 Membership churn (S10/T2, anchor = U): U вставив власний код у G, потім вигнаний із G → наступний send відхиляється, binding інвалідований
- [x] 4.7 Per-account роутер (S11): incoming own-linked акаунта маршрутизується лише власнику; mock-consumer іншої identity його не бачить
- [x] 4.8 Anchor-churn (T2, proxy-випадок): код U вставив P; P вийшов із G → send U відхиляється, binding інвалідований (право живе з поручителем)
- [x] 4.9 T3: consume-and-bind фіксує групу за місцем появи коду; той самий код у другій групі після consume/TTL → відмова; `revokeMyBinding` прибирає небажаний binding
