# add-authn-token-pop

## Чому
Браузерний WebSocket не вміє кастомних заголовків → bearer через `Sec-WebSocket-Protocol`;
самого bearer недостатньо (крадіжка токена = replay до TTL/revoke) → PoP device-ключем.

## Що змінюється
- **Токен:** opaque ≥256-bit CSPRNG, `ptzh_<pepperVer>_<base64url без padding>_<checksum>`;
  серверний стор — єдине джерело правди (жодних claims у токені).
- **At-rest:** `HMAC-SHA256(decoded random payload, pepper[ver])` — канонічна pre-image
  = ЛИШЕ decoded random (W18); version-hint у prefix → 1 lookup при ротації pepper (D10-рішення).
  Без обіцянки «constant-time compare» для index-lookup (W19 — чесна модель: 256-bit ентропія).
- **Lifecycle:** `ExpiresAt` (TTL ~12h) + `IsRevoked` у сторі; lookup на кожен connect;
  ротація видає новий opaque й АТОМАРНО revoke-ить попередній у тій самій транзакції (W21);
  revoke identity каскадить на access-токен І device-секрет.
- **Handshake:** subprotocol-токен на upgrade через `OnWsConnecting` (NetCoreServer seam,
  V4/W11); **обов'язковий** ручний echo non-auth субпротоколу — connectivity-блокер,
  WHATWG-клієнт інакше не досягає `open` (C4); Origin-allowlist пінити на конкретний
  `chrome-extension://<id>`, без гілки «or null» (C3); fallback first-message `authenticate`
  з auth-таймаутом.
- **In-band auth-відмова (T4, дзеркало S9):** браузерний WS не бачить HTTP-статусу фейленого
  upgrade → якщо подано *синтаксично валідний* токен-субпротокол, але токен
  невалідний/прострочений/revoked — завершити upgrade і одразу закрити **`4401`** з
  машиночитним reason (`expired`/`revoked`/`invalid`), жодного RPC не виконуючи; голий
  401-до-апгрейду — лише для запитів узагалі без токена (не-браузерні клієнти).
  4401-сокети рахуються в per-IP cap неавтентифікованих (admission task 3.2).
- **Token-renewal через PoP (T5):** прострочений (НЕ revoked) токен + успішний PoP
  device-ключем через fallback `authenticate` → атомарна видача нового токена
  (та сама W21-транзакція ротації). Обґрунтування: device-ключ enrollment-rooted (D1) —
  володіння приватним ключем доводить identity; `IsRevoked` завжди виграє (revoked +
  валідний PoP → відмова: revocation лишається головною гарантією D7); renewal під
  rate-limit per identity. Отже прострочення TTL 12h не блокує identity назавжди,
  а інвайти лишаються строго one-time.
- **PoP:** challenge-response device-ключем раз на **з'єднання** (W14-рішення, не на нотіф);
  nonce one-time + bound до conn-id (підпис покриває `nonce‖connId`) + короткий TTL + CSPRNG
  ≥128-bit (G8); стан `PoP-pending` — жодного RPC до підпису (D8); PoP ≠ захист від
  malicious-extension — серверний revoke головна гарантія (D7, чесний residual).
- **Enrollment (D1):** `redeemInvite` реєструє device-pubkey (TOFU під одноразовим інвайтом);
  зміна ключа = новий інвайт, не мовчазна.
- Re-чек principal/`IsRevoked` перед dispatch (D9); redact токена в
  `Sec-WebSocket-Protocol`/`authenticate`-params/логах.

## Consumer-контракт (не серверна робота, R3.3)
C1 (device-key лише IndexedDB), C2 (idempotency на клієнті) — у consumer-доках;
серверні половини (idempotency-key, heartbeat) — див. GAPS S8.

## Вплив
- Код: `Security/` (token store, PoP), `Sessions/SignalRpcSession.OnWsConnecting`, DTO
  у serializer-context (secret-DTO: source-gen + `[JsonIgnore]` на секретах — A1-рішення).
- Specs: `authn` (нова capability).
- Залежності (T1): durable-стор токенів/device-секретів — таблиці з
  `add-secure-deploy-persistence` (task 3.1); цей change їх наповнює, не створює.
- Прогалини: закриває S7-частини (W21-інваріант), **T4** (in-band `4401` — task 2.6, DoD 4.6),
  **T5** (renewal через PoP — task 3.5, DoD 4.7); залежить від S9-рішення (канал Retry-After).
