# Tasks: add-group-claim-receive

## 0. Рішення (S10 ЗАКРИТО)
- [x] 0.1 Claim-код = **≥96-bit CSPRNG** (візуальні блоки для ручного вводу, як інвайти)
- [x] 0.2 Membership churn: **перевірка членства (U ∈ G) при кожному send через бота** (primary) + **TTL binding'у** (backstop); U вийшов/вигнаний із G → send-відмова + інвалідизація binding'у
- [x] 0.3 Delegation-by-proxy — **accepted-risk, док у `docs/shared-bot.md`**: код секретний до U, one-time, TTL ~10 хв, bound (U,G); вставлення коду будь-ким лише активує binding самого U (не грантить прав вставнику)

## 1. Receive
- [ ] 1.1 `UseManualReceiveMode=false`; інвертувати Phase-1 task-7-тезу в докс (R3.1 скасовує V2)
- [ ] 1.2 Окремий non-blocking споживач для сканера + drop-policy (head-of-line, Уточнення-3)
- [ ] 1.3 Сканер scoped до shared-bot потоку; own-linked incoming не обробляється (R3.5)
- [ ] 1.4 **Per-account receive-роутер** (S11): єдиний вузол, що фанаутить спільний notification-потік демона за `account → owning-identity` (стор із `add-secure-deploy-persistence`); own-linked → лише власнику, shared-bot → лише сканер. Будь-який майбутній consumer (events subscribe Phase-3) підписується ЧЕРЕЗ роутер, не на сирий потік — інваріант R3.5 має механізм, не «поверхні ще нема»
- [ ] 1.5 W25-рішення: daemon-внутрішній cross-account стан (contact-merge/profile-key) — **accepted-risk**, задокументувати в `docs/shared-bot.md`; app-фільтр тримає ізоляцію на поверхні, daemon-internal leak — відомий residual (own-number linking у спільний демон не рекомендувати)

## 2. Claim-флоу
- [ ] 2.1 `requestGroupClaim`: one-time код, TTL ~10 хв, bound (U, G), rate-limit + per-identity quota
- [ ] 2.2 Verify: код у заявленій G, у TTL → атомарний consume-and-bind (W20-патерн)
- [ ] 2.3 `sendTextMessage` у групу через shared-bot гейтиться claim'ом (не лише account-guard)
- [ ] 2.4 Revoke identity → каскад на bindings; admin-revoke binding'у
- [ ] 2.5 Membership churn (S10 0.2): перед send через бот перевірити U ∈ G; не член → відмова + інвалідизація binding'у; binding має TTL як backstop

## 3. Privacy
- [ ] 3.1 Скан-і-викинь: тіла/номери/вкладення не логуються/не зберігаються (тест лог-виходу)

## 4. Тести (DoD плану)
- [ ] 4.1 A claim'ить G → send проходить; B без claim → відмова
- [ ] 4.2 Повторний/перехоплений код → відмова (one-time, bound U+G); griefing-burn → re-request працює
- [ ] 4.3 Сканер не блокує RPC-шлях (повільний споживач ≠ повільні RPC-відповіді)
- [ ] 4.4 `listGroups` віддає виключно claim'нуті/власні (R3.2)
- [ ] 4.5 Own-account incoming юзера A не видно юзеру B (W25-inbound, R3.5)
- [ ] 4.6 Membership churn (S10): U claim'ив G, потім вигнаний із G → наступний send відхиляється, binding інвалідований
- [ ] 4.7 Per-account роутер (S11): incoming own-linked акаунта маршрутизується лише власнику; mock-consumer іншої identity його не бачить
