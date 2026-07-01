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

### Requirement: Admin-автентифікація mTLS
Адмін SHALL автентифікуватись mTLS client-cert від CA сервера → `role=admin`;
admin-методи для не-admin principal SHALL повертати `-32001`.

### Requirement: Audit-trail
Security-події (лінк бот-девайсу, redeem інвайту, промоушн admin, revoke, admin-перегляд
усіх акаунтів) SHALL лягати у tamper-evident audit-лог без токенів/номерів у тілі.
