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
| `TUR0001` | Error | A Remoting API-record field (method) carrying **none** of `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]`, by simple name. |
| `TUR0003` | Error | A field carrying a marker **named** like one of those five that is **not the SDK's attribute** by what the file shows: qualified by a path that reaches neither `ToolUp.Platform` nor `ToolUp.Remoting.Server` through the file's opens, or unqualified while a type of that name is declared in the record's own module, an enclosing one, or one the file opens. The runtime refuses it at startup as a marker collision; `TUR0001` reads the field as classified. |
| `TUR0002` | Warning (opt-in) | An unaudited API method whose input record carries a `[<PiiSafe>]` field — PII flows through but no `[<Audit(...)>]` records it. Off by default. |
| `TUA001` | Warning | Container resolution (`GetService` / `GetRequiredService` / `GetServices` / keyed forms / `RequestServices`) outside service registration. |
| `TUA002` | Warning | An `HttpClient` constructed without the platform egress handler (`PlatformHttpClient`). |
| `TUA003` | Warning | `Async.RunSynchronously` outside an `[<EntryPoint>]`. |

The `TUA` rules ship as a second analyser, `ToolUpContainment`. The full rule reference —
rationale, sanctioned alternative and since-version for every code — is generated from `Rules.fs` into
[`docs/platform/analyzer-rules.md`](../../docs/platform/analyzer-rules.md); the `Analyzers` leg of
`VerifyAll` fails when the two differ, and runs the `TUA` rules over this repository's own `src/`
against the inventory in `analyzer-inventory.json`.

For `TUR0001` / `TUR0002` / `TUR0003`, both attribute families are the SDK's: the server-tier `ToolUp.Remoting.Server.*`
set and the tier-shared `ToolUp.Platform.*` mirrors that Fable-compiled API records carry.

## Two rules for one runtime rule

The dispatcher matches an authorisation marker by **CLR identity** — the attribute's type must be one of the SDK's — and
refuses a method carrying an attribute that has a marker's simple name without being the SDK's. The analyzer sees
syntax, not types, so it states that one rule as two: `TUR0001` (no marker by name) and `TUR0003` (a marker by name
that the file shows is foreign). Where the file does not show where an unqualified name comes from, the analyzer takes
it to be the SDK's rather than guess, so an unqualified foreign marker brought in by an open alone is still found only
at server start.

### Codefixes

Each finding offers a one-keystroke fix that inserts a **fail-closed placeholder** — never a real role:

- `TUR0001` → `[<AllowAnonymous>]` (still respects an auth context for telemetry; forces the author to
  confirm the method is genuinely open).
- `TUR0002` → `[<Audit("Custom:TODO")>]`.

Replace the placeholder with the real classification deliberately. `TUR0003` offers none: inserting the SDK's marker
beside a foreign one is still a collision, so the foreign marker has to go.

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
parity test runs it against the real internal `AuthClassifier` over identical fixture record types declared
with the SDK's own attributes, and asserts the "unclassified" verdict is the exact set the runtime refuses on.
The foreign-marker case those types cannot express is pinned on the syntax side, in
`ToolUp.Remoting.Analyzers.Tests`, where `TUR0003` fires on a foreign-named `RequiresRole` that `TUR0001` reads as
classified.

Apache-2.0.
