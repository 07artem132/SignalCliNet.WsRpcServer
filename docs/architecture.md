# Архітектура: універсальний модуль сповіщень + Signal-бекенд

Потік: подія в Потужності → шар підписок → канал(и) доставки. Канал Chrome push — локальний. Канал Signal — через WsRpcServer (цей репо) → SignalCli.NET → signal-cli → Signal.

> **Як читати схему (важливо — закриває N1/N2 зі звірки плану):**
> Діаграма показує **цільовий** стан після Фази 2. Зеленим — як-збудовано-сьогодні (Фаза 1: send-only, bind `127.0.0.1`, **БЕЗ auth**, тестів 0). Підграф **«Phase-2 target»** + усі 🔴 безпекові вузли (chokepoint, principal, token/identity/budget-стори, link-session, lockfile, L4) — **ще не існують у коді**, це NET-NEW робота Фази 2 (див. `PLAN-notifications-backend.md`). Events-адаптер (`AEV`) існує в коді, але **не зареєстрований** і активується лише в Фазі 3 (receive-шлях).

Кольори: 🔵 клієнт (розширення, поза скоупом цих репо) · 🟢 сервер (.NET, цей репо) · 🟠 зовнішнє (signal-cli/мережа) · 🔴 безпекові вузли (здебільшого Phase-2 target).

```mermaid
flowchart TB
  subgraph EXT["Потужність — Chrome MV3 розширення (клієнт, ПОЗА скоупом репо)"]
    direction TB
    subgraph SRC["Джерела подій (Delta / VEZHA)"]
      E1["battle-captain: CREATED / UPDATED / DELETED"]
      E2["layer health / missing layers"]
      E3["VEZHA stream-logs (фільтр: важливість/район/тип)"]
      E4["processing error"]
    end
    NM["Універсальний модуль сповіщень\nшар підписок + debounce/coalesce"]
    subgraph CH["Канали доставки (pluggable)"]
      C1["Chrome push\nchrome.notifications"]
      C2["Signal client\nWS JSON-RPC + idempotency-key (C2)\nserver-heartbeat reuse (C5)"]
      C3["element (майбутнє)"]
    end
    KEY["device-key: non-extractable WebCrypto\nIndexedDB (НЕ chrome.storage) — PoP\n(рішення C1)"]
    TOK["token at-rest: chrome.storage.local\n+ AES-GCM non-extractable ключ\nчитати лише з background SW"]
    SRC --> NM
    NM --> C1 & C2 & C3
    C2 -.PoP-підпис.-> KEY
    C2 -.читає.-> TOK
  end

  C1 --> OS["Локальна ОС-нотифікація"]
  C2 ==>|"wss TLS1.3 + subprotocol bearer token + apiVersion (A5)"| L4

  subgraph SRV["WsRpcServer — .NET (цей репо)"]
    direction TB
    L4["Зовнішній L4: nftables / cloud-SG / WAF\nSYN/handshake/frame-flood = accepted-risk\n(D5 + U1: frame-DoS НЕ app-side)"]
    WS["WS JSON-RPC 2.0 endpoint\nOnWsConnecting: 401-до-upgrade, Origin-allowlist,\necho лише non-auth субпротоколу (C4)"]
    subgraph P2["Phase-2 target  (Фаза 1 = loopback 127.0.0.1, send-only, БЕЗ auth)"]
      direction TB
      CHOKE["Герметичний chokepoint — NET-NEW\ndefault-deny -32601 ДО dispatch\nadmission(): global-pause → per-user-floor →\nnew-recipient → budget-reserve (W16)\nIDOR-guard: account з principal, НЕ з аргумента"]
      PRIN["per-connection principal (V8: per-invocation)\ntoken→Principal, PoP-pending gate"]
    end
    subgraph ADP["RPC-адаптери (stateless щодо principal)"]
      A1["Accounts: listAccounts"]
      A2["Devices: startLink / finishLink\nper-call timeout ≥ TTL (D16+W9)"]
      A3["Message: sendTextMessage"]
      A4["Groups: listGroups"]
      AEV["Events: subscribe (Phase-3)"]
    end
    subgraph STORE["Durable embedded DB — SQLite/WAL на LUKS-томі"]
      T1["token→identity: HMAC + окремий pepper\nprefix-version-hint → 1 lookup (D10)\npre-image = decoded-random (W18)"]
      T2["identity→accounts (+прапор спільного бот-акаунта)"]
      BUD["aggregate bot-budget\nblock-reservation, persist-ДО-send (W13)"]
    end
    LS["link-session (in-memory)\nSessionID 256-bit, TTL 120c, one-time, in-proc lock"]
    LOCK["single-instance lockfile (G1)\nflock / O_EXCL → replicas=1"]
    WS --> CHOKE
    CHOKE --> PRIN
    CHOKE --> ADP
    PRIN -.lookup.-> T1
    PRIN -.scope.-> T2
    A3 -.reserve-1.-> BUD
    A2 -.-> LS
    LOCK -.guard.-> STORE
  end

  ADP ==>|"типізовані виклики + server-wide in-flight semaphore (G6)"| LIB

  subgraph LIB["SignalCli.NET — NuGet 4.10.0 (типізовані фасади)"]
    F1["ISignalAccounts / ISignalDevices / ISignalMessage / ISignalGroups\nтипізовані -4/-5/-6 на send-response (W12)"]
  end

  LIB ==>|"JSON-RPC stdin/stdout (єдиний спільний pipe)"| CLI

  subgraph CLI["signal-cli — subprocess daemon (JDK 25, --receive-mode=manual)"]
    D1["мульти-акаунт, спільний config-том\nНЕ ізолює акаунти (W8/W25 caveat)"]
    D2["ключі at-rest: LUKS-том, dir 0700, сервіс-юзер signal-runner"]
  end

  CLI ==> NET["Signal мережа"]
  NET ==> R["Отримувачі: ти / група / підрозділ"]

  classDef client fill:#e3f2fd,stroke:#1565c0,color:#0d2b45
  classDef server fill:#e8f5e9,stroke:#2e7d32,color:#14331a
  classDef ext fill:#fff3e0,stroke:#e65100,color:#3a2300
  classDef sec fill:#fce4ec,stroke:#ad1457,color:#3a0d22
  class EXT,SRC,CH,NM,C1,C2,C3,E1,E2,E3,E4,OS client
  class SRV,WS,ADP,A1,A2,A3,A4,AEV,STORE,LIB,F1,P2 server
  class CLI,D1,D2,NET,R ext
  class CHOKE,PRIN,T1,T2,BUD,LS,LOCK,KEY,TOK,L4 sec
```

