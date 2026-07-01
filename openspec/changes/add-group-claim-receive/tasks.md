# Tasks: add-group-claim-receive

## 0. Рішення до старту (S10)
- [ ] 0.1 Запінити ентропію/формат claim-коду (пропозиція: ≥96-bit CSPRNG, як інвайти)
- [ ] 0.2 Політика membership churn (пропозиція: TTL binding'у + перевірка членства при send або periodic re-check)
- [ ] 0.3 Задокументувати delegation-by-proxy residual у shared-bot.md

## 1. Receive
- [ ] 1.1 `UseManualReceiveMode=false`; інвертувати Phase-1 task-7-тезу в докс (R3.1 скасовує V2)
- [ ] 1.2 Окремий non-blocking споживач для сканера + drop-policy (head-of-line, Уточнення-3)
- [ ] 1.3 Сканер scoped до shared-bot потоку; own-linked incoming не обробляється (R3.5)

## 2. Claim-флоу
- [ ] 2.1 `requestGroupClaim`: one-time код, TTL ~10 хв, bound (U, G), rate-limit + per-identity quota
- [ ] 2.2 Verify: код у заявленій G, у TTL → атомарний consume-and-bind (W20-патерн)
- [ ] 2.3 `sendTextMessage` у групу через shared-bot гейтиться claim'ом (не лише account-guard)
- [ ] 2.4 Revoke identity → каскад на bindings; admin-revoke binding'у

## 3. Privacy
- [ ] 3.1 Скан-і-викинь: тіла/номери/вкладення не логуються/не зберігаються (тест лог-виходу)

## 4. Тести (DoD плану)
- [ ] 4.1 A claim'ить G → send проходить; B без claim → відмова
- [ ] 4.2 Повторний/перехоплений код → відмова (one-time, bound U+G); griefing-burn → re-request працює
- [ ] 4.3 Сканер не блокує RPC-шлях (повільний споживач ≠ повільні RPC-відповіді)
- [ ] 4.4 `listGroups` віддає виключно claim'нуті/власні (R3.2)
- [ ] 4.5 Own-account incoming юзера A не видно юзеру B (W25-inbound, R3.5)
