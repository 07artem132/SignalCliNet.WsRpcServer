#!/usr/bin/env bash
# Шлюз сповіщень Signal однією командою (Linux-сервер з публічним IP і доменом):
#
#   curl -fsSL https://raw.githubusercontent.com/07artem132/SignalCliNet.WsRpcServer/main/deploy/quickstart/install.sh \
#     | sudo bash -s -- --domain notify.example.com --extension-id <id-розширення> --team "Моя команда"
#
# Що робить (повторний запуск безпечний — зроблене пропускається):
#   1. ставить Docker, якщо його нема;
#   2. кладе compose-стек у /opt/signal-notify (шлюз з релізного бандла + Caddy з Let's Encrypt);
#   3. показує QR для привʼязки номера Signal (бот), якщо номер ще не привʼязано;
#   4. запускає стек і чекає готовності;
#   5. створює команду та одноразовий інвайт team-admin (якщо задано --team).
#
# Детальна інструкція: deploy/quickstart/README.md
set -euo pipefail

REPO="07artem132/SignalCliNet.WsRpcServer"
REF="main"
DIR="/opt/signal-notify"
DOMAIN=""
VERSION=""
TEAM=""
DEVICE_NAME="signal-notify"
EXTENSION_IDS=()
SKIP_LINK=0

usage() {
  cat <<'EOF'
Використання: install.sh --domain <домен> [опції]

  --domain <домен>        домен шлюзу; DNS A/AAAA уже має вказувати на цей сервер (обовʼязково)
  --extension-id <id>     ID розширення Chrome (chrome://extensions → Деталі → ID). Можна кілька разів
                          або через кому. Без нього браузер не пустить: дозволені Origin порожні
  --team <назва>          створити команду й видати інвайт для її першого team-admin
  --device-name <назва>   як бот зветься в «Привʼязаних пристроях» телефона (дефолт signal-notify)
  --version <X.Y.Z>       версія шлюзу (дефолт — останній реліз)
  --dir <шлях>            куди встановити (дефолт /opt/signal-notify)
  --skip-link             не привʼязувати номер зараз
  --ref <git-ref>         з якої гілки/тега брати файли стека (дефолт main)
EOF
}

die() { echo "Помилка: $*" >&2; exit 1; }
step() { printf '\n==> %s\n' "$*"; }

while [ $# -gt 0 ]; do
  case "$1" in
    --domain) DOMAIN="${2:-}"; shift 2 ;;
    --extension-id) IFS=',' read -r -a ids <<< "${2:-}"; EXTENSION_IDS+=("${ids[@]}"); shift 2 ;;
    --team) TEAM="${2:-}"; shift 2 ;;
    --device-name) DEVICE_NAME="${2:-}"; shift 2 ;;
    --version) VERSION="${2#v}"; shift 2 ;;
    --dir) DIR="${2:-}"; shift 2 ;;
    --ref) REF="${2:-}"; shift 2 ;;
    --skip-link) SKIP_LINK=1; shift ;;
    -h|--help) usage; exit 0 ;;
    *) usage >&2; die "невідомий параметр: $1" ;;
  esac
done

[ -n "$DOMAIN" ] || { usage >&2; die "потрібен --domain"; }
[[ "$DOMAIN" =~ ^[A-Za-z0-9.-]+$ ]] || die "домен має бути без схеми й шляху, напр. notify.example.com"
for id in "${EXTENSION_IDS[@]}"; do
  # ID розширення Chrome — 32 літери a-p.
  [[ "$id" =~ ^[a-p]{32}$ ]] || die "невалідний --extension-id '$id' (очікую 32 літери a-p)"
done

# --- 1. Docker ---------------------------------------------------------------------------------------
step "Docker"
if ! command -v docker >/dev/null 2>&1; then
  [ "$(id -u)" -eq 0 ] || die "Docker не встановлено; запусти скрипт через sudo, щоб його поставити"
  echo "Docker не знайдено — ставлю офіційним скриптом get.docker.com"
  curl -fsSL https://get.docker.com | sh
fi
docker compose version >/dev/null 2>&1 || die "потрібен Docker Compose v2 (плагін 'docker compose')"
docker info >/dev/null 2>&1 || die "Docker daemon недоступний (запусти через sudo або додай користувача в групу docker)"
echo "ok: $(docker --version)"

