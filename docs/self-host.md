# Self-host: свій Signal-send сервер за 5 хвилин

`SignalCliNet.WsRpcServer` — standalone-сервіс, що відкриває Signal через один двонапрямний
**WebSocket JSON-RPC 2.0** канал. Цей гайд — для **однокористувацького self-host** (Фаза 1):
ти піднімаєш сервер для себе, лінкуєш свій номер і шлеш повідомлення.

> **⚠️ Безпека (критично для Фази 1).** У цій фазі **немає автентифікації**. Тому сервер
> **слухає лише `127.0.0.1`** (loopback) за замовчуванням. НЕ виставляй порт назовні без
> auth — інакше це відкрите Signal-send реле для всіх у мережі. Якщо тобі ОБОВ'ЯЗКОВО потрібен
> не-loopback bind у Фазі 1 — постав зовнішній firewall (nftables / cloud SG), що пускає лише
> довірені джерела, як передумову. Мультитенант + `wss://` + токени — це Фаза 2.

## Передумови

- **.NET 10.0+** — [завантажити](https://dotnet.microsoft.com/download/dotnet/10.0)
- **JDK 25+** — signal-cli 0.14.3 має class-file 69 (Java 25). Менша версія не запуститься.
- Зареєстрований номер Signal **або** свій номер для лінкування пристрою (QR).

## Запуск

```bash
dotnet build  --configuration Release
dotnet run --project src/SignalCliNet.WsRpcServer
```

Сервер підніме WS JSON-RPC на `Server:Host`/`Server:Port` (дефолт `127.0.0.1:9000`).

### Перший флоу (link → send)

1. `startLink(targetAccount?)` → повертає `{ sessionId, deviceLinkUri }`. `deviceLinkUri` — QR:
   відскануй у Signal → Linked devices. `sessionId` — одноразовий 256-бітний ідентифікатор link-сесії
   (TTL **120с**, rate-limit **3/хв на identity**). `targetAccount` — опційна підказка: якщо це вже
   прив'язаний (спільний) акаунт, старт лінкування дозволений лише admin/full-access principal (захист
   від takeover; не-admin → `-32001`).
2. `finishLink(sessionId, deviceName)` → завершує прив'язку. **Передається `sessionId`, а НЕ сирий
   `deviceLinkUri`** (breaking-зміна щодо Фази-1: старий клієнт із `finishLink(deviceLinkUri, …)` не
   сумісний). Сесію гасить лише та сама identity, що її створила (анти-griefing); успіх приватно
   прив'язує щойно-злінкований номер до caller (R3.5). Невідома / чужа / прострочена сесія → однакова
   помилка `-32004` (anti-oracle: клієнт не дізнається, який саме випадок).
3. `sendTextMessage(account, recipients, message)` → шле текст.
4. `listAccounts` / `listGroups(account)` → перегляд.
5. `ping` → `"pong"` (health, без стану — для контейнер-healthcheck).

> **Device-link URI/QR — чутливе.** Сервер НІКОЛИ не логує `deviceLinkUri`; клієнт після `startLink`
> оперує лише `sessionId`. Сирий URI живе тільки у in-memory сесії й іде в signal-cli напряму.

## Конфігурація (`appsettings.json`)

| Ключ | Дефолт | Опис |
|---|---|---|
| `Server:Host` | `127.0.0.1` | Адреса прослуховування. **Не став `0.0.0.0` без TLS/firewall** (див. `Server:AllowNonLoopback`). |
| `Server:Port` | `9000` | Порт WS. |
| `Server:MaxMessageSizeBytes` | `65536` | **V3.** Стеля розміру WS-кадру (64KB — дефолт деплою). Framework-дефолт бібліотеки — 100MB, завеликий для JSON-RPC-керівних кадрів; менша стеля обмежує пам'ять на кадр. Кадр понад ліміт → close `MessageTooBig` (1009). |
| `Server:AllowNonLoopback` | `false` | **D4 fail-closed.** Свідомий opt-in для не-loopback bind. Сервер **відмовиться стартувати** на не-loopback адресі без цього прапорця. Вимагає `Server:Tls:Enabled=true`. |
| `Server:Tls:Enabled` | `false` | Підтвердження оператора, що TLS сконфігуровано перед сервером (термінація у reverse-proxy). Передумова для не-loopback bind. |
| `Server:Tls:Hostname` | — | Internal-hostname у SAN авто-згенерованого server-cert (для internal-CA деплою без домену; опційно). |
| `Server:Auth:Enabled` | `false` | Увімкнути token/PoP-автентифікацію (Фаза 2). Дефолт `false` **зберігає** Фазу-1 loopback-full-access незмінною. |
| `Server:Auth:TokenTtlHours` | `12` | TTL access-токена, годин (1..168). |
| `Server:Auth:AuthTimeoutSeconds` | `10` | Скільки браузерна сесія може лишатись неавтентифікованою (чекати fallback `authenticate`) до close `4408` (3..120). |
| `Server:Auth:MaxUnauthenticatedPerIp` | `8` | Cap одночасних неавтентифікованих сокетів на один IP (1..1024). |
| `Server:Auth:RenewalPerIdentityPerHour` | `4` | Скільки разів identity може оновити прострочений токен через PoP (T5) за ковзну годину (1..60). Перевищення → renewal тимчасово недоступний (close `4401 expired`). |
| `Server:Auth:AllowedOrigins` | `[]` | Exact-match allowlist браузерних `Origin` (defense-in-depth, **не** authn). Кожен елемент — абсолютний `scheme://host[:port]` без `/` в кінці (напр. `chrome-extension://<id>`). Порожній список **відхиляє будь-який** Origin (fail-closed); запити без `Origin` (не-браузери) не підпадають під перевірку. |
| `Persistence:DataDirectory` | `data` | Каталог durable-стану: lockfile (G1), SQLite-стор, авто-згенеровані секрети (CA/cert/пеппери). **МУСИТЬ бути на платформно-шифрованому томі** (R3.7). У контейнері — `/data`. |
| `SignalCli:LibDirectory` | `signal-cli/lib` | Шлях до signal-cli lib (payload `SignalCli.Runtime`). |
| `SignalCli:StoragePathCli` | `SignalCliStorageData` | Дані акаунта signal-cli (git-ignored; **не комітити**). |
| `SignalCli:AppHome` | — | Override домашньої теки signal-cli (опційно). |
| `SignalCli:JavaExecutable` | автодетект | Шлях до `java` (опційно; JDK 25). |
| `SignalCli:MaxRestartAttempts` | `3` | Бюджет рестартів демона у вікні. |
| `SignalCli:HealthCheckIntervalSeconds` | `40` | Період health-ping демона. |
| `SignalCli:HealthCheckTimeoutSeconds` | `10` | Таймаут health-ping. |

