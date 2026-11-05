#!/usr/bin/env bash
#
# Збирає «all-in-one» бандл для одного Linux-RID: self-contained сервер (уже опублікований у
# PUBLISH_DIR) + Eclipse Temurin JRE 25 + (для arm64) нативний libsignal_jni під aarch64.
# Такий бандл запускається без системної Java — його качає Home Assistant-інтеграція
# (ha-signal-wsrpc, managed mode) і стартує як subprocess.
#
#   deploy/bundle-linux.sh <x64|arm64> <PUBLISH_DIR> <OUT_TARBALL>
#
# Довіра до завантажень:
#   * JRE — офіційний Adoptium API; SHA-256 береться з тієї ж API-відповіді й перевіряється.
#   * libsignal_jni (aarch64) — signal-cli офіційно постачає лише linux x86_64 у jar'і; aarch64
#     беремо з community-збірок exquo/signal-libs-build під ТУ САМУ версію libsignal-client, що в jar.
#     SHA-256 друкується в лог і записується в BUNDLE-VERSIONS.txt (аудит). Версія виводиться з імені
#     jar'а, тож bump signal-cli автоматично тягне відповідний libsignal.
#   * Підтримується лише glibc (musl/Alpine — ні: libsignal не має musl-збірок).
set -euo pipefail

ARCH="${1:?arch: x64|arm64}"
PUBLISH_DIR="${2:?publish dir}"
OUT="${3:?output tarball}"

case "$ARCH" in
  x64)   ADOPT_ARCH="x64";     LIBSIGNAL_TRIPLE="" ;;
  arm64) ADOPT_ARCH="aarch64"; LIBSIGNAL_TRIPLE="aarch64-unknown-linux-gnu" ;;
  *) echo "unknown arch: $ARCH" >&2; exit 1 ;;
esac

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
STAGE="$WORK/stage"
cp -a "$PUBLISH_DIR" "$STAGE"
VERSIONS="$STAGE/BUNDLE-VERSIONS.txt"
: > "$VERSIONS"

# --- 1. Temurin JRE 25 -----------------------------------------------------------------------
echo "==> JRE 25 ($ADOPT_ARCH)"
curl -fsSL "https://api.adoptium.net/v3/assets/latest/25/hotspot?architecture=${ADOPT_ARCH}&image_type=jre&os=linux&vendor=eclipse" \
  -o "$WORK/jre.json"
JRE_URL="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[0]["binary"]["package"]["link"])' "$WORK/jre.json")"
JRE_SHA="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[0]["binary"]["package"]["checksum"])' "$WORK/jre.json")"
JRE_VER="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))[0]["version"]["semver"])' "$WORK/jre.json")"
curl -fsSL "$JRE_URL" -o "$WORK/jre.tar.gz"
echo "${JRE_SHA}  $WORK/jre.tar.gz" | sha256sum -c -
mkdir -p "$STAGE/jre"
tar -xzf "$WORK/jre.tar.gz" -C "$STAGE/jre" --strip-components=1
test -x "$STAGE/jre/bin/java"
echo "jre=temurin-${JRE_VER} sha256=${JRE_SHA}" >> "$VERSIONS"

# --- 2. libsignal_jni для aarch64 -------------------------------------------------------------
JAR="$(find "$STAGE/signal-cli/lib" -maxdepth 1 -name 'libsignal-client-*.jar' | head -n1)"
test -n "$JAR"
LIBSIGNAL_VER="$(basename "$JAR" .jar)"; LIBSIGNAL_VER="${LIBSIGNAL_VER#libsignal-client-}"
echo "libsignal-client=${LIBSIGNAL_VER}" >> "$VERSIONS"

if [ -n "$LIBSIGNAL_TRIPLE" ]; then
  echo "==> libsignal_jni ${LIBSIGNAL_VER} (${LIBSIGNAL_TRIPLE})"
  gh api "repos/exquo/signal-libs-build/releases?per_page=100" > "$WORK/releases.json"
  ASSET_URL="$(python3 - "$WORK/releases.json" "$LIBSIGNAL_VER" "$LIBSIGNAL_TRIPLE" <<'PY'
import json, sys
releases, ver, triple = json.load(open(sys.argv[1])), sys.argv[2], sys.argv[3]
for rel in releases:
    if "libsignal" not in rel["tag_name"] or ver not in rel["tag_name"]:
        continue
    for asset in rel["assets"]:
        name = asset["name"]
        if "libsignal_jni" in name and triple in name and name.endswith(".tar.gz"):
            print(asset["browser_download_url"]); sys.exit(0)
sys.exit(f"no exquo/signal-libs-build asset for libsignal {ver} {triple}")
PY
)"
  curl -fsSL "$ASSET_URL" -o "$WORK/libsignal.tar.gz"
  LIB_SHA="$(sha256sum "$WORK/libsignal.tar.gz" | cut -d' ' -f1)"
  echo "libsignal_jni aarch64: $ASSET_URL sha256=$LIB_SHA"
  mkdir -p "$WORK/libsignal"
  tar -xzf "$WORK/libsignal.tar.gz" -C "$WORK/libsignal"
  SO="$(find "$WORK/libsignal" -name 'libsignal_jni*.so' | head -n1)"
  test -n "$SO"
  # libsignal шукає ресурс libsignal_jni_<arch>.so всередині jar'а.
  cp "$SO" "$WORK/libsignal_jni_aarch64.so"
  (cd "$WORK" && zip -q "$JAR" libsignal_jni_aarch64.so)
  # amd64-бібліотеку з arm64-бандла прибираємо (~170 МБ мертвої ваги).
  zip -q -d "$JAR" 'libsignal_jni_amd64.so' 'libsignal_jni_amd64.dylib' 'libsignal_jni_aarch64.dylib' 'signal_jni_amd64.dll' >/dev/null || true
  echo "libsignal_jni_aarch64=${ASSET_URL} sha256=${LIB_SHA}" >> "$VERSIONS"
else
  zip -q -d "$JAR" 'libsignal_jni_amd64.dylib' 'libsignal_jni_aarch64.dylib' 'signal_jni_amd64.dll' >/dev/null || true
fi

# --- 3. Архів ---------------------------------------------------------------------------------
chmod +x "$STAGE/SignalCliNet.WsRpcServer" "$STAGE/jre/bin/java"
tar -C "$STAGE" -czf "$OUT" .
echo "==> $OUT ($(du -h "$OUT" | cut -f1))"
cat "$VERSIONS"
