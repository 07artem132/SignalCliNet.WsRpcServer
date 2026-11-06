# Tasks: add-binding-auto-renew

## 0. Рішення
- [x] 0.1 Поновлення на шлюзі, а не в клієнті: TTL і anchor-перевірка живуть тут; клієнт лише показує строк (власник, 2026-11-06)
- [x] 0.2 Сигнал «anchor вийшов» і `core.layers-access` — поза скоупом (власник, 2026-11-06)

## 1. Стор
- [x] 1.1 `GetGroupBindingsExpiringBefore` — лише найновіший рядок на `(identity, group)`; `GetTeamBindingsExpiringBefore`
- [x] 1.2 `TryExtendGroupBinding` / `TryExtendTeamBinding` — умовний `UPDATE`, revoke і перепривʼязка виграють

## 2. Сервіс
- [x] 2.1 `GroupBindingRenewalService`: вікно ≤ TTL/2, інтервал ≤ 1 год і ≤ вікно/2, `PeriodicTimer` на `TimeProvider`
- [x] 2.2 Anchor-перевірка на всіх shared-bot акаунтах: Member → поновити; NotMember → не чіпати; Unknown → відкласти
- [x] 2.3 Опції `AutoRenewBindings`, `BindingRenewWindowHours` + `appsettings.json`

## 3. Тести й документація
- [x] 3.1 `GroupBindingRenewalServiceTests`: вікно, anchor вийшов, транзієнт, лише shared-bot, revoked/expired/superseded, alias і застаріла перепривʼязка, повторний прохід, межі вікна, вимкнено
- [x] 3.2 `docs/self-host.md`, `CHANGELOG.md` `[Unreleased]`
