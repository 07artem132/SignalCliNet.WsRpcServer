# ws-jsonrpc-gateway (as-built, Фаза 1)

Стан на 2026-07-01, звірено з `src/SignalCliNet.WsRpcServer/`.

## Purpose

Генеричний WebSocket JSON-RPC 2.0 Signal-gateway: транспорт + RPC-поверхня
(accounts/devices/messages/groups), receive-режим, msg-size cap і privacy-контракт логів.

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

#### Scenario: клієнт викликає зареєстрований RPC-метод
- **WHEN** клієнт викликає `listAccounts` через WS JSON-RPC
- **THEN** сервер диспетчить у відповідний адаптер і повертає результат

### Requirement: Ліміт розміру повідомлення
Сесія SHALL закривати з'єднання (`MessageTooBig`) на повідомленні, що перевищує
`MaxMessageSizeBytes`. Чек — на зібраному повідомленні (post-assembly); захист
під час накопичення фреймів — поза app-шаром (accepted-risk D5/U1, зовнішній L4).

#### Scenario: повідомлення понад ліміт
- **WHEN** клієнт шле повідомлення, більше за `MaxMessageSizeBytes`
- **THEN** сесія закривається з `MessageTooBig`

### Requirement: Privacy у логах
Логи SHALL NOT містити тіла повідомлень, номери (E.164), токени, device-link URI.
`ConsoleTraceListener` на `JsonRpc.TraceSource` заборонений (A2); `account` не входить
у шаблони Information-логів адаптерів (W6).

#### Scenario: send не пише номер
- **WHEN** викликається `sendTextMessage`
- **THEN** лог містить метод/статус/id, але не account/recipient/тіло

### Requirement: Receive-mode за замовчуванням manual
Демон signal-cli SHALL стартувати з `--receive-mode=manual` (дефолт, send-only, V2), ЯКЩО
`Server:GroupClaim:Enabled=false`. З `Server:GroupClaim:Enabled=true` демон SHALL стартувати в
режимі безперервного авто-receive (`UseManualReceiveMode=false`, R3.1) — це скасовує send-only
передумову V2 і потрібне, щоб код-сканер груп-claim матчив коди у вхідному потоці. Head-of-line-ефект
спільного stdout-loop (Уточнення-3) SHALL зніматися окремим non-blocking споживачем.

#### Scenario: дефолт — manual receive
- **WHEN** сервіс стартує з `GroupClaim:Enabled=false` (дефолт)
- **THEN** демон у manual receive-mode (send-only) — поведінка Фази 1 незмінна

#### Scenario: увімкнений group-claim — безперервний авто-receive
- **WHEN** сервіс стартує з `GroupClaim:Enabled=true`
- **THEN** демон безперервно ресивить; claim-коди з груп-чатів доходять до сканера без ручного receive

