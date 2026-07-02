# group-claim — delta

## ADDED Requirements

### Requirement: Send у групу лише за claim
`sendTextMessage` у групу через спільний бот SHALL вимагати активний binding
(identity, group); binding створюється лише через verified claim-код.

#### Scenario: без claim
- **WHEN** автентифікований B без claim шле у групу G
- **THEN** відмова авторизації (recipient-рівень, не лише account)

### Requirement: One-time claim-код
Claim-код SHALL бути one-time (атомарний consume-and-bind з перевіркою rows-affected),
із TTL ~10 хв, прив'язаним до (identity, заявлена group), виданим під rate-limit і quota.
Код SHALL мати ентропію **≥96-bit CSPRNG** (візуальні блоки для ручного вводу, як інвайти).

#### Scenario: паралельний confirm
- **WHEN** один код підтверджується двічі конкурентно
- **THEN** рівно один binding; повтор → відмова

### Requirement: Валідність binding при зміні членства
Binding (identity, group) SHALL перевірятись на актуальне членство перед КОЖНИМ send через
спільний бот: якщо identity більше не член group — send SHALL відхилятись і binding
SHALL інвалідуватись. Binding SHALL мати TTL як backstop (форсує re-claim).

#### Scenario: ex-member
- **WHEN** U має binding до G, але потім вийшов/вигнаний із G
- **THEN** наступний send через бот у G — відмова; binding інвалідований (не безстроковий доступ)

### Requirement: Per-account receive-роутер
Сирий notification-потік демона SHALL проходити через єдиний per-account роутер
(`account → owning-identity`), який фанаутить own-linked incoming ЛИШЕ власнику, а
shared-bot-потік — ЛИШЕ сканеру. Будь-який receive/event-consumer (вкл. Phase-3 subscribe)
SHALL підписуватись через роутер, а не на сирий потік; інваріант R3.5/W25-inbound
SHALL забезпечуватись цим механізмом, а не відсутністю поверхні.

#### Scenario: own-linked incoming
- **WHEN** приходить повідомлення на own-linked акаунт юзера A
- **THEN** його отримує лише principal A; жоден consumer іншої identity (ні сканер, ні events) його не бачить

### Requirement: Scoped-сканер
Код-сканер SHALL обробляти лише receive-потік спільного бота (через per-account роутер);
incoming own-linked акаунтів SHALL лишатися приватним власнику і не потрапляти в жодну
поверхню інших identity.

### Requirement: Privacy при receive
Сканер SHALL працювати в режимі скан-і-викинь: тіла, номери та вкладення вхідних
SHALL NOT логуватись чи зберігатись.

### Requirement: Non-blocking споживач
Сканер SHALL бути окремим споживачем із drop-policy; його повільність SHALL NOT
блокувати RPC-відповіді (head-of-line через спільний stdout-loop).
