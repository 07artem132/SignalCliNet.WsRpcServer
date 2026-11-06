# add-binding-auto-renew

## Чому
Прив'язка групи (per-identity і командний alias) живе `BindingTtlHours` (30 днів) і потім мовчки спливає.
Споживач «Потужність» (`docs/notifications-signal-design.md`, PR #908) шле в деякі групи рідко: після 30 днів
тиші перше ж важливе сповіщення отримує `-32007`, а team-admin мусить вручну прив'язати групу знову. Backstop
TTL задумувався як захист на випадок зміни членства, але цю гарантію вже дає anchor-перевірка
`anchorAci ∈ members(group)` на кожному send.

## Що змінюється
- **Фонове поновлення.** `GroupBindingRenewalService` (hosted) раз на прохід (≤ 1 год) бере активні прив'язки, що
  спливають у межах `BindingRenewWindowHours` (дефолт 168 год, у рантаймі ≤ половини TTL), і перевіряє anchor
  через `GroupMemberCache` на shared-bot акаунтах. Anchor член → `expires_at = now + BindingTtlHours`.
- **Fail-closed.** Anchor не член на жодному shared-bot акаунті → не поновлюємо і не відкликаємо (спливе сама;
  send-gate відкличе на першій спробі). Транзієнт або збій `listAccounts` → пропуск до наступного проходу.
- **Атомарний запис.** `TryExtendGroupBinding` / `TryExtendTeamBinding` — умовний `UPDATE` (не revoked, не expired,
  новий строк пізніший; для alias ще й та сама група/anchor). Паралельний revoke чи перепривʼязка виграє.
- **Опції** `Server:GroupClaim:AutoRenewBindings` (дефолт `true`) і `BindingRenewWindowHours` (1..8760, дефолт 168).
  `AutoRenewBindings=false` повертає строгий backstop. Інертно, коли `GroupClaim:Enabled=false`.

## Що НЕ змінюється
Wire-протокол, коди помилок, схема БД (без міграції), quota, anchor-модель, send-gate.

## Вплив
Нові методи `IDurableStore` (`GetGroupBindingsExpiringBefore`, `TryExtendGroupBinding`,
`GetTeamBindingsExpiringBefore`, `TryExtendTeamBinding`). Навантаження: ≤ 1 `listAccounts` + `listGroups` на
shared-bot акаунт за прохід (member-list кешується ~60 с), лише коли є прив'язки у вікні.