# --- 2. Версія шлюзу ---------------------------------------------------------------------------------
if [ -z "$VERSION" ]; then
  VERSION="$(curl -fsSL "https://api.github.com/repos/${REPO}/releases/latest" \
    | grep -o '"tag_name": *"[^"]*"' | head -n1 | sed 's/.*"v\{0,1\}\([^"]*\)"$/\1/')"
  [ -n "$VERSION" ] || die "не вдалося визначити останній реліз; задай --version"
fi
echo "Версія шлюзу: $VERSION"

# --- 3. Файли стека ----------------------------------------------------------------------------------
step "Файли стека → $DIR"
mkdir -p "$DIR/tools"
# Локальні файли беремо лише коли скрипт запущено з файлу (клон репо); при `curl | bash` — качаємо з GitHub.
SRC_DIR=""
if [ -f "${BASH_SOURCE[0]:-}" ]; then SRC_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"; fi
for f in Dockerfile compose.yml Caddyfile tools/wsrpc-admin tools/signal-link; do
  if [ -n "$SRC_DIR" ] && [ -f "$SRC_DIR/$f" ] && [ "$SRC_DIR" != "$DIR" ]; then
    cp "$SRC_DIR/$f" "$DIR/$f"                       # запуск із клону репозиторію
  elif [ "$SRC_DIR" != "$DIR" ] || [ ! -f "$DIR/$f" ]; then
    curl -fsSL "https://raw.githubusercontent.com/${REPO}/${REF}/deploy/quickstart/$f" -o "$DIR/$f"
  fi
done
chmod 0755 "$DIR/tools/wsrpc-admin" "$DIR/tools/signal-link"

cat > "$DIR/.env" <<EOF
# Згенеровано install.sh. Після зміни: docker compose up -d --build
DOMAIN=$DOMAIN
WSRPC_VERSION=$VERSION
EOF

origins_json() {
  local out="" id
  for id in "${EXTENSION_IDS[@]}"; do out+="${out:+, }\"chrome-extension://$id\""; done
  printf '[%s]' "$out"
}

write_gateway_json() {
  cat > "$DIR/gateway.json" <<EOF
{
  "Server": {
    "Auth": {
      "Enabled": true,
      "AllowedOrigins": $(origins_json),
      "MaxUnauthenticatedPerIp": 64
    },
    "Admission": { "Enabled": true },
    "GroupClaim": { "Enabled": true }
  }
}
EOF
}

if [ ! -f "$DIR/gateway.json" ]; then
  write_gateway_json
