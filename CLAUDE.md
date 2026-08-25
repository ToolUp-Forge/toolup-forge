# CLAUDE.md — ToolUp Forge SDK

## What is ToolUp Platform?

ToolUp Platform is a modular F# full-stack SDK for building production multi-tenant analytical applications. It ships as a set of independently-versioned NuGet packages under the `ToolUp.*` namespace; consumers compose them with their own domain modules.

Core foundations:
- **Server**: Giraffe over ASP.NET Core; in-tree ToolUp.Remoting transport (Fable.Remoting fork, distributed inside `ToolUp.Platform.Server`) for type-safe APIs. `namespace Fable.Remoting.*` is preserved, so `open Fable.Remoting.Server` / `open Fable.Remoting.Giraffe` continue to compile unchanged.
- **Client**: Fable + Feliz (React bindings) with an in-tree Elmish runtime (Fable.Elmish v5.x fork, distributed inside `ToolUp.Platform.Client`). `namespace Elmish` is preserved; consumers see classical Elm Architecture plus ToolUp additions (`IDispatcher<'msg>`, `Prefetch<'a>`, structured `ErrorContext`, `EffectHandle` lifetimes, `Cmd.OfRemoting`).
- **Build**: FAKE targets in a Pack-able `ToolUp.Platform.Build` package.

