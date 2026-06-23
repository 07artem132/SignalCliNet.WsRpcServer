# CLAUDE.md

Guidance for AI coding agents (Claude Code, Copilot, etc.) working in this repository.

> This layout mirrors the agent-instruction convention from the sibling repo **SignalCli.NET**
> (the org's reference for agent-friendly practices): a short always-relevant root file + path-scoped
> topic rules under `.claude/rules/`. This repo is an **application**, not a library, so the rule set is
> deliberately lighter — there is no OpenSpec/regression-guard machinery here.

## Project

**SignalCliNet.WsRpcServer** — a standalone console application (runnable as a service / in Docker) that
exposes the [Signal](https://signal.org/) messenger over a single bidirectional **WebSocket JSON-RPC 2.0**
channel. It is the **bridge** between two libraries:

- **SignalCli.NET** (`AddSignalCli` + `AddSignalEvents`) — typed Signal API over a supervised `signal-cli` process.
- **JSON-RPC.NET / WsRpcServer** (`AddJsonRpcCore`) — the generic WebSocket JSON-RPC framework (abstract base classes).

This app supplies the concrete subclasses + adapters that wire Signal's facades onto the WS framework, so
external clients (any language that speaks WebSocket + JSON-RPC) can send messages, manage accounts, link
devices, and subscribe to incoming events without implementing HTTP/SSE plumbing.

- Target framework: **net10.0**, `OutputType=Exe`. Assembly version **1.1.0** (in the csproj).
- Requires **JDK 25** at runtime (signal-cli 0.14.3); the Docker image pulls Temurin 25 automatically.
- Pinned upstream versions: **SignalCli.NET 4.10.0**, **JSON-RPC.NET 1.1.0**, **SignalCli.Runtime 0.14.3.1**.

## Build & run

```bash
dotnet build  --configuration Release
dotnet run --project src/SignalCliNet.WsRpcServer    # serves WS JSON-RPC on Server:Host/Server:Port
```

Configuration is `appsettings.json` (sections `Server`, `SignalCli`, `Logging`), overridable via
environment/`Host.CreateDefaultBuilder`. Default listen address is `0.0.0.0:9000`.

### Restoring packages in a sandboxed env (Claude Code on the web)

`NuGet.Config` has a `<packageSourceMapping>` pointing `SignalCli.NET`, `JSON-RPC.NET` and
`SignalCli.Runtime` at the **private** GitHub Packages feed (`nuget.pkg.github.com/07artem132`), which
needs a token with `read:packages`. In a fresh remote container that feed is unreachable, so a plain
`dotnet restore` of those three packages will fail. Two ways forward:

1. **Build against sibling source** (the supported offline path): clone `JSON-RPC.NET` and `SignalCli.NET`
   next to this repo and use `deploy/build-local-feed.sh` (it packs them into a local feed `.localfeed/`).
   See `deploy/DEPLOYMENT.md`.
2. With a `GITHUB_TOKEN`, add the authenticated feed and restore normally.

The `dotnet` SDK itself is `apt`-installable from `packages.microsoft.com`.

## Architecture (key types — under `src/SignalCliNet.WsRpcServer/`)

- `Program.cs` — `Host.CreateDefaultBuilder` → `AddSignalCli(...)` + `AddSignalEvents()` + `AddSignalJsonRpc(...)`.
- `Extensions/SignalRpcExtensions.AddSignalJsonRpc` — the composition root: calls `AddJsonRpcCore`, then
  **overrides** the framework's core services with Signal-specific implementations
  (`SubscriptionManager`, `EventProcessor`, `RpcServiceRegistry`) and registers the RPC adapters + the
  `SignalRpcHostedService`.
- `Services/Signal*RpcAdapter` — thin adapters exposing `ISignalAccountsRpc` / `ISignalDevicesRpc` /
  `ISignalMessageRpc` (the WS-facing RPC surface) on top of SignalCli.NET facades.
- `Services/RpcServiceRegistry` — subclass of `AbstractRpcServiceRegistry` adding this assembly's prefix to discovery.
- `Sessions/SignalRpcSession`, `Services/SignalRpcServer` — concrete `Abstract*` subclasses from the framework.
- `Events/EventProcessor` + `EventTypeMapping`, `Subscriptions/SubscriptionManager` + `SubscriptionStore` — Signal event fan-out + subscription bookkeeping.
- `Serialization/SignalCliSerializerContext` — `System.Text.Json` source-gen context for the RPC payloads.

## Topic-scoped rules

- [`.claude/rules/conventions.md`](.claude/rules/conventions.md) — modern C# / naming / comments-in-Ukrainian *(loads when editing `src/**`)*.
- [`.claude/rules/upstream-dependencies.md`](.claude/rules/upstream-dependencies.md) — the two-library contract, version pinning, private-feed caveat, where each capability really lives *(loads when editing `src/**`, `*.csproj`, `NuGet.Config`, `deploy/**`)*.
- [`.claude/rules/cloud-dev.md`](.claude/rules/cloud-dev.md) — Claude Code on the web setup + SessionStart hook *(loads when editing `.claude/**`, `deploy/**`)*.

## Critical rules (do not regress)

1. **This is glue, not the place to fix framework bugs.** A bug in WebSocket framing, JSON-RPC
   correlation, or the abstract base classes belongs in **JSON-RPC.NET**; a bug in Signal process
   management / typed API belongs in **SignalCli.NET**. Don't fork upstream behavior here — bump the
   dependency version instead. See `.claude/rules/upstream-dependencies.md`.
2. **Upstream versions move together.** When bumping `SignalCli.NET`, `JSON-RPC.NET`, `SignalCli.Runtime`
   or the JDK, update the csproj, `NuGet.Config` (if a feed/package id changes), `deploy/Dockerfile*`,
   and `deploy/DEPLOYMENT.md` in the **same commit** — they drift easily. (The README's `.NET` /
   `07artem132` references are now aligned with the code; the one remaining stale claim is **JDK 21+** —
   signal-cli 0.14.3 actually needs **JDK 25**, per `deploy/DEPLOYMENT.md`. Fix it when you next touch the
   README.)
3. **No secrets in the repo.** Signal account data lives under `SignalCliStorageData/` (git-ignored);
   never commit account keys, phone numbers, tokens, or `appsettings` overrides with real values.
4. **Privacy in logs.** Don't log message bodies, phone numbers, or attachment payloads — mirror the
   SignalCli.NET privacy contract. Log method names, status, ids.
5. **Comments and log messages are written in Ukrainian** — match the surrounding code.

## Git

Work on a feature branch; do not push or commit unless asked. Never force-push or amend already-pushed
commits without explicit approval.
