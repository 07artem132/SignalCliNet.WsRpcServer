# add-link-sessions

## Чому
`startLink`/`finishLink` — шлях takeover бот-акаунта і griefing чужих лінк-сесій;
30с глобальний RPC-таймаут тісніший за link-TTL 120с (W9), ручний QR-скан не встигає.

## Що змінюється
- Link-session state-machine: `SessionID` 256-bit CSPRNG, in-memory стор
  `SessionID → (CreatedByIdentityId, TargetAccount, ExpiresAt)`, TTL 120с, one-time,
  rate-limit 3/хв/identity.
- `finishLink`: атомарне умовне гашення під in-process lock — видалити сесію ЛИШЕ якщо
  identity ініціатора збігся; mismatch → НЕ видаляти (анти-griefing) + лог-alert.
- Лінк із target = спільний бот → лише admin (захист від takeover); новий свій номер →
  allow + приватний bind до caller (R3.5).
- D16+W9-рішення: TTL лишається 120с; `finishLink` — per-call timeout ≥130с
  (НЕ глобальний bump `RequestTimeoutSeconds`); спайк: чи приймає SignalCli.NET per-call CT.
- G7: після own-number finishLink — one-shot receive/sync для груп/контактів
  (moot, якщо `add-group-claim-receive` вже ввімкнув авто-receive — звірити порядок мерджу).
- Device-link URI/QR — чутливе: не логувати.

## Вплив
- Код: `Security/LinkSessionStore`, правки devices-адаптера/chokepoint-політики.
- Specs: `account-linking` (нова capability).
- Спайки: per-call timeout у SignalCli.NET (інакше upstream-ask — rule #1: не форкати тут).
