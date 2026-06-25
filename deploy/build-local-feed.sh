#!/usr/bin/env bash
#
# Builds the three sibling dependencies of SignalCliNet.WsRpcServer into a local
# NuGet feed so the server can be built/published without the auth-gated GitHub
# Packages feed.
#
#   JSON-RPC.NET         -> JSON-RPC.NET.<ver>.nupkg
#   SignalCli.NET (core) -> SignalCli.NET.<ver>.nupkg
#   SignalCli.Runtime    -> SignalCli.Runtime.<ver>.nupkg   (ships the signal-cli payload)
#
# The runtime package is built in TWO passes on purpose: its <None> payload glob
# is evaluated at project-load time, before the download target populates
# obj/signal-cli. Pass 1 (build) downloads signal-cli; pass 2 (pack, fresh
# invocation) re-evaluates the glob with the files present and ships them.
#
# Usage:
#   deploy/build-local-feed.sh [FEED_DIR]
#
# Env overrides (default to sibling checkouts next to this repo):
#   JSONRPC_REPO   path to JSON-RPC.NET checkout      (default ../JSON-RPC.NET)
#   SIGNALCLI_REPO path to SignalCli.NET checkout     (default ../SignalCli.NET)
#   NUGET_SOURCE   upstream restore source            (default nuget.org)

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PARENT="$(cd "$REPO_ROOT/.." && pwd)"

FEED_DIR="${1:-$REPO_ROOT/.localfeed}"
JSONRPC_REPO="${JSONRPC_REPO:-$PARENT/JSON-RPC.NET}"
SIGNALCLI_REPO="${SIGNALCLI_REPO:-$PARENT/SignalCli.NET}"
NUGET_SOURCE="${NUGET_SOURCE:-https://api.nuget.org/v3/index.json}"

RESTORE_ARGS=(--source "$NUGET_SOURCE" -p:NuGetAudit=false -p:GeneratePackageOnBuild=false)

echo "Feed dir:        $FEED_DIR"
echo "JSON-RPC.NET:    $JSONRPC_REPO"
echo "SignalCli.NET:   $SIGNALCLI_REPO"
echo "Restore source:  $NUGET_SOURCE"
echo

for repo in "$JSONRPC_REPO" "$SIGNALCLI_REPO"; do
  if [ ! -d "$repo" ]; then
    echo "ERROR: dependency repo not found: $repo" >&2
    echo "Set JSONRPC_REPO / SIGNALCLI_REPO or check out the repos as siblings." >&2
    exit 1
  fi
done

mkdir -p "$FEED_DIR"

echo "==> [1/3] JSON-RPC.NET"
dotnet pack "$JSONRPC_REPO/src/WsRpcServer/WsRpcServer.csproj" \
  -c Release "${RESTORE_ARGS[@]}" -o "$FEED_DIR"

echo "==> [2/3] SignalCli.NET (core library)"
dotnet pack "$SIGNALCLI_REPO/src/SignalCli/SignalCli.csproj" \
  -c Release "${RESTORE_ARGS[@]}" -o "$FEED_DIR"

echo "==> [3/3] SignalCli.Runtime (two-pass: download then pack payload)"
dotnet build "$SIGNALCLI_REPO/src/SignalCli.runtime/SignalCli.runtime.csproj" \
  -c Release "${RESTORE_ARGS[@]}"
dotnet pack "$SIGNALCLI_REPO/src/SignalCli.runtime/SignalCli.runtime.csproj" \
  -c Release --no-restore -p:NuGetAudit=false -o "$FEED_DIR"

echo
echo "Done. Packages in $FEED_DIR:"
ls -1 "$FEED_DIR"/*.nupkg
