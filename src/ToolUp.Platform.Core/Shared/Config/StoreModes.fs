// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of SDK.Shared.fs: the store / scheduler / ingestion /
// session-registry / backup / model / telemetry / tenant `*Mode` DUs (and their
// option records) that `ServerConfig` selects between. Declaration order is
// unchanged from the monolith; same namespace, same names — only the file
// boundary moved.

/// Selects which `IEventStore` implementation `compose` registers.
/// Default: `InMemoryOnly` (preserves existing behaviour — events are
/// lost on restart). Apps that want persistent, audit-grade history
/// opt in to `PersistentBlobBacked` and pass a retention policy.
type EventStoreMode =
    /// In-process, thread-safe list. Lost on restart. Suitable for
    /// development and for deployments that treat events as ephemeral
    /// signals rather than the system of record.
    | InMemoryOnly
    /// Blob-backed append-only store. One JSON blob per event under
    /// `_platform/events/{scopeId}/`. Uses whatever `IBlobStorage`
    /// implementation is registered (local disk, Azure, S3, GCS).
    /// The retention policy governs when old events are pruned; prune
    /// is NOT automatic — apps prune the scope's stored events
    /// from a scheduled job or startup routine.
    | PersistentBlobBacked of EventRetentionPolicy

/// Selects which `IResultStore` implementation `compose` registers.
/// Default: `NoResultStore` — analytical-result persistence is
/// opt-in. Apps that don't enable a mode get nothing
/// in DI; modules calling `ctx.RequestServices.GetService<IResultStore>()`
/// receive `null` and fall back to local computation only. Apps that
/// want results-without-history use `InMemoryResultStore` for
/// dev/test; production uses `PersistentResultStore` which routes
/// through `IDataObjectStore` with `StrictlyVersioned` policy and
/// emits `AnalysisCompleted` events.
type ResultStoreMode =
    /// No `IResultStore` registered. Modules treat result persistence
    /// as unavailable. Default — keeps the SDK lean for deployments
    /// that don't care about analytical-output history.
    | NoResultStore
    /// In-process store. Lost on restart. Suitable for tests and
    /// short-lived dev environments that want the API surface but
    /// not the durability cost.
    | InMemoryResultStore
    /// Blob-backed via `IDataObjectStore`. Results are stored
    /// `StrictlyVersioned` (no delete, full history). Each successful
    /// `SaveResult` emits an `AnalysisCompleted` event to
    /// `IEventStore` and (when `Lineage = FeatureMode.Enabled`) one
    /// `LineageLink` per declared input.
    | PersistentResultStore

/// Phase 527 — selects whether `compose` registers the service-account
/// substrate: named machine principals owned by a scope, each minting
/// scoped, expiring, revocable API tokens.
///
/// Default `NoServiceAccounts` — no `IServiceAccountStore` in DI, no
/// `ServiceAccountTokenMiddleware` in the pipeline, no
/// `_platform/service-accounts/` blob layout, no admin routes. A
/// deployment that does not opt in is byte-for-byte the deployment it
/// was before this substrate existed (GP 11 / GP 13), including its
/// handling of an `Authorization: Bearer` header, which nothing here
/// touches when the middleware is absent.
type ServiceAccountStoreMode =
    /// No service-account substrate registered. Default.
    | NoServiceAccounts
    /// Register `BlobServiceAccountStore` against the resolved
    /// `IBlobStorage`, mount the admin API, and insert the bearer-token
    /// middleware ahead of scope resolution. Audit emission flows
    /// through the configured `IAuditLog` — under `FeatureMode.Disabled` the impl
    /// skips it at no runtime cost.
    | EnabledServiceAccounts
    /// The consumer composed its own `IServiceAccountStore` singleton
    /// (a directory-backed principal store, a hosted-secrets-backed
    /// token store). The middleware and admin routes are wired exactly
    /// as for `EnabledServiceAccounts`; only the default blob-backed
    /// registration is skipped, so the consumer's singleton stands.
    | CustomServiceAccountStore

