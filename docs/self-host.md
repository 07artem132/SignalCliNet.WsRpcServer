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
| `Server:Host` | `127.0.0.1` | Адреса прослуховування. **Не став `0.0.0.0` без auth/firewall.** |
| `Server:Port` | `9000` | Порт WS. |
| `SignalCli:LibDirectory` | `signal-cli/lib` | Шлях до signal-cli lib (payload `SignalCli.Runtime`). |
| `SignalCli:StoragePathCli` | `SignalCliStorageData` | Дані акаунта signal-cli (git-ignored; **не комітити**). |
| `SignalCli:AppHome` | — | Override домашньої теки signal-cli (опційно). |
| `SignalCli:JavaExecutable` | автодетект | Шлях до `java` (опційно; JDK 25). |
| `SignalCli:MaxRestartAttempts` | `3` | Бюджет рестартів демона у вікні. |
| `SignalCli:HealthCheckIntervalSeconds` | `40` | Період health-ping демона. |
| `SignalCli:HealthCheckTimeoutSeconds` | `10` | Таймаут health-ping. |

Усе override-иться через environment (`Host.CreateDefaultBuilder`).

## Receive-mode (send-only MVP)

signal-cli стартує у **`--receive-mode=manual`** (send-only) з коробки — `UseManualReceiveMode`
default = `true`, і сервер його не чіпає. Тобто демон **не** receive-ить вхідні за всі акаунти.
Це навмисно для send-only MVP. Наслідок: щойно-злінкований власний номер може мати порожній/stale
`listGroups`, доки не пройде sync через receive (актуально для Фази 2 own-number flow).

## Контейнер

`deploy/` містить `Dockerfile` (приватний GitHub Packages feed, потребує `GITHUB_TOKEN` з
`read:packages`), `Dockerfile.from-source` (офлайн-збірка із sibling-репозиторіїв через
`build-local-feed.sh`), `docker-compose.yml` і `DEPLOYMENT.md`. Деталі — у `deploy/DEPLOYMENT.md`.
