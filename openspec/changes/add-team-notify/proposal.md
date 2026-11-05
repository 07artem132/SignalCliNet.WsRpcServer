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
- **Team-admin видає інвайти у свою команду.** Admin (mTLS) створює команду і перший інвайт із роллю
  `team-admin`. Далі identity з роллю `team-admin` через звичайний WS (token + PoP) викликає
  `createTeamInvite(maxRedemptions, ttl, role?)` лише у власну команду; роль `team-admin` у такому інвайті
  може видати тільки team-admin. Інвайти в чужу команду або без команди → `-32001`.
- **Команда не лишається без адміна.** Інваріант: у команді ≥1 активний `team-admin`.
  - team-admin може підвищити member до `team-admin` (`setTeamRole`), рекомендовано ≥2 на команду;
  - зняття ролі, self-revoke чи revoke останнього team-admin через team-RPC відмовляє (`-32013`),
    перевірка й зміна в одній транзакції (дві паралельні спроби не знімуть обох);
  - revoke identity адміном шлюзу (компрометація) дозволено завжди: безпека важливіша; команда
    переходить у стан «без адміна», це пишеться в audit і видно в `listTeams`;
  - відновлення: адмін шлюзу (mTLS) у будь-який момент видає в команду інвайт із роллю `team-admin`
    або підвищує наявне identity — це break-glass, з audit-записом;
  - втрачений пристрій team-admin (PoP-ключ не відновити) закривається тим самим break-glass шляхом.
- **Командні alias прив'язок.** `requestGroupClaim(alias?)`: з `alias` binding стає командним
  `(team, alias) → group` і доступний усім identity команди; без `alias` — нинішній per-identity binding.
  Anchor-модель (T2), TTL і quota не змінюються. Перепривʼязка того ж alias атомарно замінює групу.
- **Адресація за alias у `sendTextMessage`.** Recipient виду `alias:<alias>` резолвиться в групу
  командного binding-у виклику; далі нинішній `GroupSendGate`. Чужий або неіснуючий alias дає ту саму
  відмову `-32007` (anti-oracle).
- **Dedup за чатом.** Для групового recipient ключ dedup = `(groupId, messageId)` — за чатом-адресатом,
  а не за identity чи alias: повтор із будь-якого пристрою, команди чи alias, що веде в той самий чат,
  повертає результат першого виклику. Нинішній `(identity, messageId)` лишається для прямих (не групових) recipient.
- **Coalescing на шлюзі, за чатом.** Перше повідомлення в чат іде одразу; наступні в той самий чат у вікні
  (`Notify:CoalesceWindowSeconds`, дефолт 300) копляться і йдуть одним повідомленням наприкінці вікна
  (тексти через `; `). Кожен виклик одразу отримує результат «прийнято/об'єднано»; фінальний статус
  доставки об'єднаного повідомлення — в audit. Вимкнення: `Notify:CoalesceWindowSeconds = 0`.
- **Ретеншн:** фоновий purge dedup-рядків старших за TTL.

## Не змінюється
- Транспорт і authn: `wss` + bearer-субпротокол + PoP (`authn`), admin mTLS (`admin-and-invites`).
- `sendTextMessage` для звичайних identity, вільний текст, адресація за group-id/номером.

## Відкриті рішення (для власника)
_(немає)_

## Вплив
- Код: `Persistence/` (міграція: `teams`, `identities.team_id`, `invites.team_id/max_redemptions/redemptions`,
  `team_bindings`, ключ send-dedup за чатом, coalescer), `Security/RecipientValidator` (`alias:`),
  `GroupSendGate` (резолв alias), admin-RPC `createTeam`/`createInvite(teamId, maxRedemptions, role)`, team-RPC `createTeamInvite`.
- Specs: нова `team-notify`; MODIFIED `admin-and-invites` (multi-use інвайт у команду),
  `group-claim` (командний alias).
- Версія: minor (2.3.0), без breaking.
