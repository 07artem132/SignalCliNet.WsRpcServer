# add-team-notify

## Чому
Споживач «Потужність» (дизайн: `mil-development/potuzhnist` `docs/notifications-signal-design.md`, PR #908)
хоче сповіщати команду в Signal-групи без вільного тексту. Нинішня поверхня для цього не підходить:

- `sendTextMessage` приймає довільний текст: будь-яке автентифіковане identity може надіслати що завгодно
  в групу, на яку має binding. Межа «без ДСК» тримається лише на чесності клієнта;
- identity = один пристрій (TOFU, `add-authn-token-pop`), bindings і dedup ведуться на identity
  (`add-group-claim-receive`, `add-ops-observability` 3.3). Команда з кількох пристроїв не має спільних
  груп і спільного захисту від дублів: та сама подія з двох вкладок піде двічі;
- група адресується ідентифікатором Signal, а клієнту потрібен стабільний псевдонім
  (`all`, `dir:<id>`, `login:<id>`, `admin`).

## Що змінюється
- **Команда (team)** — нова durable-сутність. Identity належить не більше ніж одній команді.
  Інвайт отримує `teamId` і `maxRedemptions` (≥1, дефолт 1 = нинішня поведінка): кожне погашення
  створює окреме identity в цій команді (TOFU per-device лишається).
- **Командні псевдоніми прив'язок.** `requestGroupClaim(alias?)`: з `alias` binding стає командним
  `(team, alias) → group`; без `alias` — нинішній per-identity binding. Anchor-модель (T2), TTL і
  quota не змінюються. Перепривʼязка того ж alias атомарно замінює стару групу.
- **Каталог шаблонів** — файл оператора (R3.3: шлюз consumer-agnostic, тексти Потужності не вшиті
  в код). Шаблон = `kind`, текст із плейсхолдерами, схема параметрів: лише `int` у `[min,max]` та
  `enum` зі списку значень → текст. Каталог валідується fail-fast на старті.
- **RPC `notify(kind, alias, dedupKey, params)`** через chokepoint:
  alias має належати команді виклику → params строго за схемою (зайві/відсутні/поза межами = `-32602`)
  → dedup `(team, alias, dedupKey)` durable з TTL → coalescing → send у групу через нинішній
  `GroupSendGate` (anchor-перевірка). `dedupKey` — непрозорий хеш від клієнта (≤64 байти), шлюз
  його не тлумачить.
- **Coalescing:** перша подія `(team, alias)` іде одразу; наступні у вікні (дефолт 5 хв) копляться
  і йдуть одним повідомленням наприкінці вікна (шаблони з `coalesce: join`). Алярм не затримується.
- **`login:<id>`:** перед send перевірити, що в групі рівно двоє (бот + адресат); інакше відмова
  без revoke (людина могла тимчасово додати когось).
- **`sendTextMessage` лише для ролі `raw-send`/admin.** Звичайне командне identity отримує `-32001`.
  Конфіг-прапорець `Notify:AllowRawSendForMembers` (дефолт `false`) — перехідний вихід для наявних
  консьюмерів.
- **Ретеншн:** фоновий purge dedup-рядків старших за TTL і audit/abuse-журналів за строком.

## Не змінюється
- Транспорт і authn: `wss` + bearer-субпротокол + PoP (`authn`), admin mTLS (`admin-and-invites`).
- Наскрізного шифрування вмісту немає й не додається: шлюз мусить бачити параметри, щоб зібрати
  текст. Межа ДСК = білий список параметрів у каталозі.

## Відкриті рішення (для власника)
1. Coalescing: «перша одразу + решта в кінці вікна» чи «все в кінці вікна»?
2. Чи потрібен перехідний `Notify:AllowRawSendForMembers`, чи `sendTextMessage` для членів команди
   закриваємо одразу (breaking для нинішніх клієнтів)?
3. Хто створює команду: лише admin-CLI (`createTeam`), чи admin команди теж може видавати інвайти
   у свою команду?

## Вплив
- Код: `Persistence/` (міграція: `teams`, `identities.team_id`, `invites.team_id/max_redemptions/redemptions`,
  `team_bindings`, `notify_dedup`), `Security/` (notify-політика, роль `raw-send`), новий
  `Notify/` (каталог, рендер, coalescer), RPC `ISignalNotifyRpc`, admin-RPC `createTeam`/`createInvite(teamId, maxRedemptions)`.
- Specs: нова `team-notify`; MODIFIED `admin-and-invites` (multi-use інвайт у команду),
  `group-claim` (командний alias), `authz-isolation` (`sendTextMessage` за роллю).
- Версія: minor (2.3.0); breaking лише якщо рішення 2 = «закрити одразу».
