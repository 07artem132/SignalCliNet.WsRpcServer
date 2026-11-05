# Tasks: add-team-notify

## 0. Рішення
- [x] 0.1 `sendTextMessage` лишається для всіх; шлюз текст не фільтрує, межа = bindings (власник, 2026-11-05)
- [x] 0.1a Перевірки «рівно двоє» в особистій групі немає (власник, 2026-11-05)
- [x] 0.2 Coalescing на шлюзі; dedup і coalescing — за чатом-адресатом (власник, 2026-11-05)
- [x] 0.3 Інвайти в команду видає team-admin; admin створює команду і перший team-admin інвайт (власник, 2026-11-05)

## 1. Стор
- [ ] 1.1 Міграція: `teams`, `identities.team_id`, `invites.team_id/max_redemptions/redemptions` (атомарний інкремент, W20)
- [ ] 1.2 `team_bindings (team_id, alias, group_id, anchor_aci, expires_at, revoked)`, унікальність `(team_id, alias)`
- [ ] 1.3 Ключ send-dedup `(groupId, messageId)` для групових recipient + purge за TTL
- [ ] 1.4 Coalescer за чатом: вікно `Notify:CoalesceWindowSeconds`, flush наприкінці вікна, переживає рестарт або чесно губить (рішення в design), FakeTimeProvider-тести

## 2. Команда й інвайти
- [ ] 2.1 admin-RPC `createTeam`, `createInvite(teamId, maxRedemptions, role)`; дефолт 1 = нинішня поведінка
- [ ] 2.1a team-RPC `createTeamInvite(maxRedemptions, ttl, role?)` для ролі `team-admin`, лише своя команда; через chokepoint
- [ ] 2.1b `setTeamRole` для team-admin; інваріант ≥1 team-admin у транзакції (`-32013`); break-glass адміна шлюзу + audit; стан «без адміна» в `listTeams`
- [ ] 2.2 `redeemInvite` створює identity в команді інвайту
- [ ] 2.3 `requestGroupClaim(alias?)` → командний binding; перепривʼязка alias атомарна

## 3. Send за alias
- [ ] 3.1 `RecipientValidator`: формат `alias:<all|admin|dir:<id>|login:<id>>`
- [ ] 3.2 Резолв alias → група команди в `GroupSendGate`; anti-oracle `-32007`
- [ ] 3.3 RPC-політика нових admin-методів у `SignalRpcPolicyRegistry`

## 4. Privacy
- [ ] 4.1 Логи без alias-id, messageId і тексту; лише статус і код помилки

## 5. Тести й доки
- [ ] 5.1 Тест на кожен сценарій із `specs/team-notify/spec.md`
- [ ] 5.2 `docs/self-host.md`, `docs/shared-bot.md`: команди, alias
- [ ] 5.3 Версія 2.3.0 + CHANGELOG
