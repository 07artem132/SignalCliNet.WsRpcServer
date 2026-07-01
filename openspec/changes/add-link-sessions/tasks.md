# Tasks: add-link-sessions

- [ ] 1. In-memory link-session стор: 256-bit SessionID, TTL 120с, one-time, 3/хв/identity
- [ ] 2. `finishLink` атомарне умовне гашення (in-process lock; mismatch identity → сесію НЕ видаляти)
- [ ] 3. Політика: target=shared-bot → admin-only; новий номер → приватний bind до caller
- [ ] 4. Спайк + впровадження per-call timeout ≥130с для `finishLink` (W9); глобальний 30с не чіпати
- [ ] 5. One-shot sync після own-number finishLink АБО задокументувати stale listGroups (G7)
- [ ] 6. Device-link URI/QR не логувати (redact-тест)

## Тести (DoD)
- [ ] 7. `finishLink` токеном B на сесію A → помилка; сесія A НЕ знищена (анти-griefing)
- [ ] 8. Повторний `finishLink` тим самим SessionID → відмова (one-time)
- [ ] 9. Не-admin лінкує до бот-акаунта → відмова
- [ ] 10. Own-number finishLink проходить за ручний скан ≤120с (не вбитий 30с таймаутом)
