# OpenSpec: SignalCliNet.WsRpcServer

Декомпозиція `PLAN-notifications-backend.md` (885 рядків, 7 ітерацій звірки + рішення власника R3.x)
на OpenSpec-структуру. План лишається джерелом деталей/доказів (посилання виду `D1`, `G3`, `W13`,
`R3.1` вказують на його розділи); тут — нормалізована структура: **що вже збудовано** (`specs/`)
і **що заплановано** (`changes/`), без append-only нашарувань, у яких superseded-текст живе поруч
із заміною.

## Контекст

Генеричний WS JSON-RPC 2.0 Signal-gateway (рішення R3.3: consumer-agnostic; розширення «Потужність» —
один зі споживачів). Стек: net10.0, SignalCli.NET 4.10.0, JSON-RPC.NET 2.7.0, signal-cli 0.14.3 (JDK 25).
Конвенції коду — `.claude/rules/conventions.md`; коментарі/логи українською; privacy-контракт:
не логувати тіла/номери/токени.

## Статус (звірено з кодом, 2026-07-01)

- **Фаза 1 — ВИКОНАНА** (гілка phase1-dod змерджена): loopback-bind `127.0.0.1`, Groups DI,
  тести `tests/`, CI `build.yml`, health-ендпоінт, `docs/self-host.md`, privacy-фікси A2/W6.
- **Task-0 — ВИКОНАНИЙ**: `JSON-RPC.NET` вже 2.7.0 у csproj (міграція breaking-2.0.0 пройдена).
- Розділ плану «Поточний стан» цим застарів; as-built зафіксовано у `specs/`.

## Capabilities (as-built → specs/)

| Capability | Зміст |
|---|---|
| `ws-jsonrpc-gateway` | WS JSON-RPC транспорт + RPC-поверхня (accounts/devices/messages/groups), msg-size cap, privacy-логи |
| `self-host-deployment` | Docker/compose, health, loopback-безпека Фази 1, локальний NuGet-feed |

## Changes (заплановано → changes/)

Порядок = залежності. Усі 8 — Фаза 2/3 плану. `add-secure-deploy-persistence` стоїть
другим НЕ хронологічно, а як **фундамент** (T1): durable SQLite-стор, auto-gen CA,
encrypted-том — їх споживають changes 2, 3, 5, 6 (кожен proposal називає свій артефакт).

| # | Change | Джерело в плані | Залежить від |
|---|---|---|---|
| 1 | `add-authz-chokepoint` | Phase-2 task-1, V8/V9, R3.2/R3.5/R3.6, A5 | task-0 (done) |
| 2 | `add-secure-deploy-persistence` | Phase-2 task-3/9, G1/G4, R3.4/R3.7, D17 | — (фундамент: стор/CA/том для 3-6) |
| 3 | `add-authn-token-pop` | Phase-2 task-2/4, D1/D7/D8/D10/G8, W18/W19/W21, C1-C4 | 1, 2 (durable-стор токенів/device-секретів) |
| 4 | `add-admission-rate-limits` | Phase-2 task-5, W13/W16-рішення, G2/G3/G6, D2/D12 | 1, 2 (durable budget store) |
| 5 | `add-link-sessions` | Phase-2 task-6, D16+W9-рішення, G7 | 1, 3 |
| 6 | `add-invites-admin` | Phase-2 task-7/8/11, G5/G10/G12, R3.6/R3.8, M8 | 1, 2 (CA + `0600` на томі), 3 |
| 7 | `add-group-claim-receive` | «Group-claim» + R3.1/R3.5, W20/W25 | 1, 2 (стор bindings/`account→identity`), 3, 4 |
| 8 | `add-ops-observability` | Фаза 3 повністю, D13, V10/W10, G11/G13, C2/C5-серверні | 1-7 |

## Прогалини

Залишкові прогалини логіки/захисту, знайдені при декомпозиції (понад D/G/V/W/N/C/A/U плану), —
у [`GAPS.md`](GAPS.md) (серія S). Кожен change, якого стосується S-прогалина, посилається на неї
у своєму `proposal.md`.

Незалежне рев'ю самої OpenSpec-структури (2026-07-03) дало **серію T** (T1–T8, теж у
[`GAPS.md`](GAPS.md)) — переважно діри на швах між change-ами: хибний граф залежностей (T1),
anchor-модель membership-check (T2), group-agnostic claim-код (T3), in-band `4401` (T4),
token-renewal через PoP (T5), durable idempotency (T6), forward-правило роутера (T7),
доc-дрейф (T8). Усі закриті; рішення інлайнені у відповідні changes.
