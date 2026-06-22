# 🚀 Розгортання SignalCliNet.WsRpcServer

Покрокова інструкція: підняти WS JSON-RPC сервер на VPS, привʼязати акаунт
Signal, відкрити доступ з інтернету через `wss://` і підключитися до нього
з розширення **Потужність**.

```
[Телефон Signal] --(link)--> [WsRpcServer на VPS] <--wss--> [Потужність / browser]
                                     |
                                  signal-cli (JDK 25)
```

---

## 1. Вимоги

- VPS з Linux x64, **.NET 10 SDK** (тільки для збірки) і Docker (рекомендовано).
- Три репозиторії, виложені поруч (siblings):
  ```
  <parent>/
    JSON-RPC.NET/
    SignalCli.NET/
    SignalCliNet.WsRpcServer/
  ```
- Окремий номер телефону з активним Signal на телефоні (сервер реєструється як
  **linked device**, не як основний пристрій).

> ⚠️ **JDK 25.** signal-cli 0.14.3 вимагає Java 25. Docker-образ тягне Temurin 25
> автоматично. Для bare-metal встановіть JDK 25 і вкажіть шлях через
> `SignalCli:JavaExecutable`.

---

## 2. Збірка та запуск (Docker, рекомендовано)

Залежності (`JSON-RPC.NET`, `SignalCli.NET`, `SignalCli.Runtime`) збираються з
вихідного коду в локальний NuGet-feed — публічний GitHub-feed не потрібен.

```bash
cd SignalCliNet.WsRpcServer/deploy
docker compose up -d --build
docker compose logs -f          # очікуйте "WebSocket server started on 0.0.0.0:9000"
```

Контейнер слухає `127.0.0.1:9000` і зберігає стан акаунта у volume
`signalcli-data` (це і є привʼязаний пристрій — бекапте його).

### Альтернатива без Docker

```bash
# 1. зібрати локальний feed із сусідніх репо
SignalCliNet.WsRpcServer/deploy/build-local-feed.sh /tmp/feed

# 2. опублікувати сервер проти цього feed
cat > /tmp/nuget.config <<'EOF'
<configuration>
  <packageSources>
    <clear/>
    <add key="local" value="/tmp/feed"/>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json"/>
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="SignalCli.NET"/><package pattern="JSON-RPC.NET"/><package pattern="SignalCli.Runtime"/>
    </packageSource>
    <packageSource key="nuget.org"><package pattern="*"/></packageSource>
  </packageSourceMapping>
</configuration>
EOF
dotnet publish SignalCliNet.WsRpcServer/src/SignalCliNet.WsRpcServer/SignalCliNet.WsRpcServer.csproj \
  -c Release -o /opt/wsrpc --configfile /tmp/nuget.config -p:NuGetAudit=false

# 3. запустити (JDK 25 у PATH)
cd /opt/wsrpc
SignalCli__JavaExecutable=/opt/jdk25/bin/java dotnet SignalCliNet.WsRpcServer.dll
```

---

## 3. Привʼязка акаунта (device linking)

Сервер ще не має акаунта — `listAccounts` повертає `[]`. Привʼязка робиться
через два JSON-RPC виклики по тому ж WebSocket-каналу.

1. **`startLink`** → повертає `DeviceLinkUri` (рядок `sgnl://linkdevice?...`).
2. Згенеруйте з цього URI **QR-код** і відскануйте телефоном:
   Signal → *Налаштування → Привʼязані пристрої → Привʼязати новий пристрій*.
3. **`finishLink`** з тим самим `deviceLinkUri` і будь-яким `deviceName` →
   завершує привʼязку, повертає номер.

Приклад (значення method — camelCase, як їх віддає сервер):

```json
--> {"jsonrpc":"2.0","id":1,"method":"startLink","params":[]}
<-- {"jsonrpc":"2.0","id":1,"result":{"deviceLinkUri":"sgnl://linkdevice?uuid=...&pub_key=..."}}

(сканування QR телефоном)

--> {"jsonrpc":"2.0","id":2,"method":"finishLink",
     "params":{"deviceLinkUri":"sgnl://linkdevice?uuid=...","deviceName":"notify-bot"}}
<-- {"jsonrpc":"2.0","id":2,"result":{"number":"+380XXXXXXXXX"}}
```

Після цього `listAccounts` повертає привʼязаний номер, і можна слати notify.

> 💡 QR з `deviceLinkUri` зручно показати у popup Потужності або просто вивести
> у терміналі сервера (`qrencode -t ANSIUTF8 "<uri>"`).

---

## 4. Відкриття доступу з інтернету (TLS / wss)

**Не виставляйте порт 9000 голим у мережу.** Сервер не має власної
автентифікації — будь-хто, хто дотягнеться до сокета, зможе слати повідомлення
від вашого акаунта. Тримайте 9000 на `127.0.0.1` і ставте перед ним reverse
proxy з TLS.

1. Домен → A-запис на IP VPS.
2. Сертифікат: `certbot --nginx -d signal-rpc.example.com`.
3. Конфіг nginx — див. [`nginx-wss.conf.example`](nginx-wss.conf.example)
   (проксі WebSocket-upgrade на `127.0.0.1:9000`).
4. Firewall: відкрийте лише 443 (і 22), порт 9000 — ні.

### Захист каналу (обовʼязково)

Оскільки в самому RPC автентифікації немає, додайте один із рівнів:

- **HTTP Basic auth** на `location /` у nginx (`auth_basic` + `htpasswd`) — і
  передавайте креденшали у Потужності в URL `wss://user:pass@host`; **або**
- **allowlist за IP** (`allow`/`deny`), якщо у клієнтів статичні адреси; **або**
- доступ лише через **VPN/WireGuard** до приватної мережі.

---

## 5. Підключення з Потужності

Розширення підключається до `wss://signal-rpc.example.com/` і працює через
JSON-RPC 2.0. Основні методи (усі — camelCase):

| Метод | Параметри | Призначення |
|---|---|---|
| `listAccounts` | `[]` | список привʼязаних акаунтів |
| `startLink` / `finishLink` | див. §3 | привʼязка пристрою |
| `sendTextMessage` | `{ account, recipients[], message }` | **надсилання notify** |
| `subscribe` / `unsubscribe` | `{ account, eventTypes }` / `{ subscriptionId }` | підписка на події |

Приклад notify:

```json
--> {"jsonrpc":"2.0","id":10,"method":"sendTextMessage",
     "params":{"account":"+380XXXXXXXXX","recipients":["+380YYYYYYYYY"],
               "message":"⚠️ Алерт зі служби підтримки"}}
<-- {"jsonrpc":"2.0","id":10,"result":{"results":[...],"timestamp":1700000000000}}
```

`recipients` — номери телефонів або group-id. Деталі клієнтського модуля —
у репозиторії Потужності (`signal-notify`).

---

## 6. Експлуатація

- **Бекап:** volume `signalcli-data` = привʼязаний пристрій. Втрата → повторна
  привʼязка з телефона.
- **Оновлення:** `docker compose up -d --build` (стан в volume зберігається).
- **Health-check:** сервер сам перезапускає signal-cli при збоях
  (`SignalCli:MaxRestartAttempts`, `HealthCheckIntervalSeconds`).
- **Логи:** `docker compose logs -f`. Рівень — `Logging:LogLevel:Default`.
