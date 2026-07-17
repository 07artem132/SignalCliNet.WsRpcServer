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

> ⚠️ **JDK 25.** signal-cli 0.14.6 вимагає Java 25. Docker-образ тягне Temurin 25
> автоматично. Для bare-metal встановіть JDK 25 і вкажіть шлях через
> `SignalCli:JavaExecutable`.

---

## 2. Збірка та запуск (Docker, рекомендовано)

Залежності (`SignalCli.NET`, `JSON-RPC.NET`, `SignalCli.Runtime`) ставляться як
**NuGet-пакети з GitHub Packages** (`nuget.pkg.github.com/07artem132`). Feed
приватний — потрібен GitHub-токен зі скоупом `read:packages`.

> 📦 **Передумова:** пакети потрібних версій (`SignalCli.NET 4.10.3`,
> `JSON-RPC.NET 2.8.0`, `SignalCli.Runtime 0.14.6.1`) мають бути опубліковані у
> feed. Публікація — workflow `publish-nuget.yml` у кожному репо
> (`Actions → Publish NuGet → Run workflow`, або автоматично при релізі).

```bash
cd SignalCliNet.WsRpcServer/deploy
export GITHUB_USERNAME=<ваш-github-логін>
export GITHUB_TOKEN=<PAT-зі-скоупом-read:packages>   # передається як BuildKit secret, не осідає у шарах
DOCKER_BUILDKIT=1 docker compose up -d --build
docker compose logs -f wsrpcserver     # очікуйте лог провіжну секретів + "server started"
```

`docker compose` піднімає **два** сервіси: `wsrpcserver` (НЕ публікується на хост — досяжний лише
всередині compose-мережі) і `reverse-proxy` (nginx), що термінує **`wss://127.0.0.1:9443`**
авто-згенерованим internal-CA сертифікатом із тому. На першому старті сервер сам генерує на волюмі
CA + server-cert + admin-cert + пеппери (`0600`, persist-once) — жодних секрет-env не потрібно.

Стан акаунта + durable-стор (`durable.db`) + секрети (`secrets/`) лежать на volume `signalcli-data`.
**Монтуйте його на платформно-шифрований том** (R3.7/S2): at-rest шифрування — відповідальність
платформи (KMS-disk / host dm-crypt), НЕ застосунку (app-LUKS у контейнері вимагав би `--privileged`
— гірша безпека). Це і є привʼязаний пристрій — бекапте його (§7).

> ⚠️ **Клієнт має довіряти internal-CA.** Витягніть його й додайте у trust-store клієнта/системи:
> ```bash
> docker compose exec reverse-proxy cat /data/secrets/ca.crt > wsrpc-ca.crt
> ```
> «Голий IP без CA» не підтримується — TLS без довіри до CA браузер/розширення не валідує.
> За наявності **домену** використовуйте ACME замість internal-CA (§4).

> 🔒 **G1 (single-instance).** Рівно одна репліка на data-dir: другий процес на тому ж томі
> **не стартує** (exclusive lockfile). НЕ використовуйте `--scale`/`replicas>1`.

### Альтернатива: збірка залежностей із вихідного коду (без feed/токена)

Якщо feed недоступний, залежності збираються з сусідніх репо у локальний
feed. Потрібні `JSON-RPC.NET` та `SignalCli.NET`, виложені поруч (§1).

```bash
# Docker-варіант (context — батьківська тека з трьома репо):
docker build -f SignalCliNet.WsRpcServer/deploy/Dockerfile.from-source \
  -t wsrpcserver ..

# або без Docker:
SignalCliNet.WsRpcServer/deploy/build-local-feed.sh /tmp/feed
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

# запуск (JDK 25 у PATH)
cd /opt/wsrpc
SignalCli__JavaExecutable=/opt/jdk25/bin/java dotnet SignalCliNet.WsRpcServer.dll
```

---

