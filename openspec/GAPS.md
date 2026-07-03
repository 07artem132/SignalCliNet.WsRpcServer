# Прогалини плану — S-серія (аналіз при OpenSpec-декомпозиції, 2026-07-01)

Знайдено при розкладанні `PLAN-notifications-backend.md` на OpenSpec-частини. Це прогалини
**понад** уже зафіксовані планом серії D/G/V/W/N/C/A/U: (а) внутрішні суперечності — superseded
текст живе поруч із рішенням-заміною; (б) рішення/мітигації без task/DoD-власника;
(в) нові логічні/безпекові діри. Формат плану: severity 🔴/🟠/🟡.

Попередньо звірено з кодом: **Фаза 1 і task-0 вже виконані** (csproj = JSON-RPC.NET 2.7.0,
bind `127.0.0.1`, Groups DI, tests+CI, health, self-host.md, privacy-фікси A2/W6 — у
`SignalRpcSession.cs` навіть є коментар-заборона `ConsoleTraceListener`). Розділ плану
«Поточний стан» і 🔴-блокер V1 — **застарілі як опис реальності**, хоч і виконані як задачі.

## (а) Внутрішні суперечності (superseded-текст не вичищено)

**🟠 S1. G9-формулювання не зведені з R3.2.**
Модель бота (§717) і DoD (§836) досі кажуть «`listGroups` спільного бота → admin-only АБО
accepted-risk», тоді як R3.2 фіксує жорстке правило «ВИКЛЮЧНО claim'нуті/власні, повністю
закриває G9». Імплементер, що читає таблицю/DoD, збудує слабший варіант.
→ Канон: R3.2; зафіксовано в `add-authz-chokepoint`.

**🟠 S2. LUKS vs платформний encrypted volume.**
R3.7 явно supersede-ить LUKS-формулювання, але табл. «Дані at-rest» (§739), task-9 (§796)
і DoD (§826) досі вимагають LUKS-том/LUKS-ключ. Два несумісні deployment-дизайни в одному
документі (app-LUKS ще й потребує `--privileged` — гірша безпека).
→ Канон: R3.7; зафіксовано в `add-secure-deploy-persistence`.

**🔴 S3. Admin-креденшел: token чи mTLS-cert — дві живі історії. ✅ ЗАКРИТО.**
R3.8: «адмін автентифікується mTLS client-cert, НЕ token/PoP». Водночас task-8 (§794),
G5-мітигація і R3.7 описували видачу **one-time admin-токена**. Не було зведено: що саме лежить
у `0600`-файлі, що робить bootstrap, який шлях перевіряє DoD G5. Ризик: імплементують обидва
шляхи → подвоєна поверхня привілейованого доступу без lifecycle для одного з них.
→ **РІШЕННЯ (канон):** admin-креденшел = **mTLS client-cert bundle** (PKCS12/PEM+ключ) у
`0600`-файлі, виданий авто-ген CA сервера (R3.8/R3.4). Історія «one-time admin-token»
(G5/R3.7-редакція) — **superseded**, не імплементувати. Зафіксовано в `add-invites-admin`
(proposal + tasks 2.1 `[x]`); spec `admin-and-invites` уже мав лише mTLS-вимогу.

**🟠 S4. R3.1 (авто-receive) не каскадований по тексту.**
Табл. рядок №1 (§39), Уточнення-1/3, Phase-2 табл. Rate-limit (§738 «send-only»), task-7
Фази 1 (§691 — «no-op» і «інвертувати» одночасно) лишились у send-only/manual-парадигмі.
Наслідки R3.1 (head-of-line тепер актуальний, privacy-експозиція receive) описані лише в R3.1.
→ Канон: R3.1; зафіксовано в `add-group-claim-receive`.

