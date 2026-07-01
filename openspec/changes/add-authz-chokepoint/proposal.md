# add-authz-chokepoint

## Чому
Фреймворковий `[RpcAuthorize]` — deny-by-default лише для атрибутованих методів; неатрибутований
метод відкритий (звірка №4). Ізоляція акаунтів тримається на єдиній лінії (`AssertAccountAllowed`,
табл. №1) — потрібен герметичний default-deny chokepoint + inbound/outbound IDOR-guard.

## Що змінюється
- Pre-dispatch фільтр з **декларативним реєстром політик**, незалежним від авто-дискавері:
  метод без явної політики → `-32601` на вході, до виконання.
- Політики: `Public` / `IdentityOnboarding` / `AccountCommand` (inbound guard) /
  `AccountQuery` (inbound + outbound `FilterReadOutputAsync`).
- Набір дозволених акаунтів виводиться з principal, ніколи з аргумента (анти-IDOR).
- Видимість (R3.2/R3.5/R3.6): `listGroups` — ВИКЛЮЧНО claim'нуті/власні групи caller (закриває G9);
  own-linked акаунти приватні власнику; admin бачить усі акаунти (visibility ≠ operation).
- Outbound-фільтр fail-closed: виняток → empty/error, ніколи raw (W22).
- Санітизація всіх клієнтських помилок; generic-текст, деталі — лише в серверний лог.
- `apiVersion` + мінімальна capability-negotiation у handshake (рішення A5): mismatch →
  типізована «upgrade required», не тихий `-32601`.
- Валідація/нормалізація recipient (E.164/group-ID) на chokepoint до dispatch (D11).

## Технічні обмеження (звірено з кодом)
- V9: правка `Sessions/SignalRpcSession.cs` — `new JsonRpc(...)` → `AuthorizingJsonRpc(handler,
  principal, policy)` + виставити `Principal` на сесії; нові DTO → `SignalCliSerializerContext`.
- V8: адаптери root-resolved/shared → principal читати per-invocation з call-context,
  НЕ з ctor адаптера.
- W8: IDOR-guard на КОЖНОМУ account-bearing методі, не лише 5 MVP; build-guard валить збірку
  на методі без явної політики (W23: guard — belt-and-suspenders, dispatch-time default-deny первинний).

## Вплив
- Код: `Sessions/SignalRpcSession.cs`, новий `Security/` (реєстр політик, guard, фільтр),
  `Serialization/SignalCliSerializerContext.cs`, тести.
- Specs: `authz-isolation` (нова capability).
- Прогалини: закриває S1 (суперечність G9-формулювань), S6 (D11 без власника); див. `openspec/GAPS.md`.