## 3. Привʼязка акаунта (device linking)

Сервер ще не має акаунта — `listAccounts` повертає `[]`. Привʼязка робиться
через два JSON-RPC виклики по тому ж WebSocket-каналу.

1. **`startLink`** → повертає `{ sessionId, deviceLinkUri }` (URI — рядок `sgnl://linkdevice?...`).
2. Згенеруйте з `deviceLinkUri` **QR-код** і відскануйте телефоном:
   Signal → *Налаштування → Привʼязані пристрої → Привʼязати новий пристрій*.
3. **`finishLink`** з тим самим `sessionId` (**не** сирим URI) і будь-яким `deviceName` →
   завершує привʼязку, повертає номер.

> ⚠️ **BREAKING у v2.0.0.** `finishLink` тепер приймає one-time `sessionId` зі `startLink`, а не
> `deviceLinkUri`. Сирий URI/QR — чутливий, живе лише у in-memory сесії (TTL 120с) і не логується.

Приклад (значення method — camelCase, як їх віддає сервер):

```json
--> {"jsonrpc":"2.0","id":1,"method":"startLink","params":[]}
<-- {"jsonrpc":"2.0","id":1,"result":{"sessionId":"<base64url>","deviceLinkUri":"sgnl://linkdevice?uuid=...&pub_key=..."}}

(сканування QR телефоном)

--> {"jsonrpc":"2.0","id":2,"method":"finishLink",
     "params":{"sessionId":"<base64url>","deviceName":"notify-bot"}}
<-- {"jsonrpc":"2.0","id":2,"result":{"number":"+380XXXXXXXXX"}}
```

Після цього `listAccounts` повертає привʼязаний номер, і можна слати notify.

> 💡 QR з `deviceLinkUri` зручно показати у popup Потужності або просто вивести
> у терміналі сервера (`qrencode -t ANSIUTF8 "<uri>"`).

---

## 4. Відкриття доступу з інтернету (TLS / wss)

**Ніколи не виставляйте порт 9000 (голий `ws`) у мережу.** Сервер не має власної автентифікації
у Фазі 1 (D4 fail-closed відмовить у не-loopback bind без свідомого opt-in). TLS термінується
reverse-proxy; app лишається за ним.

### Два шляхи довіри (D3)

| Ситуація | Сертифікат | Клієнтський trust |
|---|---|---|
| **Є домен** | ACME/Let's Encrypt: `certbot --nginx -d signal-rpc.example.com` + [`nginx-wss.conf.example`](nginx-wss.conf.example) | публічний CA — нічого робити не треба |
| **Немає домену** | авто-згенерований **internal-CA** (bundled reverse-proxy, [`nginx-wss-autocert.conf`](nginx-wss-autocert.conf)) | додати `ca.crt` у trust-store клієнта |
| **Голий IP без CA** | — | **не підтримується** (браузер/розширення не валідує TLS без довіреного CA) |

Обидва reverse-proxy-конфіги мають **TLS 1.3 з підлогою 1.2** (старіші протоколи вимкнено) + HSTS.

### L4-передумова публічної експозиції (D5/U1)

Оскільки app-шар не має auth у Фазі 1, публічний біндинг вимагає **зовнішнього L4-фаєрволу** як
передумови (це не app-робота — accepted-risk, виноситься на мережу):

