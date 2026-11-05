# Tasks: add-team-notify

## 0. Рішення
- [ ] 0.1 Coalescing: перша одразу + решта в кінці вікна (пропозиція) чи все в кінці вікна
- [ ] 0.2 `sendTextMessage` для членів: перехідний прапорець чи закрити одразу
- [ ] 0.3 Хто видає інвайти в команду: лише admin чи і team-admin

## 1. Стор
- [ ] 1.1 Міграція: `teams`, `identities.team_id`, `invites.team_id/max_redemptions/redemptions` (атомарний інкремент, W20)
- [ ] 1.2 `team_bindings (team_id, alias, group_id, anchor_aci, expires_at, revoked)`, унікальність `(team_id, alias)`
- [ ] 1.3 `notify_dedup (team_id, alias, dedup_key, status, result_json, created_at)` + purge за TTL

## 2. Команда й інвайти
- [ ] 2.1 admin-RPC `createTeam`, `createInvite(teamId, maxRedemptions)`; дефолт 1 = нинішня поведінка
- [ ] 2.2 `redeemInvite` створює identity в команді інвайту
- [ ] 2.3 `requestGroupClaim(alias?)` → командний binding; перепривʼязка alias атомарна

## 3. Notify
- [ ] 3.1 Каталог шаблонів: формат, завантаження, fail-fast валідація, `listNotifyKinds`
- [ ] 3.2 `notify`: alias команди → схема params → dedup → coalescer → `GroupSendGate`
- [ ] 3.3 `login:<id>`: перевірка «рівно двоє» через `GroupMemberCache`
- [ ] 3.4 Роль `raw-send`; `sendTextMessage` за роллю (+ прапорець за рішенням 0.2)
- [ ] 3.5 RPC-політика для нових методів у `SignalRpcPolicyRegistry` (герметичний chokepoint)

## 4. Privacy й ретеншн
- [ ] 4.1 Логи без params, dedupKey, alias-id і тексту; лише kind, статус, код помилки
- [ ] 4.2 Purge dedup/audit/abuse за строком

## 5. Тести й доки
- [ ] 5.1 Тест на кожен сценарій із `specs/team-notify/spec.md` (FakeTimeProvider для вікна й TTL)
- [ ] 5.2 `docs/self-host.md`: формат каталогу, нові налаштування; `docs/shared-bot.md`: командні alias
- [ ] 5.3 Версія 2.3.0 + CHANGELOG