**🟠 S5. Frame-DoS: табл. Phase-2 суперечить U1-рішенню.**
Табл. Rate-limit/DoS (§738) досі вимагає «msg-size енфорс ПІД ЧАС накопичення фреймів» +
frame-count + zip-bomb-чек як app-роботу; U1-рішення виносить це в accepted-risk/L4
(app-seam'у немає), V6 доводить, що deflate неможливий. DoD (§817) оновлений, таблиця — ні.
→ Канон: U1/V3/V6; зафіксовано в `add-admission-rate-limits`.

## (б) Рішення без власника (task/DoD відсутні)

**🟠 S6. D11 (валідація recipient E.164/group-ID) — жодна Phase-2 задача і жоден DoD-рядок
її не містить.** Malformed recipient іде в signal-cli. → `add-authz-chokepoint` task 2.4.

**🟠 S7. Пакет unowned-мітигацій:** D15 (монотонний годинник), D17 (core dumps),
W21 (rotation zero-overlap — DoD має лише revoke-каскад), W24 (abuse-log equality-oracle —
per-record salt), W19 (прибрати хибну «constant-time» обіцянку), A4 (упорядкувати teardown
сесії ДО auth-стан-машини — названо ризиком, task-номера немає), W22 fault-injection тест
(R3.2 називає критичним — у DoD-матриці відсутній), N5 (V10/W10 без Phase-3 DoD).
→ Розкладено по changes: chokepoint (A4, W22), authn (W21, W19), admission (D15),
deploy (D17), invites-admin (W24), ops (V10/W10).

**🔴 S8. Серверні половини consumer-контракту заявлені, але не заплановані. ✅ ЗАКРИТО.**
R3.3 мандатує як генеричні серверні фічі: **idempotency-key/`messageId`** (C2) і
**server-heartbeat <25с** (C5). Спершу жодна задача Фаз 2/3 їх не імплементувала (Phase-3
task-3 — docs-only). Без idempotency: MV3 at-most-once + ретраї клієнта → дубль-send АБО тихі
втрати; взаємодія з бюджетом (ретрай не має декрементувати двічі) ніде не специфікована — це
безпекова взаємодія з анти-бан-механікою, не лише UX.
→ **ЗРОБЛЕНО:** `add-ops-observability` — tasks 3.2 (heartbeat) / 3.3 (idempotency), spec
`operations` має Requirement «Idempotency send-шляху» (+ scenario дубль-ретрай) і Requirement
«Server-heartbeat (persistent-WS)»; DoD 5.3 (idempotency) + 5.4 (heartbeat).

## (в) Нові логічні/безпекові прогалини

**🟠 S9. `Retry-After`-контракт мертвий для основного споживача.**
Браузерний `WebSocket` не бачить HTTP-статусу/заголовків фейленого upgrade → «HTTP 429 +
`Retry-After` до апгрейду» невидимі MV3-клієнту (він дістає лише generic `error`/`close`).
G13 фіксує одиниці/jitter, але не канал доставки. retry_after МУСИТЬ їхати in-band:
`-32005 data.retry_after` або close-reason `4429`; HTTP-гілка — лише для не-браузерних
клієнтів. → `add-admission-rate-limits` task 4.1.

**🟠 S10. Group-claim: три недоспецифіковані місця. ✅ ЗАКРИТО.**
(1) Ентропія/формат claim-коду не запінені (інвайти: ≥96-bit; клейми: «one-time, TTL 10 хв» —
все). (2) Membership churn: юзер вийшов/вигнаний із G — binding U↔G живе далі; ex-member
зберігає send-right у групу через бота безстроково (revocation покриває лише revoke identity
і видалення бота з G). (3) Delegation-by-proxy: код, вставлений у G будь-ким (не U),
грантить права U — соц-інженерний residual не задокументований.
→ **РІШЕННЯ (`add-group-claim-receive` tasks 0.1-0.3, spec `group-claim`):** (1) claim-код =
**≥96-bit CSPRNG** (як інвайти); (2) **перевірка членства U ∈ G при кожному send** (primary) +
**TTL binding'у** (backstop) → ex-member одразу втрачає send-right + binding інвалідується
(Requirement «Валідність binding при зміні членства», DoD 4.6); (3) delegation-by-proxy —
**accepted-risk**, код секретний до U й активує лише binding самого U (не грантить прав
вставнику) → док у `docs/shared-bot.md`.

**🟡 S11. R3.5-ізоляція incoming — інваріант без механізму. ✅ ЗАКРИТО.**
«Повідомлення own-account юзера A ніколи не сурфейсити B» заявлено (R3.5/W25-inbound), але
єдиний механізм у задачах — «сканер scoped до shared-bot». Phase-3 events-поверхня
(`ISignalEventsRpc`, W5-асиметрія: discovery може підняти адаптер БЕЗ DI-рядка) отримає
спільний notification-потік демона — потрібен per-account routing/filter на receive-шляху
як окремий компонент, інакше інваріант тримається лише на «поверхні ще нема». W25
(daemon-внутрішній cross-account стан) так і лишився «аудит АБО accepted-risk» без вибору.
→ **РІШЕННЯ (`add-group-claim-receive`):** додано **per-account receive-роутер**
(`account → owning-identity`) як єдиний вузол фанауту — own-linked → лише власнику,
shared-bot → лише сканеру; усі consumer-и (вкл. Phase-3 subscribe) підписуються ЧЕРЕЗ роутер
(Requirement «Per-account receive-роутер», tasks 1.4, DoD 4.7). **W25 → accepted-risk**:
daemon-internal cross-account leak — відомий residual (док `docs/shared-bot.md`; own-number
linking у спільний демон не рекомендувати), app-фільтр тримає ізоляцію на поверхні (task 1.5).

**🟡 S12. План застарів відносно коду (доc-drift, не безпека). ✅ ЗАКРИТО.**
«Поточний стан», V1-блокер, task-7-суперечка, «тестів 0» — уже неправда (див. шапку).
Append-only формат звірок вичерпав себе: 7 ітерацій дали 4 пари «твердження ↔ заперечення»
в одному файлі (S1/S2/S4/S5). Ця OpenSpec-структура і є мітигація: as-built → `specs/`,
плановане → `changes/`, рішення інлайнені канонічно, суперечності зняті.
→ **ЗРОБЛЕНО:** `PLAN-notifications-backend.md` позначено **архівним** (банер угорі → канон у
`openspec/`), а розділ «Поточний стан» оновлено під реальність 2026-07-02 (Фаза 1 + task-0
виконані: JSON-RPC.NET 2.7.0, Groups у DI, тести/CI/health є, bind `127.0.0.1`). Тіло журналу
рішень (D/G/V/W/N/C/A/U/R/S) лишили як історію — на нього посилаються change-и.

## Підсумок

| Severity | IDs | Статус |
|---|---|---|
| 🔴 | S3 | ✅ ЗАКРИТО — канон = mTLS client-cert bundle; token-шлях superseded (`add-invites-admin`) |
| 🔴 | S8 | ✅ ЗАКРИТО — idempotency + server-heartbeat специфіковані в `add-ops-observability` (spec + tasks 3.2/3.3, DoD 5.3/5.4) |
| 🟠 | S1, S2, S4, S5, S6, S7, S9 | ✅ ЗАКРИТО — аудит трасування пройдено (див. блок нижче): кожна має конкретний task + DoD-рядок |
| 🟠 | S10 | ✅ ЗАКРИТО — entropy ≥96-bit / membership-churn / delegation-residual (`add-group-claim-receive`) |
| 🟡 | S11 | ✅ ЗАКРИТО — per-account receive-роутер + W25 accepted-risk (`add-group-claim-receive`) |
| 🟡 | S12 | ✅ ЗАКРИТО — `PLAN-*.md` заархівовано, «Поточний стан» оновлено, канон у `openspec/` |

## Аудит трасування 🟠-групи (2026-07-02)

Перевірено, що кожна прогалина має конкретний task + DoD-рядок у своїй зміні; виправлено
знайдені дефекти трасування.

| Gap | Канон | Task | DoD | Примітка аудиту |
|---|---|---|---|---|
| S1 | R3.2 (`listGroups` лише claim'нуті/власні, закриває G9) | authz-chokepoint 2.3 | 4.3 | ok |
| S2 | R3.7 (платформний encrypted volume, НЕ app-LUKS) | secure-deploy 2.1 | 5.2 | ok |
| S4 | R3.1 (безперервний авто-receive) | group-claim 1.1 | — | **fix:** додано `MODIFIED ws-jsonrpc-gateway` receive-mode delta (проposal обіцяв, spec бракував) |
| S5 | U1/V3/V6 (frame-тріада → accepted-risk/L4; 64KB cap) | admission 3.1/3.4 | 5.5 | ok |
| S6 | D11 recipient-валідація на chokepoint | authz-chokepoint 2.4 | 4.5 | **fix:** додано DoD 4.5; admission помилково цитував S6 замість S7 (D15) — виправлено |
| S7 | пакет unowned-мітигацій | A4→chokepoint 1.4, W22→2.2, W21→authn 1.3, W19→authn 1.1, D15→admission 2.3, D17→deploy 1.4, W24→invites 3.3, N5→ops 2.1/2.3 | chokepoint 4.5, authn 4.x, ops 5.x | **fix:** chokepoint тепер цитує S7 (A4/W22); W19 отримав task-рядок |
| S9 | in-band retry_after (`-32005`/`4429`) | admission 4.1 | 5.6 | **fix:** додано DoD 5.6 |

Найсильніший загальний висновок: план **безпеково дуже зрілий** (7 проходів аудиту, більшість
класичних дір уже знайдені й закриті рішеннями), але його **append-only форма стала джерелом
нових дефектів** — чотири суперечності (а)-групи виникли саме тому, що рішення дописувались
у кінець, а базові розділи не правились. Декомпозиція на specs/changes прибирає цей клас.

**Стан S-серії: усі S1–S12 закриті** (рішення/task/DoD у відповідних changes; трасування звірено).

---

# Прогалини OpenSpec-структури — T-серія (незалежне рев'ю, 2026-07-03)

Знайдено при незалежній перевірці вже готової OpenSpec-декомпозиції (**понад** S-серію):
переважно логічні діри **на швах між change-ами** — клас, який неможливо побачити, читаючи
кожен change окремо. Формат той самий: severity 🔴/🟠/🟡; рішення інлайнені канонічно.

**🔴 T1. Граф залежностей суперечить змісту change-ів. ✅ ЗАКРИТО.**
`project.md` оголошував `add-secure-deploy-persistence` «паралельним 1-6», але саме він створює
фундамент, який споживають інші: durable SQLite-стор (authn — токени/device-секрети; admission —
budget store; group-claim — `account → owning-identity` + bindings; task 1.4 group-claim *прямо*
посилався на «стор із add-secure-deploy-persistence»), auto-gen CA (invites-admin — mTLS admin-cert)
і encrypted-том (`0600`-файли). Імплементер, що йде за таблицею, впирається у відсутній фундамент.
→ **РІШЕННЯ:** `add-secure-deploy-persistence` переміщено на позицію 2 порядку (залежностей
не має); `add-authn-token-pop`, `add-admission-rate-limits`, `add-invites-admin`,
`add-group-claim-receive` явно залежать від нього; кожен proposal-споживач називає конкретний
артефакт, який бере з фундаменту. Дробити change не стали — його внутрішні задачі 2.x/3.x
виконуються першими.

**🔴 T2. Membership-check S10.2 суперечив delegation-by-proxy S10.3 — механізму «U ∈ G» не існувало. ✅ ЗАКРИТО.**
Spec вимагав «перевірку членства (U ∈ G) при КОЖНОМУ send», але identity U — користувач
розширення з токеном, а членство — Signal-ідентифікатор у member-list групи; **звідки сервер
знає Signal-ідентифікатор U — не було сказано ніде**. Єдине природне джерело (відправник, що
вставив код у чат) ламається рівно в accepted-risk сценарії S10.3 (код може вставити не U).
→ **РІШЕННЯ (anchor-модель):** при confirm сканер фіксує в binding'у **ACI вставника коду**
(«anchor»; ACI, не E.164 — менше PII). Binding = `(identity U, group G, anchorAci)`; перевірка
членства при кожному send = чітко визначене `anchorAci ∈ members(G)`. У нормальному випадку
anchor = сам U (семантика «U вийшов → відмова» як задумано); у proxy-випадку право U живе,
поки вставник-поручитель лишається членом G — accepted-risk дістає чесну семантику. Перф:
кеш member-list з коротким TTL (~60с) + інвалідизація по group-update подіях авто-receive.
→ `add-group-claim-receive` (spec «Валідність binding», tasks 0.2/2.2/2.5, DoD 4.6/4.8).

**🟠 T3. Bootstrap-діра: щоб claim'нути G, треба знати group-ID, який нізвідки взяти. ✅ ЗАКРИТО.**
`requestGroupClaim(G)` вимагав ідентифікатор групи, але `listGroups` (R3.2) до claim'у її не
показує, а внутрішній group-ID Signal із клієнтського застосунку не видно — курка-і-яйце.
→ **РІШЕННЯ (group-agnostic код):** `requestGroupClaim()` видає код, прив'язаний лише до
(U, TTL, one-time); **група фіксується сканером** — binding створюється до тієї G, де код
фактично з'явився (+ anchor із T2). Discovery-поверхні не треба → R3.2/G9 не послаблюються.
Residual «чужий код вставили в іншу групу» дешевий: binding дає права лише самому U;
U бачить свої bindings (`listMyBindings`) і може revoke-нути зайвий; quota на активні
bindings уже була. → `add-group-claim-receive` (spec «One-time claim-код» + новий Requirement
самокерування bindings, tasks 2.1/2.2/2.6, DoD 4.9).

**🟠 T4. Auth-відмови невидимі браузерному клієнту — логіка S9 не була застосована до 401. ✅ ЗАКРИТО.**
«401 ДО апгрейду» на невалідний токен → браузерний WS бачить лише generic close 1006 і не
відрізняє «токен протух → re-auth» від «сервер лежить → ретрай» (та сама діра, що S9 для 429).
→ **РІШЕННЯ (дзеркало S9):** close-code **`4401`** з машиночитним reason (`expired`/`revoked`/
`invalid`): якщо в upgrade є синтаксично валідний токен-субпротокол, але токен невалідний —
сервер завершує upgrade і одразу закриває `4401`, нічого не виконуючи. Голий 401-до-апгрейду —
лише для запитів без токена. 4401-сокети рахуються в per-IP cap неавтентифікованих
(admission 3.2) — вартість зайвого upgrade обмежена. → `add-authn-token-pop`
(spec «Bearer через субпротокол», task 2.6, DoD 4.6).

**🟠 T5. Відновлення після протухання токена не специфіковане (TTL 12h блокував identity назавжди). ✅ ЗАКРИТО.**
Ротація вимагає валідного токена; клієнт, відсутній довше за TTL, лишався замкненим назовні,
а поведінка повторного інвайту (нова identity vs re-bind) не була визначена ніде.
→ **РІШЕННЯ (renewal через PoP):** device-ключ = довготривалий корінь (enrollment-rooted TOFU,
D1), токен = короткоживуча сесія. Через існуючий fallback `authenticate`: **прострочений
(НЕ revoked) токен + успішний PoP device-ключем → новий токен** тією самою W21-транзакцією
ротації; `IsRevoked` завжди виграє (revoked + валідний PoP → відмова — revocation лишається
головною гарантією D7); renewal під rate-limit per identity. Інвайти лишаються строго one-time.
→ `add-authn-token-pop` (новий Requirement, task 3.5, DoD 4.7).

**🟠 T6. Idempotency: durability стору й місце dedup у admission-ланцюжку не задані. ✅ ЗАКРИТО.**
In-memory dedup ламає «рівно один send» через рестарт (краш після send до відповіді → ретрай →
другий реальний send = over-send, який W13 так ретельно виключив для бюджету); місце dedup
відносно admission/бюджету не було специфіковане.
→ **РІШЕННЯ (симетрія з W13):** (1) стор **durable** у тій самій SQLite, ключ
`(identityId, messageId)` — без крос-тенантних колізій; (2) запис `pending` + декремент бюджету
в **одній транзакції ДО send**, після send — апдейт у `done` з результатом; (3) канон при
краші = **at-most-once**: ретрай, що знаходить `pending` після рестарту, дістає типізовану
помилку «outcome unknown» і НЕ тригерить авто-повтор (over-send → бан — безпекова властивість,
недосил допустимий — та сама логіка, якою W13 обрав форфейт); (4) dedup-lookup стоїть **ПЕРЕД**
admission-функцією (hit `done` → збережений результат без admission і декременту).
→ `add-ops-observability` (spec «Idempotency», task 3.3, DoD 5.3/5.5) + одне речення
в `admission-control` spec (Requirement «Єдине admission-правило»).

**🟡 T7. Роутер «shared-bot → ЛИШЕ сканеру» суперечив опційній Phase-3 events-поверхні. ✅ ЗАКРИТО.**
Будь-який майбутній events-підписник shared-bot потоку порушив би щойно прийнятий інваріант
буквально — Phase-3 почалась би з ревізії правила.
→ **РІШЕННЯ (forward-правило зараз, імплементація потім):** shared-bot-потік фанаутиться
(а) сканеру завжди; (б) events-consumer'у identity X — **лише** envelope'и груп, на які X має
активний binding (group-scoped фільтр по bindings із T2/T3); (в) решта shared-bot трафіку
(DM боту тощо) — admin-only або drop. → `add-group-claim-receive` (spec «Per-account
receive-роутер»), посилання з ops-proposal (events-surface).

**🟡 T8. Доc-дрейф CLAUDE.md. ✅ ЗАКРИТО.**
`CLAUDE.md` стверджував «Default listen address is `0.0.0.0:9000`» — код (`appsettings.json`)
і as-built spec кажуть `127.0.0.1:9000` (loopback-hardening Фази 1). Заодно виявилось, що
примітка CLAUDE.md про «stale JDK 21+ у README» сама застаріла — README уже каже JDK 25.
→ Виправлено обидва місця в `CLAUDE.md`.

## Трасування T-серії

| Gap | Severity | Рішення (канон) | Task | DoD |
|---|---|---|---|---|
| T1 | 🔴 | change 7 = фундамент, позиція 2; залежності 2/3/5/6 ← 7 | `project.md` табл. + proposals | — (структурне) |
| T2 | 🔴 | anchor-модель: binding несе ACI вставника; check = anchor ∈ G | group-claim 0.2/2.2/2.5 | 4.6, 4.8 |
| T3 | 🟠 | group-agnostic claim-код; G фіксує сканер; listMyBindings/revoke | group-claim 2.1/2.2/2.6 | 4.9 |
| T4 | 🟠 | in-band `4401` + reason; 401 лише для no-token | authn 2.6 | 4.6 |
| T5 | 🟠 | renewal: expired (не revoked) + PoP → новий токен (W21-транзакція) | authn 3.5 | 4.7 |
| T6 | 🟠 | durable dedup `(identity, messageId)`; pending-до-send; at-most-once; dedup ПЕРЕД admission | ops 3.3; admission spec | ops 5.3, 5.5 |
| T7 | 🟡 | forward-правило роутера: events лише по claim'нутих групах | group-claim spec (роутер) | — (правило) |
| T8 | 🟡 | CLAUDE.md: `127.0.0.1:9000`; прибрано stale-примітку про README | — | — (docs) |

**Стан T-серії: усі T1–T8 закриті** (2026-07-03; правки внесені у відповідні proposals/tasks/specs).