See [`README.md`](README.md#in-tree-client--transport-forks) for the full list of fork additions and the source-compat guarantee.

## Versioning

`0.x.y` while the public surface is unstable. Per the SemVer-on-`0.x` policy: minor bumps may include breaking changes, patch bumps are non-breaking. `1.0.0` is declared once the surface is judged stable.

Each companion package versions independently — `ToolUp.Platform.Core 0.3.0` can pair with `ToolUp.AI 0.5.0`. Compatibility documented per release. For coordinated bumps, consumers use the `ToolUp.Sdk` meta-manifest: a one-line `<ToolUpSdkVersion>` property in `Directory.Packages.props` resolves every `ToolUp.*` package at the same version.

## Repo layout

```
toolup-forge/
├── src/
│   ├── ToolUp.Platform.{Core,Server,Client,Build}/   # core SDK (4 packages)
│   ├── ToolUp.AI.{Core,Server,Client}/               # AI agent loop + SSE + tools
│   ├── ToolUp.AI.Wire{,.Conformance}/                # AI wire format + conformance fixtures
│   ├── ToolUp.RAG.{Core,Server}/                     # retrieval-augmented generation
│   ├── ToolUp.KnowledgeBase.{Core,Server,Client}/    # document ingestion + extraction
│   ├── ToolUp.Forms.{Core,Server,Client}/            # schema-driven forms + workflows
│   ├── ToolUp.Scheduling.{Core,Server}/              # booking + recurrence
│   ├── ToolUp.Algorithms.{Core,Server,Client}/       # analytical-primitive catalog + provider seam
│   ├── ToolUp.Workflow.{Core,Server}/                # standalone workflow engine
│   ├── ToolUp.Experiments.{Core,Server}/             # A/B experiment substrate
│   ├── ToolUp.Reporting{,.Core,.Server}/             # report generation
│   ├── ToolUp.Stripe.{Webhook,TierToken,Server}/     # Stripe billing: webhook verify + tier cookies + Giraffe wiring
│   ├── ToolUp.Tabular/, ToolUp.OpenXml/              # tabular-data + OpenXml substrates
│   ├── ToolUp.ArtefactSigning/                       # artefact signing + module-binding manifest + SBOM stamp
│   ├── ToolUp.AssetStore/, ToolUp.BrandKit/          # asset store + brand kit
│   ├── ToolUp.PublicRendering/                       # SSR public pages + sitemap / SEO surface
│   ├── ToolUp.Cli/                                   # `toolup` admin / deploy CLI
│   ├── InterPlatform/                                # opt-in cross-deployment typed peer RPC (server-only)
│   ├── AIProviders/{Claude,OpenAI,Gemini}/           # LLM providers
│   ├── EmbeddingProviders/{Local,OpenAI}/            # embedding providers
│   ├── AuthProviders/{Oidc,OidcClient,ClerkUI,Entra*}/ # auth providers
│   ├── Storage/{AwsS3,Azure,GoogleCloud}/            # IBlobStorage companions
│   ├── AuditSinks/{S3Archive,GcsArchive,AzureBlobArchive,SplunkHec,DatadogLogs}/ # audit replication
│   ├── NotificationChannels/{Redis,Email/...,Sms/Twilio,Push/WebPush}/
│   ├── VectorStores/Hnsw/                            # scalable IVectorStore
│   ├── TimeSeriesStores/, Rerankers/, ContainerSchedulers/, TelemetrySinks/,
│   │   FeatureFlagProviders/, RateLimiters/, DataSources/, Webhooks/,
│   │   ContentAuthoring/, Media/, Encryption/, Cloud/, Hosts/  # further companion families
│   ├── Metrics/OpenTelemetry/                        # IMetricsSink companion
│   ├── Secrets/AzureKeyVault/                        # ISecretStore companion
│   ├── AgGridEnterprise/                             # AG Grid Enterprise init shim
│   ├── ToolUp.Platform.Tests/                        # SDK contract test packs
│   ├── ToolUp.{Forms,Scheduling,Stripe,Tabular,OpenXml,ArtefactSigning,AssetStore,Cli,AIProviders}.Tests/ # per-companion test packs
│   ├── ToolUp.RAG.Evaluation/, ToolUp.RAG.Benchmarks/
│   └── ToolUp.Sdk/                                   # coordinated-bump meta-manifest
├── samples/HelloWorld/                               # runnable end-to-end sample
├── docs/                                             # OSS docs site
├── ToolUp.Forge.sln
├── Directory.Packages.props                          # CPM
├── Build.fs / Build.fsproj                           # FAKE pipeline
├── LICENSE                                           # Apache 2.0
├── CONTRIBUTING.md, CODE_OF_CONDUCT.md, SECURITY.md
└── CLAUDE.md                                         # this file
```

## Architecture overview

### `ToolUp.Platform` — core SDK

Pure infrastructure with zero domain knowledge. Per-tier packages:

- **`Core`** — shared types + interfaces (`ILogger`, `IBlobStorage`, `IAuthProvider`, `IAIProvider`, `INotificationChannel`, `IHealthCheck`, `IConfigValidator`, `IEventStore`, `ISecretStore`, etc.). No server or client deps; the "minimum viable consumer" floor. Source ships in the nupkg under `fable/` for Fable consumers.
- **`Server`** — Giraffe-over-ASP.NET Core implementation: `ServerApp` composition root, `StorageScope` / scope resolvers, `ITeamStore`, `IConfigStore`, `IPermissionStore`, `IEntityStore`, `IDataObjectStore`, `IShareTokenStore`, `IJobScheduler`, `IDataIngestor`, `IAuditLog`, transactional dispatch, rate limiting, security headers middleware, `MetricsMiddleware`, `RequestTimingMiddleware`, OAuth flow handler, encryption-at-rest decorator, default in-process implementations of every interface, etc.
- **`Client`** — Fable + Feliz shell with the in-tree Elmish runtime (under `Client/Elmish/`): MVU, sidebar navigation, `UIToolkit`, `AgChart`/`AgGrid` Fable bindings, `AuthUIProvider` delegate registry, `NotificationClient` (SSE), `ToastCentre`, `ProcessedDataContext`. Source ships in the nupkg under `fable/`.
- **`Build`** — FAKE pipeline targets (`Run` / `Bundle` / `Format` / `Pack` / `ThirdPartyNotices`).

### `ToolUp.AI` — AI assistant companion

Agent loop, SSE streaming, conversation persistence, tool registry, system-prompt composition (platform + team + module layers), `composeWithAI`. Built on `IAIProvider` (extension point in `Platform.Core`).

`AIAssistantMode` DU (in `ToolUp.AI.Core/Shared/AITypes.fs`):
- `NoAIAssistant` — no AI module or side panel (default)
- `DefaultAIAssistant` — built-in AI module with SDK defaults
- `ConfiguredAIAssistant of AIAssistantBranding` — built-in AI module with custom branding

Apps wanting AI call `AIServerApp.run` (from `ToolUp.AI.Server/Server/AICompose.fs`); apps without AI use `ServerApp.run` directly. `AIServerApp` is a flat superset of `ServerApp` — same fluent shape, AI-specific helpers added.

### `ToolUp.RAG` — retrieval-augmented generation

Chunking, vector store, retrieval pipeline, ingestion + reembedding background services, `RAGPromptBuilder`, `composeWithRAG`. Built on `IEmbeddingProvider` + `IVectorStore` + `IRetrievalPipeline` extension points (in `Platform.Server`).

Apps wanting RAG call `RAGServerApp.run` (from `ToolUp.RAG.Server/Server/RAGCompose.fs`); `RAGServerApp` is a flat superset of `AIServerApp`. Per-deployment tuning via `withTopK` / `withMinScore` / `withMergeStrategy` / `withSnippetCharLimit` / `withOriginFilter` / `withGroundingMode` / `withIngestionConcurrency` / `withIngestionQueueCapacity` / `withTelemetry`.

### `ToolUp.KnowledgeBase` — document KB

User-facing canonical consumer of `ToolUp.RAG`: document upload, multi-format extraction (PDF / PPTX / DOCX / XLSX / CSV), ingestion-status surfacing, narrative-commit, notes, AI-context. Three integration contracts external KB replacements must honour:
1. NarrativeCommit handler — call `Toolup.NarrativeCommit.install` with a submit handler
2. `IIngestionStatusObserver` — wired explicitly into `composeWithRAG`
3. Notification-key contract — `"KnowledgeBase.IngestionStatus"` is the published wire format the AI side panel subscribes to.

### `ToolUp.Forms` — schema-driven forms

`FormSchema` / `Submission` / `WorkflowDefinition` / `FormError` types, validation engine, workflow engine, `FormsServerApp` composition. Supports publishable surveys (`FormVisibility.Publishable`) via the SDK's `IShareTokenStore` substrate (HMAC-SHA256-signed tokens, blob-backed claim store, per-token rate-limit partition).

Public-form surface adds `IPublicFormApi` (token-gated submit at `/api/public/forms/`), `PublicEmbed` (`/r/{token}` standalone Feliz component), `SurveyDashboardView` / `SurveyListView`.

### `ToolUp.Scheduling` — booking + recurrence

`IBookingScheduler` interface (per-resource concurrency lock, conflict detector), `RecurrenceExpander`, `iCalendar` types, `SchedulingApi` ToolUp.Remoting contract, `SchedulingCompose`. Consumers wire it into modules that surface booking-grid UIs.

### `ToolUp.Algorithms` — analytical-primitive catalog

A curated catalog of analytical primitives with a provider seam, so the numerics come from a companion package rather than the SDK. **Zero vendor dependency in all three tiers** — no arithmetic beyond argument validation and the AIC/BIC identities (GP 1).

Four fitter interfaces: `IRegressionFitter`, `IDescriptiveStats`, `IDistributionFitter`, `ITimeSeriesFilter`. A provider implements whichever subset it serves and assembles them with `AlgorithmProvider.create`; `IAlgorithmCatalog` enumerates, `IAlgorithmDispatcher` executes, and `AlgorithmProviderRegistry` **rejects a duplicate algorithm id at compose**, naming both providers and the contested id (same family as the `INotificationSink` one-per-`Kind` rule). Compose with `AlgorithmsCompose.withAlgorithms`; it registers the DI singletons, mounts the read-only catalog remoting endpoint, adds a `/dev/inspect` panel, and registers an `_algorithms.*` AI tool family (one tool per algorithm plus `_algorithms.list`). A deployment that never calls it pays nothing (GP 13).

The interface set was chosen by measurement — `evals/algorithms-primitives-eval/` ran a code assistant through five representative tasks against a raw numerics library and scored compile failures separately from *silent* divergence (code that compiles, runs, and returns a plausible number that is wrong for the question asked). The two turned out close to anti-correlated. The wrapper's measured value is almost never the arithmetic but four **echoed convention** fields — `QuantileConvention`, `SmoothingAlignment`, `EstimationMethod`, `ReferenceLevels` — each making explicit a choice the raw library made silently. `ICurveFitter` was measured as a control and deliberately excluded.

Distinct from `IModelFitProvider` (`Platform.Server`, Phase 449), which is the long-running opaque-spec fit envelope: forge stores and compares gates there and never interprets. See [`src/ToolUp.Algorithms/README.md`](src/ToolUp.Algorithms/README.md).

### `ToolUp.Stripe` — Stripe billing companion

Three independently-versioned packages (`0.1.0-alpha`) for deployments that bill via Stripe, each isolating the Stripe wire format behind a small F# surface (GP 1) — **no `Stripe.net` dependency**; a consumer wanting the richer Stripe client API consumes `Stripe.net` directly alongside these.

- **`ToolUp.Stripe.Webhook`** — pure-F# webhook signature verification (`WebhookSigner.verify` / `verifyWith`: HMAC-SHA256 over `"{timestamp}.{body}"`, constant-time compare, 5-minute freshness window) returning `Result<VerifiedEvent, WebhookError>`, plus the typed `StripeEvent` model (`StripeEvent.fs`). Zero ASP.NET Core / Giraffe deps.
- **`ToolUp.Stripe.TierToken`** — HMAC-signed tier-claim cookie machinery: `Tier` DU (`Anonymous | Free | Personal | Teacher`), `Token.mint` / `Token.validate`, `Cookie.issue` / `clear` / `resolveFromRequest`. Depends only on `Microsoft.AspNetCore.Http`. Single-issuer/-audience by design — swap the signer for a JWT validator without changing the cookie/claim shape when federation lands.
- **`ToolUp.Stripe.Server`** — Giraffe / ASP.NET Core wiring: `StripeConfig` + `Routes`, the production webhook handler, Customer-Portal (`CustomerPortal.fs`) + Checkout (`Checkout.fs`) wrappers, pluggable webhook idempotency (`Idempotency.fs` / `DurableIdempotencyStore.fs`), `StripeBillingProvider`, and the tier-token sink (`TierTokenSink.fs`). Still versioned `0.1.0-alpha` pending surface stabilisation; subscription lifecycle (dunning, invoicing, tax) remains out of scope.

Test pack: `src/ToolUp.Stripe.Tests` (Expecto; wired into `dotnet run -- VerifyAll`).

### `ToolUp.InterPlatform` — cross-deployment peer RPC

Opt-in, server-only companion that lets one deployment call a **typed contract** hosted by another ToolUp deployment (a *peer*) over the wire. A contract is an ordinary record of functions (same shape ToolUp.Remoting uses for in-deployment APIs); the substrate produces a typed initiator proxy (`JsonRpcPeerClient.create<'TApi>`) on the caller and a fail-closed JSON-RPC 2.0 host (`JsonRpcPeerHost.contract` + `routes`) on the receiver. Wire format is **JSON-RPC 2.0 over HTTP** — a deliberately open, language-neutral peer contract, *not* the in-tree ToolUp.Remoting transport. Two method shapes: immediate (`… -> Async<'T>`) and long-running (`… -> Async<PeerJobHandle<'T>>`, fused onto `IJobScheduler`). Identity rides a fail-closed HS256 JWT layer (`JwtPeerAuthProvider`, keys read per-call from `ISecretStore`); the receiver rebuilds the call context from the *validated* principal, never the self-asserted wire body.

Selected by `ServerConfig.PeerSubstrate = EnabledPeerSubstrate` (default `NoPeerSubstrate`). Compose with `PeerServerApp.run` (the `PeerCompose` companion root), which wraps a base `ServerApp`, registers the peer DI singletons from already-present substrate (`IBlobStorage` / `ISecretStore` / optionally `IJobScheduler` / `IAuditLog`), and mounts `/peer/v1/*`. When `NoPeerSubstrate`, `run` short-circuits byte-for-byte to `ServerApp.run` — zero cost when unused (GP 13). See [`src/InterPlatform/README.md`](src/InterPlatform/README.md) + [`TECHNICAL_GUIDE.md`](src/InterPlatform/TECHNICAL_GUIDE.md).

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

`ToolUp.Platform.Tests` ships contract test packs (`IJobSchedulerContract`, `IModuleQueryBusContract`, `IShareTokenStoreContract`, `IDataSourceContract`, `IEntityStoreContract`, etc.). Any external implementation can validate against the same conformance bar.

**Authorization-by-attribute (Phase 69d.tail) is part of the same structural-enforcement story.** API record methods declare their access requirement via `[<RequiresRole>]` / `[<RequiresClaim>]` / `[<TenantScoped>]` / `[<AllowAnonymous>]` / `[<PublicEndpoint>]` (the tier-shared `ToolUp.Platform.*` mirrors for Fable-compiled records), and the dispatcher's startup classifier refuses to start on any unclassified method. This satisfies rule 4 by construction: the handler's auth state arrives parameter-passed per request via the Phase 66 `Subject` resolution, never closure-captured — so any distributed dispatcher implementation evaluates the same normalised `AuthRequirement` data. Migration recipe: [`docs/migrations/69d-authorization-metadata.md`](docs/migrations/69d-authorization-metadata.md); audit twin: [`docs/migrations/69h-audit-annotation-sweep.md`](docs/migrations/69h-audit-annotation-sweep.md).

## Module convention (consumer-facing, 4 files per module)

Consumers organise their domain modules as four files. Single-fsproj — modules are not subject to the cross-tier split that applies to SDK companions. The Core/Server/Client split applies only to **publishable SDK packages** because DLL boundaries are part of their public contract; consumer modules are deployment-specific.

| File | Purpose | Compiled by |
|---|---|---|
| `SharedTypes.fs` / contracts | API record, DTOs, domain types | Both |
| `Server.fs` | Route handlers, data processing, `DataType` registration, AI tool metadata + executors | Server |
| `ClientModel.fs` | Elmish Model, Msg, init, update | Fable |
| `ClientView.fs` | Feliz view + `register()` returning `ErasedModule` | Fable |

Plus `.fsproj` + `.Client.props` (MSBuild props injecting client files into the consumer's Client project — hidden from Solution Explorer).

**Canonical sample**: `samples/HelloWorld/HelloWorld.Module/` shows the absolute minimum.

```fsharp
// SharedTypes.fs
module HelloWorld.SharedTypes
type HelloApi = { DoThing: string -> Async<string> }

// Server.fs
module HelloWorld.Server
let routine (input: string) : string = sprintf "did: %s" input

// ClientModel.fs
module HelloWorld.ClientModel
open Elmish
open ToolUp.Platform
type Model = { Text: string }
type Msg = NoOp
let init () : Model * Cmd<Msg> = { Text = "" }, Cmd.none
let update _ m = m, Cmd.none

// ClientView.fs
module HelloWorld.ClientView
open Feliz
open ToolUp.Platform
open HelloWorld.ClientModel
let view (model: Model) (dispatch: Msg -> unit) =
    Html.div [], Html.div [ Html.text model.Text ]
let register () : ErasedModule =
    ClientModule.create {
        Init = init
        Update = update
        Name = "Hello World"
        Icon = "/svg/chart.svg"
    }
    |> ClientModule.withView view
    |> ClientModule.register
```

## Companion-authoring guide

Companion packages live under named subdirectories (`AIProviders/`, `Storage/`, `AuditSinks/`, `NotificationChannels/`, etc.). Each:

- Has its own `.fsproj` with `<PackageId>` (unless assembly name = desired package id).
- Implements one or more SDK interfaces.
- Receives `ISecretStore` (or other substrate dependencies) through its `create` function — never reads env vars or config files directly.
- Has its own `<PackageReference>` items for vendor SDKs.
- Ships a `README.md` (packed into the nupkg via the auto-include in `Directory.Build.props`).
- Optionally provides `.Server.props` / `.Client.props` for source-injection delivery (used when the companion needs to inject `.fs` files into a consuming project rather than ship a DLL).
- Adds a per-package `<PackageTags>` override to aid discoverability.

For HTTP-shaped companions (audit sinks, notification sinks): use BCL `HttpClient` rather than a vendor SDK where the API is permissive. This minimises the dep graph and the OSS supply-chain surface.

For stateful companions (job scheduler, vector store, etc.): document explicitly in the file header whether the impl is dev-only or distributed-ready. Distributed-ready impls must be stateless between handler calls (rule 4). Dev-only impls are clearly marked.

A companion's effect / determinism / distributed-readiness posture can additionally be declared as a **typed, queryable value** — `CompanionCapability` (`ToolUp.Platform.Core`, `Shared/CompanionCapability.fs`): `EffectClass` (`Pure` | `Effecting`), `DeterminismSource` (`Deterministic` | a set of `DeterminismFactor`s), and `Readiness` (`DistributedReady` | `DevOnly`). Each axis is a join-semilattice whose bottom (pure / deterministic / distributed-ready) is `CompanionCapability.identity`, the value an *undeclared* companion contributes — so a deployment that declares nothing is byte-for-byte unchanged (GP 11). Declare a posture with the reference constants (`distributedEffecting` / `devOnlyEffecting` / `pure'`) or the fluent `withEffect` / `withDeterminism` / `withReadiness` helpers, keyed by the companion's stable `ComponentId`. The descriptor is *read* by the introspection manifest + preflight (it makes the file-header prose machine-checkable), and joined componentwise into a composed-app effect signature; it is never a hard runtime gate on its own — the opt-in `CompositionCapabilityGate` is.

### Native-dependency companions (P/Invoke)

Every shipped companion to date wraps a managed SDK. A companion that wraps a *native* library (via P/Invoke) follows these additional conventions:

- **RID-specific vendoring.** Native binaries ship inside the nupkg under `runtimes/{rid}/native/` (`runtimes/win-x64/native/`, `runtimes/linux-x64/native/`, `runtimes/osx-arm64/native/`, …) so the .NET host resolves the right artefact per platform at restore time. Declare only the RIDs actually built and tested. An absent RID must fail loudly at companion `create` time — probe for the native library and raise a descriptive error naming the missing RID — never at first P/Invoke call deep in a request path.
- **Narrow C-shim facade.** Bind against a deliberately small, stable C surface. If the upstream API is wide or C++-shaped, vendor a thin C shim exposing only the entry points the companion needs. All `DllImport` extern declarations live in a single `Native.fs` per companion; everything above it is ordinary managed F# implementing the SDK interface. Treat the extern file like a wire contract — changes are reviewed as breaking-change candidates.
- **Native artefact CI: build-or-vendor, hash-pinned.** Decide per companion whether CI *builds* the native artefact from pinned upstream source or *vendors* a prebuilt binary. Either way the artefact is hash-pinned — SHA-256 recorded in the repo and verified during the build — so a silently-swapped binary fails the build instead of shipping. Vendored binaries record upstream version, source URL, and hash in the companion README.
- **Packaging + licensing for LGPL-class native deps.** Dynamic linking only — the P/Invoke boundary is dynamic by construction; keep it that way (no static linking of LGPL code into a shipped artefact). Ship the native library unmodified, credit it in `NOTICE.md` (licence + version) and the companion README, and keep the binary user-replaceable — the `runtimes/{rid}/native/` layout already satisfies LGPL's relinking expectation. Stronger-copyleft (GPL-class) native deps are not shippable as companions consumers compose by default; if one is genuinely needed, gate it behind explicit opt-in and document the licence implications in the README.

Everything else follows the standard companion rules above: GP 1 isolation (the native dependency never reaches `ToolUp.Platform.*`), substrate dependencies through `create`, README packed into the nupkg, and an explicit dev-only vs production-ready declaration in the file header.

## Store-substrate authoring (file layout + opt-in wiring)

When adding a new store substrate (an `IXxxStore` + its types), the canonical split is decided by
**Fable-safety + client-facing, not habit**:

- **Fable-safe shared types → `src/ToolUp.Platform.Core/Shared/XxxTypes.fs`** — records/DUs for
  schema, values, page reads, errors: anything a client renders or that crosses the wire. Only
  `Platform.Core` ships its source under `fable/` in the nupkg (GP 10), so only types housed there
  are Fable-compilable. Register the file in `ToolUp.Platform.Core.fsproj` near `DataObjectTypes.fs`.
- **Server-only interface + default impl → `src/ToolUp.Platform.Server/Server/IXxxStore.fs` +
  `XxxStore.fs`** (canonical precedent: `IDataObjectStore` — types in Core, interface in Server).
- **Crypto-addressed / server-only-compute types stay in Server** — e.g. SHA-256-addressed envelope
  types use `System.Security.Cryptography`, which is not Fable-compilable, and belong in
  `Platform.Server/Server/` even though "types" usually go to Core.
- **Opt-in wiring (GP 13):** an `XxxStoreMode` DU + a `ServerConfig.Xxx` field defaulting to `NoXxx`
  in `SDK.Shared.fs`; a `registerXxxStore` helper in `Server/Compose/ComposeStores.fs`
  (`TryAddSingleton` with a lazy factory); called from `Server/SDK.Server.fs` next to
  `registerTimeSeriesStore`. Note a new `ServerConfig` field retypes the record ctor and forces a
  Core api-baseline regen (see [Public-API approval baselines](#public-api-approval-baselines-phase-175)).

**Vendor dependencies never enter `ToolUp.Platform.*` (GP 1).** `ToolUp.Tabular` carries
`DocumentFormat.OpenXml` for its XLSX leg, and its `.fsproj` says so: the vendor dep stays there. Do
NOT add a `ProjectReference` from any `ToolUp.Platform.*` project to `ToolUp.Tabular` — even to call
the BCL-only CSV path — because it drags OpenXml into the SDK core's dependency graph. When SDK-core
code needs Tabular-style behaviour, cut a dependency-free interface seam in `Platform.Server`, ship a
BCL-only default over the coarse schema, and let a Tabular-backed implementation be composed over the
seam (precedent: `IMappingDryRunValidator`).

**The remoting `IIdempotencyStore` is deliberately NOT composed by `ServerApp`.** Unlike
`IOAuthStateStore`, forge never builds or DI-registers an idempotency store — it is wired per-API by
the consumer via `Remoting.withIdempotencyStore` inside `Api.make(customOptions = …)`, and the
default `RemotingOptions.IdempotencyStore` is `None`. Any validator or preflight over it must probe
the DI `IServiceCollection` (the `DeployPlaneDepsValidator` shape) — there is no forge-held instance
to inspect, and customOptions-only wiring is invisible to preflight by design.

## Props-injection pattern (legacy source-injection contract)

Some Client-tier companions still inject source via `.Client.props` rather than ship a DLL nupkg. The contract: a companion's `.Client.props` file extends the `_ToolUpPlatformClientSources` item group, and `ToolUp.Platform.Client.props` (in the consuming app's MSBuild graph) prepends those items to `<Compile>` before CoreCompile.

The cross-tier source-injection pattern is gradually being phased out as Client-tier companions migrate to Fable source-in-nupkg delivery (the `<Content Include="**\*.fsproj;**\*.fs" Exclude="**\*.fs.js;**\bin\**;**\obj\**" PackagePath="fable\" />` convention). Both paths coexist today.

## Source-in-nupkg conditional-directive rule

Client-tier SDK packages ship their `.fs` files under `fable/` in the nupkg; a Fable consumer's package loader extracts the source and compiles it as part of the consumer's project. Each extracted package carries its own `.fableproj` with an empty `<DefineConstants>` — whether the consumer's defines (e.g. `DEBUG`) propagate to the extracted compilation unit is not part of Fable's documented contract. Empirically Fable 5.x walks the consumer's MSBuild graph and applies the root project's defines, so today the propagation does work; that behaviour is brittle to rely on.

**Rule:** in any `.fs` file packed under `fable/`, the only acceptable compile-time gates are `#if FABLE_COMPILER` / `#if !FABLE_COMPILER` (Fable defines this constant itself, so propagation isn't a question) or gates against a constant the SDK's own `.fsproj` declares explicitly. `#if DEBUG` / `#if !DEBUG` and any custom consumer-side define MUST be checked at runtime instead — via a `BundleConstants.*` accessor, a `window?Foo`-style feature detection, or by unconditionalising the branch and accepting the production cost. A `console.warn` is cheap; a silently-skipped or silently-included branch is not.

## Nullable reference types — keep disabled on Fable-touching projects

F# 9+ supports nullable reference types via `<Nullable>enable</Nullable>` in a `.fsproj`. The Fable compiler itself supports nullness (Fable 5.0.0+). However, enabling nullness in a Fable consumer causes the compiler to re-compile every transitive Fable dependency in null-aware mode, and any dependency that hasn't been nullness-annotated produces a cascade of warnings (or errors, depending on configuration).

As of 2026-05, the major Fable-ecosystem libraries this SDK consumes or embeds (Feliz, Fable.SimpleJson, the in-tree Elmish runtime, the in-tree ToolUp.Remoting transport) have not been nullness-annotated, and there's no published timeline for that to land.

**Rule:** do not set `<Nullable>enable</Nullable>` on any project that compiles via Fable or whose Fable-compiled output consumes Feliz / Fable.SimpleJson / the in-tree Elmish / ToolUp.Remoting runtimes. Leave the property unset (which inherits F#'s default of `disable`), or explicitly set `<Nullable>disable</Nullable>` for projects sitting in a solution whose `Directory.Build.props` enables nullness by default. Server-only projects with no Fable involvement may enable nullness per the standard F# 10 default.

This rule retires when the upstream packages ship nullness annotations.

## Type erasure boundaries

Type erasure (`box`/`unbox`) is contained in six sanctioned boundaries inside forge:
1. **`ClientModule.register`** — erases per-module `'Model`/`'Msg` for the heterogeneous module list.
2. **`DataTypeDisplay.RenderSummary`** — every data-producing module boxes its summary record in its server-side `DataType.Process` and unboxes in the client-side `RenderSummary` callback. Symmetric same-module-known-type cast on both ends.
3. **Narrative `Component` block renderers** (Phase 87) — the `NarrativeElement.Component of name * props` case carries a stringly-typed `Map<string,string>` rather than a typed payload, so a deployment can register custom block renderers (`props -> XmlNode` in `NarrativeLayout`, bridged to the SDK's `string -> Map -> string option` resolver in `NarrativeHtml.RenderOptions`) without forking the `NarrativeElement` DU. The "erasure" is the stringly-typed prop bag at the registry seam; renderers are pure and resolve by name, with an unregistered name degrading to a safe placeholder.
4. **`GridApiRegistry`** (`AgGrid.fs`) — boxed `IGridApi` handles in a keyed registry; symmetric unbox at the same-module read site.
5. **`EntityStore` registrations** (`Server/EntityStore.fs`) — symmetric box/unbox of `EntityRegistration<'T>` in the name-keyed registration dictionary.
6. **`AuthUIProvider` handler dispatch** (`AuthUIProvider.fs`) — the auth-UI delegate registry boxes the provider config for the registered handler.

Fable/JS interop boxing (React dependency arrays, `isNull (box x)` probes, erased-type coercions in the AG Grid bindings) and `HttpContext.Items` stamps are idiomatic interop, not domain erasure, and don't count against this list. Module code outside these boundaries never sees type erasure.

## Build pipeline

```bash
dotnet build ToolUp.Forge.sln       # full build
# Canonical "run every Expecto test pack" aggregator — sequential,
# fails the target on any pack's non-zero exit, prints a one-line
# per-pack summary at the end:
dotnet run --project Build.fsproj -- VerifyAll
# Individual packs (useful during iteration on one pack):
dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
dotnet run --project src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj
dotnet run --project src/ToolUp.Scheduling.Tests/ToolUp.Scheduling.Tests.fsproj
# Live-API integration pack for the shipped AIProvider companions.
# Env-gated: each per-provider arm runs when its API-key env var is set
# (ANTHROPIC_API_KEY / OPENAI_API_KEY / GEMINI_API_KEY); arms with the
# env var unset report Pending, not Failed, so a fresh checkout is green
# without per-provider credentials. Live arms exercise a streaming
# system + user + tool round-trip plus an IProviderProfile factory
# round-trip through DefaultAIProviderFactory.Resolve.
dotnet run --project src/ToolUp.AIProviders.Tests/ToolUp.AIProviders.Tests.fsproj
# Client-tier Fable test harness for AI.Client MVU surfaces (Phase 70
# E.4 follow-on). Different shape: transpiles via Fable + runs under
# Node's built-in test runner (`node:test`, zero npm test-runner
# deps); see docs/platform/testing-conventions.md for rationale and
# the full procedure. The canonical invocation is the FAKE target
# below, which owns all four steps (tool restore, `npm ci`, the Fable
# compile, the node:test run) so CI and a developer cannot drift:
dotnet run --project Build.fsproj -- VerifyFable
# The four steps it wraps, for when you need to iterate inside one:
#   cd src/ToolUp.AI.Client.Tests
#   dotnet tool restore && npm install --no-fund --no-audit
#   dotnet fable -o output --noCache
#   node --import ./register-loader.mjs --test output/Program.js
dotnet run --project Build.fsproj -- Pack   # produce nupkgs to ../local-nuget-feed
dotnet run -- Format                # fantomas
dotnet run -- ThirdPartyNotices     # regenerate THIRD_PARTY_NOTICES.md
```

**Do not run `dotnet test` against the solution or these projects.** They are Expecto console runners (`<OutputType>Exe</OutputType>` + a `Program.fs` entry point), so `dotnet test` exits 0 having run nothing — a silent false-green. Each runner exits non-zero on failure; the real non-breakage gate is a full `dotnet build ToolUp.Forge.sln` (catches cross-companion breakage that per-project builds miss) plus the fourteen `dotnet run --project` Expecto suites `VerifyAll` runs with 0 failures (`Platform` / `Forms` / `Scheduling` / `Stripe` / `Build` / `RemotingAnalyzers` / `Cli` / `Voice` / `AICookbooks` / `Algorithms` / `AlgorithmProviders` / `CloudParity` / `ArtefactSigning` always-on; `AIProviders` env-gated — clean on a fresh checkout, asserts live when an API-key env var is set), plus the Fable-tier `AI.Client.Tests` runner (Node's built-in `node:test` against the Fable-transpiled output; runs via the `node --import ./register-loader.mjs --test output/Program.js` invocation shown above).

**Shortcut for all fourteen Expecto packs**: `dotnet run --project Build.fsproj -- VerifyAll` runs all 14 sequentially (`Platform` / `Forms` / `Scheduling` / `AIProviders` / `Stripe` / `Build` / `RemotingAnalyzers` / `Cli` / `Voice` / `AICookbooks` / `Algorithms` / `AlgorithmProviders` / `CloudParity` / `ArtefactSigning` — `BuildConfig.TestPacks` in `Build.fs` is the authoritative list) with a per-pack summary at the end — the canonical "run everything" invocation. Each per-pack `dotnet run --project` shown above is still the right shape for iteration on one pack.

**Shortcut for the Fable tier**: `dotnet run --project Build.fsproj -- VerifyFable` (Phase 614) runs the client-tier harness end to end — `dotnet tool restore`, `npm ci`, `dotnet fable -o output --noCache`, then `node --test` over the transpiled output. It asserts the TAP **pass / fail counts**, not just the exit status, because `node --test` exits 0 when it matched no test file at all; a harness that stopped emitting cases is otherwise indistinguishable from a green run. The floor is a lower bound (currently 100 against 131 shipped cases), so adding a case never needs an edit here.

**Shortcut for the template content**: `dotnet run --project Build.fsproj -- VerifyTemplates` compiles the `dotnet new` scaffolds under `templates/`. It packs the six-package closure the templates reference (`ToolUp.Platform.{Core,Client,Server}` + `ToolUp.AI.Wire` + `ToolUp.Graph.{InMemory,Core}`) at a throwaway `0.0.0-templategate` version into a scratch feed, then builds each template against it.

The throwaway version is load-bearing, not cosmetic: packing at `$(ToolUpSdkVersion)` would be a same-version repack, and NuGet would resolve from the already-extracted global-packages entry — so the gate would compile the templates against **whatever was packed last** rather than current source. The target wipes those cache entries first. NU1603 is escalated to an error for the same reason: it fires when a gate-versioned package declares a `ToolUp.*` dependency that was not packed at the gate version, and NuGet then silently falls back to an older feed version. **Adding an SDK→SDK dependency therefore fails this gate by name** until the package is added to `templateGatePackages` in `Build.fs`.

`templates/safer/` and `templates/platformsdk-solution/` are deliberately **not** covered — they are standalone solutions carrying their own `nuget.config` (a `../local-nuget-feed` path resolved relative to the consumer's instantiated location) and, in `safer`'s case, a literal `TOOLUP_SDK_VERSION` placeholder substituted at instantiation. Neither is buildable in-repo without rewriting what makes it a template; gating them needs an instantiate-then-build harness.

### What CI actually gates (Phase 614)

Written down here so the next reader does not have to re-derive it from `.github/workflows/checks.yml` — three phases in one batch had to. **Read the "gates?" column, not the job list**: a job existing is not the same as a job gating.

| Job | Checks | Gates? |
|---|---|---|
| `spdx-headers` | Apache-2.0 SPDX header on every Fable-packed source file | yes |
| `fantomas` | `dotnet fantomas --check .` over the repo | yes |
| `dco` | `Signed-off-by:` on every commit in a PR | PR only |
| `fable-wire-smoke` | `ToolUp.AI.Wire` compiles + round-trips on both the .NET and Fable hosts | yes |
| `ai-wire-conformance` | the connector mappers produce identical output across both hosts, over one corpus | yes |
| **`fable-tier`** | the **client-tier `node:test` harness** (131 cases, every Sidebar pack) via `VerifyFable` | **yes** |
| **`verify-all`** | `dotnet build ToolUp.Forge.sln` then **all fourteen Expecto packs** via `VerifyAll` | **yes** |
| `doc-snippets` | every in-scope `fsharp` block under `docs/**` compiles, via `VerifyDocSnippets` | yes |
| **`templates`** | the **`dotnet new` scaffolds under `templates/`** compile, via `VerifyTemplates` | **yes** |

Everything marked "yes" runs on every push to `main` and every PR against it. `dco` is PR-only because direct-to-main is this repo's normal integration path, so signed-off discipline there relies on the local commit template.

Both test gates were local-only until Phase 614. `fable-tier` was demonstrated red on a scratch branch against a deliberately broken `node:test` case before landing, because an unproven CI gate is precisely the failure mode it exists to prevent. `verify-all` shipped dispatch-only because the .NET suite turned out **not to be green on Linux**; Phase 617 fixed the four blockers and promoted it. Neither was ever made green by excluding packs or swallowing exit codes — that trades a gate for a tick.

Both jobs are written so they **cannot pass vacuously**. `verify-all` parses the `VerifyAll summary:` block and fails unless at least `EXPECTED_PACKS` packs report `PASS` (the floor is currently 12 while fourteen ship — raising it to 14 restores the vanished-pack protection for the two newest and is a pending one-line tidy in `checks.yml`) — the target legitimately exits 0 with an empty `BuildConfig.TestPacks`, which would otherwise read as green. `VerifyFable` fails unless the TAP summary exists and clears its case floor, because `node --test` exits 0 when it matched no test file at all.

#### Two traps the Linux gate exposed (Phase 617)

Running the suite somewhere other than a Windows dev box found four blockers, and **two of them were defects in shipped SDK code rather than test-environment noise**. Both are shapes that any future pack or backend can walk into again, so they are written down here rather than only in the phase file:

- **Blob names are `/`-delimited on `IBlobStorage`, always.** `LocalFileStorage.List` was returning `Path.GetRelativePath` output, i.e. the OS separator. The break was silent, because `Download` accepts either separator on Windows — what failed was callers that strip a known prefix to recover an id (`name.Replace("memberships/", "")`), which simply no-opped and returned a mangled id. That is how `TeamStore.GetTeamMembers` reported `UserId = "memberships\alice"`, `IsLastOwner` never matched, and **the last Owner of a team could be removed on Windows**. Now normalised, and pinned by the `IBlobStorage` contract pack so every backend is held to the same shape. If you add a backend or a `List` caller, that contract test is the one to read.
- **Expecto deadlocks when parallel tests write to the console, so every pack now runs sequenced by default.** Expecto replaces `Console.Out`/`Console.Error` with a synchronized writer; a parallel test writing through it can take that writer's monitor, descend into `ANSIOutputWriter.flushInner` → the ProgressIndicator, and block on the real console stream lock while siblings hold the writer's. This is not confined to the pack that surfaced it — `ToolUp.Cli.Tests` hung 6 runs in 6 and burned a 60-minute CI timeout, and `ToolUp.Platform.Tests` deadlocks identically, having only been getting away with it because a 2-core runner rarely wins the race (still 2-in-6 at `--parallel-workers 2`). No CLI flag avoids it (`--no-spinner` still hangs, `--colours 0` is worse) and Expecto 11.1.0 is worse than the pinned 10.2.3. Each `Program.fs` therefore passes `CLIArguments.Sequenced` as a default — overridable with `--parallel`, and effectively free: the 5,214-case Platform pack runs in **4m28s** sequenced. Rationale, measurements and the rule for new packs: [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md).

The other two were environmental but worth knowing: the `IAssetStore` pack needs a SkiaSharp native for the running RID (`ToolUp.Platform.Tests` references `SkiaSharp.NativeAssets.Linux.NoDependencies`; the shipped `SkiaSharpDerivativeRenderer` now probes at construction and names the missing package rather than failing at first render), and the `IContainerScheduler` real-backend leg needs `alpine:latest` in the local image cache — the Docker HTTP API does not pull on demand the way `docker run` does, so the job pre-pulls it.

`dotnet run --project Build.fsproj -- Pack` walks every public-surface SDK fsproj (filtered against `IsPackable=false`) and packs each individually into a local feed (default `../local-nuget-feed/`). ~9 minutes for a clean cold pack of ~43 packages; subsequent packs are incremental. Point a consumer's `nuget.config` at the same folder to test unreleased changes end-to-end.

The `Publish` FAKE target (`dotnet run -- Publish`) packs every public-surface SDK fsproj into a per-run `./artifacts/` directory and pushes each `.nupkg` to **nuget.org** (the default source since the 2026-08-19 cutover; the old `ToolUp-Forge` GitHub Packages feed is frozen and no longer pushed to). CI workflow is [`.github/workflows/publish-nuget.yml`](.github/workflows/publish-nuget.yml) — triggers on tag `v*.*.*` (or manual `workflow_dispatch` to heal a failed tag run — a dispatch packs main's current `<Version>`, tagged or not, so never dispatch ahead of a tag); authentication is nuget.org **Trusted Publishing** (the workflow's login step exchanges its OIDC token for a one-hour key — no stored credential; the only repo secret is the `NUGET_USER` profile name). The push is idempotent (`--skip-duplicate`) so re-runs after a transient failure skip versions already published. Local manual publishes set `NUGET_API_KEY` (a classic push-scoped api.nuget.org key — the exception path; CI never uses one) and optionally override `TOOLUP_PUBLISH_SOURCE`. Symbol packages (`.snupkg`) are pushed automatically to nuget.org's symbol server (`dotnet nuget push` detects the sibling file) and also remain in `./artifacts/` for local inspection.

### Fast iteration

`dotnet build ToolUp.Forge.sln` evaluates ~50 fsprojs (~1.5–6 minutes); a one-shot Fable compile is similarly expensive. Use:

```bash
# Targeted fsproj build
dotnet build src/ToolUp.Forms.Server/ToolUp.Forms.Server.fsproj

# Watch loop for one fsproj
dotnet watch build --project src/ToolUp.Platform.Server/
```

Full-sln verification is for end-of-task / pre-commit, not per-edit.

## Build verification

After every step: `dotnet build` (fast).
At phase boundaries: full Fable JS verification — `cd samples/MinimalClient && dotnet fable -o output`. Spot-check the emitted JS. (`MinimalClient.fsproj` ProjectReferences `ToolUp.Platform.Client`, so this drives the full Client-tier source tree through the Fable compiler; `samples/MixedMode/src/Client/` is the multi-module alternative when the change touches module wiring.)

**When this applies:**
- Any edit to a `Client/` source file or a file consumed by Fable.
- Any refactor of interface signatures crossing the client/server boundary.
- Any change to a module using `[<Erase>]`, `[<ReactComponent>]`, `inline` members, `importSideEffects`, `import`, or explicit `emitJsExpr`.

**Always pass `-o output` when verifying.** Bare `dotnet fable` emits `*.fs.js` next to source and leaves `output/` stale.

**Never reference a bare `..\Foo.fs` from a Fable test project.** Fable mirrors each source's
relative path under `output/`, so an immediate-parent reference resolves to `output/../Foo.js` — a
stray transpiled `.js` in the project root that `.gitignore` (`output/` only) does not cover and
`git add <dir>` will stage. References into a subdir (`..\Shared\Foo.fs`) stay safely under
`output/`; keep shared sources in a `Shared/` subdir so both host projects reference them that way.
(Bonus from the same class: an XML comment in a `.fsproj` cannot contain `--` — `dotnet build`
tolerates it but `dotnet fable`'s msbuild crack fails with MSB4025.)

**Expecto `--filter` joins the test path with `.`, not `/` — and a filter that matches nothing
reports SUCCESS.** A slash-shaped filter (the shape test names suggest) selects zero tests, prints
`0 tests run … Success!` and exits 0: a vacuous green. Read the *count*, never the exit code; if a
filtered run reports suspiciously few tests, `--list-tests` first, or make the filter fail once
(break a test it should select) before believing a green.

**`MemoizedChart` must NOT be `private`.** `AgChart.chart` is a `static member inline` on an `[<Erase>]` type; Fable inlines the method body at every call site, which means call sites import `MemoizedChart` directly. If it's `private`, Fable doesn't export it → runtime `SyntaxError: does not provide an export named 'MemoizedChart'`. Same rule for any module-level value referenced from an `inline` method on an erased type.

## Public-API approval baselines (Phase 175)

`ToolUp.Platform.Tests`' approval gate renders each packable `ToolUp.*` assembly's public surface
and diffs it against `api-baselines/<assembly>.approved.txt`. The rules that matter when a phase
touches public surface:

- **It fails in BOTH directions (Phase 618): removed / renamed / retyped members, AND additive
  growth whose baseline was not regenerated** — the failure names the added members and the baseline
  file, so any new public surface means a surgical regen for the affected assemblies. (Until 618 the
  gate was one-directional and additive growth passed silently; this bullet said so long after it
  stopped being true, and cost a session a red `VerifyAll`.) A changed return type counts as a
  retype (the old token is "lost"). F# module functions and record fields are public by default, so
  an "internal-looking" compose helper or a Client `Msg`/model record is tracked surface.
- **Optional constructor args (`?foo`) read as a REMOVAL.** `type Foo(bar, ?policy)` folds into ONE
  widened ctor, so the pre-existing `Foo..ctor(bar)` token disappears — a genuine break, not a false
  positive. Use explicit secondary constructors (`new(bar) = Foo(bar, defaultPolicy)`) to keep the
  diff additive.
- **A field added to a shared record ripples exactly as far as EMBEDDING, not reference.** The type's
  own assembly reddens (the compiler-generated ctor gains a parameter — every `ServerConfig` field
  addition regens Core), plus any downstream assembly whose own public record/DU embeds the type. An
  assembly that merely takes it as a parameter/return renders only the type NAME and needs no regen.
  After a surgical regen, re-run the FULL test pack — not just the approval filter — to catch a
  downstream baseline you missed.
- **Regen is non-surgical: `TOOLUP_APPROVE_API=1` rewrites EVERY built baseline** (~95 files),
  folding in unrelated additive drift and EOL-only churn. Build the whole solution first (the
  renderer reads DLLs), regen, then `git restore` every baseline except your surgical targets and
  stage those by name. Verify the real diff WITHOUT the env var — approve mode passes trivially, so
  its green proves nothing. Never run the regen in a shared working tree (it rewrites files
  concurrent sessions have in flight), and never apply baseline hunks with `git apply --3way` there
  (it writes the shared index as a side effect).
- **A worktree regen is only valid against the HEAD it is pinned to.** If surface lands between your
  pin and your apply, copying the regenerated file back silently deletes the landed lines — re-pin
  against the current HEAD copy, apply only your own hunks, and diff-verify zero foreign removals.
- **An untracked sibling packable project reddens the gate** (`discoverPackable` walks disk, not the
  git index): a concurrent session's uncommitted `src/**/*.fsproj` with no committed baseline fails
  the new-package arm. It is not your break — confirm with `git status --short` and note it; do not
  author the sibling's baseline.
- **A failing baseline names an assembly, not a cause** — confirm attribution with `git log -- <source
  files>` before writing it into a commit message.
- **Design corollary:** a new opt-in feature that would widen a shared record's ctor can instead
  ship as a NEW options record + NEW builder entry points — existing builders delegate with a
  behaviour-preserving default (GP 11). Since Phase 618 the new surface still needs its assemblies'
  baselines regenerated (additions are a named, surgical regen rather than a silent pass), but the
  existing types' baselines stay untouched and no consumer breaks.

## F# style + idioms

- **Runtime**: .NET 10 (`net10.0`).
- **Language**: F# 10 (the SDK pin in `global.json` is `10.0.203`). All workspace siblings are on the same baseline; assume F# 10 features are available without further qualification.
- **Formatter**: Fantomas — pre-commit step. Run `dotnet fantomas <file>` BEFORE `dotnet build`.

### Fantomas pitfall: indexer ambiguity

**Never write `map[key] arg1 arg2` on a single line.** F# treats `map[key]` (no space) as an indexer and `map [key]` (with space) as a list-application. Fantomas can insert a space in the no-space form, breaking compilation. Extract the indexer read to its own line:

```fsharp
let pageView = map[route]
pageView currentState dispatchMsg
```

### Raw control bytes in source — never

A raw NUL (or any raw control byte) embedded in a source file makes git classify the file **binary**
(`-text` in `git ls-files --eol`), which permanently disables `.gitattributes` EOL normalisation for
it — the file stays CRLF in an all-LF repo and re-dirties on every Fantomas pass. Use the F# escape
sequence instead — backslash + u0000, spelled out in prose here because tool payloads carrying the literal sequence have repeatedly mangled it into a real NUL (this very paragraph landed one on first write): it compiles to the identical char (same hashes, same test input) and the
file stays text. To find offenders: `git ls-files --eol | grep -- -text`. Beware that writing the
escape VIA A TOOL can itself land a raw byte — build the needle and replacement programmatically
(e.g. PowerShell `[string][char]0` / `[char]92 + 'u0000'`), then byte-scan the file to prove zero
control bytes remain.

### Lambda preference: `_.Property` over `fun x -> x.Property`

F# 10 (and any version 8+) supports `_.Property` and `_.Method()`. Prefer it for one-step access:

| Avoid                                              | Prefer                              |
|----------------------------------------------------|-------------------------------------|
| `xs \|> Array.maxBy (fun x -> x.Index)`            | `xs \|> Array.maxBy _.Index`        |
| `opt \|> Option.map (fun e -> e.Body)`             | `opt \|> Option.map _.Body`         |

Method-call lambdas need parens: `AgGrid.onGridReady (_.AutoSizeAllColumns())`.

### Elmish MVU discipline

- `update` functions must be pure. All side effects flow through `Cmd`.
- No mutable global state in client code (except documented exceptions).
- Text inputs use `React.useState` for display state. Only dispatch on submit (Enter / button click). No per-keystroke `UpdateInput` messages.
- Modules declare what they are (`Definition`), what they need (`NeedsData`), what they provide (`ProvidesProcessedData`, `DataTypes`), and how they behave (`Init`, `Update`, `View`). The shell handles all wiring.

### Serialisation

- **ToolUp.Remoting APIs**: handled automatically by the transport (the in-tree Fable.Remoting fork bundled inside `ToolUp.Platform.{Core,Client,Server}`).
- **SSE / non-Remoting JSON**: must use `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()` (returns a `System.Text.Json.JsonSerializerOptions` with the full F# converter set registered — Option / DU / tuple / record / CLIMutable / list / Map / Set / decimal / DateTime / DateOnly / TimeOnly / byte[] / DataSet / DataTable / etc.). Construct once at module level, then call `JsonSerializer.Serialize(value, options)` / `JsonSerializer.Deserialize<'T>(json, options)`. The options instance is mutated to set `PropertyNameCaseInsensitive = true` and `Encoder = UnsafeRelaxedJsonEscaping` — match the Fable.SimpleJson wire shape and absorb camelCase inputs without ceremony. Do NOT use plain `JsonSerializerOptions()` without `FableConverters.addTo` — F# DUs / Option / records all break on the wire. The legacy `Fable.Remoting.Json.FableJsonConverter` (Newtonsoft) was retired in the STJ migration — `Newtonsoft.Json` is no longer a forge dependency.
- **`unit -> Async<T>` API functions**: work because body normalisation is folded into the dispatcher itself (shipped 0.4.0). The standalone `RemotingBodyNormalizationMiddleware` that 0.3.x relied on was retired — `dotnet build` is the gate, not a middleware presence check.
- **Consumer dependency contract**: server projects consuming `ToolUp.Platform.Server` need no extra `Fable.Remoting.*` / `ToolUp.Remoting.*` PackageReferences — the transport, the JSON converter set, and the Giraffe / ASP.NET Core adapters all arrive transitively via `ToolUp.Platform.Server`. `System.Text.Json` ships in the BCL.
- **Additive fields on persisted records: the STJ path yields `null`, the SimpleJson path THROWS — handle both.** `FableConverters` initialises absent reference-type fields to `null`, so a blob persisted before a field was added comes back with `null` where a `list`/`Map` should be — and a null F# `list` NREs on every list op (`[]` is the `Empty` singleton, NOT null; only `option`'s `None` is null). Coerce at the store read path AND in the pure consumer (`if isNull (box x.Field) then { x with Field = [] }`; `isNull (box …)` is the Fable-safe check), and test by deserialising a JSON literal that OMITS the field. Meanwhile Fable.SimpleJson's `Json.parseAs<'T>` (browser-localStorage records) is the opposite: a missing field **throws** — and a `try/catch reset` fallback then silently discards ALL the user's persisted state, not just the new field. Backfill absent fields into the raw JSON before `parseAs` (array `[]` for list/Set, object `{}` for Map); a post-parse coercion cannot work because parse throws first.

### AG Charts axes + animation

- `AgChart.axes` uses a direction-keyed object (`"x"` / `"y"`), not an array (AG Charts v13+ regression).
- `AgChart.chart` uses a memoised wrapper (`MemoizedChart`) to preserve animations on Elmish re-renders.
- Don't add `prop.key` to the chart `Html.div` wrapper when underlying data changes — forces React remount, destroys the chart instance, prevents transition animations.

### AG Grid / AG Charts binding reference (Phase 12e)

The bindings target **ag-grid 35.3.0** / **ag-charts 13.3.0** (pinned in the samples' `package.json`; the binding-version contract follows those pins). Comprehensive typed surface — events, filters, theming, CSV/Excel export, the full series catalogue — reached at ~80% of the published public API.

- **Community** bindings: [`src/ToolUp.Platform.Client/Client/UI/AgGrid.fs`](src/ToolUp.Platform.Client/Client/UI/AgGrid.fs) + [`AgChart.fs`](src/ToolUp.Platform.Client/Client/UI/AgChart.fs). Cookbook: [`src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md`](src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md) — the canonical authoring reference (constraints → shortest-possible → recipes → anti-patterns), single-Read for an AI agent.
- **Enterprise** file split: [`src/AgGridEnterprise/AgGridEnterpriseTypes.fs`](src/AgGridEnterprise/AgGridEnterpriseTypes.fs) (grid: Set Filter / Multi Filter / Excel Export / Master-Detail / Status Bar / Sidebar / charts integration / SSRM / custom aggs) + [`AgChartEnterpriseTypes.fs`](src/AgGridEnterprise/AgChartEnterpriseTypes.fs) (Sankey / Sunburst / Treemap / Candlestick / Ohlc / Heatmap / Waterfall / Box-plot / Range series + Sparkline). Cookbook: [`src/AgGridEnterprise/COOKBOOK.md`](src/AgGridEnterprise/COOKBOOK.md).
- Source of truth: the `.d.ts` under `node_modules/ag-{grid,charts}-{community,enterprise}/dist/types/src/` and `node_modules/ag-charts-types/`. In 13.3.0 heatmap / waterfall / box-plot / range-bar / range-area are **Enterprise-only** (bound in the companion, not Community).
- `MemoizedSparkline` (Enterprise) follows the same **non-`private`** rule as `MemoizedChart` — a module value referenced by an `inline` member on an `[<Erase>]` type must export, else a consumer sees a runtime "does not provide an export" `SyntaxError`.
- Enterprise series types are erased and emit **no** JS imports; the sole `ag-charts-enterprise` / `ag-grid-enterprise` imports stay module-top-level in `AgGridEnterprise.fs`.
- Long-tail features (Advanced Filter custom UI, Viewport Row Model, nightingale / radial / radar series, Annotations) remain reachable via documented `obj` escape hatches.

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