Усе override-иться через environment (`Host.CreateDefaultBuilder`).

## Rate limits (admission)

Anti-abuse / admission-ліміти (send-бюджет, per-identity cap конектів, msg-rate, idle-timeout,
in-flight cap, server-wide гейт до signal-cli) — **опт-ін**, як і auth. З `Server:Admission:Enabled=false`
(дефолт) жоден ліміт не діє й поведінка незмінна. Вмикай разом із `Server:Auth:Enabled=true`
(бюджети мають сенс лише коли є identity).

| Ключ | Дефолт | Опис |
|---|---|---|
| `Server:Admission:Enabled` | `false` | Головний перемикач. `false` ⇒ admission-фасад пропускає все без side-effect, транспортні ліміти вимкнені. |
| `Server:Admission:AggregateBudgetPerWindow` | `300` | Σ-стеля send'ів за вікно на всі identity (анти-бан бюджет, W13, 1..100000). |
| `Server:Admission:BudgetWindowMinutes` | `60` | Довжина admission-вікна, хв (спільна для бюджету/floor/new-recipient, 1..1440). |
| `Server:Admission:PerUserFloorPerWindow` | `10` | Гарантований floor send'ів на identity під агрегатом (G2, 0..10000). МУСИТЬ бути ≤ `AggregateBudgetPerWindow`. |
| `Server:Admission:NewRecipientsPerWindow` | `5` | Максимум НОВИХ (first-contact) отримувачів на identity/вікно (D2, 1..1000). |
| `Server:Admission:ReserveBlockSize` | `10` | Гранула durable-резервації бюджету K (W13, 1..1000). |
| `Server:Admission:MaxConnectionsPerIdentity` | `4` | Cap одночасних автентифікованих сокетів на одну identity (1..256). Перевищення → close `4429` (`too-many-connections`). Loopback поза cap. |
| `Server:Admission:MessagesPerMinutePerConnection` | `100` | Msg-rate per-connection (token-bucket; ємність = burst, 1..10000). Перевищення → close `4429` (`message-rate:<sec>`). Ping/pong не рахуються. |
| `Server:Admission:IdleTimeoutMinutes` | `30` | Idle-timeout: мовчазна (без data-кадрів) сесія закривається штатним `1000` (`idle-timeout`, 1..1440). |
| `Server:Admission:MaxInFlightPerConnection` | `8` | Cap одночасних in-flight RPC на з'єднання (D12, 1..256). Понад → `-32005` (`retry_after=1`), без черги. |
| `Server:Admission:SignalCliConcurrencyLimit` | `4` | Server-wide неблокуючий гейт паралельних dispatch'ів до signal-cli (G6, 1..64). Зайнято → `-32005` (`retry_after=1`) — pending не росте unbounded. |

> **Auth-timeout — не тут.** Скільки браузерна сесія може лишатись неавтентифікованою — це
> `Server:Auth:AuthTimeoutSeconds` (close `4408`), див. таблицю Auth вище. Не дублюється в admission.

### Контракт відмов (для клієнтів)

Перевищення ліміту повідомляється **in-band** (щоб бачив і браузерний WS-клієнт, якому HTTP-`429`
до апгрейду невидимий — GAPS S9):

- **`-32005` JSON-RPC error** — для відмов на рівні виклику (send-бюджет, in-flight cap, G6-гейт).
  `error.data` = `{"retry_after": <int секунд>}`, `error.message` = `"Rate limit exceeded"` (без PII).
