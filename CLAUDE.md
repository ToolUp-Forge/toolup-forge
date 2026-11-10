# CLAUDE.md — ToolUp Forge SDK

## What is ToolUp Platform?

ToolUp Platform is a modular F# full-stack SDK for building production multi-tenant analytical applications. It ships as a set of independently-versioned NuGet packages under the `ToolUp.*` namespace; consumers compose them with their own domain modules.

Core foundations:
- **Server**: Giraffe over ASP.NET Core; the in-tree **ToolUp.Remoting** transport (the `ToolUp.Platform.Server.Remoting` part, which `ToolUp.Platform.Server` references) for type-safe APIs. Consumers write `open ToolUp.Remoting.Server` / `open ToolUp.Remoting.Giraffe`; no separate `PackageReference` is needed.
- **Client**: Fable + Feliz (React bindings) with the in-tree **ToolUp.Elmish** MVU runtime (distributed inside `ToolUp.Platform.Client`). Consumers write `open ToolUp.Elmish` and see classical Elm Architecture plus ToolUp additions (`IDispatcher<'msg>`, `Prefetch<'a>`, structured `ErrorContext`, `EffectHandle` lifetimes, `Cmd.OfRemoting`).
- **Build**: FAKE targets in a Pack-able `ToolUp.Platform.Build` package.

Both substrates began as forks — ToolUp.Remoting from Zaid Ajaj's Fable.Remoting (MIT), ToolUp.Elmish from Eugene Tolmachev's and the Elmish community's Fable.Elmish (Apache 2.0) — and both upstream projects remain excellent and the right choice for anyone whose use case fits them unmodified. The substantive credit lives in [`NOTICE.md`](NOTICE.md) and under [`README.md` → Standing on shoulders](README.md#in-tree-client--transport-forks), which also lists what the in-tree variants add and drop. Consumer call sites move by search-and-replace per [`docs/migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md`](docs/migrations/73-namespace-rename-to-toolup-remoting-and-toolup-elmish.md).

## Versioning

`0.x.y` while the public surface is unstable. Per the SemVer-on-`0.x` policy: minor bumps may include breaking changes, patch bumps are non-breaking. `1.0.0` is declared once the surface is judged stable.

