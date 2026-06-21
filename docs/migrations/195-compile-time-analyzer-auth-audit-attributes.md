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
dotnet tool install --global fsharp-analyzers
fsharp-analyzers --project MyProject.fsproj --analyzers-path <restored-pkg>/analyzers/dotnet/fs
```

Or point your editor's F# analyzer path at the restored package's `analyzers/dotnet/fs` directory.
Enable `TUR0002` with `TOOLUP_REMOTING_ANALYZER_AUDIT=1`.

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

## Rollback

Remove the `PackageReference`. No runtime artefact, no data migration; the dispatcher's startup
classifier is the unchanged enforcement of record (the analyzer only mirrors it for earlier feedback).
