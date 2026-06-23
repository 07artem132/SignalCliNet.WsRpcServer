#!/bin/bash
# SessionStart hook для Claude Code on the web (SignalCliNet.WsRpcServer — application).
#
# Що робить:
#   1) Лише у віддаленому середовищі (CLAUDE_CODE_REMOTE=true) — інакше виходить тихо.
#   2) Ставить .NET 10 SDK через apt (застосунок таргетить net10.0).
#   3) Намагається прогріти NuGet-кеш (restore), але ТОЛЕРАНТНО до помилок:
#      upstream-пакети (SignalCli.NET, JSON-RPC.NET, SignalCli.Runtime) лежать у
#      ПРИВАТНОМУ GitHub Packages feed-і й потребують токена `read:packages`.
#      У свіжому контейнері без токена restore цих трьох пакетів впаде — це очікувано.
#      Офлайн-шлях: зібрати залежності з sibling-репозиторіїв через
#      deploy/build-local-feed.sh (див. deploy/DEPLOYMENT.md).
#
# Ідемпотентно: повторні запуски — no-op, якщо SDK уже встановлено.

set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
    exit 0
fi

REPO_DIR="${CLAUDE_PROJECT_DIR:-$(pwd)}"
cd "$REPO_DIR"

echo "[session-start] Готую середовище для SignalCliNet.WsRpcServer у $REPO_DIR"

# 1) .NET 10 SDK (застосунок таргетить net10.0).
if ! command -v dotnet >/dev/null 2>&1; then
    echo "[session-start] Встановлюю dotnet-sdk-10.0 (apt)..."
    SUDO_PREFIX=""
    if [ "$(id -u)" != "0" ] && command -v sudo >/dev/null 2>&1; then
        SUDO_PREFIX="sudo"
    fi
    $SUDO_PREFIX apt-get update -qq || true
    $SUDO_PREFIX env DEBIAN_FRONTEND=noninteractive \
        apt-get install -y --no-install-recommends dotnet-sdk-10.0
else
    echo "[session-start] dotnet уже встановлено: $(dotnet --version)"
fi

export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
echo 'export DOTNET_CLI_TELEMETRY_OPTOUT=1' >> "${CLAUDE_ENV_FILE:-/dev/null}" 2>/dev/null || true
echo 'export DOTNET_NOLOGO=1' >> "${CLAUDE_ENV_FILE:-/dev/null}" 2>/dev/null || true

# 2) Best-effort restore. --ignore-failed-sources не зробить приватний feed доступним,
# але дасть restore'у не падати на нього й прогріти все, що публічне.
echo "[session-start] dotnet restore (best-effort; приватний feed може бути недоступний без токена)..."
dotnet restore --nologo --ignore-failed-sources || \
    echo "[session-start] УВАГА: restore не завершився — найімовірніше потрібен GITHUB_TOKEN (read:packages) " \
         "або локальний feed (deploy/build-local-feed.sh). Див. deploy/DEPLOYMENT.md."

echo "[session-start] Готово. Швидкі команди:"
echo "  dotnet build --configuration Release"
echo "  dotnet run --project src/SignalCliNet.WsRpcServer"
