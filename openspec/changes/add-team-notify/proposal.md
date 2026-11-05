# add-team-notify

## Чому
Споживач «Потужність» (дизайн: `mil-development/potuzhnist` `docs/notifications-signal-design.md`, PR #908)
хоче сповіщати команду в Signal-групи з кількох пристроїв. Нинішня поверхня для цього не підходить:

- identity = один пристрій (TOFU, `add-authn-token-pop`), bindings і dedup ведуться на identity
  (`add-group-claim-receive`, `add-ops-observability` 3.3). Команда з кількох пристроїв не має спільних
  груп і спільного захисту від дублів: та сама подія з двох вкладок піде двічі;
- група адресується ідентифікатором Signal, а клієнту потрібен стабільний псевдонім
  (`all`, `dir:<id>`, `login:<id>`, `admin`), однаковий для всіх пристроїв команди.

Безпекова межа лишається нинішньою: identity шле лише в групи, на які має binding з anchor-перевіркою
(`GroupSendGate`). Вміст повідомлення — відповідальність клієнта; шлюз текст не фільтрує, і
`sendTextMessage` лишається основним методом.

## Що змінюється
- **Команда (team)** — нова durable-сутність. Identity належить не більше ніж одній команді.
  Інвайт отримує `teamId` і `maxRedemptions` (≥1, дефолт 1 = нинішня поведінка): кожне погашення
  створює окреме identity в цій команді (TOFU per-device лишається).
- **Командні alias прив'язок.** `requestGroupClaim(alias?)`: з `alias` binding стає командним
  `(team, alias) → group` і доступний усім identity команди; без `alias` — нинішній per-identity binding.
  Anchor-модель (T2), TTL і quota не змінюються. Перепривʼязка того ж alias атомарно замінює групу.
- **Адресація за alias у `sendTextMessage`.** Recipient виду `alias:<alias>` резолвиться в групу
  командного binding-у виклику; далі нинішній `GroupSendGate`. Чужий або неіснуючий alias дає ту саму
  відмову `-32007` (anti-oracle).
- **Командний dedup.** Для alias-recipient ключ dedup = `(team, alias, messageId)` замість
  `(identity, messageId)`: повтор з будь-якого пристрою команди повертає результат першого виклику.
- **`login:<id>`:** перед send перевірити, що в групі рівно двоє (бот + адресат); інакше відмова
  без revoke.
- **Ретеншн:** фоновий purge dedup-рядків старших за TTL.

## Не змінюється
- Транспорт і authn: `wss` + bearer-субпротокол + PoP (`authn`), admin mTLS (`admin-and-invites`).
- `sendTextMessage` для звичайних identity, вільний текст, адресація за group-id/номером.

## Відкриті рішення (для власника)
1. Coalescing (об'єднання подій за 5 хв) — робити на шлюзі чи лишити клієнту?
2. Хто створює команду й видає в неї інвайти: лише admin-CLI чи також team-admin?

## Вплив
- Код: `Persistence/` (міграція: `teams`, `identities.team_id`, `invites.team_id/max_redemptions/redemptions`,
  `team_bindings`, командний ключ у send-dedup), `Security/RecipientValidator` (`alias:`),
  `GroupSendGate` (резолв alias), admin-RPC `createTeam`/`createInvite(teamId, maxRedemptions)`.
- Specs: нова `team-notify`; MODIFIED `admin-and-invites` (multi-use інвайт у команду),
  `group-claim` (командний alias).
- Версія: minor (2.3.0), без breaking.
