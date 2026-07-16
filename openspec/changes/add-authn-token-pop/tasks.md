# Tasks: add-authn-token-pop

## 1. Token store + lifecycle
- [ ] 1.1 Формат `ptzh_<ver>_<random≥256bit>_<checksum>`; HMAC pre-image = decoded random only (W18/D10) + round-trip тест-пін; БЕЗ обіцянки «constant-time» index-lookup — модель = 256-bit ентропія (W19)
- [ ] 1.2 Durable embedded DB: hash → identity, `ExpiresAt`/`IsRevoked`; lookup на кожен connect
- [ ] 1.3 Ротація: новий токен + `IsRevoked=1` старого в одній транзакції; zero-overlap тест (W21)
- [ ] 1.4 Revoke identity → каскад на токен + device-секрет атомарно; розрив живих конектів in-process
- [ ] 1.5 Re-чек `IsRevoked` перед dispatch у signal-cli (D9)

## 2. Handshake

*(поетапний rollout: `Server:Auth:Enabled=false` (дефолт) зберігає Phase-1 loopback full-access
шлях незмінним; D4-посилення «non-loopback ⇒ Auth.Enabled» відкладено до add-invites-admin —
до появи bootstrap-admin/інвайтів жорстка вимога зламала б робочий compose-деплой без
можливості онбордингу. Поточний D4-інваріант change 2 (opt-in + TLS) лишається в силі)*
- [ ] 2.1 `OnWsConnecting`: парс `Sec-WebSocket-Protocol` (base64url без padding), 401 до апгрейду
- [ ] 2.2 Явний `SetHeader` echo non-auth субпротоколу; НІКОЛИ не копіювати client-list (C4/W11)
- [ ] 2.3 Origin-allowlist: конкретний `chrome-extension://<id>`; defense-in-depth, не authn (C3)
- [ ] 2.4 Fallback first-message `authenticate` + auth-таймаут + cap неавтентиф. сокетів/IP
- [ ] 2.5 Redact: токен/`authenticate`-params/device-link URI не в логах
- [ ] 2.6 In-band auth-відмова (T4): валідний за формою, але невалідний токен → upgrade завершити й одразу закрити `4401` + reason (`expired`/`revoked`/`invalid`); голий 401 — лише без токена; 4401-сокети рахуються в per-IP unauth-cap

## 3. PoP + enrollment
- [ ] 3.1 Challenge-response: nonce one-time, TTL, bound `nonce‖connId`, CSPRNG ≥128-bit (G8)
- [ ] 3.2 Стан `PoP-pending`: RPC заборонені, auth-таймаут, рахувати в half-open cap (D8)
- [ ] 3.3 `redeemInvite` реєструє device-pubkey (D1); key-rotation-on-loss = форс re-invite
      *(уточнення шва з add-invites-admin: цей change дає enrollment-СЕРВІС (TOFU-реєстрація
      device-pubkey + видача першого токена; повторна реєстрація ключа → відмова, лікується
      новим інвайтом). Wire-RPC `redeemInvite` + invites-таблиця + атомарний consume (W20/G12) —
      у add-invites-admin task 1.3, який викликає цей сервіс)*
- [ ] 3.4 Secret-DTO: source-gen реєстрація + `[JsonIgnore]` + guard-тест resolve через Default (A1)
- [ ] 3.5 Renewal через PoP (T5): expired (не revoked) токен + успішний PoP → новий токен у W21-транзакції ротації; revoked → відмова завжди; rate-limit renewal per identity

## 4. Тести (DoD)
- [ ] 4.1 Прострочений токен → auth-помилка без бізнес-логіки
- [ ] 4.2 Echo-leak: токен-субпротокол ніколи не в response; браузерний WS досягає `open` (позитивний C4)
- [ ] 4.3 PoP: replay вкраденим токеном без device-ключа → відмова; релей підпису на інший сокет → відмова (G8)
- [ ] 4.4 Revoke-каскад: device-секрет мертвий після revoke identity
- [ ] 4.5 Токен не в логах (перевірка лог-виходу)
- [ ] 4.6 T4: браузерний WS із протухлим токеном бачить close `4401` + reason і відрізняє його від мережевого фейлу; запит без токена → HTTP 401 до апгрейду
- [ ] 4.7 T5: expired+PoP → новий токен, старий revoked (zero-overlap); revoked+PoP → відмова
