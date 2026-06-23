---
paths:
  - ".claude/**"
  - "deploy/**"
---

# Cloud development (Claude Code on the web)

A `SessionStart` hook (`.claude/hooks/session-start.sh`, wired via `.claude/settings.json`) installs
`dotnet-sdk-10.0` (the app targets net10.0) and does a **best-effort** `dotnet restore`. It runs only when
`CLAUDE_CODE_REMOTE=true` and is idempotent.

The restore is best-effort because the three upstream packages (`SignalCli.NET`, `JSON-RPC.NET`,
`SignalCli.Runtime`) live in a **private** GitHub Packages feed that needs a `read:packages` token. In a
fresh container without that token the restore of those packages fails — that's expected. The supported
offline path is to build the dependencies from the sibling repos into a local feed via
`deploy/build-local-feed.sh` (see `deploy/DEPLOYMENT.md`).

For environments / network policy / triggers, see https://code.claude.com/docs/en/claude-code-on-the-web.