/// Phase 9c.E — deployment settings for the Quartz.NET job-scheduler
/// companion (`ToolUp.JobSchedulers.Quartz`). Lives here rather than in
/// the companion because `JobSchedulerMode.QuartzJobScheduler` carries
/// it and the mode is a `ServerConfig` field, which the Fable-packed
/// shared layer must be able to construct. Nothing in this record names
/// a Quartz *type* — it is four primitives (GP 1: the vendor dependency
/// stays inside the companion package).
type QuartzConfig = {
    /// Scheduler instance name. Quartz keys its in-process scheduler
    /// registry on this, so two schedulers in one process must not share
    /// a name. Default `"toolup"`.
    SchedulerName: string
    /// Maximum jobs the scheduler's thread pool runs concurrently.
    /// Default 10, which is Quartz's own default. Must be >= 1 — the
    /// companion's `IConfigValidator` refuses anything lower rather than
    /// letting a zero-sized pool silently never dispatch.
    MaxConcurrency: int
    /// How late a fire may be before Quartz treats it as a misfire.
    /// Default 60 seconds, matching the minute-precision contract
    /// `JobPrecision.Minute` declares.
    MisfireThreshold: TimeSpan
    /// Whether the companion's hosted service starts the scheduler.
    /// `false` leaves it in standby — composed, queryable and writable,
    /// but firing nothing — which is what a `WebOnly` replica of a
    /// multi-process deployment wants. Default `true`.
    StartScheduler: bool
}

[<RequireQualifiedAccess>]
module QuartzConfig =
    /// The documented defaults — scheduler name `"toolup"`, 10 concurrent
    /// jobs, a 60-second misfire threshold, started on compose.
    let defaults: QuartzConfig = {
        SchedulerName = "toolup"
        MaxConcurrency = 10
        MisfireThreshold = TimeSpan.FromSeconds 60.0
        StartScheduler = true
    }

/// Selects which `IJobScheduler` implementation `compose` registers.
/// Default: `NoJobScheduler` — background jobs are opt-in. Apps that
/// don't enable a mode get nothing — no `IJobScheduler` in DI, no
/// `_platform/jobs/` blob layout, no scheduler tick. Apps that opt in
/// pick the in-process default; future distributed companions (Akka,
/// Orleans, Hangfire) add new cases here without changing existing
/// consumers.
type JobSchedulerMode =
    /// No `IJobScheduler` registered. Default — keeps the SDK lean
    /// for deployments that don't run background work.
    | NoJobScheduler
    /// In-process scheduler with minute-precision cron evaluation
    /// and `IBlobStorage`-backed `IJobStore`. Suitable for single-
    /// instance deployments. Multi-silo deployments need a
    /// distributed companion to avoid double-dispatch.
    | InProcessJobScheduler

    /// Phase 9c.E — the Quartz.NET companion
    /// (`ToolUp.JobSchedulers.Quartz`). Quartz owns trigger evaluation and
    /// dispatch; the deployment's `IJobStore` stays the canonical record
    /// of job definitions and run history. The companion constructs the
    /// scheduler and registers it as an instance BEFORE `ServerApp.run`;
    /// `ComposeJobs.registerJobScheduler` adopts that instance (the SDK
    /// core carries no Quartz reference — GP 1). Appended, never inserted:
    /// this DU is a `ServerConfig` field written through `FableConverters`,
    /// so a case inserted mid-union would shift every later case's tag.
    | QuartzJobScheduler of QuartzConfig

/// Phase 218 — policy for the mapping-aware Data Manager's dry-run
/// validation step (the per-row/per-cell error preview shown before a
/// mapped CSV is committed). Read only when `ColumnMapping =
/// FeatureMode.Enabled`; the report is always shown, the policy only
/// decides whether failing rows BLOCK the commit or merely WARN.
type MappingDryRunPolicy =
    /// Default — the dry-run report is advisory: failing rows are
    /// surfaced but the user may still commit (GP 11: prior behaviour,
    /// where commit was unconditional, is preserved).
    | WarnOnValidationFailure
    /// The dry-run report blocks commit when any row would fail
    /// validation — the user must fix the mapping or the source first.
    | BlockOnValidationFailure

