# authn — delta

## ADDED Requirements

### Requirement: Bearer через субпротокол
Клієнт SHALL передавати opaque-токен у `Sec-WebSocket-Protocol` (base64url без padding);
сервер SHALL echo-ити лише non-auth субпротокол і НІКОЛИ не повертати токен-субпротокол.
Відмова SHALL бути in-band для браузерного клієнта (T4, дзеркало S9): синтаксично валідний,
але невалідний/прострочений/revoked токен → завершити upgrade і одразу закрити
**`4401`** з машиночитним reason (`expired`/`revoked`/`invalid`) без виконання RPC;
HTTP 401 ДО апгрейду — лише за повної відсутності токена. Сокети, закриті `4401`,
SHALL рахуватись у per-IP cap неавтентифікованих з'єднань.

#### Scenario: браузерний клієнт конектиться
- **WHEN** клієнт подає `[token-proto, real-proto]`
- **THEN** 101 містить `Sec-WebSocket-Protocol: <real-proto>` і з'єднання досягає `open`

#### Scenario: протухлий токен видимий браузеру
- **WHEN** браузерний клієнт конектиться з простроченим токеном
- **THEN** з'єднання закривається close-code `4401` із reason `expired` — клієнт відрізняє
  «потрібен re-auth» від мережевого фейлу (generic 1006)

### Requirement: Токен-стор єдине джерело правди
Токени SHALL зберігатись як версіонований HMAC (pre-image = лише decoded random payload);
`ExpiresAt`/`IsRevoked` — у сторі; lookup стору на кожен connect, без кешу revoke-стану.

### Requirement: Proof-of-Possession
На кожне з'єднання сервер SHALL видавати one-time nonce, прив'язаний до conn-id;
до успішної перевірки підпису device-ключем RPC SHALL бути заборонені (PoP-pending).

#### Scenario: вкрадений токен без ключа
- **WHEN** токен replay-иться з пристрою без device-ключа
- **THEN** PoP-challenge провалюється, з'єднання закривається, жоден RPC не виконано

### Requirement: Атомарні ротація і revoke
Ротація SHALL revoke-ити попередній токен у тій самій транзакції (zero-overlap);
revoke identity SHALL каскадити на access-токен і device-секрет атомарно.

### Requirement: Renewal протухлого токена через PoP
Прострочений (НЕ revoked) токен + успішна PoP-перевірка device-ключем через fallback
`authenticate` SHALL атомарно видавати новий токен (та сама транзакція ротації);
`IsRevoked` SHALL завжди перемагати — revoked токен із валідним PoP отримує відмову.
Renewal SHALL бути rate-limited per identity (T5).

#### Scenario: клієнт повернувся після TTL
- **WHEN** клієнт із токеном, простроченим понад TTL, але живим device-ключем, проходить
  `authenticate` + PoP
- **THEN** він отримує новий токен без нового інвайту; старий токен revoked (zero-overlap)

#### Scenario: revocation перемагає
- **WHEN** identity revoked, а клієнт пробує renewal із валідним PoP
- **THEN** відмова; жоден токен не видається
