# ws-jsonrpc-gateway (as-built, Фаза 1)

Стан на 2026-07-01, звірено з `src/SignalCliNet.WsRpcServer/`.

## Requirements

### Requirement: WS JSON-RPC ендпоінт на loopback
Сервер SHALL слухати WebSocket JSON-RPC 2.0 на адресі з `Server:Host/Port`;
дефолт конфігурації — `127.0.0.1:9000` (Фаза 1 без auth → не-loopback bind заборонений).

#### Scenario: без auth немає зовнішньої експозиції
- **WHEN** сервер стартує з дефолтним `appsettings.json`
- **THEN** порт відкритий лише на `127.0.0.1`, ззовні недоступний

### Requirement: RPC-поверхня Фази 1
Сервер SHALL експонувати через `RpcServiceRegistry`: `ISignalAccountsRpc` (`listAccounts`),
`ISignalDevicesRpc` (`startLink`/`finishLink`), `ISignalMessageRpc` (`sendTextMessage`),
`ISignalGroupsRpc` (`listGroups`). Auth відсутній (компенсовано loopback-bind).

### Requirement: Ліміт розміру повідомлення
Сесія SHALL закривати з'єднання (`MessageTooBig`) на повідомленні, що перевищує
`MaxMessageSizeBytes`. Чек — на зібраному повідомленні (post-assembly); захист
під час накопичення фреймів — поза app-шаром (accepted-risk D5/U1, зовнішній L4).

### Requirement: Privacy у логах
Логи SHALL NOT містити тіла повідомлень, номери (E.164), токени, device-link URI.
`ConsoleTraceListener` на `JsonRpc.TraceSource` заборонений (A2); `account` не входить
у шаблони Information-логів адаптерів (W6).

#### Scenario: send не пише номер
- **WHEN** викликається `sendTextMessage`
- **THEN** лог містить метод/статус/id, але не account/recipient/тіло

### Requirement: Receive-mode за замовчуванням manual
Демон signal-cli SHALL стартувати з `--receive-mode=manual` (дефолт SignalCli.NET,
V2). Зміна на безперервний авто-receive — у change `add-group-claim-receive` (R3.1).
