#!/usr/bin/env bash
# Авто-версіонер: запускається з .github/workflows/auto-release.yml на КОЖЕН push у main
# (тобто на кожен merge PR). Модель та сама, що в PRC-Player/PRC-Linux: merge — це і є
# авторизація релізу, окремого ручного кроку (тег, dispatch) більше нема.
#
#   scripts/auto-release.sh [--dry-run]
#     --dry-run : лише обчислити й надрукувати версію/тег; нічого не комітити й не пушити.
#                 Це непривілейований probe, яким job `plan` вирішує, чи потрібен реліз узагалі.
#
# Як обирається версія (від останнього тегу v*):
#   1. Змін у shipped-поверхні нема (лише docs/тести/CI/агентські файли, див. EXEMPT) → зелений no-op.
#   2. Версія у файлі > останнього тегу → автор PR підняв її сам (разом із CHANGELOG) — тегуємо HEAD
#      цією версією, без додаткового коміту.
#   3. Версія у файлі == останньому тегу → conventional-commit bump з комітів після тегу
#      (`!:`/BREAKING CHANGE → major, feat → minor, решта → patch); коміт `release: X.Y.Z` з новою
#      версією (і секцією CHANGELOG, якщо репозиторій його веде), потім тег.
#   4. Версія у файлі < останнього тегу → помилка (хтось відкотив версію).
#
# Опубліковані теги незмінні: скрипт відмовляється перетегувати наявну версію; поганий реліз
# котиться вперед і «спалює» номер.
#
# Захист від циклу: CI пушить коміт і тег через GITHUB_TOKEN, а такі push-і не створюють нових
# workflow-run-ів. Плюс auto-release.yml відсікає push, у якого subject починається з `release: ` —
# тримай цей префікс синхронним з workflow, якщо колись заміниш токен на PAT.
set -euo pipefail

DRY=0
case "${1:-}" in
  --dry-run) DRY=1 ;;
  "") ;;
  *) echo "usage: auto-release.sh [--dry-run]" >&2; exit 2 ;;
esac

cd "$(dirname "$0")/.."

# --- Налаштування репозиторію -------------------------------------------------------------------
PRODUCT="SignalCliNet.WsRpcServer"
# "файл|MSBuild-властивість" — усі місця, де живе версія; перше — джерело істини.
VERSION_FILES=("src/SignalCliNet.WsRpcServer/SignalCliNet.WsRpcServer.csproj|AssemblyVersion"
               "src/SignalCliNet.WsRpcServer/SignalCliNet.WsRpcServer.csproj|FileVersion")
# Шлях до CHANGELOG.md або порожньо, якщо репозиторій його не веде (тоді нотатки — --generate-notes).
CHANGELOG="CHANGELOG.md"
# Не shipped-поверхня: зміни лише тут релізу не потребують.
# example/ і deploy/ — shipped: AdminCli (wsrpc-admin) і quickstart/bundle ідуть у реліз.
# global.json — пін SDK для збірки, не shipped-поверхня (пакет/бінарі від нього не змінюються).
EXEMPT=(':!global.json' ':!docs' ':!*.md' ':!.claude' ':!.github' ':!openspec' ':!scripts' ':!tests' ':!PLAN-*')
# ------------------------------------------------------------------------------------------------

emit() {  # emit <key> <value> — step output для workflow; поза Actions — no-op.
  if [ -n "${GITHUB_OUTPUT:-}" ]; then printf '%s=%s\n' "$1" "$2" >> "$GITHUB_OUTPUT"; fi
}

read_version() {
  local file="${VERSION_FILES[0]%%|*}" prop="${VERSION_FILES[0]##*|}"
  sed -n "s|.*<$prop>\([0-9]*\.[0-9]*\.[0-9]*\)</$prop>.*|\1|p" "$file" | head -1
}

write_version() {
  local entry file prop
  for entry in "${VERSION_FILES[@]}"; do
    file="${entry%%|*}"; prop="${entry##*|}"
    sed -i -E "s|(<$prop>)[0-9]+\.[0-9]+\.[0-9]+(</$prop>)|\1$1\2|" "$file"
    grep -qF "<$prop>$1</$prop>" "$file" || { echo "auto-release FAIL: не вдалося оновити $prop у $file" >&2; exit 1; }
  done
}

ver_gt() {  # ver_gt A B — true, якщо A > B (semver X.Y.Z)
  [ "$1" != "$2" ] && [ "$(printf '%s\n%s\n' "$1" "$2" | sort -V | tail -1)" = "$1" ]
}

compute_bump() {  # compute_bump <cur> <range>
  local cur="$1" range="$2" level=patch subjects bodies
  subjects="$(git log --no-merges --format='%s' "$range" -- . "${EXEMPT[@]}")"
  bodies="$(git log --no-merges --format='%b' "$range" -- . "${EXEMPT[@]}")"
  if grep -qE '^[a-z]+(\([^)]*\))?!:' <<<"$subjects" || grep -qE '^BREAKING[ -]CHANGE:' <<<"$bodies"; then
    level=major
  elif grep -qE '^feat(\([^)]*\))?:' <<<"$subjects"; then
    level=minor
  fi
  local major minor patch
  IFS=. read -r major minor patch <<<"$cur"
  case "$level" in
    major) echo "$((major + 1)).0.0" ;;
    minor) echo "$major.$((minor + 1)).0" ;;
    patch) echo "$major.$minor.$((patch + 1))" ;;
  esac
}

