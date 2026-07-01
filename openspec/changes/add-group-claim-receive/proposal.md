# add-group-claim-receive

## Чому
Апгрейд W17 для груп: авторизація на рівні **(identity, group)** замість лише account —
знімає «бот = відкрите реле» для груп-таргету. Потребує безперервного авто-receive (R3.1),
що скасовує send-only передумову V2 і робить head-of-line-caveat (Уточнення-3) актуальним.

## Що змінюється
- **Receive:** `UseManualReceiveMode=false` — бот безперервно ресивить; код-сканер —
  окремий non-blocking споживач із drop-policy (не на гарячому RPC-шляху).
- **Потік claim:** юзер додає бота в G → `requestGroupClaim(G)` → one-time-код TTL ~10 хв,
  bound (identity U, G), rate-limited → юзер вписує код у чат G → сканер матчить →
  атомарний consume-and-bind (`UPDATE…WHERE consumed=0` + rows-affected — патерн W20).
- **Scoping (R3.5):** сканер бігає ЛИШЕ по receive-потоку shared-bot; own-linked incoming
  приватний власнику — жодна receive/event-поверхня не сурфейсить його іншим (W25-inbound).
- `listGroups` → лише claim'нуті/власні групи (R3.2, разом із chokepoint-фільтром).
- Privacy: скан-і-викинь; тіла/номери/вкладення не логувати/не зберігати.
- Revocation: binding падає при revoke identity; бот прибрали з G → send фейлиться;
  опційний admin-revoke binding'у.
- Per-identity quota на group-claims (footprint-контроль, D2).

## Відкриті прогалини цього флоу (GAPS S10, вирішити ДО імплементації)
1. Ентропія/формат claim-коду не запінені (інвайти мають ≥96-bit; тут — ні).
2. Membership churn: U вийшов/вигнаний із G — binding живе → ex-member шле в G через бота.
3. Соц-інженерний residual: код, вставлений у G БУДЬ-КИМ, грантить права саме U
   (delegation-by-proxy) — прийняти й задокументувати або звузити.

## Вплив
- Код: receive-конфіг, `Events/`-споживач-сканер, claim store, RPC `requestGroupClaim`/
  `confirmGroupClaim` (через chokepoint; код — секрет → source-gen DTO + `[JsonIgnore]`, A1).
- Specs: `group-claim` (нова capability); MODIFIED `ws-jsonrpc-gateway` (receive-mode).
