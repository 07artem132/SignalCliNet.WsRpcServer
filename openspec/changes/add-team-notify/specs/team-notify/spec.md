# team-notify — delta

## ADDED Requirements

### Requirement: Адресат за alias лише зі своєї команди
Recipient `alias:<alias>` у `sendTextMessage` SHALL резолвитись лише в командні bindings команди
виклику. Чужий або неіснуючий alias SHALL давати ту саму відмову `-32007` (anti-oracle).

#### Scenario: чужа команда
- **WHEN** identity команди A шле на alias, прив'язаний лише в команді B
- **THEN** `-32007`, як для неіснуючого alias

#### Scenario: інший пристрій тієї ж команди
- **WHEN** alias `dir:1` прив'язав пристрій X, а шле пристрій Y тієї ж команди
- **THEN** повідомлення йде в групу `dir:1` через нинішню anchor-перевірку

### Requirement: Командний dedup
Повтор `(team, alias, messageId)` у межах TTL SHALL NOT надсилати повторно, незалежно від того,
яке identity команди викликає, і SHALL повертати результат першого виклику.

#### Scenario: дві вкладки однієї команди
- **WHEN** identity X і Y однієї команди шлють на той самий alias з однаковим messageId
- **THEN** у групу йде одне повідомлення, обидва отримують той самий результат

### Requirement: Multi-use інвайт у команду
Інвайт з `maxRedemptions = N` SHALL погашатись не більше N разів (атомарний інкремент), і кожне
погашення SHALL створювати окреме identity в команді інвайту.

#### Scenario: паралельні погашення на межі
- **WHEN** інвайт з `maxRedemptions = 2` погашають три пристрої конкурентно
- **THEN** рівно два identity в команді, третій отримує `-32006`
