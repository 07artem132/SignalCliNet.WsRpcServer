# Ops-runbook (add-ops-observability)

Операційна база для production-готовності: fail-path, реактивний анти-бан, idempotency,
heartbeat, метрики. Це **базовий** документ (закладений виконавцем секцій 1-3); повний runbook +
API-reference доповнюється окремо.

## Метрики (task 1.1)

App-level `Meter` **`SignalCliNet.WsRpcServer`** (окремий від фреймворкового `WsRpcServer`),
`Diagnostics/AppDiagnostics.cs`. Інструменти:

| Інструмент | Тип | Теги | Що лічить |
|---|---|---|---|
| `wsrpc.app.sends` | Counter | `result=ok\|denied\|paused\|deduped` | рішення send-шляху |
| `wsrpc.app.global_pause` | UpDownCounter | — | стан глобальної паузи (1 паузнуто / 0 активно) |
| `wsrpc.app.dedup` | Counter | `result=hit_done\|hit_pending\|miss` | idempotency-lookup |
| `wsrpc.app.heartbeats` | Counter | — | розіслані heartbeat-нотіфи |

**Privacy-інваріант:** єдиний дозволений тег-ключ — `result` (allowlist). НІКОЛИ account/номери/тіла/
токени. Пінить guard-тест `AppDiagnosticsTests` (MeterListener). Інструментація інертна без слухачів.
Логи проходять redact-дисципліну (метод/статус/id — не PII), як і решта коду (CLAUDE rule #4).

## Fail-path: коди помилок (tasks 2.1/2.2/2.3)

V10 catch-матриця (`Security/Fail/UpstreamSendFailureMapper.cs`) навколо send-dispatch мапить
типізовані upstream-винятки + non-RPC на **санітизовані** клієнт-коди (без витоку внутрішніх деталей/PII):

| Джерело | Клієнт-код | Пауза бота? |
|---|---|---|
| `RateLimitException` (upstream `-5`) | `-32005` (rate-limited, `retry_after`) | **так** (global-pause) |
| `CaptchaRequiredException` (`-6`) | `-32005` (rate-limited, `retry_after`) | **так** (global-pause) |
| `UntrustedIdentityException` (`-4`) | `-32012` (untrusted identity) | ні (per-recipient) |
| базовий `JsonRpcException` (`-1`/`-3`/…) | `-32603` (generic internal) | ні |
| restart-cancel / IO / timeout / disposed (W10) | `-32009` (service restarting, retry) | ні |
| client-cancel (client token скасовано) | пропускається (StreamJsonRpc) | ні |
| idempotency pending (at-most-once) | `-32011` (outcome unknown) | ні |
| несподіване | `-32000` (generic invocation-error) | ні |

Obsolete-типи (`IdentityChangedException`, видалений в SignalCli.NET) НЕ ловимо.

### Реактивний анти-бан / global-pause (task 2.2)

`Security/Admission/GlobalPauseGate.cs` (замінює `NoopGlobalPauseGate` у DI). На upstream `-5`/`-6`
send-шлях викликає `PauseFor(...)` — бот паузиться (перший крок W16-admission деніїть кожен наступний
send із `-32005`/`retry_after`), з **jittered escalating backoff** (`Server:Pause:BasePauseSeconds`
default 60, ескалація ×2, cap `MaxPauseSeconds` default 900, +до 25% джитер). Auto-resume — на
**монотонному** deadline (D15, імунний до стрибків wall-clock).

**Чому -5/-6 глобальні, а -4 ні:** rate-limit/captcha — стан акаунта-відправника проти Signal-сервера;
будь-який наступний send через того самого (спільного) бота отримає те саме → пауза мусить бути
глобальною, інакше клієнти «долбитимуть» і поглиблять бан. Untrusted-identity (`-4`) — стан конкретного
**отримувача** (зміна ключа); per-recipient, actionable (verify safety number) — глобальна пауза тут
заблокувала б відправку всім через одного «поганого» отримувача.

**Сурфейс стану:** (a) нотіфи `bot.paused`/`bot.resumed` (reason + `retryAfter`, БЕЗ PII) усім активним
WS-клієнтам; (b) метрика `wsrpc.app.global_pause`; (c) прапорець `paused` у heartbeat-payload.

### V11 blast-radius: health-restart рве in-flight (task 2.4)

Upstream `SignalCliHealthMonitor` (SignalCli.NET) при фейлі **force-рестартить signal-cli**. Це рве
**усі in-flight send-и всіх тенантів** (спільний демон). Такий розрив сурфейситься клієнту окремим
типізованим кодом **`-32009` «service restarting; retry»** (з кореляцією request-id — нотіф не губиться
мовчки), а НЕ мовчазною втратою. **Клієнт зобов'язаний ретраїти** (ідемпотентно — краще з `messageId`,
див. нижче). Це відрізняється від client-cancel (розрив з боку клієнта), який просто пропускається.

