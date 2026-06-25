# План: WsRpcServer → бекенд Signal-каналу для модуля сповіщень Потужності

## Контекст і межі

WsRpcServer — **тільки транспорт Signal-каналу** універсального модуля сповіщень. Детект подій (CREATED/UPDATED/DELETED, layer health, стрім-логи VEZHA, помилки обробки), підписки, UI, Chrome push, element — усе на боці розширення (Потужність). Цей сервер лише надійно шле в Signal та керує акаунтами/пристроями/групами.

Поточний стан (станом на старт плану):
- Ядрові RPC-методи працюють end-to-end A→B: `listAccounts`, `startLink`, `finishLink`, `sendTextMessage`.
- `ISignalGroupsRpc` / `SignalGroupsRpcAdapter` — написані, але **не зареєстровані в DI** (`Extensions/SignalRpcExtensions.cs`), 3 файли незакоммічені.
- Хост standalone є (`Program.cs`, `0.0.0.0:9000`), signal-cli у JSON-RPC daemon-режимі через NuGet `SignalCli.NET 4.10.0`.
- Тестів — **0**. Auth / ізоляції користувачів — **нема**.
- База JSON-RPC.NET уже містить `Authorization/` (RpcAuthorizeAttribute, IRpcAuthorizationPolicy, StaticRoleMapAuthorizationPolicy), `Security/` (mTLS, NodeIdentity, TLS), connection quotas, `WsRpcServerDiagnostics` — **підключаємо, не пишемо з нуля**.

## Уточнення з рев'ю (внесено)

Зовнішнє рев'ю + чек коду SignalCli.NET дали 4 рішення:

1. **Модель зʼєднання — ЗАФІКСОВАНО: connect-on-demand для MVP.** MV3 Service Worker ефемерний (~30с idle → kill), персистентний WS вмирає з ним і дає reconnect-шторм. Сповіщення — потік **вихідний**, тому MVP: `connect → authenticate → send → close` на нотіф (немає півживого сокета, немає keep-alive-гімнастики). Персистентний WS + keep-alive (`chrome.alarms`) + exponential-backoff-with-jitter — **лише** коли додамо receive-шлях (Phase 3). Це клієнтський контракт (Потужність), сервер під нього готує backoff-friendly відповіді.
2. **Backoff-friendly сервер.** Проти reconnect-шторму: `429` + `Retry-After` на перевищенні квоти, connection-quota per-IP/identity (Phase 2, rate-limit).
3. **signal-cli IPC — НЕ вузьке місце (перевірено в коді).** SignalCli.NET уже: async `ReadLineAsync` на bg-таску (`JsonRpcClient.cs:286`), serialized async-запис `SemaphoreSlim(1,1)` (`:568,584`), кореляція `ConcurrentDictionary<id,TCS>` (`:42,356`) → паралельні in-flight, повільна відповідь не блокує інших і не блокує thread-pool WS-сервера; нотифікації через bounded `Channel(1024, Wait)` (`:101`). Socket-режим не юзається і **не потрібен** — pipe не bottleneck. Реальний cap — рейт-ліміти мережі Signal. → Жодної роботи тут, лише **perf-gate перед оптимізацією** (Phase 3).
   - **Caveat (receive-шлях):** один stdout-loop + notification-channel `Wait` → повільний споживач нотифікацій робить head-of-line блок і для RPC-відповідей. Стосується лише Phase 3 (subscribe).
4. **Агрегація/debounce/coalesce подій — НЕ цей репо.** «50 апдейтів обʼєкта ≠ 50 пушів» — шар підписок розширення (Потужність), не WsRpcServer. Фіксуємо в плані ідеї/клієнта.

## Глобальний Definition of Done (для КОЖНОЇ фази)

Фаза не закрита, поки не виконані всі три умови:

1. **Реально працює на e2e** — ручний прогін + автоматичний e2e-тест зелені проти живого signal-cli (не моки).
2. **Покрито тестами** — кожен новий публічний метод / гілка має тест; CI зелений; негативні шляхи теж покриті.
3. **Документація написана** — відповідний гайд (self-host / shared-bot / ops) оновлено в `docs/`, API-методи описані.

## Стратегія делегування субагентам

Головний потік тільки **оркеструє і рев'ює дифи**, не читає файли цілком. Точкові задачі — субагентам, щоб не засмічувати контекст:

| Тип задачі | Агент | Навіщо |
|---|---|---|
| Знайти де щось визначено / хто викликає | `cavecrew-investigator` | повертає `file:line`, ~60% менше токенів |
| Правка 1-2 файлів (DI-реєстрація, дрібний фікс) | `cavecrew-builder` | хірургічний дифф-чек |
| Рев'ю дифу/гілки перед коммітом | `cavecrew-reviewer` | один рядок на знахідку |
| Мапа підсистеми / широкий пошук | `Explore` | читає уривки, не цілі файли |
| Багатофайлова фіча + тести разом | `general-purpose` | автономний, повертає підсумок |

Правило: якщо задача — «знайти», «змінити 1-2 файли» або «рев'ю» — **не роблю інлайн**, делегую. Інлайн лишається лише фінальна збірка результатів і рішення.

---

## Фаза 1 — Self-host Signal-send бекенд (path C: «свій RPC сервер»)

**Мета:** WsRpcServer — робочий, протестований, задокументований однокористувацький Signal-сервер. Однокористувацький → auth не потрібен.

### Задачі
1. **Закрити WIP групи.** Додати `AddScoped<ISignalGroupsRpc, SignalGroupsRpcAdapter>()` у `Extensions/SignalRpcExtensions.cs`. Закоммітити 3 файли.
   → `cavecrew-builder` (1 файл правка + коміт-діфф).
2. **Контейнеризація — фіналізувати наявне.** `deploy/Dockerfile*` + `deploy/DEPLOYMENT.md` уже є (JDK 25 / Temurin, signal-cli 0.14.3). Перевірити збірку, додати `docker-compose.yml` якщо бракує, звірити приватний feed (`build-local-feed.sh` / `NuGet.Config` packageSourceMapping) для офлайн-збірки.
   → `general-purpose` (звірити Dockerfile, compose, smoke up).
3. **Health/readiness ендпоінт** для контейнера.
   → `cavecrew-builder`.
4. **Інтеграційні тести RPC-адаптерів** (мок `ISignal*`): `listAccounts`, `startLink`, `finishLink`, `sendTextMessage`, `listGroups` — happy + негативні (порожні recipients, невалідний account).
   → `general-purpose` (створює тест-проект `tests/`, бо його зараз нема).
5. **e2e-смоук:** clean `docker compose up` → link номера → надсилання тексту реальному отримувачу → `listGroups`.
   → ручний прогін + скриптований e2e.
6. **Docs:** `docs/self-host.md` — як підняти свій сервер за 5 хв, таблиця конфігу (`Server:Host/Port`, `SignalCli:LibDirectory/AppHome`).
   → `general-purpose`.

### Умови зупинки (DoD Фази 1)
- [ ] e2e: з чистого docker up — link, send (доставлено реально), listGroups — усе зелене (ручний + авто).
- [ ] Кожен з 5 RPC-методів має інтеграційний тест (happy + ≥1 негативний); CI зелений.
- [ ] `docs/self-host.md` готовий, конфіг задокументований.
- [ ] Гілка пройшла `cavecrew-reviewer`, зміни закоммічені/запушені.

---

## Фаза 2 — Мультитенант + безпека (path A/B: «спільний бот»)

**Мета:** сервер безпечно хостить багатьох. AuthN + ізоляція акаунтів на фреймворковому `Authorization/`+`Security/`.

### Модель бота — ЗАФІКСОВАНО: поєднання обох варіантів
Сервер підтримує одночасно:
- **Спільний бот-номер «Потужність»** — один дефолтний акаунт сервера; будь-який автентифікований юзер шле через нього на власних отримувачів; нуль налаштувань (галочка).
- **Свій лінкований номер** — хто хоче, лінкує власний номер (QR / device-link); цей акаунт **приватний для юзера**, що його прив'язав.

Наслідок: **повна ізоляція обов'язкова** (бо свій-номер шлях її вимагає). Правила доступу:
- `listAccounts` → повертає спільний бот-акаунт (read-only) **+** власні лінковані акаунти caller. Чужі приватні — не видно.
- `sendTextMessage` / `listGroups` → дозволений `account ∈ {спільний бот} ∪ {власні caller}`.
- `finishLink` → прив'язує новий номер приватно до caller.
- Деструктивні/девайс-операції над спільним бот-акаунтом → лише admin (звичайний юзер не може unregister / removeDevice бота).

