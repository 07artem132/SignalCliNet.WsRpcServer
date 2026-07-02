# self-host-deployment (as-built, Фаза 1)

## Requirements

### Requirement: Контейнеризація
Репозиторій SHALL надавати `deploy/Dockerfile*` + `docker-compose.yml` (JDK 25 Temurin,
signal-cli 0.14.3); офлайн-збірка — через sibling-репо + `deploy/build-local-feed.sh`.

### Requirement: Health-ендпоінт
Сервер SHALL відповідати на health/readiness-пробу без auth, але без внутрішнього
стану у відповіді (нема акаунтів/номерів/конфігу; не DoS-ампліфікатор).

### Requirement: Тести та CI
Кожен RPC-метод SHALL мати інтеграційний тест (happy + ≥1 негативний) у `tests/`;
CI (`build.yml`) — build + test.

### Requirement: Документація self-host
`docs/self-host.md` SHALL описувати підняття сервера та таблицю конфігу
(`Server:Host/Port`, `SignalCli:*`).
