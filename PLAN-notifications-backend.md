# План: WsRpcServer → бекенд Signal-каналу для модуля сповіщень Потужності

## Контекст і межі

WsRpcServer — **тільки транспорт Signal-каналу** універсального модуля сповіщень. Детект подій (CREATED/UPDATED/DELETED, layer health, стрім-логи VEZHA, помилки обробки), підписки, UI, Chrome push, element — усе на боці розширення (Потужність). Цей сервер лише надійно шле в Signal та керує акаунтами/пристроями/групами.

Поточний стан (станом на старт плану):
- Ядрові RPC-методи працюють end-to-end A→B: `listAccounts`, `startLink`, `finishLink`, `sendTextMessage`.
- `ISignalGroupsRpc` / `SignalGroupsRpcAdapter` — написані, але **не зареєстровані в DI** (`Extensions/SignalRpcExtensions.cs`). **(Звірка-3 V7: код уже закоммічено `eca3852` — бракує ЛИШЕ DI-рядка, не «3 незакоммічені файли».)**
- Хост standalone є (`Program.cs`, `0.0.0.0:9000`), signal-cli у JSON-RPC daemon-режимі через NuGet `SignalCli.NET 4.10.0`.
- Тестів — **0**. Auth / ізоляції користувачів — **нема**.
- База JSON-RPC.NET уже містить `Authorization/` (RpcAuthorizeAttribute, IRpcAuthorizationPolicy, StaticRoleMapAuthorizationPolicy), `Security/` (mTLS, NodeIdentity, TLS), connection quotas, `WsRpcServerDiagnostics` — **частково підключаємо, частково пишемо з нуля** (звірено з кодом, див. розділ «Звірка плану з кодом»). Конкретно: `[RpcAuthorize]` = deny-by-default **лише для атрибутованих** методів (НЕ герметичний default-deny — chokepoint net-new); principal — **лише з mTLS** (токен→principal net-new); connection quota — **лише ГЛОБАЛЬНА** `MaxConcurrentConnections` (per-IP / idle-timeout / auth-timeout — net-new); `MaxMessageSizeBytes` — **не читається ніде в сорсі (no-op)**, реальний msg-size-cap net-new.

## Уточнення з рев'ю (внесено)

Зовнішнє рев'ю + чек коду SignalCli.NET дали 4 рішення:

1. **Модель зʼєднання — ЗАФІКСОВАНО: connect-on-demand для MVP.** MV3 Service Worker ефемерний (~30с idle → kill), персистентний WS вмирає з ним і дає reconnect-шторм. Сповіщення — потік **вихідний**, тому MVP: `connect → authenticate → send → close` на нотіф (немає півживого сокета, немає keep-alive-гімнастики). Персистентний WS + keep-alive (`chrome.alarms`) + exponential-backoff-with-jitter — **лише** коли додамо receive-шлях (Phase 3). Це клієнтський контракт (Потужність), сервер під нього готує backoff-friendly відповіді.
2. **Backoff-friendly сервер.** Проти reconnect-шторму: `429` + `Retry-After` на перевищенні квоти, connection-quota per-IP/identity (Phase 2, rate-limit).
3. **signal-cli IPC — НЕ вузьке місце (перевірено в коді).** SignalCli.NET уже: async `ReadLineAsync` на bg-таску (`JsonRpcClient.cs:286`), serialized async-запис `SemaphoreSlim(1,1)` (`:568,584`), кореляція `ConcurrentDictionary<id,TCS>` (`:42,356`) → паралельні in-flight, повільна відповідь не блокує інших і не блокує thread-pool WS-сервера; нотифікації через bounded `Channel(1024, Wait)` (`:101`). Socket-режим не юзається і **не потрібен** — pipe не bottleneck. Реальний cap — рейт-ліміти мережі Signal. → Жодної роботи тут, лише **perf-gate перед оптимізацією** (Phase 3).
   - **Caveat (receive-шлях):** один stdout-loop + notification-channel `Wait` → повільний споживач нотифікацій робить head-of-line блок і для RPC-відповідей. Стосується лише Phase 3 (subscribe).