### AuthN/AuthZ — ЗАФІКСОВАНО на основі дослідження безпеки (OWASP / RFC / k8s)
Delta SSO відсутній → JWT/SSO відпадає. mTLS — лише node-to-node/admin, не для розширення.
Базовий факт: **браузерний `WebSocket` не вміє кастомні заголовки** → `Authorization: Bearer` неможливий.

Обраний стек по шарах (джерела — OWASP WebSocket / Multi-Tenant / ASVS / Secrets, RFC 8725, k8s):

| Шар | Рішення | Runner-up |
|---|---|---|
| Транспорт | `wss://` only, **TLS 1.3** (1.2 floor), AEAD-шифри, **HSTS**. Cert-pinning НЕ робити (HPKP deprecated) | — |
| Доставка креденшела | **k8s-стиль subprotocol-токен** у `Sec-WebSocket-Protocol`: `base64url.bearer.<token>` + другий реальний субпротокол. Auth на HTTP-upgrade → відмова **401 до апгрейду**, нема стану неавтентиф. сокета | first-message `authenticate(token)` з **жорстким auth-таймаутом** + cap неавтентиф. сокетів/IP |
| Формат токена | **opaque ≥256-bit CSPRNG**, prefix + checksum (GitHub-стиль `ptzh_…`), серверний стор → миттєвий revoke | PASETO v4.local (лише якщо колись треба stateless) |
| Токен at-rest | **HMAC-SHA256 з окремо збереженим pepper** (швидкий хеш; argon2/bcrypt НЕ треба для ≥112-bit ентропії — ASVS V2.9). Порівняння constant-time | salted SHA-256 |
| Сховище токена в розширенні (персист) | **`chrome.storage.local`** (єдиний first-party персист) + токен зашифрований **non-extractable WebCrypto AES-GCM ключем у IndexedDB** (`extractable:false` → raw-ключ недоступний JS); читати **тільки в background SW**, не в content-скриптах; серверний revoke = головна гарантія. **НЕ `.sync`** (тече в Google-хмару). `.session` — опція «не запам'ятовувати» | без шифру: `.local` + короткий TTL + revoke (браузер НЕ шифрує at-rest) |
| AuthZ | **per-connection principal**, deny-by-default на кожен RPC, скоуп `(identity, account)`. Набір акаунтів **виводиться з principal, НІКОЛИ з аргумента клієнта** (захист від confused-deputy/IDOR) | RBAC зверху |
| Rate-limit/DoS | per-token cap конектів + msg-rate (~100/хв) + msg-size cap (64KB) + auth-timeout + per-IP cap неавтентиф. сокетів | per-IP only |
| Дані at-rest | **AES-256-GCM** на signal-cli storage; майстер-ключ у secrets-manager (не на тому ж диску); дир `0700`, окремий сервіс-юзер; ротація | OS full-disk як backstop |

Identity record: `{ id, displayName?, role (user|admin), linkedAccounts[], createdAt }`. Revoke = прибрати зі стору + розрив живих конектів. Identity-provider абстрактний (під майбутній Delta SSO).

### Задачі
1. **AuthN-хендшейк:** валідація subprotocol-токена на HTTP-upgrade (відмова 401 до апгрейду), echo лише non-auth субпротоколу. Fallback first-message `authenticate` з auth-таймаутом + cap неавтентиф. сокетів.
   → `cavecrew-investigator` (де JSON-RPC.NET приймає upgrade / читає `Sec-WebSocket-Protocol` / per-connection principal) → `general-purpose` (вшити).
2. **Token store + видача/відкликання:** стор `HMAC-SHA256(token, pepper) → identity`, pepper окремо від БД; токени `prefix+random≥256-bit+checksum`; admin-видача (інвайт-коди) через admin-RPC або CLI; ротація/revoke + розрив живих конектів.
   → `general-purpose`.
3. **Стор прив'язок** `identity → account(s)` (+ прапор спільного бот-акаунта, доступного всім).
   → `general-purpose`.
4. **AuthZ + ізоляція (deny-by-default):** `listAccounts` = спільний бот + власні caller; `sendTextMessage`/`listGroups` дозволені на `{бот} ∪ {власні}`; `finishLink` прив'язує номер приватно до caller; деструктив над ботом — лише admin. **Набір акаунтів виводиться з principal, не з аргумента клієнта** (анти-IDOR).
   → `general-purpose`.
5. **Rate-limit/DoS + backoff-friendly:** per-token cap конектів + msg-rate (~100/хв) + msg-size cap (64KB) + auth-timeout + per-IP cap неавтентиф. сокетів (фреймворковий ConnectionQuota як база). На перевищенні — `429` + `Retry-After` (проти reconnect-шторму MV3 SW).
   → `cavecrew-builder`.
