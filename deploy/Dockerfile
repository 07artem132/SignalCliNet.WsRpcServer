# syntax=docker/dockerfile:1
#
# Package-based build for SignalCliNet.WsRpcServer.
#
# Dependencies (SignalCli.NET, JSON-RPC.NET, SignalCli.Runtime) are restored as
# NuGet packages from GitHub Packages (nuget.pkg.github.com/07artem132) — no
# sibling source checkouts needed. Build context is THIS repo only.
#
# The feed requires authentication, so pass a GitHub username + a token with
# read:packages scope:
#
#   docker build -f deploy/Dockerfile \
#       --build-arg GITHUB_USERNAME=<user> \
#       --build-arg GITHUB_TOKEN=<PAT-with-read:packages> \
#       -t wsrpcserver .
#
# (docker-compose.yml passes these from the GITHUB_USERNAME / GITHUB_TOKEN env.)
#
# Offline / no-token alternative that builds the dependencies from sibling
# source checkouts: see Dockerfile.from-source.

# ----------------------------------------------------------------------------
# Stage 1 — restore packages from GitHub Packages + publish the server
# ----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG GITHUB_USERNAME
ARG GITHUB_TOKEN

WORKDIR /src
COPY . .

# Build-time NuGet config: authenticate the GitHub Packages feed declared in the
# repo's NuGet.Config, fall back to nuget.org for transitive dependencies.
RUN cat > /tmp/nuget.config <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="github" value="https://nuget.pkg.github.com/07artem132/index.json" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github>
      <add key="Username" value="${GITHUB_USERNAME}" />
      <add key="ClearTextPassword" value="${GITHUB_TOKEN}" />
    </github>
  </packageSourceCredentials>
  <packageSourceMapping>
    <packageSource key="github">
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

# Publish the server. The SignalCli.Runtime package's .targets stages signal-cli
# into the build output; CopySignalCliToPublish (in the csproj) carries it into
# the publish folder.
RUN dotnet publish src/SignalCliNet.WsRpcServer/SignalCliNet.WsRpcServer.csproj \
        -c Release -o /app \
        --configfile /tmp/nuget.config \
    && rm -f /tmp/nuget.config

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
