# admin-cli — reference-клієнт mTLS admin-порту

Мінімальний консольний JSON-RPC 2.0 клієнт **окремого mTLS admin-порту**
`SignalCliNet.WsRpcServer` (зміна `add-invites-admin`, task 3.4). Показує, як зовнішній consumer
викликає admin-поверхню (`ISignalAdminRpc` + `listAccounts`/`ping`) через `wss://` з client-cert.

Це **reference-консьюмер** (R3.3 — generic JSON-RPC contract): він спілкується сирим JSON-RPC 2.0 і
**не залежить** ні від серверних DTO, ні від приватного NuGet-feed — єдина залежність `StreamJsonRpc`
з nuget.org. Тримайте його простим; це приклад, не продакшн-tooling.

## Модель безпеки (коротко)

Admin-поверхня доступна **лише** через окремий mTLS-порт (`SecureAdminRpcServer`, дефолт
`wss://127.0.0.1:9443`, `Server:Admin`). Автентифікація — **client-cert = креденшел** (жодного
токена/PoP): сервер резолвить SPKI cert-а в admin-identity (`admin_credentials`) або закриває конект.
На плейн-порту (токени) admin-методи недосяжні (chokepoint → `-32001`).

Секрети авто-провіжняться `SecretMaterialProvisioner` на томі даних у `<dataDir>/secrets/`
(у Docker — `/data/secrets/`):

| Файл | Призначення |
|---|---|
| `admin-client.crt` | admin client-cert (публічна частина) |
| `admin-client.key` | приватний ключ admin client-cert — **секрет**, ніколи не логується/не друкується |
| `ca.crt` | приватний CA деплою — для CustomRootTrust до server-cert (не в системному trust) |

> **Приватність.** CLI ніколи не друкує приватний ключ. **Код інвайту** друкується навмисно — це
> секрет, який адмін передає новому користувачу out-of-band (особисто / у вже довіреному каналі),
> а НЕ через самого бота.

## Збірка

Проєкт **поза** основним `.sln` (щоб не впливати на основну сюїту). Збирається окремо:

```bash
dotnet build example/AdminCli -c Release
# або запуск напряму:
dotnet run --project example/AdminCli -- <команда> [прапорці]
```

Restore тягне лише `StreamJsonRpc` (+ транзитиви) з nuget.org — приватний GitHub-feed не потрібен.

## Приклади

```bash
# Жвавість каналу (round-trip ping)
dotnet run --project example/AdminCli -- ping \
  --cert /data/secrets/admin-client.crt \
  --key  /data/secrets/admin-client.key \
  --ca   /data/secrets/ca.crt

# Видати one-time інвайт (дефолтні TTL/attempts з конфігу сервера) → друкує код + термін дії
dotnet run --project example/AdminCli -- invite

# Інвайт із явними TTL (сек) та лімітом спроб
dotnet run --project example/AdminCli -- invite --ttl 3600 --max-attempts 3

# Каскадний revoke identity (усі токени + device-секрет + розрив живих конектів)
dotnet run --project example/AdminCli -- revoke user-123

# Перелік акаунтів (admin бачить усі — R3.6)
dotnet run --project example/AdminCli -- accounts

# Інший хост/порт
dotnet run --project example/AdminCli -- accounts --url wss://10.0.0.5:9443
```

Дефолти шляхів секретів (`/data/secrets/…`) можна не вказувати, якщо запускати поряд зі змонтованим
томом; локально вкажіть `--cert/--key/--ca` явно.

## Команди

| Команда | RPC | Результат |
|---|---|---|
| `invite [--ttl N] [--max-attempts N]` | `createInvite` | друкує `code` + `expiresAt` |
| `revoke <identityId>` | `revokeIdentity` | підтвердження каскад-revoke |
| `accounts` | `listAccounts` | JSON переліку акаунтів (admin — усі) |
| `ping` | `ping` | `pong` |
| `audit-dump` | — | **немає серверного audit-read RPC** (див. нижче) |

### `audit-dump` — свідомо не реалізовано

Tamper-evident **audit-trail** (`audit_log`, hash-chain) сервера **не** має admin audit-read RPC-методу.
Ця команда навмисно **не вигадує** такий метод: вона лише друкує пояснення й виходить із ненульовим
кодом. Audit-trail інспектується на боці сервера (durable-БД). Якщо/коли upstream додасть audit-read
admin-метод — цю команду можна підключити.

## Коди виходу

`0` — успіх; `1` — помилка аргументів/usage; `2` — `audit-dump` (немає RPC); `3` — RPC-помилка сервера
(друкує код+повідомлення, напр. `-32001` для не-admin); `4` — помилка підключення/mTLS.