/// Phase 447 — selects whether `compose` applies registered `ISeedPack`
/// fixtures at end-of-compose (dev / demo seed data). Default:
/// `NoSeedData` — no packs are applied, no `_platform/seed/` marker blobs
/// are read or written, and a composition that registered packs pays
/// nothing (GP 13). Demo / fixture data must never leak into a real
/// tenant, so `EnabledSeedData` refuses startup on a Team / multi-team
/// production shape; a deployment that deliberately wants seeded data
/// there sets `ForcedSeedData`.
type SeedDataMode =
    /// No seed packs applied. Default — the SDK reads/writes no seed
    /// marker and never resolves a registered pack.
    | NoSeedData
    /// Apply registered packs once per `Name@Version` (idempotent via an
    /// applied-marker blob under `_platform/seed/`). Refused on a Team /
    /// multi-team shape — use `ForcedSeedData` to override.
    | EnabledSeedData
    /// Apply registered packs even on a Team / multi-team production
    /// shape. Deliberate override of the demo-data-in-production refusal.
    | ForcedSeedData

/// Phase 68 — selects the graph-data store backend (`IGraphStore`), the
/// graph-shaped peer of `IEntityStore`. Default: `InMemoryGraphStore` —
/// the zero-dependency in-memory default (GP 2, no engine-by-default) is
/// registered *lazily*, so a deployment that never calls a graph API pays
/// nothing (GP 13). Engine companions (Kùzu / Neo4j / AGE) register their
/// own `IGraphStore` singleton in DI and select `CustomGraphStore`.
type GraphStoreMode =
    /// The zero-dependency in-memory `IGraphStore` (from
    /// `ToolUp.Graph.InMemory`), registered lazily. The default — a
    /// consumer gets a working graph store with no external dependency.
    /// Interprets the documented openCypher subset (the portability floor).
    | InMemoryGraphStore
    /// A companion-provided `IGraphStore` (e.g. an engine-backed
    /// `ToolUp.Graph.Kuzu`) is registered in DI by the deployment;
    /// `compose` registers no default and leaves the consumer's singleton
    /// in place.
    | CustomGraphStore

/// Phase 161 — selects the time-series storage backend (`ITimeSeriesStore`)
/// for high-frequency numeric/analytical series. Default:
/// `NoTimeSeriesStore` — no `ITimeSeriesStore` registered, zero cost.
type TimeSeriesStoreMode =
    /// No time-series substrate registered (default). A deployment with no
    /// high-frequency series pays nothing (GP 13).
    | NoTimeSeriesStore
    /// Register the dev/test in-memory `ITimeSeriesStore` — unbounded
    /// in-memory retention, no durability. For local dev / single-instance
    /// demos only.
    | InMemoryTimeSeries
    /// A companion-provided `ITimeSeriesStore` (e.g.
    /// `ToolUp.TimeSeriesStores.Timescale`) is registered in DI by the
    /// deployment; `compose` registers no default and leaves the consumer's
    /// singleton in place.
    | CustomTimeSeriesStore

/// Phase 10a — selects the module data-migration substrate. Default:
/// `NoDataMigrations` — no registry, no status store, no background
/// runner, no `IDataMigrationApi` route, and not a single blob read at
/// startup. A deployment whose modules have never evolved a persisted
/// shape pays nothing (GP 13), and one that upgrades into this SDK
/// version behaves byte-for-byte as before until it opts in (GP 11).
type DataMigrationMode =
    /// No migration substrate (default).
    | NoDataMigrations
    /// Register the registry, the blob-backed status store, the
    /// `IDataMigrationApi` route, AND the startup runner. The runner
    /// sweeps every team returned by `ITeamStore.ListTeams` for
    /// objects whose stamped schema version lags the version their
    /// module declares, and upgrades them through the registered
    /// chain.
    | EnabledDataMigrations
    /// Register the registry, the status store and the API route, but
    /// NOT the background runner: a pass runs only when an
    /// Owner / Admin presses the button in the admin module. For
    /// deployments that want the upgrade to be a deliberate, observed
    /// act rather than something that happens inside a deploy's
    /// startup window.
    | ManualDataMigrations

