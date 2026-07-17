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

1. `startLink` → повертає `deviceLinkUri` (QR). Відскануй у Signal → Linked devices.
2. `finishLink(deviceLinkUri, deviceName)` → завершує прив'язку.
3. `sendTextMessage(account, recipients, message)` → шле текст.
4. `listAccounts` / `listGroups(account)` → перегляд.
5. `ping` → `"pong"` (health, без стану — для контейнер-healthcheck).

## Конфігурація (`appsettings.json`)

| Ключ | Дефолт | Опис |
|---|---|---|
| `Server:Host` | `127.0.0.1` | Адреса прослуховування. **Не став `0.0.0.0` без TLS/firewall** (див. `Server:AllowNonLoopback`). |
| `Server:Port` | `9000` | Порт WS. |
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

## Контейнер

`deploy/` містить `Dockerfile` (приватний GitHub Packages feed, потребує `GITHUB_TOKEN` з
`read:packages`), `Dockerfile.from-source` (офлайн-збірка із sibling-репозиторіїв через
`build-local-feed.sh`), `docker-compose.yml` і `DEPLOYMENT.md`. Деталі — у `deploy/DEPLOYMENT.md`.