update_changelog() {  # update_changelog <version> <range>
  [ -n "$CHANGELOG" ] || return 0
  local version="$1" range="$2" today notes
  today="$(date -u +%Y-%m-%d)"
  notes="$(git log --no-merges --format='- %s (%h)' "$range" -- . "${EXEMPT[@]}")"
  VERSION="$version" TODAY="$today" NOTES="$notes" python3 -I - "$CHANGELOG" <<'PY'
import os, re, sys
path = sys.argv[1]
text = open(path, encoding="utf-8").read()
version, today, notes = os.environ["VERSION"], os.environ["TODAY"], os.environ["NOTES"]
header = f"## [{version}] — {today}"
m = re.search(r"^## \[Unreleased\][^\n]*\n(.*?)(?=^## \[|\Z)", text, re.M | re.S)
if m and m.group(1).strip():
    # Є написаний руками [Unreleased] — він і стає секцією релізу; новий [Unreleased] порожній.
    text = text[:m.start()] + "## [Unreleased]\n\n" + header + "\n" + m.group(1) + text[m.end():]
else:
    section = (f"{header}\n\nАвтоматичний реліз (`scripts/auto-release.sh`): conventional-commit bump "
               f"із комітів після попереднього тегу.\n\n{notes}\n\n")
    first = re.search(r"^## \[(?!Unreleased)", text, re.M)
    text = text[:first.start()] + section + text[first.start():] if first else text.rstrip() + "\n\n" + section
open(path, "w", encoding="utf-8").write(text)
PY
}

git fetch -q --tags --unshallow origin 2>/dev/null || git fetch -q --tags origin 2>/dev/null || true

LAST_TAG="$(git tag -l 'v[0-9]*' --sort=v:refname | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+$' | tail -1 | tr -d '\r' || true)"
if [ -z "$LAST_TAG" ]; then
  echo "auto-release FAIL: не видно жодного тегу v* — базовий тег ставиться руками, не цим скриптом." >&2
  exit 1
fi
LAST="${LAST_TAG#v}"

if git diff --quiet "$LAST_TAG"..HEAD -- . "${EXEMPT[@]}"; then
  echo "auto-release: змін у shipped-поверхні з $LAST_TAG нема — релізу не потрібно."
  emit release_owed false
  emit next_tag ""
  exit 0
fi

FILE_VER="$(read_version)"
[ -n "$FILE_VER" ] || { echo "auto-release FAIL: не прочитано версію з ${VERSION_FILES[0]}" >&2; exit 1; }

if ver_gt "$FILE_VER" "$LAST"; then
  MODE=as-is; NEXT="$FILE_VER"
elif [ "$FILE_VER" = "$LAST" ]; then
  MODE=bump; NEXT="$(compute_bump "$LAST" "$LAST_TAG..HEAD")"
else
  echo "auto-release FAIL: версія у файлі ($FILE_VER) менша за останній тег ($LAST_TAG)." >&2
  exit 1
fi
TAG="v$NEXT"

if git rev-parse -q --verify "refs/tags/$TAG" >/dev/null; then
  echo "auto-release FAIL: $TAG уже існує — опубліковані теги незмінні." >&2
  exit 1
fi

echo "auto-release: $LAST_TAG -> $TAG ($MODE)"
git diff --stat "$LAST_TAG"..HEAD -- . "${EXEMPT[@]}" | tail -8
emit release_owed true
emit next_tag "$TAG"

if [ "$DRY" = 1 ]; then
  echo "auto-release: --dry-run — нічого не змінено."
  exit 0
fi

git config user.name "github-actions[bot]"
git config user.email "41898282+github-actions[bot]@users.noreply.github.com"

if [ "$MODE" = bump ]; then
  write_version "$NEXT"
  update_changelog "$NEXT" "$LAST_TAG..HEAD"
  for entry in "${VERSION_FILES[@]}"; do git add "${entry%%|*}"; done
  [ -z "$CHANGELOG" ] || git add "$CHANGELOG"
  git commit -q -m "release: $NEXT" -m "Автоверсія scripts/auto-release.sh після merge у main: conventional-commit bump з $LAST."
fi

git tag -a "$TAG" -m "$PRODUCT $TAG — автоматичний реліз після merge у main ($MODE, попередній $LAST_TAG)."

# Один атомарний push коміту й тегу: або обидва, або нічого. Якщо main поїхав уперед лише
# неshipped-змінами (наприклад, бот coverage-бейджа з [skip ci]), переносимо реліз на нову вершину.
for attempt in 1 2 3; do
  if git push --atomic origin "HEAD:refs/heads/main" "refs/tags/$TAG:refs/tags/$TAG"; then
    emit released_tag "$TAG"
    emit released_sha "$(git rev-parse HEAD)"
    echo "auto-release: випущено $TAG"
    exit 0
  fi
  [ "$attempt" -lt 3 ] || break
  git fetch -q origin main
  if ! git diff --quiet HEAD...origin/main -- . "${EXEMPT[@]}"; then
    echo "auto-release FAIL: main отримав нові shipped-зміни під час релізу — їх випустить наступний прогін." >&2
    exit 1
  fi
  git tag -d "$TAG" >/dev/null
  if [ "$MODE" = bump ]; then
    git rebase -q origin/main
  else
    # Без власного коміту реліз переїжджає лише вперед по main, і лише якщо HEAD — його предок.
    git merge-base --is-ancestor HEAD origin/main || { echo "auto-release FAIL: HEAD не є предком origin/main." >&2; exit 1; }
    git checkout -q --detach origin/main
  fi
  git tag -a "$TAG" -m "$PRODUCT $TAG — автоматичний реліз після merge у main ($MODE, попередній $LAST_TAG)."
done
echo "auto-release FAIL: push не вдався після 3 спроб." >&2
exit 1