/// Phase 448 — selects the dataset substrate (`IDatasetStore`) for immutable,
/// versioned, rectangular typed datasets. Default: `NoDatasets` — no
/// `IDatasetStore` registered, zero cost (GP 13).
type DatasetStoreMode =
    /// No dataset substrate registered (default). A deployment with no
    /// datasets pays nothing (GP 13).
    | NoDatasets
    /// Register the blob-backed `BlobDatasetStore` over the composed
    /// `IDataObjectStore` with the BCL-only JSON-frame codec — no vendor
    /// dependency (GP 1 / GP 2). A new vintage is a new immutable version.
    | BlobDatasets
    /// A companion-provided `IDatasetStore` (or a `BlobDatasetStore` with a
    /// Parquet companion codec) is registered in DI by the deployment;
    /// `compose` registers no default and leaves the consumer's singleton in
    /// place.
    | CustomDatasetStore

/// Phase 528 — tuning for the session registry. Carried on the mode DU
/// rather than as loose `ServerConfig` fields so a deployment that never
/// composes a registry cannot set them in isolation and wonder why
/// nothing happened.
type SessionRegistryOptions = {
    /// Bounded staleness, in seconds, of `SessionRevocationMiddleware`'s
    /// in-process "not revoked" cache. This IS the revocation window and
    /// the whole point of it being a named knob: an operator who needs a
    /// revoke to bite within a second can pay a store read per request,
    /// and one who does not can keep the read off the hot path. Zero
    /// disables the cache entirely (every authenticated request reads the
    /// store).
    ///
    /// The cache is deliberately one-sided. A *revoked* verdict is never
    /// cached-away — revocation is terminal, so once seen it is applied
    /// for the process lifetime of that entry, and the only staleness
    /// that can ever exist is a session that is still being honoured
    /// slightly after it was revoked. There is no window in which a
    /// revoked session is honoured again.
    RevocationCacheSeconds: int
    /// How long a session record is retained after `LastSeenAt` before
    /// `ListForUser` stops returning it. Bounds the growth of the
    /// per-scope prefix without a sweeper: an expired record is filtered
    /// on read and overwritten when the same credential returns.
    RetentionDays: int
}

module SessionRegistryOptions =
    /// 30-second revocation window, 30-day retention. The window matches
    /// the order of magnitude an operator expects from "signed out
    /// everywhere" without putting a store read on every authenticated
    /// request; the retention matches the Phase 337 anonymous-binding
    /// lifetime, so an anonymous session's record does not outlive the
    /// cookie that can present it.
    let defaults: SessionRegistryOptions = {
        RevocationCacheSeconds = 30
        RetentionDays = 30
    }

/// Phase 528 — selects the session-registry substrate (`ISessionRegistry`)
/// backing the active-sessions view, sign-out-everywhere, and admin
/// force-revoke. Default: `NoSessionRegistry` — nothing recorded, no
/// middleware registered, no route mounted, zero cost (GP 13).
type SessionRegistryMode =
    /// No session registry (default). Sessions are not recorded, the
    /// revocation middleware is not registered, and `ISessionApi` 404s —
    /// a deployment that upgrades stays byte-for-byte unchanged (GP 11).
    | NoSessionRegistry
    /// Register the blob-backed `SessionRegistry` over the composed
    /// `IBlobStorage`, under `_platform/sessions/{scopeId}/`. BCL-only
    /// JSON, no vendor dependency (GP 1).
    | BlobSessionRegistry of SessionRegistryOptions
    /// A companion-provided `ISessionRegistry` (e.g. a Redis-backed one)
    /// is registered in DI by the deployment; `compose` registers no
    /// default and leaves the consumer's singleton in place. The options
    /// still apply — they configure the middleware, not the store.
    | CustomSessionRegistry of SessionRegistryOptions

module SessionRegistryMode =
    /// The options a mode carries, or `None` when no registry is
    /// composed. The single reader every wiring site uses, so the
    /// "is a registry composed?" question is asked one way everywhere.
    let options (mode: SessionRegistryMode) : SessionRegistryOptions option =
        match mode with
        | NoSessionRegistry -> None
        | BlobSessionRegistry opts
        | CustomSessionRegistry opts -> Some opts

