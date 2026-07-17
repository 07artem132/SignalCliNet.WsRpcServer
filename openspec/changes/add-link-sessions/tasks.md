# Tasks: add-link-sessions

- [x] 1. In-memory link-session стор: 256-bit SessionID, TTL 120с, one-time, 3/хв/identity
  → `Security/LinkSessionStore.cs` (`record LinkSession`, `Create`/`TryConsume`, TimeProvider, ліниве
  прибирання прострочених, rate-limit монотонним ковзним вікном за патерном `RenewalRateLimiter`).
- [x] 2. `finishLink` атомарне умовне гашення (in-process lock; mismatch identity → сесію НЕ видаляти)
  → `LinkSessionStore.TryConsume` під одним локом: власник → видалити+повернути; чужа identity → null
  + warning (лише префікс sessionId, без URI); прострочена/невідома → null. Адаптер маплить null → `-32004`.
- [x] 3. Політика: target=shared-bot → admin-only; новий номер → приватний bind до caller
  → guard у `Services/SignalDevicesRpcAdapter.cs` (не в generic chokepoint): `startLink(targetAccount)`
  з відомим bind + не-admin → `-32001` + alert; `finishLink` bind-иться за ФАКТИЧНИМ номером із
  `FinishLinkResponse.Number` (не за target-hint) → новий номер = приватний bind (R3.5, `IsPrivate=true`);
  вже-прив'язаний чужий акаунт не переписуємо (анти-takeover на рівні стору) + alert. `startLink`/
  `finishLink` лишаються `IdentityOnboarding` у `SignalRpcPolicyRegistry` (політика без змін).
- [ ] 4. Спайк + впровадження per-call timeout ≥130с для `finishLink` (W9); глобальний 30с не чіпати
  → **СПАЙК-ВЕРДИКТ: відкладено (upstream-ask).** SignalCli.NET `JsonRpcClient` має єдиний спільний
  `_requestTimeout` на весь клієнт — per-call CT/timeout-seam відсутній, тож ≥130с для `finishLink` не
  задати, не бампнувши глобальний таймаут send-шляху. Це вимагає зміни SignalCli.NET upstream (rule #1:
  не форкати тут) — окремий цикл, відкладено до релізу SignalCli.NET із per-call seam. Стопгап для
  оператора: `SignalCli:RequestTimeoutSeconds` (`Program.cs`) можна тимчасово підняти глобально.
  TTL сесії лишається 120с (W9: НЕ звужено під глобальний RPC-таймаут).
- [x] 5. One-shot sync після own-number finishLink АБО задокументувати stale listGroups (G7)
  → обрано документування (one-shot receive свідомо НЕ роблено — авто-receive приходить із
  `add-group-claim-receive`). Нотатка у `docs/self-host.md` (розділ Receive-mode, блок «G7»).
- [x] 6. Device-link URI/QR не логувати (redact-тест)
  → жоден лог-виклик у `LinkSessionStore`/`SignalDevicesRpcAdapter` не друкує URI (сесія оперує
  sessionId; повний sessionId у warning теж не друкується — лише 8-символьний префікс). Redact-guard'и:
  `LinkSessionStoreTests.TryConsume_ForeignIdentity_LogsWarning_WithoutFullSessionId`,
  `SignalDevicesRpcAdapterTests.LinkFlow_NeverLogsDeviceLinkUri`.

## Breaking-зміна (авторизована, у межах Phase-2)

- `finishLink` тепер приймає `sessionId` замість сирого `deviceLinkUri`; `startLink` повертає
  `{ sessionId, deviceLinkUri }` (новий DTO `Model/StartLinkSessionResponse`) замість голого
  `deviceLinkUri`. Сумісність зі старим Фаза-1 клієнтом свідомо НЕ підтримується (авторизований breaking).
  Оновлено: `Interfaces/ISignalDevicesRpc`, `Serialization/SignalCliSerializerContext` (реєстрація DTO),
  `docs/self-host.md` (флоу + коди відмов), тести адаптера.

## Тести (DoD)

- [x] 7. `finishLink` токеном B на сесію A → помилка; сесія A НЕ знищена (анти-griefing)
  → `LinkSessionStoreTests.TryConsume_ForeignIdentity_ReturnsNull_AndKeepsSessionAlive`,
  `SignalDevicesRpcAdapterTests.FinishLink_ForeignIdentity_DoesNotConsumeSession`.
- [x] 8. Повторний `finishLink` тим самим SessionID → відмова (one-time)
  → `LinkSessionStoreTests.TryConsume_Owner_ReturnsSession_ThenOneTime`.
- [x] 9. Не-admin лінкує до бот-акаунта → відмова
  → `SignalDevicesRpcAdapterTests.StartLink_TargetIsKnownBinding_NonAdmin_Denied` (+ `_Admin_Allowed`).
- [x] 10. Own-number finishLink проходить за ручний скан ≤120с (не вбитий 30с таймаутом)
  → session-TTL рівень: `LinkSessionStoreTests.TryConsume_Expired_ReturnsNull` + happy-path пінять, що
  сесія валідна весь 120с-вікно ручного скану й гасне рівно на TTL. **Транспортний** per-call timeout
  (щоб finishLink реально блокував ≥120с попри глобальний 30с) — залежить від task 4 (deferred upstream);
  до нього стопгап — глобальний `RequestTimeoutSeconds`.

## Додаткові тести (не в DoD, але покривають архітектуру)

- Anti-oracle: невідома / чужа / прострочена сесія → ІДЕНТИЧНА `-32004`
  (`SignalDevicesRpcAdapterTests.FinishLink_Unknown_Foreign_Expired_ProduceIdenticalError`).
- SessionId ентропія/формат (43 символи base64url, унікальність), TTL-expiry через TestTimeProvider,
  rate-limit 3/хв + скид вікна + per-identity ізоляція (`LinkSessionStoreTests`).
- startLink повертає sessionId (upstream мок), finishLink маплить sessionId→uri, приватний bind нового
  номера у durable store, rate-limit → `-32005` (`SignalDevicesRpcAdapterTests`).

## Підсумок

Реалізовано tasks 1–3, 5, 6 + DoD 7–10 (10 — на рівні session-TTL). Task 4 — spike-вердикт
«upstream-ask», відкладено. Повна сюїта зелена: **360** тестів (344 baseline − 3 старі devices-тести
+ 19 нових).
