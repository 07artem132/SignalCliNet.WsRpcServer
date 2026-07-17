# account-linking — delta

## ADDED Requirements

### Requirement: Link-сесія one-time та identity-bound
Link-сесія SHALL мати 256-bit CSPRNG id, TTL 120с, one-time-гашення, прив'язку до
identity-ініціатора; `finishLink` чужою identity SHALL відмовляти БЕЗ знищення сесії.

#### Scenario: анти-griefing
- **WHEN** B викликає `finishLink` із SessionID сесії A
- **THEN** відмова; сесія A лишається валідною до TTL; пристрої A не змінені

### Requirement: Захист бот-акаунта від takeover
`startLink`/`finishLink` з target = спільний бот SHALL вимагати `role=admin`;
лінк нового номера SHALL приватно прив'язувати акаунт до caller-identity.

#### Scenario: non-admin намагається лінкнути бота
- **WHEN** не-admin principal викликає `startLink`/`finishLink` з target = спільний бот
- **THEN** відмова авторизації; лінк бот-акаунта виконується лише для `role=admin`

### Requirement: finishLink переживає ручний скан
`finishLink` SHALL мати per-call timeout ≥ link-TTL (120с), незалежний від глобального
30с `RequestTimeoutSeconds` для send-шляху.

#### Scenario: ручний QR-скан довше 30с
- **WHEN** користувач сканує QR вручну і `finishLink` виконується довше глобального `RequestTimeoutSeconds` (30с), але в межах per-call timeout (≥120с)
- **THEN** виклик завершується успішно; глобальний 30с-таймаут send-шляху не вбиває його передчасно