/// Phase 552 — selects the consented-grant registry (`IGrantConsentStore`)
/// that a module's declared `RequiresCounterpartyApproval` grant policy is
/// resolved through, at the grant write AND again at dispatch.
///
/// Default `NoGrantConsentStore`: no store is registered, no consent blob
/// is written or read, and the dispatch path performs one failed service
/// lookup — so a deployment that declares no counterparty policy is
/// byte-for-byte its pre-552 self (GP 11 / GP 13). It is also the reason
/// the Phase 551 arm refuses conservatively: with no store composed there
/// is no artifact that could satisfy the policy, and admitting the grant
/// anyway would make the declaration decorative.
type GrantConsentMode =
    /// No registry (default). `RequiresCounterpartyApproval` refuses every
    /// grant, at write and at dispatch — the shipped Phase 551 behaviour.
    | NoGrantConsentStore
    /// Register the in-process `InMemoryGrantConsentStore`. Dev and test
    /// only: records do not survive a restart and two app instances do not
    /// share them, which for an authorization artifact means one instance
    /// can honour a consent another has seen revoked.
    | InMemoryGrantConsent
    /// Register the blob-backed default over the composed `IBlobStorage`,
    /// under `_platform/grant-consent/{teamId}/`. BCL-only JSON, no vendor
    /// dependency (GP 1), no state between calls (GP 12 rule 4).
    | BlobGrantConsent
    /// A consumer- or companion-provided `IGrantConsentStore` is registered
    /// in DI by the deployment; `compose` registers no default and leaves
    /// that singleton in place.
    | CustomGrantConsentStore

module GrantConsentMode =
    /// Whether a registry is composed at all. The single reader every
    /// wiring site uses, so "is consent resolvable here?" is asked one way
    /// everywhere rather than by re-matching the DU at each call.
    let isComposed (mode: GrantConsentMode) =
        match mode with
        | NoGrantConsentStore -> false
        | InMemoryGrantConsent
        | BlobGrantConsent
        | CustomGrantConsentStore -> true

/// Phase 445 — the settings behind `BackupMode.BackupEnabled`. Both
/// schedules are OPTIONAL and independent: a deployment may take
/// snapshots on demand (through `IBackupCoordinator`) and drill on a cron,
/// or the reverse. Cron expressions are the five-field shape
/// `JobTypes.Trigger.CronTrigger` takes; either schedule needs
/// `ServerConfig.JobScheduler <> NoJobScheduler` to fire, and the compose
/// validator warns when it cannot.
type BackupSettings = {
    /// Cron on which the coordinator snapshots `Containers` (plus the
    /// reserved `_platform` container, always) into the composed backup
    /// target. `None` = on-demand snapshots only.
    SnapshotCron: string option
    /// Cron on which `RestoreDrillVerifier` restores the latest snapshot
    /// into a scratch prefix, verifies it and reports the outcome as an
    /// audit event + health signal. `None` = on-demand drills only.
    DrillCron: string option
    /// Scope-derived containers snapshotted in ADDITION to `_platform`
    /// (`user-{id}` / `team-{id}` / `session-{id}`). Empty (the default)
    /// snapshots the platform container alone; a per-team backup is the
    /// GP 4 offboarding / onboarding story and names its team here.
    Containers: string list
}

/// Helpers over `BackupSettings`.
module BackupSettings =
    /// On-demand only, platform container only.
    let defaults = {
        SnapshotCron = None
        DrillCron = None
        Containers = []
    }

/// Phase 445 — selects the platform backup / restore coordinator.
/// Default `NoBackup`: nothing registered, no job, no health probe, zero
/// background weight (GP 13). `BackupEnabled` composes
/// `IBackupCoordinator` over the RAW blob storage (beneath any Phase 22
/// encryption decorator, so snapshots copy ciphertext as-is and never
/// carry key material) and requires the deployment to register an
/// `IBackupTarget` naming the destination `IBlobStorage` — any companion
/// (local directory, S3, Azure, GCS — GP 3). The compose validator
/// REFUSES startup when the mode is enabled and no target is registered.
type BackupMode =
    /// No coordinator (default).
    | NoBackup
    /// Compose the coordinator, the restore-drill verifier + its health
    /// probe, and the scheduled snapshot / drill jobs the settings name.
    | BackupEnabled of BackupSettings

