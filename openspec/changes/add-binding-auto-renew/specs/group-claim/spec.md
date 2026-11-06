# group-claim — delta

## MODIFIED Requirements

### Requirement: Валідність binding при зміні членства
Binding SHALL нести anchorAci — ACI вставника claim-коду, зафіксований сканером при confirm
(T2: identity сама по собі не має Signal-ідентифікатора, тому «членство identity» без anchor
неперевірюване). Перед КОЖНИМ send через спільний бот SHALL перевірятись
`anchorAci ∈ members(group)`: якщо anchor більше не член — send SHALL відхилятись і binding
SHALL інвалідуватись. Member-list MAY кешуватись із коротким TTL (~60с) з інвалідизацією по
group-update подіях авто-receive. Binding SHALL мати TTL як backstop; з увімкненим автоподовженням
TTL поновлюється, поки anchor член групи, інакше спливання форсує re-claim.

#### Scenario: ex-member (anchor = сам U)
- **WHEN** U вставив власний код у G (anchor = U), а потім вийшов/вигнаний із G
- **THEN** наступний send через бот у G — відмова; binding інвалідований (не безстроковий доступ)

#### Scenario: anchor-поручитель вийшов (proxy-випадок)
- **WHEN** код U вставив P (anchor = P), і P вийшов/вигнаний із G
- **THEN** send U у G — відмова; binding інвалідований — право U живе з членством поручителя

## ADDED Requirements

### Requirement: Автоподовження прив'язки до спливу TTL
Коли `AutoRenewBindings` увімкнено (дефолт), шлюз SHALL поновлювати активну прив'язку, що спливає у межах
вікна поновлення, до `now + BindingTtlHours` лише якщо anchor підтверджено членом групи хоча б на одному
shared-bot акаунті. Поновлення SHALL NOT відновлювати відкликану, прострочену чи перепривʼязану прив'язку.

#### Scenario: тиха група з anchor на місці
- **WHEN** прив'язка спливає за 3 дні, а anchor досі член групи
- **THEN** строк прив'язки стає `now + BindingTtlHours`, і send після початкових 30 днів проходить

#### Scenario: anchor вийшов
- **WHEN** прив'язка у вікні поновлення, а anchor уже не член групи
- **THEN** строк не змінюється; прив'язка спливає сама або відкликається send-gate на першому send

#### Scenario: revoke під час проходу
- **WHEN** прив'язку відкликали між читанням і записом поновлення
- **THEN** умовний запис нічого не змінює; прив'язка лишається відкликаною

#### Scenario: членство невизначене
- **WHEN** signal-cli не відповів на запит членства
- **THEN** поновлення відкладено до наступного проходу, строк не змінюється

#### Scenario: автоподовження вимкнено
- **WHEN** `AutoRenewBindings=false`
- **THEN** прив'язка спливає через `BindingTtlHours` і потребує re-claim, як раніше
