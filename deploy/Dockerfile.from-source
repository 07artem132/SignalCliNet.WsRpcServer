# syntax=docker/dockerfile:1
#
# Multi-stage build for SignalCliNet.WsRpcServer.
#
# Build context MUST be the parent directory that contains all three repos as
# siblings (so the SDK stage can build the dependency NuGet feed from source):
#
#   <parent>/
#     JSON-RPC.NET/
#     SignalCli.NET/
#     SignalCliNet.WsRpcServer/
#
# Build from that parent directory:
#   docker build -f SignalCliNet.WsRpcServer/deploy/Dockerfile -t wsrpcserver .
#
# (docker-compose.yml already sets `context: ..` and this Dockerfile path.)

# ----------------------------------------------------------------------------
# Stage 1 — build dependency feed + publish the server
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

RUN apt-get update && apt-get install -y --no-install-recommends \
        bash curl ca-certificates tar \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /src
COPY JSON-RPC.NET/ ./JSON-RPC.NET/
COPY SignalCli.NET/ ./SignalCli.NET/
COPY SignalCliNet.WsRpcServer/ ./SignalCliNet.WsRpcServer/

# Build the local NuGet feed (JSON-RPC.NET + SignalCli.NET core + SignalCli.Runtime).
ENV JSONRPC_REPO=/src/JSON-RPC.NET
ENV SIGNALCLI_REPO=/src/SignalCli.NET
RUN bash SignalCliNet.WsRpcServer/deploy/build-local-feed.sh /feed

# A build-time NuGet config that resolves the three packages from the local feed
# and everything else from nuget.org.
RUN cat > /feed/nuget.config <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local">
      <package pattern="SignalCli.NET" />
      <package pattern="JSON-RPC.NET" />
      <package pattern="SignalCli.Runtime" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

# Publish the server. The SignalCli.Runtime .targets stages signal-cli into the
# publish output during the build that publish performs.
RUN dotnet publish SignalCliNet.WsRpcServer/src/SignalCliNet.WsRpcServer/SignalCliNet.WsRpcServer.csproj \
        -c Release -o /app \
        --configfile /feed/nuget.config -p:NuGetAudit=false

# ----------------------------------------------------------------------------
# Stage 2 — runtime image (ASP.NET + JDK 25 for signal-cli)
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime

# signal-cli 0.14.3 requires JDK 25. Temurin 25 is fetched from GitHub releases.
ARG TEMURIN_URL=https://github.com/adoptium/temurin25-binaries/releases/download/jdk-25%2B36/OpenJDK25U-jdk_x64_linux_hotspot_25_36.tar.gz
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && mkdir -p /opt/jdk25 \
    && curl -sSL "$TEMURIN_URL" -o /tmp/jdk25.tar.gz \
    && tar -xzf /tmp/jdk25.tar.gz -C /opt/jdk25 --strip-components=1 \
    && rm /tmp/jdk25.tar.gz \
    && apt-get purge -y curl && apt-get autoremove -y && rm -rf /var/lib/apt/lists/*

ENV JAVA_HOME=/opt/jdk25
ENV PATH=/opt/jdk25/bin:$PATH
# Point SignalCli at the bundled JDK 25 explicitly (config key SignalCli:JavaExecutable).
ENV SignalCli__JavaExecutable=/opt/jdk25/bin/java
# Persist linked-account keys/state here (mount a volume).
ENV SignalCli__StoragePathCli=/data/SignalCliStorageData

WORKDIR /app
COPY --from=build /app ./

# WebSocket JSON-RPC port (see appsettings.json -> Server:Port).
EXPOSE 9000
VOLUME ["/data"]

ENTRYPOINT ["dotnet", "SignalCliNet.WsRpcServer.dll"]