/// Helpers over `BackupMode`.
module BackupMode =
    /// Whether a coordinator is composed at all.
    let isComposed (mode: BackupMode) =
        match mode with
        | NoBackup -> false
        | BackupEnabled _ -> true

/// Phase 54i — confirmation gate in front of the destructive
/// tenant-offboard surface. Default `NoConfirmation` preserves Phase 54's
/// one-call behaviour byte-for-byte (GP 11); the two stronger modes
/// require an out-of-band confirmation token (minted via
/// `IPlatformTenantApi.RequestDeprovisionToken`, backed by
/// `IShareTokenStore`) before any token-less destructive offboard
/// (`DeprovisionTenant` / `…Sync` / `…Async` / `ExportThenDeprovision`)
/// is allowed — those token-less paths are refused under a confirmation
/// mode, and the operator must instead call `DeprovisionTenantConfirmed`
/// with a valid token. Opt-in per deployment (GP 13).
type OffboardConfirmationMode =
    /// No confirmation gate. The token-less destructive offboard
    /// surface behaves exactly as Phase 54 — a single Owner /
    /// Platform-Admin call shreds the tenant. Default.
    | NoConfirmation
    /// A short-lived confirmation token (minted by
    /// `RequestDeprovisionToken`) is required: a token-less offboard is
    /// refused with `Error "offboard confirmation required"`, and
    /// `DeprovisionTenantConfirmed` proceeds only with a valid, in-scope,
    /// unexpired token. Guards against fat-finger single-click teardown.
    | TokenConfirmation
    /// As `TokenConfirmation`, plus a two-person rule: the admin who
    /// *redeems* the token (executes `DeprovisionTenantConfirmed`) must be
    /// a *different* Platform-Admin than the one who requested it
    /// (`ShareTokenClaim.IssuedBy`). Same-admin redemption is refused.
    /// Guards against single-insider destruction.
    | TwoPersonRule

module OffboardConfirmationMode =
    /// `true` when the mode requires a confirmation token before a
    /// destructive offboard (everything except `NoConfirmation`). The
    /// handler refuses every token-less destructive path when this holds.
    let requiresToken =
        function
        | NoConfirmation -> false
        | TokenConfirmation
        | TwoPersonRule -> true

/// Phase 828 — the self-hosted log store's tuning knobs: where the
/// database lives, how much of it is kept, and how its full-text index
/// tokenises. Carried by `LogStoreMode.SqliteLogStore`, so a deployment
/// that never opts in never constructs one.
type LogStoreConfig = {
    /// Path to the store's single database file. Relative paths resolve
    /// against the process working directory. The containing directory is
    /// created on first open if it does not exist.
    DatabasePath: string
    /// Retention bound by age — entries older than this many days are
    /// deleted by the hourly sweep. Default 14. Zero or negative disables
    /// the age bound (the row cap still applies).
    MaxAgeDays: int
    /// Retention bound by count — the sweep trims the oldest entries until
    /// at most this many remain. Default 5,000,000. Zero or negative
    /// disables the row cap (the age bound still applies).
    MaxRows: int64
    /// The FTS5 tokeniser the message index is built with. Default
    /// `"unicode61"`, whose word rule is the one `LogSearchQuery.tokenise`
    /// mirrors; changing it is a deliberate departure from the portable
    /// text-match contract and applies only to a freshly-created index.
    FtsTokeniser: string
}

[<RequireQualifiedAccess>]
module LogStoreConfig =
    /// The documented defaults — 14 days, 5,000,000 rows, the
    /// `unicode61` tokeniser — around the caller's chosen database path.
    let create (databasePath: string) : LogStoreConfig = {
        DatabasePath = databasePath
        MaxAgeDays = 14
        MaxRows = 5_000_000L
        FtsTokeniser = "unicode61"
    }