Рендер: [architecture.svg](architecture.svg) · [architecture.png](architecture.png)

## Що змінилося vs as-built

| Шар | Фаза 1 (як-збудовано) | Фаза 2 (target на схемі) |
|---|---|---|
| Bind | `127.0.0.1`, без TLS | `0.0.0.0` `wss://` TLS 1.3 + HSTS, за зовнішнім L4 |
| AuthN/AuthZ | **немає** | subprotocol-token + PoP + герметичний default-deny chokepoint |
| Ізоляція | н/д (один користувач) | per-connection principal (per-invocation, V8), IDOR-guard |
| Стори | немає | durable SQLite/WAL на LUKS: token (pepper-version-hint, D10), identity→accounts, aggregate-budget (block-reservation, W13); link-session in-memory |
| Rate-limit | глобальний `MaxConcurrentConnections` (вимкнено) | єдина admission-функція (W16) + reservation-budget + per-user floor |
| Адаптери | Accounts/Devices/Message **зареєстровані**; Groups — DI-рядок бракує (task-1); Events — існує, не зареєстрований | + Groups; Events лише Phase-3 (receive) |
| Single-instance | не гейтиться | lockfile `flock`/`O_EXCL` → `replicas=1` (G1) |

frame-accumulation DoS (assembly-abort / max-frame-count / slowloris) — **не app-side і не фікситься bump'ом JSON-RPC.NET** (gap у NetCoreServer нижче `OnWsReceived`); винесено в accepted-risk + зовнішній L4 (D5 + U1).
