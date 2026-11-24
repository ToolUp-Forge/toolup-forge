# CLAUDE.md — ToolUp Forge SDK

This file keeps each rule for working in this repository, the command that applies it and a pointer. The full text of
each section (the reasoning, the history, the procedures and the reference material) is in
[`docs/contributing/agent-notes.md`](docs/contributing/agent-notes.md), under the same heading.

## What is ToolUp Platform?

ToolUp Platform is a modular F# full-stack SDK for building production multi-tenant analytical applications. It ships as a set of independently-versioned NuGet packages under the `ToolUp.*` namespace; consumers compose them with their own domain modules.

Core foundations:
- **Server**: Giraffe over ASP.NET Core; the in-tree **ToolUp.Remoting** transport (the `ToolUp.Platform.Server.Remoting` part, which `ToolUp.Platform.Server` references) for type-safe APIs. Consumers write `open ToolUp.Remoting.Server` / `open ToolUp.Remoting.Giraffe`; no separate `PackageReference` is needed.
- **Client**: Fable + Feliz (React bindings) with the in-tree **ToolUp.Elmish** MVU runtime (distributed inside `ToolUp.Platform.Client`). Consumers write `open ToolUp.Elmish` and see classical Elm Architecture plus ToolUp additions (`IDispatcher<'msg>`, `Prefetch<'a>`, structured `ErrorContext`, `EffectHandle` lifetimes, `Cmd.OfRemoting`).
- **Build**: FAKE targets in a Pack-able `ToolUp.Platform.Build` package.

Both substrates began as forks — ToolUp.Remoting from Zaid Ajaj's Fable.Remoting (MIT), ToolUp.Elmish from Eugene Tolmachev's and the Elmish community's Fable.Elmish (Apache 2.0) — and both upstream projects remain excellent and the right choice for anyone whose use case fits them unmodified. The substantive credit lives in [`NOTICE.md`](NOTICE.md) and under [`README.md` → Standing on shoulders](README.md#in-tree-client--transport-forks), which also lists what the in-tree variants add and drop. Consumer call sites move by search-and-replace per [`docs/migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md`](docs/migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md).

## Versioning

`0.x.y` while the public surface is unstable. Per the SemVer-on-`0.x` policy: minor bumps may include breaking changes, patch bumps are non-breaking. `1.0.0` is declared once the surface is judged stable.

- **A change that rides or advances `<Version>` appends ONE line to [`DRAFT-NOTES.md`](DRAFT-NOTES.md), never to `Directory.Build.props` (Phase 1210).** Every project imports the props, so any edit to them forces the full test lane.
- **The bump is checked against the diff, not trusted (Phase 260).** `dotnet run --project Build.fsproj -- VerifySemVerBump` FAILS a version group whose declared version implies a smaller bump than its own packages moved by (`--propose` reports without failing). It runs at release from `publish-nuget.yml`.
- **Release candidates exist ONLY for majors from 1.0 on (Phase 263),** as a tag `vX.0.0-rc.N`; a major is promoted only from a candidate that has served its soak window with no BREAKING surface change since (`dotnet run --project Build.fsproj -- VerifyRcPromotion` before tagging). **The scorecard is rendered on demand, never committed** (`dotnet run --project Build.fsproj -- V1Readiness`).
- **Version groups: the core is one lockstep line; each companion family has its own.** [`version-groups.json`](version-groups.json) puts every packable project on exactly one group; `GenerateSdkManifest` renders it, and `SdkManifestTests` fails on a project in no group or in two.

