# ToolUp.Platform Technical Guide

This document explains how ToolUp.Platform extends the standard F# full-stack architecture (Shared / Server / Client on Giraffe + Fable + Elmish + Feliz + Fable.Remoting) to support a modular, multi-module platform. It assumes familiarity with Giraffe, ASP.NET Core, Fable, Elmish, and Fable.Remoting.

> **Origins.** ToolUp.Platform was bootstrapped from the [SAFE Stack](https://safe-stack.github.io/) template. The Saturn DSL (a thin wrapper over Giraffe) and the SAFE.Client / SAFE.Server metapackages (which bundled Fable + Elmish + Feliz + Fable.Remoting at pinned minor versions) were retired in favour of direct library references. The SAFE `Api.make` / `Api.makeProxy<T>` / `ApiCall<_,_>` / `RemoteData<_>` call-site surface is preserved inside `ToolUp.Platform` itself (`Shared/Api.fs` + `Server/Api.fs` + `Client/Api.fs`), so the experience for a developer familiar with SAFE is unchanged.
>
> **Post-SAFE composition pipeline.** With Saturn gone, `compose` builds the server directly via `WebApplication.CreateBuilder` and adds middleware / services with the raw ASP.NET Core APIs. The fluent record-based surface (`ServerApp` → `AIServerApp` → `RAGServerApp`) is unchanged — apps still write the same pipeline — but several extension points Saturn used to provide implicitly are now explicit `ServerConfig` fields: `RequireHttps`, `TrustForwardedHeaders`, `StaticPathBehaviour`. See [Post-SAFE composition pipeline](technical-guide/01-architecture-and-composition.md#post-safe-composition-pipeline).

---

## How this guide is organised

This guide was split from a single ~3,300-line file into thirteen coherent chapters under [`technical-guide/`](technical-guide/). Read them in order for a front-to-back tour, or jump straight to the chapter that owns the section you need. Every chapter carries prev / next / index navigation; cross-chapter references are linked.

This page is the table of contents — each chapter lists the `##` sections it contains so a "see *Section X*" pointer from elsewhere (companion guides, the README) resolves to the right chapter.

## Chapters

### [1. Architecture & Composition](technical-guide/01-architecture-and-composition.md)

The foundational model: why the standard three-project layout doesn't scale, props-based code injection, type erasure for module composition, the Elmish shell, and the server composition pipeline (now including Phase 9j CSP / CSRF hardening).

- The Problem with the Standard Three-Project Layout
- The Core Insight: Props-Based Code Injection
- Type Erasure for Module Composition
- The Shell MVU
- Server Composition (incl. Post-SAFE composition pipeline, production deployment knobs, `logApiError` / ILogger validation, composition seam & audit, Phase 9j `ICspContributor` + CSRF guard)

### [2. Multi-Tenancy, Teams & Access Control](technical-guide/02-multi-tenancy-and-access.md)

How the platform scopes storage per deployment mode, manages teams, enforces access control, resolves per-team configuration, and surfaces five concern-scoped platform Fable.Remoting APIs (`PlatformInfoApi` / `TeamApi` / `PermissionApi` / `AccessibilityApi` / `DataCatalogApi`).

- Platform Modes and Storage Scoping
- Team Management (incl. the five platform-level Fable.Remoting APIs and the team-switching reset flow)
- Access Control
- Per-Team Configuration
- PlatformAdmin profiles (Phase 61 — Standard vs PublicUtility composition)

### [3. Authentication, Secrets & Encryption](technical-guide/03-authentication-secrets-and-encryption.md)

Auth provider contracts, the sign-in UI companion registry, build-time constants (Vite-define ↔ `BundleConstants` mapping), secret storage, and blob encryption at rest.

- Authentication Providers
- Sign-in UI companions
- Build-time constants (Phase 11.G + Phase 16e — typed-accessor table, fail-loud-on-placeholder behaviour, `vite.config.mts` define wiring)
- Secret Storage and Encryption
- Blob storage encryption at rest (Phase 22)

### [4. Data & Storage Substrate](technical-guide/04-data-and-storage-substrate.md)

The persistent stores: event store, data object store, data catalog, result store and lineage.

- Persistent Event Store
- Data Object Store (Phase 7)
- Data Catalog (Phase 7a)
- Result Store and Lineage (Phases 8 / 8a)

### [5. Audit, Health & Metrics](technical-guide/05-audit-health-and-metrics.md)

Observability substrate: the audit trail, external audit replication, health / timing / quotas / rate limiting (including the post-deploy smoke endpoint), metrics + OpenTelemetry export, and distributed tracing.

- Audit trail (Phase 9)
- Audit replication to external sinks (Phase 9g)
- Health, request timing, quotas, and rate limiting (Phase 9 + 9k, including Phase 9o smoke endpoint)
- Metrics and OpenTelemetry export (Phase 9e)
- Distributed tracing — `IActivitySink` (Phase 9l)

### [6. Background Jobs, Ingestion & Diagnostics](technical-guide/06-jobs-ingestion-and-diagnostics.md)

Background job scheduling, the data ingestion substrate, the OAuth Authorization Code substrate + data-source admin UI, the dev diagnostics endpoint, and the diagnostic support bundle.

- Background jobs (Phase 9b)
- Data ingestion (Phase 10 — interface-first; connectors deferred)
- OAuth Authorization Code substrate + data-source admin UI (Phase 10e)
- Dev diagnostics endpoint (Phase 9a)
- Diagnostic support bundle (Phase 9n)

### [7. Module Communication, Indexing & Portability](technical-guide/07-module-communication-and-portability.md)

The module-to-module query bus, secondary indexes over blob storage, production-hardening refusals, the cross-interface portability audit, and the share-token substrate.

- Module-to-module query bus
- Secondary indexes over `IBlobStorage` (Phase 9f)
- Production hardening — silent-default refusals (Phase 6l)
- Phase 9c portability — cross-interface audit summary
- Share-token substrate + anonymous routes (Phase 21b)

### [8. UI Components & Front-End](technical-guide/08-ui-components.md)

The AG Grid Enterprise companion, AG Charts axes/animation rules, and module-level error boundaries.

- AG Grid Enterprise Companion
- AG Charts: Axes Format and Animation
- Module-level error boundaries (Phase 12c)

### [9. Module Conventions, Data Flow & Build](technical-guide/09-module-conventions-data-flow-and-build.md)

The four-file module convention, end-to-end data flow, the build pipeline, and what changed from the standard three-project layout.

- The Four-File Module Convention
- Data Flow
- Build Pipeline
- What Changed from the Standard Three-Project Layout

### [10. Notifications & Webhooks](technical-guide/10-notifications-and-webhooks.md)

The real-time SSE notification pipeline, transactional notifications, and outbound webhooks.

- Real-time notification pipeline
- Transactional notifications (Phase 6f)
- Outbound webhooks

### [11. AI Integration & Closing Notes](technical-guide/11-ai-integration-and-closing-notes.md)

Where AI integration lives (the `ToolUp.AI` companion), key design constraints, and runtime quirks observed during the .NET 10 / Fable 5 migration.

- AI integration — see the companion
- Key Design Constraints
- Runtime quirks observed during the .NET 10 / Fable 5 migration

### [12. Hosting Models](technical-guide/12-hosting-models.md)

Picking a host runtime (Kestrel default vs the three host-adapter companions for Azure Functions / AWS Lambda / Google Cloud Functions), the compatibility matrix between `ServerConfig` flags and each runtime, and end-to-end worked examples per cloud provider.

- When serverless is appropriate / NOT appropriate
- Host runtimes
- Compatibility matrix (`ServerlessHostMode`, `ProcessProfile`, background subsystems, notifications, platform mode, transport-level features)
- Worked examples (common composition root, Azure Functions, AWS Lambda, Google Cloud Functions, hybrid serverless front-door + Kestrel worker silo)
- Cold-start mitigation (framework-dependent publish, R2R over trimming, pre-resolved singletons, provider always-warm levers, module footprint, measurement)
- Per-host packaging (FAKE) — `HostPackaging.packAzureFunctions` / `packAwsLambda` / `packGoogleCloudFunctions` helpers wire `dotnet publish` output into deployable zips, with built-in size-floor verification for the Verify chain

### [13. Deployment Shapes](technical-guide/13-deployment-shapes.md)

How a pure-Kestrel deployment partitions across silos via `ServerConfig.ProcessProfile` — the three shipped shapes (single-process / web+worker / web+worker+dispatcher), the substrate every shape must share, the cross-silo coordination contract, and the `/dev/inspect` panel that surfaces the resolved matrix to operators.

- Three pure-Kestrel deployment shapes (`AllInOne` / `WebOnly` + `WorkerOnly` / `WebOnly` + `WorkerOnly` + `DispatcherOnly`)
- Substrate contract — what every shape must share
- Cross-silo coordination contract (single-leader subsystems, `IDistributedLock` Phase 9i deferral, `ReplicaCount` pinning)
- Operator visibility — `/dev/inspect`'s `ProcessProfile` panel
- Follow-ups (deferred)

### [14. Docker Hosting](technical-guide/14-docker-hosting.md)

Packaging a Kestrel deployment as a container — the OCI image layout, `tini` signal forwarding, non-root convention, healthcheck wrapper, per-platform deployment entry points (App Service Linux / Cloud Run / ECS / Kubernetes), and how the same image runs every `ProcessProfile` via env var. Companion: [`ToolUp.Hosts.Docker`](../Hosts/Docker/README.md).

- Image layout (multi-stage F# build, restore vs publish layer caching)
- Why `tini` (signal forwarding, zombie reaping, the `SIGKILL`-after-grace-period failure mode)
- Non-root by convention (uid/gid 10001, per-platform support matrix)
- Healthcheck wrapper (`/health` Liveness, `TOOLUP_HEALTHCHECK_URL` / `TOOLUP_HEALTHCHECK_TIMEOUT` overrides)
- `ProcessProfile` interaction — one image, env-var-driven role (`WorkerOnly` + multi-replica caveat)
- Forwarded-headers trust (Phase 16d default-on; opt-out only)
- Per-platform deployment (Azure App Service Linux, GCP Cloud Run, AWS ECS Fargate, Kubernetes)
- Build-context hygiene — `.dockerignore`
- Limitations (no streaming caveat — pass-through, no multi-replica `WorkerOnly`, no signing pipeline / SBOM)
