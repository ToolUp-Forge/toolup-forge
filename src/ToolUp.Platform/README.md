# ToolUp.Platform

A modular application framework for building analytical platforms in F# on ASP.NET Core + Giraffe (server), Fable + Feliz with an in-tree Elmish runtime (client), in-tree ToolUp.Remoting (type-safe wire — Fable.Remoting fork; `namespace Fable.Remoting.*` preserved), and any-cloud blob / event storage.

Originally bootstrapped from the [SAFE Stack](https://safe-stack.github.io/) template; the Saturn DSL and SAFE.Client/Server metapackages were retired in favour of direct Giraffe + Fable + Elmish + Feliz references, with the SAFE `Api.makeProxy<T>` / `Api.make` / `ApiCall<_,_>` / `RemoteData<_>` surface re-homed inside `ToolUp.Platform` itself.

## Design Philosophy

ToolUp.Platform exists to solve a specific problem: building platforms where **multiple independent analytical modules** coexist within a single application, without coupling to each other or to the platform infrastructure.

### Product purpose

ToolUp provides simple, smart tools for ad and media agencies to quickly gain insights from their data — especially for smaller agencies without big data resources. ToolUp.Platform is the infrastructure that makes this possible: it handles everything an analytical module doesn't want to think about, so module authors can focus entirely on domain logic.

### Architectural principles

The framework enforces a strict separation of concerns:

- **The SDK owns all infrastructure.** Routing, authentication, data persistence, storage scoping, team management, the application shell, the Elmish program lifecycle, the build pipeline — these are platform concerns, not module concerns. A module author should never need to think about how the sidebar works, how files are stored, or how teams are managed.

- **Modules own all domain logic.** The SDK has zero knowledge of any specific module, its data types, or its business rules. The SDK cannot name a module, reference a module type, or make assumptions about what modules do.

- **Modules are self-contained.** Each module is a single F# project containing everything it needs: shared types, server logic, client model, and client view. A module can be added to or removed from a platform implementation by changing two lines in the entry point files.

- **Modules do not reference each other.** If two modules need to share domain types, those types live in a shared types project that both reference. If one module needs to act on another module's output, it does so by reading persisted data or consuming events through the platform's event infrastructure — never by importing the other module's code.

- **Platform implementations are thin.** A complete platform application consists of a `Client.fs` that lists its modules and a `Server.fs` that lists its modules. Everything else comes from the SDK. Adding a new module to an implementation should require no understanding of the SDK internals.

### Elmish MVU discipline

- **Update functions must be pure** — no mutable state, no side effects. All side effects (API calls, storage, global state sync) must flow through `Cmd`.
- **No new side effects.** Refactors and new features must not introduce mutable global state or imperative side effects in module code. Where legacy side effects exist, prefer eliminating them over working around them.

  **Documented module-level mutable exceptions.** A small number of SDK-internal mutables exist for initialisation-time or cache state that is set once and read many times. These are the *only* sanctioned module-level mutables; new ones require an entry in this list and a justification.

  | Mutable | File:line | Role | Why this is acceptable |
  |---|---|---|---|
  | `UserSession.currentMode` | `Client/UserSession.fs:36` | Platform mode set once by `SDK.Client.run` via `UserSession.configure` before any API call | Initialisation-time state. Set at boot, read per request. Threading it through every `Remoting`-call site adds friction without eliminating the read-after-configure contract. |
  | `FeatureFlags.warnedKeys` | `Client/FeatureFlags.fs` (`HashSet` memo) | Dedupes the "undeclared feature flag key" `console.warn` so a read-loop doesn't spam the console | Diagnostics-only cache. Clearing or duplicating the warning is harmless; the mutable saves console-log noise. |
  | `AIToolRegistry.tools` | `src/ToolUp.AI/Server/AIToolRegistry.fs:86` | Agent-tool registration list populated during startup, stable for process lifetime | Initialisation-time state. A DI-scoped registry for a list that never changes after boot is over-engineering. |
  | `LocalEmbeddingProvider` IDF state | `src/EmbeddingProviders/Local/LocalEmbeddingProvider.fs` | TF-IDF vocabulary that evolves across embed calls | Dev-only provider; the file header documents this as a Phase 9c rule-4 exception. Production providers must be stateless between calls. |
  | `SDK.Client.shellDispatch` | `Client/SDK.Client.fs` | Shell's Elmish `dispatch` captured at boot via `Cmd.ofEffect` so `ClientModuleContext.OnTeamSwitched` (invoked from inside a module's `update`) can dispatch the shell-level `TeamSwitched` message | Initialisation-time state. Set once on first init effect, read by callbacks installed on contexts. The alternative (threading shell dispatch through every module's `update`) violates module independence — modules' updates only see their own typed `Msg`. |
  | `NarrativeCommit.installed` | `Client/NarrativeCommit.fs:35` | KB narrative-commit handler broker; `install()` once at app boot from `Client.fs` if a Knowledge Base is wired in. The renderer (which lives in core, not in KB) reads this to decide whether to show the Save-to-KB button | Initialisation-time companion-bridge mutable. The renderer cannot import KB types (KB is a downstream companion); the broker hides the cross-package dependency the same way `AgGridEnterprise.register` and `UserSession.configure` do. |
  | `RegisteredModules.entries` | `Client/RegisteredModules.fs:28` | Per-tab snapshot of the modules registered with the shell. `publish()` once from `Client.init` with the resolved `ErasedModule` list (post-RBAC / module-filter); read by AI-tool-dispatch companions (e.g. a navigation tool validating `(moduleId, pageRoute)` pairs) | Initialisation-time state. Re-publish supported when team membership changes the accessible-modules set; never on a hot path. Companion lookups are stable for the tab's lifetime. |
  | `AgGrid.gridModulesRegistered` | `Client/UI/AgGrid.fs:65` | Set-once flag preventing the Community-edition fallback when the AgGridEnterprise companion has registered Enterprise modules first | Initialisation-time guard. `AgGridEnterprise.register` flips it before `Client.run`; subsequent reads from `ensureGridModulesRegistered ()` short-circuit. The alternative (registering Community modules unconditionally then having Enterprise re-register) breaks AG Grid's module-registry invariants. |
  | `AgChart.chartsModulesRegistered` | `Client/UI/AgChart.fs:19` | Same shape as `AgGrid.gridModulesRegistered` for AG Charts Community/Enterprise selection | Initialisation-time guard. Same precedent. |
  | `FastPathBridge.resolver` | `src/ToolUp.AI/Client/FastPathBridge.fs:43` | A downstream AI-tool-dispatch companion registers a Tier 1 fast-path resolver here at module load; `ToolUp.AI`'s chat-send path calls it before sending the request to the server. ToolUp.AI cannot import the downstream companion directly (cyclic compile order) | Initialisation-time companion-bridge mutable. Resolves the compile-order constraint. Same pattern as `NarrativeCommit.installed`. |
  | `FileManagement.pendingPostSaveHooks` | `Server/FileManagement.fs:188` | Companions (notably `RAGCompose`) register post-save hooks via `configurePostSaveHooks` BEFORE `compose` runs. `compose` drains this into the immutable `FileManagementRuntime` it builds + registers as a DI singleton; afterwards the list is no longer consulted | Initialisation-time companion-bridge mutable. The compose pipeline can't see RAG's hooks via DI (RAG's compose runs before the SDK's), so a small queue is needed at the SDK boundary. Same precedent as `FastPathBridge.resolver`. The other three former FileManagement mutables (`postSaveHooksLogger` / `quotaResolverConfig` / `usageLogConfig`) were folded into the runtime record (2026-05-06 design pass — recommendation 2). |
  | `FileManagement.storeEvictionMinutes` | `Server/FileManagement.fs:492` | TTL knob read by the in-process eviction timer every 10 minutes. `compose` calls `configureEvictionMinutes` once with the resolved `ServerConfig.EphemeralStoreEvictionMinutes`; the next tick picks it up | Initialisation-time config knob, read on a background timer that lives outside any DI scope. Threading a runtime record into a static `System.Threading.Timer` would require dismantling the timer; the value is set-once at boot. |
  | `UserSession.currentBridge` | `Client/UserSession.fs:219` | Auth-bridge holder set once by the auth-UI companion (`OidcClient` / `ClerkUI`) at module load; read by Remoting headers and SSE establishment to attach the current bearer token | Initialisation-time companion-bridge mutable, twin of `currentMode`. Same precedent — set at boot, read per request, threading via DI breaks the at-any-call-site Remoting helper shape. |
  | `NotificationClient` SSE singleton (`connection`, `nextHandlerId`, `handlers`) | `Client/NotificationClient.fs:58-60` | Per-tab single `EventSource` that fans envelopes to multiple subscribers. The connection is opened once on first `subscribe`; handler IDs and the handler list are mutated in-tab to support `subscribe` / `unsubscribe` returning a dispose thunk | Per-tab singleton mutable state. The browser tab is the natural lifetime; subscribers come and go as views mount/unmount. The alternative (one `EventSource` per subscriber) would multiply long-lived SSE connections per request scope. |
  | `AgChart.ChartPalette.fills`/`strokes`/`accentColor`/`markerFill`/`markerStroke`/`fontFamily` | `Client/UI/AgChart.fs:32-37` | Theme palette read at chart-construction time. `ClientConfig` chart-theme overrides flip these at boot; module views read them when building chart options | Initialisation-time theme tokens. Apps configure the palette once during `Client.run`'s setup phase; chart code reads it per-render. Threading a theme record through every `AgChart.chart` call site would balloon the API surface. |

  None of these are on an Elmish `update` path or a hot request path. Anything beyond this list is a regression and should be refactored or added here with explicit justification.
- **Module independence in the MVU sense.** Modules declare what they are (`Definition`), what they need (`NeedsData`), what they provide (`ProvidesProcessedData`, `DataTypes`), and how they behave (`Init`, `Update`, `View`). The shell and SDK handle all wiring. Modules should never reach into SDK internals or other modules.
- If state needs to be shared across modules, the shell should extract and distribute it after each update cycle, not have modules mutate globals directly.

## Architecture

```
ToolUp.Platform              Pure infrastructure. Zero domain knowledge.
    |
ToolUp-SharedTypes        Cross-module domain types and utilities.
    |                     (Optional — only needed when modules share domain concepts.)
    |
Individual Modules        Self-contained. Reference SDK + optionally SharedTypes.
    |                     Never reference each other.
    |
ToolupApp-Server          Thin composition root. Lists server modules.
ToolupApp-Client          Thin composition root. Lists client modules.
```

### Module convention (4 files per module)

Every module follows a strict four-file structure:

| File | Responsibility | Compiled by |
|------|---------------|-------------|
| `SharedTypes.fs` | API contracts, DTOs, route constants, validation | Both server and Fable |
| `Server.fs` | Route handlers, data access, file processors | Server only |
| `ClientModel.fs` | Elmish Model, Msg, init, update | Fable only |
| `ClientView.fs` | Feliz view functions + `register()` | Fable only |

Each module also provides:
- `.fsproj` — lists Shared + Server as `<Compile>`, client files as `<None>` (visible in Solution Explorer for navigation, compiled by the Client project via props)
- `.Client.props` — MSBuild props file that injects its Fable-compilable files into the consuming client project

**Modules stay single-fsproj — they are NOT subject to the SDK companion cross-tier (Core/Server/Client) split.** Phase 11.B's split applies to SDK companions (`ToolUp.AI`, `ToolUp.RAG`, `ToolUp.KnowledgeBase`, `ToolUp.Forms`, `ToolUp.Scheduling`, `src/AIProviders/`, `src/EmbeddingProviders/`, `src/AuditSinks/`, `src/NotificationChannels/`, etc.) because they are reusable infrastructure shipped as publishable NuGet packages — DLL boundaries are part of their public contract. Modules are App-tier domain code (commercial deployment-specific implementations), not SDK; they don't get NuGet-packaged. The single-fsproj + `.Client.props` source-injection pattern is the deliberate, preserved convention. Do not split a module fsproj into Core/Server/Client.

### Type erasure

Modules define their own strongly-typed Elmish models and messages. The SDK provides `ClientModule<'Model, 'Msg>` for type-safe construction and `ClientModule.register` to erase the types into `ErasedModule` for composition into a heterogeneous list. `box`/`unbox` is contained within three sanctioned boundaries:

1. **`ClientModule.register`** — erases per-module `'Model` / `'Msg` for the heterogeneous module list.
2. **Companion-side AI tool registries** — e.g. a controllable-field registry, where the cast is the same module's known type at registration time and the same module's known type at decode time.
3. **`DataTypeDisplay.RenderSummary`** — every data-producing module necessarily `box`es its summary record server-side (in its `Server.fs`'s `DataType.Process` callback, where the SDK can't know the per-module summary type) and `unbox`es client-side in the module's `RenderSummary: obj list -> ReactElement` callback. The cast is symmetric — same module's known type on both ends — and confined to the registered callbacks.

Module code outside these three boundaries never sees type erasure.

### Modules vs pages

A **module** is one Elmish MVU — one `Model`, one `Msg`, one `init`, one `update`. A **page** is a sidebar-visible entry rendered against that MVU. A module can expose one or many pages, all sharing the same state.

Single-page modules — the default — register a `View: 'Model -> ('Msg -> unit) -> ReactElement * ReactElement` (control pane, output pane). The shell wraps the tuple in `SplitPanel(l, r)` and renders it. No change required for existing modules.

Multi-page modules opt in with `ClientModule.withPages`, declaring one view per page keyed by `PageConfig.Route`. Each page view returns a `PageContent` value directly — picking its own layout shape:

```fsharp
type PageContent =
    | SplitPanel of left: ReactElement * right: ReactElement   // narrow left + wide right (legacy shape)
    | Stacked of sections: ReactElement list                   // top-down flow (selection + summary + preview)
    | FullWidth of content: ReactElement                       // single full-width pane (reports, single visualisations)
    | Dashboard of areas: (string * ReactElement) list         // responsive tile grid
    | Custom of ReactElement                                   // escape hatch — shell renders verbatim, no gutter
```

The shell emits one sidebar entry per `PageConfig` for multi-page modules (composite Id `"{moduleId}{pageRoute}"` — the route's leading `/` is the separator) and routes page navigation without re-initialising the MVU. Same-module page clicks only update `ActivePageRoute`; cross-module clicks init the new module as usual. `ModuleStates` remains keyed by module Id — all pages share the same state record, so a selection made on one page is immediately visible to every other page of the same module.

`Custom` is a last resort — prefer `Stacked` or `Dashboard` for anything that can live within the shell's gutter conventions.

### Server composition root

The server `Server.fs` is a thin composition root. It builds one `ServerModule` record per module and assembles them into an `ServerApp` pipeline:

```fsharp
let skuAnalysisModule =
    ServerModule.create "SkuAnalysis"
    |> ServerModule.withGuardedApi skuAnalysisApi
    |> ServerModule.withDataTypes [ salesDataType ]
    |> ServerModule.withConfig skuAnalysisConfigSchema

ServerApp.empty
|> ServerApp.withConfig config
|> ServerApp.withAuth authProvider
|> ServerApp.withLogger logger
|> ServerApp.withStorage blobStorage
|> ServerApp.withNotifications notificationChannel
|> ServerApp.addModules [ skuAnalysisModule; (* ... *) ]
|> ServerApp.run
```

`ServerModule` carries a module's Name (the RBAC key used by `makePermissionGuardedApi`), its `Handlers`, any registered `DataType`s, `VectorisationHandler`s, and an optional `ModuleConfigSchema`. `ServerApp` then aggregates those plus config, auth provider, logger, blob storage, and notification channel — `ServerApp.run` internally calls `SDK.Server.compose` with the flattened lists.

Two companion records layer on top without duplicating the core wiring:
- `AIServerApp` — wraps `ServerApp`, adds an `AIProviderFactory`, `AIConfigStore`, `AITools`, and optional `ModuleAIContexts`. `AIServerApp.run` calls `composeWithAI`. See [`src/ToolUp.AI/README.md`](../ToolUp.AI/README.md).
- `RAGServerApp` — wraps `AIServerApp`, adds an `IEmbeddingProvider`. `RAGServerApp.run` calls `composeWithRAG` and re-uses the AI wiring underneath. See [`src/ToolUp.RAG/README.md`](../ToolUp.RAG/README.md).

Apps without AI use `ServerApp.run` directly; with AI only, `AIServerApp.run`; with RAG, `RAGServerApp.run`. Each outer record has a `mapBase` / `mapAI` helper so you can build the inner layer inline.

### Data type registration

Each module that handles file data declares a `DataType` record (detection + processing) in its `Server.fs` and a `DataTypeDisplay` record (summary rendering) in its `ClientView.fs`. The `DataTypeInfo` record (`{ Id; DisplayName }`) is declared once in `SharedTypes.fs` and referenced by both.

### Inter-module communication

Modules communicate exclusively through the platform's event infrastructure. A module emits a `ModuleEvent` — a JSON-serialised envelope with source module, event type, and payload. Other modules query the event store by type or source. The platform persists events and makes them available to any module that asks. No module needs to know which other modules exist.

### Platform modes

`PlatformMode` controls authentication, data scoping, and persistence across the entire stack. Set it in both `ServerConfig.Mode` and `ClientConfig.Mode`.

| Mode | Auth required | Data scoped to | Persistent | Use case |
|------|---------------|----------------|------------|----------|
| `Anonymous` | No | Session (per-tab) | No | Dev, demos, public tools |
| `AuthenticatedEphemeral` | Yes | User | No | Trial accounts, compliance-sensitive analysis |
| `Individual` | Yes | User | Yes | Single-user paid accounts |
| `Team` | Yes | Active team | Yes | Multi-user organisations, one team per user (no switcher UI) |
| `MultiTeam` | Yes | Active team | Yes | Users belong to many teams and switch between them in-session |

`Team` and `MultiTeam` share the server-side data model and storage layout; they differ only in client UX and deployment intent. `MultiTeam` activates a header team-switcher (visible when the user has 2+ memberships) and a shell-level `TeamSwitched` reset path that swaps every module's state to the new team's data without re-auth — see the [Technical Guide](technical-guide/02-multi-tenancy-and-access.md#team-switching-reset-flow).

**Server side** (`ServerApp.run` → `SDK.Server.compose`):
1. Registers the appropriate `IStorageScopeResolver` based on mode
2. For `Team` mode, also registers `TeamStore` (persists team metadata to `_platform` blob container)
3. Each request resolves a `StorageScope` (`{ ScopeId; Container; Persist }`) via the scope resolver
4. `SessionFileStore` uses `scope.Persist` to decide whether to persist files to `IBlobStorage` or keep them in-memory only
5. `AccessContext` (userId, teamId, mode, permissions) is resolved per-request via DI

#### Production deployment knobs

Three `ServerConfig` fields exist because Saturn used to provide them implicitly; the post-SAFE composition pipeline makes them explicit so deployments opt in:

| Field | Default | Purpose | Reference env var |
|-------|---------|---------|-------------------|
| `RequireHttps` | `false` | Adds `app.UseHttpsRedirection()` ahead of scope resolution. Set in production. | `TOOLUP_REQUIRE_HTTPS` |
| `TrustForwardedHeaders` | `true` (Phase 16d) | Adds `app.UseForwardedHeaders(...)` honouring `X-Forwarded-Proto` / `X-Forwarded-For` from any peer. Default-on since Phase 16d — containerised / serverless deploys are almost always behind a TLS-terminating ingress and need this on. Set `TOOLUP_TRUST_FORWARDED_HEADERS=0` (or `=false` / `=no` / `=off`) on a direct-bind dev shell with no proxy hop. Unrecognised values crash startup. | `TOOLUP_TRUST_FORWARDED_HEADERS` |
| `StaticPathBehaviour` | `Warn` | Behaviour when `PublicPath` does not exist at startup. `Warn` (dev — Vite serves assets), `RequireExist` (production — fail loudly), `SkipSilent` (pure API deployments). | `TOOLUP_STATIC_PATH_BEHAVIOUR` (`warn|require|skip`) |
| `SlowRequestThreshold` | `TimeSpan.FromSeconds 1.0` | `RequestTimingMiddleware` logs `Warn` for any request whose handler takes longer than this. Operational signal only — not an audit event. | `TOOLUP_SLOW_REQUEST_MS` |
| `DefaultTeamStorageQuotaBytes` | `None` | Deployment-wide upper bound on `SessionFileStore` bytes per scope. `Some limit` rejects uploads that would push the scope over the limit (no audit event, no file persisted). | `TOOLUP_DEFAULT_STORAGE_QUOTA_BYTES` |
| `RateLimit` | `None` | Opt-in fixed-window per-scope rate limit (`PermitLimit` / `WindowSeconds` / `QueueLimit`). Partitioned by team / user / IP; `429 + Retry-After` on breach; `/health`, `/ready`, `/api/notifications` excluded. | `TOOLUP_RATE_LIMIT_PERMITS`, `TOOLUP_RATE_LIMIT_WINDOW_SECONDS`, `TOOLUP_RATE_LIMIT_QUEUE` |

See [`TECHNICAL_GUIDE.md` — Production deployment knobs](technical-guide/01-architecture-and-composition.md#production-deployment-knobs) for middleware-order details.

#### Audit log and observability (Phase 9)

`IAuditLog` (`Server/AuditLog.fs`) is registered automatically. The default `EventStoreAuditLog` writes audit events as `ModuleEvent`s under reserved `SourceModule = "_platform.audit"`, so they flow through the same `IEventStore` (in-memory or persistent) and can be queried per-scope via `GetAuditTrail(scopeId, dateRange?, eventType?)`. The DU `AuditEvent` covers `UserLoggedIn`, `TeamCreated`, `MemberAdded`, `MemberRemoved`, `MemberRoleChanged`, `FileUploaded`, `FileDeleted`, `AnalysisRun`, and `PermissionChanged`. Module code can emit `AnalysisRun` (or other case it constructs) by resolving `IAuditLog` from DI; SDK-level cases are emitted automatically by middleware and the platform-API handlers.

Health endpoints are mapped before Giraffe:
- `/health` — liveness probe (runs probes tagged `Liveness` only; today none are registered, so the response is `200 OK` whenever the process accepts requests)
- `/ready` — readiness probe; runs every registered `IHealthCheck` (both Liveness and Readiness probes). Three first-party probes ship by default: `BlobStorageHealthCheck`, `AuthProviderHealthCheck`, `EventStoreHealthCheck`. Companions register additional probes via `ServerApp.withHealthCheck` (Phase 9k) — Redis notification channel, AI providers, embedding providers, vector stores, transactional sinks, and cloud storage backends each ship a probe in their own companion package.

Both endpoints emit JSON: `{"status": "Healthy" | "Degraded" | "Unhealthy", "checks": [{"name": "...", "kind": "Readiness", "status": "...", "message": "..."}]}`. Per-probe `Timeout` (declared on the `IHealthCheck` impl, default 5s) is enforced by the SDK aggregator — slow probes are reported `Degraded` (200 with summary), only `Unhealthy` flips `/ready` to 503.

`AuditReplicator` / `IAuditSink` (Splunk HEC, Datadog Logs, S3 archive, SIEM-specific export) is deferred to a Phase 9 follow-up.

### Metrics (Phase 9e)

`/metrics` emits OpenMetrics 1.0 text scrapable by Prometheus, Grafana Cloud, and OTel collectors with the openmetrics receiver. Opt-in via `ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint` (default `NoMetricsEndpoint` to avoid information disclosure surprises). The `IMetricsSink` interface (`Record` / `Increment` / `SetGauge`) is sync by deliberate design — Phase 9c rule 2 documented exemption for hot-path write-only emission.

Standard SDK metrics (registered automatically when enabled):

| Metric | Kind | Tags | Description |
|---|---|---|---|
| `toolup.requests.total` | Counter | `method`, `route_class`, `status_class` | HTTP requests received |
| `toolup.requests.latency_ms` | Histogram (5ms→10s) | `method`, `route_class`, `status_class` | Request duration |
| `toolup.errors.total` | Counter | `route_class`, `status_class` | Responses with status ≥ 400 |
| `toolup.sse.active_connections` | Gauge | `endpoint` | Open SSE streams |
| `toolup.jobs.queued` | Gauge | (none) | Active scheduled jobs |
| `toolup.jobs.runs.total` | Counter | `handler`, `outcome` | Job runs since process start |
| `toolup.storage.bytes_read` / `_written` | Counter | `container_class` | Blob I/O |

`route_class` is the request path's two-segment prefix (`/api/_platform/teams/team-abc/members/user-xyz` → `/api/_platform`); `status_class` is `1xx` / `2xx` / `3xx` / `4xx` / `5xx`. This bucketing keeps cardinality bounded structurally.

**Cardinality cap.** Two-layer defence: (1) per-metric tag-key allowlist in `MetricDefinition.Tags` drops unsanctioned tags before they hit the accumulator; (2) per-metric distinct-series ceiling (default 1000, configurable per metric via `MetricsSinkConfig.PerMetricMaxSeries`). Series past the ceiling fold into a single `_overflow="true"` series and the first overflow logs `Warn` once per metric.

**Auth surface.** `/metrics` is exempt from `AuthEnforcementMiddleware` so vanilla scrapers without bearer tokens can read it. Deployments needing authn gate at the network layer (LB allowlist, monitoring-network CIDR). Information disclosed is intentional — route templates, tag values, traffic patterns. Deployments that don't want that surface open keep `MetricsEndpoint = NoMetricsEndpoint`.

**OpenTelemetry companion.** `src/Metrics/OpenTelemetry/` ships a sub-companion that implements `IMetricsSink` over BCL `System.Diagnostics.Metrics.Meter` (the OTel-native primitive on .NET 10). The companion does NOT take an OpenTelemetry SDK dep — deployments that want OTLP export add the SDK to their server project and call `MeterProviderBuilder.AddMeter("ToolUp")`. Multi-sink fan-out via `FanOutMetricsSink` so a single emission dispatches to every registered sink; Prometheus stays at the head of the chain so `/metrics` keeps returning current values even if a companion fails. Wire via `ServerApp.withMetricsSink (OtelMetricsSink.create regs logger)`.

**Client side** (`SDK.Client.run`):
1. Calls `UserSession.configure mode` during initialisation
2. `Anonymous`: uses `sessionStorage` for user ID (per-tab, lost on close)
3. All other modes: uses `localStorage` for user ID (persists across tabs/sessions)
4. `Anonymous`: attaches `X-User-Id` header to API calls
5. Authenticated modes: attaches `Authorization: Bearer <token>` header (falls back to `X-User-Id` until auth token is available)

### Authentication and tenancy

The SDK defines `IAuthProvider` which returns an `AuthenticatedUser` with user ID, display name, email, tenant ID, and roles. Auth providers supply identity only — the SDK owns permissions, team management, and storage scoping.

Built-in providers (shipped):
- `HeaderAuthProvider` — trusts `X-User-Id` header. Dev-only, no validation.
- `StaticJwtAuthProvider` — validates HS256 JWTs (BCL-only, no package dependencies). Checks signature, expiry, optional issuer/audience. Extracts `sub`, `name`, `email` claims.
- `OidcAuthProvider` — OIDC/JWT provider using JWKS discovery. BCL-only (no `Microsoft.IdentityModel.Tokens` dependency); validates RS256 tokens against keys fetched from `/.well-known/openid-configuration`. Works with any OIDC-compliant IdP (Clerk, Auth0, Azure AD, Keycloak, Google Identity). Lives in the `src/AuthProviders/Oidc/` sub-companion.

`AuthConfig` (in `Shared/AuthConfig.fs`) is the declarative shape every auth provider consumes: `KeySource = StaticSecret | JwksDiscovery | JwksExplicit`, `TokenLocation = BearerHeader | Cookie | CustomHeader`, plus optional `Issuer` and `Audience` for claim validation. Each provider exposes a `fromConfig` factory so deployments can wire auth from env vars / config files without touching the provider internals.

Custom providers implement `IAuthProvider.GetUser` (lenient — returns anonymous on missing credentials) and `ValidateRequest` (strict — returns `Error` on missing / invalid / expired credentials).

### Client-side sign-in UI

The server-side auth providers only validate tokens the browser hands them — they don't obtain tokens. Sign-in UI lives in client-side companion packages that register with `AuthUIProvider` (a delegate registry in the core SDK). `ClientConfig.AuthUI` selects the active flow:

- `NoAuthUI` (default) — no SDK-provided sign-in UI; the app takes responsibility for obtaining tokens and handing them to `UserSession.setAuthToken`.
- `OidcAuthUI of OidcUIConfig` — generic Authorization Code + PKCE flow via the `src/AuthProviders/OidcClient/` companion. No npm dependencies (uses browser-native fetch and WebCrypto). Works with any OIDC-compliant IdP — Auth0, Keycloak, Okta, Azure AD, Google Identity, etc.
- `ClerkAuthUI of ClerkUIConfig` — Clerk-managed flow via the `src/AuthProviders/ClerkUI/` sub-companion.
- `CustomAuthUI of CustomAuthUI` — caller-supplied shell wrapper; bypasses the companion registry.

Companions register their handler at module load via a top-level `do AuthUIProvider.register _ _`. Importing the companion's `.Client.props` in the consumer client `.fsproj` is sufficient to activate it. Removing the import removes all of the companion's code from the Fable bundle — the core SDK never references any companion's types.

See [`TECHNICAL_GUIDE.md` — Sign-in UI companions](technical-guide/03-authentication-secrets-and-encryption.md#sign-in-ui-companions) for the full OIDC flow, refresh-token handling, and XSS / multi-tab semantics.

### Peer-bearer-auth (Phase 37)

Phase 37 ships an opt-in substrate that lets one ToolUp instance accept authenticated HTTP calls from another using a shared per-peer bearer token. Targets the small peer scenarios that need cross-instance calls without the full identity / cascade / handshake layer Phase 18 plans to ship.

Register handler prefixes via `ServerApp.withPeerRoutePrefix "/api/peer/echo"`. For every request matching one of these prefixes, `PeerBearerAuthMiddleware`:

1. Reads `X-Peer-Name` to identify the caller (e.g. `"buyer-a"`).
2. Resolves the expected token from `ISecretStore.GetSecret("_platform", $"peers/{peerName}/bearer")`.
3. Compares it constant-time against the request's `Authorization: Bearer <token>` header via `CryptographicOperations.FixedTimeEquals`.
4. On match: stamps `HttpContext.Items["PeerName"]` so handlers can partition state per caller, then lets the request continue. The bearer IS the authentication — peer routes are exempt from `AuthEnforcementMiddleware`'s user-auth check.
5. On mismatch (missing header, missing secret, wrong token, missing `X-Peer-Name`): returns 401 before the handler runs.

Audit emission: `PeerCallAccepted` / `PeerCallRejected` events land in `IEventStore` under `SourceModule = "_platform.peer.bearer"` with `PeerName` on every payload — load-bearing for the FederatedCHAID prototype's 1-N concurrency partitioning. The `PeerBearerConfigValidator` warns at startup when peer prefixes are registered but no peer secrets are seeded (typical "stand up the routes first, seed secrets later" gap).

Strip-imports contract: with `PeerRoutePrefixes = []` (the default), `SDK.Server.compose` skips the middleware registration entirely — deployments without peer routes pay zero runtime cost.

**Relationship to Phase 18.** Phase 18's planned `IPeerAuthProvider` / `JwtPeerAuthProvider` ship a richer peer surface (cryptographic signature verification, delegated assertions, capability handshake, JSON-RPC envelope, cascade context, job-substrate fusion). Phase 37 is the minimum precursor — the simplest peer scenario, "let instance X POST to my `/api/peer/foo` with a shared bearer", needs only this middleware. The two flavours coexist on different prefixes: a deployment can register `withPeerRoutePrefix "/api/peer/echo"` for the bearer flavour AND register the Phase 18 substrate at `/api/peer/federated/` at the same time. Phase 18 supersedes the bearer flavour when the deployment's threat model justifies the larger surface; until then the bearer middleware remains as the simplest peer-auth path.

`IPeerBearerAuthContract` (in `ToolUp.Platform.Tests/Contracts/`) covers the validator's six documented decisions (acceptance, missing token, wrong token, constant-time comparison, X-Peer-Name spoofing, missing X-Peer-Name) so external implementations can validate against the same conformance bar.

### Team management

Team CRUD, membership, and active-team tracking are entirely SDK-owned (not part of the auth provider). `TeamStore` persists to `IBlobStorage` under the `_platform` container:
- `teams/{teamId}.json` — team metadata
- `memberships/{userId}.json` — user's team memberships
- `active-team/{userId}.txt` — user's currently selected team

Five ToolUp.Remoting APIs (auto-injected by `ServerApp.run`) cover the platform surface:
- **`TeamApi`** — team CRUD / membership: `CreateTeam`, `GetMyTeams`, `AddTeamMember`, `RemoveTeamMember`, `ChangeMemberRole`, `GetTeamMembers`, `SetActiveTeam`, `GetActiveTeam`.
- **`PermissionApi`** — Owner/Admin RBAC: `GetTeamPermissions`, `SetMemberPermissions`, `SetTeamDefaults`.
- **`AccessibilityApi`** — `GetAccessibleModules` returns `{ Managed; Accessible }` (module Ids the server RBAC-tracks, and the subset the caller can access). The client shell filters server-managed modules by `Accessible`; SDK-built-ins outside `Managed` stay visible unconditionally.
- **`PlatformInfoApi`** — `GetPlatformInfo` returns mode + auth-required flag.
- **`DataCatalogApi`** — `GetDataCatalog` returns every data type the platform supports + the producing modules.

Role-based access (`Owner`, `Admin`, `Member`) gates mutations via `TeamRoles.canManageMembers` / `canWriteTeamConfig`. `TeamScopeResolver` validates current membership on every request — a user removed from their active team is denied immediately regardless of cache TTL. `TeamStore` refuses to remove or demote the last `Owner` of a team (prevents unmanageable team state).

The built-in `TeamManagerUI` client module is auto-injected in Team mode (controlled by `ClientConfig.TeamManager`). It covers team list / create / switch and member list / add / remove / role-change with admin gating.

### Per-team configuration

The SDK's configuration layer lets modules declare a `ModuleConfigSchema` alongside their `register()` and lets team admins override the declared defaults per scope. Values are typed but stored as JSON strings — the blob format is intentionally forward-compatible and validation happens at the handler boundary, not inside the blob.

Core types live in `Shared/ConfigTypes.fs`:
- `ConfigFieldKind` — `Bool | Int (min, max) | Float (min, max) | String (maxLen) | Choice (options)`
- `ConfigFieldSchema` — `{ Key; DisplayName; Description; Kind; DefaultJson; Required }`
- `ModuleConfigSchema` — `{ ModuleKey; DisplayName; Description; Fields }`

Server surface:
- `IConfigStore` (`IConfigStore.fs`) — `GetValues / GetValue / SetValues / ClearModule`. Values are raw JSON strings; strongly-typed helpers (`GetEffective<'T>`) can layer on top.
- `ConfigStore.fs` — blob-backed default. Persists under `_platform/config/{scopeId}/{moduleKey}.json`.
- `ConfigHandler.fs` — ToolUp.Remoting `IConfigApi` implementation. `ListModules` returns every registered schema (plus the reserved `_platform` entry). `GetModuleConfig` / `SaveModuleConfig` / `ClearModuleConfig` resolve the current scope from `AccessContext` and validate payloads against the schema before writing. Writes are gated by the same Owner/Admin check as team management (`TeamRoles.canWriteTeamConfig`).
- `ServerApp.run` auto-injects the config handler and picks up both `ServerConfig.ModuleConfigs` and every registered `ServerModule.ConfigSchema` — an empty module list still yields a live surface because `_platform` is always present.

Client surface:
- `Client/SDK.ClientTypes.fs` carries `ClientModule.Config : ModuleConfigSchema option` alongside every other module attribute. `ClientModuleContext` is threaded into `Init`; `ClientModule.withUnitInit` preserves the existing `unit -> _` signature for modules that don't need config.
- `SDK.Client.run` prefetches every registered module's values on startup and dispatches `ConfigsLoaded`. That message evicts the active module's state and re-inits it with the fresh `ClientModuleContext`. Inactive modules re-init lazily on next selection.
- `Client/TeamConfigUI.fs` is the built-in admin form. One tab per registered module, one input per field (checkbox / number / text / select). Draft values live in `React.useState` (no per-keystroke Elmish dispatch); explicit Save and Clear buttons. Reserved `_sdk.TeamConfig` Id so it can never collide with an app-declared module.
- `ClientConfig.TeamConfig : TeamConfigMode` controls injection — `DefaultTeamConfig` (default), `ConfiguredTeamConfig { Name; Icon }`, `ExternalTeamConfig of ErasedModule`, or `NoTeamConfig`. The admin UI is auto-injected in every non-Anonymous mode; Anonymous has no persistent scope so the form would fail every read.

The `_platform` key carries deployment-wide display defaults (currency, date format, locale). The SDK ships its own default `_platform` schema declaring `currencySymbol: String (max 4 chars, default "£")` — `mergePlatformSchema` in `SDK.Server.fs` joins the SDK's schema with any app-supplied `_platform` entry at compose time so the admin UI always exposes the platform tab. Apps extend the schema by registering their own `_platform` entry in `ServerConfig.ModuleConfigs`; SDK fields the app didn't redeclare are appended (app wins on field-key collision). The typed accessor lives in `Shared/Visualisation.fs` (`Visualisation.PlatformDefaults` + `fromConfig` for client/server, `Server/SDK.Server.fs` `PlatformDefaultsResolver.resolve` for server-side reads). Modules read the resolved record at `Init` time from `ClientModuleContext.PlatformConfig` and store the fields they care about on their own `Model`.

### Access control

`AccessContext` is resolved per-request and provides `UserId`, `TeamId`, `Mode`, and `ModulePermissions` (of type `Map<string, ModulePermission list>` where `ModulePermission = Read | Write | Admin`, with hierarchy `Admin ⊇ Write ⊇ Read` encoded in `ModulePermission.implies`).

**Permissive default:** `ModulePermissions = Map.empty` means unrestricted — every module accessible. RBAC is opt-in per team; teams that haven't configured permissions preserve pre-Phase-4 "everyone can use everything" behaviour.

**Enforcement (shipped):**
- `makePermissionGuardedApi moduleName api` wraps a module's ToolUp.Remoting routes with a `canAccessModule moduleName` check before dispatch. Denials raise `UnauthorizedAccessException` which a custom error handler translates to HTTP 403.
- `ScopeResolutionMiddleware` loads the user's effective permissions from `IPermissionStore` on every team-scoped request and stashes them in `HttpContext.Items` for the `AccessContext` DI factory to pick up.
- Client shell calls `AccessibilityApi.GetAccessibleModules` on startup to filter the sidebar to modules the user can actually use. Not a security boundary — the per-route guard is the actual enforcement.

**Permission store** (`IPermissionStore` + `PermissionStore` blob-backed default): one JSON document per team at `_platform/permissions/{teamId}.json`. Each document holds team-wide `Defaults` (per-module permission lists applied to any member without an explicit override) plus per-member overrides. `GetEffectivePermissions(userId, teamId)` merges these.

**Team-mode onboarding:** a freshly-signed-up user with no active team sees only the Teams sidebar entry — `GetAccessibleModules` returns `{ Managed = ModuleNames; Accessible = [] }` in that state so server-managed modules all hide but SDK built-ins (TeamManager) remain visible.

### Team isolation

The platform's non-negotiable contract: **teams cannot see anything that originated in another team.** Every team-scoped piece of content — uploaded files, KB documents, narratives, notes, AI conversations, vector chunks, audit events — lives under a `team-{teamId}` container resolved per-request from the caller's `StorageScope`. Cross-team reads are structurally impossible: handlers derive their target scope from the request context; they never accept a caller-supplied scope parameter. `IRetrievalPipeline.authorisedScopes` filters team-scope queries against `AccessContext.TeamId` before any vector store call.

**Application-UI-driven writes always target the caller's scope, never `VectorScope.Platform`.** Narrative-commit, KB document upload, KB note creation, AI-context writes, and every other in-app authoring path is structurally restricted to the caller's `Team teamId` scope (or session scope in `Anonymous` / `AuthenticatedEphemeral` modes). No team-side endpoint exposes a target-scope parameter.

**The one write path to `VectorScope.Platform`** is `IPlatformKnowledgeApi` (Phase 4b), shipped from `ToolUp.KnowledgeBase.Core`. Every method is gated server-side on `AccessContext.canModifyPlatformConfig` — the predicate added in Phase 4b commit 4a, returning true only when the caller holds `PlatformRole.PlatformAdmin`. The role itself is bootstrapped via `TOOLUP_INITIAL_PLATFORM_ADMIN` env var on first startup; subsequent assignments go through the gated `PlatformAdminApi.AssignPlatformAdmin` endpoint.

**Read access to `VectorScope.Platform`** is governed by a separate toggle: `ServerConfig.PlatformKnowledgeBase = EnabledPlatformKnowledgeBase` lifts the gate at retrieval time. `NoPlatformKnowledgeBase` (the default) filters `Platform` out of `RetrievalPipeline.authorisedScopes` regardless of caller — existing Platform-scoped chunks stay on disk but are invisible to RAG retrieval. `ListPlatformDocuments` still functions when the toggle is off so admins can pre-populate content before flipping the read switch.

Read and write are orthogonal axes: admin role controls writes regardless of toggle; toggle controls reads regardless of role. The two compose, not overlap.

### Data persistence

The SDK defines `IBlobStorage` for file/object persistence and `IEventStore` for event persistence. Interfaces ship with default implementations suitable for development (`LocalFileStorage` on disk under `data/`, `InMemoryEventStore`). Production deployments swap in cloud-backed implementations via sub-companions without changing module code.

**Cloud blob-storage providers (shipped as sub-companions):**
- `src/Storage/AzureBlobStorage/` — Azure Blob Storage via `Azure.Storage.Blobs`.
- `src/Storage/AwsS3Storage/` — AWS S3 via `AWSSDK.S3`. Optional `EndpointUrl` + path-style addressing for S3-compatible stores (MinIO, Cloudflare R2, Backblaze B2).
- `src/Storage/GoogleCloudStorage/` — GCS via `Google.Cloud.Storage.V1`.

All three share the same shape: one root container / bucket holds every ToolUp logical container as a blob-name prefix (`{toolupContainer}/{blobName}`) — works around per-provider naming restrictions and per-account quotas. Each exposes `fromEnv ()` that reads deployment env vars and returns `Some IBlobStorage` when configured. `ToolupApp-Server/Server.fs` picks between them via `TOOLUP_BLOB_STORAGE = local | azure | s3 | gcs`.

`IBlobStorage` surface: `Upload`, `Download`, `Delete` (idempotent), `List` (with prefix — returns `[]` for a missing prefix on all backends), `Exists`, `GetMetadata` (returns `{ Size; LastModified; ContentType }` without fetching content).

**Storage scoping:** Each mode resolves a `StorageScope` per request. `SessionFileStore` checks `scope.Persist` to decide whether to also write to `IBlobStorage`. Ephemeral stores are evicted after a configurable TTL (default 60 minutes).

**Event store (`IEventStore`):**
- `InMemoryEventStore` — thread-safe list, lost on restart. Default (`ServerConfig.EventStore = InMemoryOnly`).
- `PersistentEventStore` — blob-backed, append-only JSON per event under `_platform/events/{scopeId}/`. Opt in with `ServerConfig.EventStore = PersistentBlobBacked retentionPolicy`. Uses whatever `IBlobStorage` is registered (local disk or any cloud companion). Scope isolation is structural (prefix-based). `EventRetentionPolicy` supports `MaxAge` / `MaxCountPerScope` — both optional and independent. Pruning is explicit (`store.PruneScope` / `store.PruneScopes`), not triggered by writes; apps schedule it from startup or a background job (Phase 9b).
- `EventReplay.foldScope` / `foldScopeOfType` — helpers that fold a scope's history chronologically to reconstruct projections or audit trails.

**Contract tests:** `src/ToolUp.Platform.Tests/` ships a parametrised Expecto contract pack per interface (`IBlobStorage`, `IEventStore`, `ISecretStore`, `IPermissionStore`) plus per-implementation bindings. Every new blob-storage provider runs the same contract tests — divergence is caught as a portability bug, not a feature gap.

### Real-time notifications

The SDK ships a generic `INotificationChannel` interface (`Shared/INotificationChannel.fs`, compiled into `ToolUp.Platform.dll` so module projects can publish without importing `.Server.props`) plus a shared `SSEConnectionManager` (`Server/SSEConnectionManager.fs`) that any companion can publish through. `ServerApp.run` registers both as DI singletons and auto-injects the `/api/notifications` SSE route (`Server/NotificationHandler.fs`).

- **Five notification kinds** (`Shared/NotificationTypes.fs`): `SystemMessage` (Info/Warning/Error), `JobProgress`, `JobComplete`, `RefreshData`, `CustomNotification` (payload string). Every envelope carries `Id: Guid`, `ScopeId`, and `OccurredAt`. Subscription handles are `Guid` (Phase 9c portability — identity by value).
- **Per-connection subscription.** Each `/api/notifications` client opens one `EventSource`; the handler subscribes a callback that writes directly to that response. No cross-connection multiplexing at the in-memory layer — distributed implementations are free to shard differently.
- **Serialization.** `NotificationHandler` uses `Fable.Remoting.Json.FableJsonConverter`, matching the rule for any manual server→Fable JSON. Named events (`event: SystemMessage`, etc.) map `NotificationKind` onto the SSE frame so the client can `addEventListener` per kind.
- **Default implementation**: `InMemoryNotificationChannel` in `Server/NotificationChannel.fs` — thread-safe per-scope subscriber list. AI and RAG companions publish `JobProgress` / `JobComplete` through this channel via DI resolution; they do not own the transport any more.
- **Distributed companions:** `src/NotificationChannels/Redis/` ships `RedisNotificationChannel` over `StackExchange.Redis` pub/sub (MIT-licensed, no paid dependency required). Per-scope Redis channel naming (`toolup:notifications:{scopeId}`) gives structural scope isolation at the transport layer — a subscriber for scope A listens on a different Redis channel from scope B. Activated via `TOOLUP_NOTIFICATION_CHANNEL=redis` + `TOOLUP_REDIS_CONNECTION`. Both the in-process default and the Redis companion pass the identical `INotificationChannelContract` test pack; the six Phase 9c portability rules hold on both implementations without interface retrofit.

### Transactional notification companions (Phase 6f)

Out-of-band email / SMS / push delivery for events whose audience isn't a live SSE consumer (job-completion email, alert digests, `MemberRoleChanged` SMS to oncall). Three new `Notification` cases (`TransactionalEmail`, `TransactionalSms`, `MobilePush`) ride the same `INotificationChannel` for portability but the wrapping `DispatchingNotificationChannel` decorator routes them to a `TransactionalDispatcher` queue instead of the inner transport — PII never crosses the wire of a Redis pub/sub topic, and SSE subscribers can't accidentally receive them.

- **`INotificationSink`** (`Server/INotificationSink.fs`) — adapter contract. One sink per `Kind` (`Email` / `Sms` / `Push`); duplicate registration fails at compose time. `SinkResult` is `Delivered | Skipped | TransientFailure | PermanentFailure`; `[<RequireQualifiedAccess>]` because `JobResult` shares case names.
- **`TransactionalDispatcher`** (`Server/TransactionalDispatcher.fs`) — `BackgroundService` draining a bounded queue, mirrors `WebhookDispatcher`'s retry shape (`TransactionalRetryPolicy` record, exponential backoff). Pre-flight prefs check via `IConfigStore.GetRaw` against `_platform.notification_prefs` short-circuits to `Skipped` (no audit, no retry) when the team kill-switch is off. Audit emission per terminal status (`NotificationSent` / `NotificationDeliveryFailed`); PII stays out of audit payloads.
- **`INotificationAddressBook`** (`Server/INotificationAddressBook.fs`) — resolves `userId` → vendor-neutral `EmailAddress` / `PhoneNumber` / `PushToken` at sink dispatch time. SDK defaults: `NoOpNotificationAddressBook` (returns `None` / `[]` always; safe for deployments without a directory) and `BlobBackedNotificationAddressBook` (reads `_platform/contacts/{scopeId}/{userId}.json` JSON via `IBlobStorage`). Scope-isolated by `(userId, scopeId)` lookup key. Real production deployments substitute LDAP / Okta / Azure AD impls by overriding the singleton post-`compose`.
- **Vendor companions:**
  - `src/NotificationChannels/Email/Smtp/` — MIT-licensed MailKit, the no-paid-deps default. Activated via `TOOLUP_TRANSACTIONAL_EMAIL=smtp` + `TOOLUP_SMTP_*`.
  - `src/NotificationChannels/Email/SendGrid/` — pure HTTP REST against api.sendgrid.com (no SDK), supports templated email via `dynamic_template_data`. Activated via `TOOLUP_TRANSACTIONAL_EMAIL=sendgrid`. API key from `ISecretStore.GetSecret("_platform", "SENDGRID_API_KEY")`.
  - `src/NotificationChannels/Email/Postmark/` — directory reservation, README only. Implementation deferred.
  - `src/NotificationChannels/Sms/Twilio/` — REST against api.twilio.com, Basic auth (`AccountSid` in settings, `TWILIO_AUTH_TOKEN` in secret store). One POST per recipient; first failure short-circuits the loop.
  - `src/NotificationChannels/Push/WebPush/` — RFC 8030 Web Push protocol via the `WebPush` NuGet package. VAPID public key + subject in env, private key in `ISecretStore`. Per-token send (one POST per registered browser). Includes `examples/sw.js` reference service worker.

Apps wire sinks through `ServerApp.withTransactionalSink` (mirrored on `AIServerApp` / `RAGServerApp`); registering any sink causes `compose` to merge the SDK-shipped `_platform.notification_prefs` schema, build the `DispatchingNotificationChannel` decorator, and host the dispatcher as `IHostedService`. Apps without out-of-band delivery omit the call and pay zero runtime cost.

On the client, `Client/NotificationClient.fs` opens a single `EventSource` router and `subscribe (NotificationEnvelope -> unit)` returns a dispose thunk. The built-in `Components/ToastCentre.fs` subscribes to this router, filters to `SystemMessage`, and renders a fixed-position toast strip. `ToastCentreMode` on `ClientConfig` (`NoToastCentre` | `DefaultToastCentre` | `CustomToastCentre of ReactElement`) controls injection; the default is `DefaultToastCentre`. Toast state is local `React.useState` — not part of the Elmish model, per the "text inputs / transient UI state" convention. Transactional kinds (`TransactionalEmail` / `TransactionalSms` / `MobilePush`) are filtered out of SSE delivery defence-in-depth, so the in-process channel can ride non-decorated mode without leaking PII to any open tab.

### Consumer dependency contract

ToolUp.Platform's `paket.references` declares only `FSharp.Core` — but twelve SDK server files import `Newtonsoft.Json` + `Fable.Remoting.Json` so they can use `FableJsonConverter` to persist F# records and DUs losslessly. Newtonsoft arrives as a *transitive* dependency through `Fable.Remoting.Json`, which the **consuming server project must list in its `paket.references`**. Without that reference, Newtonsoft is unresolved and every store file in the list below fails to compile — pointing at the symptom (`Newtonsoft.Json` not found) rather than the cause (missing `Fable.Remoting.Json`).

- **Why not `System.Text.Json`?** It does not round-trip F# DUs losslessly — nullary cases and DU-with-payload shapes like `VersioningPolicy` (`Unversioned` / `Versioned` / `StrictlyVersioned`) and `LinkType` are mangled or dropped. `FableJsonConverter` (shipped in `Fable.Remoting.Json`) handles them correctly, and the same wire format is what `Fable.SimpleJson` parses on the client.
- **SDK files carrying this dependency** (grep `open Newtonsoft.Json` to verify): `Server/ConfigStore.fs`, `Server/DataObjectStore.fs`, `Server/ResultStore.fs`, `Server/LineageStore.fs`, `Server/AuditLog.fs`, `Server/FeatureFlagStore.fs`, `Server/FeatureFlagHandler.fs`, `Server/ModuleQueryBus.fs`, `Server/NotificationHandler.fs`, `Server/WebhookApiHandler.fs`, `Server/WebhookDispatcher.fs`, `Server/WebhookRegistry.fs`. The canonical helper shape lives in `Server/ConfigStore.fs:16-26` — new SDK files that persist F# data should copy it rather than re-deriving the converter wiring.
- **Symmetric on the client side.** Fable client projects that manually parse JSON emitted by these stores (or by SSE frames) need `Fable.SimpleJson` in `paket.references` — it reads the `FableJsonConverter` wire format losslessly without an extra converter, and `src/ToolUpApp-Client/paket.references` already lists it. `Fable.Remoting` RPC serialisation is automatic via `Fable.Remoting.Client` and is unaffected by this contract.

### AG Grid Enterprise companion

AG Grid Enterprise initialisation lives in a separate companion package (`src/AgGridEnterprise/`), not in ToolUp.Platform. This separation exists because:

1. **Licensing boundary.** `ag-grid-enterprise` has a commercial EULA. Keeping it out of ToolUp.Platform means the SDK works with Community edition without shipping Enterprise code.
2. **Bundle isolation.** `ag-grid-enterprise` registers modules with a global `ModuleRegistry` on import — bundlers cannot tree-shake it.

All Enterprise imports and module registration calls are at module top level in `AgGridEnterprise.fs` — this ensures AG Charts Enterprise animation hooks are installed before the first chart renders. The `register(licenseKey)` function only sets the license key. To use Community only: remove the `.props` import and the `register` call.

## SDK Structure

| File | Purpose | Compiled by |
|------|---------|-------------|
**Shared (all projects — `<Compile>` in `ToolUp.Platform.fsproj`):**

| File | Purpose |
|------|---------|
| `ILogger.fs` | `ILogger` interface — Debug/Info/Warn/Error surface for all SDK logging |
| `DataManagementTypes.fs` | File upload types, `DataTypeId`, `DataTypeInfo` |
| `ProcessedDataTypes.fs` | `ProcessedData` (tagged payload), `ProcessedFileEntry`, `FileManagementApi` |
| `IFileProcessor.fs` | `DataType` record, `CsvHeaders` helpers |
| `IAuthProvider.fs` | `AuthenticatedUser`, `IAuthProvider` interface |
| `AuthConfig.fs` | `AuthConfig` / `KeySource` / `TokenLocation` — declarative auth-provider shape |
| `IBlobStorage.fs` | Blob storage interface + `BlobMetadata` |
| `ISecretStore.fs` | Secret management interface (scoped; write + list methods) |
| `IAIProvider.fs` | `IAIProvider` interface, AI request/response types, `RetryPolicy`, `AIProviderError` |
| `StorageScope.fs` | `PlatformMode`, `StorageScope`, `ScopeResolutionRequest`, `ScopeResolutionError` |
| `RoleTypes.fs` | `TeamRole` DU |
| `PermissionTypes.fs` | `ModulePermission` DU + `ModulePermission.implies` helper, `TeamPermissions` storage record (shared so `PermissionApi` can expose it) |
| `AccessContext.fs` | `AccessContext` record + module helpers (`canAccessModule`, `hasPermission`, `configScope`, `flagScope`) |
| `TeamTypes.fs` | `TeamInfo`, `TeamMembership`, `PlatformInfo`, plus the five platform-API contracts (`PlatformInfoApi`, `TeamApi`, `PermissionApi`, `AccessibilityApi`, `DataCatalogApi`) |
| `TeamRoles.fs` | Role predicates (`canManageMembers`, `canWriteTeamConfig`, `isOwner`, `displayName`) |
| `ModuleAITypes.fs` | `AIToolDefinition`, `ToolParameterSchema` (module-facing AI tool surface) |
| `ConfigTypes.fs` | `ModuleConfigSchema`, `ConfigFieldSchema`, `ConfigFieldKind` DU |
| `ConfigApi.fs` | `IConfigApi` ToolUp.Remoting contract, `ModuleConfigEntry`, `ModuleConfigView` |
| `NotificationTypes.fs` | `Notification` DU (5 kinds), `NotificationKind`, `NotificationEnvelope` record + companion module (`NotificationEnvelope.create`), `SystemMessageLevel`, subscription-handle Guid alias |
| `INotificationChannel.fs` | `INotificationChannel` interface — `Publish` / `Subscribe` / `Unsubscribe`, `Guid` handles. Lives in Shared so module projects can consume it from `ToolUp.Platform.dll` (e.g. `KnowledgeBase` publishes `DataRefreshed` after narrative ingestion) |
| `SDK.Shared.fs` | `ModuleEvent`, `IEventStore`, `EventRetentionPolicy`, `EventStoreMode`, `EventReplay`, `PageConfig`, `ModuleDefinition`, `StaticPathBehaviour`, `RateLimitConfig`, `ServerConfig` (incl. `ModuleConfigs`, `RequireHttps`, `TrustForwardedHeaders`, `StaticPathBehaviour`, `SlowRequestThreshold`, `DefaultTeamStorageQuotaBytes`, `RateLimit`) |
| `AuditTypes.fs` | `AuditEvent` DU + per-case payload records (`UserLoggedInPayload` etc), `AuditSourceModule`, JSON helpers; reused by middleware, handlers, and `IAuditLog` consumers |
| `ShareTokenTypes.fs` | Phase 21b — `ShareTokenClaim`, `ShareTokenIssueRequest`, `ShareToken`, `ShareTokenError` (with `[<RequireQualifiedAccess>]`), `ShareTokenTypes.DefaultLifetime` / `DefaultUseLimit` / `AuditSourceModule`. Generic share-token primitive — Forms publishable surveys are the first consumer; future shareable dashboards / magic-login links bind the same shape |

**Server-only (injected via `.Server.props`):**

| File | Purpose |
|------|---------|
| `ConsoleLogger.fs` | Default `ILogger` — stdout/stderr with level tags |
| `InMemoryEventStore.fs` | Default `IEventStore` — thread-safe list, reverse-chronological by `OccurredAt` |
| `SSEConnectionManager.fs` | Shared SSE connection registry — scope-keyed `ConcurrentDictionary`, resolved from DI by AI / RAG / notifications. 30s keepalive timer evicts zombie connections; `Broadcast` awaits `WriteAsync`/`FlushAsync` so failed writes prune immediately. `IDisposable` so DI tears the timer down at shutdown |
| `NotificationChannel.fs` | `InMemoryNotificationChannel` default — per-scope subscriber list, thread-safe |
| `NotificationHandler.fs` | `/api/notifications` SSE handler — named-event framing, FableJsonConverter, 15s keepalive on each connection's response |
| `PersistentEventStore.fs` | Blob-backed `IEventStore` — append-only one-blob-per-event under `_platform/events/{scopeId}/`; optional `EventRetentionPolicy` with `PruneScope` / `PruneScopes` methods |
| `Auth/HeaderAuthProvider.fs` | Dev-only `IAuthProvider` — trusts `X-User-Id` header |
| `Auth/StaticJwtAuthProvider.fs` | HS256 JWT validator |
| `LocalFileStorage.fs` | Default `IBlobStorage` on the local filesystem |
| `IShareTokenStore.fs` | Phase 21b — `IShareTokenStore` interface (`Issue` / `Validate` / `MarkUsed` / `Revoke` / `ListByResource`). Six-rule Phase 9c portability audit clean. Opt-in via `ServerConfig.ShareTokenStore = EnabledShareTokenStore` |
| `ShareTokenStore.fs` | Phase 21b — default `BlobShareTokenStore`. HMAC-SHA256-signed wire format `{tokenId}.{base64url(payload)}.{base64url(hmac)}`; persisted claims under `_platform/share-tokens/{scopeId}/{tokenId}.json`; signing key auto-resolved from `ISecretStore` under `_platform/share_token_signing_key`; resource-index entries enable `ListByResource` queries |
| `EnvironmentSecretStore.fs` | Env-var backed `ISecretStore` (read-only) |
| `FileSecretStore.fs` | File-backed `ISecretStore` with `baseDir` param for testability |
| `EncryptedSecretStore.fs` | AES-GCM envelope wrapper around any `ISecretStore` + `rotateScope` helper |
| `TeamManagement.fs` | `TeamStore` — team CRUD, membership, role change, active-team, last-Owner safeguard |
| `PermissionStore.fs` | `IPermissionStore` + blob-backed default — per-team permission document with merge logic |
| `StorageScopeResolver.fs` | `IStorageScopeResolver` + four per-mode implementations; membership-validating `TeamScopeResolver` |
| `FileManagement.fs` | File detection, processing, `SessionFileStore`, `fileManagementApi` |
| `IConfigStore.fs` | Per-scope module config interface (`GetValues`, `GetValue`, `SetValues`, `ClearModule`) |
| `ConfigStore.fs` | Blob-backed default — persists JSON under `_platform/config/{scopeId}/{moduleKey}.json` |
| `ConfigHandler.fs` | `IConfigApi` handler — validates writes against registered schemas, gates via `TeamRoles.canWriteTeamConfig` |
| `Shared/Api.fs` | `ApiCall<'S,'F>` DU (`Start` / `Finished`) and `RemoteData<'T>` DU + companion module — Elmish message/state helpers re-homed from SAFE.Client.Utils (MIT) |
| `Server/Api.fs` | `type Api` with `static member make (builder, ?routeBuilder, ?errorHandler, ?customOptions)` — thin wrapper over the in-tree `Fable.Remoting.Giraffe` adapter (namespace preserved; ships inside `ToolUp.Platform.Server`), keeping the SAFE call-site syntax (injected via `.Server.props`) |
| `Client/Api.fs` | `type Api` with `static member inline makeProxy<'T> (?routeBuilder, ?customOptions)` — thin wrapper over the in-tree `Fable.Remoting.Client` proxy builder (namespace preserved; ships inside `ToolUp.Platform.Client`), keeping the SAFE call-site syntax (injected via `.Client.props`) |
| `SDK.Server.fs` | `WebApplication.CreateBuilder` composition, middleware (`ScopeResolutionMiddleware`, `AuthEnforcementMiddleware`, `RequestTimingMiddleware`, `RemotingBodyNormalizationMiddleware`), `makeApi`, `makePermissionGuardedApi`, the five `platform*ApiHandler` builders (info / team / permission / accessibility / data-catalog), `configApiHandler`, `ServerModule` / `ServerApp` record-based composition API, audit / health / quota / rate-limit DI wiring |
| `AuditLog.fs` | `IAuditLog` interface + `EventStoreAuditLog` default — wraps `IEventStore` under reserved `SourceModule = "_platform.audit"`; fire-and-forget `Record`, scope-filtered `GetAuditTrail` |
| `Shared/IHealthCheck.fs` | Phase 9k portable interface (`Name`, `Kind`, `Timeout`, `Check : unit -> Async<HealthResult>`) for companion-contributed readiness probes. Lives in Shared so `ProjectReference`-style companions (storage backends) see it without `.Server.props` injection |
| `Server/HealthCheck.fs` | First-party probes — `BlobStorageHealthCheck`, `AuthProviderHealthCheck`, `EventStoreHealthCheck` (refactored Phase 9k to implement the ToolUp `IHealthCheck` rather than BCL) |
| `Server/HealthCheckAggregator.fs` | Walks `services.AddSingleton<IHealthCheck>` registrations near end-of-compose and registers each via BCL `AddCheck` through `BclHealthCheckAdapter` (per-probe timeout enforcement, exception → "probe threw:" Unhealthy with 500-char truncation) |
| `Server/HealthCheckResponseWriter.fs` | Custom `HealthCheckOptions.ResponseWriter` emitting `{"status": ..., "checks": [...]}` JSON for both `/health` and `/ready` |
| `RequestTimingMiddleware.fs` | Stopwatch-based slow-request logger; warns via `ILogger` when elapsed > `ServerConfig.SlowRequestThreshold` |
| `RateLimiting.fs` | `RateLimiting.configure` — fixed-window `RateLimiterOptions` partitioned by team / user / IP; `429 + Retry-After`; bypasses `/health`, `/ready`, `/api/notifications` |
| `Shared/MetricsTypes.fs` | Phase 9e — `MetricDefinition`, `MetricKind` DU (`Counter` / `Gauge` / `Histogram of buckets:float list` / `Summary`), `MetricRegistration` |
| `Server/IMetricsSink.fs` | Phase 9e portable interface (`Record` / `Increment` / `SetGauge`); sync-by-design (Phase 9c rule 2 documented exemption — hot path, write-only, no return to await). `NoOpMetricsSink` default registered when `MetricsEndpoint = NoMetricsEndpoint` |
| `Server/PrometheusMetricsSink.fs` | In-process default + `FanOutMetricsSink`. OpenMetrics text emission via `Render()`; two-layer cardinality cap (per-metric tag-key allowlist + per-metric distinct-series ceiling); concurrent-safe accumulators (per-cell lock for floats; histogram bucket counts in `ConcurrentDictionary<float,int64>`) |
| `Server/MetricsMiddleware.fs` | Per-request emission of `toolup.requests.total` / `_latency_ms` / `errors.total` with bucketed tags (`method`, `route_class` = two-segment prefix, `status_class` = 1xx/2xx/3xx/4xx/5xx); `StandardMetrics.registrations` lists the 8 SDK-owned metrics |
| `Server/MetricsEndpoint.fs` | `/metrics` Giraffe handler emitting OpenMetrics 1.0 text. Exempt from `AuthEnforcementMiddleware`; `Cache-Control: no-store` |

**Client-only (injected via `.Client.props`):**

| File | Purpose |
|------|---------|
| `Client/UI/AgChart.fs` / `AgGrid.fs` | Chart and grid Fable bindings (Community) |
| `Client/UI/Toolkit/*.fs` | Design system — split into 6 sub-modules (`OutputFormatting`, `Tokens`, `Typography`, `Layout` with `AppShell` / `Panel` / `Tabs`, `Forms`, `Data`); all under `namespace Toolup.UIToolkit` |
| `Client/UserSession.fs` | User-ID + auth-token storage, mode-aware Remoting headers |
| `Client/SDK.ClientTypes.fs` | `ErasedModule`, `ClientModule.register` / `withUnitInit`, `ClientConfig`, `DataManagerMode`, `TeamManagerMode`, `TeamConfigMode`, `ToastCentreMode`, `AuthUIMode` (+ `OidcUIConfig` / `ClerkUIConfig` / `CustomAuthUI`), `ClientModuleContext` |
| `Client/AuthUIProvider.fs` | Delegate registry for companion-supplied sign-in UI (`OidcClient`, `ClerkUI`); `register tag handler` + `gate authUI mode shell` dispatch |
| `Client/GeneralUITypes.fs` | Small shared UI helpers (`Toggle`, `UpdateApp`, `CommonComponents`) |
| `Client/ProcessedDataContext.fs` | React context + `ProcessedData.forType` hook — shell-distributed `ProcessedFileEntry list` consumed by module view `[<ReactComponent>]`s |
| `Components/Modal.fs` | Modal dialog component |
| `Components/ToastCentre.fs` | Built-in toast renderer — subscribes to `NotificationClient`, filters `SystemMessage`, auto-dismiss for Info/Warning |
| `Client/NotificationClient.fs` | Single `EventSource` router over `/api/notifications` — named-event dispatch, returns dispose thunk |
| `Client/FileManagerUI.fs` | Built-in file upload/management module (auto-injected per `DataManagerMode`) |
| `Client/TeamManagerUI.fs` | Built-in team-management module (auto-injected in Team mode per `TeamManagerMode`) |
| `Client/TeamConfigUI.fs` | Built-in config admin module (auto-injected in non-Anonymous modes per `TeamConfigMode`, Id `_sdk.TeamConfig`) |
| `Client/SDK.Client.fs` | Shell MVU, sidebar filter, `prepareModules`, config prefetch + re-init, `Client.run` |

**Build pipeline:**

| File | Purpose |
|------|---------|
| `Build/SDK.BuildTypes.fs` | `BuildOutput`, `BuildConfig` |
| `Build/SDK.Build.fs` | FAKE pipeline targets — Clean / Build / Bundle / Run / Docker / Format |

**AI companion (`src/ToolUp.AI/`) and sub-companions** (`src/AIProviders/Claude/`, `src/AIProviders/OpenAI/`, `src/AuthProviders/Oidc/`, `src/Storage/AzureBlobStorage/`, `src/Storage/AwsS3Storage/`, `src/Storage/GoogleCloudStorage/`, `src/EmbeddingProviders/Local/`, `src/EmbeddingProviders/OpenAI/`, `src/NotificationChannels/Redis/`, `src/NotificationChannels/Email/Smtp/`, `src/NotificationChannels/Email/SendGrid/`, `src/NotificationChannels/Sms/Twilio/`, `src/NotificationChannels/Push/WebPush/`) each own their own source files and `.Server.props` / `.Client.props` files. See `src/ToolUp.AI/README.md` for the AI companion's internal structure.