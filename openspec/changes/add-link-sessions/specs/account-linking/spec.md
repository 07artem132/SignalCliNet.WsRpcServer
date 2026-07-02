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

### Requirement: finishLink переживає ручний скан
`finishLink` SHALL мати per-call timeout ≥ link-TTL (120с), незалежний від глобального
30с `RequestTimeoutSeconds` для send-шляху.
