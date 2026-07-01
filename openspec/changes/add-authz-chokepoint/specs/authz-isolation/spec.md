# authz-isolation — delta

## ADDED Requirements

### Requirement: Герметичний default-deny
Кожен RPC-виклик SHALL проходити один pre-dispatch фільтр; метод без явної політики
в декларативному реєстрі SHALL отримати `-32601` до будь-якого виконання.

#### Scenario: незареєстрований метод
- **WHEN** клієнт викликає метод, відсутній у реєстрі політик
- **THEN** відповідь `-32601`, цільовий код не виконувався

### Requirement: Анти-IDOR
Набір дозволених акаунтів SHALL виводитись із principal; `account`-аргумент клієнта
SHALL перевірятись `AssertAccountAllowed` на кожному account-bearing методі.

#### Scenario: чужий акаунт
- **WHEN** автентифікований A передає `account` користувача B
- **THEN** відмова авторизації; операція не досягає signal-cli

### Requirement: Видимість read-виходу
`AccountQuery`-відповіді SHALL фільтруватись outbound-фільтром fail-closed:
user бачить власні + shared-bot акаунти і ВИКЛЮЧНО claim'нуті/власні групи;
admin бачить усі акаунти (read-only нагляд, аудитований).

#### Scenario: виняток у фільтрі
- **WHEN** фільтр кидає виняток (malformed principal)
- **THEN** клієнт отримує порожній результат/помилку, ніколи нефільтровані дані

### Requirement: Версіонування контракту
Handshake SHALL нести `apiVersion`; несумісна версія SHALL діставати типізовану
помилку upgrade-required, а не тихий behavioral-break.
