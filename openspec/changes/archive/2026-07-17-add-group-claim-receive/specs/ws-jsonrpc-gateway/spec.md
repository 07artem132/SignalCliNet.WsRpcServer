# ws-jsonrpc-gateway — delta

## MODIFIED Requirements

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
