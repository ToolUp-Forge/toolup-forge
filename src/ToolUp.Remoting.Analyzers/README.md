# ToolUp.Remoting.Analyzers

An [FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK) analyzer that catches missing
ToolUp.Remoting authorisation / audit attributes **at edit / compile / CI time** instead of at server
startup.

The dispatcher's startup classifier refuses to boot when an API-record method carries none of the
authorisation classifications, raising:

```
ToolUp.Remoting refused to start: API record 'MyApi' has 1 unclassified method(s): [SaveThing]. ...
```

This analyzer shifts that failure **left** to an IDE squiggle / CI diagnostic — strictly superior to
discovering the gap only when the server fails to start. It is pure tooling: a consumer that never
installs it sees byte-for-byte-unchanged behaviour, and the runtime classifier is untouched.

## Diagnostics

| Code | Severity | What it flags |
|------|----------|---------------|
| `TUR0001` | Error | A Remoting API-record field (method) carrying **none** of `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]`. Mirrors the runtime `AuthClassifier`'s by-simple-name recognition, so the analyzer flags exactly the set the dispatcher would refuse to start on. |
| `TUR0002` | Warning (opt-in) | An unaudited API method whose input record carries a `[<PiiSafe>]` field — PII flows through but no `[<Audit(...)>]` records it. Off by default. |

Both attribute families are recognised: the server-tier `ToolUp.Remoting.Server.*` set and the
tier-shared `ToolUp.Platform.*` mirrors that Fable-compiled API records carry.

### Codefixes

Each finding offers a one-keystroke fix that inserts a **fail-closed placeholder** — never a real role:

- `TUR0001` → `[<AllowAnonymous>]` (still respects an auth context for telemetry; forces the author to
  confirm the method is genuinely open).
- `TUR0002` → `[<Audit("Custom:TODO")>]`.

Replace the placeholder with the real classification deliberately.

## What counts as an "API record"

The analyzer treats a record as a Remoting API contract when it has ≥1 field and **every** field is a
function type (`'a -> Async<'b>` etc.) — the Remoting contract shape. Ordinary data records are never
flagged. Once a record is a candidate, classification of its fields is in lock-step with the runtime.

## Usage

Add the package reference (it contributes no compile/runtime surface):

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Remoting.Analyzers" Version="..." PrivateAssets="all" />
</ItemGroup>
```

Run from the CLI:

```pwsh
dotnet tool install --global fsharp-analyzers
fsharp-analyzers --project MyProject.fsproj --analyzers-path <restored-package>/analyzers/dotnet/fs
```

Or point your editor's F# analyzer path at the restored package's `analyzers/dotnet/fs` directory.

### Enabling TUR0002 (audit heuristic)

`TUR0002` is off by default. Set the environment variable before running the analyzer host:

```pwsh
$env:TOOLUP_REMOTING_ANALYZER_AUDIT = "1"
```

## Parity guarantee

The analyzer's `Recognition.fs` decision core is **source-linked** into `ToolUp.Platform.Tests`, where a
parity test runs it against the real internal `AuthClassifier` over identical fixture record types and
asserts the "unclassified" verdict is the exact set the runtime refuses on. The analyzer and the
dispatcher can never silently diverge on what counts as classified.

Apache-2.0.
