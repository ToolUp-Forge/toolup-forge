# Phase 195 — `ToolUp.Remoting.Analyzers` (compile-time auth/audit analyzer)

**What changes.** A new, separate package — `ToolUp.Remoting.Analyzers` — ships an
[FSharp.Analyzers.SDK](https://github.com/ionide/FSharp.Analyzers.SDK) analyzer that flags
ToolUp.Remoting API-record methods missing their Phase 69d authorisation classification **at edit /
compile / CI time** instead of at server startup. It shifts the dispatcher's startup refusal
(`AuthClassifier` raising *"ToolUp.Remoting refused to start: API record … unclassified"*) **left**
to an IDE squiggle / CI diagnostic.

**Scope.** Purely additive opt-in tooling (GP 11/13). The analyzer references nothing at runtime; a
consumer that never installs it is **byte-for-byte unchanged**, and the runtime classifier in
`Auth.fs` / `Audit.fs` is **untouched**. There is no required migration — adopt only if you want the
shift-left diagnostic.

## Diagnostics

| Code | Severity | Flags |
|------|----------|-------|
| `TUR0001` | Error | A Remoting API-record field (method) carrying none of `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]`. |
| `TUR0002` | Warning (opt-in) | An unaudited API method whose input record carries a `[<PiiSafe>]` field (Phase 69h). Off by default; set `TOOLUP_REMOTING_ANALYZER_AUDIT=1`. |

Recognition mirrors the runtime `AuthClassifier`'s by-simple-name matching exactly (both attribute
families — `ToolUp.Remoting.Server.*` and the tier-shared `ToolUp.Platform.*` mirrors), so the
analyzer flags precisely the set the dispatcher would refuse to start on. Each finding offers a
one-keystroke codefix inserting a fail-closed placeholder (`[<AllowAnonymous>]` /
`[<Audit("Custom:TODO")>]`) — **never a real role**.

"API record" detection is a shape heuristic: a record with ≥1 field where every field is a function
type. Ordinary data records are never flagged.

## Adopting

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.Remoting.Analyzers" Version="$(ToolUpSdkVersion)" PrivateAssets="all" />
</ItemGroup>
```

CLI:

```pwsh
# Pin the host to 0.36.0 — it MUST match the analyzer's FSharp.Analyzers.SDK
# pin (0.36.0). The analyzer DLL is built against FSharp.Core 10.0.101
# (assembly 10.0.0.0), which is exactly what the 0.36.0 host bundles, so it
# loads cleanly. A newer host (e.g. 0.37.2) rejects a 0.36.0-built analyzer on
# the SDK-version check ("built using SDK version 0.36.0.0. Expect 0.37.2.0").
dotnet tool install --global fsharp-analyzers --version 0.36.0
fsharp-analyzers --project MyProject.fsproj --analyzers-path <restored-pkg>/analyzers/dotnet/fs
```

Or point your editor's F# analyzer path at the restored package's `analyzers/dotnet/fs` directory.
Enable `TUR0002` with `TOOLUP_REMOTING_ANALYZER_AUDIT=1`.

> **Host ABI note.** An analyzer DLL must be FSharp.Core-ABI-compatible with the host that loads it,
> not with the workspace baseline. The forge analyzer project therefore `VersionOverride`s FSharp.Core
> to `10.0.101` (assembly `10.0.0.0`) — the version `fsharp-analyzers` **0.36.0** bundles — while the
> rest of the workspace stays on the `10.1.300` baseline. Building the analyzer against `10.1.300`
> instead produces `Could not load FSharp.Core 10.1.0.0. The expected assembly version of FSharp.Core
> is 10.0.0.0.` and the host registers **0 analyzers while exiting 0** (a silent false-green). If the
> SDK pin ever moves, re-pin FSharp.Core to whatever the matching host bundles, in lockstep.

## Verification

1. Add an API record with one unattributed method to an analyzed project → `TUR0001` surfaces at the
   method, with an "insert `[<AllowAnonymous>]`" codefix; classify the method → the diagnostic clears.
2. A fully-classified record raises nothing.
3. Do not install the analyzer → no diagnostics, no behaviour change, build unchanged.
4. Contract packs (in-tree, run by `dotnet run --project Build.fsproj -- VerifyAll`):
   - `RemotingAnalyzerRecognitionTests` in `ToolUp.Platform.Tests` — the **parity** gate: the
     analyzer's source-linked `Recognition.fs` decision core vs the runtime `AuthClassifier`
     (unclassified-set equality) over identical fixture record types, plus TUR0001/TUR0002 finding
     shape and source-/CLR-name normalisation.
   - `ToolUp.Remoting.Analyzers.Tests` — the AST-extraction gate: offline FCS parse of fixture
     sources driven through `Analyzer.analyzeParseTree` (TUR0001 raised / classified clean / data
     records ignored / TUR0002 opt-in gating).
5. CLI host load (the path consumers actually run). With `fsharp-analyzers` **0.36.0** installed,
   pointing `--analyzers-path` at the restored `toolup.remoting.analyzers/<ver>/analyzers/dotnet/fs`
   logs `Registered 1 analyzers from 1 dlls` and emits `TUR0001` on an unclassified fixture API
   record — confirmed against SDK 0.7.0. Anything other than `Registered 1 analyzers` (e.g.
   `Could not load FSharp.Core …`, `Registered 0 analyzers`, `Assembly will be skipped`) is a
   host/analyzer ABI mismatch, not a clean run — see the Host ABI note above. The consumer-side
   guarded runner `dev-scripts/run-analyzers.ps1` treats those strings as hard failures so the
   false-green can't reach CI.

## Rollback

Remove the `PackageReference`. No runtime artefact, no data migration; the dispatcher's startup
classifier is the unchanged enforcement of record (the analyzer only mirrors it for earlier feedback).
