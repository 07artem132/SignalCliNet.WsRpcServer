# authn — delta

## ADDED Requirements

### Requirement: Bearer через субпротокол
Клієнт SHALL передавати opaque-токен у `Sec-WebSocket-Protocol` (base64url без padding);
невалідний/відсутній токен → 401 ДО апгрейду; сервер SHALL echo-ити лише non-auth
субпротокол і НІКОЛИ не повертати токен-субпротокол.

#### Scenario: браузерний клієнт конектиться
- **WHEN** клієнт подає `[token-proto, real-proto]`
- **THEN** 101 містить `Sec-WebSocket-Protocol: <real-proto>` і з'єднання досягає `open`

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
