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

### Requirement: Team-admin видає інвайти лише у свою команду
`createTeamInvite` SHALL бути доступним лише ролі `team-admin` і SHALL створювати інвайт тільки в
команду виклику. Іншим ролям → `-32001`.

#### Scenario: member пробує видати інвайт
- **WHEN** identity з роллю `member` викликає `createTeamInvite`
- **THEN** `-32001`, інвайт не створено

#### Scenario: team-admin видає інвайт
- **WHEN** team-admin команди A викликає `createTeamInvite(maxRedemptions = 5)`
- **THEN** інвайт погашається до 5 разів, і кожне identity потрапляє в команду A

### Requirement: Команда не лишається без team-admin
Team-RPC SHALL NOT залишати команду без активного `team-admin`: зняття ролі, self-revoke чи revoke
останнього team-admin SHALL відмовляти `-32009`, перевірка і зміна — в одній транзакції. Адмін шлюзу
SHALL завжди мати змогу видати інвайт `team-admin` або підвищити identity в будь-якій команді (audit).

#### Scenario: останній team-admin знімає себе
- **WHEN** єдиний team-admin команди викликає self-revoke
- **THEN** `-32009`, роль і identity без змін

#### Scenario: два team-admin знімають одне одного одночасно
- **WHEN** у команді два team-admin, і кожен паралельно знімає роль з іншого
- **THEN** рівно одна операція успішна, у команді лишається один team-admin

#### Scenario: компрометація останнього team-admin
- **WHEN** адмін шлюзу revoke-ить identity єдиного team-admin
- **THEN** revoke проходить, команда позначена «без адміна» в audit і `listTeams`, адмін шлюзу видає новий інвайт `team-admin`