_Full text: [Versioning](docs/contributing/agent-notes.md#versioning)_

## Repo layout

`src/` holds the SDK (`ToolUp.Platform.*`, the server tier's six part assemblies, the feature tiers), the companion families in named subdirectories, and the test packs; `samples/` the runnable samples; `docs/` the OSS docs site; `templates/` the `dotnet new` scaffolds. The solution is `ToolUp.Forge.sln`, package versions are central (`Directory.Packages.props`), and the FAKE pipeline is `Build.fs` / `Build.fsproj`.

_Full text: [Repo layout](docs/contributing/agent-notes.md#repo-layout)_

## Architecture overview

- **`ToolUp.Platform`** — the core SDK, pure infrastructure with zero domain knowledge: `Core` (shared types and interfaces, Fable-compiled from `fable/`), `Server` (the umbrella over six part assemblies, Phase 1193: `Remoting`, `Infra`, `Config`, `Scope`, `Jobs`, `Audit`), `Client` (the Fable + Feliz shell with the in-tree ToolUp.Elmish runtime), `UI` (the toolkit) and `Build`.
- **The parts' order is the dependency order: a file may only use files in its own part or an earlier one.** A file's part is read from the project files, not from its folder.
- **A namespace names the part (Phase 1198).** `ToolUp.Platform.Core` stays flat (`namespace ToolUp.Platform`); a new server file opens what it uses.
- **The feature tiers and companions** compose onto a `ServerApp`: `ToolUp.AI` (`AICompose.withAI`), `ToolUp.RAG` (`RAGCompose.withRAG`, which composes the AI tier itself), `ToolUp.KnowledgeBase`, `ToolUp.Forms` (`FormsCompose.withForms`), `ToolUp.Scheduling`, `ToolUp.Algorithms` (`AlgorithmsCompose.withAlgorithms`), `ToolUp.Stripe` and `ToolUp.InterPlatform` (`PeerCompose.withPeer`).

_Full text: [Architecture overview](docs/contributing/agent-notes.md#architecture-overview)_

## Guiding Principles

A numbered design canon the SDK is built against. Source comments and docs cite these as `(GP NN)`; the definitions live here. The list is non-exhaustive — only the principles the codebase currently references are enumerated.

- **GP 1 — Companion packages isolate vendor dependencies.** The SDK core (`ToolUp.Platform.*`) carries no third-party vendor SDK. Every cloud / vendor integration lives in a companion package behind an SDK interface, so the core dependency graph stays minimal and the OSS supply-chain surface stays small.
- **GP 2 — No paid-by-default dependencies.** The default composition runs on freely-available components. Paid / enterprise components (AG Grid Enterprise, hosted auth UIs, etc.) are opt-in companions, never a default a consumer is silently billed for.
- **GP 4 — Tenant / team isolation is non-negotiable and enforced structurally.** Scope isolation is carried by the type system and the storage-scope resolver, not by a runtime "remember to filter" convention. A handler cannot accidentally read across tenants.
- **GP 5 — Immutable by default.** Domain and config types are immutable records; state transitions produce new values. Mutability is a documented, justified exception (e.g. hot-path metrics), never the default.
- **GP 7 — Correlation / context rides the async chain.** Request-scoped context (correlation id, scope, principal) flows via `AsyncLocal`-backed ambient context, not threaded by hand through every signature. Handlers read it where needed without it polluting every parameter list.
- **GP 10 — Shared request/response types live in a shared-types project or are primitives.** Types crossing the client/server boundary (ToolUp.Remoting contracts, DTOs) sit in a Core/shared `<Compile>` file or are primitives — never defined server-side only and leaked, which would break the Fable client compile.
- **GP 11 — Backward-compatible defaults.** A new SDK feature defaults to off / to its prior behaviour, so an existing deployment that upgrades stays byte-for-byte identical until it opts in. `fromEnv` helpers and config records preserve prior dispatch behaviour exactly.
- **GP 12 — The six portability rules for distributed implementations** (see [Six portability rules](#six-portability-rules-for-distributed-implementations) below).
- **GP 13 — Advanced behaviour is opt-in; deployments that don't use it pay nothing.** Optional subsystems (RAG, AI, scheduling, transactional sinks) cost zero — no hosted service, no middleware, no allocation — when a deployment doesn't compose them in.

## Six portability rules for distributed implementations

Any infrastructure interface that could plausibly be implemented by a distributed task framework (`IJobScheduler`, `IJobStore`, `IModuleQueryBus`, `INotificationChannel`, `IShareTokenStore`, etc.) MUST satisfy all six rules. Violations make a second implementation impossible.

1. **Identity by value.** Returns / parameters use `string`, `Guid`, or domain records — never live handles (`IActorRef`, `IGrainReference`, etc.).
2. **Async at every boundary.** Every method returns `Async<T>` or `Task<T>`. Synchronous methods or fire-and-forget `Tell`-style signatures are violations. (Documented exception: `IMetricsSink` is sync — hot path, write-only, no return to await.)
3. **Retry + supervision as data.** Retry, backoff, and dead-letter behaviour expressed as records (e.g. `RetryPolicy`). Callback parameters like `OnFailure: exn -> unit` leak framework semantics.
4. **Stateless handlers between invocations.** Handlers (`IJobHandler.Execute`, `IQueryHandler.Handle`, notification subscribers) receive all state via parameters. No in-memory state between calls — Orleans can deactivate grains, Akka.Persistence can restart actors.
5. **No cross-shard ordering promises.** Ordering guaranteed only within a `ShardKey`. Cross-shard ordering correctness is a violation.
6. **Precision at the lower bound.** Scheduling / timing primitives declare their precision contract (e.g. `JobPrecision: Second | Minute`). Implicit sub-second promises that some implementations can't honour are violations.

Additionally:
- No framework-specific serialisation attributes (`[<Serializable>]`, `[<ProtoContract>]`, Akka `IWithUnboundedStash`) on any type in a shared `<Compile>` file.
- No `open Akka.*` / `open Orleans.*` in any file under `ToolUp.Platform`.
- Companion packages exist only at the SDK boundary — the SDK interface never references a companion's types.

`ToolUp.Platform.Tests` ships contract test packs (`IJobSchedulerContract`, `IModuleQueryBusContract`, `IShareTokenStoreContract`, `IDataSourceContract`, `IEntityStoreContract`, etc.). Any external implementation can validate against the same conformance bar. API record methods declare their access requirement by attribute (Phase 69d.tail), and the dispatcher's startup classifier refuses to start on any unclassified method.

_Full text: [Six portability rules for distributed implementations](docs/contributing/agent-notes.md#six-portability-rules-for-distributed-implementations)_

## Module convention (consumer-facing, 4 files per module)

Consumers organise their domain modules as four files. Single-fsproj — modules are not subject to the cross-tier split that applies to SDK companions. The Core/Server/Client split applies only to **publishable SDK packages** because DLL boundaries are part of their public contract; consumer modules are deployment-specific.

| File | Purpose | Compiled by |
|---|---|---|
| `SharedTypes.fs` / contracts | API record, DTOs, domain types | Both |
| `Server.fs` | Route handlers, data processing, `DataType` registration, AI tool metadata + executors | Server |
| `ClientModel.fs` | Elmish Model, Msg, init, update | Fable |
| `ClientView.fs` | Feliz view + `register()` returning `ErasedModule` | Fable |

**Canonical sample**: `samples/HelloWorld/HelloWorld.Module/` shows the absolute minimum.

### AI-queryability is opt-in, and OFF unless the module says otherwise (Phase 36.C)

A module's data is **not** reachable by the built-in cross-module AI tool family (`_platform.ai.*`) until the module declares it with `ServerModule.withAIExposure ModuleAIExposure.Queryable`. **This is the one place the SDK deliberately inverts GP 11** — a user installing a third-party module must not have its data become AI-readable as a side effect of installing it. The declaration **narrows only**, exactly like `ServerModule.withGrantPolicy`.

_Full text, with the four-file sample: [Module convention](docs/contributing/agent-notes.md#module-convention-consumer-facing-4-files-per-module)_

## Companion-authoring guide

Companion packages live under named subdirectories (`AIProviders/`, `Storage/`, `AuditSinks/`, `NotificationChannels/`, etc.). Each:

- Has its own `.fsproj` with `<PackageId>` (unless assembly name = desired package id).
- Implements one or more SDK interfaces.
- Receives `ISecretStore` (or other substrate dependencies) through its `create` function — never reads env vars or config files directly.
- Has its own `<PackageReference>` items for vendor SDKs.
- Ships a `README.md` (packed into the nupkg via the auto-include in `Directory.Build.props`).
- Adds a per-package `<PackageTags>` override to aid discoverability.
- **Ships with its test pack from the first commit**: `src/<Family>/ToolUp.<Family>.Tests/ToolUp.<Family>.Tests.fsproj` beside the family, a gate-manifest row in `GateManifest.fs`, and the SDK contract pack the companion implements, **source-linked** from `src/ToolUp.Platform.Tests/Contracts/`, never copied. A live backend's arm is env-gated, Pending without its variable, never Failed.

For HTTP-shaped companions (audit sinks, notification sinks): use BCL `HttpClient` rather than a vendor SDK where the API is permissive. This minimises the dep graph and the OSS supply-chain surface.

For stateful companions (job scheduler, vector store, etc.): document explicitly in the file header whether the impl is dev-only or distributed-ready. Distributed-ready impls must be stateless between handler calls (rule 4). Dev-only impls are clearly marked.

A native-dependency (P/Invoke) companion ships RID-specific binaries under `runtimes/{rid}/native/`, fails loudly at `create` on an absent RID, keeps every `DllImport` in one `Native.fs`, hash-pins the native artefact, and links LGPL-class code dynamically only.

_Full text: [Companion-authoring guide](docs/contributing/agent-notes.md#companion-authoring-guide)_

## Store-substrate authoring (file layout + opt-in wiring)

The split is decided by **Fable-safety + client-facing, not habit**:

- **Fable-safe shared types → `src/ToolUp.Platform.Core/Shared/XxxTypes.fs`**; only `Platform.Core` ships its source under `fable/` (GP 10).
- **Server-only interface + default impl → `src/ToolUp.Platform.Server/Server/IXxxStore.fs` + `XxxStore.fs`, registered in the `ToolUp.Platform.Server.Infra` part's `.fsproj`** (Phase 1193), unless it needs a later part. Its `Contribution` and any `ServerApp` builder stay in the umbrella.
- **Opt-in wiring (GP 13) — one `FeatureMode` field, one `ConfigKey`, one `Contribution` (Phase 877).** Never mint an `XxxStoreMode` union for an on/off store.
- **Vendor dependencies never enter `ToolUp.Platform.*` (GP 1).** Do NOT add a `ProjectReference` from any `ToolUp.Platform.*` project to `ToolUp.Tabular`; cut a dependency-free seam instead.
- **The remoting `IIdempotencyStore` is deliberately NOT composed by `ServerApp`;** a validator over it probes the DI `IServiceCollection`.

_Full text: [Store-substrate authoring](docs/contributing/agent-notes.md#store-substrate-authoring-file-layout--opt-in-wiring)_

## Props-injection pattern (legacy source-injection contract)

Some Client-tier companions still inject source via `.Client.props` rather than ship a DLL nupkg. The contract: a companion's `.Client.props` file extends the `_ToolUpPlatformClientSources` item group, and `ToolUp.Platform.Client.props` (in the consuming app's MSBuild graph) prepends those items to `<Compile>` before CoreCompile.

The cross-tier source-injection pattern is gradually being phased out as Client-tier companions migrate to Fable source-in-nupkg delivery (the `<Content Include="**\*.fsproj;**\*.fs" Exclude="**\*.fs.js;**\bin\**;**\obj\**" PackagePath="fable\" />` convention). Both paths coexist today.

## Source-in-nupkg conditional-directive rule

Client-tier SDK packages ship their `.fs` files under `fable/` in the nupkg; a Fable consumer's package loader extracts the source and compiles it as part of the consumer's project. Each extracted package carries its own `.fableproj` with an empty `<DefineConstants>` — whether the consumer's defines (e.g. `DEBUG`) propagate to the extracted compilation unit is not part of Fable's documented contract. Empirically Fable 5.x walks the consumer's MSBuild graph and applies the root project's defines, so today the propagation does work; that behaviour is brittle to rely on.

**Rule:** in any `.fs` file packed under `fable/`, the only acceptable compile-time gates are `#if FABLE_COMPILER` / `#if !FABLE_COMPILER` (Fable defines this constant itself, so propagation isn't a question) or gates against a constant the SDK's own `.fsproj` declares explicitly. `#if DEBUG` / `#if !DEBUG` and any custom consumer-side define MUST be checked at runtime instead — via a `BundleConstants.*` accessor, a `window?Foo`-style feature detection, or by unconditionalising the branch and accepting the production cost. A `console.warn` is cheap; a silently-skipped or silently-included branch is not.

## Nullable reference types — keep disabled on Fable-touching projects

F# 9+ supports nullable reference types via `<Nullable>enable</Nullable>` in a `.fsproj`. The Fable compiler itself supports nullness (Fable 5.0.0+). However, enabling nullness in a Fable consumer causes the compiler to re-compile every transitive Fable dependency in null-aware mode, and any dependency that hasn't been nullness-annotated produces a cascade of warnings (or errors, depending on configuration).

As of 2026-05, the major Fable-ecosystem libraries this SDK consumes or embeds (Feliz, Fable.SimpleJson, the in-tree ToolUp.Elmish runtime, the in-tree ToolUp.Remoting transport) have not been nullness-annotated, and there's no published timeline for that to land.

**Rule:** do not set `<Nullable>enable</Nullable>` on any project that compiles via Fable or whose Fable-compiled output consumes Feliz / Fable.SimpleJson / the in-tree ToolUp.Elmish / ToolUp.Remoting runtimes. Leave the property unset (which inherits F#'s default of `disable`), or explicitly set `<Nullable>disable</Nullable>` for projects sitting in a solution whose `Directory.Build.props` enables nullness by default. Server-only projects with no Fable involvement may enable nullness per the standard F# 10 default.

This rule retires when the upstream packages ship nullness annotations.

## Type erasure boundaries

Type erasure (`box`/`unbox`) is contained in these sanctioned boundaries inside forge: (1) `ClientModule.register`; (2) `DataTypeDisplay.RenderSummary`, closed by Phase 817; (3) narrative `Component` block renderers; (4) `GridApiRegistry`; (5) `EntityStore` registrations; (6) `AuthUIProvider` handler dispatch; (7) `IDataMigrator.Migrate`; (8) `RemoteStream.subscribe`; (9) `ErasedCodec`; (10) `GeneratedDispatchRegistry`; (11) `GeneratedProxies`; (12) the job dispatcher's boxed statuses and the `LongRunning` bridges.

Fable/JS interop boxing (React dependency arrays, `isNull (box x)` probes, erased-type coercions in the AG Grid bindings) and `HttpContext.Items` stamps are idiomatic interop, not domain erasure, and don't count against this list. Module code outside these boundaries never sees type erasure.

_Full text, with why each boundary is symmetric: [Type erasure boundaries](docs/contributing/agent-notes.md#type-erasure-boundaries)_

## Build pipeline

```bash
dotnet build ToolUp.Forge.sln                                         # full build
dotnet run --project Build.fsproj -- VerifyAll                        # every Expecto pack, then the legs
dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj # one pack, while iterating
dotnet run --project Build.fsproj -- VerifyFable                      # the client-tier Fable harness
dotnet run --project Build.fsproj -- Pack                             # nupkgs to ../local-nuget-feed
dotnet run -- Format                                                  # fantomas
```

**Do not run `dotnet test` against the solution or these projects.** They are Expecto console runners, so `dotnet test` exits 0 having run nothing — a silent false-green. The real non-breakage gate is a full `dotnet build ToolUp.Forge.sln` plus every Expecto suite `VerifyAll` runs with 0 failures. `GateManifest.rows` in `GateManifest.fs` is the one list of what the gate runs: every pack, leg and CI-only check is a row there, and every test project is a row or a named exclusion in `GateManifest.exclusions` (the `GateReconcile` row is red otherwise).

**Gate lanes — `TOOLUP_TEST_LANE` (Phase 762).** The full gate is tens of minutes; read [`gate-timings.json`](gate-timings.json) for what it costs. A lane is an environment variable, never a `verify.ps1` switch; unset *is* `full`, an unrecognised value falls back to `full` with a warning, and a ship may only cite `full`.

- `full` (unset) — the whole suite, and the only lane a ship cites.
- `fast` — everything but the measured slow set: the pre-merge lane.
- `pure` — the declared no-filesystem packs, in seconds.
- `affected` — the Platform family's lists a change can reach (`TOOLUP_TEST_LISTS`), and every other pack whole.
- `preflight` (Phase 1394) — the checks that make a full lane red in minutes, and no behavioural pack: run it before a push and before a landing claims the full lane.
- `prose` (Phase 1395) — what a documents-only change can turn red; it refuses a change it does not cover.

```powershell
$env:TOOLUP_TEST_LANE='fast'; pwsh ./verify.ps1       # or pure, affected, preflight, prose
Remove-Item Env:TOOLUP_TEST_LANE                      # back to full
```

The lanes, what each runs and what each costs: [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md).

### What CI actually gates (Phase 614)

**The table below is GENERATED** from the gate manifest — `GateManifest.rows` in `GateManifest.fs`, each row's `Where` saying which `checks.yml` job runs it — by `dotnet run --project Build.fsproj -- Gate --render`, and held byte-for-byte by `GateManifestTests`; `Gate --check` refuses a drift. Edit the rows, never the table. **Read the "Gates?" column, not the job list**: a job existing is not the same as a job gating. `Reads` is the union of the rows' declared inputs; a job's rows that read nothing are the `pure` lane's members.

<!-- gate-manifest:ci-table:begin -->
| Job | Rows of the gate manifest it runs | Reads | Gates? |
|---|---|---|---|
| `spdx-headers` | — (CI-only: a shell lint over the Fable-packed sources' headers; no driver target) | — | yes |
| `fantomas` | — (CI-only: the formatter's own `--check`, which `verify.ps1` also runs) | — | yes |
| `dco` | — (CI-only: a commit-message lint; PR only, since direct-to-main is this repo's integration path) | — | PR only |
| `fable-wire-smoke` | WireTests, WireFable | Tree, Node | yes |
| `ai-wire-conformance` | WireConformance, WireConformanceFable | Tree, Node | yes |
| `verify-all` | 59 rows, every `Local` row of the full lane: Platform, Platform.Remoting, Platform.Infra, Platform.Config, Platform.Scope, Platform.Jobs, Platform.Audit, Platform.PublicApi, Forms, Scheduling, AIProviders, Stripe, Build, RemotingAnalyzers, Cli, Voice, AICookbooks, Algorithms, AlgorithmProviders, CloudParity, ArtefactSigning, ReportingDocx, ReportingXlsx, DataSources, OpenXml, OpenXmlSpreadsheet, AssetStore, SvgRasterizerSkia, Reporting, Offline, CompanionsIsolation, AIEvaluation, TriageCalibration, Tabular, Classification, DataSubject, ReportingHtmlPdf, Webhooks, JobSchedulers, NotificationChannels, AuditSinks, Storage, Secrets, EmbeddingProviders, EntityStores, TimeSeriesStores, FeatureFlagProviders, Media, GateReconcile, Fable, Samples, Analyzers, ScopeLiterals, ServiceConfigFold, NavRoleFinding, FuaranCoResolution, FuaranCoBuild, FuaranReferences, MergeDrivers | Tree, Git, Docker, Network, Browser, Node, 109 env var(s) | yes |
| `cloud-parity` | CloudParity | Tree, Docker, 8 env var(s) | yes |
| `fable-tier` | Fable | Tree, Node | yes |
| `doc-snippets` | DocSnippets | Tree | yes |
| `source-citations` | SourceCitations, UploadResults | Tree | yes |
| `templates` | Templates, PackagedModuleTemplate, DataPlatformTemplate | Tree, Network, Node | yes |
| `browser-smoke` | BrowserSmoke | Tree, Node, Browser | yes |
| `perf-budget` | PerfBudget | Tree, Node | runs; not yet a required check |
<!-- gate-manifest:ci-table:end -->

**A new gate is a row.** Every list of what the gate runs (`BuildConfig.TestPacks`, the pure-lane set, CI's `EXPECTED_PACKS`, this table, `VerifyGate`'s gated targets) is a render of `GateManifest.rows`. `dotnet run -- Gate --run <row>` runs one row on its own.

**What the gate reads is a generated record too: [`gate-reads.json`](gate-reads.json) (Phase 1208).** Never edit the JSON; declare the read on the row that opens it. `Gate --render` writes it, and `Gate --check` and the `GateReconcile` row are red when the committed file is not the render.

**`ToolUp.Offline.Client` is transpiled in CI by `browser-smoke` only,** so a `<ProjectReference>` dropped from its fixture narrows the repo's Fable coverage; `VerifyBrowserSmoke` asserts its own compile set. **Blob names are `/`-delimited on `IBlobStorage`, always,** and **every Expecto pack runs sequenced by default** (`CLIArguments.Sequenced`), because parallel tests writing to the console deadlock.

### Phase citations in source comments

New code cites no phase in a source comment: the phase's recorded outcome is the record of why a thing exists, and a number in a comment goes stale the first time the thing is reshaped. An existing phase-history comment is removed from any file a change touches (the tidy rule).

### Fast iteration

Full-sln verification is for end-of-task / pre-commit, not per-edit; build the one fsproj you are changing (`dotnet build src/ToolUp.Forms.Server/ToolUp.Forms.Server.fsproj`). **After touching a `[<Literal>]`, adding an `InternalsVisibleTo`, or moving a type a surface names to another assembly, build its dependants clean once** — the reference assembly does not change and an incremental build keeps the stale copy.

_Full text, with the template gates, the publish flow and the measurements: [Build pipeline](docs/contributing/agent-notes.md#build-pipeline)_

## Build verification

After every step: `dotnet build` (fast).
At phase boundaries: full Fable JS verification — `cd samples/MinimalClient && dotnet fable -o output`. Spot-check the emitted JS.

**When this applies:**
- Any edit to a `Client/` source file or a file consumed by Fable.
- Any refactor of interface signatures crossing the client/server boundary.
- Any change to a module using `[<Erase>]`, `[<ReactComponent>]`, `inline` members, `importSideEffects`, `import`, or explicit `emitJsExpr`.

**Always pass `-o output` when verifying.** Bare `dotnet fable` emits `*.fs.js` next to source and leaves `output/` stale.

- **Never reference a bare `..\Foo.fs` from a Fable test project;** keep shared sources in a `Shared/` subdir.
- **Provision a verification worktree at a SHORT path** — a deep path hits Windows MAX_PATH and `fsc` then emits no dll and no error.
- **Expecto `--filter` joins the test path with `.`, not `/` — and a filter that matches nothing reports SUCCESS.** Read the *count*, never the exit code.
- **`MemoizedChart` must NOT be `private`.** A module-level value referenced from an `inline` method on an `[<Erase>]` type must export.

_Full text: [Build verification](docs/contributing/agent-notes.md#build-verification)_

## Public-API approval baselines (Phase 175)

The approval gate renders each packable `ToolUp.*` assembly's public surface and diffs it against `api-baselines/<assembly>.approved.txt`; run it alone with `dotnet run --project src/ToolUp.Platform.Tests.PublicApi/ToolUp.Platform.Tests.PublicApi.fsproj`. The rules that matter when a phase touches public surface:

- **It fails in BOTH directions (Phase 618): removed / renamed / retyped members, AND additive growth whose baseline was not regenerated.**
- **Optional constructor args (`?foo`) read as a REMOVAL;** use explicit secondary constructors to keep the diff additive.
- **A field added to a shared record ripples exactly as far as EMBEDDING, not reference.**
- **Every generated artefact is a row of [`projections.json`](projections.json), and one switch regenerates it:** `TOOLUP_APPROVE`, or `pwsh ./dev-scripts/regen.ps1 <stem>` (the baselines are stem `api`).
- **Scope the regen by NAME:** `TOOLUP_APPROVE=api:ToolUp.Platform.Core,api:ToolUp.Platform.Server`. Verify the real diff WITHOUT the switch — approve mode passes trivially. Never run an unscoped regen in a shared working tree.
- **A worktree regen is only valid against the HEAD it is pinned to.**
- **An UNBUILT tree is one failure, not fifty-two (Phase 731):** build, then re-run.
- **An untracked sibling packable project reddens the gate;** it is not your break — confirm with `git status --short` and note it.
- **A failing baseline names an assembly, not a cause** — confirm attribution with `git log -- <source files>`.
- **A new opt-in is a `FeatureMode` key on the concern record it belongs to** (Phase 877), so only that record's ctor widens.
- **Marking a member `[<Obsolete>]` moves the baseline, and the notice is gated (Phase 258):** its message names a **replacement** and a **removal target**. See [`docs/platform/deprecation-policy.md`](docs/platform/deprecation-policy.md).

_Full text: [Public-API approval baselines](docs/contributing/agent-notes.md#public-api-approval-baselines-phase-175)_

## F# style + idioms

- **Runtime**: .NET 10 (`net10.0`).
- **Language**: F# 10 (the SDK pin in `global.json` is `10.0.203`). Assume F# 10 features are available without further qualification.
- **Formatter**: Fantomas — pre-commit step. Run `dotnet fantomas <file>` BEFORE `dotnet build`.
- **A builder's name says whether it sets or adds (Phases 1050, 1052).** A `with*` builder **sets**; an `add*` builder **appends** and nothing later replaces it. The one fold of `ComposeExtensions.ServiceConfig` is `ServiceConfig.append` (`dotnet run --project Build.fsproj -- VerifyServiceConfigFold`).
- **Never write `map[key] arg1 arg2` on a single line;** Fantomas can turn the indexer into a list-application. Extract the indexer read to its own line.
- **Raw control bytes in source — never.** A raw NUL makes git classify the file binary; use the F# escape sequence. Find offenders with `git ls-files --eol | grep -- -text`.
- **Prefer `_.Property` over `fun x -> x.Property`** for one-step access; method-call lambdas need parens (`_.AutoSizeAllColumns()`).
- **SSE / non-Remoting JSON must use `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()`,** never a plain `JsonSerializerOptions()`. **Additive fields on persisted records: the STJ path yields `null`, the SimpleJson path THROWS — handle both.**
- **AG Charts:** `AgChart.axes` is direction-keyed (`"x"` / `"y"`); don't add `prop.key` to the chart wrapper when data changes. The bindings target **ag-grid 35.3.0** / **ag-charts 13.3.0**.

### Elmish MVU discipline

- `update` functions must be pure. All side effects flow through `Cmd`.
- No mutable global state in client code (except documented exceptions).
- Text inputs use `React.useState` for display state. Only dispatch on submit (Enter / button click). No per-keystroke `UpdateInput` messages.
- Modules declare what they are (`Definition`), what they need (`NeedsData`), what they provide (`ProvidesProcessedData`, `DataTypes`), and how they behave (`Init`, `Update`, `View`). The shell handles all wiring.

_Full text: [F# style + idioms](docs/contributing/agent-notes.md#f-style--idioms)_

## AI provider authoring

A new provider goes in `src/AIProviders/<Name>/` with its own `.fsproj`, implementing `IAIProvider` and exposing a builder for `DefaultAIProviderFactory`.

- Receives `ISecretStore` through its builder / `create` function.
- Never reads env vars or config files directly.
- Supports the streaming + tool-calling contract documented in `docs/ai/extending.md`.
- Documents capability flags via `IAIProvider.Capabilities` (e.g. `SupportsPromptCaching: bool`, `Vision: bool`, `SupportsTriage: bool`).

`AIProviderResponse.Usage: TokenUsage option` reports `{ PromptTokens; CachedPromptTokens; OutputTokens; CacheCreationTokens }`. Streaming providers parse usage from terminal events (Anthropic `message_delta`; OpenAI `stream_options.include_usage=true` chunks).

## Embedding provider authoring

A new provider goes in `src/EmbeddingProviders/<Name>/`, implementing `IEmbeddingProvider`.

- Receives `ISecretStore` through `create` for API-backed implementations.
- Offline providers take no arguments.
- Distributed-ready providers must be stateless between `GenerateEmbedding` calls (rule 4). `LocalEmbeddingProvider` is the documented exception — mark any new stateful provider as dev-only in its file header.

## Storage / Secrets / VectorStore authoring

Same pattern: implement the relevant interface (`IBlobStorage` / `ISecretStore` / `IVectorStore`), accept substrate dependencies via `create`, ship companion as its own `<PackageReference>` set, register an `IHealthCheck` probe in the same package, register an `IConfigValidator` for preflight if connection state is testable.

## Audit-sink authoring

`IAuditSink` is a 2-method interface (`Name` + `Deliver`). Sinks must be batch-idempotent: the dispatcher retries the entire batch on `Result.Error`, and the catch-up sweep can re-deliver after a process restart. Use vendor-specific dedup keys (Splunk `_meta.uuid`, S3 content-addressable naming, etc.).

API keys / tokens always come through `ISecretStore` — no hardcoded credentials, no env-var-only sinks. Token rotation is the operator's lever; the sink reads on every `Deliver` so rotated values flow through immediately.

## Notification-channel authoring

Transactional sinks (`INotificationSink`) implement `Kind: NotificationKind` + `Deliver`. Per-`Kind` registry rejects duplicates at compose time. Wire via `ServerApp.withTransactionalSink`; deployments without sinks skip the dispatcher hosted-service entirely.

Distributed `INotificationChannel` companions (Redis is the shipped reference) replace the default `InMemoryNotificationChannel` and provide scope-isolated pub/sub — per-scope topic, not a post-hoc filter.

## Contributing

- License: Apache 2.0. See [LICENSE](LICENSE).
- Developer Certificate of Origin: every commit MUST carry a `Signed-off-by:` line. CI enforces this.
- Contribution flow: issue → PR → review → merge. See [CONTRIBUTING.md](CONTRIBUTING.md).
- Code of Conduct: Contributor Covenant v2.1. See [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md).
- Security disclosure: see [SECURITY.md](SECURITY.md).

## Commands the user is asked to run — PowerShell syntax

When suggesting shell commands for the user to execute on Windows, write them in PowerShell syntax. POSIX syntax is used for Bash tool calls (which run via Git-Bash).

Built-in aliases like `rm`, `cp`, `ls` exist in PowerShell but use DIFFERENT flag syntax than POSIX. Use full cmdlet names (`Remove-Item`, `Copy-Item`, `Get-ChildItem`) in suggested commands. Multi-line continuation: backtick `` ` `` not `\`.

## Executing actions with care

Carefully consider reversibility and blast radius. Local, reversible actions (editing files, running tests) are fine to take. Destructive operations, force-pushes, force-pushes to main, modifying CI/CD pipelines, sending messages to external systems all need explicit user confirmation. Authorisation stands for the scope specified, not beyond.

## Scope

The job of this `CLAUDE.md` is to support OSS contributors using Claude Code (or any AI coding assistant) to read and modify SDK source. Architectural docs live in `docs/`; per-companion deep-dives in `docs/{ai,rag,knowledge-base,forms,scheduling}/` and `docs/companions/{auth-providers,storage-providers,ai-providers,embedding-providers,notification-channels}.md`.

**A change touching a rule in [`docs/security/PLATFORM-SECURITY-RULES.md`](docs/security/PLATFORM-SECURITY-RULES.md) refreshes that rule's cited evidence in the same commit** — the artefact is version-stamped and every rule carries an `Evidence:` path, so a stale pointer is a defect, not a documentation nit. Cadence and versioning policy: [`docs/security/README.md`](docs/security/README.md).