- **WS close `4429`** — для транспортних відмов, що рвуть сокет. Машиночитний `reason` у close-payload:
  - `too-many-connections` — перевищено per-identity cap конектів;
  - `message-rate:<sec>` — msg-rate флуд; суфікс `<sec>` — retry_after у секундах (сам код `4429`
    секунд не несе, тож вони в reason-рядку).
- **WS close `1000` `idle-timeout`** — не rate-подія: сесія мовчала довше за `IdleTimeoutMinutes`.
  Клієнт може перепідключитись одразу (без backoff).

Коди відмов лінкування пристрою (`startLink`/`finishLink`):

- **`-32004`** — `finishLink` на невалідну link-сесію (невідома / чужа / прострочена). Однакова
  помилка на всі три випадки (anti-oracle). `error.message` = `"Link session is invalid or has expired."`.
- **`-32001`** — `startLink(targetAccount)`, де `targetAccount` — уже прив'язаний (спільний) акаунт, а
  principal не admin/full-access. `error.message` = `"Access denied."`.
- **`-32005`** — перевищено rate-limit стартів лінкування (**3/хв на identity**); `error.data` несе
  `retry_after` (секунди), як і решта `-32005`.

**Рекомендація клієнтам (jittered backoff, G13).** На `-32005`/`4429` не ретрайте одразу й не в унісон.
Стартуйте з `retry_after` (якщо є) або base = **1с**, множник **2** на кожну наступну відмову, з
джитером **±50%**, cap **60с**. Приклад: `delay = min(60, base * 2^n) * (0.5 + random()*1.0)`.
Це розводить «громовий стукіт» ретраїв і не поглиблює перевантаження.

### L4-передумова та frame-assembly (D5/U1/S5 — поза застосунком)

Ці ліміти — application-рівень; вони **не** замінюють мережевий периметр:

- **L4-фаєрвол — передумова не-loopback експозиції (D5/U1).** Перед виставленням назовні став
  nftables / cloud SG, що пускає лише довірені джерела. App-ліміти бачать трафік лише **після**
  TCP+TLS+upgrade; SYN-флуд/конект-флуд до апгрейду відсікає саме L4 (див. `deploy/DEPLOYMENT.md §4`).
- **Frame-assembly-тріада — accepted-risk поза app (S5-канон).** Захист від зловмисної WS-фрагментації
  (нескінченні continuation-кадри, повільне складання, зомбі-fragment-буфери) **не** реалізується
  тут — це відповідальність транспорту/reverse-proxy. `permessage-deflate` вимкнено (zip-bomb
  неможливий, V6), а `MaxMessageSizeBytes` (64KB) обмежує зібраний кадр. Решта тріади — свідомо
  прийнятий ризик на рівні застосунку.

## Durable-стан і секрети (Фаза 2 фундамент)

На першому старті сервер авто-генерує на `Persistence:DataDirectory` (persist-once, права `0600`
на Unix): TLS CA + server-cert + admin mTLS-cert і пеппери `pepper_token`/`pepper_abuse`. На
рестартах вони **переюзаються** (CA ніколи не перегенеровується — інакше зламався б client-trust);
прострочені leaf-cert переоформлюються тим самим CA. Секрети **ніколи** не потрапляють у
env/логи/шари образу.

Поруч лежить embedded SQLite-стор (`durable.db`, WAL) з identity/binding-станом; на старті —
integrity-check (пошкоджена БД → **відмова старту**) і версіонована міграція схеми
(`PRAGMA user_version`). Data-dir захищено exclusive lockfile: **другий процес на тому ж каталозі
не стартує** (G1, `replicas=1`).

## Receive-mode (send-only MVP)

signal-cli стартує у **`--receive-mode=manual`** (send-only) з коробки — `UseManualReceiveMode`
default = `true`, і сервер його не чіпає. Тобто демон **не** receive-ить вхідні за всі акаунти.
Це навмисно для send-only MVP. Наслідок: щойно-злінкований власний номер може мати порожній/stale
`listGroups`, доки не пройде sync через receive (актуально для Фази 2 own-number flow).

> **G7 (link-sessions, task 5).** Після `finishLink` нового номера `listGroups(account)` може бути
> **порожнім/stale до першого receive** — link-sessions свідомо НЕ робить one-shot receive/sync.
> Авто-receive (claim-router, який receive-ить за власними акаунтами) приходить окремою зміною
> `add-group-claim-receive`; до її мержу групи підтягуються лише коли демон отримає вхідні (напр. після
> ручного receive або першого повідомлення). Це очікувана поведінка, а не баг.

## Контейнер

`deploy/` містить `Dockerfile` (приватний GitHub Packages feed, потребує `GITHUB_TOKEN` з
`read:packages`), `Dockerfile.from-source` (офлайн-збірка із sibling-репозиторіїв через
`build-local-feed.sh`), `docker-compose.yml` і `DEPLOYMENT.md`. Деталі — у `deploy/DEPLOYMENT.md`.
