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
- **Scoping (R3.5) через per-account роутер (S11):** сирий notification-потік демона проходить
  через єдиний **per-account receive-роутер** (`account → owning-identity`), який фанаутить:
  own-linked incoming → лише власнику, shared-bot-потік → лише сканеру. Усі майбутні consumer-и
  (events subscribe Phase-3) підписуються ЧЕРЕЗ роутер — інваріант W25-inbound тримається
  механізмом, а не тим, що «поверхні ще нема».
- `listGroups` → лише claim'нуті/власні групи (R3.2, разом із chokepoint-фільтром).
- Privacy: скан-і-викинь; тіла/номери/вкладення не логувати/не зберігати.
- Revocation: binding падає при revoke identity; бот прибрали з G → send фейлиться;
  опційний admin-revoke binding'у.
- Per-identity quota на group-claims (footprint-контроль, D2).

## Рішення флоу (GAPS S10 ЗАКРИТО)
1. **Ентропія/формат claim-коду:** ≥96-bit CSPRNG, візуальні блоки для ручного вводу (як інвайти).
2. **Membership churn:** перевірка членства (U ∈ G) при КОЖНОМУ send через бот (primary) +
   TTL binding'у (backstop). U вийшов/вигнаний із G → send-відмова + інвалідизація binding'у;
   закриває «ex-member шле в G безстроково».
3. **Delegation-by-proxy — accepted-risk (док у `docs/shared-bot.md`):** код секретний до U,
   one-time, TTL ~10 хв, bound (U,G); вставлення коду будь-ким лише активує binding САМОГО U
   (не грантить прав вставнику) → privilege-escalation немає, лише griefing-burn (re-request працює).

## Вплив
- Код: receive-конфіг, **per-account receive-роутер (S11)**, `Events/`-споживач-сканер, claim
  store, RPC `requestGroupClaim`/`confirmGroupClaim` (через chokepoint; код — секрет → source-gen
  DTO + `[JsonIgnore]`, A1).
- Specs: `group-claim` (нова capability); MODIFIED `ws-jsonrpc-gateway` (receive-mode).
- Прогалини: закриває **S10** (три рішення вище), **S11** (per-account роутер + W25-рішення
  accepted-risk).