- **nftables**/**iptables** на VPS: пускати `443`/`9443` лише з довірених джерел, `22` — за потреби;
- або **cloud Security Group** (AWS SG / GCP firewall) з тим самим allowlist;
- або доступ лише через **VPN/WireGuard** до приватної мережі.

Порт app-контейнера (`9000`) не публікується на хост зовсім — лише reverse-proxy слухає `9443`.

### Додатковий захист каналу (опційно, поки немає auth)

- **HTTP Basic auth** на `location /` у nginx (`auth_basic` + `htpasswd`), креденшали в URL
  `wss://user:pass@host`; **або** **allowlist за IP** (`allow`/`deny`) у nginx.

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

## 6. Увімкнення Phase-2/3 фіч (shared-bot)

За замовчуванням сервер поводиться як у Фазі 1 (loopback, full-access) — усі shared-bot-поверхні
**вимкнені**. Щоб віддати бота кільком identity через `wss://`, вмикайте фічі свідомо.

### Прапорці (`appsettings.json` / env)

| Прапорець | Дефолт | Вмикає |
|---|---|---|
| `Server:Auth:Enabled` | `false` | token/PoP-автентифікацію (bearer через субпротокол + PoP device-ключем). |
| `Server:Admission:Enabled` | `false` | admission/rate-limits (бот-бюджет, msg-rate, in-flight, connection cap, idle-timeout). |
| `Server:GroupClaim:Enabled` | `false` | безперервний авто-receive + group-claim send-gate (`requestGroupClaim`). |
| `Server:Admin:Enabled` | `true` (loopback) | **окремий mTLS admin-порт** `wss://127.0.0.1:9443` (client-cert = креденшел). |
| `Server:Heartbeat:Enabled` | `true` | адитивні app-level heartbeat-нотіфи (`<25с`; вимкніть для connect-on-demand MVP). |

> ⚠️ Вмикайте `Server:Admission:Enabled=true` **разом** із `Server:Auth:Enabled=true` — ліміти
> «на identity» осмислені лише коли identity встановлена автентифікацією. Повна таблиця ключів і
> лімітів — у [`docs/self-host.md`](../docs/self-host.md); shared-bot-модель — [`docs/shared-bot.md`](../docs/shared-bot.md).

### Порядок bootstrap (shared-bot онбординг)

1. **Провіжн секретів — автоматично.** На first-boot сервер сам генерує на томі (`/data/secrets/`,
   `0600`, persist-once) CA + server-cert + **admin-cert** + пеппери. Жодних секрет-env.
2. **Admin-cert уже на томі.** Витягніть bundle для admin-клієнта:
   ```bash
   docker compose exec wsrpcserver cat /data/secrets/admin-client.crt > admin-client.crt
   docker compose exec wsrpcserver cat /data/secrets/admin-client.key > admin-client.key   # СЕКРЕТ
   docker compose exec wsrpcserver cat /data/secrets/ca.crt          > ca.crt
   ```
3. **`createInvite` через mTLS admin-порт** (`wss://127.0.0.1:9443`, client-cert). Reference-клієнт —
   [`example/AdminCli`](../example/AdminCli):
   ```bash
   dotnet run --project example/AdminCli -- invite \
     --cert admin-client.crt --key admin-client.key --ca ca.crt
   # → друкує одноразовий код інвайту + термін дії
   ```
   Код інвайту — секрет; передайте його новому користувачу out-of-band (не через самого бота).
4. **Онбординг через `redeemInvite`.** Новий користувач гасить one-time код на плейн-порту
   (`redeemInvite`) — це реєструє його device-pubkey (TOFU) і видає перший токен. Далі — token/PoP
   на кожному connect.

> 🔐 Admin-поверхня (`createInvite` / `revokeIdentity` / `listAccounts(all)`) доступна **лише** через
> mTLS-порт. На плейн-порту (токени) admin-методи недосяжні (chokepoint → `-32001`). НІКОЛИ не
> публікуйте порт `9443` у мережу без L4-обмеження до довірених джерел (див. §4).

## 7. Експлуатація

- **Оновлення (свідоме downtime-вікно, W15):** через G1 (single-instance) деплой = зупинка старого
  контейнера й запуск нового, а не rolling-update. `docker compose up -d --build` робить саме це;
  НЕ піднімайте другий екземпляр на тому ж томі паралельно (він refuse-start).
- **`replicas=1` — hard-constraint:** не масштабуйте. Durable-стор і бюджет — єдина копія на томі.
- **Health-check:** сервер сам перезапускає signal-cli при збоях
  (`SignalCli:MaxRestartAttempts`, `HealthCheckIntervalSeconds`).
- **Логи:** `docker compose logs -f`. Рівень — `Logging:LogLevel:Default`. Секрети/тіла/номери у
  логи не пишуться (privacy-контракт).
- **Core dumps вимкнено (D17):** compose ставить `ulimits.core: 0` (bare-metal/systemd —
  `LimitCORE=0` або `ulimit -c 0` для сервіс-юзера), щоб дамп не виніс ключі/пеппери на
  (можливо незашифрований) шлях.

### Бекап і відновлення durable-стану (G4)

Том `signalcli-data` містить і привʼязаний пристрій (signal-cli state), і durable-стор (`durable.db`),
і секрети (`secrets/`). Втрата тому = повторна привʼязка з телефона + втрата identity/binding-стану.

**Бекап** (пишіть на ОКРЕМИЙ платформно-шифрований том/сховище, не на той самий диск):

```bash
# 1. Консистентний онлайн-знімок SQLite (безпечно на живому сервері, WAL-сумісно):
docker compose exec wsrpcserver \
  sqlite3 /data/durable.db ".backup '/data/durable.backup.db'"
# 2. Винесіть знімок + секрети на окреме шифроване сховище:
docker compose cp wsrpcserver:/data/durable.backup.db ./backup/durable.$(date +%F).db
docker run --rm -v deploy_signalcli-data:/v -v "$PWD/backup":/b alpine \
  sh -c 'cp -a /v/secrets /b/secrets && tar czf /b/signalcli-state.tgz -C /v SignalCliStorageData'
```

**Відновлення:** зупиніть сервіс → покладіть `durable.<date>.db` назад як `/data/durable.db` (і
`secrets/` + `SignalCliStorageData`) на новому томі → підніміть сервіс. На старті виконається
`integrity_check` (пошкоджена БД → **відмова старту** з чітким логом) і міграції схеми
(`PRAGMA user_version`) — тобто відновлений/старіший знімок автоматично домігрується до поточної
версії.

---

## 8. Реліз-бінарники

Крім Docker, кожен GitHub Release (тег `vX.Y.Z`) публікує два **self-contained**
архіви — окремий `.NET`-рантайм не потрібен, лише розпакувати й запустити:

| Asset | Платформа |
|---|---|
| `SignalCliNet.WsRpcServer-<версія>-linux-x64.tar.gz` | Linux x64 |
| `SignalCliNet.WsRpcServer-<версія>-win-x64.zip` | Windows x64 |

```bash
# Linux, приклад
tar -xzf SignalCliNet.WsRpcServer-2.0.0-linux-x64.tar.gz -C /opt/wsrpc
cd /opt/wsrpc
SignalCli__JavaExecutable=/opt/jdk25/bin/java ./SignalCliNet.WsRpcServer
```

Payload signal-cli (з `SignalCli.Runtime`) вже всередині архіву — MSBuild-таргет
`CopySignalCliToPublish` доносить його у publish-папку. Але **JDK 25 у бінарник
не входить**: signal-cli — Java-застосунок, тому на цільовій машині все одно
потрібен встановлений JDK 25 (шлях — через `SignalCli:JavaExecutable`, як і в
розділі 1).

Ручний запуск пайплайна (`Actions → Release → Run workflow`, без тегу) не
створює GitHub Release — лише завантажує ті самі два архіви як
workflow-артефакти (версія `0.0.0-manual`), зручно для перевірки збірки без
публікації.

CI-пайплайн: [`.github/workflows/release.yml`](../.github/workflows/release.yml)
(build → тести → publish `-r linux-x64`/`-r win-x64` → архів → реліз). Restore
залежностей — та сама FAST/FALLBACK-логіка, що й у `build.yml` (§2).
