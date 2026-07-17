# group-claim Specification

## Purpose
TBD - created by archiving change add-group-claim-receive. Update Purpose after archive.
## Requirements
### Requirement: Send у групу лише за claim
`sendTextMessage` у групу через спільний бот SHALL вимагати активний binding
(identity, group); binding створюється лише через verified claim-код.

#### Scenario: без claim
- **WHEN** автентифікований B без claim шле у групу G
- **THEN** відмова авторизації (recipient-рівень, не лише account)

### Requirement: One-time claim-код
Claim-код SHALL бути one-time (атомарний consume-and-bind з перевіркою rows-affected),
із TTL ~10 хв, прив'язаним при видачі ЛИШЕ до identity (T3: group-agnostic — юзер не має
звідки взяти group-ID до claim'у, `listGroups` її не покаже), виданим під rate-limit і
quota (вкл. cap активних bindings). Групу та anchor SHALL фіксувати сканер при confirm:
binding = (identity, group-де-з'явився-код, anchorAci вставника — T2). Код SHALL мати
ентропію **≥96-bit CSPRNG** (візуальні блоки для ручного вводу, як інвайти).

#### Scenario: паралельний confirm
- **WHEN** один код підтверджується двічі конкурентно
- **THEN** рівно один binding; повтор → відмова

#### Scenario: групу визначає місце появи коду
- **WHEN** код U з'являється у групі G1, а пізніше (після consume) той самий код — у G2
- **THEN** binding створено лише до G1; поява в G2 не дає нічого (one-time)

### Requirement: Валідність binding при зміні членства
Binding SHALL нести anchorAci — ACI вставника claim-коду, зафіксований сканером при confirm
(T2: identity сама по собі не має Signal-ідентифікатора, тому «членство identity» без anchor
неперевірюване). Перед КОЖНИМ send через спільний бот SHALL перевірятись
`anchorAci ∈ members(group)`: якщо anchor більше не член — send SHALL відхилятись і binding
SHALL інвалідуватись. Member-list MAY кешуватись із коротким TTL (~60с) з інвалідизацією по
group-update подіях авто-receive. Binding SHALL мати TTL як backstop (форсує re-claim).

#### Scenario: ex-member (anchor = сам U)
- **WHEN** U вставив власний код у G (anchor = U), а потім вийшов/вигнаний із G
- **THEN** наступний send через бот у G — відмова; binding інвалідований (не безстроковий доступ)

#### Scenario: anchor-поручитель вийшов (proxy-випадок)
- **WHEN** код U вставив P (anchor = P), і P вийшов/вигнаний із G
- **THEN** send U у G — відмова; binding інвалідований — право U живе з членством поручителя

### Requirement: Самокерування bindings
Юзер SHALL бачити власні bindings (`listMyBindings` — лише свої, без даних інших identity)
і SHALL мати змогу відкликати їх (`revokeMyBinding`) — зокрема binding, створений
вставленням його коду у небажану групу (residual T3).

#### Scenario: listMyBindings бачить лише свої
- **WHEN** identity A викликає `listMyBindings`
- **THEN** повертаються лише bindings identity A, без даних інших identity

#### Scenario: revokeMyBinding для небажаної групи
- **WHEN** identity A відкликає binding через `revokeMyBinding`, створений вставленням коду A у небажану групу
- **THEN** binding відкликається; подальший send через бот у цю групу для A недоступний

### Requirement: Per-account receive-роутер
Сирий notification-потік демона SHALL проходити через єдиний per-account роутер
(`account → owning-identity`), який фанаутить own-linked incoming ЛИШЕ власнику, а
shared-bot-потік — ЛИШЕ сканеру. Будь-який receive/event-consumer (вкл. Phase-3 subscribe)
SHALL підписуватись через роутер, а не на сирий потік; інваріант R3.5/W25-inbound
SHALL забезпечуватись цим механізмом, а не відсутністю поверхні.
Forward-правило розширення (T7): майбутній events-consumer identity X SHALL отримувати з
shared-bot-потоку ЛИШЕ envelope'и груп, на які X має активний binding (group-scoped фільтр);
shared-bot трафік поза claim'нутими групами (напр. DM боту) — admin-only або drop.
Phase-3 поверхня SHALL розширювати роутер за цим правилом, не ревізуючи інваріант.

#### Scenario: own-linked incoming
- **WHEN** приходить повідомлення на own-linked акаунт юзера A
- **THEN** його отримує лише principal A; жоден consumer іншої identity (ні сканер, ні events) його не бачить

### Requirement: Scoped-сканер
Код-сканер SHALL обробляти лише receive-потік спільного бота (через per-account роутер);
incoming own-linked акаунтів SHALL лишатися приватним власнику і не потрапляти в жодну
поверхню інших identity.

#### Scenario: сканер ігнорує own-linked
- **WHEN** через роутер приходить incoming на own-linked акаунт юзера A
- **THEN** сканер не обробляє це повідомлення — воно лишається приватним A і не потрапляє в жодну іншу поверхню (R3.5)

### Requirement: Privacy при receive
Сканер SHALL працювати в режимі скан-і-викинь: тіла, номери та вкладення вхідних
SHALL NOT логуватись чи зберігатись.

#### Scenario: скан-і-викинь
- **WHEN** сканер обробляє вхідне повідомлення бот-потоку в пошуках claim-коду
- **THEN** тіло, номер відправника та вкладення не потрапляють у логи чи сховище — зберігається лише факт confirm claim-коду

### Requirement: Non-blocking споживач
Сканер SHALL бути окремим споживачем із drop-policy; його повільність SHALL NOT
блокувати RPC-відповіді (head-of-line через спільний stdout-loop).

#### Scenario: сканер відстає
- **WHEN** сканер обробляє повідомлення повільніше, ніж надходить потік
- **THEN** зайві повідомлення відкидаються за drop-policy; RPC-відповіді клієнтам не затримуються через відставання сканера

