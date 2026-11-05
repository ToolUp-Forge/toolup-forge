# A namespace names the part: the server tier's per-part namespaces

**Ruling: YES** — recorded 2026-11-02, with the parts as Phase 1193 cut them in hand. The SDK is 0.x and the 1.0
plan rides breaking changes on 0.x minors with a codemod and a migration doc; the flat namespace was the one
obstacle the gate-lane plan named to a static "which tests open which parts" map; and 1193 left every source where
it was, so a file's namespace is the only signal of its part a reader or a static map can see.

**Ships in:** 0.39.0 (the same draft slot 1193 rides). **Breaking for source:** every consumer's `open` lines.

**Affected:** every file that compiled against the server tier with `open ToolUp.Platform`. That open now reaches
Core and nothing else; the server tier's names sit under the namespaces below, and a consumer opens the parts it
uses. The codemod derives those opens (see [Run the codemod](#run-the-codemod)); the shapes it cannot decide are
listed under [What the codemod leaves to you](#what-the-codemod-leaves-to-you).

## The rule

A namespace names the part, so the map from namespace to assembly is a function: a namespace may cover fewer
files than its assembly, and never spans two.

| Assembly | Namespace | Holds |
|---|---|---|
| `ToolUp.Platform.Server.Remoting` | `ToolUp.Remoting.Server` (and `.Giraffe`, `.AspNetCore`, `.Server.Proxy`), unchanged | the remoting library's server half, as Phase 73 named it |
| | `ToolUp.Remoting.Server.Http` | the part's one platform-named module, `HttpBodyBinding` |
| `ToolUp.Platform.Server.Infra` | `ToolUp.Platform.Infra` | the substrate: stores, seams, health, secrets, compute, data sources |
| | `ToolUp.Platform.Infra.Metrics`, `.Infra.Tracing`, `.Infra.Transport`, `.Infra.Usage` | the Infra files that declared `ToolUp.Platform.Metrics` / `.Tracing` / `.Transport` / `.Usage` |
| `ToolUp.Platform.Server.Config` | `ToolUp.Platform.Config` | config resolution, the binder, profiles, migrations, flags, validators |
| | `ToolUp.Platform.Config.Auth` | the Config files that declared `ToolUp.Platform.Auth.*` |
| `ToolUp.Platform.Server.Scope` | `ToolUp.Platform.Scope` | auth providers, scope resolution, sessions, permissions, grants, share tokens |
| | `ToolUp.Platform.Scope.Teams`, `.Scope.Premium` | the Scope files that declared `ToolUp.Platform.Teams` / `.Premium.*` |
| `ToolUp.Platform.Server.Jobs` | `ToolUp.Platform.Jobs` | the job store and scheduler, notifications, presence, webhooks |
| | `ToolUp.Platform.Jobs.SmokeTests` | `SmokeTests.Defaults` |
| `ToolUp.Platform.Server.Audit` | `ToolUp.Platform.Audit` | the audit log, replication, classification, consent, erasure |
| | `ToolUp.Platform.Audit.Metrics`, `.Audit.Consent` | the Audit files that declared `ToolUp.Platform.Metrics` / `.Consent` |
| `ToolUp.Platform.Server` (umbrella) | `ToolUp.Platform.Compose` | the composition root, the plan, boot, preflight, lifecycle |
| | `ToolUp.Platform.Files` | the five file-management sources 1193 merged into the umbrella |
| `ToolUp.Platform.Core` | `ToolUp.Platform` (and its existing `.Metrics`, `.Usage`, `.Narrative`, …), unchanged | everything shared |

**The 53 reach files take the namespace of the part they are PLACED in**, not of the subsystem their name says:
`ConsoleLogger` is `ToolUp.Platform.Config.ConsoleLogger`, `TeamManagement` is `ToolUp.Platform.Config.TeamManagement`,
`PlatformAdminStore` is `ToolUp.Platform.Jobs.PlatformAdminStore`, the narrative stores are
`ToolUp.Platform.Compose.*`. A namespace that disagreed with the assembly would make the static opens map lie, and
the disagreement is the coupling 1193 chose to make visible. Each is marked in the map below.

**Core stays flat.** Its types cross the wire or are shared with the client tier (GP 10), and a Core type only the
server uses is still Core; nothing moves between assemblies here. The one consequence inside the tier: F# opens no
enclosing namespace on its own, so a file in `ToolUp.Platform.Jobs` sees Core only through an explicit
`open ToolUp.Platform`, and an earlier part only through its namespace. Every tier file now carries those opens.

**Names already taken.** `ToolUp.Remoting` is the remoting library's (Phase 73); its `ToolUp.Remoting`,
`ToolUp.Remoting.Json` and `ToolUp.Remoting.MsgPack` namespaces span Core and the Remoting part by that library's
own design (the shared codec in Core, the server reader and verification gates in the part) and are left as they
are — the one place the function rule is read as "the library's, not the platform's". `ToolUp.Platform.Remoting`
is a module of the client tier (`ToolUp.Platform.Client`), and the Platform test pack compiles against both tiers,
so the Remoting part's platform-named module takes `ToolUp.Remoting.Server.Http` rather than that name — its own
sub-namespace, so a consumer opening it for the body binder is not handed the whole remoting server surface, whose
`AuditEvent` record shadows Core's. `ToolUp.Platform.Config` and `ToolUp.Platform.Server` were checked as module
names before the namespaces were declared: the first was free; the second is the umbrella's composition module,
which moves to `ToolUp.Platform.Compose.Server` with the rest of the umbrella.

**Two namespaces are declared by Core and by a part.** `ToolUp.Platform.Metrics` and `ToolUp.Platform.Usage` keep
their Core half; the Infra and Audit halves move under their parts, and a tier file that left one opens the Core
half to keep seeing it. `ToolUp.Platform.Tracing` was declared only by Infra (the OpenTelemetry companion declares a
module beneath it), so it moves whole to `ToolUp.Platform.Infra.Tracing`; the companion's module keeps its own
path. `ToolUp.Platform.Teams` moves whole to `ToolUp.Platform.Scope.Teams`. Two modules were split across Core and
a part under one name — `ToolUp.Platform.BlobStorage` (Core's interface, Infra's helpers) and `ToolUp.Platform.Logger`
— and six modules declared no namespace at all (`EncryptedSecretStore`, `FileSecretStore`, `HeaderAuthProvider`,
`StaticJwtAuthProvider`, `LocalFileStorage`, `EnvironmentSecretStore`); all take their part's namespace, and the
shipped table records where each came from.

## Run the codemod

```
pwsh ./dev-scripts/codemod-1198-namespaces.ps1 -Path <your-source-dir> -Check   # the diff, nothing written
pwsh ./dev-scripts/codemod-1198-namespaces.ps1 -Path <your-source-dir>          # apply, then Fantomas
```

The script runs the Build project's `Codemod` target with `--phase 1198` — the same migration a consumer's own
Build project reaches with `dotnet run --project Build.fsproj -- Codemod <dir> --phase 1198`, since the pass and
its name table ship inside `ToolUp.Platform.Build`. It is named, never implied: the default every-migration pass
does not run it, because its rewrites are derived from a table rather than fixed renames.

Three things it does to a file whose project reaches the server tier, reading only the file's code. A string of any
spelling (regular, verbatim, triple-quoted, across lines) and a comment are data, so an `open` line, a path or a name
inside one is not touched and derives nothing; an interpolated string's holes are code.

1. **Rewrites an `open` of a path that moved.** `open ToolUp.Platform.TeamManagement` becomes
   `open ToolUp.Platform.Config.TeamManagement`; `open ToolUp.Platform.Teams` becomes `open ToolUp.Platform.Scope.Teams`;
   `open ToolUp.Platform.Metrics` stays, because Core still declares it.
2. **Rewrites a qualified path that moved**, outside strings and comments: `ToolUp.Platform.Server.compose` becomes
   `ToolUp.Platform.Compose.Server.compose`; a project path or package id such as `ToolUp.Platform.Server.Jobs` is
   left alone.
3. **Derives the opens the file needs from the names it mentions** — every namespace-level type, module and
   exception of the tier, every union case, and the members of its `[<AutoOpen>]` modules, read from the shipped
   table — in the parts' dependency order, where [Where the opens go](#where-the-opens-go-and-why) says. Never a
   blanket list: a file that names nothing from the Jobs part does not open it. A qualified mention through a Core
   module whose nested module moved (`Auth.IdentitySanitiser`) opens the namespace above the moved one. Record
   fields are deliberately not read: `Decision = …` says nothing about which record is being built, and the
   tier's field names overlap Core's far too often.

   A union case derives less readily than a type, because its name says nothing about whose union it is. A case
   name derives no open when any file in the tree declares that name as a type, case, record field, exception or
   active pattern, when the file already resolves it through an open outside the tier (`Scalar` in a file that
   opens `ToolUp.Facts`), or when it is a record label (`{| Existing = n |}`). An open of `ToolUp.Platform` does not count, because a server case won over a Core case of
   the same spelling. If your tree declares `Required` and one file really does mean the tier's, that file fails to
   compile at the site and takes the open by hand.

   A name Core and the tier both declare (`ServerConfig`, `ScopeId`, `BlobStorage`) derives an open only when the file
   names one of the tier module's members: `ServerConfig.fromEnv` opens `ToolUp.Platform.Config`, while the bare
   `ScopeId` is Core's type and opens nothing. Where the tier's half is a type (`EgressPolicy`, a module in Core), the
   bare name is the use and does open it.

Which projects it touches is read from the project files: a project that references the server tier (a part
project, a `ToolUp.Platform.Server*` or `ToolUp.Sdk` package, an imported `.Server.props`, a project that reaches
one of those, or a project that compiles tier sources against Core) is in scope; a Fable or Core-only project is
not, and a file compiled by both kinds — a source linked into a Fable project as well as a server one — takes no
open the Fable compile could not resolve. A tier file compiled by a project that does not reference the parts (a
test pack over a few server files and Core) may open only the tier namespaces those sources declare.

**Idempotent.** A second run finds every open in place and changes nothing.

**Opens the move leaves dead are reported, never deleted.** An open of `ToolUp.Platform` or of a moved path, where
the code it covers names nothing it reaches, appears as a `1198-likely-dead-open` review site. Remove it if the file
still compiles. The tables hold no record labels and no extension members, so a file that reaches Core only through
those is reported too, and the compiler is what decides. The one open the pass removes itself is in a nested module:
see below.

### Where the opens go, and why

Resolution is kept as it was while everything shared `ToolUp.Platform`, where a later open shadows an earlier one: a
server name won over a Core name of the same spelling, and a module a file opened itself won over both.

- **A file that opens `ToolUp.Platform`** takes the part opens straight after that open, with its other opens still
  below them, so its own modules keep shadowing the parts.
- **A file that does not** takes them above its first open, or after its `namespace` / `module` header when it has no
  open at all (whatever the header's spelling, a double-backticked name included).
- **A tier file that lacks `open ToolUp.Platform`** takes it straight after its header, the part opens right after it,
  and both before the file's own opens.
- **A nested module with opens of its own** takes what its body needs inside the module, indented with those opens:
  beside its `open ToolUp.Platform`, or above its first open when it has none. If its body reaches nothing through
  `ToolUp.Platform`, the part opens replace that open. A nested module with no opens is covered from namespace level.

The script as tagged at 0.39.0 appended a consumer's part opens below its last open, which let a forge name shadow the
consumer's, and inserted a nested module's opens at namespace level; re-order those by hand if you ran it.

## What the codemod leaves to you

Where the two halves of the old namespace genuinely collide, the codemod cannot know which was meant, and the
site takes a qualification or a type annotation by hand. In this repository that was 24 sites in the Platform test
pack and 6 in the tier and its companions, in these shapes:

- **A union case two unions spell alike**, where the file meant the other half: `Allow` / `Deny`
  (`AuthDecision` and `ClientToolAuthDecision` in Core against `ClassificationDecision` in Audit), `VersionConflict` /
  `NotFound` (`EntityError` in Core against `ConditionalDeleteError` in Infra), `Scheduled` (`PublishStatus` in Core
  against `ScheduleOutcome` in Jobs), `Unauthorized` / `Forbidden` / `NetworkError` (`AuthError` in Core against
  `OAuthError`; `GitHubApiError` in the GitHub provider). Qualify the case: `AuthDecision.Allow`.
- **A record whose labels two records share** (`ProvenanceEdge` against Core's `WireProvenanceEdge`, `ConfigKnob`
  against `InspectedKnob`): annotate the literal, `({ From = f; To = t; Kind = k }: ProvenanceEdge)`.
- **An interface two assemblies name alike**: `IRateLimitStore` is both `ToolUp.Remoting.Server.IRateLimitStore`
  and `ToolUp.Platform.Infra.IRateLimitStore`; qualify the one you mean.
- **A file that was declared in the remoting library's namespace but is the platform's** (`IdempotencySweepJob`,
  now `ToolUp.Platform.Jobs`) opens `ToolUp.Remoting.Server` for the layout it reads.

A nested module whose `open ToolUp.Platform` was replaced, but which built a Core record by its labels alone, fails to
compile at the literal; put the open back beside the part opens.

Each compiles to a clear error at the site, and the fix is one qualifier. Nothing is silent: a name that resolves
to the wrong half is a type error, never a changed behaviour, because the two halves never share a shape.

**A documentation snippet** is an excerpt, not a program, and the doc-snippets harness supplies its context
out-of-band: the harness's preamble now opens every part, exactly the context `open ToolUp.Platform` gave a snippet
before the move, so a page does not grow `open` lines a reader would copy. The two README blocks that were red
before this change are the two still red after it.

## The static opens map

The acceptance's evidence: with one namespace, a static "which lists open which parts" map selected every list
for any server diff — 100 % of the Platform pack's registered lists open `ToolUp.Platform`. After the move, for each
part, the share of the pack's registered test lists whose source file opens that part's namespace (a top-level
`open`, or a qualified use), computed over tree `06adf017` (the tree the four source commits leave; the lists and their opens are unchanged by the documentation commit that follows):

| Part namespace | Lists opening it | Share of resolved lists |
|---|---|---|
| `ToolUp.Remoting.Server` | 29 | 4.3% |
| `ToolUp.Platform.Infra` | 392 | 57.7% |
| `ToolUp.Platform.Config` | 260 | 38.3% |
| `ToolUp.Platform.Scope` | 124 | 18.3% |
| `ToolUp.Platform.Jobs` | 199 | 29.3% |
| `ToolUp.Platform.Audit` | 128 | 18.9% |
| `ToolUp.Platform.Compose` | 423 | 62.3% |
| `ToolUp.Platform.Files` | 7 | 1.0% |

A one-part diff therefore selects at most 62 % of the lists (the umbrella's composition root, which the whole pack
builds on), and under a fifth of them for Scope, Audit or the Remoting part. These are the figures Phase 1194 (the
Platform pack partitioned by part) starts from; the lists themselves and the names they open are in
`src/ToolUp.Platform.Tests`, and the script that computes the table reads `Program.fs` for the registered lists and
each list's source file for its opens.

## Verification

- `dotnet build ToolUp.Forge.sln` — every project, the server tier and every consumer in the tree.
- `dotnet run --project Build.fsproj -- SubsystemNamespaces` — the shipped table matches a fresh scan of the tier.
- `dotnet run --project Build.fsproj -- Codemod . --phase 1198 --check` — zero rewrites pending over the migrated tree.
- The Build pack (`SubsystemNamespacesTests`: placement, the rewrite's six arms, the table's drift; `ServerPartsBaselineTests`
  unchanged and green: no token in two server baselines, parts reference only earlier parts).
- The Platform pack's `Phase 175 — Public-API approval baseline` list: every server part's baseline and 84
  companion baselines regenerated surgically by name (their signatures spell the moved types); Core's baseline is
  byte-identical.
- `VerifyFable`, `VerifyTemplates`, `VerifySamples`, `VerifyDocSnippets` (to the two pre-existing README blocks).

## Rollback

Revert the phase's commits. A consumer that already ran the codemod keeps compiling against 0.39.0 until it does.

## The map

Every file the seven part projects compile, with the header it declared before this change and the header it
declares now. "Part" is the project that compiles the file (the umbrella is `umbrella`); a file whose part is not
the one its name says is one of 1193's 53 reach files, and takes the namespace of the part it is placed in. Paths
are under `src/ToolUp.Platform.Server/Server/`.

| Part | File | Declared today | Declared after |
|---|---|---|---|
| Remoting | `Remoting/MsgPack/TypeShape.fs` | `module internal TypeShape` | `module internal TypeShape` |
| Remoting | `Remoting/MsgPack/TypeShapeUtils.fs` | `module internal TypeShape_Utils` | `module internal TypeShape_Utils` |
| Remoting | `Remoting/MsgPack/Write.fs` | `module ToolUp.Remoting.MsgPack.Write` | `module ToolUp.Remoting.MsgPack.Write` |
| Remoting | `Remoting/DecoderVerification.fs` | `namespace ToolUp.Remoting` | `namespace ToolUp.Remoting` |
| Remoting | `Remoting/Json/JsonDecoderVerification.fs` | `namespace ToolUp.Remoting.Json` | `namespace ToolUp.Remoting.Json` |
| Remoting | `Remoting/Json/JsonRead.fs` | `namespace ToolUp.Remoting.Json` | `namespace ToolUp.Remoting.Json` |
| Remoting | `Remoting/Json/SystemTextJsonConverter.fs` | `namespace ToolUp.Remoting.Json.SystemTextJson` | `namespace ToolUp.Remoting.Json.SystemTextJson` |
| Remoting | `Remoting/Types.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/CallContext.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Auth.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/BoundedFifo.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/RateLimit.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Audit.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Idempotency.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Validation.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Jobs.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Streaming.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/LongRunning.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/SourceGenDispatch.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/SchemaVersion.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Errors.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Diagnostics.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Patterns.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Documentation.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Proxy.fs` | `module ToolUp.Remoting.Server.Proxy` | `module ToolUp.Remoting.Server.Proxy` |
| Remoting | `Remoting/DocsApp.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Remoting.fs` | `namespace ToolUp.Remoting.Server` | `namespace ToolUp.Remoting.Server` |
| Remoting | `Remoting/Giraffe/GiraffeAdapter.fs` | `namespace ToolUp.Remoting.Giraffe` | `namespace ToolUp.Remoting.Giraffe` |
| Remoting | `Remoting/AspNetCore/Middleware.fs` | `namespace ToolUp.Remoting.AspNetCore` | `namespace ToolUp.Remoting.AspNetCore` |
| Remoting | `HttpBodyBinding.fs` | `module ToolUp.Platform.HttpBodyBinding` | `module ToolUp.Remoting.Server.Http.HttpBodyBinding` |
| Infra | `HttpClientTransport.fs` | `namespace ToolUp.Platform.Transport` | `namespace ToolUp.Platform.Infra.Transport` |
| Infra | `JwtCrypto.fs` | `module ToolUp.Platform.JwtCrypto` | `module ToolUp.Platform.Infra.JwtCrypto` |
| Infra | `DataVocabularyPins.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `BlobStorageSignedUrl.fs` | `module ToolUp.Platform.BlobStorage` | `module ToolUp.Platform.Infra.BlobStorage` |
| Infra | `Rag/IEmbeddingProvider.fs` | `module ToolUp.Platform.IEmbeddingProvider` | `module ToolUp.Platform.Infra.IEmbeddingProvider` |
| Infra | `IEmbeddingCache.fs` | `module ToolUp.Platform.IEmbeddingCache` | `module ToolUp.Platform.Infra.IEmbeddingCache` |
| Infra | `Rag/IVectorStore.fs` | `module ToolUp.Platform.IVectorStore` | `module ToolUp.Platform.Infra.IVectorStore` |
| Infra | `ISparseIndex.fs` | `module ToolUp.Platform.ISparseIndex` | `module ToolUp.Platform.Infra.ISparseIndex` |
| Infra | `IIndexLifecycle.fs` | `module ToolUp.Platform.IIndexLifecycle` | `module ToolUp.Platform.Infra.IIndexLifecycle` |
| Infra | `IReranker.fs` | `module ToolUp.Platform.IReranker` | `module ToolUp.Platform.Infra.IReranker` |
| Infra | `IQueryRewriter.fs` | `module ToolUp.Platform.IQueryRewriter` | `module ToolUp.Platform.Infra.IQueryRewriter` |
| Infra | `ITextSummariser.fs` | `module ToolUp.Platform.ITextSummariser` | `module ToolUp.Platform.Infra.ITextSummariser` |
| Infra | `IOcrProvider.fs` | `module ToolUp.Platform.IOcrProvider` | `module ToolUp.Platform.Infra.IOcrProvider` |
| Infra | `ITableExtractor.fs` | `module ToolUp.Platform.ITableExtractor` | `module ToolUp.Platform.Infra.ITableExtractor` |
| Infra | `IImageEmbedder.fs` | `module ToolUp.Platform.IImageEmbedder` | `module ToolUp.Platform.Infra.IImageEmbedder` |
| Infra | `Rag/IRetrievalPipeline.fs` | `module ToolUp.Platform.IRetrievalPipeline` | `module ToolUp.Platform.Infra.IRetrievalPipeline` |
| Infra | `IRetrievalTracer.fs` | `module ToolUp.Platform.IRetrievalTracer` | `module ToolUp.Platform.Infra.IRetrievalTracer` |
| Infra | `IRagTelemetry.fs` | `module ToolUp.Platform.IRagTelemetry` | `module ToolUp.Platform.Infra.IRagTelemetry` |
| Infra | `IMetricsSink.fs` | `namespace ToolUp.Platform.Metrics` | `namespace ToolUp.Platform.Infra.Metrics` |
| Infra | `PrometheusMetricsSink.fs` | `namespace ToolUp.Platform.Metrics` | `namespace ToolUp.Platform.Infra.Metrics` |
| Infra | `IActivitySink.fs` | `namespace ToolUp.Platform.Tracing` | `namespace ToolUp.Platform.Infra.Tracing` |
| Infra | `IEdgeCache.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DatadogReadbackMetrics.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `Logger.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `LoggerScope.fs` | `module ToolUp.Platform.LoggerScope` | `module ToolUp.Platform.Infra.LoggerScope` |
| Infra | `Infra/JsonConsoleLogger.fs` | `module ToolUp.Platform.JsonConsoleLogger` | `module ToolUp.Platform.Infra.JsonConsoleLogger` |
| Infra | `Infra/InMemoryEventStore.fs` | `module ToolUp.Platform.InMemoryEventStore` | `module ToolUp.Platform.Infra.InMemoryEventStore` |
| Infra | `Infra/LocalFileStorage.fs` | `module LocalFileStorage` | `module ToolUp.Platform.Infra.LocalFileStorage` |
| Infra | `Infra/InMemoryTimeSeriesStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `Infra/SqliteLogStore.fs` | `module ToolUp.Platform.SqliteLogStore` | `module ToolUp.Platform.Infra.SqliteLogStore` |
| Infra | `Infra/NoOpLogStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `LogStoreLogger.fs` | `module ToolUp.Platform.LogStoreLogger` | `module ToolUp.Platform.Infra.LogStoreLogger` |
| Infra | `SecondaryIndex.fs` | `module ToolUp.Platform.SecondaryIndex` | `module ToolUp.Platform.Infra.SecondaryIndex` |
| Infra | `Infra/PersistentEventStore.fs` | `module ToolUp.Platform.PersistentEventStore` | `module ToolUp.Platform.Infra.PersistentEventStore` |
| Infra | `IDataObjectStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DataObjectStore.fs` | `module ToolUp.Platform.DataObjectStore` | `module ToolUp.Platform.Infra.DataObjectStore` |
| Infra | `SidecarStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DataProvenanceLabels.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IDatasetStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `BlobDatasetStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IColumnMappingStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ColumnMappingStore.fs` | `module ToolUp.Platform.ColumnMappingStore` | `module ToolUp.Platform.Infra.ColumnMappingStore` |
| Infra | `IngestionStatusStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IEntityStore.fs` | `module ToolUp.Platform.IEntityStore` | `module ToolUp.Platform.Infra.IEntityStore` |
| Infra | `EntityOutbox.fs` | `module ToolUp.Platform.EntityOutbox` | `module ToolUp.Platform.Infra.EntityOutbox` |
| Infra | `EntityOutboxRelayService.fs` | `module ToolUp.Platform.EntityOutboxRelayService` | `module ToolUp.Platform.Infra.EntityOutboxRelayService` |
| Infra | `EntityQueryExecutor.fs` | `module ToolUp.Platform.EntityQueryExecutor` | `module ToolUp.Platform.Infra.EntityQueryExecutor` |
| Infra | `EntityStore.fs` | `module ToolUp.Platform.EntityStore` | `module ToolUp.Platform.Infra.EntityStore` |
| Infra | `IUserSchemaStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `BlobUserSchemaStore.fs` | `module ToolUp.Platform.BlobUserSchemaStore` | `module ToolUp.Platform.Infra.BlobUserSchemaStore` |
| Infra | `TenantEntity.fs` | `module ToolUp.Platform.TenantEntity` | `module ToolUp.Platform.Infra.TenantEntity` |
| Infra | `IBuildOrchestrator.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ITenantFleet.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IDeployPipeline.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IContainerScheduler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DeployPlanner.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IExternalComputeDispatcher.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IComputeBackendRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `RoutingComputeDispatcher.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `MemoizedComputeDispatcher.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IBudgetLedger.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `BudgetLedger.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IComputeBudgetStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ComputeBudgetStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `BudgetedComputeDispatcher.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IExternalHandleStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `WorkerKeyRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `SignedOutcomeVerifier.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `EntityStoreTenantFleet.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IDataCatalog.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `SyntheticSampleGenerator.fs` | `module ToolUp.Platform.SyntheticSampleGenerator` | `module ToolUp.Platform.Infra.SyntheticSampleGenerator` |
| Infra | `DataCatalog.fs` | `module ToolUp.Platform.DataCatalog` | `module ToolUp.Platform.Infra.DataCatalog` |
| Infra | `SchemaOnlyGuard.fs` | `module ToolUp.Platform.SchemaOnlyGuard` | `module ToolUp.Platform.Infra.SchemaOnlyGuard` |
| Infra | `IResultStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ISeedPack.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ILineageStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `LineageStore.fs` | `module ToolUp.Platform.LineageStore` | `module ToolUp.Platform.Infra.LineageStore` |
| Infra | `IProvenanceGraph.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ProvenanceGraph.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ProvenanceApiHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ModelFitTypes.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DatasetProvenanceOps.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IModelFitProvider.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ModelRegistryTypes.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IModelRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `BlobModelRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ModelRegistrationObservers.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ModelTransitionSeam.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ModelArtifactProvenance.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ModelPromotionTransfer.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IModelScorer.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DatasetAssemblyTypes.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `ModelEvaluationTypes.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DeployRecords.fs` | `module ToolUp.Platform.DeployRecords` | `module ToolUp.Platform.Infra.DeployRecords` |
| Infra | `RestoreClosures.fs` | `module ToolUp.Platform.RestoreClosures` | `module ToolUp.Platform.Infra.RestoreClosures` |
| Infra | `WorkProvenanceSource.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `SbomProjection.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DefaultDeployPipeline.fs` | `module ToolUp.Platform.DefaultDeployPipeline` | `module ToolUp.Platform.Infra.DefaultDeployPipeline` |
| Infra | `IDataSource.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `VirtualDatasetBinding.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IOAuthCredentialFlow.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `OAuth1aSigner.fs` | `module ToolUp.Platform.OAuth1aSigner` | `module ToolUp.Platform.Infra.OAuth1aSigner` |
| Infra | `IOAuth1aFlow.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IOAuth1aStateStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `InMemoryOAuth1aStateStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IOAuthTokenRefresher.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IOAuthStateStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `OAuthStateStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IDataSourceConfigStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IDataIngestor.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `InMemoryDataSource.fs` | `module ToolUp.Platform.InMemoryDataSource` | `module ToolUp.Platform.Infra.InMemoryDataSource` |
| Infra | `DataSourceConfigStore.fs` | `module ToolUp.Platform.DataSourceConfigStore` | `module ToolUp.Platform.Infra.DataSourceConfigStore` |
| Infra | `IPayloadReader.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `DataIngestor.fs` | `module ToolUp.Platform.DataIngestor` | `module ToolUp.Platform.Infra.DataIngestor` |
| Infra | `Infra/EnvironmentSecretStore.fs` | `module EnvironmentSecretStore` | `module ToolUp.Platform.Infra.EnvironmentSecretStore` |
| Infra | `Infra/TransientFaultPolicy.fs` | `module ToolUp.Platform.TransientFault` | `module ToolUp.Platform.Infra.TransientFault` |
| Infra | `Infra/ResilientBlobStorage.fs` | `module ToolUp.Platform.ResilientBlobStorage` | `module ToolUp.Platform.Infra.ResilientBlobStorage` |
| Infra | `Infra/ResilientSecretStore.fs` | `module ToolUp.Platform.ResilientSecretStore` | `module ToolUp.Platform.Infra.ResilientSecretStore` |
| Infra | `IBlobEncryptionKeyResolver.fs` | `module ToolUp.Platform.BlobEncryption` | `module ToolUp.Platform.Infra.BlobEncryption` |
| Infra | `EncryptedBlobStorage.fs` | `module ToolUp.Platform.EncryptedBlobStorage` | `module ToolUp.Platform.Infra.EncryptedBlobStorage` |
| Infra | `SingleKeyResolver.fs` | `module ToolUp.Platform.SingleKeyResolver` | `module ToolUp.Platform.Infra.SingleKeyResolver` |
| Infra | `NoOpRateLimiter.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `InProcessRateLimiter.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IConversationStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IPlatformAdminStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IPlatformRuntimeConfigStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `Infra/PlatformRuntimeConfigStore.fs` | `module ToolUp.Platform.PlatformRuntimeConfigStore` | `module ToolUp.Platform.Infra.PlatformRuntimeConfigStore` |
| Infra | `PlatformAdminDevDiagnostics.fs` | `module ToolUp.Platform.PlatformAdminDevDiagnostics` | `module ToolUp.Platform.Infra.PlatformAdminDevDiagnostics` |
| Infra | `BlobMapStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `IRateLimitStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `InMemoryRateLimitStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `Providers/BlobProviderProfile.fs` | `module ToolUp.Platform.BlobProviderProfile` | `module ToolUp.Platform.Infra.BlobProviderProfile` |
| Infra | `Providers/IProviderOAuthFlow.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `Providers/IProviderEntryProbe.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `Providers/ProviderOAuthFlowBuilder.fs` | `module ToolUp.Platform.ProviderOAuthFlow` | `module ToolUp.Platform.Infra.ProviderOAuthFlow` |
| Infra | `IUsageLog.fs` | `namespace ToolUp.Platform.Usage` | `namespace ToolUp.Platform.Infra.Usage` |
| Infra | `UsageLog.fs` | `module ToolUp.Platform.UsageLog` | `module ToolUp.Platform.Infra.UsageLog` |
| Infra | `ITeamQuotaPolicy.fs` | `namespace ToolUp.Platform.Usage` | `namespace ToolUp.Platform.Infra.Usage` |
| Infra | `ExternalComputeCallback.fs` | `module ToolUp.Platform.ExternalComputeCallback` | `module ToolUp.Platform.Infra.ExternalComputeCallback` |
| Infra | `HealthCheck.fs` | `module ToolUp.Platform.HealthCheck` | `module ToolUp.Platform.Infra.HealthCheck` |
| Infra | `DegradedCapabilities.fs` | `module ToolUp.Platform.DegradedCapabilities` | `module ToolUp.Platform.Infra.DegradedCapabilities` |
| Infra | `HealthCheckResponseWriter.fs` | `module ToolUp.Platform.HealthCheckResponseWriter` | `module ToolUp.Platform.Infra.HealthCheckResponseWriter` |
| Infra | `HealthCheckAggregator.fs` | `module ToolUp.Platform.HealthCheckAggregator` | `module ToolUp.Platform.Infra.HealthCheckAggregator` |
| Infra | `HealthCheckRunner.fs` | `module ToolUp.Platform.HealthCheckRunner` | `module ToolUp.Platform.Infra.HealthCheckRunner` |
| Infra | `TelemetryApiHandler.fs` | `module ToolUp.Platform.TelemetryApiHandler` | `module ToolUp.Platform.Infra.TelemetryApiHandler` |
| Infra | `PlatformAdmin/RateLimitEventApi.fs` | `module ToolUp.Platform.RateLimitEventApi` | `module ToolUp.Platform.Infra.RateLimitEventApi` |
| Infra | `DatadogReadbackHandlers.fs` | `module ToolUp.Platform.DatadogReadbackHandlers` | `module ToolUp.Platform.Infra.DatadogReadbackHandlers` |
| Infra | `RequestTimingMiddleware.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `MetricsEndpoint.fs` | `module ToolUp.Platform.MetricsEndpoint` | `module ToolUp.Platform.Infra.MetricsEndpoint` |
| Infra | `Static/PrerenderedRoutesHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `Static/SpaFallbackMiddleware.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `RateLimiting.fs` | `module ToolUp.Platform.RateLimiting` | `module ToolUp.Platform.Infra.RateLimiting` |
| Infra | `IModuleVisibilityStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `INarrativeIngestor.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `HealthStateTracker.fs` | `module ToolUp.Platform.HealthStateTracker` | `module ToolUp.Platform.Infra.HealthStateTracker` |
| Infra | `AlertRuleEngine.fs` | `module ToolUp.Platform.AlertRuleEngine` | `module ToolUp.Platform.Infra.AlertRuleEngine` |
| Infra | `MetricsHistoryFlusher.fs` | `module ToolUp.Platform.MetricsHistoryFlusher` | `module ToolUp.Platform.Infra.MetricsHistoryFlusher` |
| Infra | `ObservabilityHandlers.fs` | `module ToolUp.Platform.ObservabilityHandlers` | `module ToolUp.Platform.Infra.ObservabilityHandlers` |
| Infra | `RateLimitMiddleware.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Infra | `BlobXmlRepository.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Infra` |
| Config | `Config/ConfigResolution.fs` | `module ToolUp.Platform.ConfigResolution` | `module ToolUp.Platform.Config.ConfigResolution` |
| Config | `ServerBinding.fs` | `module ToolUp.Platform.ServerBinding` | `module ToolUp.Platform.Config.ServerBinding` |
| Config | `Config/ServerConfigFromEnv.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `ConfigProfiles.fs` | `module ToolUp.Platform.ConfigProfiles` | `module ToolUp.Platform.Config.ConfigProfiles` |
| Config | `ConfigResolver.fs` | `module ToolUp.Platform.ConfigResolver` | `module ToolUp.Platform.Config.ConfigResolver` |
| Config (reach; named Infra) | `Infra/ConsoleLogger.fs` | `module ToolUp.Platform.ConsoleLogger` | `module ToolUp.Platform.Config.ConsoleLogger` |
| Config (reach; named Infra) | `Api.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config (reach; named Jobs) | `Notifications/NotificationChannel.fs` | `module ToolUp.Platform.NotificationChannel` | `module ToolUp.Platform.Config.NotificationChannel` |
| Config (reach; named Jobs) | `Background.fs` | `module ToolUp.Platform.Background` | `module ToolUp.Platform.Config.Background` |
| Config (reach; named Scope) | `src/ToolUp.Platform.Server/Auth/IdentitySanitiser.fs` | `module ToolUp.Platform.Auth.IdentitySanitiser` | `module ToolUp.Platform.Config.Auth.IdentitySanitiser` |
| Config (reach; named Scope) | `src/ToolUp.Platform.Server/Auth/HeaderAuthProvider.fs` | `module HeaderAuthProvider` | `module ToolUp.Platform.Config.HeaderAuthProvider` |
| Config (reach; named Infra) | `Infra/BlobStorageFromEnv.fs` | `module ToolUp.Platform.BlobStorageEnv` | `module ToolUp.Platform.Config.BlobStorageEnv` |
| Config (reach; named Infra) | `Infra/EmbeddingProviderFromEnv.fs` | `module ToolUp.Platform.EmbeddingProviderEnv` | `module ToolUp.Platform.Config.EmbeddingProviderEnv` |
| Config | `IMappingDryRunValidator.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `MappingDryRunValidator.fs` | `module ToolUp.Platform.MappingDryRunValidator` | `module ToolUp.Platform.Config.MappingDryRunValidator` |
| Config (reach; named Infra) | `ResultStore.fs` | `module ToolUp.Platform.ResultStore` | `module ToolUp.Platform.Config.ResultStore` |
| Config (reach; named Infra) | `Infra/FileSecretStore.fs` | `module FileSecretStore` | `module ToolUp.Platform.Config.FileSecretStore` |
| Config (reach; named Infra) | `Infra/EncryptedSecretStore.fs` | `module EncryptedSecretStore` | `module ToolUp.Platform.Config.EncryptedSecretStore` |
| Config (reach; named Infra) | `Infra/SecretStoreFromEnv.fs` | `module ToolUp.Platform.SecretStore` | `module ToolUp.Platform.Config.SecretStore` |
| Config (reach; named Infra) | `ConversationStore.fs` | `module ToolUp.Platform.ConversationStore` | `module ToolUp.Platform.Config.ConversationStore` |
| Config (reach; named Infra) | `Providers/ProviderOAuthConnect.fs` | `module ToolUp.Platform.ProviderOAuthConnect` | `module ToolUp.Platform.Config.ProviderOAuthConnect` |
| Config (reach; named Infra) | `PerScopeKeyResolver.fs` | `module ToolUp.Platform.PerScopeKeyResolver` | `module ToolUp.Platform.Config.PerScopeKeyResolver` |
| Config | `LocalStorageEncryptionValidator.fs` | `module ToolUp.Platform.LocalStorageEncryptionValidator` | `module ToolUp.Platform.Config.LocalStorageEncryptionValidator` |
| Config | `PerScopeKeyResolverDistributedValidator.fs` | `module ToolUp.Platform.PerScopeKeyResolverDistributedValidator` | `module ToolUp.Platform.Config.PerScopeKeyResolverDistributedValidator` |
| Config | `KeyDestroyAckCoverageValidator.fs` | `module ToolUp.Platform.KeyDestroyAckCoverageValidator` | `module ToolUp.Platform.Config.KeyDestroyAckCoverageValidator` |
| Config | `ConfigValidator.fs` | `module ToolUp.Platform.ConfigValidator` | `module ToolUp.Platform.Config.ConfigValidator` |
| Config | `BlobStorageSelectionValidator.fs` | `module ToolUp.Platform.BlobStorageSelectionValidator` | `module ToolUp.Platform.Config.BlobStorageSelectionValidator` |
| Config | `ServiceStatusBoardDepsValidator.fs` | `module ToolUp.Platform.ServiceStatusBoardDepsValidator` | `module ToolUp.Platform.Config.ServiceStatusBoardDepsValidator` |
| Config | `UnknownConfigKeyValidator.fs` | `module ToolUp.Platform.UnknownConfigKeyValidator` | `module ToolUp.Platform.Config.UnknownConfigKeyValidator` |
| Config | `TraceCategoriesValidator.fs` | `module ToolUp.Platform.TraceCategoriesValidator` | `module ToolUp.Platform.Config.TraceCategoriesValidator` |
| Config | `EventStoreChainValidator.fs` | `module ToolUp.Platform.EventStoreChainValidator` | `module ToolUp.Platform.Config.EventStoreChainValidator` |
| Config | `SurfaceCoherenceValidator.fs` | `module ToolUp.Platform.SurfaceCoherenceValidator` | `module ToolUp.Platform.Config.SurfaceCoherenceValidator` |
| Config (reach; named Infra) | `SmokeTestHandler.fs` | `module ToolUp.Platform.SmokeTestHandler` | `module ToolUp.Platform.Config.SmokeTestHandler` |
| Config (reach; named Scope) | `IUserClaims.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `EncryptedSecretStoreModeValidator.fs` | `module ToolUp.Platform.EncryptedSecretStoreModeValidator` | `module ToolUp.Platform.Config.EncryptedSecretStoreModeValidator` |
| Config | `OAuthSecretEncryptionModeValidator.fs` | `module ToolUp.Platform.OAuthSecretEncryptionModeValidator` | `module ToolUp.Platform.Config.OAuthSecretEncryptionModeValidator` |
| Config | `SecretStoreAtRestPostureValidator.fs` | `module ToolUp.Platform.SecretStoreAtRestPostureValidator` | `module ToolUp.Platform.Config.SecretStoreAtRestPostureValidator` |
| Config | `PublicBaseUrlFormatValidator.fs` | `module ToolUp.Platform.PublicBaseUrlFormatValidator` | `module ToolUp.Platform.Config.PublicBaseUrlFormatValidator` |
| Config | `OAuthStateStoreInstanceValidator.fs` | `module ToolUp.Platform.OAuthStateStoreInstanceValidator` | `module ToolUp.Platform.Config.OAuthStateStoreInstanceValidator` |
| Config | `RateLimitModeValidator.fs` | `module ToolUp.Platform.RateLimitModeValidator` | `module ToolUp.Platform.Config.RateLimitModeValidator` |
| Config | `RateLimitConfigValidator.fs` | `module ToolUp.Platform.RateLimitConfigValidator` | `module ToolUp.Platform.Config.RateLimitConfigValidator` |
| Config | `RateLimiterInstanceValidator.fs` | `module ToolUp.Platform.RateLimiterInstanceValidator` | `module ToolUp.Platform.Config.RateLimiterInstanceValidator` |
| Config | `IdempotencyStoreInstanceValidator.fs` | `module ToolUp.Platform.IdempotencyStoreInstanceValidator` | `module ToolUp.Platform.Config.IdempotencyStoreInstanceValidator` |
| Config | `StaticPathBehaviourValidator.fs` | `module ToolUp.Platform.StaticPathBehaviourValidator` | `module ToolUp.Platform.Config.StaticPathBehaviourValidator` |
| Config | `MaxRequestBodyBytesValidator.fs` | `module ToolUp.Platform.MaxRequestBodyBytesValidator` | `module ToolUp.Platform.Config.MaxRequestBodyBytesValidator` |
| Config | `IntConfigKeyValidator.fs` | `module ToolUp.Platform.IntConfigKeyValidator` | `module ToolUp.Platform.Config.IntConfigKeyValidator` |
| Config | `EventStoreRetentionValidator.fs` | `module ToolUp.Platform.EventStoreRetentionValidator` | `module ToolUp.Platform.Config.EventStoreRetentionValidator` |
| Config | `CorsConfigValidator.fs` | `module ToolUp.Platform.CorsConfigValidator` | `module ToolUp.Platform.Config.CorsConfigValidator` |
| Config | `LocalSecretFilePermissionsValidator.fs` | `module ToolUp.Platform.LocalSecretFilePermissionsValidator` | `module ToolUp.Platform.Config.LocalSecretFilePermissionsValidator` |
| Config | `OAuthFlowValidator.fs` | `module ToolUp.Platform.OAuthFlowValidator` | `module ToolUp.Platform.Config.OAuthFlowValidator` |
| Config (reach; named Scope) | `Teams/TeamManagement.fs` | `module ToolUp.Platform.TeamManagement` | `module ToolUp.Platform.Config.TeamManagement` |
| Config (reach; named Infra) | `MaintenanceApiHandler.fs` | `module ToolUp.Platform.MaintenanceApiHandler` | `module ToolUp.Platform.Config.MaintenanceApiHandler` |
| Config (reach; named Infra) | `OAuth1aFlowHandler.fs` | `module ToolUp.Platform.OAuth1aFlowHandler` | `module ToolUp.Platform.Config.OAuth1aFlowHandler` |
| Config | `MigrationRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `MigrationStatusStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `MigrationRunner.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `MigrationApiHandler.fs` | `module ToolUp.Platform.MigrationApiHandler` | `module ToolUp.Platform.Config.MigrationApiHandler` |
| Config | `Config/IConfigStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `Config/ConfigStore.fs` | `module ToolUp.Platform.ConfigStore` | `module ToolUp.Platform.Config.ConfigStore` |
| Config | `Config/ConfigMigrationRegistry.fs` | `module ToolUp.Platform.ConfigMigrationRegistry` | `module ToolUp.Platform.Config.ConfigMigrationRegistry` |
| Config | `Config/ConfigMigrationDevDiagnostics.fs` | `module ToolUp.Platform.ConfigMigrationDevDiagnostics` | `module ToolUp.Platform.Config.ConfigMigrationDevDiagnostics` |
| Config | `Config/ConfigHandler.fs` | `module ToolUp.Platform.ConfigHandler` | `module ToolUp.Platform.Config.ConfigHandler` |
| Config (reach; named Infra) | `UserSchemaApiHandler.fs` | `module ToolUp.Platform.UserSchemaApiHandler` | `module ToolUp.Platform.Config.UserSchemaApiHandler` |
| Config | `IFeatureFlagStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `IFlagSource.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `FeatureFlagStore.fs` | `module ToolUp.Platform.FeatureFlagStore` | `module ToolUp.Platform.Config.FeatureFlagStore` |
| Config | `FeatureFlagSources.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Config` |
| Config | `FlagEvaluator.fs` | `module ToolUp.Platform.FlagEvaluator` | `module ToolUp.Platform.Config.FlagEvaluator` |
| Config | `FeatureFlagHandler.fs` | `module ToolUp.Platform.FeatureFlagHandler` | `module ToolUp.Platform.Config.FeatureFlagHandler` |
| Config (reach; named Audit) | `RedactionAllowlist.fs` | `module ToolUp.Platform.RedactionAllowlist` | `module ToolUp.Platform.Config.RedactionAllowlist` |
| Config | `ConfigDriftDetector.fs` | `module ToolUp.Platform.ConfigDriftDetector` | `module ToolUp.Platform.Config.ConfigDriftDetector` |
| Config | `DataProtectionBackendValidator.fs` | `module ToolUp.Platform.DataProtectionBackendValidator` | `module ToolUp.Platform.Config.DataProtectionBackendValidator` |
| Config | `LocaleResolver.fs` | `module ToolUp.Platform.LocaleResolver` | `module ToolUp.Platform.Config.LocaleResolver` |
| Scope | `Auth/RequestContext.fs` | `module ToolUp.Platform.RequestContextBuilder` | `module ToolUp.Platform.Scope.RequestContextBuilder` |
| Scope | `src/ToolUp.Platform.Server/Auth/StaticJwtAuthProvider.fs` | `module StaticJwtAuthProvider` | `module ToolUp.Platform.Scope.StaticJwtAuthProvider` |
| Scope | `src/ToolUp.Platform.Server/Auth/AuthProviderFromEnv.fs` | `module ToolUp.Platform.AuthProvider` | `module ToolUp.Platform.Scope.AuthProvider` |
| Scope | `IShareTokenStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `ShareTokenStore.fs` | `module ToolUp.Platform.ShareTokenStore` | `module ToolUp.Platform.Scope.ShareTokenStore` |
| Scope | `IServiceAccountStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `ServiceAccountStore.fs` | `module ToolUp.Platform.ServiceAccountStore` | `module ToolUp.Platform.Scope.ServiceAccountStore` |
| Scope | `TokenAttemptThrottle.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope (reach; named Infra) | `EncryptionAdminHandler.fs` | `module ToolUp.Platform.EncryptionAdminHandler` | `module ToolUp.Platform.Scope.EncryptionAdminHandler` |
| Scope | `AdminTokenValidator.fs` | `module ToolUp.Platform.AdminTokenValidator` | `module ToolUp.Platform.Scope.AdminTokenValidator` |
| Scope | `OidcConfigCompletenessValidator.fs` | `module ToolUp.Platform.OidcConfigCompletenessValidator` | `module ToolUp.Platform.Scope.OidcConfigCompletenessValidator` |
| Scope | `OidcAudienceBindingValidator.fs` | `module ToolUp.Platform.OidcAudienceBindingValidator` | `module ToolUp.Platform.Scope.OidcAudienceBindingValidator` |
| Scope | `OidcClaimMappingAdvisoryValidator.fs` | `module ToolUp.Platform.OidcClaimMappingAdvisoryValidator` | `module ToolUp.Platform.Scope.OidcClaimMappingAdvisoryValidator` |
| Scope | `HeaderAuthProviderModeValidator.fs` | `module ToolUp.Platform.HeaderAuthProviderModeValidator` | `module ToolUp.Platform.Scope.HeaderAuthProviderModeValidator` |
| Scope | `InviteEmailCapabilityValidator.fs` | `module ToolUp.Platform.InviteEmailCapabilityValidator` | `module ToolUp.Platform.Scope.InviteEmailCapabilityValidator` |
| Scope | `DirectAddIdentityProofValidator.fs` | `module ToolUp.Platform.DirectAddIdentityProofValidator` | `module ToolUp.Platform.Scope.DirectAddIdentityProofValidator` |
| Scope | `CsrfDefaultModeValidator.fs` | `module ToolUp.Platform.CsrfDefaultModeValidator` | `module ToolUp.Platform.Scope.CsrfDefaultModeValidator` |
| Scope | `Premium/PremiumGate.fs` | `module ToolUp.Platform.Premium.PremiumGate` | `module ToolUp.Platform.Scope.Premium.PremiumGate` |
| Scope | `Premium/GrantPremiumApiHandler.fs` | `module ToolUp.Platform.GrantPremiumApiHandler` | `module ToolUp.Platform.Scope.GrantPremiumApiHandler` |
| Scope | `PlatformAdmin/PremiumUserApi.fs` | `module ToolUp.Platform.PremiumUserApi` | `module ToolUp.Platform.Scope.PremiumUserApi` |
| Scope | `ShareTokenSigningKeyProvenanceValidator.fs` | `module ToolUp.Platform.ShareTokenSigningKeyProvenanceValidator` | `module ToolUp.Platform.Scope.ShareTokenSigningKeyProvenanceValidator` |
| Scope | `MultiInstanceAdminCoherenceValidator.fs` | `module ToolUp.Platform.MultiInstanceAdminCoherenceValidator` | `module ToolUp.Platform.Scope.MultiInstanceAdminCoherenceValidator` |
| Scope | `SecurityHeaders.fs` | `module ToolUp.Platform.SecurityHeaders` | `module ToolUp.Platform.Scope.SecurityHeaders` |
| Scope | `ICspContributor.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `GoogleIdentityCspValidator.fs` | `module ToolUp.Platform.GoogleIdentityCspValidator` | `module ToolUp.Platform.Scope.GoogleIdentityCspValidator` |
| Scope | `SecurityHardening.fs` | `module ToolUp.Platform.SecurityHardening` | `module ToolUp.Platform.Scope.SecurityHardening` |
| Scope | `SecurityHeadersValidator.fs` | `module ToolUp.Platform.SecurityHeadersValidator` | `module ToolUp.Platform.Scope.SecurityHeadersValidator` |
| Scope | `ForwardedHeadersTrustValidator.fs` | `module ToolUp.Platform.ForwardedHeadersTrustValidator` | `module ToolUp.Platform.Scope.ForwardedHeadersTrustValidator` |
| Scope | `CsrfHardeningValidator.fs` | `module ToolUp.Platform.CsrfHardeningValidator` | `module ToolUp.Platform.Scope.CsrfHardeningValidator` |
| Scope | `PeerRouteRegistry.fs` | `module ToolUp.Platform.PeerRouteRegistry` | `module ToolUp.Platform.Scope.PeerRouteRegistry` |
| Scope | `PeerBearerAuthMiddleware.fs` | `module ToolUp.Platform.PeerBearerAuthMiddleware` | `module ToolUp.Platform.Scope.PeerBearerAuthMiddleware` |
| Scope | `ShareTokenAuthMiddleware.fs` | `module ToolUp.Platform.ShareTokenAuth` | `module ToolUp.Platform.Scope.ShareTokenAuth` |
| Scope | `ServiceAccountTokenHandler.fs` | `module ToolUp.Platform.ServiceAccountTokenHandler` | `module ToolUp.Platform.Scope.ServiceAccountTokenHandler` |
| Scope | `SurfaceEnforcementMiddleware.fs` | `module ToolUp.Platform.SurfaceEnforcement` | `module ToolUp.Platform.Scope.SurfaceEnforcement` |
| Scope | `PeerBearerConfigValidator.fs` | `module ToolUp.Platform.PeerBearerConfigValidator` | `module ToolUp.Platform.Scope.PeerBearerConfigValidator` |
| Scope | `SecurityHeadersMiddleware.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `CspMiddleware.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `CsrfMiddleware.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `AuthSessionHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `Teams/MembershipDoctor.fs` | `module ToolUp.Platform.MembershipDoctor` | `module ToolUp.Platform.Scope.MembershipDoctor` |
| Scope | `Teams/InMemoryPendingInviteStore.fs` | `namespace ToolUp.Platform.Teams` | `namespace ToolUp.Platform.Scope.Teams` |
| Scope | `Teams/BlobPendingInviteStore.fs` | `namespace ToolUp.Platform.Teams` | `namespace ToolUp.Platform.Scope.Teams` |
| Scope | `Teams/PendingInviteStoreInstanceValidator.fs` | `module ToolUp.Platform.Teams.PendingInviteStoreInstanceValidator` | `module ToolUp.Platform.Scope.Teams.PendingInviteStoreInstanceValidator` |
| Scope | `Teams/TeamInvitationHandler.fs` | `module ToolUp.Platform.Teams.TeamInvitationHandler` | `module ToolUp.Platform.Scope.Teams.TeamInvitationHandler` |
| Scope | `Teams/TeamPolicyStore.fs` | `module ToolUp.Platform.TeamPolicyStore` | `module ToolUp.Platform.Scope.TeamPolicyStore` |
| Scope | `Infra/BootstrapTeam.fs` | `module ToolUp.Platform.BootstrapTeam` | `module ToolUp.Platform.Scope.BootstrapTeam` |
| Scope | `TeamCreationPolicyValidator.fs` | `module ToolUp.Platform.TeamCreationPolicyValidator` | `module ToolUp.Platform.Scope.TeamCreationPolicyValidator` |
| Scope | `BootstrapTeamValidator.fs` | `module ToolUp.Platform.BootstrapTeamValidator` | `module ToolUp.Platform.Scope.BootstrapTeamValidator` |
| Scope | `Scope/PermissionStore.fs` | `module ToolUp.Platform.PermissionStore` | `module ToolUp.Platform.Scope.PermissionStore` |
| Scope | `Scope/PublicationReaderGrantStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `GrantPolicyGuard.fs` | `module ToolUp.Platform.GrantPolicyGuard` | `module ToolUp.Platform.Scope.GrantPolicyGuard` |
| Scope | `GrantConsentStore.fs` | `module ToolUp.Platform.GrantConsentStore` | `module ToolUp.Platform.Scope.GrantConsentStore` |
| Scope | `Scope/AdminMutationApproval.fs` | `module ToolUp.Platform.AdminMutationApproval` | `module ToolUp.Platform.Scope.AdminMutationApproval` |
| Scope | `Scope/StoreIdSanitising.fs` | `module ToolUp.Platform.StoreIdSanitising` | `module ToolUp.Platform.Scope.StoreIdSanitising` |
| Scope | `Scope/PermissionStoreActionAuthorizer.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `UsageQueryApi.fs` | `module ToolUp.Platform.UsageQueryApiHandler` | `module ToolUp.Platform.Scope.UsageQueryApiHandler` |
| Scope | `ServiceAccountApiHandler.fs` | `module ToolUp.Platform.ServiceAccountApiHandler` | `module ToolUp.Platform.Scope.ServiceAccountApiHandler` |
| Scope | `PublicationReaderApiHandler.fs` | `module ToolUp.Platform.PublicationReaderApiHandler` | `module ToolUp.Platform.Scope.PublicationReaderApiHandler` |
| Scope | `ModuleVisibilityStore.fs` | `module ToolUp.Platform.ModuleVisibilityStore` | `module ToolUp.Platform.Scope.ModuleVisibilityStore` |
| Scope | `ModuleVisibilityResolver.fs` | `module ToolUp.Platform.ModuleVisibilityResolver` | `module ToolUp.Platform.Scope.ModuleVisibilityResolver` |
| Scope | `ModuleVisibilityApiHandler.fs` | `module ToolUp.Platform.ModuleVisibilityApiHandler` | `module ToolUp.Platform.Scope.ModuleVisibilityApiHandler` |
| Scope | `ModuleVisibilityRouteMiddleware.fs` | `module ToolUp.Platform.ModuleVisibilityRoutes` | `module ToolUp.Platform.Scope.ModuleVisibilityRoutes` |
| Scope | `AnonymousSessionBinding.fs` | `module ToolUp.Platform.AnonymousSessionBinding` | `module ToolUp.Platform.Scope.AnonymousSessionBinding` |
| Scope | `ISessionRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `SessionRegistry.fs` | `module ToolUp.Platform.SessionRegistry` | `module ToolUp.Platform.Scope.SessionRegistry` |
| Scope | `Scope/StorageScopeResolver.fs` | `module ToolUp.Platform.StorageScopeResolver` | `module ToolUp.Platform.Scope.StorageScopeResolver` |
| Scope | `Scope/RequestViewer.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `Scope/DefaultSubjectResolver.fs` | `module ToolUp.Platform.DefaultSubjectResolver` | `module ToolUp.Platform.Scope.DefaultSubjectResolver` |
| Scope | `NoOpAnonymousSessionMigrator.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Scope` |
| Scope | `SessionRevocationMiddleware.fs` | `module ToolUp.Platform.SessionRevocation` | `module ToolUp.Platform.Scope.SessionRevocation` |
| Scope | `SessionApiHandler.fs` | `module ToolUp.Platform.SessionApiHandler` | `module ToolUp.Platform.Scope.SessionApiHandler` |
| Scope | `PlatformAdminAuthorizationMiddleware.fs` | `module ToolUp.Platform.PlatformAdminAuthorization` | `module ToolUp.Platform.Scope.PlatformAdminAuthorization` |
| Scope | `AnonymousSessionMigrationMiddleware.fs` | `module ToolUp.Platform.AnonymousSessionMigration` | `module ToolUp.Platform.Scope.AnonymousSessionMigration` |
| Scope | `TeamScopedSecretStore.fs` | `module ToolUp.Platform.TeamScopedSecretStore` | `module ToolUp.Platform.Scope.TeamScopedSecretStore` |
| Scope | `TeamProviderResolver.fs` | `module ToolUp.Platform.TeamProviderResolver` | `module ToolUp.Platform.Scope.TeamProviderResolver` |
| Scope | `UserDirectoryApiHandler.fs` | `module ToolUp.Platform.UserDirectoryApiHandler` | `module ToolUp.Platform.Scope.UserDirectoryApiHandler` |
| Jobs | `Notifications/SSEConnectionManager.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `SseTraceContributor.fs` | `module ToolUp.Platform.SseTraceContributor` | `module ToolUp.Platform.Jobs.SseTraceContributor` |
| Jobs | `Notifications/PresenceChannel.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IPresenceTracker.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IEntityLockStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `ICrdtDocumentStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `InMemoryCrdtDocumentStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `BlobCrdtDocumentStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `INotificationSink.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IExternalContactStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `INotificationAddressBook.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `NotificationAddressBook.fs` | `module ToolUp.Platform.NotificationAddressBook` | `module ToolUp.Platform.Jobs.NotificationAddressBook` |
| Jobs | `IWhatsAppTemplateRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `WhatsAppTemplateRegistry.fs` | `module ToolUp.Platform.WhatsAppTemplateRegistry` | `module ToolUp.Platform.Jobs.WhatsAppTemplateRegistry` |
| Jobs | `Notifications/SseScopeResolution.fs` | `module ToolUp.Platform.SseScopeResolution` | `module ToolUp.Platform.Jobs.SseScopeResolution` |
| Jobs | `Notifications/NotificationHandler.fs` | `module ToolUp.Platform.NotificationHandler` | `module ToolUp.Platform.Jobs.NotificationHandler` |
| Jobs | `Notifications/LiveSessionHost.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `Notifications/TreePatchChannel.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `ExternalContactStore.fs` | `module ToolUp.Platform.ExternalContactStore` | `module ToolUp.Platform.Jobs.ExternalContactStore` |
| Jobs | `IDistributedLock.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `InProcessDistributedLock.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IJobStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IJobHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `RunJob.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `CarriedScopeToken.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IJobScheduler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IJobProgressSink.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs (reach; named Infra) | `ResourceEnvelopeEnforcement.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `SchemaMigrationJobHandler.fs` | `module ToolUp.Platform.SchemaMigrationJobHandler` | `module ToolUp.Platform.Jobs.SchemaMigrationJobHandler` |
| Jobs | `ModelFitJobHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs (reach; named Infra) | `ExternalModelFitProvider.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `ModelFitBatchItemJobHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `ModelScoringJobHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs (reach; named Infra) | `DatasetAssemblyExecutor.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs (reach; named Infra) | `ModelEvaluationRunner.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs (reach; named Infra) | `ModelPromotionPolicy.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `JobStore.fs` | `module ToolUp.Platform.JobStore` | `module ToolUp.Platform.Jobs.JobStore` |
| Jobs | `CronExpression.fs` | `module ToolUp.Platform.CronExpression` | `module ToolUp.Platform.Jobs.CronExpression` |
| Jobs | `JobTriggerWatermark.fs` | `module ToolUp.Platform.JobTriggerWatermark` | `module ToolUp.Platform.Jobs.JobTriggerWatermark` |
| Jobs | `SchedulerPolicy.fs` | `module ToolUp.Platform.SchedulerPolicy` | `module ToolUp.Platform.Jobs.SchedulerPolicy` |
| Jobs | `JobLifecycle.fs` | `module ToolUp.Platform.JobLifecycle` | `module ToolUp.Platform.Jobs.JobLifecycle` |
| Jobs | `JobScheduler.fs` | `module ToolUp.Platform.JobScheduler` | `module ToolUp.Platform.Jobs.JobScheduler` |
| Jobs | `IdempotencySweepJob.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `JobSchedulerDiagnosticsContributor.fs` | `module ToolUp.Platform.JobSchedulerDiagnosticsContributor` | `module ToolUp.Platform.Jobs.JobSchedulerDiagnosticsContributor` |
| Jobs | `JobNotifyEventStore.fs` | `module ToolUp.Platform.JobNotifyEventStore` | `module ToolUp.Platform.Jobs.JobNotifyEventStore` |
| Jobs | `JobSchedulerBuildOrchestrator.fs` | `module ToolUp.Platform.JobSchedulerBuildOrchestrator` | `module ToolUp.Platform.Jobs.JobSchedulerBuildOrchestrator` |
| Jobs | `DataIngestionJobHandler.fs` | `module ToolUp.Platform.DataIngestionJobHandler` | `module ToolUp.Platform.Jobs.DataIngestionJobHandler` |
| Jobs | `DeliveryCompleted.fs` | `module ToolUp.Platform.DeliveryCompleted` | `module ToolUp.Platform.Jobs.DeliveryCompleted` |
| Jobs (reach; named Infra) | `InProcessOAuthTokenRefresher.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `OAuthRefreshJobHandler.fs` | `module ToolUp.Platform.OAuthRefreshJobHandler` | `module ToolUp.Platform.Jobs.OAuthRefreshJobHandler` |
| Jobs (reach; named Infra) | `Infra/PlatformAdminStore.fs` | `module ToolUp.Platform.PlatformAdminStore` | `module ToolUp.Platform.Jobs.PlatformAdminStore` |
| Jobs | `Providers/ProviderOAuthRefreshJobHandler.fs` | `module ToolUp.Platform.ProviderOAuthRefreshJobHandler` | `module ToolUp.Platform.Jobs.ProviderOAuthRefreshJobHandler` |
| Jobs | `Providers/ProviderStatusProbeJobHandler.fs` | `module ToolUp.Platform.ProviderStatusProbeJobHandler` | `module ToolUp.Platform.Jobs.ProviderStatusProbeJobHandler` |
| Jobs (reach; named Scope) | `AutoBootstrapDevAdminModeValidator.fs` | `module ToolUp.Platform.AutoBootstrapDevAdminModeValidator` | `module ToolUp.Platform.Jobs.AutoBootstrapDevAdminModeValidator` |
| Jobs (reach; named Infra) | `SmokeTests.fs` | `module ToolUp.Platform.SmokeTests.Defaults` | `module ToolUp.Platform.Jobs.SmokeTests.Defaults` |
| Jobs | `JobSchedulerInstanceValidator.fs` | `module ToolUp.Platform.JobSchedulerInstanceValidator` | `module ToolUp.Platform.Jobs.JobSchedulerInstanceValidator` |
| Jobs (reach; named Config) | `DeployPlaneDepsValidator.fs` | `module ToolUp.Platform.DeployPlaneDepsValidator` | `module ToolUp.Platform.Jobs.DeployPlaneDepsValidator` |
| Jobs | `NotificationChannelInstanceValidator.fs` | `module ToolUp.Platform.NotificationChannelInstanceValidator` | `module ToolUp.Platform.Jobs.NotificationChannelInstanceValidator` |
| Jobs | `SseAuthModeValidator.fs` | `module ToolUp.Platform.SseAuthModeValidator` | `module ToolUp.Platform.Jobs.SseAuthModeValidator` |
| Jobs | `JobApiHandler.fs` | `module ToolUp.Platform.JobApiHandler` | `module ToolUp.Platform.Jobs.JobApiHandler` |
| Jobs (reach; named Infra) | `ModelExecutionApi.fs` | `module ToolUp.Platform.ModelExecutionApiHandler` | `module ToolUp.Platform.Jobs.ModelExecutionApiHandler` |
| Jobs (reach; named Infra) | `OAuthFlowHandler.fs` | `module ToolUp.Platform.OAuthFlowHandler` | `module ToolUp.Platform.Jobs.OAuthFlowHandler` |
| Jobs (reach; named Infra) | `DataIngestionApiHandler.fs` | `module ToolUp.Platform.DataIngestionApiHandler` | `module ToolUp.Platform.Jobs.DataIngestionApiHandler` |
| Jobs | `GrantNotificationObserver.fs` | `module ToolUp.Platform.GrantNotification` | `module ToolUp.Platform.Jobs.GrantNotification` |
| Jobs (reach; named Scope) | `TeamQuotaPolicy.fs` | `module ToolUp.Platform.TeamQuotaPolicy` | `module ToolUp.Platform.Jobs.TeamQuotaPolicy` |
| Jobs | `TransactionalDispatcher.fs` | `module ToolUp.Platform.TransactionalDispatcher` | `module ToolUp.Platform.Jobs.TransactionalDispatcher` |
| Jobs | `INotificationPreferenceStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `BlobNotificationPreferenceStore.fs` | `module ToolUp.Platform.BlobNotificationPreferenceStore` | `module ToolUp.Platform.Jobs.BlobNotificationPreferenceStore` |
| Jobs | `NotificationPreferenceFilter.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `ExternalContactConsentFilter.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `WhatsAppSendPolicyFilter.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `DigestJobHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `IWebhookRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Jobs` |
| Jobs | `WebhookRegistry.fs` | `module ToolUp.Platform.WebhookRegistry` | `module ToolUp.Platform.Jobs.WebhookRegistry` |
| Jobs | `WebhookSecretMigration.fs` | `module ToolUp.Platform.WebhookSecretMigration` | `module ToolUp.Platform.Jobs.WebhookSecretMigration` |
| Jobs | `WebhookSecretRotationFanoutValidator.fs` | `module ToolUp.Platform.WebhookSecretRotationFanoutValidator` | `module ToolUp.Platform.Jobs.WebhookSecretRotationFanoutValidator` |
| Jobs | `WebhookSecretValidator.fs` | `module ToolUp.Platform.WebhookSecretValidator` | `module ToolUp.Platform.Jobs.WebhookSecretValidator` |
| Jobs | `WebhookUrlValidator.fs` | `module ToolUp.Platform.WebhookUrlValidator` | `module ToolUp.Platform.Jobs.WebhookUrlValidator` |
| Jobs | `WebhookDispatcher.fs` | `module ToolUp.Platform.WebhookDispatcher` | `module ToolUp.Platform.Jobs.WebhookDispatcher` |
| Jobs | `HookedEventStore.fs` | `module ToolUp.Platform.HookedEventStore` | `module ToolUp.Platform.Jobs.HookedEventStore` |
| Jobs | `WebhookApiHandler.fs` | `module ToolUp.Platform.WebhookApiHandler` | `module ToolUp.Platform.Jobs.WebhookApiHandler` |
| Jobs | `NotificationPreferenceApiHandler.fs` | `module ToolUp.Platform.NotificationPreferenceApiHandler` | `module ToolUp.Platform.Jobs.NotificationPreferenceApiHandler` |
| Jobs | `ExternalContactApiHandler.fs` | `module ToolUp.Platform.ExternalContactApiHandler` | `module ToolUp.Platform.Jobs.ExternalContactApiHandler` |
| Jobs | `PresenceApiHandler.fs` | `module ToolUp.Platform.PresenceApiHandler` | `module ToolUp.Platform.Jobs.PresenceApiHandler` |
| Audit | `AuditFallbackStore.fs` | `module ToolUp.Platform.AuditFallbackStore` | `module ToolUp.Platform.Audit.AuditFallbackStore` |
| Audit | `PermissionAuditChain.fs` | `module ToolUp.Platform.PermissionAuditChain` | `module ToolUp.Platform.Audit.PermissionAuditChain` |
| Audit | `AuditLog.fs` | `module ToolUp.Platform.AuditLog` | `module ToolUp.Platform.Audit.AuditLog` |
| Audit | `AuditFallbackReplayService.fs` | `module ToolUp.Platform.AuditFallbackReplayService` | `module ToolUp.Platform.Audit.AuditFallbackReplayService` |
| Audit | `RemotingAuditDrainService.fs` | `module internal ToolUp.Platform.RemotingAuditDrainService` | `module internal ToolUp.Platform.Audit.RemotingAuditDrainService` |
| Audit | `AuthAuditHook.fs` | `module ToolUp.Platform.AuthAuditHook` | `module ToolUp.Platform.Audit.AuthAuditHook` |
| Audit | `ArtifactCanonical.fs` | `module ToolUp.Platform.ArtifactCanonical` | `module ToolUp.Platform.Audit.ArtifactCanonical` |
| Audit | `IArtifactSigner.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `IArtifactVerifier.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `IPublisherKeyStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `Ed25519ArtifactSigner.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `Ed25519ArtifactVerifier.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `BlobBackedPublisherKeyStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `Classification/IFieldClassifier.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `Classification/DefaultFieldClassifier.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `Classification/ClassificationGate.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `IDataExporter.fs` | `module ToolUp.Platform.IDataExporter` | `module ToolUp.Platform.Audit.IDataExporter` |
| Audit | `ErasurePipeline.fs` | `module ToolUp.Platform.ErasurePipeline` | `module ToolUp.Platform.Audit.ErasurePipeline` |
| Audit | `IBackgroundExportStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `DsseEnvelope.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `KeyedByteSigning.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `EvidencePackGenerator.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `EvidenceChainWalker.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `EvidenceBundleExport.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `DataSubjectRequestApiHandler.fs` | `module ToolUp.Platform.DataSubjectRequestApiHandler` | `module ToolUp.Platform.Audit.DataSubjectRequestApiHandler` |
| Audit | `BlobBackedBackgroundExportStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `DSRExportJobHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `DSRErasureJobHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `EventStoreErasureHandler.fs` | `module ToolUp.Platform.EventStoreErasureHandler` | `module ToolUp.Platform.Audit.EventStoreErasureHandler` |
| Audit | `DataObjectStoreErasureHandler.fs` | `module ToolUp.Platform.DataObjectStoreErasureHandler` | `module ToolUp.Platform.Audit.DataObjectStoreErasureHandler` |
| Audit | `DataObjectOrphanSweepJobHandler.fs` | `module ToolUp.Platform.DataObjectOrphanSweep` | `module ToolUp.Platform.Audit.DataObjectOrphanSweep` |
| Audit | `LineageStoreErasureHandler.fs` | `module ToolUp.Platform.LineageStoreErasureHandler` | `module ToolUp.Platform.Audit.LineageStoreErasureHandler` |
| Audit | `ConversationEraseHandler.fs` | `module ToolUp.Platform.ConversationEraseHandler` | `module ToolUp.Platform.Audit.ConversationEraseHandler` |
| Audit | `ConversationExporter.fs` | `module ToolUp.Platform.ConversationExporter` | `module ToolUp.Platform.Audit.ConversationExporter` |
| Audit | `IAuditSink.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `AuditReplicatorTypes.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `InMemoryAuditSink.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Audit` |
| Audit | `AuditReplicator.fs` | `module ToolUp.Platform.AuditReplicator` | `module ToolUp.Platform.Audit.AuditReplicator` |
| Audit | `AuditLogModeValidator.fs` | `module ToolUp.Platform.AuditLogModeValidator` | `module ToolUp.Platform.Audit.AuditLogModeValidator` |
| Audit | `AuditLogHealthCheck.fs` | `module ToolUp.Platform.AuditLogHealthCheck` | `module ToolUp.Platform.Audit.AuditLogHealthCheck` |
| Audit | `ConsentApiHandler.fs` | `module ToolUp.Platform.ConsentApiHandler` | `module ToolUp.Platform.Audit.ConsentApiHandler` |
| Audit | `Consent/ConsentStateStore.fs` | `module ToolUp.Platform.Consent.ConsentStateStore` | `module ToolUp.Platform.Audit.Consent.ConsentStateStore` |
| Audit | `PlatformAdmin/AdUnitConfigApi.fs` | `module ToolUp.Platform.AdUnitConfigApi` | `module ToolUp.Platform.Audit.AdUnitConfigApi` |
| Audit | `AdAnalyticsApiHandler.fs` | `module ToolUp.Platform.AdAnalyticsApiHandler` | `module ToolUp.Platform.Audit.AdAnalyticsApiHandler` |
| Audit (reach; named Config) | `SignedExportDepsValidator.fs` | `module ToolUp.Platform.SignedExportDepsValidator` | `module ToolUp.Platform.Audit.SignedExportDepsValidator` |
| Audit (reach; named Infra) | `MetricsMiddleware.fs` | `namespace ToolUp.Platform.Metrics` | `namespace ToolUp.Platform.Audit.Metrics` |
| Audit | `Scope/HostActionAuditHook.fs` | `module ToolUp.Platform.HostActionAuditHook` | `module ToolUp.Platform.Audit.HostActionAuditHook` |
| Audit | `ConfigStoreErasureHandler.fs` | `module ToolUp.Platform.ConfigStoreErasureHandler` | `module ToolUp.Platform.Audit.ConfigStoreErasureHandler` |
| Audit | `FeatureFlagStoreErasureHandler.fs` | `module ToolUp.Platform.FeatureFlagStoreErasureHandler` | `module ToolUp.Platform.Audit.FeatureFlagStoreErasureHandler` |
| Audit | `BlobStorageErasureHandler.fs` | `module ToolUp.Platform.BlobStorageErasureHandler` | `module ToolUp.Platform.Audit.BlobStorageErasureHandler` |
| Audit | `VectorStoreErasureHandler.fs` | `module ToolUp.Platform.VectorStoreErasureHandler` | `module ToolUp.Platform.Audit.VectorStoreErasureHandler` |
| Audit | `AuditViewApiHandler.fs` | `module ToolUp.Platform.AuditViewApiHandler` | `module ToolUp.Platform.Audit.AuditViewApiHandler` |
| Audit (reach; named Scope) | `AuthDenialsDiagnosticsHandler.fs` | `module ToolUp.Platform.AuthDenialsDiagnosticsHandler` | `module ToolUp.Platform.Audit.AuthDenialsDiagnosticsHandler` |
| umbrella | `Compose/ServiceComposition.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella (reach; named Infra) | `ComposeModelExecution.fs` | `module ToolUp.Platform.ComposeModelExecution` | `module ToolUp.Platform.Compose.ComposeModelExecution` |
| umbrella (reach; named Config) | `ModelExecutionDepsValidator.fs` | `module ToolUp.Platform.ModelExecutionDepsValidator` | `module ToolUp.Platform.Compose.ModelExecutionDepsValidator` |
| umbrella | `Files/SessionFileStoreInstanceValidator.fs` | `module ToolUp.Platform.SessionFileStoreInstanceValidator` | `module ToolUp.Platform.Files.SessionFileStoreInstanceValidator` |
| umbrella | `ConfigValidatorAggregator.fs` | `module ToolUp.Platform.ConfigValidatorAggregator` | `module ToolUp.Platform.Compose.ConfigValidatorAggregator` |
| umbrella | `InternetReadinessScorecard.fs` | `module ToolUp.Platform.InternetReadinessScorecard` | `module ToolUp.Platform.Compose.InternetReadinessScorecard` |
| umbrella | `Files/FileNameSanitiser.fs` | `module ToolUp.Platform.FileNameSanitiser` | `module ToolUp.Platform.Files.FileNameSanitiser` |
| umbrella | `Files/ProcessedDataCodec.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Files` |
| umbrella | `Files/FileManagement.fs` | `module ToolUp.Platform.FileManagement` | `module ToolUp.Platform.Files.FileManagement` |
| umbrella | `ColumnMappingApiHandler.fs` | `module ToolUp.Platform.ColumnMappingApiHandler` | `module ToolUp.Platform.Files.ColumnMappingApiHandler` |
| umbrella (reach; named Infra) | `INarrativeStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella (reach; named Infra) | `InMemoryNarrativeStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella (reach; named Infra) | `Infra/PersistentNarrativeStore.fs` | `module ToolUp.Platform.PersistentNarrativeStore` | `module ToolUp.Platform.Compose.PersistentNarrativeStore` |
| umbrella (reach; named Infra) | `NotifyingNarrativeStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella (reach; named Infra) | `NarrativeSupersession.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella (reach; named Infra) | `GroundedNarrative.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ModuleApiFactory.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ModuleQueryBus.fs` | `module ToolUp.Platform.ModuleQueryBus` | `module ToolUp.Platform.Compose.ModuleQueryBus` |
| umbrella | `Compose/Hosting.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `DevDiagnosticsHandler.fs` | `module ToolUp.Platform.DevDiagnosticsHandler` | `module ToolUp.Platform.Compose.DevDiagnosticsHandler` |
| umbrella (reach; named Infra) | `HealthMonitorApiHandler.fs` | `module ToolUp.Platform.HealthMonitorApiHandler` | `module ToolUp.Platform.Compose.HealthMonitorApiHandler` |
| umbrella | `HomeOverviewApiHandler.fs` | `module ToolUp.Platform.HomeOverviewApiHandler` | `module ToolUp.Platform.Compose.HomeOverviewApiHandler` |
| umbrella | `ServiceStatusBoardApiHandler.fs` | `module ToolUp.Platform.ServiceStatusBoardApiHandler` | `module ToolUp.Platform.Compose.ServiceStatusBoardApiHandler` |
| umbrella | `DeploymentReadinessReport.fs` | `module ToolUp.Platform.DeploymentReadinessReport` | `module ToolUp.Platform.Compose.DeploymentReadinessReport` |
| umbrella | `DeploymentVerificationReport.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella (reach; named Infra) | `EgressPolicyHandler.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella (reach; named Infra) | `DiagnosticBundleHandler.fs` | `module ToolUp.Platform.DiagnosticBundleHandler` | `module ToolUp.Platform.Compose.DiagnosticBundleHandler` |
| umbrella | `Middleware.fs` | `module ToolUp.Platform.Middleware` | `module ToolUp.Platform.Compose.Middleware` |
| umbrella (reach; named Remoting) | `RemotingHelpers.fs` | `module ToolUp.Platform.RemotingHelpers` | `module ToolUp.Platform.Compose.RemotingHelpers` |
| umbrella (reach; named Infra) | `ProviderProfileApiHandler.fs` | `module ToolUp.Platform.ProviderProfileApiHandler` | `module ToolUp.Platform.Compose.ProviderProfileApiHandler` |
| umbrella | `PlatformSchema.fs` | `module ToolUp.Platform.PlatformSchema` | `module ToolUp.Platform.Compose.PlatformSchema` |
| umbrella | `PlatformApiHandler.fs` | `module ToolUp.Platform.PlatformApiHandler` | `module ToolUp.Platform.Compose.PlatformApiHandler` |
| umbrella | `PlatformAdminApiHandler.fs` | `module ToolUp.Platform.PlatformAdminApiHandler` | `module ToolUp.Platform.Compose.PlatformAdminApiHandler` |
| umbrella | `HealthChecksFromEnv.fs` | `module ToolUp.Platform.HealthCheckDefaults` | `module ToolUp.Platform.Compose.HealthCheckDefaults` |
| umbrella | `ConfigValidatorsFromEnv.fs` | `module ToolUp.Platform.ConfigValidators` | `module ToolUp.Platform.Compose.ConfigValidators` |
| umbrella | `Compose/Substrate.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `Compose/ComposeExtensions.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `Compose/ConfigurePipeline.fs` | `module ToolUp.Platform.ConfigurePipeline` | `module ToolUp.Platform.Compose.ConfigurePipeline` |
| umbrella | `ITenantLifecycle.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ITenantLifecyclePreview.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ILifecycleLock.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `TenantLifecycleAggregator.fs` | `module ToolUp.Platform.TenantLifecycleAggregator` | `module ToolUp.Platform.Compose.TenantLifecycleAggregator` |
| umbrella | `Lifecycle/TenantHook.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `Lifecycle/EncryptionKeyLifecycle.fs` | `module ToolUp.Platform.EncryptionKeyLifecycle` | `module ToolUp.Platform.Compose.EncryptionKeyLifecycle` |
| umbrella | `Lifecycle/MembershipCacheLifecycle.fs` | `module ToolUp.Platform.MembershipCacheLifecycle` | `module ToolUp.Platform.Compose.MembershipCacheLifecycle` |
| umbrella | `Lifecycle/JobSchedulerLifecycle.fs` | `module ToolUp.Platform.JobSchedulerLifecycle` | `module ToolUp.Platform.Compose.JobSchedulerLifecycle` |
| umbrella | `Lifecycle/DataSubjectRequestLifecycle.fs` | `module ToolUp.Platform.DataSubjectRequestLifecycle` | `module ToolUp.Platform.Compose.DataSubjectRequestLifecycle` |
| umbrella | `Lifecycle/ConfigSeedLifecycle.fs` | `module ToolUp.Platform.ConfigSeedLifecycle` | `module ToolUp.Platform.Compose.ConfigSeedLifecycle` |
| umbrella | `Lifecycle/OwnerTeamBootstrapLifecycle.fs` | `module ToolUp.Platform.OwnerTeamBootstrapLifecycle` | `module ToolUp.Platform.Compose.OwnerTeamBootstrapLifecycle` |
| umbrella | `Lifecycle/EncryptionKeyProvisionLifecycle.fs` | `module ToolUp.Platform.EncryptionKeyProvisionLifecycle` | `module ToolUp.Platform.Compose.EncryptionKeyProvisionLifecycle` |
| umbrella | `Lifecycle/ConversationStoreLifecycle.fs` | `module ToolUp.Platform.ConversationStoreLifecycle` | `module ToolUp.Platform.Compose.ConversationStoreLifecycle` |
| umbrella | `Lifecycle/UserMembershipTeardownLifecycle.fs` | `module ToolUp.Platform.UserMembershipTeardownLifecycle` | `module ToolUp.Platform.Compose.UserMembershipTeardownLifecycle` |
| umbrella | `ILifecycleSummaryStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ILifecycleLedger.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `PrincipalRegistry.fs` | `module ToolUp.Platform.PrincipalRegistry` | `module ToolUp.Platform.Compose.PrincipalRegistry` |
| umbrella | `PlatformTenantApiHandler.fs` | `module ToolUp.Platform.PlatformTenantApiHandler` | `module ToolUp.Platform.Compose.PlatformTenantApiHandler` |
| umbrella | `LifecycleJobHandler.fs` | `module ToolUp.Platform.LifecycleJobHandler` | `module ToolUp.Platform.Compose.LifecycleJobHandler` |
| umbrella | `ScheduledDeprovisionJobHandler.fs` | `module ToolUp.Platform.ScheduledDeprovisionJobHandler` | `module ToolUp.Platform.Compose.ScheduledDeprovisionJobHandler` |
| umbrella | `IBackupCoordinator.fs` | `module ToolUp.Platform.Backup` | `module ToolUp.Platform.Compose.Backup` |
| umbrella | `BackupCoordinator.fs` | `module ToolUp.Platform.BackupCoordinator` | `module ToolUp.Platform.Compose.BackupCoordinator` |
| umbrella | `RestoreDrillVerifier.fs` | `module ToolUp.Platform.RestoreDrill` | `module ToolUp.Platform.Compose.RestoreDrill` |
| umbrella | `Compose/BuildRouteHandlers.fs` | `module ToolUp.Platform.BuildRouteHandlers` | `module ToolUp.Platform.Compose.BuildRouteHandlers` |
| umbrella | `Compose/ComposeAudit.fs` | `module ToolUp.Platform.ComposeAudit` | `module ToolUp.Platform.Compose.ComposeAudit` |
| umbrella | `Compose/ComposeEncryption.fs` | `module ToolUp.Platform.ComposeEncryption` | `module ToolUp.Platform.Compose.ComposeEncryption` |
| umbrella | `Compose/ComposeAuth.fs` | `module ToolUp.Platform.ComposeAuth` | `module ToolUp.Platform.Compose.ComposeAuth` |
| umbrella | `Compose/ComposeNotifications.fs` | `module ToolUp.Platform.ComposeNotifications` | `module ToolUp.Platform.Compose.ComposeNotifications` |
| umbrella | `Compose/ComposeJobs.fs` | `module ToolUp.Platform.ComposeJobs` | `module ToolUp.Platform.Compose.ComposeJobs` |
| umbrella | `Compose/ComposeDevDiagnostics.fs` | `module ToolUp.Platform.ComposeDevDiagnostics` | `module ToolUp.Platform.Compose.ComposeDevDiagnostics` |
| umbrella | `Compose/ComposeConfigValidators.fs` | `module ToolUp.Platform.ComposeConfigValidators` | `module ToolUp.Platform.Compose.ComposeConfigValidators` |
| umbrella | `Compose/ComposeRuntimeServices.fs` | `module ToolUp.Platform.ComposeRuntimeServices` | `module ToolUp.Platform.Compose.ComposeRuntimeServices` |
| umbrella | `Compose/ComposeStores.fs` | `module ToolUp.Platform.ComposeStores` | `module ToolUp.Platform.Compose.ComposeStores` |
| umbrella | `Compose/ComposeObservability.fs` | `module ToolUp.Platform.ComposeObservability` | `module ToolUp.Platform.Compose.ComposeObservability` |
| umbrella | `Compose/ComposeTeamRuntime.fs` | `module ToolUp.Platform.ComposeTeamRuntime` | `module ToolUp.Platform.Compose.ComposeTeamRuntime` |
| umbrella | `Compose/ComposeHealthSmoke.fs` | `module ToolUp.Platform.ComposeHealthSmoke` | `module ToolUp.Platform.Compose.ComposeHealthSmoke` |
| umbrella | `Compose/ComposeScopeResolver.fs` | `module ToolUp.Platform.ComposeScopeResolver` | `module ToolUp.Platform.Compose.ComposeScopeResolver` |
| umbrella | `IServerHost.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `SeedDataLoader.fs` | `module ToolUp.Platform.SeedDataLoader` | `module ToolUp.Platform.Compose.SeedDataLoader` |
| umbrella | `Compose/ComposeBootstrap.fs` | `module ToolUp.Platform.ComposeBootstrap` | `module ToolUp.Platform.Compose.ComposeBootstrap` |
| umbrella | `Compose/ComposeTenantLifecycle.fs` | `module ToolUp.Platform.ComposeTenantLifecycle` | `module ToolUp.Platform.Compose.ComposeTenantLifecycle` |
| umbrella | `StartupModes.fs` | `module ToolUp.Platform.StartupModes` | `module ToolUp.Platform.Compose.StartupModes` |
| umbrella | `SDK.Server.fs` | `module ToolUp.Platform.Server` | `module ToolUp.Platform.Compose.Server` |
| umbrella | `CompositionManifest.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionDiff.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionCapabilityGate.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `BootVerificationPreflight.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `HostingBaseline.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `GroundingEnvelopeSeal.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `HostedTreeUsageExport.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionValidator.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionInspectorHandler.fs` | `module ToolUp.Platform.CompositionInspectorHandler` | `module ToolUp.Platform.Compose.CompositionInspectorHandler` |
| umbrella | `ReflectionTools.fs` | `module ToolUp.Platform.ReflectionTools` | `module ToolUp.Platform.Compose.ReflectionTools` |
| umbrella | `ServerApp.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ModuleBindingManifest.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ModuleBindingRevocation.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `Compose/LiveSessionsCompose.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ComponentConfigResolver.fs` | `module ToolUp.Platform.ComponentConfigResolver` | `module ToolUp.Platform.Compose.ComponentConfigResolver` |
| umbrella | `ComponentRequirements.fs` | `module ToolUp.Platform.ComponentRequirementsPreflight` | `module ToolUp.Platform.Compose.ComponentRequirementsPreflight` |
| umbrella | `ComposableSurface.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ModuleSurface.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `SeamAuthorityEnforcement.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `EventTopology.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `DataFootprint.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ScaleReadiness.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `OperationalTelemetryDiode.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ApplianceProfile.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `EntitlementToken.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `EntitlementValidator.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionRuleVersioning.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `AuthorizationSurface.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `AccessAttestation.fs` | `module ToolUp.Platform.AccessAttestation` | `module ToolUp.Platform.Compose.AccessAttestation` |
| umbrella | `PlatformInHandlerGates.fs` | `module ToolUp.Platform.PlatformInHandlerGates` | `module ToolUp.Platform.Compose.PlatformInHandlerGates` |
| umbrella | `ComponentProvenance.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `HostEnvelope.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ComponentHealthRollup.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ComponentLifecycle.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionHotSwap.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionDescriptor.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionDescriptorVersion.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `TenantCompositionPreset.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionDryRun.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CompositionUpgradePlan.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `PrivacyBudgetLedger.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `ScopeEnumeration.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `CountersignatureRegistry.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `PageLayers.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
| umbrella | `PageDocumentStore.fs` | `namespace ToolUp.Platform` | `namespace ToolUp.Platform.Compose` |