elif [ ${#EXTENSION_IDS[@]} -gt 0 ]; then
  cp "$DIR/gateway.json" "$DIR/gateway.json.bak"
  write_gateway_json
  echo "gateway.json перезаписано з новими --extension-id (попередній — gateway.json.bak)"
fi
[ ${#EXTENSION_IDS[@]} -gt 0 ] || grep -q 'chrome-extension://' "$DIR/gateway.json" \
  || echo "Увага: не задано --extension-id — браузерне розширення шлюз поки не пустить (див. README, крок «Origin»)."

cd "$DIR"
# </dev/null: при `curl | bash` stdin — це сам скрипт; docker compose run/exec інакше «зʼїв» би його решту.
compose() { docker compose --project-directory "$DIR" "$@" </dev/null; }

if ! getent hosts "$DOMAIN" >/dev/null 2>&1; then
  echo "Увага: $DOMAIN поки не резолвиться. Caddy не отримає сертифікат, доки DNS не вкаже на цей сервер."
fi

# --- 4. Образ ----------------------------------------------------------------------------------------
step "Збірка образу шлюзу (бандл релізу v$VERSION, перший раз ~200 MB)"
compose build

# --- 5. Привʼязка номера -----------------------------------------------------------------------------
has_account() {
  compose run --rm -T --no-deps --entrypoint sh wsrpcserver -c \
    'grep -qs "\"number\"" /data/SignalCliStorageData/data/accounts.json'
}

if [ "$SKIP_LINK" -eq 0 ]; then
  if has_account; then
    step "Номер Signal уже привʼязано — пропускаю"
  else
    step "Привʼязка номера Signal (бот)"
    compose stop wsrpcserver >/dev/null 2>&1 || true
    compose run --rm -T --no-deps --entrypoint signal-link wsrpcserver "$DEVICE_NAME" \
      || die "привʼязка не вдалася; запусти install.sh ще раз, щоб отримати новий QR"
  fi
fi

# --- 6. Старт ----------------------------------------------------------------------------------------
step "Запуск стека"
compose up -d

echo -n "Чекаю на admin-канал шлюзу"
ready=0
for _ in $(seq 1 60); do
  if compose exec -T wsrpcserver wsrpc-admin ping >/dev/null 2>&1; then ready=1; break; fi
  echo -n "."; sleep 3
done
echo
[ "$ready" -eq 1 ] || die "шлюз не відповів за 3 хв; дивись логи: cd $DIR && docker compose logs wsrpcserver"

# listAccounts від адміна позначає непривʼязані номери як спільний бот (admin-owned, не приватний).
ACCOUNTS_JSON="$(compose exec -T wsrpcserver wsrpc-admin accounts)"
BOT_NUMBER="$(printf '%s\n' "$ACCOUNTS_JSON" | sed -n 's/.*"number": *"\([^"]*\)".*/\1/p' | head -n1)"

# --- 7. Команда й інвайт -----------------------------------------------------------------------------
INVITE_CODE=""
INVITE_EXPIRES=""
if [ -n "$TEAM" ]; then
  step "Команда «$TEAM»"
  TEAMS_JSON="$(compose exec -T wsrpcserver wsrpc-admin teams)"
  # Шукаємо команду з такою ж назвою (повторний запуск не плодить дублікатів).
  TEAM_ID="$(printf '%s\n' "$TEAMS_JSON" | TEAM="$TEAM" awk '
    /"teamId"/ { gsub(/.*"teamId": *"|".*/, ""); id=$0 }
    /"name"/   { line=$0; gsub(/.*"name": *"|",?[[:space:]]*$/, "", line); if (line == ENVIRON["TEAM"]) { print id; exit } }')"
  if [ -n "$TEAM_ID" ]; then
    echo "Команда вже існує: $TEAM_ID"
  else
    CREATED="$(compose exec -T wsrpcserver wsrpc-admin create-team "$TEAM")"
    TEAM_ID="$(printf '%s\n' "$CREATED" | sed -n 's/.*"teamId": *"\([^"]*\)".*/\1/p')"
    [ -n "$TEAM_ID" ] || die "не вдалося створити команду: $CREATED"
    echo "Створено команду: $TEAM_ID"
  fi
  INVITE_JSON="$(compose exec -T wsrpcserver wsrpc-admin invite --team "$TEAM_ID" --role team-admin)"
  INVITE_CODE="$(printf '%s\n' "$INVITE_JSON" | sed -n 's/.*"code": *"\([^"]*\)".*/\1/p')"
  INVITE_EXPIRES="$(printf '%s\n' "$INVITE_JSON" | sed -n 's/.*"expiresAt": *"\([^"]*\)".*/\1/p')"
  [ -n "$INVITE_CODE" ] || die "не вдалося видати інвайт: $INVITE_JSON"
fi

# --- Підсумок ----------------------------------------------------------------------------------------
cat <<EOF

================================================================================
Шлюз піднято.

  Адреса для розширення:  wss://$DOMAIN
  Номер бота:             ${BOT_NUMBER:-<не привʼязано — запусти install.sh ще раз без --skip-link>}
  Дозволені Origin:       $(grep -o 'chrome-extension://[a-p]*' "$DIR/gateway.json" | paste -sd ' ' - || echo "немає — див. README, крок «Origin»")
EOF
if [ -n "$INVITE_CODE" ]; then
  cat <<EOF

  Інвайт team-admin для «$TEAM» (одноразовий, до $INVITE_EXPIRES):
      $INVITE_CODE
  Передай його людині особисто, не через самого бота. Вона вводить код у налаштуваннях
  модуля сповіщень Потужності разом з адресою шлюзу й номером бота.
EOF
fi
cat <<EOF

Далі:
  - додай бота (номер вище) у Signal-групи, куди мають іти сповіщення;
  - team-admin у Потужності прив'язує групи до псевдонімів (all, dir:…, login:…).

Керування: cd $DIR && docker compose exec wsrpcserver wsrpc-admin --help
Логи:      cd $DIR && docker compose logs -f
================================================================================
EOF
