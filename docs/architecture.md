# Архітектура: універсальний модуль сповіщень + Signal-бекенд

Потік: подія в Потужності → шар підписок → канал(и) доставки. Канал Chrome push — локальний. Канал Signal — через WsRpcServer (цей репо) → SignalCli.NET → signal-cli → Signal.

Кольори: 🔵 клієнт (розширення) · 🟢 сервер (.NET) · 🟠 зовнішнє (signal-cli/мережа) · 🔴 безпекові вузли.

```mermaid
flowchart TB
  subgraph EXT["Потужність — Chrome MV3 розширення (клієнт)"]
    direction TB
    subgraph SRC["Джерела подій (Delta / VEZHA)"]
      E1["battle-captain: CREATED / UPDATED / DELETED"]
      E2["layer health / missing layers"]
      E3["VEZHA stream-logs (фільтр: важливість/район/тип)"]
      E4["processing error"]
    end
    NM["Універсальний модуль сповіщень\nшар підписок: подія -> канал(и)"]
    subgraph CH["Канали доставки (pluggable)"]
      C1["Chrome push\nchrome.notifications"]
      C2["Signal client\nsignal-notify (WS JSON-RPC)"]
      C3["element (майбутнє)"]
    end
    TOK["Токен at-rest\nchrome.storage.local + non-extractable WebCrypto ключ (IndexedDB)\nдоступ лише з background SW"]
    SRC --> NM
    NM --> C1
    NM --> C2
    NM --> C3
    C2 -.читає.-> TOK
  end

  C1 --> OS["Локальна ОС-нотифікація"]

  C2 ==>|"wss TLS1.3 + subprotocol bearer token"| WS

  subgraph SRV["WsRpcServer — .NET (цей репо)"]
    direction TB
    WS["WS JSON-RPC 2.0 endpoint"]
    AUTHN["AuthN: валідація subprotocol-токена\nна HTTP-upgrade -> 401 до апгрейду"]
    AUTHZ["AuthZ: per-connection principal\ndeny-by-default, ізоляція\nакаунти з principal, не з аргумента"]
    RL["Rate-limit / connection quota\nauth-timeout, msg-cap"]
    subgraph ADP["RPC-адаптери"]
      A1["Accounts: listAccounts"]
      A2["Devices: startLink / finishLink"]
      A3["Message: sendTextMessage"]
      A4["Groups: listGroups"]
    end
    subgraph STORE["Стори"]
      T1["token -> identity\nHMAC-SHA256 + окремий pepper"]
      T2["identity -> accounts\n+ прапор спільного бот-акаунта"]
    end
    WS --> AUTHN
    AUTHN --> AUTHZ
    AUTHZ --> ADP
    RL -.-> WS
    AUTHN -.лукап.-> T1
    AUTHZ -.скоуп.-> T2
  end

  ADP ==>|"типізовані виклики"| LIB

  subgraph LIB["SignalCli.NET — NuGet 4.10.0 (типізовані фасади)"]
    F1["ISignalAccounts / ISignalDevices\nISignalMessage / ISignalGroups"]
  end

  LIB ==>|"JSON-RPC stdin/stdout"| CLI

  subgraph CLI["signal-cli — subprocess daemon (JDK 25)"]
    D1["мульти-акаунт на диску"]
    D2["ключі at-rest: AES-256-GCM, dir 0700, сервіс-юзер"]
  end

  CLI ==> NET["Signal мережа"]
  NET ==> R["Отримувачі: ти / група / підрозділ"]

  classDef client fill:#e3f2fd,stroke:#1565c0,color:#0d2b45
  classDef server fill:#e8f5e9,stroke:#2e7d32,color:#14331a
  classDef ext fill:#fff3e0,stroke:#e65100,color:#3a2300
  classDef sec fill:#fce4ec,stroke:#ad1457,color:#3a0d22
  class EXT,SRC,CH,NM,C1,C2,C3,E1,E2,E3,E4,OS client
  class SRV,WS,ADP,A1,A2,A3,A4,STORE,LIB,F1 server
  class CLI,D1,D2,NET,R ext
  class AUTHN,AUTHZ,RL,T1,T2,TOK sec
```

Рендер: [architecture.svg](architecture.svg)
