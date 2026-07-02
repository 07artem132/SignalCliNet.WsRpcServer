# Tasks: add-authn-token-pop

## 1. Token store + lifecycle
- [ ] 1.1 Формат `ptzh_<ver>_<random≥256bit>_<checksum>`; HMAC pre-image = decoded random only (W18/D10) + round-trip тест-пін; БЕЗ обіцянки «constant-time» index-lookup — модель = 256-bit ентропія (W19)
- [ ] 1.2 Durable embedded DB: hash → identity, `ExpiresAt`/`IsRevoked`; lookup на кожен connect
- [ ] 1.3 Ротація: новий токен + `IsRevoked=1` старого в одній транзакції; zero-overlap тест (W21)
- [ ] 1.4 Revoke identity → каскад на токен + device-секрет атомарно; розрив живих конектів in-process
- [ ] 1.5 Re-чек `IsRevoked` перед dispatch у signal-cli (D9)

## 2. Handshake
- [ ] 2.1 `OnWsConnecting`: парс `Sec-WebSocket-Protocol` (base64url без padding), 401 до апгрейду
- [ ] 2.2 Явний `SetHeader` echo non-auth субпротоколу; НІКОЛИ не копіювати client-list (C4/W11)
- [ ] 2.3 Origin-allowlist: конкретний `chrome-extension://<id>`; defense-in-depth, не authn (C3)
- [ ] 2.4 Fallback first-message `authenticate` + auth-таймаут + cap неавтентиф. сокетів/IP
- [ ] 2.5 Redact: токен/`authenticate`-params/device-link URI не в логах

## 3. PoP + enrollment
- [ ] 3.1 Challenge-response: nonce one-time, TTL, bound `nonce‖connId`, CSPRNG ≥128-bit (G8)
- [ ] 3.2 Стан `PoP-pending`: RPC заборонені, auth-таймаут, рахувати в half-open cap (D8)
- [ ] 3.3 `redeemInvite` реєструє device-pubkey (D1); key-rotation-on-loss = форс re-invite
- [ ] 3.4 Secret-DTO: source-gen реєстрація + `[JsonIgnore]` + guard-тест resolve через Default (A1)

## 4. Тести (DoD)
- [ ] 4.1 Прострочений токен → auth-помилка без бізнес-логіки
- [ ] 4.2 Echo-leak: токен-субпротокол ніколи не в response; браузерний WS досягає `open` (позитивний C4)
- [ ] 4.3 PoP: replay вкраденим токеном без device-ключа → відмова; релей підпису на інший сокет → відмова (G8)
- [ ] 4.4 Revoke-каскад: device-секрет мертвий після revoke identity
- [ ] 4.5 Токен не в логах (перевірка лог-виходу)
