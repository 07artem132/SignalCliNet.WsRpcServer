# Self-host: свій Signal-send сервер за 5 хвилин

Цей гайд описує **Фазу 1** — однокористувацький self-host `SignalCliNet.WsRpcServer`:
один лінкований Signal-номер, надсилання повідомлень + читання акаунтів/груп через
WebSocket JSON-RPC. Auth немає за дизайном → сервер слухає **лише `127.0.0.1`**.

> ⚠️ **Фаза 1 = loopback-only.** Без автентифікації відкритий не-loopback бінд (`0.0.0.0`)
> = вільне Signal-send реле для всіх у мережі. Багатокористувацький режим + `wss`/auth — Фаза 2.

## Що вміє (Фаза 1)

| JSON-RPC метод | Параметри | Призначення |
|---|---|---|
| `listAccounts` | — | Список лінкованих акаунтів |
| `startLink` | — | Почати лінкування → повертає `deviceLinkUri` (рендериться у QR) |
| `finishLink` | `deviceLinkUri`, `deviceName` | Завершити лінкування після скану |
| `sendTextMessage` | `account`, `recipients[]`, `message` | Надіслати текст |
| `listGroups` | `account` | Список груп акаунта |

Режим прийому — **manual** (`UseManualReceiveMode=true` за замовчуванням у SignalCli.NET):
демон не receive-ить фоном, MVP — суто send-only.

## Передумови

- **.NET 10 SDK**.
- **signal-cli 0.14.3** — постачається пакетом `SignalCli.Runtime` (jar-и стейджаться у вихід білда).
- **Java (JDK/JRE 25)** для запуску signal-cli. Два шляхи:
  - системна Java 25 на `PATH` / `JAVA_HOME`, **АБО**
  - вказати конкретний `java` через конфіг `SignalCli:JavaExecutable` (напр. bundled-JRE
    з пакета `SignalCli.Runtime.Jre.win-x64` / Docker-образ тягне Temurin 25 сам).
- Доступ до приватного NuGet-feed (`nuget.pkg.github.com/07artem132`) для відновлення
  `SignalCli.NET` / `JSON-RPC.NET` / `SignalCli.Runtime` — потрібен PAT з `read:packages`
  (`dotnet restore --configfile <config-with-creds>`), або офлайн-збірка з sibling-сорсу
  (`deploy/build-local-feed.sh`, див. `deploy/DEPLOYMENT.md`).

## 5-хвилинний старт

```bash
# 1. Відновити + зібрати (з автентифікованим feed)
dotnet restore SignalCliNet.WsRpcServer.sln --configfile <nuget-config-з-PAT>
dotnet build  -c Release --no-restore

# 2. Запустити (loopback, як вимагає Фаза 1)
dotnet run --project src/SignalCliNet.WsRpcServer -c Release
#   або вказати власну Java, якщо немає системної:
#   dotnet run --project src/SignalCliNet.WsRpcServer -c Release \
#       --SignalCli:JavaExecutable=/path/to/jre/bin/java
```

Сервер підніметься на `ws://127.0.0.1:9000`, запустить signal-cli у JSON-RPC daemon-режимі
й залогує `Signal JSON-RPC WebSocket server started on 127.0.0.1:9000`.

## Лінкування номера (QR)

Лінкуємо сервер як **вторинний пристрій** до існуючого Signal-акаунта на телефоні:

1. Клієнт викликає `startLink` → отримує `deviceLinkUri` виду
   `sgnl://linkdevice?uuid=...&pub_key=...`.
2. Рендеримо URI у QR-код (будь-який QR-генератор; URI кодується **дослівно**, з percent-encoding).
3. Телефон → **Signal → Settings → Linked Devices → Link New Device** → скан QR.
   ⏱️ Скануй **одразу** — провіжн-сокет до Signal живе коротко (~30–90 с) і ріжеться
   при простої; прострочений QR дає `Connection closed!` / "не вдалося зв'язати пристрій",
   тоді почни новий `startLink`.
4. Клієнт викликає `finishLink(deviceLinkUri, deviceName)` → акаунт зʼявляється в `listAccounts`.

Альтернатива без сервера (через сам signal-cli): `signal-cli --config <storage> link -n "<name>"`
друкує той самий URI у stdout і завершує лінк після скану.

## Приклад WS JSON-RPC

Кожне повідомлення — один WS-фрейм з одним JSON-RPC обʼєктом (StreamJsonRpc
`SystemTextJsonFormatter`, camelCase). Приклад (псевдо):

```json
--> {"jsonrpc":"2.0","id":1,"method":"listAccounts","params":[]}
<-- {"jsonrpc":"2.0","id":1,"result":[{"number":"+380XXXXXXXXX"}]}

--> {"jsonrpc":"2.0","id":2,"method":"sendTextMessage",
     "params":["+380XXXXXXXXX",["+380XXXXXXXXX"],"hello"]}
<-- {"jsonrpc":"2.0","id":2,"result":{"results":[{"type":"SUCCESS",...}],"timestamp":...}}

--> {"jsonrpc":"2.0","id":3,"method":"listGroups","params":["+380XXXXXXXXX"]}
<-- {"jsonrpc":"2.0","id":3,"result":[{"id":"...","name":"...","isMember":true,...}]}
```

## Конфігурація

`appsettings.json` (секції `Server`, `SignalCli`, `Logging`), кожен ключ перекривається
аргументом командного рядка `--Секція:Ключ=значення` або env-змінною `Секція__Ключ`.

| Ключ | Дефолт | Опис |
|---|---|---|
| `Server:Host` | `127.0.0.1` | Адреса бінда. **Фаза 1: тримати loopback.** Не-loopback без auth = відкрите реле. |
| `Server:Port` | `9000` | TCP-порт WS-сервера. |
| `SignalCli:LibDirectory` | `signal-cli/lib` | Тека з jar-ами signal-cli (відносно `AppHome`). |
| `SignalCli:StoragePathCli` | `SignalCliStorageData` | Тека стану signal-cli (**= лінкований пристрій**; берегти/бекапити). |
| `SignalCli:AppHome` | `AppContext.BaseDirectory` | База для відносних шляхів (вихідна тека білда). |
| `SignalCli:JavaExecutable` | (системна Java) | Шлях до `java`, якщо немає Java на `PATH`/`JAVA_HOME`. |
| `SignalCli:MaxRestartAttempts` | `3` | Спроби рестарту демона у вікні health-monitor. |
| `SignalCli:HealthCheckIntervalSeconds` | `40` | Період health-ping демона. |
| `SignalCli:HealthCheckTimeoutSeconds` | `10` | Таймаут health-ping. |

## Безпека / експлуатація

- **Бінд:** Фаза 1 не має auth → `Server:Host` мусить лишатись `127.0.0.1`. Якщо потрібен
  не-loopback — це передумова **обовʼязкового зовнішнього firewall** (Фаза 2 додає `wss`+auth,
  тоді й знімається loopback-обмеження).
- **Storage = пристрій.** `SignalCliStorageData/` містить ключі лінкованого пристрою. Не комітити
  (git-ignored), берегти том; втрата = повторне лінкування з телефону.
- **Приватність логів:** не логуються тіла повідомлень/номери/вкладення (контракт SignalCli.NET).
- **Контейнер:** `deploy/docker-compose.yml` мапить `127.0.0.1:9000:9000` на хості й тягне Temurin 25
  в образ — деталі в `deploy/DEPLOYMENT.md`.
