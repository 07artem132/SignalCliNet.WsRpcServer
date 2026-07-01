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
із TTL, прив'язаним до (identity, заявлена group), виданим під rate-limit і quota.

#### Scenario: паралельний confirm
- **WHEN** один код підтверджується двічі конкурентно
- **THEN** рівно один binding; повтор → відмова

### Requirement: Scoped-сканер
Код-сканер SHALL обробляти лише receive-потік спільного бота; incoming own-linked
акаунтів SHALL лишатися приватним власнику і не потрапляти в жодну поверхню інших identity.

### Requirement: Privacy при receive
Сканер SHALL працювати в режимі скан-і-викинь: тіла, номери та вкладення вхідних
SHALL NOT логуватись чи зберігатись.

### Requirement: Non-blocking споживач
Сканер SHALL бути окремим споживачем із drop-policy; його повільність SHALL NOT
блокувати RPC-відповіді (head-of-line через спільний stdout-loop).
