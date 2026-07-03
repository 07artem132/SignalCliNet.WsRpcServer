# add-group-claim-receive

## Чому
Апгрейд W17 для груп: авторизація на рівні **(identity, group)** замість лише account —
знімає «бот = відкрите реле» для груп-таргету. Потребує безперервного авто-receive (R3.1),
що скасовує send-only передумову V2 і робить head-of-line-caveat (Уточнення-3) актуальним.

## Що змінюється
- **Receive:** `UseManualReceiveMode=false` — бот безперервно ресивить; код-сканер —
  окремий non-blocking споживач із drop-policy (не на гарячому RPC-шляху).
- **Потік claim (уточнено T2/T3):** юзер додає бота в G → `requestGroupClaim()` (без
  аргументу-групи — T3: group-ID нізвідки взяти, `listGroups` до claim'у G не покаже) →
  one-time-код TTL ~10 хв, bound лише до (identity U), rate-limited → юзер вписує код у
  чат G → сканер матчить → атомарний consume-and-bind (`UPDATE…WHERE consumed=0` +
  rows-affected — патерн W20). **Групу і anchor фіксує сканер:** binding =
  `(identity U, group G-де-з'явився-код, anchorAci = ACI вставника)` (T2; ACI, не E.164 —
  менше PII у сторі).
- **Scoping (R3.5) через per-account роутер (S11):** сирий notification-потік демона проходить
  через єдиний **per-account receive-роутер** (`account → owning-identity`), який фанаутить:
  own-linked incoming → лише власнику, shared-bot-потік → лише сканеру. Усі майбутні consumer-и
  (events subscribe Phase-3) підписуються ЧЕРЕЗ роутер — інваріант W25-inbound тримається
  механізмом, а не тим, що «поверхні ще нема». **Forward-правило розширення (T7, фіксується
  зараз, імплементація — Phase-3):** shared-bot-потік фанаутиться (а) сканеру завжди;
  (б) events-consumer'у identity X — ЛИШЕ envelope'и груп, на які X має активний binding
  (group-scoped фільтр по bindings); (в) решта shared-bot трафіку (DM боту тощо) —
  admin-only або drop. Phase-3 events-поверхня не ревізує інваріант, а розширюється за цим правилом.
- `listGroups` → лише claim'нуті/власні групи (R3.2, разом із chokepoint-фільтром).
- Privacy: скан-і-викинь; тіла/номери/вкладення не логувати/не зберігати.
- Revocation: binding падає при revoke identity; бот прибрали з G → send фейлиться;
  опційний admin-revoke binding'у.
- Per-identity quota на group-claims (footprint-контроль, D2).

## Рішення флоу (GAPS S10 ЗАКРИТО; механізм уточнено T2/T3)
1. **Ентропія/формат claim-коду:** ≥96-bit CSPRNG, візуальні блоки для ручного вводу (як інвайти).
2. **Membership churn — anchor-модель (T2 замінює абстрактне «U ∈ G»):** binding несе
   `anchorAci` — ACI вставника коду, зафіксований сканером при confirm; перевірка при
   КОЖНОМУ send через бот = `anchorAci ∈ members(G)` (primary) + TTL binding'у (backstop).
   Anchor вийшов/вигнаний із G → send-відмова + інвалідизація binding'у; закриває «ex-member
   шле в G безстроково». Перф: кеш member-list із коротким TTL (~60с) + інвалідизація по
   group-update подіях авто-receive (не смикати signal-cli на кожен send).
3. **Delegation-by-proxy — accepted-risk із чесною семантикою (T2):** код секретний до U,
   one-time, TTL ~10 хв; вставлення коду будь-ким активує binding САМОГО U (не грантить прав
   вставнику), але anchor = вставник → **право U живе, поки поручитель-вставник лишається
   членом G**. Privilege-escalation немає; griefing-burn → re-request працює. Residual T3
   («чужий код вставили в іншу групу») дешевий: binding дає права лише U, U бачить свої
   bindings (`listMyBindings`) і revoke-ить зайві, quota обмежує кількість. Док у
   `docs/shared-bot.md`.

## Вплив
- Код: receive-конфіг, **per-account receive-роутер (S11)**, `Events/`-споживач-сканер, claim
  store, RPC `requestGroupClaim`/`confirmGroupClaim` (через chokepoint; код — секрет → source-gen
  DTO + `[JsonIgnore]`, A1).
- Specs: `group-claim` (нова capability); MODIFIED `ws-jsonrpc-gateway` (receive-mode).
- Залежності (T1): durable-стор bindings і `account → owning-identity` — таблиці з
  `add-secure-deploy-persistence` (tasks 3.1/3.2).
- Прогалини: закриває **S10** (три рішення вище), **S11** (per-account роутер + W25-рішення
  accepted-risk), **T2** (anchor-модель — tasks 0.2/2.2/2.5, DoD 4.6/4.8), **T3**
  (group-agnostic код + самокерування bindings — tasks 2.1/2.2/2.6, DoD 4.9), **T7**
  (forward-правило роутера).