6. **Hardening at-rest + транспорт:** AES-256-GCM на signal-cli storage (`SignalCliStorageData/`), майстер-ключ у secrets-manager/env (не на тому ж диску), дир `0700`, окремий сервіс-юзер; `wss://` TLS 1.3 (1.2 floor) + HSTS у деплої.
   → `general-purpose`.
7. **Тести ізоляції + auth:** user A не бачить/не шле від акаунта user B (анти-IDOR); конект з невалідним/без токена → 401; revoke рве конект; rate-limit спрацьовує.
   → `general-purpose`.
8. **Docs:** `docs/shared-bot.md` — деплой спільного сервера + налаштування auth + at-rest. Токени/номери **не логувати** (privacy-контракт репо).

### Умови зупинки (DoD Фази 2)
- [ ] e2e (спільний бот): автентифікований юзер шле через бот-номер на свого отримувача — доставлено.
- [ ] e2e (свій номер + ізоляція): A лінкує свій номер; A не дістає приватний акаунт B (тест + вручну); невалідний токен → 401; revoke рве живий конект; rate-limit тригериться.
- [ ] Позитивні + негативні auth/ізоляційні тести зелені (включно анти-IDOR: спроба A назвати акаунт B); CI зелений.
- [ ] at-rest шифрування ввімкнене, ключ поза диском даних; `wss`/HSTS у деплої.
- [ ] `docs/shared-bot.md` готовий.
- [ ] Security-рев'ю дифу (`cavecrew-reviewer` + ручний погляд на межі ізоляції).

---

## Фаза 3 — Операційна готовність + release

**Мета:** production-ready: діагностика/метрики, відновлення, логування, опційно приймання подій, release-пайплайн.

### Задачі
1. **Метрики/діагностика** — вшити `WsRpcServerDiagnostics` (лічильники конектів, RPC-викликів, помилок).
   → `cavecrew-builder`.
2. **Структуровані логи + таксономія помилок** на RPC-межі (єдиний формат `RpcErrorException`).
   → `general-purpose`.
3. **Контракт зʼєднання + reconnect/backoff** для клієнта — задокументувати: MVP connect-on-demand (send-only); персист+keep-alive+exponential-backoff-with-jitter лише з receive; поведінка при падінні signal-cli; реакція на `429`/`Retry-After`.
   → docs-задача.
4. **Perf-gate перед оптимізацією IPC.** Load-test pipe під реальним сплеском **перш ніж** щось чіпати. Базлайн: SignalCli.NET уже async/TCS/bounded-channel (не bottleneck — див. «Уточнення з рев'ю»). Socket-режим signal-cli — лише якщо тест докаже потребу.
   → `general-purpose` (load-smoke + рішення).
5. **(Опційно) Receive/events** — `ISignalEventsRpc` (subscribe), якщо нотіфам потрібні delivery-receipts чи вхідні. Інакше відкласти. **Caveat:** один stdout-loop + notification-channel `Wait` → повільний споживач робить head-of-line блок і RPC-відповідям; передбачити окремий споживач / drop-policy.
   → `Explore` (оцінити events-шар) → рішення робити/відкласти.
6. **Тести fail-path:** signal-cli впав посеред роботи → сервер відновлюється; невалідний ввід → коректна RPC-помилка.
   → `general-purpose`.
7. **CI:** build + test + docker publish.
   → `general-purpose`.
8. **Docs:** `docs/ops-runbook.md` + повний API-reference усіх RPC-методів.

### Умови зупинки (DoD Фази 3)
- [ ] e2e: вбити signal-cli посеред прогону → сервер відновлюється; метрики показують лічильники; смоук на N конектів.
- [ ] Fail-path тести зелені; CI повний (build+test+publish) зелений.
- [ ] `docs/ops-runbook.md` + API-reference готові.
- [ ] Фінальне рев'ю гілки.

---

## Порядок і залежності

```
Фаза 1 (self-host) ──► Фаза 2 (shared-bot: спільний номер + свій номер, повна ізоляція) ──► Фаза 3 (ops/release)
        │
        └─ Фаза 3 п.1-2 (метрики/логи) можна тягнути паралельно після Фази 1
```

Кожна фаза мерджиться в `main` лише після свого DoD. Жодна фаза не «майже готова» — або всі три умови (e2e + тести + докси), або не закрита.
