---
paths:
  - "src/**"
---

# Conventions (match the existing code)

- Modern C#: file-scoped namespaces, primary constructors (the adapters/registry use them), records for
  DTOs, `sealed` for leaf classes, `var` only when the type is obvious.
- `string`/`int` keywords, not `String`/`Int32`. `_camelCase` private fields, PascalCase public,
  `I`-prefixed interfaces.
- Always `.ConfigureAwait(false)` in non-UI async code.
- **Exceptions:** throw/catch specific types. Broad `catch (Exception)` only at long-running loop
  boundaries (event fan-out, session loops), and only if it logs and continues.
- Keep XML doc comments on public members (existing adapters/extensions document them in English; new
  members follow suit, but inline comments and log messages stay Ukrainian — see below).
- **Comments and log messages are written in Ukrainian** in this codebase — match that when editing.
- **Serialization:** `System.Text.Json` only. Register every new RPC payload root type in the source-gen
  context `Serialization/SignalCliSerializerContext.cs` — an unregistered type fails at runtime under
  source-gen serialization.
- This is an app: prefer wiring + thin adapters. Keep real protocol/process logic in the upstream
  libraries (see `.claude/rules/upstream-dependencies.md`).