## Клієнтський контракт (task 3.1)

- **Connect-on-demand + burst-reuse (W14).** Клієнт може конектитись на вимогу і **перевикористовувати**
  один WS-конект для серії (burst) викликів — не відкривати сокет на кожен send.
- **Jittered backoff + одиниці `retry_after` (G13).** Усі `retry_after` — у **секундах**. На `-32005`
  клієнт чекає `retry_after` (+власний джитер, щоб не синхронізувати ретраї) перед повтором. Наосліп не
  долбити.
- **C6 (wake→connect латентність).** Для ефемерних клієнтів (MV3 Service Worker) латентність
  пробудження→конект необмежена; клієнт має толерувати її й не вважати повільний конект помилкою.
- **Server-heartbeat / C5 (task 3.2).** Для persistent-WS сервер шле app-level `heartbeat`-нотіф
  (`{seq, timestamp, paused}`, БЕЗ PII) кожні `<25с` (`Server:Heartbeat:IntervalSeconds` default 20).
  Клієнт відрізняє живий сокет від завислого; **серія пропущених heartbeat → реконект**. Це НЕ WS-ping
  (той — транспортний рівень фреймворку), а app-level notification. За connect-on-demand MVP heartbeat
  не потрібен (`Server:Heartbeat:Enabled=false`).

## Idempotency send-шляху (task 3.3, T6/C2 — at-most-once)

`sendTextMessage` приймає опційний `messageId` (idempotency-key, клієнт-генерований). Коли заданий —
send дедуплікується **durably** (та сама SQLite, міграція v7, таблиця `send_dedup`, ключ
`(identity_id, message_id)` — без крос-тенантних колізій; переживає рестарт).

Потік (координатор `Security/Idempotency/IdempotentSendCoordinator.cs`), **dedup-lookup ПЕРЕД admission**:

1. `GetSendDedup` → **hit `done`**: повертаємо збережений результат БЕЗ admission і БЕЗ декременту
   бюджету. **hit `pending`**: `-32011` outcome-unknown (at-most-once). **miss**: далі.
2. miss → `AdmissionControl.Admit` (усі W16-гейти, вкл. **durable** reserve-then-send блок-резервацію
   бюджету). Deny → повертаємо denial БЕЗ запису dedup-рядка.
3. Admitted (одиницю бюджету durably зарезервовано) → атомарний `TryBeginSend` (`INSERT OR IGNORE`
   pending-рядка, `budget_reserved=1`). Після успішного send → `CompleteSend` (pending→done + результат).

**Канон при краші = at-most-once.** Краш між `pending` і send → ретрай (навіть після рестарту) бачить
`pending` → `-32011` outcome-unknown; send **НЕ** повторюється, бюджет **НЕ** декрементується вдруге
(над-відправка → бан — безпекова властивість; недосил допустимий, симетрія форфейту W13). Клієнт НЕ
ретраїть цей `messageId` наосліп.

> **Точна dedup+budget послідовність (ordering-i).** Бюджет резервуємо через admission (durable-at-block
> + crash-forfeit) **ДО** атомарного pending-claim — бо durable блок-резервація виконується власною
> транзакцією й не може безпечно вкластися у зовнішню SQLite-транзакцію pending-claim. Інваріанти
> зберігаються: budget-exhaust → deny ДО claim (жодного залиплого pending); краш після claim → ретрай
> отримує outcome-unknown (без повторного send/декременту); over-send неможливий. Єдиний «коштовний»
> край — конкурентний дубль того самого `messageId`: обидва резервують одиницю, програвший claim
> форфейтить свою (benign under-count, НІКОЛИ не over-send).

Коли `messageId` **не** заданий — поточна поведінка (admission як є, без dedup): зворотна сумісність.
```