/// Phase 828 — selects the self-hosted log store (`ILogStore`) the
/// deployment records its own structured log lines into. Default:
/// `NoLogStore` — nothing registered, no logger decorated, no file
/// opened, zero cost (GP 13) and a boot path byte-for-byte unchanged
/// (GP 11).
type LogStoreMode =
    /// No log store (default). Log lines go to stdout/stderr exactly as
    /// they did before this substrate existed; nothing is queryable
    /// in-platform. A deployment shipping its logs to an external
    /// aggregator wants this.
    | NoLogStore
    /// Record every log line into a local SQLite database at the given
    /// config, decorate the resolved `ILogger` so writes reach the console
    /// **and** the store, and run the hourly retention sweep.
    | SqliteLogStore of LogStoreConfig

/// Phase 829 — the metrics-history flusher's tuning knobs: how often the
/// live metric registry is sampled into `ITimeSeriesStore`, and how long
/// the resulting points are kept. Carried by
/// `MetricsHistoryMode.EnabledMetricsHistory`, so a deployment that never
/// opts in never constructs one.
type MetricsHistoryConfig = {
    /// Seconds between flushes. Each flush appends one point per
    /// `(metric, tag set)` series. Default 60 — one sample a minute, the
    /// cadence a Prometheus scrape would use. Values below 1 are clamped
    /// to 1 by the flusher so a mis-set config cannot spin the tick loop.
    FlushSeconds: int
    /// Retention bound by age — points older than this many days are
    /// dropped by the daily sweep. Default 30. Zero or negative disables
    /// the sweep entirely, which makes the series grow without bound.
    RetentionDays: int
}

[<RequireQualifiedAccess>]
module MetricsHistoryConfig =
    /// The documented defaults — a 60-second flush cadence and 30 days of
    /// retention.
    let defaults: MetricsHistoryConfig = {
        FlushSeconds = 60
        RetentionDays = 30
    }

/// Phase 829 — selects whether the live metric registry is sampled into
/// `ITimeSeriesStore` on a cadence, giving the standard SDK metrics a
/// queryable history without a new store. Default: `NoMetricsHistory` —
/// no `BackgroundService`, no reads of the sink, not a single point
/// appended, zero cost (GP 13) and a boot path byte-for-byte unchanged
/// (GP 11).
type MetricsHistoryMode =
    /// No metrics history (default). The live registry is still rendered
    /// by `/metrics` exactly as before; nothing is retained across a
    /// process restart. A deployment scraping into an external
    /// time-series system wants this.
    | NoMetricsHistory
    /// Sample the live registry into the composed `ITimeSeriesStore`
    /// every `FlushSeconds` and run the daily retention sweep. Requires
    /// `MetricsEndpoint = FeatureMode.Enabled` (the concrete
    /// `PrometheusMetricsSink` is the read tap) and a composed
    /// `ITimeSeriesStore`; the flusher names either one's absence once
    /// rather than sampling into nothing silently.
    | EnabledMetricsHistory of MetricsHistoryConfig

/// Phase 9w — selects whether `compose` mounts the Datadog readback
/// surface: three Owner/Platform-Admin-gated endpoints under
/// `/api/observability/datadog/*` over a DI-registered
/// `IDatadogReadbackApi` (implemented by the
/// `ToolUp.Observability.Datadog` companion). Default:
/// `NoDatadogReadback` — no route is mounted, nothing resolves the
/// interface, and a deployment that does not use Datadog pays nothing
/// (GP 13) and boots byte-for-byte as it did before this substrate
/// existed (GP 11).
///
/// The read side of the Datadog pairing — the WRITE side is the
/// independent `IAuditSink` companion, which this mode neither requires
/// nor enables. A deployment may run either alone.
type DatadogReadbackMode =
    /// No readback surface (default). The three endpoints are not
    /// mounted, so a client proxy against such a deployment 404s — the
    /// established absence shape for an unmounted SDK route.
    | NoDatadogReadback
    /// Mount the readback endpoints under the supplied configuration.
    /// Requires an `IDatadogReadbackApi` in DI (the companion's
    /// `Datadog.create`) and both `DD-API-KEY` and
    /// `DD-APPLICATION-KEY` in `ISecretStore` under `_platform`; each
    /// endpoint reports a typed, audited soft failure for whichever is
    /// absent rather than blanking the admin surface.
    | EnabledDatadogReadback of DatadogReadbackConfig