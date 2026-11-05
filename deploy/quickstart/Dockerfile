# syntax=docker/dockerfile:1
#
# Образ шлюзу з ГОТОВОГО релізного бандла (GitHub Release `linux-<arch>-bundle`): self-contained сервер +
# Temurin JRE 25 + signal-cli. На відміну від deploy/Dockerfile тут НЕ потрібен ні .NET SDK, ні токен до
# приватного NuGet-feed — лише публічні assets релізу. SHA-256 бандла звіряється з SHA256SUMS того ж релізу.
#
#   docker build --build-arg WSRPC_VERSION=2.3.2 -t signal-notify-gateway deploy/quickstart
#
# Підтримка: linux/amd64 і linux/arm64 (glibc; Alpine/musl не підходить — libsignal без musl-збірок).

ARG WSRPC_VERSION

# ----------------------------------------------------------------------------------------------------
# Stage 1 — завантажити й перевірити бандл
# ----------------------------------------------------------------------------------------------------
FROM debian:bookworm-slim AS fetch
ARG WSRPC_VERSION
ARG TARGETARCH
ARG WSRPC_REPO=07artem132/SignalCliNet.WsRpcServer

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

RUN <<'SCRIPT'
set -eu
test -n "${WSRPC_VERSION}" || { echo "WSRPC_VERSION не задано (--build-arg WSRPC_VERSION=X.Y.Z)" >&2; exit 1; }
case "${TARGETARCH:-amd64}" in
  amd64) RID=x64 ;;
  arm64) RID=arm64 ;;
  *) echo "Непідтримувана архітектура: ${TARGETARCH}" >&2; exit 1 ;;
esac
NAME="SignalCliNet.WsRpcServer-${WSRPC_VERSION}-linux-${RID}-bundle.tar.gz"
BASE="https://github.com/${WSRPC_REPO}/releases/download/v${WSRPC_VERSION}"
cd /tmp
curl -fsSL --retry 3 -o "$NAME" "$BASE/$NAME"
curl -fsSL --retry 3 -o SHA256SUMS "$BASE/SHA256SUMS"
grep " ${NAME}\$" SHA256SUMS | sha256sum -c -
mkdir -p /app
tar -xzf "$NAME" -C /app
test -x /app/SignalCliNet.WsRpcServer
test -x /app/jre/bin/java
ls /app/signal-cli/lib/signal-cli-*.jar >/dev/null
SCRIPT

# ----------------------------------------------------------------------------------------------------
# Stage 2 — runtime
# ----------------------------------------------------------------------------------------------------
FROM debian:bookworm-slim AS runtime

# libicu — глобалізація .NET; qrencode — QR для лінкування в терміналі; python3-websockets — wsrpc-admin
# (mTLS admin-клієнт). Жодного компілятора чи SDK в образі.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates libicu72 qrencode python3 python3-websockets \
    && rm -rf /var/lib/apt/lists/*

COPY --from=fetch /app /app
COPY tools/wsrpc-admin tools/signal-link /usr/local/bin/
RUN chmod 0755 /usr/local/bin/wsrpc-admin /usr/local/bin/signal-link

ENV JAVA_HOME=/app/jre \
    PATH=/app/jre/bin:$PATH \
    SignalCli__JavaExecutable=/app/jre/bin/java \
    SignalCli__LibDirectory=/app/signal-cli/lib \
    SignalCli__StoragePathCli=/data/SignalCliStorageData \
    Persistence__DataDirectory=/data

WORKDIR /app
EXPOSE 9000
VOLUME ["/data"]

ENTRYPOINT ["/app/SignalCliNet.WsRpcServer"]
