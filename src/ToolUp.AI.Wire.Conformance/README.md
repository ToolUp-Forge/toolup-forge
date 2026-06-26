# ToolUp.AI.Wire.Conformance

The **portability conformance pack** for `ToolUp.AI.Wire` and the three
provider wire mappers (OpenAI, Gemini, Claude). It runs one fixture corpus on
**both** hosts — .NET and Fable/browser — and fails the build if the
byte-for-byte request output or the parsed response shape diverges.

See [`PORTABILITY.md`](PORTABILITY.md) for the guarantee and its bounds.

## Layout

| Path | Role |
|---|---|
| `Corpus.fs` | The consolidated corpus — adapts the per-provider golden fixtures into the uniform `(request byte-parity, response structural-parity)` shape. No golden data is duplicated; it is source-linked from the provider fixtures. |
| `ConformanceSuite.fs` | The **one** dual-run harness, compiled by both hosts (signature-compatible test facades), so the assertion logic cannot drift. |
| `ProgramNet.fs` + `ToolUp.AI.Wire.Conformance.fsproj` | The .NET (Expecto) host. |
| `Fable/` | The Fable host: `NodeTest.fs` facade, `ProgramFable.fs`, the `.Fable.fsproj`, and its zero-dep `package.json`. |

## Running

Both legs must pass — a single host proves nothing about portability.

```pwsh
# .NET (Expecto) host — from the repo root
dotnet run --project src/ToolUp.AI.Wire.Conformance/ToolUp.AI.Wire.Conformance.fsproj

# Fable (node:test) host — from src/ToolUp.AI.Wire.Conformance/Fable
dotnet tool restore
dotnet fable -o output --noCache
node --test output/ProgramFable.js
```

CI runs both in `.github/workflows/checks.yml` (job `ai-wire-conformance`).

> Run via `dotnet run`, **not** `dotnet test` — the .NET host is an Expecto
> console runner and `dotnet test` exits 0 having run nothing.

`IsPackable=false` — this project ships nothing; it is a CI gate.
