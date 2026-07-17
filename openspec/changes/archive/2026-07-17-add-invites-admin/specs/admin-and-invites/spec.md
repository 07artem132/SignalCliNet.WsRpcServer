# admin-and-invites — delta

## ADDED Requirements

### Requirement: Інвайт-гейт
Онбординг identity SHALL вимагати one-time інвайт-код ≥96-bit CSPRNG з per-code attempt cap
(атомарний інкремент) та атомарним consume-and-bind.

#### Scenario: паралельний redeem
- **WHEN** два клієнти одночасно redeem-лять один код
- **THEN** рівно один отримує identity; другий — відмову

### Requirement: Admin bootstrap без витоку
Перший старт SHALL ідемпотентно створити admin-identity; admin-креденшел SHALL записуватись
лише у `0600`-файл на захищеному томі й НІКОЛИ у stdout/логи контейнера.

#### Scenario: перший старт генерує admin bootstrap
- **WHEN** сервер стартує вперше й admin-identity ще не існує
- **THEN** admin-credential (cert-bundle) записується лише у `0600`-файл на захищеному томі; жоден байт credential не потрапляє у stdout чи логи контейнера (G5)

### Requirement: Admin-автентифікація mTLS
Адмін SHALL автентифікуватись mTLS client-cert від CA сервера → `role=admin`;
admin-методи для не-admin principal SHALL повертати `-32001`.

#### Scenario: валідний client-cert від серверної CA
- **WHEN** клієнт конектиться на admin-порт із mTLS client-cert, підписаним CA сервера
- **THEN** з'єднання отримує principal `role=admin`

#### Scenario: не-admin викликає admin-метод
- **WHEN** principal без `role=admin` викликає admin-метод
- **THEN** відповідь `-32001`

### Requirement: Audit-trail
Сервер SHALL фіксувати security-події (лінк бот-девайсу, redeem інвайту, промоушн admin,
revoke, admin-перегляд усіх акаунтів) у tamper-evident audit-лог (hash-chain) без
токенів/номерів у тілі запису.

#### Scenario: security-подія лягає в audit-лог
- **WHEN** відбувається security-подія (напр. redeem інвайту або admin-перегляд усіх акаунтів)
- **THEN** запис додається до tamper-evident audit-логу (hash-chain), без токенів чи номерів у тілі

#### Scenario: спроба підробити запис
- **WHEN** хтось намагається змінити чи видалити існуючий запис audit-логу
- **THEN** розрив hash-chain виявляється при перевірці — журнал tamper-evident
