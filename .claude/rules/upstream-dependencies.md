---
paths:
  - "src/**"
  - "**/*.csproj"
  - "NuGet.Config"
  - "deploy/**"
---

# Upstream dependencies (this app is glue)

This application is the concrete wiring on top of two libraries. Knowing **where a capability lives** is
the single most important fact for editing it safely.

| Concern | Lives in | Surface here |
|---|---|---|
| signal-cli process launch/supervise/health, typed Signal API, incoming events | **SignalCli.NET** (4.10.0) | `AddSignalCli(...)` + `AddSignalEvents()` in `Program.cs`; consumed by the `Signal*RpcAdapter`s |
| WebSocket transport, JSON-RPC 2.0 correlation, abstract base classes, service discovery | **JSON-RPC.NET / WsRpcServer** (1.1.0) | `AddJsonRpcCore(...)` via `AddSignalJsonRpc`; the `Abstract*` subclasses (`SignalRpcServer`, `SignalRpcSession`, `RpcServiceRegistry`, `EventProcessor`, `SubscriptionManager`) |
| signal-cli + JDK 25 payload | **SignalCli.Runtime** (0.14.3.1) | staged into output by its `.targets`; published via the `CopySignalCliToPublish` MSBuild target |

## Rules

1. **Fix bugs upstream, not here.** A framing/correlation/lifecycle bug belongs in JSON-RPC.NET; a
   process/API bug belongs in SignalCli.NET. In this repo you bump the dependency version and adapt the
   call site — you don't reimplement upstream behavior. If upstream needs a change, make it there (those
   repos have their own `CLAUDE.md` + OpenSpec process).
2. **Version bumps move as a set.** SignalCli.NET ↔ SignalCli.Runtime are coupled (the runtime ships the
   signal-cli + JRE the library drives); JDK requirement tracks signal-cli (0.14.3 ⇒ JDK 25). When you
   touch any of these, update **all** of: `SignalCliNet.WsRpcServer.csproj`, `NuGet.Config` (package
   patterns), `deploy/Dockerfile*`, `deploy/DEPLOYMENT.md`, and the README — in one commit.
3. **Private feed reality.** `SignalCli.NET`, `JSON-RPC.NET`, `SignalCli.Runtime` resolve **only** from
   the private GitHub Packages feed (`nuget.pkg.github.com/07artem132`) per `NuGet.Config`'s
   `packageSourceMapping`. Restore needs a `read:packages` token, or build the deps from sibling source
   into a local feed (`deploy/build-local-feed.sh`). Don't "fix" a sandbox restore failure by deleting
   the source mapping — that mapping is intentional supply-chain scoping.
4. **`AddSignalJsonRpc` overrides framework defaults deliberately.** It calls `AddJsonRpcCore` first, then
   replaces `ISubscriptionManager`/`IEventProcessor`/`IRpcServiceRegistry` with Signal-specific
   singletons. If JSON-RPC.NET later completes its composition root (audit finding H1, a generic
   `AddJsonRpcCore<...>`), revisit this method so the two don't double-register.
5. **Known doc drift to fix when you touch the README:** badges/links still say `.NET 9.0` and
   `mil-development` while the code is net10 + `07artem132`. `DEPLOYMENT.md` is the accurate reference.