4. **Агрегація/debounce/coalesce подій — НЕ цей репо.** «50 апдейтів обʼєкта ≠ 50 пушів» — шар підписок розширення (Потужність), не WsRpcServer. Фіксуємо в плані ідеї/клієнта.
5. **Топологія деплою — ЗАФІКСОВАНО (Варіант 1): standalone Kestrel сам термінує TLS.** Жодного reverse-proxy/ingress у MVP — `Host.CreateDefaultBuilder`/Kestrel слухає `wss://` напряму (TLS 1.3, 1.2 floor, HSTS). **Наслідок для безпеки:** єдиний логер — сам застосунок, тож захист subprotocol-токена від витоку в логах зводиться до **не логувати `Sec-WebSocket-Protocol`/`Authorization` у застосунку** + тримати Kestrel/ASP.NET request-logging нижче рівня, що пише заголовки. Проксі-конфіги скрабінгу (nginx/Traefik/ALB) **не застосовні** за цієї топології — повертати лише якщо колись з'явиться ingress/LB перед сервером (тоді й писати конфіг під конкретний проксі).
6. **Масштаб — ЗАФІКСОВАНО: ОДИН інстанс, горизонтального масштабування НЕ буде.** Наслідки, що каскадять на безпеку й архітектуру:
   - **Redis НЕ потрібен.** Початковий мотив централізованих лічильників (мультіінстанс) відпав. Ефемерний стан (rate-limit лічильники, link-сесії) — **in-memory**. Durable стан (identity-записи, token-хеші, прив'язки account, device-секрети, агрегатний бот-бюджет) — **вбудована БД** (SQLite/LiteDB/файл), НЕ Redis. Менше рухомих частин, менше attack-surface (нема Redis AUTH/TLS, які можна зіпсувати).
   - **`finishLink` атомарність — in-process lock**, не Lua/`GETDEL`. **Увага (звірено з кодом):** сервер **БАГАТОПОТОКОВИЙ** (StreamJsonRpc-dispatch + NetCoreServer per-session), «один інстанс ≠ single-threaded». Атомарність забезпечує **САМЕ lock**, не відсутність конкуренції; embedded-DB запис іде під WAL + retry на `SQLITE_BUSY`.
   - **Revoke живих конектів — in-process**, без крос-інстансного pub/sub.
   - **Availability — SPOF за дизайном** (прийнятно для MVP): рестарт рве живі сокети (ок при connect-on-demand), вбиває in-flight лінки (ок, TTL 120c). Стеля пропускної — вертикальна; реальний лімітер однаково агрегатний бот-бюджет.
   - **Caveat анти-бан:** агрегатний бот-бюджет НЕ скидати на рестарт → інакше crash-loop/рестарт-шторм скидає захист → over-send → бан Signal. Персистити в durable АБО cold-start з **порожнього** бакета (не повного). **Порядок (звірено): persist декремент ДО реальної відправки** (краш між persist і send → недосилання = fail-safe; навпаки → over-send → бан).

## Звірка плану з кодом (verification аудиту, 2026-06-25)

Перевірено проти локального сорсу `JSON-RPC.NET/` (фреймворк, v2.7.0) і `SignalCli.NET/` (v4.10.0). Це коригує кілька припущень плану — **читати перед Фазою 2**.

> **🔴 КРИТИЧНО (Звірка-3 V1):** ця звірка йшла проти **2.7.0-сорсу**, але апка пінить **`JSON-RPC.NET 1.1.0`** (`csproj:13`). Увесь auth/security/observability стек нижче з'явився у 2.6.0/2.7.0 — у 1.1.0 його **НЕМАЄ**. Усі цитати нижче істинні для коду, але **не для білда апки** до bump 1.1.0→2.7.0 (Phase-2 task-0, перетинає breaking 2.0.0). Деталі — Звірка-3 V1.

| # | Припущення плану | Вердикт | Доказ / наслідок |
|---|---|---|---|
| 1 | signal-cli тягне багато акаунтів (бот + чужі номери) | **Працює, але стейкс вищі** | Демон стартує `jsonRpc --receive-mode=on-start` **без `-a`** → multi-account; `account` — параметр виклику (`SignalCli.NET/.../SignalCliOptionsExtensions.cs:46-47`; `ISignalMessage` send бере `account`). **signal-cli НЕ ізолює акаунти** — виклик з `account=X` шле від імені X без перевірки володіння → `AssertAccountAllowed` = **ЄДИНА** лінія ізоляції (не defense-in-depth). Один процес / один pipe / один `--config`-том = спільний домен відмови: health-monitor force-restart рве ВСІ акаунти. Send-only MVP → `--receive-mode=manual` (`UseManualReceiveMode=true`), щоб демон не receive-ив за всі акаунти. **(Звірка-3 V2 🟠: `--receive-mode=on-start` НЕ дефолт — `UseManualReceiveMode` default=`true` (`SignalCliOptions.cs:56`), app `Program.cs` не чіпає → demon ВЖЕ `manual`. Send-only діє з коробки; task-7 = no-op.)** Деструктив (`unregister`/`deleteLocalAccountData`/device) гейтиться `SignalCliOptions.EnableDestructiveOperations`. |
| 2 | JSON-RPC batch обходить per-message rate-limit | **Спростовано на цьому стеку** | StreamJsonRpc не підтримує batch; транспорт десеріалізує 1 повідомлення/фрейм (`WebSocketMessageHandler.cs:149`), array-парсингу нема. 1 WS-msg = 1 виклик. Але **сам** rate-limit net-new (фреймворк має лише глобальний cap + parse-throttle). Перепровірити, якщо колись міняти протокол-шар. |
| 3 | «один інстанс → single-threaded → атомарність тривіальна» | **Підтверджено (помилка плану)** | Фреймворк багатопотоковий. Ніщо не серіалізує доступ → атомарність `finishLink`/бюджету тримає ВИКЛЮЧНО in-process lock; embedded-DB треба WAL + retry на `SQLITE_BUSY`. |
| 4 | «герметичний default-deny chokepoint підключаємо, не пишемо» | **Підтверджено + загострено (найвищий пріоритет)** | Фреймворковий `[RpcAuthorize]` = deny-by-default **лише для атрибутованих** методів; **неатрибутований метод відкритий** (`JSON-RPC.NET` CLAUDE.md rule #11; `Authorization/RpcAuthorizationEnforcer.cs:43` спрацьовує тільки за наявності атрибута). Реєстрового «незареєстрований/неатрибутований → відмова» chokepoint **НЕМАЄ**. → герметичний default-deny **ПИШЕМО** (або атрибутуємо КОЖЕН метод + кастомна політика, що default-deny). Забутий атрибут = відкритий метод. Токен→principal теж net-new: фреймворковий principal лише з mTLS (`AbstractSecureJsonRpcSession.TryEstablishPrincipal`); для токена — самим виставити `AbstractJsonRpcSession.Principal` (settable, `:86`) + сконструювати `AuthorizingJsonRpc(handler, principal, policy)`. |
| 5 | per-IP cap «фреймворковий ConnectionQuota як база» | **Уточнено** | Квота фреймворку — **лише ГЛОБАЛЬНА** `MaxConcurrentConnections` (`Core/JsonRpcServerConfig.cs:114`, default 0=безліміт, енфорс в `OnConnected` до dispatch) — увімкнути. **Per-IP cap, idle-timeout, auth-timeout — НЕМАЄ** (deferred у фреймворку) → net-new. L4/TLS-handshake-флуд без проксі (Варіант 1) — поза покриттям застосунку. |
| msg | «msg-size 64KB енфорс під час накопичення» | **Підтверджено + гірше** | `MaxMessageSizeBytes` (default 100MB) **ніде не читається в сорсі фреймворку** (grep: лише запис config + XML-док). **(Звірка-3 V3 🟠: але АПКА його енфорсить — `SignalRpcSession.cs:221` закриває oversized `MessageTooBig` на дефолтних 100MB. Не «no-op»; реальна прогалина: 100MB задорого + чек на зібраному msg, не під час накопичення фреймів.)** Транспорт накопичує у Pipe з backpressure (`PipeThresholdBytes` 1MB), **без hard-abort**. 64KB-cap + frame-count + assembly-timeout (slowloris) + zip-bomb-чек — **повністю net-new**. |
| 9/11 | реактивний Signal-rate-limit / зміна safety-number | **Підтверджено + матеріал готовий** | SignalCli.NET кидає типізовані: `UntrustedIdentityException` (-4, send падає на зміненому safety-number), `RateLimitException` (-5), `CaptchaRejected` (-6, base). Phase 3 пауза-бота й обробка untrusted-recipient мають готові типи для `catch`. |

### Design-level залишкові прогалини (код не суперечить — відкриті; чек-лист Фази 2)

Формат: **назва** — *Ризик* / *Доказ* / *Мітигація*. Severity: 🔴 високий, 🟠 середній, 🟡 низький.

**🔴 D1. Корінь довіри PoP — enrollment device-ключа не описано.**
- *Ризик:* PoP перевіряє підпис device-ключем, але план не каже, ЯК сервер уперше дізнається публічний ключ пристрою й чому йому довіряти. Без визначеного enrollment PoP — порожній (зловмисник реєструє свій ключ).
- *Доказ:* у плані є «той самий ключ, що для re-onboarding», але крок реєстрації pubkey відсутній; TOFU під інвайтом не формалізований.
- *Мітигація:* `redeemInvite` (IdentityOnboarding, без токена) реєструє device-pubkey, прив'язаний до identity, у durable стор; це TOFU-корінь, гейтований одноразовим інвайтом. Будь-яка зміна ключа = новий інвайт/пін, не мовчазна.

**🔴 D2. Спільний бот — Signal-бан тригериться патерном НОВИХ контактів, не обсягом.**
- *Ризик:* агрегатний token-bucket ріже сумарний volume, але Signal банить за fan-out по багатьох НОВИХ отримувачах (first-contact). Бот у межах бюджету може розіслати першим контактам сотні різних номерів → бан, хоча бюджет не вичерпано.
- *Доказ:* план явно моделює лише обсяг («ріже обсяг, не таргетинг»); патерн нових-recipient не моделюється.
- *Мітигація:* окремий лічильник унікальних нових отримувачів/вікно (нижчий за загальний бюджет); при перевищенні — throttle нових контактів, не загального трафіку. Поєднати з реактивним детектом `-6 CaptchaRejected`/`-5` (Phase 3).

**🔴 D3. Cert-trust для extension→server при self-host.**
- *Ризик:* `wss` потребує валідного серта. Self-host на приватному IP/LAN не має шляху до публічного CA (ACME потребує домену), pinning відкинуто (HPKP). Extension не з'єднається із self-signed → Фаза 2 self-host неюзабельна.
- *Доказ:* план фіксує «TLS 1.3, HSTS, без pinning», але не модель довіри серта для не-доменного self-host.
- *Мітигація:* docs-розгалуження — (а) є домен → Let's Encrypt/ACME; (б) немає → internal-CA + інструкція додати корінь у довірені на машині-хості розширення; зафіксувати, що «голий IP без CA» не підтримується.

**🔴 D4. Startup fail-closed.**
- *Ризик:* перехід Фаза1(loopback,no-auth)→Фаза2(0.0.0.0,TLS,auth) — момент misconfig: dev-флаг/змінна може лишити no-auth на публічному біндингу = відкрите реле.
- *Доказ:* у плані немає вимоги жорсткої startup-перевірки; `JsonRpcServerConfig.Host` default `0.0.0.0` (`Core/JsonRpcServerConfig.cs:30`) — небезпечний дефолт.
- *Мітигація:* startup-assertion: відмова стартувати, якщо `Host != loopback` і (auth вимкнено АБО TLS не сконфігуровано). Жодного флага, що вимикає chokepoint у не-dev-збірці.

**🔴 D5. L4/TLS-handshake флуд без проксі (наслідок Варіанта 1).**
- *Ризик:* Kestrel прямо в інтернет = жодного SYN/TLS-handshake/accept-rate скрабінгу перед застосунком. Прикладні per-IP/per-token cap не рятують від вичерпання FD/CPU на хендшейках.
- *Доказ:* «Уточнення» п.5 фіксує no-proxy; фреймворк дає лише глобальний `MaxConcurrentConnections` (`Core/JsonRpcServerConfig.cs:114`) — це не L4-захист.
- *Мітигація:* задокументувати як accepted-risk + ЗОВНІШНІЙ L4-фаєрвол/conn-rate-ліміт на хості (nftables/cloud SG) як передумову публічного біндингу; не світити «голим». Якщо неприйнятно — повернути ingress (тоді й проксі-скрабінг субпротокол-токена з «Уточнення» п.5).
- *Вфолджено (U1, рішення 2026-06-25):* **frame-accumulation DoS-тріада** (frame-assembly-abort / max-frame-count / assembly-timeout-slowloris) сюди ж — структурно НЕ app-side і не фікситься bump'ом JSON-RPC.NET (gap у NetCoreServer нижче `OnWsReceived`). Та сама accepted-risk + L4-позиція; post-assembly 64KB-cap лишається дешевим частковим захистом; reverse-proxy/WAF з WS-frame-лімітами — якщо зʼявиться ingress. Опційно (non-blocking): NetCoreServer-upstream-issue. Прибрати ці пункти з Phase-2/3 **app**-task-листів (msg-row / task-5 §5) — це не in-repo app-робота.

**🔴 D6. Інтеграційний ризик: NetCoreServer handshake-reject seam.**
- *Ризик:* увесь auth-хендшейк плану (читати `Sec-WebSocket-Protocol`/`Origin` на upgrade, **401 ДО апгрейду**, echo лише non-auth субпротоколу) залежить від того, чи NetCoreServer дає (а) відхилити upgrade на рівні HTTP, (б) керувати вибраним субпротоколом. Якщо ні — дизайн хендшейка переробляється (fallback на first-message `authenticate`).
- *Доказ:* у фреймворку немає хука handshake — grep `OnWsConnecting`/`Sec-WebSocket-Protocol`/`Origin` у `Sessions/` порожній; auth там лише mTLS. Тобто це треба будувати на сирому NetCoreServer-seam і спершу довести, що він це вміє.
- *Мітигація:* спайк-перевірка `WsSession.OnWsConnecting(request, response)` (відхилення + вибір субпротоколу) ПЕРЕД проектуванням токен-хендшейка; задача 1 інвестигатора має це підтвердити емпірично. **(Звірка-3 V4 🟢: seam ПІДТВЕРДЖЕНО — NetCoreServer 8.0.7 дає `OnWsConnecting(HttpRequest,HttpResponse)` + `PerformServerUpgrade`. Severity ↓: primary-дизайн (401-до-upgrade) життєздатний, спайк = підтвердження поведінки, не ризик fallback.)**

**🟠 D7. PoP residual — заражений SW підписує challenge.**
- *Ризик:* device-ключ non-extractable, але скомпрометований SW може попросити WebCrypto підписати challenge (ключ використовний, лише не експортовний). PoP зупиняє offline-replay вкраденого токена з іншої машини, НЕ live-абʼюз через заражений SW.
- *Доказ:* план чесно фіксує residual для СХОВИЩА («шифр не рятує від скомпрометованого SW»), але для PoP такого застереження немає.
- *Мітигація:* дописати residual явно: «PoP ≠ захист від malicious-extension; серверний revoke + abuse-detection лишаються головною гарантією від live-абʼюзу».

**🟠 D8. PoP-pending стан між upgrade і підписом.**
- *Ризик:* токен валідується на upgrade (до PoP); PoP-challenge — після відкриття сокета. Вкрадений токен проходить upgrade і падає лише на PoP → треба явний стан, де RPC заборонені.
- *Доказ:* стан-машина хендшейка в плані не розділяє «authed-by-token» і «PoP-confirmed».
- *Мітигація:* стан `PoP-pending`: жодного RPC до успішного підпису; auth-таймаут на нього; сокети в цьому стані рахувати в half-open/per-IP cap.

**🟠 D9. Revoke не перериває in-flight RPC.**
- *Ризик:* «розрив живих конектів» + «lookup на кожен connect» не покривають запит, що ВЖЕ виконується: revoke стається, а останній `sendTextMessage` уже пішов у signal-cli (TOCTOU revoke↔dispatch).
- *Доказ:* план описує teardown на connect-рівні, не на рівні in-flight операції.
- *Мітигація:* ре-чек principal/`IsRevoked` безпосередньо перед dispatch у signal-cli, не лише на connect. Ризик низький при connect-on-demand (короткі сокети), але для fallback-сокета — обов'язково.

**🟠 D10. Pepper-rotation vs напрям hash-lookup.**
- *Ризик:* токен шукається по `HMAC(token, pepper)` як ключу в БД → щоб обчислити хеш для запиту, треба знати pepper наперед. `v1$hash` у записі дає версію, але lookup іде у зворотному напрямку.
- *Доказ:* план каже «старі через old_pepper, нові через new», але не вирішує chicken-and-egg напряму lookup.
- *Мітигація:* покласти hint версії pepper у **prefix токена** (`ptzh_v2_…`) → одразу правильний pepper, один lookup; інакше явно прийняти 2-lookup (new, потім old) під час вікна ротації.

**🟠 D11. Recipient/input валідація перед signal-cli.**
- *Ризик:* немає кроку нормалізації/валідації E.164 та group-ID до передачі в signal-cli. Не shell-injection (JSON-RPC, не shell — `ProcessConfig.ArgumentList`, кожен арг окремо), але malformed recipient = непередбачувана поведінка демона / тихі помилки доставки.
- *Доказ:* у chokepoint є account-guard, але немає валідації recipient/тіла; SignalCli.NET має `ValidateRecipients` (`SignalMessage`), але виклик/нормалізація на межі RPC не зафіксовані.
- *Мітигація:* на chokepoint валідувати+нормалізувати recipient (E.164/group-ID), розмір і кодування тексту до dispatch; відмова з generic-помилкою на невалідному.

**🟠 D12. Per-connection in-flight cap.**
- *Ризик:* msg-rate (~100/хв) обмежує ЧАСТОТУ, але один сокет може запайплайнити багато одночасних запитів → роздуття bounded-channel signal-cli + пам'яті, поки rate-вікно не закрилось.
- *Доказ:* SignalCli.NET корелює паралельні in-flight через `ConcurrentDictionary<id,TCS>` (паралелізм є за дизайном); ліміту одночасних на конект у плані немає.
- *Мітигація:* cap одночасних in-flight запитів на з'єднання (напр. ≤N); понад — черга або `-32005`.

**🟠 D13. Metrics endpoint auth/експозиція (Phase 3).**
- *Ризик:* `WsRpcServerDiagnostics` (Meter/ActivitySource «WsRpcServer») при експорті (напр. Prometheus-ендпоінт) може текти лічильники конектів/identity без auth.
- *Доказ:* фреймворк дає інструменти, але експозиція/scrape-ендпоінт — на споживачі; план не визначає auth для нього.
- *Мітигація:* метрики лише на loopback/окремому порту за auth; tag-keys тримати в allowlist (фреймворк уже пінить `{result}` — не додавати identity/recipient).

**🟠 D14. Admin onboarding flow.**
- *Ризик:* bootstrap призначає першу identity `role=admin`, але адмін теж потребує токен + device-ключ (PoP) для подальших дій; flow видачі цих креденшелів адміну не описаний.
- *Доказ:* задача 8 описує лише призначення ролі, не видачу токена/enrollment ключа адміну.
- *Мітигація:* bootstrap друкує одноразовий admin-токен (CLI-вихід, не лог) + адмін проходить той самий device-enrollment (D1); ідемпотентно.

**🟡 D15. Монотонний годинник для refill.**
- *Ризик:* token-bucket refill / rate-вікна на wall-clock ламаються при стрибку годинника/NTP-корекції (раптовий burst або заморозка).
- *Доказ:* план не специфікує джерело часу для лічильників.
- *Мітигація:* refill rate-limit/бюджету — на монотонному годиннику (`Stopwatch`/`TimeProvider` monotonic); wall-clock лише для абсолютного `ExpiresAt` токена/сесії.

**🟡 D16. Link TTL 120c vs ручне сканування QR.**
- *Ризик:* для «свого номера» в extension людина сканує QR вручну; 120c тіснувато → спокуса підняти TTL небезпечно високо.
- *Доказ:* TTL 120c фіксований у link-session; UX ручного скану не врахований.
- *Мітигація:* лишити 120c для бота (admin, швидко); для user-flow — або трохи довший TTL з тим самим one-time+identity-bound, або UX, що генерує QR безпосередньо перед скануванням. Не піднімати TTL без компенсації (one-time + rate-limit лишаються).

**🟡 D17. Crash/core-dump містить секрети в пам'яті.**
- *Ризик:* dump процесу містить токени/pepper/device-секрети у відкритому вигляді.
- *Доказ:* план згадує dump-leak лише для env-секретів (provisioning), не для in-memory токенів/pepper.
- *Мітигація:* вимкнути core-dumps для сервіс-юзера (`ulimit -c 0` / systemd `LimitCORE=0`); не писати дампи на не-LUKS-том; за можливості тримати pepper у защищеній пам'яті/обнуляти буфери після use.

### Звірка-2 — residual gaps (друга ітерація аудиту, 2026-06-25)

Друга ітерація проти `JsonRpcClient.cs` / `SignalDevices.cs`. Нові прогалини, що НЕ покриті D1–D17. Усі чотири 🔴 — про **durable-стан під конкуренцією / під single-instance-передумовою**; G1 — keystone (підриває фундамент in-process-lock та анти-бан тверджень).

**🔴 G1. Single-instance інваріант — НЕ енфорситься (keystone).**
- *Ризик:* уся атомарність (`finishLink`, бюджет) + анти-бан стоять на «ОДИН інстанс» (Уточнення п.6) + in-process lock. Lock — in-**process**. Другий процес на тому ж LUKS data-dir / SQLite-файлі → два локи = безглузді. Джерела випадкового double-start: rolling-restart overlap (новий стартує до завершення SIGTERM старого), k8s `replicas=2`, systemd `Restart=`-race, ручний подвійний запуск. Наслідок: **double-spend бюджету → over-send → бан Signal** (саме D2/анти-бан фейл), `finishLink`-race, SQLite-corruption.
- *Доказ:* план: «атомарність дає САМЕ lock» (§3-verdict) — але lock in-process; singleton-передумова ніде не гейтиться. `JsonRpcServerConfig.Host` default `0.0.0.0` має guard (D4); single-instance — ні.
- *Мітигація:* exclusive data-dir lockfile (`flock`/`O_EXCL` pidfile) на старті → refuse-start якщо held. Hard-документувати `replicas=1`. Без цього кожен in-process-lock claim — на чесному слові.

**🔴 G2. Агрегатний бот-бюджет = shared-fate starvation; per-user-sum vs aggregate не зведені.**
- *Ризик:* є І агрегатний бюджет, І per-user квота, але вони не помирені. Якщо Σ(per-user) > aggregate → aggregate реальний cap, хто шле першим вичерпує → **чесні юзери заблоковані** (не зловмисник — просто FIFO). Гірше з Phase-3 «паузити БОТ на `-6`/captcha»: один абʼюзер тригерить Signal-throttle → бот паузиться → **усі down**.
- *Доказ:* план фіксує shared-fate лише для інвайтів (task 7 soft-cap) і «бот=реле в межах бюджету» (обсяг). Shared-fate **самого бюджету між чесними** не змодельований.
- *Мітигація:* per-user floor (гарантований мінімум) під агрегатом, АБО fair-queue/round-robin замість pure-FIFO, АБО cap per-user = aggregate/N_active. Вибрати + докс.

**🔴 G3. Декремент бюджету НЕ під lock (під lock лише `finishLink`).**
- *Ризик:* lock явно повішений на `finishLink`, але декремент агрегатного бюджету теж багатопотоковий. Два паралельні send читають budget=1, обидва проходять, обидва декрементують → **over-send за бюджет → бан**. «persist ДО send» лагодить crash-ordering, НЕ read-modify-write race.
- *Доказ:* §3-verdict сам каже «ніщо не серіалізує доступ» — застосовано до finishLink, не до budget-лічильника. Сервер БАГАТОПОТОКОВИЙ (StreamJsonRpc-dispatch).
- *Мітигація:* той самий lock на budget-RMW, АБО атомарний `UPDATE … SET n=n-1 WHERE n>0` + перевірка rows-affected. Lock-free `Interlocked` недостатньо для пари decrement+persist (треба атомарність обох разом).

**🔴 G4. Durable DB — нема backup/recovery + нема schema-migration.**
- *Ризик:* embedded DB — ЄДИНА копія identity/token-хешів/прив'язок/device-секретів/бот-бюджету. SPOF на availability прийнято, але **corruption** (power-loss без WAL-checkpoint, LUKS-збій) = permanent lockout УСІХ вкл. admin → форс re-bootstrap → re-enroll усіх. Нема backup, restore-drill, integrity-check, `user_version`/migration для майбутніх змін схеми.
- *Доказ:* план покриває at-rest **конфіденційність** (LUKS, task 9), не **цілісність/відновлюваність**.
- *Мітигація:* періодичний шифрований DB-backup (НЕ на той самий том), документований restore, `PRAGMA user_version` + migration-runner, integrity-check на старті.

**🟠 G5. Admin-bootstrap one-time токен → stdout = контейнерні логи.**
- *Ризик:* D14-мітигація «друкувати admin-токен у CLI-вихід, не лог». У Docker/systemd stdout застосунку **І Є** лог (json-file driver / journald ловлять). Токен лягає в `docker logs`/journald = саме той витік, який D14 уникає, ще й персистентно.
- *Доказ:* D14 не розрізняє stdout-в-контейнері від справжнього логера.
- *Мітигація:* писати у `0600`-файл на LUKS-томі (або вимагати interactive TTY / `--out file`), НІКОЛИ stdout у контейнері; one-time read-then-shred.

**🟠 G6. Глобального in-flight cap немає у signal-cli-клієнті (звірено з кодом).**
- *Ризик:* D12 капить in-flight **per-connection** на app-шарі. Спільний `JsonRpcClient._pendingRequests` (`JsonRpcClient.cs:42`) **без cap** — `_pendingRequests[requestId] = tcs` (`:474`) безумовний add. Усі конекти ллються сюди. Signal повільний/throttle → pending TCS накопичуються по ВСІХ юзерах → unbounded memory, навіть якщо кожен per-conn cap (D12) тримає: N_conn × per-conn може перевищити памʼять, агрегатної стелі нема.
- *Доказ:* grep — нема `Count`-перевірки перед add (`:474`); per-conn D12 ≠ server-wide ceiling. Notification-channel — `Wait` (`:106`), head-of-line вже відзначено в Уточненні п.3.
- *Мітигація:* server-wide in-flight semaphore до signal-cli; shed `-32005` коли повний. Доповнює D12, не замінює.

**🟠 G7. own-number link + `--receive-mode=manual` → порожній/stale `listGroups` (звірено).**
- *Ризик:* Phase-1 task-7 ставить `UseManualReceiveMode=true` (send-only). Phase-2 own-number flow лінкує реальний номер юзера, потім пропонує `listGroups`. Свіжо-злінкований secondary device дізнається групи/контакти лише через sync по **receive**-каналу; з manual демон не тягне → `listGroups` щойно-злінкованого власного номера **порожній/stale** до ручного receive.
- *Доказ:* `SignalDevices.FinishLinkAsync` (`SignalDevices.cs:42`) → лінк ЗАВЕРШУЄТЬСЯ; gap — пост-лінк дані. Конфлікт task-7 (manual) ↔ Phase-2 own-number listGroups не зведений. **(Звірка-3 V5 🟠: формулювання «через provisioning-сокет» ХИБНЕ — у обгортці finishLink йде тим самим спільним stdin/stdout pipe (`InvokeMethodAsync`), окремого сокета нема; підпадає під ті ж 30с-таймаут + cancel-on-restart. Provisioning-канал внутрішній для upstream signal-cli.)**
- *Мітигація:* після own-number finishLink — bounded one-shot receive/sync, АБО документувати, що own-number group-listing вимагає receive. Звірити емпірично (та сама спайк-дисципліна, що D6).

**🟠 G8. PoP challenge — nonce single-use/binding/expiry не специфіковані.**
- *Ризик:* D7/D8 покривають PoP residual + PoP-pending стан, але механіка challenge не зафіксована. Без per-connection binding: заражений SW **релеїть** підпис легітимного девайса на другий attacker-сокет → PoP пройдено. Nonce має бути (a) one-time (consumed на першій verify), (b) bound до ЦЬОГО socket/conn-id, (c) короткий TTL, (d) CSPRNG ≥128-bit; підпис покриває `nonce‖connId`.
- *Доказ:* план каже «короткоживий challenge (nonce)» — не пінить store/one-time/binding.
- *Мітигація:* специфікувати nonce-store (in-memory, one-time, TTL, per-conn), підписувати `nonce‖connId`, reject reuse.

**🟠 G9. Shared-bot `listGroups` — cross-tenant privacy leak.**
- *Ризик:* будь-який автентиф. юзер, що викликає `listGroups` на **спільному** бот-акаунті, бачить УСІ групи бота → розкриває чужі group-membership/активність. Outbound-фільтр (`FilterReadOutputAsync`) — account-level, не скопить ВСЕРЕДИНІ спільного акаунта.
- *Доказ:* outbound-фільтр ріже по акаунтах; груп-рівень у межах спільного бота не моделюється.
- *Мітигація:* `listGroups` на спільному боті → admin-only, АБО explicit accept+док у `shared-bot.md` як частина «open relay» ризику.

**🟡 G10. Phase1→Phase2 міграція існуючого self-host акаунта.**
- *Ризик:* D4 покриває misconfig fail-closed, не **дані**. Phase-1 self-host уже має злінкований акаунт + signal-cli storage без identity/token-моделі. Апгрейд на Phase-2: хто володіє pre-existing акаунтом? Має призначитись bootstrap-admin, інакше orphan (недосяжний нікому, або — гірше, якщо filter default-open — всім).
- *Мітигація:* міграційний крок binds pre-existing акаунти до admin-identity на першому Phase-2 старті; тест «upgraded: legacy account видно лише admin».

**🟡 G11. persist-before-send throughput — single SQLite writer у hot send-path.**
- *Ризик:* «persist декремент ДО send» кладе durable DB-write (single-writer + WAL + `SQLITE_BUSY`-retry) на КОЖЕН `sendTextMessage`, серіалізовано server-wide. ~100/min/user × багато юзерів → DB-write стає send-bottleneck + контендить з token-store writes. Correctness fail-safe ок; throughput — ні.
- *Мітигація:* group-commit / coalesce budget-persist на короткому таймері (crash-safe under-count), бенчмарк у Phase-3 perf-gate. Звʼязано з G3 (lock) і SQLITE_BUSY-retry.

**🟡 G12. Invite per-code cap — атомарність інкременту + constant-time lookup.**
- *Ризик:* per-code attempt cap (task 7) — інкремент лічильника має бути атомарний (паралельні guesses), lookup constant-time (timing-oracle на ≥96-bit prefix). Малий ризик за 96-bit, але запінити.
- *Мітигація:* атомарний counter під lock/атомарний DB-UPDATE; порівняння коду constant-time.

**🟡 G13. `Retry-After`/`4429` контракт клієнта — одиниці + jitter.**
- *Ризик:* одиниці `Retry-After` (s vs ms) + обовʼязковий jitter не запінені → connect-on-demand клієнти sync-retry-ять разом → thundering herd.
- *Доказ:* частково в Phase-3 task-3 (docs), але числовий контракт не зафіксований.
- *Мітигація:* зафіксувати одиниці + вимагати jittered backoff у клієнтському контракті (Phase-3 task-3).

### Звірка-3 — корекції цитат + версійний блокер (третя ітерація аудиту, 2026-06-25)

Перевірено csproj-пін апки + framework-changelog + sibling-сорс трьома агентами. Ця ітерація **виправляє хибні/застарілі цитати** Звірки-1/2 і додає 4 нові прогалини. **V1 — keystone-блокер Фази 2: читати ПЕРШИМ.**

**🔴 V1. Версійний розрив — звірка йшла проти 2.7.0-сорсу, апка компілюється проти 1.1.0 (Phase-2 блокер).**
- *Ризик:* увесь auth/security/observability стек, що план «частково підключає» (`[RpcAuthorize]`, `AuthorizingJsonRpc`, `RpcAuthorizationEnforcer`, mTLS, settable `Principal`, `AbstractSecureJsonRpcSession`, `WsRpcServerDiagnostics`, `MaxConcurrentConnections`), **відсутній у пакеті, який апка реально тягне**. Кожна Phase-2-цитата вказує на код, якого в білді нема.
- *Доказ:* `SignalCliNet.WsRpcServer.csproj:13` → `PackageReference Include="JSON-RPC.NET" Version="1.1.0"`. Локальний sibling `JSON-RPC.NET/` (проти якого звіряли Звірку-1/2) = `Directory.Build.props:39` **2.7.0**. Framework `CLAUDE.md`: `1.1.0 = foundation-cluster-1` (лише build-hygiene); auth → `secure-transport-mtls` **2.6.0**; квота+діагностика → `observability-and-resilience` **2.7.0**. HEAD-merge: «secure-transport-mtls (2.6.0) + observability-and-resilience (2.7.0)». Тобто Звірка-1/2 факти **істинні для 2.7.0-сорсу**, але апка їх не компілює.
- *Мітигація:* **Phase-2 task-0 (передумова): bump `JSON-RPC.NET` 1.1.0 → 2.7.0.** Перетинає **breaking 2.0.0** (`subscription-manager-cleanup`): `ISubscriptionManager` → generic `<TEventType,TEventArgs>`, `account`→`topic`, `AddJsonRpcCore` 5→7 type-params. Композит-корінь апки (`AddSignalJsonRpc` override `SubscriptionManager`/`EventProcessor`) — саме точка зламу → міграція + регрес-тести ПЕРЕД будь-якою auth-роботою. Навіть «просто ввімкнути глобальну квоту» неможливе в 1.1.0.

**🟠 V2. Receive-mode default ІНВЕРТОВАНО — Phase-1 task-7 = no-op (виправляє Звірку-1 рядок 37 та task-7).**
- *Ризик:* план будує роботу на «демон default `--receive-mode=on-start` → eager-receive за всі акаунти; task-7 виставляє `UseManualReceiveMode=true`». Передумова зворотна.
- *Доказ:* `SignalCli.NET/.../SignalCliOptions.cs:56` → `UseManualReceiveMode = true` **за замовчуванням** → `--receive-mode=manual`. App `Program.cs:22-47` його **не чіпає** → вже manual. Send-only вже діє з коробки.
- *Мітигація:* task-7 викреслити як no-op (або звести до явного тесту, що default справді manual + до фіксації в докс). G7-caveat (own-number listGroups stale під manual) лишається — але з реального дефолту, не з task-7-зміни.

**🟠 V3. `MaxMessageSizeBytes` — апка ЙОГО енфорсить, не «no-op» (виправляє Звірку-1 msg-рядок).**
- *Ризик:* план каже «`MaxMessageSizeBytes` ніде не читається → задекларований захист = no-op». Це правда **лише для фреймворку**; grep плану не зачепив сесію апки.
- *Доказ:* `Sessions/SignalRpcSession.cs:221` апки читає `_config.MaxMessageSizeBytes` і закриває oversized-msg (`MessageTooBig`). Значення ніде не виставлене → **дефолт 100MB**.
- *Мітигація:* справжня прогалина не «no-op», а: (а) 100MB задорого для нотіф-payload (опустити до ~64KB у конфігу), (б) чек на **зібраному** повідомленні, не під час накопичення фреймів → frame-assembly-abort / slowloris-timeout / frame-count лишаються net-new (це план каже правильно).

**🟢 V4. D6 — handshake-seam ІСНУЄ в NetCoreServer (знижує severity D6 з 🔴).**
- *Уточнення:* D6 каже «у фреймворку нема хука handshake → спершу довести, що NetCoreServer це вміє; ризик fallback на first-message auth». NetCoreServer 8.0.7 **дає** `OnWsConnecting(HttpRequest, HttpResponse)` (валідація upgrade + субпротокол-negotiation) + `PerformServerUpgrade`. Seam існує, просто не юзається апкою.
- *Наслідок:* primary-дизайн (401-до-upgrade, Origin-чек, echo лише non-auth субпротоколу) **життєздатний** — override `OnWsConnecting` у session-сабкласі. D6-спайк лишається (емпірично підтвердити поведінку set-non-101-response + субпротокол-вибір), але це **підтвердження**, не ризик переробки на fallback.

**🟠 V5. G7/finishLink — у обгортці НЕМАЄ provisioning-сокета (виправляє механізм G7).**
- *Ризик:* G7 (і табл. рядок 9/11) пише «`SignalDevices.FinishLinkAsync` йде через provisioning-сокет (не receive-mode)». У коді обгортки такого нема.
- *Доказ:* `SignalCli.NET/.../SignalDevices.cs:42` → `InvokeMethodAsync("finishLink", ...)` тим самим спільним stdin/stdout JSON-RPC pipe, що й усі виклики. Окремий provisioning-сокет — внутрішній для upstream signal-cli, з обгортки невидимий. → finishLink підпадає під ті ж 30с-таймаут + cancel-on-restart (`OnStreamPairChanged`).
- *Мітигація:* висновок G7 (own-number group-listing stale під manual) може триматись на рівні signal-cli — але формулювання «через provisioning-сокет» прибрати; звіряти емпірично (план і так це каже).

**🟡 V6. permessage-deflate не підтримується → anti-zip-bomb робота зайва (виправляє task-5).**
- *Доказ:* NetCoreServer 8.0.7 — 0 hits на deflate/compress/permessage. WS-фрейми нестиснуті → compression-bomb-ампліфікація неможлива.
- *Мітигація:* task-5 пункт «permessage-deflate off/обмежено» звести до одноразової перевірки-факту (зроблено: не ввімкнено) + викреслити як активну роботу.

**🟡 V7. «3 файли незакоммічені» — stale (виправляє рядок 9 / task-1).**
- *Доказ:* `git log` → Groups-інтерфейс+адаптер закоммічені `eca3852 "Додано RPC-інтерфейс і адаптер груп"`, tracked, clean. Брудний лише сам plan-doc.
- *Мітигація:* task-1 = **лише** додати DI-рядок реєстрації (`AddScoped<ISignalGroupsRpc, SignalGroupsRpcAdapter>`); «закоммітити 3 файли» прибрати. Базовий `ISignalGroups` facade уже зареєстрований (Singleton) `AddSignalCli`.

**🔴 V8. Scoped-lifetime косметичний для ВСІХ адаптерів → per-connection principal НЕ можна тримати в адаптерах (нова, впливає на архітектуру Фази 2).**
- *Ризик:* план будує «per-connection principal»; якщо principal/per-user-стан інжектити в адаптери — він буде спільним між усіма конектами (мовчазний крос-тенант leak / confused-deputy).
- *Доказ:* singleton-реєстр резолвить адаптери з **root**-провайдера (`AbstractRpcServiceRegistry.cs:165`), `AddLocalRpcTarget` кешує інстанс на час target → `AddScoped` фіктивний для Accounts/Devices/Message теж (не лише Groups). Жоден адаптер не має per-connection ізоляції.
- *Мітигація:* principal **читати з call-context** (per-invocation), НЕ з ctor-injection адаптера. Chokepoint/IDOR-guard мусить жити в pre-dispatch фільтрі/сесії, не в shared-адаптері. Task-1 lifetime-фікс лишається (узгодити Groups), але глибший інваріант — адаптери stateless щодо principal.

**🟠 V9. Chokepoint вимагає правки sealed app-сесії + serializer-context, не лише config/DI (нова).**
- *Доказ:* `Sessions/SignalRpcSession.cs:98` хардкодить `new JsonRpc(_messageHandler)`. План (Chokepoint-розділ) каже конструювати `AuthorizingJsonRpc(handler, principal, policy)` → правка цього файлу. Нові token/identity-DTO → розширити `Serialization/SignalCliSerializerContext` (source-gen).
- *Мітигація:* у Phase-2 task-1 явно: «модифікувати `SignalRpcSession.OnWsConnected` (`new JsonRpc`→`AuthorizingJsonRpc`) + виставити `Principal` на сесії + DTO в serializer-context». Не чистий DI-change.

**🟠 V10. Catch-breadth типізованих exception вузький + іменування (уточнює Phase-3 task-6).**
- *Доказ:* `JsonRpcClient.cs:506-515` диспатч. Catch лише `JsonRpcException` пропускає `TimeoutException`/`OperationCanceledException`/`InvalidOperationException`("null response")/`ObjectDisposedException` — усі досяжні з `InvokeMethodAsync`. Тип captcha названо **`CaptchaRequiredException`** (enum-значення `CaptchaRejected`) — не `CaptchaRejected` як тип. `IdentityChangedException` — `[Obsolete]`/ніколи не диспатчиться (видалено в 5.0) → catch мертвий. `GroupAdminRequiredException` — через крихку евристику `-1 + substring "admin"`.
- *Мітигація:* Phase-3 fail-path catch-матриця: ловити по базовому `JsonRpcException` + явно non-RPC (`Timeout`/`OperationCanceled`/`ObjectDisposed`/`InvalidOperation`); ім'я типу `CaptchaRequiredException`; `IdentityChangedException` не ловити; не покладатись на substring-евристику admin.

**🟡 V11. Health-monitor рестарт = глобальний blast-radius (підсилює табл. рядок 1).**
- *Доказ:* `SignalCli.NET/.../SignalCliHealthMonitor.cs:131 ForceRestartAsync` + `JsonRpcClient.cs:156 OnStreamPairChanged` скасовує **ВСІ** pending по **всіх** акаунтах на кожен рестарт; restart-budget глобальний (`MaxRestartAttempts=3`/`RestartWindowSeconds=60`).
- *Мітигація:* нічого нового кодити — фіксувати в ops-докс (Phase-3): один health-fail рве in-flight усіх тенантів; рестарт-шторм одного акаунта вичерпує глобальний budget → деградація для всіх.

### Звірка-4 — multi-agent ревю плану проти коду (четверта ітерація, 2026-06-25)

Прохід 1-2 паралельними агентами: аудит усіх `file:line`-цитат, обсягу version-bump, app-side, SignalCli.NET-поверхні, deploy/CI, handshake-seam, внутрішніх суперечностей плану та адверсаріальний security-review. Цитати Звірки-1/2/3 **усі підтверджені** (дрібний drift: `SignalDevices.cs:42` = декларація, реальний `InvokeMethodAsync("finishLink")` `:51-57`; `RpcAuthorizationEnforcer.cs:43` = коментар, тіло `:34-60`; `SignalCliOptionsExtensions.cs:46-47` — там `jsonRpc`+receive-mode, account-арга в launch немає взагалі). Нижче — лише **нові** знахідки. **W13, W20 — keystone: читати першими.** ⚠ W1 та W12 (спершу 🔴) **спростовані** адверсаріальним Проходом-3 (репро + код); W18 знижено 🔴→🟠 — корекції inline нижче (деталі у Звірці-5).

**🟡 W1 (СПРОСТОВАНО Проходом-3, було 🔴 — механізм хибний, висновок колапсує у W4).**
- *Спростування (емпірично відтворено):* `Version="1.1.0"` проти джерела, де є **лише 2.7.0**, NuGet трактує як floor → резолвить 2.7.0 з **NU1603 (warning), exit 0**; restore НЕ падає, а csproj апки не має `TreatWarningsAsErrors` → білд теж не зупиняється на NU1603. Тобто «restore падає → блокує Фазу 1» — неправда; «інверсії діаграми порядку» немає (знімає й N8).
- *Залишок:* офлайн local-feed **тихо up-resolve-ить** 1.1.0→2.7.0, тож апка компілюється проти 2.7.0 і впирається у breaking-compile — а це **вже W4** (template-method-інверсія). Приватний feed із реальним 1.1.0 — не зачеплений. Фаза 1 офлайн зламана НЕ на restore, а на compile → колапсує у W4.
- *Мітигація:* у рамках W4 — пінити local-feed на справжній 1.1.0 (checkout тегу) АБО свідомо bump'ати; на час Фази 1 підняти `NU1603`→error, щоб мовчазний up-resolve не скомпілював проти не того пакета.

**🟠 W2. CI-ordering gap: DoD кожної фази вимагає «CI зелений», CI створюється лише Phase-3.**
- *Доказ:* `.github/workflows/` в апці **немає** (лише `copilot-instructions.md`); Phase-1 DoD (§304) і глобальний DoD (§260) вимагають «CI зелений», але CI = Phase-3 task-7. Plus «test»-нога CI порожня до Phase-1 task-4 (нема `tests/`).
- *Мітигація:* bootstrap CI у Phase-1 поряд із task-4, **або** переформулювати Phase-1/2 DoD на «тести проходять локально» до Phase-3. Siblings мають готові workflow для копії; стек: **xUnit 2.9.3 + Moq 4.20.72 + Microsoft.NET.Test.Sdk 18.7.0** (обидва sibling-репо ідентичні).

**🟡 W3. task-2 переоцінено / task-5 недооцінено.**
- *Доказ:* `deploy/docker-compose.yml` **уже існує** (33 рядки) → task-2 = верифікація, не авторинг; compose вішає package-feed `Dockerfile` (потребує `GITHUB_TOKEN`), не `Dockerfile.from-source`. Clean docker build потребує мережі до **3 хостів** (nuget.org, github/AsamK signal-cli, github/adoptium JDK 25) → не air-gappable; payload кілька сотень MB.
- *Мітигація:* task-2 звести до перевірки; task-5 — задокументувати мережеві передумови або pre-bake базовий образ.

**🔴 W4. task-0 недооцінює міграцію: реальний злам — інверсія template-method, не «AddJsonRpcCore 5→7» і не «DI-рядок».**
- *Доказ:* апка викликає **config-only** `AddJsonRpcCore(configureOptions)` (`SignalRpcExtensions.cs:26`), а не generic-overload → «5→7 type-params» до неї не стосується. У 2.0.0 `AbstractSubscriptionManager` зробив `Subscribe/Unsubscribe/UpdateSubscription` **non-virtual** template-wrapper'ами (база володіє `OperationLock`); override-клас апки (`SubscriptionManager.cs:30,130,196`) з `public override … (string account, object eventTypes)` + ручним локом **не скомпілюється**. Каскад: `GetClientsForEvent` `object`→typed (`:191-193`, прибрати касти); `EventProcessor._subscriptionManager` стає generic (`:18,75`); DI-рядок `:29` → `ISubscriptionManager<SignalEventTypes, BaseSignalEventArgs>` — **пастка: порядок type-арг протилежний** до `AbstractSubscriptionStore<TSub,TEventArgs,TEventType>`. `SubscriptionStore`/`RpcServiceRegistry`/`SignalRpcServer`/`SignalRpcSession` ctor-сумісні — переживають bump.
- *Мітигація:* task-0 розписати як **переписування override-класу `SubscriptionManager` на `*Core`-патерн** (викинути ручний лок, `object`→typed) + 3 каскадні правки, не «DI + arity». Це основний обсяг task-0.

**🟠 W5. Модель «нема DI-рядка = вимкнено» неточна (уточнює V7).**
- *Доказ:* (а) звичайні адаптери — reflection-discovery знаходить інтерфейс, але `ServiceProvider.GetService` повертає null → тихо пропускає (`AbstractRpcServiceRegistry.cs:166-172`), тож DI-рядок Groups **необхідний** (план доходить правильної дії, але механізм інший). (б) `ISignalEventsRpc` — **client-aware**, конструюється через `ActivatorUtilities` (`:187`) **без** DI-рядка; адаптер подій існує, незареєстрований, але discovery міг би його підняти сам — асиметрія, яку план не згадує.
- *Мітигація:* у task-1 зафіксувати обидва механізми; вирішити долю незареєстрованого `SignalEventsRpcAdapter` (Phase-3 receive-шлях).

**🟠 W6. `SignalMessageRpcAdapter.cs:25` логує `account` (= номер) на Information → порушення privacy-rule #4.**
- *Доказ:* `_logger.Log…("send text message from account {Account}", account)` — account id для бота/власного номера = E.164. CLAUDE-rule #4: не логувати номери, лише method/status/id.
- *Мітигація:* прибрати account із Information-шаблону (або хешувати/обрізати); звірити решту адаптерів. Phase-1 quick-fix.

**🟠 W7. `MaxMessageSizeBytes`-чек спрацьовує ПІСЛЯ повного буферингу (поглиблює V3).**
- *Доказ:* `SignalRpcSession.cs:221` читає вже **зібране** `size` в `OnWsReceived` — NetCoreServer забуферив повне повідомлення в пам'ять до виклику хендлера. Зниження конфігу 100MB→64KB **не** обмежує піковий memory під час накопичення фреймів (це робить власний max-frame-buffer NetCoreServer).
- *Мітигація:* справжній slowloris/big-msg захист — на рівні NetCoreServer WS-buffer / frame-count / assembly-timeout (net-new, як план і каже), не лише конфіг-значення.

**🟠 W8. Поверхня ізоляції ширша за send/listGroups — `AssertAccountAllowed` потрібен на КОЖНОМУ account-bearing методі.**
- *Доказ:* `ISignalMessage.SendTextMessageAsync` → `UnifiedSendRequest(Account: options.Account…)` (`SignalMessage.cs:221`) лише з `ArgumentNullException`/`ValidateRecipients`, **без** ownership-чека; той самий патерн у device-mgmt, payments, accounts-lifecycle. Wrapper дає нуль defense-in-depth на **жодному**.
- *Мітигація:* chokepoint IDOR-guard застосувати до повного account-bearing набору, не лише до 5 MVP-методів; build-guard має покривати кожен такий метод.

**🟠 W9. 30с глобальний RPC-таймаут зв'язує `finishLink` (тісніший за link-TTL 120с, окремо від D16).**
- *Доказ:* `SignalCliOptions.RequestTimeoutSeconds=30` (`:80`) — один глобальний таймаут на ВСІ методи, вкл. `finishLink`. Для own-number ручного QR-скану 30с може не вистачити; link-**сесія** живе 120с, але `finishLink`-**виклик** помирає на 30с. App не override-ить.
- *Мітигація:* для own-number flow підняти `RequestTimeoutSeconds` (або per-call timeout) до ≥ link-TTL; звірити з D16 (UX ручного скану).

**🟡 W10. Restart-cancel віддає голий `OperationCanceledException`, нерозрізнюваний від client-cancel (ускладнює V10 fail-path).**
- *Доказ:* `OnStreamPairChanged` (`JsonRpcClient.cs:162-167`) скасовує in-flight через transient CTS → catch-сайт не відрізнить «втрачено через health-restart, ретраїти» від «клієнт відмінив, не ретраїти».
- *Мітигація:* у Phase-3 fail-path обернути/мапити restart-cancel в окремий тип (або корелювати з process-state), щоб нотіф не губилась мовчки.

**🟠 W11. NetCoreServer 8.0.7 `OnWsConnecting` НЕ авто-echo-ить субпротокол і НЕ читає Origin (поглиблює V4).**
- *Доказ:* `OnWsConnecting(HttpRequest,HttpResponse)` (`public virtual`, доступний на **plaintext** `AbstractJsonRpcSession`) дає reject (`return false`/non-101) — seam існує. Але handshake **не читає** `Sec-WebSocket-Protocol`/`Origin` і **echo-ить нічого**. Наслідки: (а) Origin-allowlist — ручний парс request-заголовків; (б) echo non-auth субпротоколу — **обов'язкова ручна `SetHeader`**, бо інакше RFC 6455 §4.2.2 клієнт може обірвати (present-but-unechoed); (в) leak токена повертається ЛИШЕ при наївному копіюванні всього client-list. Seam **не залежить** від bump 1.1.0→2.7.0 (NetCoreServer-рівень) → handshake-reject можна робити незалежно; токен→principal у authorization потребує bump.
- *Мітигація:* у task-4 явно: override `OnWsConnecting` на `SignalRpcSession`, ручний Origin-чек + явний `SetHeader("Sec-WebSocket-Protocol", <non-auth>)`; ніколи не копіювати весь client-list. Спайк лишається (емпірично проти restored DLL 8.0.7 — git-тегу нема, лише NuGet).

#### Внутрішні суперечності плану (reasoning-прохід — пари, які план не зводить)

**❌ W12 (СПРОСТОВАНО Проходом-3 — було 🔴, хибна).**
- *Спростування:* `-4/-5/-6` (UntrustedIdentity/RateLimit/CaptchaRejected) приходять як **error у JSON-RPC ВІДПОВІДІ на send-виклик** (signal-cli шле всі помилки на stdout тим самим каналом, що й success-відповіді), і мапляться синхронно на return-path `InvokeMethodAsync` (`JsonRpcClient.cs:506-515`). У `SignalEventService` **немає** event-kind для rate-limit/captcha/proof (grep порожній). Send-only MVP **бачить** ці сигнали; вимкнений receive НЕ засліплює anti-ban. Передумова «Signal сигналить throttle асинхронно через receive» — без опори в коді.
- *Висновок:* anti-ban реактивний детект сумісний із send-only; D2/Phase-3 task-6 коректні як є.

**🔴 W13. G3 ⊥ G11 — взаємовиключні, план перелічує обидва як сумісні.**
- *Доказ:* G3 вимагає atomic-locked-**durable** декремент бюджету ПЕРЕД кожним send (correctness); G11 визнає цей single-SQLite-writer bottleneck'ом і пропонує coalesce-на-таймері. Але coalesce **ламає** «persist ДО send» fail-safe: send комітиться в Signal до durable-декременту → crash-window over-send → бан (саме те, що Уточнення-6 забороняє).
- *Мітигація:* **вибрати один режим**. Або строгий atomic-durable (прийняти throughput-стелю, можливо micro-batch під тим самим локом), або coalesce + явно прийняти crash-window over-send-ризик (несумісно з анти-баном). Не можна мати обидва.

**🟠 W14. Connect-on-demand vs PoP-per-connect — бюджет латентності не зведений.**
- *Доказ:* Уточнення-1 (1 нотіф = `connect→auth→send→close`) + PoP «на КОЖЕН connect» + lookup «на КОЖЕН connect». План виправдовує «дешево» лише для DB-lookup, мовчки успадковує для важчого **PoP round-trip** (server→client→server) на кожну нотіф.
- *Мітигація:* зважити PoP-вартість на ефемерному шляху; можливо short-lived post-PoP session-resumption для серій нотіфів, або прийняти латентність явно.

**🟠 W15. G1 lockfile активно ПОГІРШУЄ failover.**
- *Доказ:* rolling-restart overlap — саме те, що `O_EXCL` lockfile відмовляє → новий інстанс не стартує доки старий повністю не звільнить → гарантований availability-gap на кожен деплой. G1 (correctness) і availability-ціль у прямій тензії.
- *Мітигація:* задокументувати deploy = свідомий downtime-window (stop-old→start-new, не overlap); зважити graceful-handoff lockfile (короткий TTL/takeover-протокол), якщо downtime неприйнятний.

**🟠 W16. Три+ rate-limit-шари ніколи не зведені в одне admission-control правило.**
- *Доказ:* агрегатний бот-бюджет + per-user квота + new-recipient лічильник (D2) + Phase-3 global-pause — чотири незалежні gate, кожен відмовляє окремо. G2 зводить лише пару aggregate↔per-user; D2-counter не вфолдований.
- *Мітигація:* одна явна admission-функція (порядок перевірок, який gate що повертає, як вони компонуються) — щоб не голодоморили одне одного.

**🟠 W17. Важкий auth-стек непропорційний прийнятому ризику «бот = відкрите реле».**
- *Доказ:* головний abuse-вектор нотіф-реле — спам/харасмент на довільного E.164 через спільний бот — **прийнятий як ризик** (нема доказу володіння отримувачем). 11 hardening-задач захищають лише від неавтентиф. абʼюзу й крадіжки токена; PoP (D7) не спиняє malicious-extension. Інвестиція vs залишковий ризик не зведені.
- *Мітигація:* або підсилити recipient-сторону (opt-in/allowlist отримувачів, verified-recipient), або явно прийняти й задокументувати, що auth-стек = захист акаунт-ізоляції (own-number), НЕ recipient-абʼюзу; не продавати його як останнє.

#### Адверсаріальний security-review (нові діри поза D/G/V)

**🟠 W18 (знижено Проходом-3 з 🔴). HMAC pre-image не канонізований.**
- *Ризик:* не зафіксовано ЯКИЙ рядок HMAC-иться (raw-wire / base64url-decoded / prefix+checksum-stripped). Mismatch issue↔lookup → масовий fail-closed або колізії. Знижено: збій — детермінований impl-баг (перший lookup падає в інтеграц-тесті), не латентна діра; «HMAC(token,pepper)» найприродніше читається як «весь рядок токена».
- *Мітигація:* одна канонічна форма (тільки decoded random payload, без prefix/checksum) на обох сайтах + round-trip тест-пін.

**🟠 W19. «Constant-time compare» обіцянка хибна для stored-lookup моделі.**
- *Ризик:* lookup = DB-індекс по `HMAC(token,pepper)` (B-tree short-circuit), не constant-time. При 256-bit pre-image timing — не-загроза, але обіцянка вводить implementer'а в оману.
- *Мітигація:* прибрати «constant-time» обіцянку (визнати, що index-lookup і Є compare; 256-bit робить timing безпечним), АБО constant-time порівняння повного `v1$hash` після fetch по coarse-prefix.

**🔴 W20. `redeemInvite` без атомарного consume (G12 покриває лише counter, не consume-and-bind).**
- *Ризик:* `finishLink` має atomic one-time-гашення; `redeemInvite` (token-LESS TOFU-корінь enrollment'у device-pubkey, D1) — лише rate-limited. Паралельні redeem одного коду → 2 attacker-device з 1 інвайта, або race проти легітимного юзера за прив'язку ключа.
- *Мітигація:* `UPDATE…SET consumed=1 WHERE code=? AND consumed=0` + rows-affected перевірка **перед** реєстрацією pubkey (той самий патерн, що `finishLink`).

**🟠 W21. Rotation залишає вікно з 2 валідними токенами.**
- *Ризик:* cascade-revoke специфіковано лише для **revoke**; рутинна rotation = «видати новий opaque» без atomic-set `IsRevoked` на старому → вкрадений-але-ротований токен живе до незалежного 12h TTL.
- *Мітигація:* rotation atomically `IsRevoked=1` на попередньому в тій самій транзакції, що інсертить наступний; zero-overlap як інваріант + тест.

**🟠 W22. `FilterReadOutputAsync` fail-open на винятку.**
- *Ризик:* default-deny заявлений для dispatch, не для output-filter. Виняток у фільтрі (malformed principal, null `linkedAccounts`) → якщо обгорнуто слабко, повертає **нефільтрований** response = крос-тенант leak усіх акаунтів/груп.
- *Мітигація:* фільтр fail-closed — будь-який виняток → empty/error, ніколи raw; fault-injection тест.

**🟠 W23. Build-guard — лише unit-test, не dispatch-time інваріант.**
- *Ризик:* `[Fact(Skip)]` / `--filter` / local `dotnet build` без `dotnet test` / disabled-CI-step → un-policied метод шипиться відкритим. Опція (б) плану (атрибут+guard) **не** runtime-fail-closed; забутий атрибут ловить лише тест.
- *Мітигація:* мандат опції (а) — pre-dispatch фільтр відмовляє будь-який метод поза явним реєстром політик на **dispatch-time**; build-guard = belt-and-suspenders, не єдиний gate.

**🟠 W24. Abuse-log = детермінований equality-oracle БЕЗ leak.**
- *Ризик:* `HMAC(pepper_abuse, account‖recipient)` детермінований → однакова пара дає однаковий digest. На **спільному боті** (account константа) будь-хто з read-доступом до abuse-log (ops/backup/майбутній metrics-export D13) лінкує частоту/гарячих отримувачів через усіх юзерів — без реверсу pepper. План визнає лише reverse-on-pepper-leak.
- *Мітигація:* per-record salt (зберігати) або включити `callerIdentityId` у pre-image; задокументувати residual equality-oracle у `shared-bot.md`.

**🟠 W25. Крос-тенант через внутрішній стан спільного signal-cli (окремо від G9).**
- *Ризик:* один daemon / один `--config`-том, signal-cli НЕ ізолює акаунти. Лінкований own-number юзера зберігає contacts/groups/profile-key у спільному сторі; daemon-внутрішній cross-account sync/contact-merge/profile-key-sharing може сурфейснути дані A через запит, легітимно авторизований для іншого акаунта. App-фільтр (account-level) структурно не дістає **всередину** daemon. G9 — shared-bot `listGroups`; це **own-number** leak через daemon-стан.
- *Мітигація:* per-account storage-ізоляція (окремий `--config` на own-number, або daemon-per-tenant), АБО емпіричний аудит + задокументований accepted-risk; app-фільтрування = necessary-but-insufficient.

### Звірка-5 — Прохід 3: doc-консистентність + клієнт-контракт + адверсаріальна верифікація (2026-06-25)

Прохід 3 — три агенти: (а) **адверсаріальна верифікація** keystone-знахідок Звірки-4 — результат застосовано inline вище: **W1/W12 спростовано** (NU1603 floor-resolution / send-response error-path), **W18 знижено** 🔴→🟠; **W13, W20 вистояли 🔴**, **W22 — 🟠**; (б) консистентність DoD/docs/strategy; (в) feasibility клієнтського контракту MV3/WebCrypto/WebSocket. Усі IDs чисті (D1-D17, G1-G13, V1-V11, W1-W25 — без dangling/duplicate). N8 (ordering-діаграма ⊥ W1) **знято** разом зі спростуванням W1.

#### N-серія — консистентність плану vs репозиторій

**🟠 N6 (знижено Проходом-4 з 🔴 — факт вірний, «уся стратегія непрацездатна» переоцінено). Delegation-стратегія маршрутизує задачі у `cavecrew-*` агентів, яких у середовищі немає.**
- *Доказ:* таблиця делегування + частина task-`→` називають `cavecrew-investigator/builder/reviewer`; доступні тут `claude`/`Explore`/`general-purpose`/`Plan`/`claude-code-guide`/`statusline-setup`. **Знижено:** `Explore` і `general-purpose` **уже є** в таблиці першокласними рядками → лише investigator/builder/reviewer-рядки + їх `→` і `cavecrew-reviewer`-DoD-гейти треба перемапити; технічна робота не блокується, фікс механічний.
- *Мітигація:* перемапити (Explore — пошук/мапа; general-purpose — фіча+тести; ревю — інлайн або general-purpose), або зробити імена середовище-агностичними.

**🟠 N7. README досі «JDK 21+» (3 місця) попри branch-правку README.**
- *Доказ (перевірено):* `README.md:54,66,109` = «JDK 21+»/«JDK 21»; має бути **JDK 25** (signal-cli 0.14.3, class-file 69). CLAUDE-rule #2 це прямо називає; branch виправив `.NET`/`07artem132`, але JDK лишив.
- *Мітигація:* виправити 3 рядки на JDK 25 при наступному дотику README (поза скоупом plan-доку, але блокер чистоти rule #2).

**🟠 N3. Phase-2 DoD не має чекбоксу для G11; G11 — unowned жодним DoD.**
- *Доказ:* Phase-2 DoD має G1-G10/G12/V1/V8/V9, але **G11** (single-SQLite-writer throughput у hot send-path) не верифікується ніде; G13 хоч згаданий у Phase-3 task-3 prose.
- *Мітигація:* додати perf-DoD на G11 (Phase-3 perf-gate) або явно позначити як accepted-perf-risk.

**🟠 N4. Internal-contradiction-знахідки (W13-W17) без task/DoD-власника.**
- *Доказ:* W13 каже «G3 ⊥ G11 — вибрати один режим», але план досі тримає обидва як живі мітигації (task-5 / §G11), жодна задача не робить вибір, жоден DoD не перевіряє. Те саме W14/W16/W17 (PoP-latency-бюджет, єдине admission-rule, пропорційність auth-стеку).
- *Мітигація:* для кожної W13-W17 — або явна задача-рішення перед Фазою 2, або свідоме accepted-decision у docs.

**🟡 N1/N2. `docs/architecture.md` малює target-state як as-built.**
- *Доказ:* `architecture.md:48-50` показує token/identity-стори + AUTHN/AUTHZ/RL-вузли, яких у `src/` НЕМАЄ (Phase-2 target); перелічує 4 адаптери, пропускає наявний `SignalEventsRpcAdapter` (W5). Версії в architecture.md коректні (SignalCli.NET 4.10.0, JDK 25; JSON-RPC.NET-версію не згадує → без 1.1/2.7-конфлікту).
- *Мітигація:* caption «target state (Phase 2)» на auth-вузлах; додати events-адаптер або позначити Phase-3.

**🟡 N5. Phase-3 DoD не верифікує V10/W10** (catch-matrix breadth + restart-cancel disambiguation); Phase-1 task-3 (health-endpoint) — без DoD-чекбоксу про наявність/відповідь.

#### C-серія — клієнтський контракт MV3 / WebCrypto / WebSocket (платформні факти, які сервер-дизайн припускає неправильно)

**🔴 C1. Non-extractable device-key НЕ може жити в `chrome.storage.local`; лише IndexedDB — план конфляує його з токеном.**
- *Доказ:* `chrome.storage.local` серіалізує значення (не зберігає `CryptoKey`); non-extractable `CryptoKey` переживає термінацію ефемерного SW **лише в IndexedDB** (structured-clone тримає handle, матеріал у key-store). Storage-рядок плану коректний для **токена**, але структурно неможливий для **device-key**; «ключ просто персиститься» шляху немає.
- *Наслідок:* якщо implementer кладе device-key у storage.local → персист падає → клієнт регенерує ключ на кожен cold-start → кожна нотіф падає PoP → сервер «lookup+PoP на кожен connect» відхиляє **легітимного** клієнта. Підриває PoP-корінь довіри (D1/D7/D8/G8) усієї Phase-2.
- *Мітигація:* контракт мусить мандатувати «device-key у IndexedDB, enrolled pubkey стабільний через рестарти SW»; D1-enrollment визначає key-rotation-on-loss (втрачений IndexedDB = форс re-invite, не тихий re-enroll).

**🟠 C2. MV3 SW: 5-хв hard-cap + kill між sign і send → at-most-once; сервер припускає недосяжну клієнтську надійність.**
- *Доказ:* свіжо-розбуджений SW killable між `sign` і `send` (якщо event-handler повертається до резолву WS-промісу); понад 5 хв на один task — безумовна термінація. WS-активність скидає 30с-idle (Chrome 116+), але hard-cap і mid-handshake-kill лишаються.
- *Наслідок:* «persist декремент ДО send» fail-safe для **сервера** (краш→недосил), але клієнт має дзеркальну проблему: kill між «accepted/budget decremented» і клієнтським ack → **нотіф тихо губиться** без client-retry-гарантії. Сервер припускає надійного клієнта; MV3 дає at-most-once.
- *Мітигація:* контракт (Phase-3 task-3) — client-side idempotency: notification-queue у storage перед connect + сервер експонує idempotency-key/`messageId`, щоб re-woken SW дедуплікував ретрай, який не може відрізнити від доставленого.

**🟠 C3. Реальний `Origin` MV3-SW = `chrome-extension://<id>` (НЕ `null`), id публічний → allowlist майже нічого не дає.**
- *Доказ:* WS із extension-SW шле `Origin: chrome-extension://<extension-id>` (це довірена extension-realm, не opaque/`null`). `null` — для sandboxed-контекстів, не extension-SW. Id публічний (Web Store / unpacked manifest), нема per-install-секрету в Origin.
- *Наслідок:* allowlist на `chrome-extension://<id>` не зупиняє нічого понад token+PoP; прийняття «or null» РОЗШИРИЛО б allowlist на кожен opaque-origin. NetCoreServer не читає Origin авто (W11) → ручний-парс-кост за майже-нульову цінність.
- *Мітигація:* пінити allowlist на **конкретний** `chrome-extension://<id>`, прибрати гілку «or null», не продавати Origin як значущий defense-in-depth — спиратись на token+PoP.

**🟠 C4. Браузер ОБРИВАЄ конект, якщо сервер не echo-ить offered субпротокол → дефолт NetCoreServer (echo-ить нічого) робить реального клієнта неконектабельним.**
- *Доказ:* RFC 6455 сам по собі **дозволяє** серверу не вибрати жодного (§4.1 fail лише на НЕ-offered). Але керівний для браузера — **WHATWG WebSockets Standard** (не сирий RFC): якщо клієнт подав непорожній список протоколів, а 101 не має співпадаючого `Sec-WebSocket-Protocol`, алгоритм fail-ить з'єднання → Chrome шле `error`/`close`, не `open`. NetCoreServer-дефолт (W11) echo-ить нічого → реальний браузерний `WebSocket([token-proto, real-proto])` **не досягає `open`**.
- *Наслідок:* явний `SetHeader("Sec-WebSocket-Protocol", <real-non-auth>)` у `OnWsConnecting` — **connectivity-блокер**, не лише hardening. Поточний DoD (echo-leak) тестує лише negative і пропустив би сервер, що взагалі не конектиться.
- *Мітигація:* у Phase-2 task-4 трактувати echo-real-subprotocol як обов'язкову connectivity-логіку; додати позитивний DoD «браузерний WS досягає `open` (сервер echo-нув real-субпротокол)»; ніколи не копіювати весь client-list (інакше токен у response).

**🟠 C5. Phase-3 persistent-WS: `chrome.alarms` min-period 30с ≥ SW-idle-вікно → не тримає сокет, лише переустановлює (reconnect-шторм). Heartbeat мусить гнати СЕРВЕР.**
- *Доказ:* `chrome.alarms` floor = 30с (< відхиляється); SW-idle-kill теж ~30с; alarm будить **свіжий** SW без відкритого сокета — persistent-WS помер із попереднім інстансом. WS-frame-активність тримає SW (Chrome 116+) → надійніший keep-alive = сервер шле app-heartbeat <~25с.
- *Наслідок:* receive-дизайн Phase-3 не може спиратись на client-`chrome.alarms` (вони лише re-establish → саме той reconnect-шторм, якого план уникає).
- *Мітигація:* контракт reconnect/backoff (Phase-3 task-3) — server-originated heartbeat-interval (<~25с, app-level JSON-RPC notification, не лише WS-ping), плюс jittered client-reconnect (G13).

**🟡 C6. Chrome push wake = best-effort/throttled/coalesced → wake→connect латентність необмежена; client-coalescing ⊥ server-budget.**
- *Доказ:* push (FCM) будить SW із `waitUntil`, але під тими ж 30с/5хв-капами (C2); доставка coalesce-абельна/drop-абельна під квотою; часті wake без UI → Chrome throttle. Push-payload ~4KB cap.
- *Наслідок:* сервер не може припускати, що нотіфи прибувають із тією ж частотою, що емітяться події; client-coalescing (Уточнення-4) і server-budget (D2/G2) — два незалежні лімітери, не зведені; сервер бачить lossy/reordered/bursty subset.
- *Мітигація:* idempotency/dedup (C2) + budget-облік мають це толерувати; одне-рядкова нотатка у Phase-3 task-3 про необмежену throttle-абельну wake→connect-латентність.

### Звірка-6 — Прохід 4: аудит плану проти ВЛАСНОЇ імплементації апки (2026-06-25)

Прохід 4: (а) адверсаріальна верифікація Проходу-3 — **C1 вистояла 🔴, C3/C4 🟠** (цитату C4 виправлено на WHATWG-шар), **N6 знижено 🔴→🟠**; (б) completeness-critic. Спільний корінь A-серії: Звірки-1…5 аудитували *дизайн плану проти upstream-коду*, але майже ніколи — *проти власної наявної імплементації апки* як субстрату, на який Phase-2/3 прищеплюється. Цей субстрат місцями ворожий плану. A1/A2 підтверджено мною в коді (cite-and-read).

**🔴 A1. «Реєструй кожен DTO або runtime-`NotSupportedException`» — для ЦІЄЇ апки ХИБНЕ; реальний ризик інвертований (тихий rot під reflection-fallback).**
- *Доказ:* `SignalRpcSession.cs:79-82` — `JsonTypeInfoResolver.Combine(SignalCliSerializerContext.Default, new DefaultJsonTypeInfoResolver())`. Reflection-fallback резолвить **будь-який** незареєстрований тип → новий `RedeemInviteRequest`/`TokenRecord` серіалізується без помилки; обіцяного V9/conventions.md tripwire (`NotSupportedException`) **не існує**. (Це прямо спростовує `.claude/rules/conventions.md` «unregistered type fails at runtime under source-gen».)
- *Наслідок:* інструкція V9 «додати DTO у context» і **непотрібна** для коректності (reflection покриває), і **неенфорсена** (апка не має regression-guard machinery, per app CLAUDE.md) → source-gen context гниє; якщо колись `PublishAot`/`PublishTrimmed`/`JsonSerializerIsReflectionEnabledByDefault=false` — усі незареєстровані DTO ламаються разом, без тесту що ловить. Нині дрімає (AOT/trim-флагів нема), але план додає цілу DTO-родину без дисципліни.
- *Мітигація:* або визнати reflection-fallback як свідомий (і прибрати «реєструй кожен» з плану/conventions), або додати app-level regression-guard (тест, що енумерує серіалізовані типи) — інакше інваріант декоративний.

**🔴 A2. Апка ШИП'ИТЬ phone-number/param-firehose СЬОГОДНІ; privacy-контракт структурно неенфорсений (W6 — лише вершина).**
- *Доказ:* `SignalRpcSession.cs:104-109` — `new TraceSource("StreamJsonRpc"){ Switch.Level = Information }` + `ConsoleTraceListener` на `JsonRpc.TraceSource`. StreamJsonRpc на Information трейсить **повні тіла RPC, включно з params** → це **точно той третій token-leak-вектор**, який план відкладає у Phase-3 task-2, вже ввімкнений безумовно: кожен `sendTextMessage`/(майбутній)`authenticate` param у консоль = `docker logs`. Plus 5 account(=E.164)-сайтів: `SignalMessageRpcAdapter.cs:25` (Information, W6), `Events/EventProcessor.cs:71` (Debug), `Subscriptions/SubscriptionManager.cs:57-58,70-71,85` (×3), `Subscriptions/SubscriptionStore.cs:110` (account у RPC-відповідь list-subscriptions, не лише лог). Нема LoggerMessage-дисципліни, нема privacy-тесту.
- *Наслідок:* щойно Phase-2 пустить auth-params, токени потечуть у `docker logs` через цей trace-listener — план пише redact-allowlist (Phase-3) так, ніби логування стартує чисте; воно ні, і найгірший порушник — trace-listener, не адаптери.
- *Мітигація:* Phase-1 quick-fix — прибрати безумовний `ConsoleTraceListener` (або гейтнути Debug-білдом/конфігом + redact), вичистити account-сайти; зафіксувати, що privacy-контракт без guard-тесту неенфорсений у цій апці.

**🟠 A3. Config/options-валідація: апка не вмикає фреймворковий fail-fast; D4 «fail-closed startup» не має субстрату.**
- *Доказ:* `SignalRpcExtensions.cs:21-26` кличе `AddJsonRpcCore(configureOptions)` (1.1.0 config-only) без `ValidateOnStart`/`JsonRpcServerConfigValidator` (той `[OptionsValidator]` прийшов у 1.3.0 — у пінненому 1.1.0 його може й не бути). `Program.cs:51-61` біндить `Server:Host/Port` руками (`serverSection["Host"]` + `int.TryParse` + тихий fallback), без `[Range]`/`[Required]`/fail-fast; `appsettings.json` шипить `Host:"0.0.0.0"` — небезпечний дефолт D4 **живий у закоммічченому конфізі**. Нова Phase-2-конфіга (pepper-шляхи, rate-limit, TLS-cert, auth-on/off, LUKS-ключ) — **нуль валідації**, а це саме knobs де misconfig = open relay.
- *Мітигація:* task-0 приносить валідатор — у план явно «wire `ValidateOnStart` + `[OptionsValidator]` для нової auth-конфіги»; без цього D4 нема на чому тримати.

**🟠 A4. Disposal/lifecycle наявної сесії вже крихкий — субстрат для per-connection auth-стану недетермінований.**
- *Доказ:* `SignalRpcSession.cs:148` `OnWsDisconnected` — `public override async void` (unobserved-exception hazard); `:187` `await Task.Run(() => JsonRpc.Dispose())` (offload sync-dispose у thread-pool); `:236` `_ = _messageHandler.ProcessReceivedDataAsync(...)` + `:256` `_ = DisconnectAsync()` — **три незалежні fire-and-forget** без узгодженого порядку. Фреймворкове critical-rule #3 (cancel→drain→dispose) — інваріант *фреймворку*, але апка **субкласиться** і override-ить саме ці методи → може локально його перемогти.
- *Наслідок:* Phase-2 примітиви (Principal-lifetime, PoP-pending gate D8/G8, auth-timeout, revoke-tears-down-this-socket D9) живуть у `SignalRpcSession`; ліпити детерміновану auth-стан-машину на сесію, де `_cts.Cancel()` гониться з `Task.Run(Dispose)` і двома fire-and-forget — реальний інтеграційний ризик, не зважений.
- *Мітигація:* у Phase-2 перед auth-стан-машиною — впорядкувати teardown сесії (await-able dispose-chain, без `async void`/fire-and-forget на критичному шляху).

**🟠 A5. RPC-поверхня не версіонована — Phase-2 = flag-day breaking для незалежно-розгорнутого розширення.**
- *Доказ:* `sendTextMessage(account,recipients,message)` (`ISignalMessageRpc`, adapter `:19-24`) сьогодні викликається будь-яким клієнтом без auth. Phase-2 додає обов'язковий subprotocol-токен на upgrade (плейнтекст-клієнт ламається на handshake) + PoP (непідписані ламаються) + principal-фільтр на `listAccounts`/`listGroups` (та сама назва, **інший result-set** — тихий behavioral-break) + default-deny `-32601` на неенумерований метод. **Нема** method-version/`apiVersion`-handshake/capability-negotiation/deprecation-window. Розширення — окремий деплой (план це каже) → Phase-1→2 апгрейд сервера = flag-day для кожного розгорнутого розширення.
- *Мітигація:* увести `apiVersion`/capability-negotiation у handshake АБО transition-вікно (приймати і authed, і unauthed тимчасово) + узгодити реліз сервера з релізом розширення; зафіксувати у contract-докс (Phase-3 task-3).

### Звірка-7 — Прохід 5 (фінальний): seam-аудит app-side vs upstream + конвергенс (2026-06-25)

Прохід 5: (а) адверс. верифікація A3/A4/A5 — **усі вистояли**, нових немає; внутрішня консистентність серій W/N/C/A — чиста (жодна спростована знахідка W1/W12/W18/N6/N8 не цитується деінде як живий блокер; C4⊕W11 узгоджені; єдина невирішена суперечність W13/G3⊥G11 уже сама прапорена планом). (б) seam-аудит: чи net-new механізми реально робляться app-side, чи приховано вимагають upstream-зміни (порушення critical-rule #1 «це glue, фіксь upstream, не тут»).

**🟠 U1. Frame-accumulation DoS-тріада — структурно НЕ app-side І НЕ фікситься bump'ом JSON-RPC.NET (єдиний механізм, де принцип «це glue» ламається).**
- *Доказ:* `OnWsReceived(buffer, offset, size)` (`SignalRpcSession.cs:210,221`) отримує **вже зібране** повідомлення — NetCoreServer буферить усі continuation-фрейми у внутрішньому `WsBuffer` до виклику консьюмера; seam'у нижче `OnWsReceived` (abort під час накопичення) **немає**. `WebSocketMessageHandler` теж бачить лише post-assembly байти. Тобто: (а) апка не може (нема seam'у); (б) JSON-RPC.NET не може додати seam без власного форку NetCoreServer's frame-loop; (в) реальний фікс — у **NetCoreServer** (second-order upstream, якого rule-1 навіть не називає). План мітить frame-assembly-abort / max-frame-count / assembly-timeout (slowloris) як «повністю net-new app work» (msg-row, W7), але вони **нерозкладні як app-задача** і **невиправні bump'ом**.
- *Підсилення:* chokepoint живе на dispatch-шарі (`JsonRpc.DispatchRequestAsync`, app-reachable), а frame-DoS-захист мусить бути **нижче** `OnWsReceived` (pre-parse, NetCoreServer-internal). Атака OOM гігантським multi-frame-повідомленням **обходить chokepoint** (він не запускається — процес падає під час reassembly до dispatch'у). Це єдиний DoS-клас, який апка архітектурно НЕ може захистити.
- *Мітигація:* пере-тегнути ці пункти з «Phase-2/3 net-new app work» на **accepted-risk + зовнішній L4-контроль (вфолдити у D5)** — план уже має цей словник для сусіднього D5; АБО запланувати явну NetCoreServer-upstream-контрибуцію. Перестати нести як in-repo app-задачі.
- *Конвергенс-нотатка:* решта **6/7** net-new механізмів (chokepoint via app-конструйований `AuthorizingJsonRpc`/`JsonRpc`-subclass; per-IP cap via override `OnConnected(TcpSession)` + `Socket.RemoteEndPoint`; auth-timeout; idle-timeout via app-owned `OnWsReceived`; server-wide in-flight semaphore G6 via decorator над `ISignalCliClient`; PoP/handshake-reject via `OnWsConnecting`) — **APP-SIDE-OK** на наявних virtual-seam'ах (апка субкласить server+session і сама конструює `JsonRpc`). Принцип «це glue» тримається для них; план місцями over-worry-ить (per-IP/idle-timeout реально app-side). Rule-1 порушується ЛИШЕ frame-тріадою.

#### Статус ревю (підсумок 5 проходів)

Темп нових знахідок по проходах: **Прохід-1/2 ~25 (W1-W25) → Прохід-3 ~16 (N1-N10, C1-C6) → Прохід-4 5 (A1-A5) → Прохід-5 1 (U1)**; верифікаційний агент Проходу-5 нових не дав, внутрішня консистентність чиста. **Конвергенція досягнута** (cap=5 проходів).

- **Спростовано/знижено за час ревю (адверсаріальна само-перевірка):** W1, W12 — **спростовані** (NU1603 floor-resolution; send-response error-path); W18, N6 — **знижені** 🔴→🟠. Це показує, що сам пайплайн ревю ловить власні false-positive.
- **Невирішені план-внутрішні рішення для власника (не код-баги, а вибори):** W13 (G3⊥G11 — обрати режим), W14/W16/W17 (PoP-latency-бюджет / єдине admission-rule / пропорційність auth-стеку), U1-тегування (accepted-risk vs upstream-контриб), N3/N4 (DoD-власники для G11 та W13-W17).
- **Keystone-знахідки, що тримаються:** A1/A2 (serializer reflection-fallback воїдить «реєструй кожен DTO»; ConsoleTraceListener param-firehose сьогодні) 🔴; C1 (device-key мусить бути IndexedDB) 🔴; W13/W20 🔴; W4 (обсяг task-0) 🔴.

### Рішення власника — закриття W13 / W14 / W16 / W17 / U1 / N3 / N4 (2026-06-25)

Сім відкритих план-внутрішніх виборів закрито. Формат: **Рішення** / *Чому* / *Власник (task + DoD)* / *Відхилено*.

**W13 (G3 ⊥ G11) → РІШЕННЯ: reserve-then-send (block-reservation) — розчіпити atomicity і durability.**
- План конфляував дві окремі гарантії; розділяємо:
  - *Race-safety (G3):* in-memory атомарний лічильник під lock (або `Interlocked` / `UPDATE…WHERE n>0`) — на кожен send, **без DB**.
  - *Crash-safety / анти-бан (Уточнення-6):* durable persist НЕ на кожен send, а **блоками** — одним атомарним durable-записом «зарезервувати» K одиниць (`UPDATE budget SET reserved=reserved+K WHERE remaining>=K`), витрачати з in-memory-блоку; новий запис лише на вичерпанні блоку / короткому таймері.
  - *Restart:* cold-start з durable `reserved/consumed`; невитрачений залишок блоку **форфейтиться** (= недосил, fail-safe; ніколи не re-spend → нуль over-send).
- *Чому:* «persist ДО send» виконано на гранулярності **блоку**, не send → анти-бан-інваріант (нуль over-send) збережено; G11-throughput знятий (1 DB-запис на K send). K — тюнінг (менший = менший форфейт на краш, більше записів; дефолт K≈max(10, бюджет/20)).
- *Власник:* Phase-2 task-5; DoD доповнити «краш форфейтить ≤K, ніколи не over-send; reservation-RMW атомарний» (поряд із наявним G3-concurrency DoD).
- *Відхилено:* чистий coalesce-on-timer (G11) — ламає persist-before-send; чистий per-send-durable (G3 буквально) — throughput-bottleneck (G11).

**W14 (connect-on-demand vs PoP-per-connect латентність) → РІШЕННЯ: PoP — per-CONNECTION, не per-notification; burst-reuse у межах SW-wake; cold-connect-латентність прийняти.**
- Уточнення-1 «close на нотіф» **пом'якшуємо** до «close на SW-idle / після дренажу burst'у». MV3 SW живе ~30с і батчить нотіфи в одному wake → **один PoP на сокет**, амортизований на ввесь burst, не на кожну нотіф.
- TLS 1.3 session-resumption tickets — дешевий серверний add, що зрізає repeat-handshake між wake'ами.
- Залишкову латентність одиничного cold-connect **приймаємо** (нотіфи її толерують; correctness > latency).
- *Власник:* Phase-3 task-3 (connection-contract); DoD «PoP раз на сокет; burst у межах wake переюзає authed-сокет (не re-PoP на кожну нотіф)».
- *Зв'язок:* C2 (idempotency-key) лишається обов'язковим — burst-reuse не скасовує at-most-once MV3.

**W16 (звести лімітери в одне admission-rule) → РІШЕННЯ: одна впорядкована admission-функція на chokepoint, short-circuit на першому deny.**
```
admit(principal, account, recipient):
  1. global_pause (Phase-3, реакція на -5/-6)? → DENY 4429 retry_after        # safety-first, найзовнішніший
  2. account == shared_bot:
       a. per_user_quota[principal] вичерпано? → DENY -32005                   # fairness-floor (G2)
       b. new_recipient_window над лімітом нових контактів? → THROTTLE          # D2 анти-first-contact-бан
       c. aggregate_bot_budget reserve-1 (схема W13) вичерпано? → DENY 4429     # анти-бан-обсяг
  3. account ∈ власні caller: лише per_user_quota                              # власний номер, без shared анти-бану
  ALLOW
```
- *Чому порядок такий:* per-user **перед** aggregate → один юзер не дренажить aggregate за межі свого floor (закриває G2-starvation). Калібровка: Σ(per_user_floor) ≤ aggregate; burst-над-floor лише за наявності aggregate-headroom (fair-share). D2-new-recipient вфолджено як крок (b); global-pause — зовнішній gate.
- *Власник:* Phase-2 task-1 (функція живе на chokepoint); DoD «єдине admission-rule; per-user floor спрацьовує під aggregate (не starvation); D2-new-recipient throttle окремо від обсягу».

**W17 (пропорційність auth-стеку vs «бот=відкрите реле») → РІШЕННЯ: явно розділити threat-model; стек тримаємо, recipient-abuse НЕ gold-plate-имо.**
- Auth-стек (token/PoP/invite/isolation/budget) пропорційний **чотирьом** загрозам: (1) privacy власного номера (причина обов'язкової ізоляції), (2) takeover бот-акаунта (лінк девайсів — лише admin), (3) виживання бота в Signal (budget/quota ріже обсяг), (4) Sybil/неавтентиф. абʼюз (invite-gating обмежує, хто взагалі юзає бота).
- Recipient-abuse (автентиф. юзер → довільний E.164) — **окремий, явно прийнятий** residual: per-user quota + abuse-log + invite-cost (вже в плані) + опційний per-user recipient-opt-in як майбутній тогл. **Без** per-recipient proof-of-ownership (bot-модель це вже прийняла).
- *Власник:* Phase-2 task-12 (`shared-bot.md`); DoD «shared-bot.md явно розділяє: стек = захист isolation/takeover/ban/Sybil; recipient-abuse = accepted residual + cheap-мітигації + регламент скарг».

**U1 (frame-DoS-тріада) → РІШЕННЯ: пере-тегнути на accepted-risk + зовнішній L4 (вфолдити у D5); прибрати з app-task-листів.**
- Нема app-seam'у, не фікситься bump'ом JSON-RPC.NET (gap у NetCoreServer). MVP: accept-risk + **зовнішній L4** (nftables/cloud-SG conn-rate; reverse-proxy/WAF з WS-frame-лімітами за наявності ingress) — позиція D5. Post-assembly 64KB-cap (app-side, вже є) — дешевий частковий захист. Опційно (non-blocking): NetCoreServer-upstream-issue.
- *Власник:* перенести frame-assembly/slowloris-пункт з Phase-2 task-5 у **D5-accepted-risk** + ops-runbook (Phase-3 task-8); відповідний Phase-2 DoD-рядок замінити на «L4-передумова задокументована» (нижче, інлайн у D5).

**N3 (G11 без DoD) → ЗАКРИТО W13-рішенням + perf-gate DoD.** Reservation-схема знімає per-send DB-write; додати Phase-3 perf-gate DoD «sustained send-rate не впирається в budget-persist (1 запис на K send); виміряти writes/sec під навантаженням». *Власник:* Phase-3 task-4.

**N4 (W13-W17 без власника) → ЗАКРИТО маршрутизацією вище:** W13→task-5, W14→Phase-3 task-3, W16→task-1, W17→task-12, кожне з DoD-рядком. Більше не «висить».

## Глобальний Definition of Done (для КОЖНОЇ фази)

Фаза не закрита, поки не виконані всі три умови:

1. **Реально працює на e2e** — ручний прогін + автоматичний e2e-тест зелені проти живого signal-cli (не моки).
2. **Покрито тестами** — кожен новий публічний метод / гілка має тест; CI зелений; негативні шляхи теж покриті.
3. **Документація написана** — відповідний гайд (self-host / shared-bot / ops) оновлено в `docs/`, API-методи описані.

## Стратегія делегування субагентам

Головний потік тільки **оркеструє і рев'ює дифи**, не читає файли цілком. Точкові задачі — субагентам, щоб не засмічувати контекст:

| Тип задачі | Агент | Навіщо |
|---|---|---|
| Знайти де щось визначено / хто викликає | `cavecrew-investigator` | повертає `file:line`, ~60% менше токенів |
| Правка 1-2 файлів (DI-реєстрація, дрібний фікс) | `cavecrew-builder` | хірургічний дифф-чек |
| Рев'ю дифу/гілки перед коммітом | `cavecrew-reviewer` | один рядок на знахідку |
| Мапа підсистеми / широкий пошук | `Explore` | читає уривки, не цілі файли |
| Багатофайлова фіча + тести разом | `general-purpose` | автономний, повертає підсумок |

Правило: якщо задача — «знайти», «змінити 1-2 файли» або «рев'ю» — **не роблю інлайн**, делегую. Інлайн лишається лише фінальна збірка результатів і рішення.

---

## Фаза 1 — Self-host Signal-send бекенд (path C: «свій RPC сервер»)

**Мета:** WsRpcServer — робочий, протестований, задокументований однокористувацький Signal-сервер. Однокористувацький → auth не потрібен.

**Безпека Фази 1 (критично):** auth немає → сервер МУСИТЬ біндити **`127.0.0.1`** (НЕ дефолтний `0.0.0.0:9000`). Інакше Фаза 1 = відкрите Signal-send реле для всіх у мережі/інтернеті. TLS у Фазі 1 нема (loopback не потребує), `wss` додається Фазою 2. Якщо комусь треба не-loopback у Фазі 1 — документувати обов'язковий firewall як передумову.

### Задачі
1. **Закрити WIP групи.** Додати `AddScoped<ISignalGroupsRpc, SignalGroupsRpcAdapter>()` у `Extensions/SignalRpcExtensions.cs`. **(Звірка-3 V7: код уже закоммічено — бракує ЛИШЕ цього рядка, не «3 файли».)** **Звірити lifetime:** `Scoped`, захоплений singleton hosted-service (`SignalRpcHostedService`), = captive-dependency-баг; lifetime має збігтися з рештою адаптерів (`Signal*RpcAdapter`). **(Звірка-3 V8: реєстр резолвить ВСІ адаптери з root-провайдера (`AbstractRpcServiceRegistry.cs:165`) → `Scoped` косметичний для всіх, не лише Groups; жоден адаптер не має per-connection ізоляції. Наслідок для Фази 2: principal читати з call-context, НЕ з ctor адаптера.)**
   → `cavecrew-investigator` (звірити lifetime інших адаптерів) → `cavecrew-builder` (1 файл правка + коміт-діфф).
2. **Контейнеризація — фіналізувати наявне.** `deploy/Dockerfile*` + `deploy/DEPLOYMENT.md` уже є (JDK 25 / Temurin, signal-cli 0.14.3). Перевірити збірку, додати `docker-compose.yml` якщо бракує, звірити приватний feed (`build-local-feed.sh` / `NuGet.Config` packageSourceMapping) для офлайн-збірки.
   → `general-purpose` (звірити Dockerfile, compose, smoke up).
3. **Health/readiness ендпоінт** для контейнера. **Unauth, але без внутрішнього стану** (нема акаунтів/номерів/конфігу у відповіді), не DoS-ампліфікатор.
   → `cavecrew-builder`.
4. **Інтеграційні тести RPC-адаптерів** (мок `ISignal*`): `listAccounts`, `startLink`, `finishLink`, `sendTextMessage`, `listGroups` — happy + негативні (порожні recipients, невалідний account).
   → `general-purpose` (створює тест-проект `tests/`, бо його зараз нема).
5. **e2e-смоук:** clean `docker compose up` → link номера → надсилання тексту реальному отримувачу → `listGroups`.
   → ручний прогін + скриптований e2e.
6. **Docs:** `docs/self-host.md` — як підняти свій сервер за 5 хв, таблиця конфігу (`Server:Host/Port`, `SignalCli:LibDirectory/AppHome`).
   → `general-purpose`.
7. **Receive-mode для send-only MVP.** **(Звірка-3 V2 🟠: ПЕРЕГЛЯНУТО — `UseManualReceiveMode` default=`true` (`SignalCliOptions.cs:56`), app `Program.cs` не чіпає → демон ВЖЕ `--receive-mode=manual`. Початкова теза «демон default on-start» хибна. Ця задача = no-op; звести до тесту-факту «default справді manual» + фіксація в докс.)** ~~Звірено: демон стартує `--receive-mode=on-start` за замовчуванням → eager-receive за всі акаунти; виставити `UseManualReceiveMode=true`.~~ **G7-caveat (для Phase-2 own-number):** manual receive → щойно-злінкований власний номер не sync-ить групи/контакти → `listGroups` порожній/stale. `finishLink` (provisioning-сокет) завершується, але пост-лінк дані ні. У Phase-2 own-number flow дати bounded one-shot receive/sync після finishLink, АБО задокументувати.
   → `cavecrew-builder` (1 опція в конфігу + тест).

### Умови зупинки (DoD Фази 1)
- [ ] e2e: з чистого docker up — link, send (доставлено реально), listGroups — усе зелене (ручний + авто).
- [ ] **Bind:** сервер слухає `127.0.0.1` (не `0.0.0.0`); тест/перевірка, що порт не відкритий назовні без auth.
- [ ] Кожен з 5 RPC-методів має інтеграційний тест (happy + ≥1 негативний); CI зелений.
- [ ] `docs/self-host.md` готовий, конфіг задокументований.
- [ ] Гілка пройшла `cavecrew-reviewer`, зміни закоммічені/запушені.

---

## Фаза 2 — Мультитенант + безпека (path A/B: «спільний бот»)

**Мета:** сервер безпечно хостить багатьох. AuthN + ізоляція акаунтів на фреймворковому `Authorization/`+`Security/`.

> **🔴 ПЕРЕДУМОВА (task-0, Звірка-3 V1) — БЕЗ ЦЬОГО ФАЗА 2 НЕ КОМПІЛЮЄТЬСЯ:** апка пінить `JSON-RPC.NET 1.1.0`; увесь `Authorization/`+`Security/`+квота існує лише в **2.6.0/2.7.0**. **Bump 1.1.0 → 2.7.0** + міграція **breaking 2.0.0** (`AddJsonRpcCore` 5→7 type-params, `ISubscriptionManager`→generic, `account`→`topic`) у `AddSignalJsonRpc`/`SubscriptionManager`/`EventProcessor` + регрес-тести — **перш ніж** будь-яка задача нижче. Синхронно оновити `csproj`, `NuGet.Config`, `deploy/*`, CLAUDE.md (CLAUDE-rule #2).
> → `general-purpose` (bump + міграція breaking-2.0.0) → `cavecrew-reviewer`.

### Модель бота — ЗАФІКСОВАНО: поєднання обох варіантів
Сервер підтримує одночасно:
- **Спільний бот-номер «Потужність»** — один дефолтний акаунт сервера; будь-який автентифікований юзер шле через нього на власних отримувачів; нуль налаштувань (галочка).
- **Свій лінкований номер** — хто хоче, лінкує власний номер (QR / device-link); цей акаунт **приватний для юзера**, що його прив'язав.

Наслідок: **повна ізоляція обов'язкова** (бо свій-номер шлях її вимагає). Правила доступу:
- `listAccounts` → повертає спільний бот-акаунт (read-only) **+** власні лінковані акаунти caller. Чужі приватні — не видно.
- `sendTextMessage` / `listGroups` → дозволений `account ∈ {спільний бот} ∪ {власні caller}`. **G9:** `listGroups` на **спільному** боті розкриває чужі групи (cross-tenant) → admin-only АБО explicit accepted-risk у `shared-bot.md` (outbound-фільтр account-level не скопить усередині спільного акаунта).
- `startLink`/`finishLink` для **нового свого номера** → allow, акаунт прив'язується приватно до caller.
- `startLink`/`finishLink` з ціллю **спільний бот-акаунт** → **лише admin** (звичайний юзер не може прилінкувати свій пристрій до бота = захист від takeover).
- Деструктивні/девайс-операції над спільним бот-акаунтом → лише admin (звичайний юзер не може unregister / removeDevice бота).

### AuthN/AuthZ — ЗАФІКСОВАНО (OWASP / RFC 8725 / k8s) + правки security-рев'ю
Delta SSO відсутній → JWT/SSO відпадає. mTLS — лише node-to-node/admin, не для розширення.
Базовий факт: **браузерний `WebSocket` не вміє кастомні заголовки** → `Authorization: Bearer` неможливий.

| Шар | Рішення (фінал) |
|---|---|
| Транспорт | `wss://` only, **TLS 1.3** (1.2 floor), AEAD, **HSTS**. **Kestrel термінує TLS сам — без проксі** (Варіант 1, «Уточнення» п.5). Cert-pinning НЕ (HPKP deprecated). |
| Доставка креденшела | subprotocol-токен у `Sec-WebSocket-Protocol`: `base64url.bearer.<token>` (**base64url БЕЗ padding — `=` не валідний RFC 6455/7230 token-char**) + другий реальний субпротокол. Auth на HTTP-upgrade → **401 до апгрейду**. **Echo лише non-auth субпротоколу — токен-субпротокол НІКОЛИ не вертати.** Fallback: first-message `authenticate(token)` з жорстким auth-таймаутом + cap неавтентиф. сокетів/IP. **Токен у params `authenticate` НЕ логувати** (третій вектор витоку після `Sec-WebSocket-Protocol`/`Authorization`). |
| Origin | **Allowlist на upgrade — defense-in-depth, НЕ authn** (захищає лише від браузерних cross-site; не-браузерний клієнт із токеном обходить). Реальне значення Origin для MV3 SW (`chrome-extension://<id>` чи `null`) — **перевірити емпірично**, не хардкодити на припущенні. |
| Формат токена | **opaque ≥256-bit CSPRNG**, prefix+checksum (`ptzh_…`). **Серверний стор — єдине джерело правди**; жодних claims/підпису в токені (не повертатись до stateless — миттєвий revoke головніший). |
| Lifecycle токена | `ExpiresAt` + `IsRevoked` — **у сторі поруч із хешем**, не в токені. TTL ~12 год. **Lookup стору на КОЖЕН connect** (connect-on-demand робить це дешево) → ніколи не пропускати перевірку revoke. Ротація = видати новий opaque. Expiry → сокет закрито, розширення робить re-onboarding. |
| Proof-of-Possession | **Bearer недостатньо:** крадіжка токена (скомпрометований SW / зловмисне розширення / дамп памʼяті) = replay звідки завгодно до TTL/revoke. На КОЖЕН connect: сервер шле короткоживий challenge (nonce), клієнт підписує **non-extractable device-ключем** (той самий ключ, що для re-onboarding); сервер звіряє підпис → токен прив'язаний до пристрою, самої крадіжки токена замало. MVP-мінімум якщо PoP відкладено: **явно зафіксувати залишковий ризик replay** + коротший TTL. |
| Re-onboarding | Явно: device-bound секрет (non-extractable WebCrypto, як токен) для мовчазного re-issue **АБО** повторний інвайт/пін. Device-секрет має **власний серверний revoke і lifecycle** (це refresh-механізм — не лишати без політики). **Revoke identity каскадить АТОМАРНО на обидва — access-токен І device-секрет**, інакше вкрадений device-секрет тихо re-issue-иться попри revoke токена. |
| Токен at-rest | **HMAC-SHA256 з окремим pepper**, **версіонований `v1$hash`** (ротація pepper без масового розлогіну: старі через old_pepper, нові через new). Порівняння constant-time. (argon2/bcrypt НЕ треба для ≥112-bit ентропії — ASVS V2.9.) |
| Сховище в розширенні | `chrome.storage.local` + шифр **non-extractable WebCrypto AES-GCM** (raw-ключ недоступний JS); читати **лише в background SW**. **Чесно: шифр не рятує від скомпрометованого SW (XSS викличе decrypt) — серверний revoke = головна гарантія.** НЕ `.sync`. `.session` = опція «не запам'ятовувати». |
| AuthZ | **per-connection principal, ГЕРМЕТИЧНИЙ default-deny chokepoint** (нижче). Набір акаунтів **виводиться з principal, НІКОЛИ з аргумента** (анти-IDOR/confused-deputy). |
| Rate-limit/DoS | per-token cap конектів + msg-rate (~100/хв) + **msg-size cap 64KB — енфорс ПІД ЧАС накопичення фреймів, не на зібраному повідомленні** (обрив до парсингу JSON) + **permessage-deflate вимкнено або обмежено за роздутим розміром (анти-zip-bomb: 64KB стиснутого роздуваються у ГБ)** + **per-message таймаут збірки + max-frame-count (анти-slowloris/partial-frame)** + auth-timeout + per-IP cap неавтентиф. сокетів + **агрегатний бюджет на спільний бот-номер (анти-Signal-ban)** + **per-user квота всередині бота**. **Лічильники in-memory** (один інстанс — «Уточнення» п.6); **агрегатний бот-бюджет персистити в durable** (persist декремент ДО send; не скидати на рестарт). Mid-connection перевищення → JSON-RPC error `-32005` + `data.retry_after`, або WS close `4429` (HTTP `429` лише до апгрейду). **Звірено з кодом: фреймворк дає ЛИШЕ глобальний `MaxConcurrentConnections`; per-token/per-IP cap, msg-rate, auth-timeout, idle-timeout, msg-size-cap (`MaxMessageSizeBytes` — no-op), frame-count, assembly-timeout, zip-bomb-чек — ВСЕ net-new.** |
| Дані at-rest | **LUKS-encrypted persistent volume** (НЕ `tmpfs` — стирає реєстрацію signal-cli при рестарті), `0700`, окремий сервіс-юзер `signal-runner` (uid 10001), LUKS-ключ поза диском даних; ротація. (App-level шифр файлів signal-cli — нежиттєздатний: демон читає plaintext.) **Durable стор (identity / token-хеші / прив'язки account / device-секрети / бот-бюджет) = embedded DB (SQLite/LiteDB), файл на ТОМУ Ж LUKS-томі.** Redis не використовується (один інстанс — «Уточнення» п.6). |
| Секрети (provisioning) | `pepper_token`, `pepper_abuse`, LUKS-ключ, TLS-приватний-ключ — **назвати механізм** (systemd-creds / Docker secret / Vault / KMS), НЕ голі env-vars (течуть через `/proc`, `docker inspect`, crash-dump). Регламент ротації кожного. TLS-серт: lifecycle/renewal (ACME або ручний), приватний ключ на LUKS, `0600`. |

Identity record: `{ id, displayName?, role (user|admin), linkedAccounts[], createdAt }` — у **durable embedded DB** (переживає рестарт; in-memory заборонено для identity/прив'язок — flush = lockout). Revoke = прибрати зі стору + **інвалідація device-секрету (каскад)** + розрив живих конектів **in-process** (один інстанс — без pub/sub). Identity-provider абстрактний (під майбутній Delta SSO).

### Герметичний Chokepoint — центральний фільтр (single chokepoint, анти-IDOR)

Усі RPC проходять через один фільтр. **Default-deny: метод не в декларативному реєстрі → `-32601` на ВХОДІ, до будь-якого виконання** (перевірка політики — перша дія, НЕ після `next()`).

> **Звірено з кодом — це net-new, не «підключення».** Фреймворковий `[RpcAuthorize]` deny-by-default спрацьовує **лише для атрибутованих** методів (`RpcAuthorizationEnforcer.cs:43`); неатрибутований метод відкритий. Герметичність досягається ОДНИМ із: (а) власний pre-dispatch фільтр з декларативним реєстром політик, **незалежним** від рефлексійного/source-gen авто-дискавері методів (інакше новий метод авто-allow-иться); АБО (б) `[RpcAuthorize]` на КОЖНОМУ методі + кастомна `IRpcAuthorizationPolicy`, що відмовляє за замовчуванням, + build-guard, який валить збірку на RPC-методі без атрибута. Principal на сесії виставляємо самі з токена (`AbstractJsonRpcSession.Principal` settable) і передаємо в `AuthorizingJsonRpc`.

Політики методів (декларативний allowlist, opt-in):
- `Public` — без authn (напр. `ping`).
- `IdentityOnboarding` — **без токена**, але з rate-limit (напр. `redeemInvite`).
- `AccountCommand` — дія над акаунтом → **inbound IDOR-guard** `AssertAccountAllowed(principal, targetAccount)`.
- `AccountQuery` — читання → **inbound guard + outbound filter**.

AuthN **умовний по політиці**: `Public`/`IdentityOnboarding` пропускають токен-гейт; решта вимагають валідний токен (стор-lookup + revoke + TTL).

Спец-логіка лінкінгу (НЕ плоский `AccountCommand`): `startLink`/`finishLink` з `targetAccount == спільний бот` → **лише admin**; новий свій номер → allow + bind до caller (існуючого account для assert немає).

Каскад спільного бота: **агрегатний бот-бюджет** (token-bucket по бот-номеру, in-memory + персист у durable, анти-бан) → **per-user псевдонімізована квота**.

**Прийнятий ризик — спільний бот = відкрите реле в межах бюджету.** Авторизація на рівні **account**, не recipient: будь-який автентиф. юзер шле на **довільний** E.164/групу через бот — доказу володіння отримувачем нема. Агрегатний бюджет ріже **обсяг**, не **таргетинг** (один юзер може цькувати конкретну жертву). Мітигація: per-user квота + abuse-log + (опційно) recipient-allowlist/opt-in на юзера. **Зафіксувати в `docs/shared-bot.md` як прийнятий ризик + регламент реагування на скарги.**

**Stale principal:** `principal` (з `linkedAccounts`) знімається при конекті; mid-connection unlink/revoke лишає сокет авторизованим. Низький ризик при connect-on-demand (короткі сокети); на fallback-сокеті — re-валідувати principal перед кожною `AccountCommand`/`AccountQuery` або тримати auth-таймаут коротким.

Abuse-log: `HMAC-SHA256(Key=pepper_abuse, Data="abuse-log" ‖ TargetAccountId ‖ Recipient)` — **домен-розділений ключ** (окремий від токен-хешу), **псевдонімізація під секретом, НЕ анонімізація** (E.164 низькоентропійний → реверс при витоку pepper). Pepper версіонований; ротація → історичні лічильники скинуті (фіксуємо в докс).

Outbound interceptor: `AccountQuery` → `FilterReadOutputAsync(principal, response)` лишає лише авторизовані акаунти (`listAccounts`/`listGroups`).

Санітизація помилок: **усі** клієнтські помилки — generic; деталі (включно `ex.Message`, stack) — лише серверний лог. Не повертати внутрішній текст клієнту (не лише для `AccountQuery`).

### Link-session state-machine
- `startLink`: `SessionID` 256-bit CSPRNG; стор **in-memory** (один інстанс) `SessionID → (CreatedByIdentityId, TargetAccount, ExpiresAt)`, **TTL 120 c**, **one-time-use**; rate-limit **3/хв/identity** (анти-exhaustion); target=спільний бот → лише admin. **Device-link URI/QR у відповіді — чутливе (лінкує signal-cli до акаунта), НЕ логувати.**
- `finishLink`: **атомарне умовне гашення** (**in-process lock** — сервер БАГАТОПОТОКОВИЙ, тож атомарність дає саме lock, не «single-threaded»; Lua/`GETDEL` не потрібні без Redis; embedded-DB запис під WAL + retry на `SQLITE_BUSY`): видалити сесію **лише якщо identity ініціатора збігся**; mismatch → **НЕ видаляти** (інакше B знищить сесію A — griefing), лог alert. Закриває TOCTOU + анти-IDOR + griefing разом. Звіряти стабільний `IdentityId`, не токен.

### Задачі
1. **Герметичний chokepoint (default-deny, inbound+outbound) — NET-NEW (не «підключення»).** Декларативний реєстр політик; перевірка політики на вході (незареєстрований метод → `-32601` без виконання); умовний AuthN по політиці; `AssertAccountAllowed`; outbound `FilterReadOutputAsync`; санітизація **всіх** клієнтських помилок. Маніпуляція `account`-аргументом клієнта → відмова авторизації. **Звірено: фреймворковий `[RpcAuthorize]` гейтить лише атрибутовані методи — реєстр політик має бути НЕЗАЛЕЖНИМ від авто-дискавері + build-guard (тест), що валить збірку на RPC-методі без явної політики.** Token→principal самим (`AbstractJsonRpcSession.Principal` settable) + `AuthorizingJsonRpc`. **(Звірка-3 V9: правка sealed `Sessions/SignalRpcSession.cs:98` — `new JsonRpc(_messageHandler)`→`AuthorizingJsonRpc(handler, principal, policy)` + виставити `Principal` на сесії + token/identity-DTO у `Serialization/SignalCliSerializerContext`. Не чистий DI/config. V8: principal читати per-invocation з call-context, НЕ з ctor адаптера — адаптери shared.)**
   → `cavecrew-investigator` (де JSON-RPC.NET приймає upgrade / читає `Sec-WebSocket-Protocol` / per-connection principal; як виставити `Principal` на plaintext-сесії) → `general-purpose` (вшити + build-guard).
2. **Token store + lifecycle + видача/revoke:** `HMAC(token,pepper)→identity`, **`v1$hash` версіонування**, pepper окремо від БД; `prefix+random≥256-bit+checksum`; `ExpiresAt`/`IsRevoked` у сторі, **TTL ~12 год, lookup на кожен connect**; ротація/revoke + розрив живих конектів **in-process**. Стор — **durable embedded DB (SQLite/LiteDB)**, переживає рестарт. Re-onboarding (device-секрет із власним revoke або повторний інвайт/пін); **revoke identity каскадить атомарно на access-токен І device-секрет**. **Proof-of-Possession:** challenge-response підписом non-extractable device-ключа на кожен connect (або явно зафіксувати залишковий replay-ризик + коротший TTL, якщо PoP відкладено).
   → `general-purpose`.
3. **Стор прив'язок** `identity → account(s)` (+ прапор спільного бот-акаунта, доступного всім). **Durable embedded DB** (НЕ in-memory — flush = lockout).
   → `general-purpose`.
4. **AuthN-хендшейк:** subprotocol-токен на upgrade (401 до апгрейду, **echo лише non-auth субпротоколу**, **base64url без padding**); **Origin-allowlist** (defense-in-depth, перевірити реальне MV3-значення); fallback first-message `authenticate` з auth-таймаутом + cap неавтентиф. сокетів/IP; **PoP-challenge** на connect; **redact токен у params `authenticate` та device-link URI у логах**. **G8:** nonce — one-time (consumed на verify), per-conn binding (підпис покриває `nonce‖connId`), короткий TTL, CSPRNG ≥128-bit → релей підпису на інший сокет у PoP-pending вікні не проходить.
   → `general-purpose`.
5. **Rate-limit/DoS (in-memory, один інстанс):** per-token cap конектів + msg-rate (~100/хв) + **msg-size 64KB — енфорс під час накопичення фреймів** + **permessage-deflate off/обмежено (анти-zip-bomb) — спершу перевірити, чи дефлейт узагалі ввімкнений** **(Звірка-3 V6 🟡: перевірено — NetCoreServer 8.0.7 deflate НЕ підтримує (0 hits); zip-bomb-ампліфікація неможлива → ця робота зайва, лишити як факт-нотатку)** + **per-message таймаут збірки + max-frame-count (анти-slowloris)** + auth-timeout + per-IP cap неавтентиф. сокетів; **агрегатний бюджет спільного бота (token-bucket по бот-номеру, in-memory + персист у durable, persist ДО send)** + per-user квота; abuse-log домен-розділений псевдонім. Mid-connection перевищення → `-32005`+`retry_after` / WS `4429` (HTTP `429` лише на upgrade). **Звірено: глобальний `MaxConcurrentConnections` (ввімкнути) — ЄДИНЕ, що дає фреймворк; усе решта (per-token/per-IP/msg-rate/auth-timeout/idle/розмір/frame-count/assembly-timeout) — net-new. `MaxMessageSizeBytes` не читається в сорсі → на нього НЕ покладатися.**
   - **G3:** декремент бот-бюджету — атомарний RMW під lock (або `UPDATE…WHERE n>0`+rows-affected), НЕ lock-free; «persist ДО send» не рятує від read-modify-write гонки (сервер багатопотоковий).
   - **G2:** звести per-user квоту з агрегатом — per-user floor / fair-queue, щоб один юзер не голодоморив решту (pure-FIFO дренаж = starvation).
   - **G6:** server-wide in-flight semaphore до signal-cli (понад → `-32005`); per-conn cap (D12) ≠ глобальна стеля — `JsonRpcClient._pendingRequests` без cap (`:42,474`).
   → `cavecrew-builder` + `general-purpose`.
6. **Link-session state-machine:** **in-memory** стор `TTL 120c` / one-time / identity-bound; **атомарне умовне гашення (in-process lock)**; startLink rate-limit 3/хв; лінк спільного бота → лише admin; device-link URI не логувати.
   → `general-purpose`.
7. **Інвайти:** код **≥96-bit CSPRNG** (візуальні блоки); **per-code attempt cap** + м'який глобальний rate-cap (НЕ жорсткий глобальний → shared-fate DoS); видача через admin-RPC/CLI; регламент передачі (не публікувати у відкритих каналах) — у докс. **G12:** інкремент per-code лічильника атомарний (паралельні guesses не обходять cap гонкою); порівняння коду constant-time (timing-oracle на prefix).
   → `general-purpose`.
8. **Admin bootstrap:** CLI-команда / env при першому запуску призначає першу identity `role=admin` в обхід інвайтів; **ідемпотентно** (не перестворювати), без статичного дефолт-пароля. **G5:** one-time admin-токен у `0600`-файл на LUKS (АБО interactive TTY), **НЕ stdout** — у контейнері stdout = `docker logs`/journald = витік. **G10:** на першому Phase-2 старті апгрейднутого self-host — bind pre-existing signal-cli акаунт до цієї admin-identity (інакше orphan).
   → `cavecrew-builder`.
9. **Hardening at-rest + транспорт:** **LUKS persistent volume** (НЕ `tmpfs`) для `SignalCliStorageData/` **+ файл embedded DB**, `0700`, сервіс-юзер `signal-runner` (uid 10001), LUKS-ключ поза диском даних; Kestrel `wss://` TLS 1.3 (1.2 floor) + HSTS, TLS-серт lifecycle/renewal + приватний ключ `0600` на LUKS; **secret provisioning** (`pepper_token`/`pepper_abuse`/LUKS-ключ/TLS-ключ через systemd-creds/Docker-secret/Vault, не голі env) + регламент ротації; **не логувати `Sec-WebSocket-Protocol`/`Authorization`/`authenticate`-params/device-link URI** (Kestrel/ASP.NET request-logging нижче header-рівня).
   - **G1 (keystone):** exclusive data-dir lockfile (`flock`/`O_EXCL` pidfile) на старті → refuse-start якщо held; `replicas=1` як hard-constraint у docs. Без цього in-process locks (finishLink/бюджет) безглузді при double-start.
   - **G4:** періодичний шифрований DB-backup НЕ на той самий том + документований restore; `PRAGMA user_version`+migration-runner; integrity-check на старті (corrupt → fail-start, не тихо).
   → `general-purpose`.
10. **Тести ізоляції + auth** — повна матриця (див. DoD): анти-IDOR (inbound+outbound), default-deny, link-griefing/one-time, token-TTL, echo-leak, msg-size/zip-bomb/slowloris, auth-timeout, Origin, агрегатний бюджет, invite-cap, **PoP (replay вкраденим токеном без device-ключа → відмова)**, **revoke-каскад (device-секрет мертвий після revoke identity)**, **durable survive-restart (identity/token/прив'язки/бот-бюджет переживають рестарт)**.
   → `general-purpose`.
11. **Audit-trail (M8):** tamper-evident лог security-подій — лінк бот-девайсу, видача/redeem інвайту, промоушн admin, revoke. Окремо від abuse-логу; без токенів/номерів у тілі.
   → `general-purpose`.
12. **Docs:** `docs/shared-bot.md` — деплой спільного сервера, auth, at-rest, **abuse-політика + регламент інвайтів + прийнятий ризик «бот = відкрите реле в межах бюджету»**. Токени/номери/device-link URI **не логувати** (privacy-контракт репо).

### Умови зупинки (DoD Фази 2)
- [ ] e2e (спільний бот): автентифікований юзер шле через бот-номер на свого отримувача — доставлено.
- [ ] e2e (свій номер + ізоляція): A лінкує свій номер; A не дістає приватний акаунт B; невалідний/без токена → 401; revoke рве живий конект; rate-limit тригериться.
- [ ] **Chokepoint default-deny:** незареєстрований метод → `-32601` без виконання `next()`.
- [ ] **Build-guard покриття політик:** тест валить збірку/CI, якщо хоч один RPC-метод не має явної політики (анти-default-allow: фреймворковий `[RpcAuthorize]` гейтить лише атрибутовані — забутий атрибут = відкритий метод).
- [ ] **Outbound IDOR:** `listAccounts`/`listGroups` у A повертає лише акаунти A; B-акаунт вирізаний фільтром (навіть якщо ініційований у демоні).
- [ ] **Inbound IDOR:** A називає `account` B у `sendTextMessage`/`listGroups` → відмова авторизації.
- [ ] **Aggregate budget:** сумарний ліміт спільного бота → наступні запити `-32005`/`4429`, бот-номер захищено.
- [ ] **Link:** `finishLink` токеном B на сесію A → помилка, конфіг пристроїв A не змінено; повторний `finishLink` тим самим `SessionID` (у межах 120c) → відмова (one-time); сесія A **не знищена** спробою B (анти-griefing).
- [ ] **Token TTL:** RPC з простроченим токеном → auth-помилка без бізнес-логіки.
- [ ] **Echo-leak:** upgrade підтверджує лише non-auth субпротокол; токен НІКОЛИ не в заголовках відповіді.
- [ ] **Msg-size/DoS:** зібране повідомлення >64KB → `MessageTooBig`-обрив (app-side `MaxMessageSizeBytes`, опущений до 64KB). **(U1-рішення:** frame-assembly-abort / max-frame-count / assembly-timeout-slowloris **НЕ app-side** → винесено в D5-accepted-risk + L4-передумова задокументована; permessage-deflate не підтримується (V6) → zip-bomb-ампліфікація неможлива.)**
- [ ] **Auth-timeout:** fallback-сокет без auth за N c → примусове закриття; per-IP cap неавтентиф. сокетів спрацьовує.
- [ ] **PoP:** replay вкраденим токеном без device-ключа (інший пристрій) → відмова на connect.
- [ ] **Revoke-каскад:** після revoke identity device-секрет мертвий — мовчазний re-issue не проходить.
- [ ] **Token не в логах:** ані `Sec-WebSocket-Protocol`, ані `authenticate`-params, ані device-link URI не пишуться (перевірка лог-виходу).
- [ ] **Durable survive-restart:** після рестарту identity/token/прив'язки валідні; **агрегатний бот-бюджет НЕ скинувся** (анти-бан).
- [ ] **Origin:** upgrade з чужим/порожнім Origin → відмова (з поправкою на реальне MV3-значення).
- [ ] **Invite:** per-code cap блокує підбір; глобальний cap не лочить усіх легітимних.
- [ ] **Audit-trail:** лінк бот-девайсу / видача інвайту / промоушн admin / revoke лягають у audit-лог (без токенів/номерів).
- [ ] at-rest: **LUKS** увімкнено (signal-cli дані + embedded DB), ключ поза диском даних; secret provisioning не голі env; Kestrel `wss`/HSTS; **subprotocol-токен не в логах**.
- [ ] `docs/shared-bot.md` готовий (вкл. прийнятий ризик «бот = реле в межах бюджету»).
- [ ] **G1 Single-instance guard:** другий процес на тому ж data-dir → refuse-start (lockfile held); `replicas=1` задокументовано як hard-constraint.
- [ ] **G3 Budget concurrency:** N паралельних send під бюджетом=1 → рівно 1 проходить (не N); read-modify-write декремент атомарний.
- [ ] **G2 Budget fairness:** один юзер не голодоморить решту в межах агрегату (per-user floor / fair-queue спрацьовує).
- [ ] **G4 DB recovery:** corrupt DB-файл → integrity-check валить старт; restore з backup відновлює; `user_version` migration проходить.
- [ ] **G5 Bootstrap secret:** admin one-time токен НЕ зʼявляється в `docker logs`/journald (перевірка лог-виходу контейнера).
- [ ] **G6 Global in-flight cap:** server-wide cap до signal-cli тримає під флудом повільних send; понад → `-32005` (pending-dict не росте unbounded).
- [ ] **G7 own-number listGroups:** після own-number finishLink `listGroups` повертає реальні групи (one-shot sync) АБО поведінка manual-receive задокументована.
- [ ] **G8 PoP nonce:** релей підпису з одного сокета на інший (PoP-pending) → відмова; nonce one-time + per-conn binding.
- [ ] **G9 Shared-bot groups:** `listGroups` спільного бота юзером-не-admin → admin-only-відмова АБО задокументований accepted-risk.
- [ ] **G10 Upgrade migration:** інстанс, апгрейднутий з Phase-1, — legacy account видно лише admin (не orphan, не всім).
- [ ] **G12 Invite cap concurrency:** паралельні guesses одного коду інкрементять лічильник атомарно (cap не обходиться гонкою); порівняння constant-time.
- [ ] **V1 Bump-передумова:** `JSON-RPC.NET` = 2.7.0 у `csproj`; білд зелений після міграції breaking-2.0.0; `Authorization/`+`MaxConcurrentConnections`+`WsRpcServerDiagnostics` реально доступні з пакета (не лише local-source).
- [ ] **V8 Principal не в адаптері:** per-user стан не тримається в `Signal*RpcAdapter` (вони root-resolved/shared); principal читається per-invocation з call-context — тест, що два конекти різних identity не бачать стан одне одного через адаптер.
- [ ] **V9 Chokepoint у сесії:** `SignalRpcSession` конструює `AuthorizingJsonRpc` (не голий `new JsonRpc`); нові DTO в serializer-context.
- [ ] Security-рев'ю дифу (`cavecrew-reviewer` + ручний погляд на межі ізоляції).

---

## Фаза 3 — Операційна готовність + release

**Мета:** production-ready: діагностика/метрики, відновлення, логування, опційно приймання подій, release-пайплайн.

### Задачі
1. **Метрики/діагностика** — вшити `WsRpcServerDiagnostics` (лічильники конектів, RPC-викликів, помилок).
   → `cavecrew-builder`.
2. **Структуровані логи + таксономія помилок** на RPC-межі (єдиний формат `RpcErrorException`). **Redact-allowlist:** токен-несні params (`authenticate`), `Sec-WebSocket-Protocol`/`Authorization`, device-link URI, номери, тіла повідомлень — НЕ логувати (логування params на RPC-межі = третій вектор витоку токена).
   → `general-purpose`.
3. **Контракт зʼєднання + reconnect/backoff** для клієнта — задокументувати: MVP connect-on-demand (send-only); персист+keep-alive+exponential-backoff-with-jitter лише з receive; поведінка при падінні signal-cli; реакція на `429`/`Retry-After`. **G13:** зафіксувати одиниці `Retry-After` (s vs ms) + вимагати **jittered** backoff — інакше connect-on-demand клієнти sync-retry-ять разом → thundering herd.
   → docs-задача.
4. **Perf-gate перед оптимізацією IPC.** Load-test pipe під реальним сплеском **перш ніж** щось чіпати. Базлайн: SignalCli.NET уже async/TCS/bounded-channel (не bottleneck — див. «Уточнення з рев'ю»). Socket-режим signal-cli — лише якщо тест докаже потребу.
   → `general-purpose` (load-smoke + рішення).
5. **(Опційно) Receive/events** — `ISignalEventsRpc` (subscribe), якщо нотіфам потрібні delivery-receipts чи вхідні. Інакше відкласти. **Caveat:** один stdout-loop + notification-channel `Wait` → повільний споживач робить head-of-line блок і RPC-відповідям; передбачити окремий споживач / drop-policy.
   → `Explore` (оцінити events-шар) → рішення робити/відкласти.
6. **Тести fail-path:** signal-cli впав посеред роботи → сервер відновлюється; невалідний ввід → коректна RPC-помилка; **реактивний Signal-side rate-limit/captcha/proof-required** від signal-cli → сервер детектить, піднімає й **паузить бот** (не довбати → жорсткіший бан), сурфейсить стан клієнту/ops. **Звірено: SignalCli.NET уже кидає типізовані — ловити `RateLimitException` (-5), `CaptchaRequiredException` (-6, enum-значення `CaptchaRejected`), `UntrustedIdentityException` (-4, зміна safety-number отримувача → send падає; нотіф губиться → сурфейсити, не ретраїти наосліп). (Звірка-3 V10: catch лише `JsonRpcException` пропускає `TimeoutException`/`OperationCanceledException`/`InvalidOperationException`("null response")/`ObjectDisposedException` — ловити явно; `IdentityChangedException` `[Obsolete]`/не диспатчиться — не ловити; `GroupAdminRequiredException` через крихку евристику `-1`+substring "admin".)**
   → `general-purpose`.
7. **CI:** build + test + docker publish.
   → `general-purpose`.
8. **Docs:** `docs/ops-runbook.md` + повний API-reference усіх RPC-методів.

### Умови зупинки (DoD Фази 3)
- [ ] e2e: вбити signal-cli посеред прогону → сервер відновлюється; метрики показують лічильники; смоук на N конектів.
- [ ] **Signal-side rate-limit:** signal-cli повертає proof-required/captcha/`429` → бот паузиться, стан сурфейситься, без долбання.
- [ ] Fail-path тести зелені; CI повний (build+test+publish) зелений.
- [ ] `docs/ops-runbook.md` + API-reference готові.
- [ ] Фінальне рев'ю гілки.

---

## Порядок і залежності

```
Фаза 1 (self-host) ──► [task-0: bump JSON-RPC.NET 1.1.0→2.7.0 + breaking-2.0.0] ──► Фаза 2 (shared-bot, повна ізоляція) ──► Фаза 3 (ops/release)
        │                          (Звірка-3 V1 — БЛОКЕР, без нього Фаза 2 не компілюється)
        └─ Фаза 3 п.1-2 (метрики/логи) можна тягнути паралельно після Фази 1 — але WsRpcServerDiagnostics теж 2.7.0-only → теж за task-0
```

Кожна фаза мерджиться в `main` лише після свого DoD. Жодна фаза не «майже готова» — або всі три умови (e2e + тести + докси), або не закрита.