**A change that rides or advances `<Version>` appends ONE line to [`DRAFT-NOTES.md`](DRAFT-NOTES.md), never to `Directory.Build.props` (Phase 1210).** Every project imports the props, so any edit to them forces the full test lane (`msbuild-import`); a note there put nearly every versioned change on it. The line's grammar, the append-only rule and the merge driver that joins two concurrent appends are in the file's own header; the convention is the [release process](docs/platform/release-process.md#the-changelog-is-rendered-at-release)'s, and the move: [`docs/migrations/1210-draft-note-ledger.md`](docs/migrations/1210-draft-note-ledger.md).

**The bump is checked against the diff, not trusted (Phase 260).** `dotnet run --project Build.fsproj -- VerifySemVerBump` classifies the public surface between the last release tag and this tree — per package, with the same comparer the [approval gate](#public-api-approval-baselines-phase-175) runs — rolls the classes up per **version group**, and FAILS a group whose declared version implies a smaller bump than its own packages moved by. Add `--propose` to report without failing. It never edits the version: the proposal is a floor, and a deliberately larger bump is never wrong. It runs at release from `publish-nuget.yml`, not in `verify.ps1`; the rule it applies is exercised on every commit by the `ToolUp.Platform.Build.Tests` pack. Note what it diffs and why: on a tree that passes the approval gate the working surface and its baselines are IDENTICAL (that gate fails in both directions since Phase 618), so the only diff that still carries information is the one between RELEASES. Rationale, the two policy tables, and the failure modes: [`docs/migrations/260-semver-bump-from-api-diff.md`](docs/migrations/260-semver-bump-from-api-diff.md).

**Release candidates, and the soak gate a major is promoted through (Phase 263).** Release candidates exist ONLY for majors from 1.0 on: a tag `vX.0.0-rc.N` (X ≥ 1) publishes the lockstep packages to nuget.org as the SemVer-2 prerelease `X.0.0-rc.N` — the tree keeps declaring the bare `X.0.0`; the label lives only on the tag, and a consumer that does not opt into prereleases never resolves one. The first candidate is `1.0.0-rc.1`; 0.x releases, minors and patches publish stable directly. `publish-nuget.yml` runs `VerifyReleaseChannel` before minting a credential: it refuses any tag other than `vX.Y.Z` / `vX.0.0-rc.N` (X ≥ 1) and any tag that does not name the tree's `<Version>` (before it, every tag published `<Version>` whatever it said). A major is promoted only from a candidate nuget.org has served for the soak window (14 days by default; `release-channel.json` holds the default and per-release overrides, each with a mandatory reason) with no BREAKING surface change since (additive growth does not block), from a descendant commit, with the Phase 257 scorecard all-green (`dotnet run --project Build.fsproj -- VerifyRcPromotion` before tagging). Rules in `ReleaseChannel.fs`; the operator flow in [`docs/platform/release-process.md`](docs/platform/release-process.md). **The scorecard is rendered on demand, never committed** (Phase 1096): `dotnet run --project Build.fsproj -- V1Readiness` writes `docs/reference/v1-readiness.md`, which is gitignored — the committed render it replaces had gone nine releases stale, because no workflow regenerates it. None does until Phase 925 or Phase 847 gives it a run; render it locally (after `git fetch --tags`) when you need it.

**Version groups: the core is one lockstep line; each companion family has its own.** [`version-groups.json`](version-groups.json) puts every packable project on exactly one group, by project-path glob. The core group (`ToolUp.Platform.*`, `ToolUp.AI.*`, the feature tiers, transport and wire, `ToolUp.Sdk`) is the one `lockstep` line: its version IS `<Version>` in `Directory.Build.props`, the number the release tag names. Each companion family (`audit-sinks`, `storage`, `data-sources`, `auth-providers`, … and the `stripe` / `workflow` / `experiments` lines) has its own version, so a break in one audit sink demands a bump of the audit-sink line, not of the SDK. `GenerateSdkManifest` renders the file into `Directory.Build.props` (each project packs at its group's version) and into the `ToolUp.Sdk` manifest (each package resolves through its group's property); `SdkManifestTests` fails on a packable project in no group, a project in two, a dead glob, a project-level `<Version>` that disagrees with its group, or a core package depending on a companion. A consumer pins `<ToolUpSdkVersion>` and gets the core at that version and every family at the version released beside it; a family's own property (`<ToolUpAuditSinksVersion>`, …) moves that family alone. One `v*` tag per release, named by the core; a release candidate and its soak are the core's. The model and the rulings: [`docs/platform/release-process.md`](docs/platform/release-process.md#version-groups--one-lockstep-core-a-line-per-companion-family); the consumer edit: [`docs/migrations/1196-version-groups.md`](docs/migrations/1196-version-groups.md).

## Repo layout

```
toolup-forge/
├── src/
│   ├── ToolUp.Platform.{Core,Server,Client,Build}/   # core SDK; Server is the umbrella over the six parts below
│   ├── ToolUp.Platform.Server.{Remoting,Infra,Config,Scope,Jobs,Audit}/ # the server tier's part assemblies (sources stay under ToolUp.Platform.Server/)
│   ├── ToolUp.Platform.UI/                           # the standalone UI toolkit (Feliz component library)
│   ├── ToolUp.AI.{Core,Server,Client}/               # AI agent loop + SSE + tools
│   ├── ToolUp.Platform.Transport/                    # lowest tier: HTTP seam, RetryPolicy, retry loop (FSharp.Core only)
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
│   ├── NotificationChannels/{Redis,Email/...,Sms/Twilio,Push/WebPush,WhatsApp/Twilio}/
│   ├── VectorStores/Hnsw/                            # scalable IVectorStore
│   ├── TimeSeriesStores/, Rerankers/, ContainerSchedulers/, TelemetrySinks/,
│   │   FeatureFlagProviders/, RateLimiters/, DataSources/, Webhooks/,
│   │   ContentAuthoring/, Media/, Encryption/, Cloud/, Hosts/  # further companion families
│   ├── Metrics/OpenTelemetry/                        # IMetricsSink companion
│   ├── Secrets/AzureKeyVault/                        # ISecretStore companion
│   ├── Feliz.AgGrid/, Feliz.AgCharts/                # standalone Feliz bindings (AG Grid / AG Charts Community)
│   ├── Feliz.AgGrid.Enterprise/                      # AG Grid Enterprise init shim (opt-in)
│   ├── ToolUp.Platform.Tests/                        # SDK contract test packs: the residue pack, and the sources every Platform pack compiles
│   ├── ToolUp.Platform.Tests.{Remoting,Infra,Config,Scope,Jobs,Audit}/ # one pack per server part, compiling its lists from ToolUp.Platform.Tests/
│   ├── ToolUp.Platform.Tests.PublicApi/              # the public-API approval gate's pack, built over every packable project
│   ├── ToolUp.Platform.Tests.Support/                # the harness every Platform pack references (lane filter, registration guard, entry point)
│   ├── ToolUp.{Forms,Scheduling,Stripe,Tabular,OpenXml,ArtefactSigning,AssetStore,Cli,AIProviders}.Tests/ # per-companion test packs
│   ├── {Webhooks,JobSchedulers,NotificationChannels,AuditSinks,Storage,Secrets,…}/ToolUp.*.Tests/ # a companion family's pack, beside it
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
  **The tier is seven assemblies (Phase 1193).** `ToolUp.Platform.Server` is the umbrella: it holds file management and the composition root (`ServerApp`, the plan, boot, preflight, the composition and module surfaces) and references six parts, each cut along a contiguous run of the tier's compile order and referencing only the parts before it:

  | Part | Holds |
  |---|---|
  | `ToolUp.Platform.Server.Remoting` | the transport: codecs, decoder gates, dispatcher, Giraffe and ASP.NET Core adapters |
  | `ToolUp.Platform.Server.Infra` | the substrate: logging, metrics and RAG seams; event, log, data-object, dataset, entity, result and lineage stores; compute dispatch; model registry types; data sources; secret stores; health checks |
  | `ToolUp.Platform.Server.Config` | config resolution and the env binder, profiles, the config store, migrations, feature flags, the config validators |
  | `ToolUp.Platform.Server.Scope` | auth providers, scope resolution, teams, sessions, permissions and grants, share tokens, service accounts, the security middleware |
  | `ToolUp.Platform.Server.Jobs` | the job store and scheduler, notifications, presence, transactional dispatch, webhooks |
  | `ToolUp.Platform.Server.Audit` | the audit log and replication, artefact signing, classification, consent, erasure, data-subject requests, evidence |

  A consumer references `ToolUp.Platform.Server` and gets every part transitively. The sources all stay under `src/ToolUp.Platform.Server/` and each part's `.fsproj` compiles its run from there, so a file's part is read from the project files, not from its folder. The parts' order is the dependency order: a file may only use files in its own part or an earlier one. Which file sits where, the files that reach across two subsystems, and what the cut costs and saves: [`docs/migrations/1193-platform-server-subsystem-assemblies.md`](docs/migrations/1193-platform-server-subsystem-assemblies.md).

  **A namespace names the part (Phase 1198).** Every file a part compiles declares that part's namespace — `ToolUp.Platform.Infra`, `.Config`, `.Scope`, `.Jobs`, `.Audit` — and a file placed in a part other than the one its name says takes the namespace of the part it is PLACED in, so the map from namespace to assembly is a function and the opens a file carries say which assemblies it reaches. The umbrella has two: `ToolUp.Platform.Compose` for the composition root and lifecycle, `ToolUp.Platform.Files` for its five file-management sources. The Remoting part keeps the remoting library's `ToolUp.Remoting.Server` (and `.Giraffe`, `.AspNetCore`), with its one platform-named module under `ToolUp.Remoting.Server.Http`; `ToolUp.Platform.Remoting` is a module of the client tier and is not used. A sub-namespace a file had keeps its tail under the part (`ToolUp.Platform.Infra.Metrics`, `ToolUp.Platform.Scope.Teams`). **`ToolUp.Platform.Core` stays flat:** its types cross the wire or are shared with the client tier (GP 10), and a Core type only the server uses is still Core, so Core declares `namespace ToolUp.Platform` throughout and no Core file moves. A file in a part sees Core only through an explicit `open ToolUp.Platform` (F# opens no enclosing namespace on its own) and an earlier part only through its namespace; a new server file opens what it uses. The file-by-file map, the consumer codemod (`dev-scripts/codemod-1198-namespaces.ps1`, the Codemod target's `--phase 1198`) and the opens-map evidence: [`docs/migrations/1198-subsystem-namespaces.md`](docs/migrations/1198-subsystem-namespaces.md).
- **`Client`** — Fable + Feliz shell with the in-tree ToolUp.Elmish runtime (under `Client/Elmish/`): MVU, sidebar navigation, the app-shell layout, `AuthUIProvider` delegate registry, `NotificationClient` (SSE), `ToastCentre`, `ProcessedDataContext`. Source ships in the nupkg under `fable/`. The AG Grid / AG Charts bindings are no longer part of this tier — Phase 344 promoted them to the standalone `Feliz.AgGrid` / `Feliz.AgCharts` packages, which the tier depends on (the compat re-exports under the old module names were retired in Phase 815). Nor is the `UIToolkit` component set — Phase 307 promoted it, the icon primitives and the typed prop helpers to `ToolUp.Platform.UI`, which the tier likewise depends on; there every namespace is preserved, so no re-export was needed.
- **`UI`** — the toolkit `ToolUp.Platform.Client` composes and re-exposes: `Toolup.UIToolkit.*` (design tokens, typography, form controls, tabular + KPI views, empty / error / loading state views), `ToolUp.Platform.{Icon,Icons}`, and the `{Svg,Data,Aria}Prop` typed `prop.custom` helpers. Depends on `Platform.Core` only, so a consumer can take the components without the SDK client tier. `Layout.fs` — the shell composition half of the same namespace — deliberately stayed in `Client`.
- **`Build`** — FAKE pipeline targets (`Run` / `Bundle` / `Format` / `Pack` / `ThirdPartyNotices`).

### `ToolUp.AI` — AI assistant companion

Agent loop, SSE streaming, conversation persistence, tool registry, system-prompt composition (platform + team + module layers), `AICompose.withAI`. Built on `IAIProvider` (extension point in `Platform.Core`).

`AISlotFills` (in `ToolUp.AI.Client/Client/AIClientTypes.fs`, Phase 1074) fills the AI companion's three shell slots — the assistant page, the AI settings page, the platform-admin keys page — each a `SlotFill` a deployment can empty or replace:
- `AISlotFills.empty` — no AI page and no side panel
- `AISlotFills.defaults` — the built-in family with SDK defaults
- `AISlotFills.configured branding` — the built-in family with the assistant rebranded

Apps wanting AI compose the companion onto their `ServerApp`: `app |> AICompose.withAI (AIOptions.create factory providerProfile |> AIOptions.withAIConfig …) |> ServerApp.run` (`ToolUp.AI.Server/Server/AICompose.fs`). `AIOptions` holds the AI tier's own settings; the tier is composed when the deployment resolves, so every `ServerApp` builder works on either side of `withAI`.

### `ToolUp.RAG` — retrieval-augmented generation

Chunking, vector store, retrieval pipeline, ingestion + reembedding background services, `RAGPromptBuilder`, `RAGCompose.withRAG`. Built on `IEmbeddingProvider` + `IVectorStore` + `IRetrievalPipeline` extension points (in `Platform.Server`).

Apps wanting RAG compose `app |> RAGCompose.withRAG (RagOptions.create factory providerProfile embedder |> …) |> ServerApp.run` (`ToolUp.RAG.Server/Server/RAGCompose.fs`); RAG composes the AI tier itself (`RagOptions.AI`), so such a deployment does not also call `withAI`. Per-deployment tuning via `RagOptions` `withTopK` / `withMinScore` / `withMergeStrategy` / `withSnippetCharLimit` / `withOriginFilter` / `withGroundingMode` / `withIngestionConcurrency` / `withIngestionQueueCapacity` / `withTelemetry`.

### `ToolUp.KnowledgeBase` — document KB

User-facing canonical consumer of `ToolUp.RAG`: document upload, multi-format extraction (PDF / PPTX / DOCX / XLSX / CSV), ingestion-status surfacing, narrative-commit, notes, AI-context. Three integration contracts external KB replacements must honour:
1. NarrativeCommit handler — call `Toolup.NarrativeCommit.install` with a submit handler
2. `IIngestionStatusObserver` — wired explicitly with `RagOptions.withIngestionObserver`
3. Notification-key contract — `"KnowledgeBase.IngestionStatus"` is the published wire format the AI side panel subscribes to.

### `ToolUp.Forms` — schema-driven forms

`FormSchema` / `Submission` / `WorkflowDefinition` / `FormError` types, validation engine, workflow engine, `FormsCompose.withForms` (a companion taking `FormsOptions`). Supports publishable surveys (`FormVisibility.Publishable`) via the SDK's `IShareTokenStore` substrate (HMAC-SHA256-signed tokens, blob-backed claim store, per-token rate-limit partition).

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

Selected by `ServerConfig.PeerSubstrate = EnabledPeerSubstrate` (default `NoPeerSubstrate`). Compose with `PeerCompose.withPeer` (a companion taking `PeerOptions`), which registers the peer DI singletons from already-present substrate (`IBlobStorage` / `ISecretStore` / optionally `IJobScheduler` / `IAuditLog`) and mounts `/peer/v1/*` when the deployment resolves. When `NoPeerSubstrate`, the companion contributes nothing — zero cost when unused (GP 13). See [`src/InterPlatform/README.md`](src/InterPlatform/README.md) + [`TECHNICAL_GUIDE.md`](src/InterPlatform/TECHNICAL_GUIDE.md).

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

### AI-queryability is opt-in, and OFF unless the module says otherwise (Phase 36.C)

A module's data is **not** reachable by the built-in cross-module AI tool family (`_platform.ai.*`)
until the module declares that it should be:

```fsharp skip=fragment
ServerModule.create "MoodJournal"
|> ServerModule.withAIExposure ModuleAIExposure.Queryable
|> ServerModule.withGuardedApi moodJournalApi
```

Undeclared means `ModuleAIExposure.NotQueryable`: `list_accessible_modules` still names the module
but reports `queryable: false`, `list_data_types` omits its data types, and the four reach tools
refuse with `UnqueryableModule`. **This is the one place the SDK deliberately inverts GP 11** — a
user installing a third-party module must not have its data become AI-readable as a side effect of
installing it, so the safer default wins over the byte-for-byte one. Adopting the SDK version that
carries this phase therefore requires one line per module you want reachable; see
[`docs/migrations/per-module-ai-queryability.md`](docs/migrations/per-module-ai-queryability.md).

The declaration **narrows only**, exactly like `ServerModule.withGrantPolicy`: a composition root may
revoke a module author's opt-in, and a call that would widen an explicit `NotQueryable` fails at
compose time. It is a separate axis from the grant model — `GrantPolicy` governs whether a given
subject's grant is live, this governs whether the module is on the AI surface for anyone — and both
are applied, in that order, at every tool site. RBAC is unchanged and still applies on top: opting in
widens no authority, it only stops the AI family refusing by default.

```fsharp skip=fragment
// SharedTypes.fs
module HelloWorld.SharedTypes
type HelloApi = { DoThing: string -> Async<string> }

// Server.fs
module HelloWorld.Server
let routine (input: string) : string = sprintf "did: %s" input

// ClientModel.fs
module HelloWorld.ClientModel
open ToolUp.Elmish
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
        Icon = Icon.ofUrl "/svg/chart.svg"
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
- **Ships with its test pack from the first commit**: `src/<Family>/ToolUp.<Family>.Tests/ToolUp.<Family>.Tests.fsproj` beside the family (or the family's existing pack), a gate-manifest row in `GateManifest.fs`, and the SDK contract pack the companion implements bound against it. The pack references the family, the substrate and nothing else the contract does not need, and it hosts the contract by **source-linking** it from `src/ToolUp.Platform.Tests/Contracts/` (`<Compile Include="…" Link="Contracts\…" />`), never by copying it. A copy is compared with its source by the Build pack and fails it. A live backend's arm is env-gated, Pending without its variable, never Failed, and the row declares the variable. A companion's cases do not go in `ToolUp.Platform.Tests`; which cases do is in [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md#a-companions-tests-live-in-its-own-pack). The families moved so far, and the files still waiting: [`docs/migrations/1197-companion-test-packs.md`](docs/migrations/1197-companion-test-packs.md).

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
  **Register both in the `ToolUp.Platform.Server.Infra` part's `.fsproj`** (Phase 1193), beside
  `IDataObjectStore.fs` / `DataObjectStore.fs`: a persistence substrate is Infra. Only when the store's
  interface or default needs a later subsystem does it go there instead — the earliest part that
  holds everything it uses (a store that audits its writes goes in `Audit`, one that schedules
  jobs in `Jobs`). The compile order enforces it: a file placed in a part before one it uses does
  not compile. Its `Contribution` and any `ServerApp` builder stay in the umbrella
  (`ToolUp.Platform.Server.fsproj`), which is where the composition root lives.
- **Crypto-addressed / server-only-compute types stay in Server** — e.g. SHA-256-addressed envelope
  types use `System.Security.Cryptography`, which is not Fable-compilable, and belong in
  `Platform.Server/Server/` even though "types" usually go to Core.
- **Opt-in wiring (GP 13) — one `FeatureMode` field, one `ConfigKey`, one `Contribution` (Phase 877).**
  A store is on or off, and that fact has one type: `Xxx: FeatureMode` (default `FeatureMode.Disabled`)
  on the concern record it belongs to — `Stores` for a persistence substrate, the top level for a
  subsystem composed over the stores — with its default in that concern's `defaults` in
  `Shared/Config/ServerConfigDefaults.fs`. Its environment key is a `ConfigKey` in `ConfigKeys.keys`
  (the binder in `Server/Config/ServerConfigFromEnv.fs` reads it with `enabledDisabled` and takes
  parser, default and direction from the entry; nothing else is written for the key). Its wiring is
  a `Contribution` under the store's `ComponentId` (`Contribution.service owner register`, admitted
  when the field is `Enabled`), not a `registerXxxStore` helper called from `SDK.Server.fs`. Never
  mint an `XxxStoreMode` union for an on/off store: the forty that existed were one boolean shape
  with forty parsers, and Phase 877 deleted them. A mode that carries a payload, has a third case, or
  chooses between substrates keeps its own type. A field added to a concern record retypes only that
  record's ctor: the Core baseline regenerates, and no downstream baseline moves unless its own
  public record embeds the concern (see [Public-API approval baselines](#public-api-approval-baselines-phase-175)).

**Vendor dependencies never enter `ToolUp.Platform.*` (GP 1).** `ToolUp.Tabular` carries
`DocumentFormat.OpenXml` for its XLSX leg, and its `.fsproj` says so: the vendor dep stays there. Do
NOT add a `ProjectReference` from any `ToolUp.Platform.*` project to `ToolUp.Tabular` — even to call
the BCL-only CSV path — because it drags OpenXml into the SDK core's dependency graph. When SDK-core
code needs Tabular-style behaviour, cut a dependency-free interface seam in `Platform.Server`, ship a
BCL-only default over the coarse schema, and let a Tabular-backed implementation be composed over the
seam (precedent: `IMappingDryRunValidator`). The ban is on `ToolUp.Platform.*` only: a companion that
already carries vendor dependencies of its own may reference `ToolUp.Tabular` directly (the KnowledgeBase
server reads CSV through `ToolUp.Tabular.Csv.parseRecords`).

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

As of 2026-05, the major Fable-ecosystem libraries this SDK consumes or embeds (Feliz, Fable.SimpleJson, the in-tree ToolUp.Elmish runtime, the in-tree ToolUp.Remoting transport) have not been nullness-annotated, and there's no published timeline for that to land.

**Rule:** do not set `<Nullable>enable</Nullable>` on any project that compiles via Fable or whose Fable-compiled output consumes Feliz / Fable.SimpleJson / the in-tree ToolUp.Elmish / ToolUp.Remoting runtimes. Leave the property unset (which inherits F#'s default of `disable`), or explicitly set `<Nullable>disable</Nullable>` for projects sitting in a solution whose `Directory.Build.props` enables nullness by default. Server-only projects with no Fable involvement may enable nullness per the standard F# 10 default.

This rule retires when the upstream packages ship nullness annotations.

## Type erasure boundaries

Type erasure (`box`/`unbox`) is contained in these sanctioned boundaries inside forge:
1. **`ClientModule.register`** — erases per-module `'Model`/`'Msg` for the heterogeneous module list.
2. **`DataTypeDisplay.RenderSummary` — CLOSED by Phase 817.** Every data-producing module used to box its summary record in its server-side `DataType.Process` and unbox it in the client-side `RenderSummary` callback (a symmetric same-module-known-type cast on both ends). `ProcessedFileEntry.Summary` now carries the summary as a `ProcessedData` envelope and `DataTypeDisplay.typed` decodes it, so nothing is boxed; `Info: obj option` and the `obj list` callback were removed outright (operator decision 2026-09-22, overriding the deprecation window), which is what put `FileManagementApi` — the last platform API record on the reflection decode path — onto the proved one. The number is kept so the cross-references to "boundary 2" below stay true.
3. **Narrative `Component` block renderers** (Phase 87) — the `NarrativeElement.Component of name * props` case carries a stringly-typed `Map<string,string>` rather than a typed payload, so a deployment can register custom block renderers (`props -> XmlNode` in `NarrativeLayout`, bridged to the SDK's `string -> Map -> string option` resolver in `NarrativeHtml.RenderOptions`) without forking the `NarrativeElement` DU. The "erasure" is the stringly-typed prop bag at the registry seam; renderers are pure and resolve by name, with an unregistered name degrading to a safe placeholder.
4. **`GridApiRegistry`** (`src/Feliz.AgGrid/AgGrid.fs`) — boxed `IGridApi` handles in a keyed registry; symmetric unbox at the same-module read site.
5. **`EntityStore` registrations** (`Server/EntityStore.fs`) — symmetric box/unbox of `EntityRegistration<'T>` in the name-keyed registration dictionary.
6. **`AuthUIProvider` handler dispatch** (`AuthUIProvider.fs`) — the auth-UI delegate registry boxes the provider config for the registered handler.
7. **`IDataMigrator.Migrate`** (Phase 10a, `Shared/MigrationTypes.fs`) — a data-migration step takes and returns `obj` because only the module that owns the data type knows its V_n and V_(n+1) record shapes; the SDK never does. Same shape as boundary 2: the runner boxes the stored content on the way in, the module's migrator casts to its own known type, and the runner accepts a `byte[]` or a `string` back and REFUSES anything else with a typed failure rather than writing bytes it cannot account for.
8. **`RemoteStream.subscribe`** (Phase 69c.D, `Client/Remoting/Streaming.fs`) — the Fable proxy returns the cold `RemoteStream<'T>` it builds for an `'arg -> IAsyncEnumerable<'T>` field through that field's static type (`IAsyncEnumerable<'T>` has no Fable runtime shape), and `RemoteStream.subscribe` takes it back. Symmetric, same-module, and guarded: a value that is not a proxy-built stream is refused by name rather than read.
9. **`ErasedCodec`** (`Shared/Remoting/Codecs.fs`) — a codec's legs are erased (`RegisteredDecoder`, `RegisteredJsonDecoder`, `RegisteredJsonEncoder`) so one table holds many types. Symmetric: the only way in is `Codecs.register<'T>`, which boxes a decoder's result and unboxes an encoder's input at `'T`, and the only way out is a lookup by that same `'T`'s identity.
10. **`GeneratedDispatchRegistry`** (`Server/Remoting/SourceGenDispatch.fs`) — a generated dispatch table is held boxed, keyed by `typeof<'TImpl>`. Symmetric: the generator's `register<'TImpl>` puts in the table for that record, and the dispatcher reads it back through `tryGet<'impl>` at the same `'impl`.
11. **`GeneratedProxies`** (`Client/Remoting/Proxy.fs`) — a generated client proxy builder is held as `RemoteBuilderOptions -> obj`, keyed by the API record's name. Symmetric: a generated module's `registerAll` writes the builder under the key of the record it builds, and `Api.makeProxy` unboxes at that record's type.
12. **The job dispatcher's boxed statuses and the `LongRunning` bridges** (`Server/Remoting/Jobs.fs`, `Server/Remoting/LongRunning.fs`) — `InMemoryJobDispatcher` stores one job's `JobStatus<'T>` as `obj` because the store is single-typed and `IJobDispatcher` is generic per call. Symmetric: `Enqueue<'T>` boxes, and `GetStatus<'T>` unboxes through the `JobHandle<'T>` that `Enqueue<'T>` returned; the terminal test is captured at `Enqueue`, where `'T` is known. The companion route knows `'T` only as a `Type`, so each `LongRunning` bridge is a generic method closed with `MakeGenericMethod resultType` over the type the classification read from the same field.

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

**Do not run `dotnet test` against the solution or these projects.** They are Expecto console runners (`<OutputType>Exe</OutputType>` + a `Program.fs` entry point), so `dotnet test` exits 0 having run nothing — a silent false-green. Each runner exits non-zero on failure; the real non-breakage gate is a full `dotnet build ToolUp.Forge.sln` (catches cross-companion breakage that per-project builds miss) plus every Expecto suite `VerifyAll` runs with 0 failures (`GateManifest.rows` in `GateManifest.fs` is the one list — every pack, leg and CI-only check is a row there, and `BuildConfig.TestPacks` is derived from the `Local` pack rows; this file deliberately does not restate the count, having gone stale twice doing so. Every test project (`*.Tests.fsproj`, or a `.Tests.` segment in the file name) in the solution, or on disk under `src/`, `tests/` or `tools/`, is a pack row there or a named exclusion with its reason in `GateManifest.exclusions` beside it, and the `GateReconcile` row of `VerifyAll` — in every lane — is red otherwise, so a test project can no longer sit in the solution, build on every push and be run by nothing. **The Platform pack is a family of rows (Phase 1194)**: `Platform.Remoting`, `.Infra`, `.Config`, `.Scope`, `.Jobs` and `.Audit` each hold the lists whose test files reach that server part and no later one, and reference only its closure; `Platform` is the residue (the lists that reach the umbrella or a companion over it); `Platform.PublicApi` is the approval gate's own pack. The sources stay under `src/ToolUp.Platform.Tests/`, and which pack compiles a file is read from the project files. A new list goes to the latest part its file reaches and registers in that pack's `Program.fs`; the rules are in [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md#the-platform-family-one-pack-per-server-part) and the map in [`docs/migrations/1194-platform-pack-partition.md`](docs/migrations/1194-platform-pack-partition.md). `AIProviders` is env-gated — clean on a fresh checkout, asserts live when an API-key env var is set), plus the Fable-tier `AI.Client.Tests` runner (Node's built-in `node:test` against the Fable-transpiled output; runs via the `node --import ./register-loader.mjs --test output/Program.js` invocation shown above). **Since Phase 803 that Fable runner is a LEG of `VerifyAll`** — a `Body` row of the gate manifest, run after the packs (outside the `pure` lane), so `verify.ps1` cannot go green on a tree whose client tier does not transpile. It reports as `PASS — Fable` in the same summary block, which is why CI's `EXPECTED_PACKS` floor, rendered from the manifest, counts legs as well as packs. **Since Phase 904 a second leg sits beside it, `Samples`** (`VerifySamples`): it builds every `*.fsproj` under `samples/` one at a time, asserts the count built against an independent enumeration of the directory (a deliberately skipped sample is named, with its reason, in `SampleGate.excluded`), and — in the full lane only, about ten minutes for the six Fable samples — transpiles the Fable ones too. The fast lane still builds every sample. **Since Phase 776 a third leg, `Analyzers`** (`VerifyAnalyzers`), runs the `ToolUp.Remoting.Analyzers` TUA rules over every tracked `src/**/*.fs` and reconciles the sites with the committed inventory `src/ToolUp.Remoting.Analyzers/analyzer-inventory.json` (a new site, a count the tree no longer needs, a stale exclusion or a hand-edited `docs/platform/analyzer-rules.md` is red); about six seconds, so it runs in `fast` as well as `full`.

**Shortcut for the full Expecto matrix**: `dotnet run --project Build.fsproj -- VerifyAll` runs every `Local` pack row of the gate manifest sequentially, then its legs, with a per-row summary at the end — the canonical "run everything" invocation. Each per-pack `dotnet run --project` shown above is still the right shape for iteration on one pack.

**Gate lanes — `TOOLUP_TEST_LANE` (Phase 762).** The full gate is tens of minutes — every pack plus the legs, the `Samples` leg (Phase 904) among them — and this file states no duration: read [`gate-timings.json`](gate-timings.json), the committed fold of the recorded gate runs, which names its window and the machines it measured (per-lane median and p90, per-row median). `dotnet run --project Build.fsproj -- GateTimings --record` re-folds it from `.git/fuaran-gate/verify-runs.jsonl`; the close-out of a series of landings runs it and commits the record by name, and a commit carrying only the record is gate-inert. The record and its rules: [`docs/migrations/1209-gate-timings-record.md`](docs/migrations/1209-gate-timings-record.md). A dispatched worker has to spend it twice: once to learn whether its own change is green, once on the folded tree. Set `TOOLUP_TEST_LANE` to spend minutes on the first and the full gate only on the second.

```powershell
pwsh ./verify.ps1                                     # full (unset) — the pre-phase gate, unchanged
$env:TOOLUP_TEST_LANE='fast'; pwsh ./verify.ps1       # ~6 min — everything but the measured slow set
$env:TOOLUP_TEST_LANE='pure'; pwsh ./verify.ps1       # ~seconds — the declared no-filesystem packs
Remove-Item Env:TOOLUP_TEST_LANE                      # back to full
```

The measurement the lanes were designed from — 2026-09-12, before the `Samples` leg existed, so it is history, not the gate's current duration (Debug, sequenced, after a clean solution build): **full 967 s** over 23 packs, of which the Platform pack is **884.8 s (91.5 %)** and all 22 others together are 82.4 s. Inside Platform, 6 registered lists hold **144 of 10,325 cases (1.4 %) and 616.0 s of the 716.3 s of case time (86 %)** — and one case, `HnswFidelity.latency smoke`, is **539.3 s on its own, 56 % of the whole gate**. So `fast` is a *list*-level decision inside that one pack (10,181 cases, ~6 min) and `pure` is a *pack*-level one (6 packs, 10.5 s); every other pack runs unchanged in `fast`.

Four things about it are deliberate, and each is the reason it is not something simpler:

- **It is an environment variable, not a `verify.ps1` switch.** `verify.ps1` is a hard-denied path on the forge roadmap side (`RM-FOOTPRINT-FORBIDDEN`), so its recorded gate-script hash must not move. The lane travels inside the recorded gate *command* instead, which keeps the evidence self-describing without touching the gate file.
- **A ship may only cite `full`, and unset *is* `full`** — a machine that has never heard of the variable runs exactly the gate it ran before, byte for byte, and prints nothing extra. A non-full lane prints one line naming itself and how much it left out, so a log cannot be mistaken for a full run. An unrecognised value falls back to `full` with a warning, never to a reduced lane: a typo may cost minutes, but it must never report a green over a suite the operator did not choose.
- **The slow set is measured, not judged.** A list is slow-marked when it costs ≥ 10 s *and* its slowest single case is ≥ 2 s — slow because it holds long-running cases, not because it holds many cheap ones. That second clause keeps `Phase 175 — Public-API approval baseline` (13.6 s over 182 cases, slowest 0.3 s) in the fast lane, where a cheap and frequently-broken gate belongs. Re-derive the split with `--junit-summary` and group `//testcase/@time` by list; the recipe is in `src/ToolUp.Platform.Tests/Program.fs` beside the declaration.
- **`pure` membership is opt-in at both levels**, so it can under-claim but never over-claim: a pack joins it only by passing the filesystem/process/socket/env grep recorded in `Build.fs`, and a list added to the Platform pack is outside `pure` until someone audits it. The Platform pack declares no pure lists at all (it is the filesystem-heavy pack by construction), so running its dll under `pure` runs the Phase 722 registration guard and nothing else.

The lane filter is applied **after** the Phase 722 registration guard, not before: the guard is about what the pack *registers* (all of it, in every lane), the lane only about what *runs*. Filtering first would make the guard fail under `fast` naming the slow lists as unregistered. Selection is by physical identity, never by label — the same choice the guard makes, for the same reason.

**How `VerifyAll` reaches a pack changed in Phase 731, and the change is worth knowing before you write another harness.** The target now **builds once** (the single `*.sln`/`*.slnx` in the working directory, else each pack project) and then invokes each pack's **built test dll directly** — `dotnet <dll>`, with the assembly asked of MSBuild via `-getProperty:TargetPath` rather than guessed from a `bin/<config>/<tfm>/` convention. It does *not* shell `dotnet run --project` per pack any more, because that launch path intermittently **hangs before the suite starts** (~0% CPU, no output, indistinguishable from a slow pack). The per-pack `PASS` / `FAIL` summary, the target's exit semantics, and the `TOOLUP_TEST_ARGS` pass-through are unchanged — CI parses that summary, so its shape is a contract. A consequence worth having: the Public-API approval gate's "build the solution first" precondition is now satisfied by the canonical invocation itself, not only by the CI job that wraps it. Full rationale: [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md).

**Shortcut for the Fable tier**: `dotnet run --project Build.fsproj -- VerifyFable` (Phase 614) runs the client-tier harness end to end — `dotnet tool restore`, `npm ci`, `dotnet fable -o output --noCache`, then `node --test` over the transpiled output. It asserts the TAP **pass / fail counts**, not just the exit status, because `node --test` exits 0 when it matched no test file at all; a harness that stopped emitting cases is otherwise indistinguishable from a green run. The floor is a lower bound (currently 100 against 258 shipped cases), so adding a case never needs an edit here. The same recipe is the body of the `Fable` leg `VerifyAll` runs (Phase 803) — the target is the shortcut for iterating on that tier alone; the leg is what makes the canonical gate cover it.

**Shortcut for the template content**: `dotnet run --project Build.fsproj -- VerifyTemplates` compiles the `dotnet new` scaffolds under `templates/`. It packs the package closure the templates reference (`ToolUp.Platform.{Core,Client,Server,Transport}` and the six `ToolUp.Platform.Server.*` parts + `ToolUp.AI.Wire` + `ToolUp.Graph.{InMemory,Core}`, plus the `ToolUp.Feliz.*` bindings — `TemplateGatePackages.templates` in `TemplateGatePackages.fs` is the authoritative list) at a throwaway `0.0.0-templategate` version into a scratch feed, then builds each template against it.

The throwaway version is load-bearing, not cosmetic: packing at `$(ToolUpSdkVersion)` would be a same-version repack, and NuGet would resolve from the already-extracted global-packages entry — so the gate would compile the templates against **whatever was packed last** rather than current source. The target wipes those cache entries first. NU1603 is escalated to an error for the same reason: it fires when a gate-versioned package declares a `ToolUp.*` dependency that was not packed at the gate version, and NuGet then silently falls back to an older feed version. **Adding an SDK→SDK dependency therefore fails this gate by name** until the package is added to `TemplateGatePackages.templates` in `TemplateGatePackages.fs`.

`templates/safer/` and `templates/platformsdk-solution/` are deliberately **not** covered — they are standalone solutions carrying their own `nuget.config` (a `../local-nuget-feed` path resolved relative to the consumer's instantiated location) and, in `safer`'s case, a literal `TOOLUP_SDK_VERSION` placeholder substituted at instantiation. Neither is buildable in-repo without rewriting what makes it a template; gating them needs an instantiate-then-build harness.

**Shortcut for the packaged-module template** (Phase 587): `dotnet run --project Build.fsproj -- VerifyPackagedModuleTemplate`. `templates/platformsdk-module-packaged/` is in the standalone class the paragraph above describes, so this is the instantiate-then-build harness that class needs, written for the one template that has one. It packs the same closure **plus `ToolUp.Platform.Build`** at the same `0.0.0-templategate` version, `dotnet new`s the template into a scratch directory **under the system temp dir** — outside the repo, because `nuget.config` files MERGE up the tree and forge's sources would otherwise paper over a broken feed declaration in the template — and then runs the scaffold's own `Pack`. One command covers scaffold → build → conformance → pack, because the generated target chain puts both conformance layers ahead of `Pack`: a green `Pack` cannot have skipped the module-seam contract pack (`Test`) or the packaging-layout laws (`VerifyPackagedModule`).

Three things it checks that a build alone would not:

- **The vendored contract pack has not drifted.** The template's test project copies the laws region of `src/ToolUp.Platform.Tests/Contracts/ModuleContract.fs`, because that project is `IsPackable=false` and copying is the documented adoption route for every SDK contract pack. A copy drifts silently by construction, and this one ships to consumers as "born conformant", so it is compared against its source (up to the `// ── a conforming reference module` boundary — the self-test half stays SDK-side).
- **Every `ToolUp.*` resolved at the gate version.** The equivalent of `VerifyTemplates`' NU1603 escalation, read off the scaffold's `project.assets.json` rather than passed as a warning flag — the scaffold runs its own build driver, so there is nowhere to inject one. A stray version means `TemplateGatePackages.packagedModule` is missing a package and the scaffold compiled against a MIX of current source and a released SDK.
- **The produced nupkg's `fable/` layout**, which is the half a pre-`Pack` declaration check structurally cannot prove: the shadow project, the four client sources and the icon asset are present, and `Server.fs` is not.

Adding an SDK→SDK dependency reachable from the packaged-module template therefore fails this gate by name, exactly as it does the other one. All three checks were demonstrated red before landing — a drifted vendored copy, a client file dropped from the shadow project, and a client id pinned away from the server's — each naming its own law.

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
| `verify-all` | 57 rows, every `Local` row of the full lane: Platform, Platform.Remoting, Platform.Infra, Platform.Config, Platform.Scope, Platform.Jobs, Platform.Audit, Platform.PublicApi, Forms, Scheduling, AIProviders, Stripe, Build, RemotingAnalyzers, Cli, Voice, AICookbooks, Algorithms, AlgorithmProviders, CloudParity, ArtefactSigning, ReportingDocx, ReportingXlsx, DataSources, OpenXml, OpenXmlSpreadsheet, AssetStore, SvgRasterizerSkia, Reporting, Offline, CompanionsIsolation, AIEvaluation, TriageCalibration, Tabular, Classification, DataSubject, ReportingHtmlPdf, Webhooks, JobSchedulers, NotificationChannels, AuditSinks, Storage, Secrets, EmbeddingProviders, EntityStores, TimeSeriesStores, FeatureFlagProviders, Media, GateReconcile, Fable, Samples, Analyzers, ScopeLiterals, ServiceConfigFold, FuaranCoResolution, FuaranCoBuild, MergeDrivers | Tree, Git, Docker, Network, Browser, Node, 106 env var(s) | yes |
| `cloud-parity` | CloudParity | Tree, Docker, 8 env var(s) | yes |
| `fable-tier` | Fable | Tree, Node | yes |
| `doc-snippets` | DocSnippets | Tree | yes |
| `source-citations` | SourceCitations, UploadResults | Tree | yes |
| `templates` | Templates, PackagedModuleTemplate, DataPlatformTemplate | Tree, Network | yes |
| `browser-smoke` | BrowserSmoke | Tree, Node, Browser | yes |
| `perf-budget` | PerfBudget | Tree, Node | runs; not yet a required check |
<!-- gate-manifest:ci-table:end -->

**A new gate is a row.** The places that used to restate the list of what the gate runs — `BuildConfig.TestPacks`, the pure-lane set, the `VerifyLeg.register` sites, CI's `EXPECTED_PACKS`, this table, `VerifyGate`'s gated targets and stale-holder bound, the scrubbed-environment allowlist each pack is launched under, and the trailer `VerifyAll` prints naming the CI-only rows it did not run — are renders of `GateManifest.rows`. A row names its project, body or script, its lanes, where it runs (`Local` under `VerifyAll`; `Ci <job>`; `Release`), what it reads (`Tree`, `Git`, `Node`, `Browser`, `Docker`, `Network`, `Env <name>` — the `pure` lane is the pack rows with `Reads = []`, held by the grep `PureLaneTests` re-runs), its `Budget` (a seed, replaced by the median `gate-timings.json` measures for the row; `GateReconcile` is red when the record and the rows disagree in membership) and its `Floor` (`AtLeast` / `Exactly` a case count, a `HighWater` file, or `FromRegistry proofs.json`). Every leg returns a `Verdict` — `Pass cases`, `Fail reason`, or `Pending reason` for an arm its named input gates — and `Aggregate` prints the summary block from them: a `PENDING` line is counted in neither `PASS` nor `FAIL`. `dotnet run -- Gate --run <row>` runs one row on its own, which is how the CI-only jobs invoke theirs.

**What the gate reads is a generated record too: [`gate-reads.json`](gate-reads.json) (Phase 1208).** The landing tooling treats the .NET default (sources, tests, proofs, build and project files, the formatter configuration, `verify.ps1`) plus the record's `reads` as the gate's inputs, and a commit on `main` touching nothing in that union is crossed without a re-gate. `Gate --render` writes the file from `stepReads`, `rowReads` and `notRead` in `GateManifest.fs`; `Gate --check` and the `GateReconcile` row are red when the committed file is not the render, and `GateReadsCensusTests` is red for a file the suite's sources or project files name that neither `reads` nor `notRead` covers. Never edit the JSON; declare the read on the row that opens it. What belongs where: [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md#what-the-gate-reads-gate-readsjson-and-what-belongs-in-reads-versus-notread).

Everything marked "yes" runs on every push to `main` and every PR against it. `dco` is PR-only because direct-to-main is this repo's normal integration path, so signed-off discipline there relies on the local commit template. `perf-budget` likewise runs on every push and PR but is not in the branch-protection rule yet — the state the seven test gates were in before 2026-09-12; see [`CONTRIBUTING.md`](CONTRIBUTING.md#required-checks-on-main-maintainer-setup).

**Which job transpiles which client tier — the answer is not the one the job names suggest (Phase 345).** Read the reference graph, not the job title:

| Client tier | Transpiled in CI by | Via |
|---|---|---|
| `ToolUp.AI.Client`, `ToolUp.KnowledgeBase.Client` | `fable-tier` | `VerifyFable` → `src/ToolUp.AI.Client.Tests`, which references both |
| `ToolUp.Platform.Client` (+ `Platform.Core`) | `fable-tier` **and** `browser-smoke` | transitively through `ToolUp.AI.Client`; and directly by the browser-smoke fixture |
| **`ToolUp.Offline.Client`** | **`browser-smoke` only** | `VerifyBrowserSmoke` → `tests/BrowserSmoke/fixture` |

`ToolUp.Offline.Client` is the one with a single point of coverage: exactly two projects in the tree reference it — the fixture and `samples/MinimalClient` — and **no CI job compiles the sample**. The `cd samples/MinimalClient && dotnet fable -o output` step under [Build verification](#build-verification) is the phase-boundary check a developer runs; it has never been a gate. So a `<ProjectReference>` dropped from the fixture would not go red — it would silently narrow the repo's Fable coverage while eight browser scenarios kept passing. `VerifyBrowserSmoke` therefore asserts its own compile set before it installs anything, and its failure names this table.

**`VerifyFable`'s reach was deliberately left where it is** rather than widened to the offline tier: widening it would compile that tier a second time, in a second job, for a `node:test` pack holding no cases over it — wall-clock with no added signal. The sanctioned CI gate for offline-client transpilation is `browser-smoke`, and if that job is ever removed or made non-gating, the transpile is re-homed in the same commit.

Both test gates were local-only until Phase 614. `fable-tier` was demonstrated red on a scratch branch against a deliberately broken `node:test` case before landing, because an unproven CI gate is precisely the failure mode it exists to prevent. `verify-all` shipped dispatch-only because the .NET suite turned out **not to be green on Linux**; Phase 617 fixed the four blockers and promoted it. Neither was ever made green by excluding packs or swallowing exit codes — that trades a gate for a tick.

Both jobs are written so they **cannot pass vacuously**. `verify-all` parses the `VerifyAll summary:` block and fails unless at least `EXPECTED_PACKS` packs report `PASS` (the floor is **rendered from the gate manifest**: `EXPECTED_PACKS` is `Gate.ciFloor`, the count of rows whose `Where` carries `Ci "verify-all"` — a leg counts the same way as a pack, since it is one more `PASS` line in the block — and `dotnet run -- Gate --check` refuses a drift between the two while `Gate --render` writes it, so a new row moves the floor without a hand count) — the target legitimately exits 0 with an empty `BuildConfig.TestPacks`, which would otherwise read as green. `VerifyFable` fails unless the TAP summary exists and clears its case floor, because `node --test` exits 0 when it matched no test file at all.

#### Two traps the Linux gate exposed (Phase 617)

Running the suite somewhere other than a Windows dev box found four blockers, and **two of them were defects in shipped SDK code rather than test-environment noise**. Both are shapes that any future pack or backend can walk into again, so they are written down here rather than only in the phase file:

- **Blob names are `/`-delimited on `IBlobStorage`, always.** `LocalFileStorage.List` was returning `Path.GetRelativePath` output, i.e. the OS separator. The break was silent, because `Download` accepts either separator on Windows — what failed was callers that strip a known prefix to recover an id (`name.Replace("memberships/", "")`), which simply no-opped and returned a mangled id. That is how `TeamStore.GetTeamMembers` reported `UserId = "memberships\alice"`, `IsLastOwner` never matched, and **the last Owner of a team could be removed on Windows**. Now normalised, and pinned by the `IBlobStorage` contract pack so every backend is held to the same shape. If you add a backend or a `List` caller, that contract test is the one to read.
- **Expecto deadlocks when parallel tests write to the console, so every pack now runs sequenced by default.** Expecto replaces `Console.Out`/`Console.Error` with a synchronized writer; a parallel test writing through it can take that writer's monitor, descend into `ANSIOutputWriter.flushInner` → the ProgressIndicator, and block on the real console stream lock while siblings hold the writer's. This is not confined to the pack that surfaced it — `ToolUp.Cli.Tests` hung 6 runs in 6 and burned a 60-minute CI timeout, and `ToolUp.Platform.Tests` deadlocks identically, having only been getting away with it because a 2-core runner rarely wins the race (still 2-in-6 at `--parallel-workers 2`). No CLI flag avoids it (`--no-spinner` still hangs, `--colours 0` is worse) and Expecto 11.1.0 is worse than the pinned 10.2.3. Each `Program.fs` therefore passes `CLIArguments.Sequenced` as a default — overridable with `--parallel`, and effectively free: the 5,214-case Platform pack runs in **4m28s** sequenced. Rationale, measurements and the rule for new packs: [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md).

The other two were environmental but worth knowing: the `IAssetStore` pack needs a SkiaSharp native for the running RID (`ToolUp.Platform.Tests` references `SkiaSharp.NativeAssets.Linux.NoDependencies`; the shipped `SkiaSharpDerivativeRenderer` now probes at construction and names the missing package rather than failing at first render), and the `IContainerScheduler` real-backend leg needs `alpine:latest` in the local image cache — the Docker HTTP API does not pull on demand the way `docker run` does, so the job pre-pulls it.

`dotnet run --project Build.fsproj -- Pack` walks every public-surface SDK fsproj (filtered against `IsPackable=false`) and packs each individually into a local feed (default `../local-nuget-feed/`). ~9 minutes for a clean cold pack of ~43 packages; subsequent packs are incremental. Point a consumer's `nuget.config` at the same folder to test unreleased changes end-to-end.

The `Publish` FAKE target (`dotnet run -- Publish`) packs every public-surface SDK fsproj into a per-run `./artifacts/` directory and pushes each `.nupkg` to **nuget.org** (the default source since the 2026-08-19 cutover; the old `ToolUp-Forge` GitHub Packages feed is frozen and no longer pushed to). CI workflow is [`.github/workflows/publish-nuget.yml`](.github/workflows/publish-nuget.yml) — triggers on tag `v*.*.*` (or manual `workflow_dispatch` to heal a failed tag run — a dispatch packs main's current `<Version>`, tagged or not, so never dispatch ahead of a tag); authentication is nuget.org **Trusted Publishing** (the workflow's login step exchanges its OIDC token for a one-hour key — no stored credential or secret; the login names the policy owner's public nuget.org profile). The push is idempotent (`--skip-duplicate`) so re-runs after a transient failure skip versions already published. Local manual publishes set `NUGET_API_KEY` (a classic push-scoped api.nuget.org key — the exception path; CI never uses one) and optionally override `TOOLUP_PUBLISH_SOURCE`. Symbol packages (`.snupkg`) are pushed automatically to nuget.org's symbol server (`dotnet nuget push` detects the sibling file) and also remain in `./artifacts/` for local inspection.

### Phase citations in source comments

New code cites no phase in a source comment: the phase's recorded outcome is the record of why a thing exists, and a number in a comment goes stale the first time the thing is reshaped. An existing phase-history comment is removed from any file a change touches (the tidy rule). No sweep is scheduled. The two ledgers of the Platform test pack, `Program.fs` and `ToolUp.Platform.Tests.fsproj`, were stripped of their per-entry comments (Phase 1101) and now list the files and nothing else; a phase a stripped comment cited that its test file's own header did not carry was moved into that header.

### Fast iteration

`dotnet build ToolUp.Forge.sln` compiles every project in the solution. This file states no count and no duration, having gone stale doing so: [`build-census.json`](build-census.json) records each project's build seconds, lines, fan-in and fan-out, and the clean build's wall clock, for the machine and tree that measured it (regenerate with `dotnet run --project Build.fsproj -- Census`; see [`docs/platform/testing-conventions.md`](docs/platform/testing-conventions.md#the-build-census-what-each-project-costs-to-build)). A one-shot Fable compile is similarly expensive. Use:

```bash
# Targeted fsproj build
dotnet build src/ToolUp.Forms.Server/ToolUp.Forms.Server.fsproj

# Watch loop for one fsproj
dotnet watch build --project src/ToolUp.Platform.Server/
```

Full-sln verification is for end-of-task / pre-commit, not per-edit.

**Compiler levers: what an incremental build recompiles.** `Directory.Build.props` declares `ProduceReferenceAssembly`
on. It is also the SDK default, and the declaration keeps it on. A dependant compiles against the reference assembly,
so an edit that leaves a project's public surface unchanged recompiles only that project. An internal edit to
`ToolUp.Platform.Server` rebuilds 2 projects instead of 129. A public change still rebuilds every dependant, and the
approval gate still renders the implementation DLLs in `bin/`. **One gap: F#'s reference assembly does not change when
a `[<Literal>]` is added or its value changes.** After touching a literal, build its dependants clean
(`--no-incremental`), or an incremental build keeps the old value. **The same gap, measured by Phase 1193: the
reference assembly's identity also stays put when an `InternalsVisibleTo` is added, or when a type its surface names
moves to another assembly.** The copy into `obj/<config>/<tfm>/ref/` is skipped, and a dependant goes on compiling
against the stale copy. The symptoms are an internal that is "not accessible" or a nonsense type error in a project
whose source did not change. Build clean once after such a change. `ParallelCompilation` is deliberately unset: measured
here it slowed both bottleneck projects, and the compiler marks it test-only (FS0075). `ToolUp.Platform.Build.Tests`
(`CompilerLeversTests.fs`) holds both decisions. The tables and the re-measure rule are in
[`docs/migrations/1195-compiler-levers.md`](docs/migrations/1195-compiler-levers.md).

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

**Provisioning a git worktree for verification.** Create it at a SHORT path (e.g. `C:\wt\<name>`)
— a deep worktree path hits Windows MAX_PATH and `fsc` then emits no dll and no error. The Platform
test pack's two externally-resolved conformance corpora (the model-execution corpus,
`TOOLUP_MODEL_EXECUTION_CORPUS`; the federation-seam specification home,
`TOOLUP_FEDERATION_SPEC_DIR`) need **no env var in a worktree**: with the variable unset, resolution
anchors its upward directory search at the repository's MAIN working tree (via `git rev-parse
--git-common-dir`; see `src/ToolUp.Platform.Tests/Support/CorpusAnchor.fs`), so a worktree of a
checkout that can resolve the corpora resolves the same ones. The search also refuses to look inside
any OTHER working tree of this repository — two worktrees sharing a parent directory used to pick up
each other's transiently-present corpus checkouts, which made the pack non-deterministic between
runs (observed 2026-08-27). An absent corpus is still a loud, named, environmental failure — never a
skip; set the env var only when the main checkout itself cannot see a corpus.

**Expecto `--filter` joins the test path with `.`, not `/` — and a filter that matches nothing
reports SUCCESS.** A slash-shaped filter (the shape test names suggest) selects zero tests, prints
`0 tests run … Success!` and exits 0: a vacuous green. Read the *count*, never the exit code; if a
filtered run reports suspiciously few tests, `--list-tests` first, or make the filter fail once
(break a test it should select) before believing a green.

**`MemoizedChart` must NOT be `private`.** `AgChart.chart` is a `static member inline` on an `[<Erase>]` type; Fable inlines the method body at every call site, which means call sites import `MemoizedChart` directly. If it's `private`, Fable doesn't export it → runtime `SyntaxError: does not provide an export named 'MemoizedChart'`. Same rule for any module-level value referenced from an `inline` method on an erased type.

## Public-API approval baselines (Phase 175)

The approval gate renders each packable `ToolUp.*` assembly's public surface and diffs it against
`api-baselines/<assembly>.approved.txt`. It is its own pack, `ToolUp.Platform.Tests.PublicApi`
(Phase 1194), whose project references are every packable project, built and not referenced: so
`dotnet run --project src/ToolUp.Platform.Tests.PublicApi/ToolUp.Platform.Tests.PublicApi.fsproj`
builds exactly the surface it renders and runs the approval lists alone, with no whole-solution build.
The rules that matter when a phase touches public surface:

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
- **Every generated artefact is a row of [`projections.json`](projections.json), and one switch
  regenerates it.** A row names the artefact, what renders it, the STEM that approves a regeneration,
  the check that holds it and how to regenerate it; `ProjectionsTests` holds the registry to the tree
  and `.fantomasignore` is its render (`dotnet run --project Build.fsproj -- Projections --render`).
  The baselines are stem `api`. `TOOLUP_APPROVE` is the one switch (`src/ToolUp.Platform.Tests/Contracts/Approval.fs`),
  and `pwsh ./dev-scripts/regen.ps1 <stem>` runs a stem's regeneration from its rows (`-List` names
  the stems). The old switches (`TOOLUP_APPROVE_API`, `TOOLUP_REGEN_*`, …) are read until 0.43.0 and
  print the spelling that replaces them.
- **Scope the regen by NAME.** `TOOLUP_APPROVE=api:ToolUp.Platform.Core,api:ToolUp.Platform.Server`
  (or `regen.ps1 api -Name ToolUp.Platform.Core,ToolUp.Platform.Server`) rewrites exactly those
  baselines and leaves every other one untouched and unconsulted; a name the run does not discover
  fails the gate rather than regenerating nothing. Prefer it always — it is the surgical filter,
  applied before anything reaches disk.
  **The whole stem (`TOOLUP_APPROVE=api`) rewrites EVERY built baseline**, folding in unrelated
  additive drift and EOL-only churn, which is what the named form exists to avoid. If you do use it,
  run the approval pack with `dotnet run` (it builds every packable project first), then `git restore`
  every baseline except your surgical targets and stage those by name. Either way, verify the real
  diff WITHOUT the switch — approve mode passes trivially, so its green proves nothing. Never run an
  unscoped regen in a shared working tree (it rewrites files concurrent sessions have in flight), and
  never apply baseline hunks with `git apply --3way` there (it writes the shared index as a side effect).
- **A worktree regen is only valid against the HEAD it is pinned to.** If surface lands between your
  pin and your apply, copying the regenerated file back silently deletes the landed lines — re-pin
  against the current HEAD copy, apply only your own hunks, and diff-verify zero foreign removals.
- **An UNBUILT tree is one failure, not fifty-two (Phase 731).** The gate renders from built DLLs,
  so running the approval pack's dll before its references were built used to fail every unbuilt
  assembly's case independently — a wall of red that reads like a
  catastrophic surface break and has misdirected a session. It now reports one
  `the solution is built` failure naming the count, a sample and the remedy, and the per-assembly
  cases defer to it. If you see that message, nothing has drifted and nothing needs regenerating:
  build, then re-run. `VerifyAll` builds first, and `dotnet run` builds the pack's references, so
  this only bites a dll launched on its own.
- **An untracked sibling packable project reddens the gate** (`discoverPackable` walks disk, not the
  git index): a concurrent session's uncommitted `src/**/*.fsproj` with no committed baseline fails
  the new-package arm. It is not your break — confirm with `git status --short` and note it; do not
  author the sibling's baseline.
- **A failing baseline names an assembly, not a cause** — confirm attribution with `git log -- <source
  files>` before writing it into a commit message.
- **Design corollary (ruled 2026-10-23, landed by Phase 877):** a new opt-in is a `FeatureMode` key on
  the concern record it belongs to (`ServerConfig.Stores`, `Observability`, `SecurityPosture`,
  `MultiInstance`, `RiskAcknowledgements`, or the top level for a subsystem) plus its `ConfigKey` in
  the registry and, where it registers anything, a `Contribution`. It widens that one concern
  record's ctor, so the Core baseline regenerates by name and nothing downstream does unless its own
  public record embeds the concern. The older escape — a NEW options record + NEW builder entry
  points so an existing shared record's ctor never widens — is still right for a feature that needs
  more than a toggle (a payload, several knobs), with existing builders delegating under a
  behaviour-preserving default (GP 11); it is not a reason to keep a toggle out of its concern record.
  Since Phase 618 any new surface needs its assemblies' baselines regenerated (additions are a named,
  surgical regen rather than a silent pass), and the existing types' baselines stay untouched.
- **Marking a member `[<Obsolete>]` moves the baseline, and the notice is gated (Phase 258).** The
  renderer emits a SEPARATE `…  (obsolete)` marker line beside the member's unchanged token, so a
  deprecation scores as an addition (regenerate, commit in the same PR) and never as a removal — the
  in-place `… [obsolete]` shape is forbidden precisely because the lost token reads as breaking. The
  same pack also fails a public `[<Obsolete>]` whose message omits a **replacement** or a **removal
  target**; the failure says "NOTHING IS BROKEN", because it is about a sentence, not a surface.
  Window, message format, and the removal-only-at-a-major rule:
  [`docs/platform/deprecation-policy.md`](docs/platform/deprecation-policy.md).

## F# style + idioms

- **Runtime**: .NET 10 (`net10.0`).
- **Language**: F# 10 (the SDK pin in `global.json` is `10.0.203`). All workspace siblings are on the same baseline; assume F# 10 features are available without further qualification.
- **Formatter**: Fantomas — pre-commit step. Run `dotnet fantomas <file>` BEFORE `dotnet build`.

### A builder's name says whether it sets or adds (Phases 1050, 1052)

A `with*` builder **sets** (a later call, or a later whole-record setter such as `withConfig` / `withExtensions`, replaces what it wrote); an `add*` builder **appends** and nothing later replaces it. A new builder follows the names: `ServerApp.addServiceConfig` appends DI registrations and survives a later `withExtensions`; `ServerApp.addContribution` appends a `Contribution` (a companion's registrations under its own `ComponentId`, via `Contribution.service owner register`). Existing appending `with*` builders keep their names — renaming them would break every consumer for no behaviour change — and say **Adds** in their doc comment; every builder says **Sets** or **Adds** there. A DI registration is never appended by folding `ComposeExtensions.ServiceConfig` by hand: the one fold is `ServiceConfig.append`, and `dotnet run --project Build.fsproj -- VerifyServiceConfigFold` (a `VerifyAll` leg) refuses a second copy under `src/`. A test that builds a provider from a composed app reads `ServerApp.serviceConfig app` — the consumer's `ServiceConfig` followed by every contribution, which is what `run` applies — never `app.Extensions.ServiceConfig` alone.

### Fantomas pitfall: indexer ambiguity

**Never write `map[key] arg1 arg2` on a single line.** F# treats `map[key]` (no space) as an indexer and `map [key]` (with space) as a list-application. Fantomas can insert a space in the no-space form, breaking compilation. Extract the indexer read to its own line:

```fsharp skip=fragment
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

- **ToolUp.Remoting APIs**: handled automatically by the transport (the in-tree ToolUp.Remoting sources bundled inside `ToolUp.Platform.{Core,Client,Server}`).
- **SSE / non-Remoting JSON**: must use `ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()` (returns a `System.Text.Json.JsonSerializerOptions` with the full F# converter set registered — Option / DU / tuple / record / CLIMutable / list / Map / Set / decimal / DateTime / DateOnly / TimeOnly / byte[] / DataSet / DataTable / etc.). Construct once at module level, then call `JsonSerializer.Serialize(value, options)` / `JsonSerializer.Deserialize<'T>(json, options)`. The options instance is mutated to set `PropertyNameCaseInsensitive = true` and `Encoder = UnsafeRelaxedJsonEscaping` — match the Fable.SimpleJson wire shape and absorb camelCase inputs without ceremony. Do NOT use plain `JsonSerializerOptions()` without `FableConverters.addTo` — F# DUs / Option / records all break on the wire. The legacy `Fable.Remoting.Json.FableJsonConverter` (Newtonsoft) was retired in the STJ migration — `Newtonsoft.Json` is no longer a forge dependency.
- **`unit -> Async<T>` API functions**: work because body normalisation is folded into the dispatcher itself (shipped 0.4.0). The standalone `RemotingBodyNormalizationMiddleware` that 0.3.x relied on was retired — `dotnet build` is the gate, not a middleware presence check.
- **Consumer dependency contract**: server projects consuming `ToolUp.Platform.Server` need no extra `ToolUp.Remoting.*` PackageReferences — the transport, the JSON converter set, and the Giraffe / ASP.NET Core adapters all arrive transitively via `ToolUp.Platform.Server`. `System.Text.Json` ships in the BCL.
- **Additive fields on persisted records: the STJ path yields `null`, the SimpleJson path THROWS — handle both.** `FableConverters` initialises absent reference-type fields to `null`, so a blob persisted before a field was added comes back with `null` where a `list`/`Map` should be — and a null F# `list` NREs on every list op (`[]` is the `Empty` singleton, NOT null; only `option`'s `None` is null). Coerce at the store read path AND in the pure consumer (`if isNull (box x.Field) then { x with Field = [] }`; `isNull (box …)` is the Fable-safe check), and test by deserialising a JSON literal that OMITS the field. Meanwhile Fable.SimpleJson's `Json.parseAs<'T>` (browser-localStorage records) is the opposite: a missing field **throws** — and a `try/catch reset` fallback then silently discards ALL the user's persisted state, not just the new field. Backfill absent fields into the raw JSON before `parseAs` (array `[]` for list/Set, object `{}` for Map); a post-parse coercion cannot work because parse throws first.

### AG Charts axes + animation

- `AgChart.axes` uses a direction-keyed object (`"x"` / `"y"`), not an array (AG Charts v13+ regression).
- `AgChart.chart` uses a memoised wrapper (`MemoizedChart`) to preserve animations on Elmish re-renders.
- Don't add `prop.key` to the chart `Html.div` wrapper when underlying data changes — forces React remount, destroys the chart instance, prevents transition animations.

### AG Grid / AG Charts binding reference (Phase 12e)

The bindings target **ag-grid 35.3.0** / **ag-charts 13.3.0** (pinned in the samples' `package.json`; the binding-version contract follows those pins). Comprehensive typed surface — events, filters, theming, CSV/Excel export, the full series catalogue — reached at ~80% of the published public API.

- **Community** bindings: the standalone `Feliz.AgGrid` + `Feliz.AgCharts` packages — [`src/Feliz.AgGrid/AgGrid.fs`](src/Feliz.AgGrid/AgGrid.fs) + [`AgChart.fs`](src/Feliz.AgCharts/AgChart.fs). The compat re-exports under the pre-Phase-344 module names (`ToolUp.Platform.AgGrid` / `.AgChart`) were retired in Phase 815; every call site opens `Feliz.AgGrid` / `Feliz.AgCharts`. Cookbook: [`src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md`](src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md) — the canonical authoring reference (constraints → shortest-possible → recipes → anti-patterns), single-Read for an AI agent.
- **Enterprise** file split: [`src/Feliz.AgGrid.Enterprise/AgGridEnterpriseTypes.fs`](src/Feliz.AgGrid.Enterprise/AgGridEnterpriseTypes.fs) (grid: Set Filter / Multi Filter / Excel Export / Master-Detail / Status Bar / Sidebar / charts integration / SSRM / custom aggs) + [`AgChartEnterpriseTypes.fs`](src/Feliz.AgGrid.Enterprise/AgChartEnterpriseTypes.fs) (Sankey / Sunburst / Treemap / Candlestick / Ohlc / Heatmap / Waterfall / Box-plot / Range series + Sparkline). Cookbook: [`src/Feliz.AgGrid.Enterprise/COOKBOOK.md`](src/Feliz.AgGrid.Enterprise/COOKBOOK.md).
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
