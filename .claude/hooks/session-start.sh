#!/bin/bash
# SessionStart hook для Claude Code on the web (SignalCliNet.WsRpcServer — application).
#
# Що робить:
#   1) Лише у віддаленому середовищі (CLAUDE_CODE_REMOTE=true) — інакше виходить тихо.
#   2) Ставить .NET SDK з global.json через dotnet-install.sh (за наявності — пропускає).
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
# SDK пінено в global.json (Roslyn генератора JSON-RPC.NET = компілятор SDK 10.0.4xx). Distro-пакет
# dotnet-sdk-10.0 з apt — це feature band 10.0.1xx, він global.json не задовольняє, тож ставимо
# офіційним dotnet-install.sh у ~/.dotnet рівно ту версію, яку просить global.json.
if ! dotnet --version >/dev/null 2>&1; then
    echo "[session-start] SDK з global.json не знайдено — ставлю через dotnet-install.sh..."
    DOTNET_DIR="$HOME/.dotnet"
    if curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
        && bash /tmp/dotnet-install.sh --jsonfile "$REPO_DIR/global.json" --install-dir "$DOTNET_DIR" --no-path; then
        export DOTNET_ROOT="$DOTNET_DIR"
        export PATH="$DOTNET_DIR:$PATH"
        echo "export DOTNET_ROOT=\"$DOTNET_DIR\"" >> "${CLAUDE_ENV_FILE:-/dev/null}" 2>/dev/null || true
        echo "export PATH=\"$DOTNET_DIR:\$PATH\"" >> "${CLAUDE_ENV_FILE:-/dev/null}" 2>/dev/null || true
    else
        echo "[session-start] УВАГА: dotnet-install.sh не вдався (мережа?) — збірка впаде на global.json." >&2
    fi
fi
echo "[session-start] dotnet: $(dotnet --version 2>/dev/null || echo 'нема')"

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
