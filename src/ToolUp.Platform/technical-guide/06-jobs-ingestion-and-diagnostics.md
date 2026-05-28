# ToolUp.Platform Technical Guide — 06. Background Jobs, Ingestion & Diagnostics

> Part of the **[ToolUp.Platform Technical Guide](../TECHNICAL_GUIDE.md)** — see the index for the full chapter list and document preamble.
> [← Prev: 5. Audit, Health & Metrics](05-audit-health-and-metrics.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 7. Module Communication, Indexing & Portability →](07-module-communication-and-portability.md)

---

## Background jobs (Phase 9b)

Modules can register recurring (cron-triggered), event-driven, or on-demand background jobs. The platform owns scheduling, dispatch, retry, lifecycle-event emission, and admin-UI surface; modules supply only an `IJobHandler` and a `JobRegistration`.

### Activation

`ServerConfig.JobScheduler` is an opt-in DU. `NoJobScheduler` (default) skips registration entirely — no `IJobStore`, no `IJobScheduler`, no scheduler tick, no `_platform/jobs/` blobs. `InProcessJobScheduler` activates the in-process default (`InProcessJobScheduler` registered as `IJobScheduler` + `Microsoft.Extensions.Hosting.IHostedService`, ticking once per minute aligned to wall-clock minute boundaries). Distributed companions (Akka, Orleans Reminders, Hangfire) add new cases here without changing existing consumers.

### Substrate

Five files form the SDK layer:

- `Shared/JobTypes.fs` — `JobId` (Guid, GP-12 Rule 1), `JobPrecision` (`Second | Minute`, Rule 6), `IdempotencyKey`, `Trigger` (`CronTrigger | OnEvent | Manual`), `JobRetryPolicy` (data, no callbacks — Rule 3), `JobDefinition`, `JobRun`, `JobStatus`, `JobRunStatus`, `TriggerSource`, `JobContext`, `JobResult`, `ScheduleError`, `JobRegistration`.
- `Server/IJobStore.fs` — persistence contract. Async at every method (Rule 2), scoped strictly per `ScopeId` for team isolation (GP 4). `Save / Get / ListJobs / Update / FindByIdempotencyKey / RecordRun / GetRecentRuns / DueJobs / ListScopesWithJobs`.
- `Server/IJobHandler.fs` — single `Execute: JobContext -> Async<JobResult>`. Stateless between invocations (Rule 4) — handlers receive every piece of state via `JobContext + Payload`.
- `Server/IJobScheduler.fs` — orchestrator. `RegisterHandler / Schedule / Cancel / Disable / Enable / Get / ListJobs / GetRecentRuns / TriggerOnce / NotifyEventWritten`.
- `Server/JobStore.fs` (default) + `Server/JobScheduler.fs` (default) + `Server/CronExpression.fs` (parser).

Plus the integration:
- `Server/JobNotifyEventStore.fs` — `IEventStore` decorator that fires `IJobScheduler.NotifyEventWritten` after every `Write`. Stacks above `HookedEventStore` (webhooks). Without this wrapper, `OnEvent`-triggered jobs never auto-fire.
- `Shared/JobApi.fs` + `Server/JobApiHandler.fs` — Fable.Remoting surface (auto-injected when scheduler is enabled). Read paths ungated within the caller's scope; write paths require Owner / Admin in `Team` / `MultiTeam` mode (mirrors `ConfigHandler.ensureWriteAllowed`). The handler overwrites caller-supplied `ScopeId` and `CreatedBy` with the resolved `AccessContext` values — wire-side impersonation is impossible.

### Dispatch lifecycle

The `BackgroundService.ExecuteAsync` loop sleeps until the next minute boundary, then for every scope returned by `IJobStore.ListScopesWithJobs` calls `IJobStore.DueJobs(scope, now)` and dispatches each due job concurrently via `Async.Start`. Per-`JobId` `SemaphoreSlim` serialises overlapping ticks for the same job (single-instance correct; distributed lease is a Phase 9c follow-up).

`dispatchOne` mirrors `WebhookDispatcher.runDelivery` in shape:

1. Re-read the job inside the per-job lock to avoid acting on a stale snapshot.
2. Loop attempts up to `RetryPolicy.MaxAttempts`. Sleep `JobRetryPolicy.delayFor attempt` (exponential backoff capped at `MaxBackoff`) before each attempt.
3. Synthesise a system `AccessContext` for the job's scope (`UserId = "_system"`, `TeamId` derived from scope in Team / MultiTeam mode, `ModulePermissions = empty` so handlers see unrestricted access). The scheduling user's permissions are NOT used — cron jobs run when no user is online.
4. Record a `Running` `JobRun` row, emit a `JobStarted` event, call `IJobHandler.Execute(ctx)`.
5. Branch on `JobResult`:
   - `Success` → record `Succeeded` run, emit `JobCompleted`, terminate the loop, reset `ConsecutiveFailures` to 0.
   - `TransientFailure` → record `Failed` run, emit `JobFailed`, increment `attempt`. Exhausted attempts promote to `DeadLettered`.
   - `PermanentFailure` → record `DeadLettered` run, emit `JobDeadLettered`, publish a `SystemMessage`-Warning notification, terminate.
6. Update the persisted `JobDefinition` with new `NextRunAt` (computed from `Trigger`), `LastRunAt`, `LastRunStatus`, `LastRunError`, `ConsecutiveFailures`.

Exceptions thrown out of `Execute` are caught and treated as `TransientFailure` so a misbehaving handler doesn't kill the scheduler.

### Lifecycle events

Five event types written to `IEventStore` under `SourceModule = "_platform.jobs"` (literal in `JobScheduler.JobsSourceModule`):

| Event | Payload |
|---|---|
| `JobScheduled` | `{ JobId; ScopeId; Handler; Trigger; CreatedBy; NextRunAt }` |
| `JobStarted` | `{ JobId; ScopeId; Handler; RunId; Attempt; TriggerSource }` |
| `JobCompleted` | `{ JobId; ScopeId; Handler; RunId; Attempt; DurationMs }` |
| `JobFailed` | `{ JobId; ScopeId; Handler; RunId; Attempt; Error; DurationMs }` |
| `JobDeadLettered` | `{ JobId; ScopeId; Handler; Error; Attempts }` |

Serialised via `FableJsonConverter` so the admin UI deserialises them through `Fable.SimpleJson` without an extra converter (canonical SDK pattern). Audit emission failures log `Warn` and are swallowed — the event-store write must never fail the primary dispatch.

### Persistence layout

```
Container: _platform
Definitions: jobs/{scopeId}/definitions/{jobId:N}.json
Runs:        jobs/{scopeId}/runs/{jobId:N}/{ts}-{runId:N}.json
```

Run-blob names start with the ISO-ordered timestamp prefix so `GetRecentRuns(_, _, count)` is `List + sortDescending + truncate count + Download` — newest first without loading every row. Same shape as `webhooks/`, `audit/`, `events/`, `lineage/` so operators see one consistent layout under `_platform/`.

### Cron parser

`Server/CronExpression.fs` is a pure-F# 5-field parser (`Minute Hour DayOfMonth Month DayOfWeek`). Supported syntax: `*`, single integers, comma lists (`1,5,9`), step values (`*/N`). Ranges (`n-m`) and named months / days are deferred — flagged in `Trigger.CronTrigger`'s doc comment for module authors. Validation is at `Schedule` time — invalid expressions return `ScheduleError.InvalidCron(expr, reason)` rather than failing silently at the next tick.

`isDue` does the per-tick check; `nextRunAfter` walks minute-by-minute up to one year ahead (defensive cap so impossible expressions like `0 0 31 2 *` return `None` instead of looping forever).

### Idempotency

When a `JobRegistration` carries `Some IdempotencyKey { Key; TtlSeconds }` and `IJobStore.FindByIdempotencyKey` returns an existing match within the TTL, `Schedule` returns the existing `JobId` without persisting a new definition. Callers cannot tell from the return whether the job is brand-new or recovered — by design, the contract guarantees "submit twice = the job runs once". This makes safe to re-issue the same scheduling call after a transient failure without producing duplicate work.

### Phase 9c portability rule audit

All six rules pass on the in-process default:

1. **Identity by value.** `JobId` is `Guid`. Cancellation, status, manual trigger all by id — no live handles in any signature.
2. **Async at every boundary.** Every method on `IJobStore`, `IJobScheduler`, `IJobHandler` returns `Async<_>`.
3. **Retry as data.** `JobRetryPolicy` is a record. No `OnFailure` callbacks; no supervision strategy types. Distributed implementations map the record onto their own retry mechanism.
4. **Stateless handlers.** `IJobHandler.Execute(JobContext)` — no in-memory state survives across invocations. Handlers that cache module-level mutables silently lose them on grain deactivation / actor restart / worker recycle.
5. **No cross-shard ordering.** `JobDefinition.ShardKey` is an affinity hint; the in-process default ignores it (single process — every job runs in the same scheduler). Distributed companions must guarantee ordering only within a single key.
6. **Precision at the lower bound.** `JobPrecision` is part of `JobDefinition`. The in-process default rejects `Second` with `ScheduleError.PrecisionUnsupported(Second, [Minute])` at registration — silent acceptance is impossible.

A second companion (Akka or Orleans) is the Phase 9c completion criterion and will validate this audit's verdict by running the same `IJobStore` contract pack and a forthcoming `IJobScheduler` contract pack against itself.

### Single-instance limitation

The in-process scheduler is single-process. Two silos running this implementation against the same `IBlobStorage`-backed `IJobStore` would each fire the same due jobs at the same minute. A distributed scheduler companion (Akka cluster sharding, Orleans Reminders, Hangfire's distributed lock, cloud-managed scheduler) is the Phase 9c migration path for multi-silo deployments. Documented in the `JobScheduler.fs` file header so a future audit picks it up; not a blocker for single-process production deployments.

### Worked example: declare a cron job from a module (Phase 9b.B)

The recommended path is the compose-time hook on `ServerModule` —
no post-`Build` resolution of `IJobScheduler`, no manual
`RegisterHandler` + `Schedule` ceremony. The SDK's
`ComposeJobs.registerScheduledJobDeclarations` step iterates module-
and app-level declarations after the scheduler singleton is built and
applies the same `RegisterHandler` + per-scope `Schedule` shape the SDK
already uses for its own internal handlers
(`DataIngestionJobHandler`, `OAuthRefreshJobHandler`).

```fsharp
// In a module's Server.fs
type SalesAnalysisRollupHandler() =
    interface IJobHandler with
        member _.Execute(ctx) = async {
            // Deserialise ctx.Payload, do the work, return Success
            return JobResult.Success
        }

let serverModule =
    ServerModule.create "SalesAnalysis"
    |> ServerModule.withGuardedApi salesAnalysisApi
    |> ServerModule.withJobHandler (
        "salesanalysis.daily-rollup",
        SalesAnalysisRollupHandler() :> IJobHandler,
        CronTrigger "0 6 * * *")  // 06:00 every day, _platform scope
```

The tupled `withJobHandler` shorthand defaults to `JobPrecision.Minute`,
empty payload, `JobRetryPolicy.defaults`, and an auto-built per-scope
idempotency key (`module-{handlerName}-{scopeId}`, one-year TTL) so a
process restart is a no-op. The same handler under
`ServerConfig.JobScheduler = NoJobScheduler` logs a single startup
`Warn` and skips registration — declaring jobs in an unscheduled
deployment is a config mismatch, not a crash.

For per-tenant fan-out, a non-empty payload, a custom retry policy, or
a shard-key, use the full-control variant `withScheduledJob`:

```fsharp
let tenantScanDeclaration =
    ScheduledJobDeclaration.create
        "salesanalysis.tenant-scan"
        (SalesAnalysisRollupHandler() :> IJobHandler)
        (CronTrigger "*/15 * * * *")
    |> ScheduledJobDeclaration.withScopes [ "team-acme"; "team-beta" ]
    |> ScheduledJobDeclaration.withRetryPolicy {
        JobRetryPolicy.defaults with MaxAttempts = 5
    }
    |> ScheduledJobDeclaration.withPayload """{"mode":"incremental"}"""

let serverModule =
    ServerModule.create "SalesAnalysis"
    |> ServerModule.withScheduledJob tenantScanDeclaration
```

Composition-root-owned crons that aren't tied to a single module use the
mirrored `ServerApp.withJobHandler` / `ServerApp.withScheduledJob` hooks
(and the same names on every superset — `AIServerApp`,
`FormsServerApp`, `RAGServerApp`, `SchedulingServerApp` — delegating to
the base).

### Manual `RegisterHandler` + `Schedule` (advanced)

The lower-level `IJobScheduler.RegisterHandler` + `IJobScheduler.Schedule`
remain available for the cases the compose hook does not yet cover:
admin UIs that schedule jobs at runtime against caller-supplied scopes,
modules that need to query a live runtime state before deciding whether
to schedule, and tests that exercise the dispatch loop manually.

```fsharp
let scheduler = sp.GetService<IJobScheduler>() :?> IJobScheduler
scheduler.RegisterHandler("salesanalysis.ad-hoc", SalesAnalysisAdHocHandler())

let registration: JobRegistration = {
    ScopeId = "team-acme"
    Handler = "salesanalysis.ad-hoc"
    Payload = """{"month":"2026-04"}"""
    Trigger = CronTrigger "0 6 * * *"
    Idempotency = Some { Key = "salesanalysis.ad-hoc.team-acme"; TtlSeconds = 86400 }
    RetryPolicy = JobRetryPolicy.defaults
    ShardKey = None
    Precision = Minute
    CreatedBy = "_system"
    Tags = Map.empty
}

let! result = scheduler.Schedule registration
```

### Tick-drift observability (Phase 9b.A)

The `InProcessJobScheduler` runs an aligned-to-wall-clock minute tick: on each iteration it computes `nextTick`, sleeps via `Task.Delay`, then dispatches due jobs against `DateTime.UtcNow`. If the process is paused (debugger break, GC stall, container CPU throttling, hypervisor pause), the `Task.Delay` resolves late — the scheduler wakes at `nextTick + drift`, runs one catch-up tick, and silently collapses every cron-triggered job whose `NextRunAt` falls inside the missed window into a single fire instead of N. Without observability, an operator sees a gap in run-history with no signal anything went wrong.

**Drift contract.** On every loop iteration, after `Task.Delay` returns, the scheduler computes `drift = DateTime.UtcNow - nextTick`. If `drift > 60s`, the scheduler:

1. Records a missed-tick entry in a rolling 60-minute counter — one entry per missed minute boundary (a 5-minute pause adds 5 entries, not 1, so the counter reflects affected boundaries rather than catch-up runs).
2. Updates `LastDriftMs` and `LastTickMissedAt` on the in-memory telemetry snapshot.
3. Walks every scope returned by `IJobStore.ListScopesWithJobs`, collects the cron-triggered jobs whose `NextRunAt` predates the pause, and emits one `JobSchedulerTickMissed` audit event under `_platform` scope with payload `{ ExpectedTickAt; ObservedTickAt; DriftMs; MissedTickCount; JobsSkipped }`.
4. (Optional, opt-in.) If `ServerConfig.BackfillMissedTicks = true`, re-fires every Active `OnEvent`-triggered job once across all scopes with `TriggerSource = ScheduledByEvent("_backfill", Guid.Empty)`. Cron jobs are NOT back-filled — cron semantics expect "fire on the boundary", and re-firing a `*/5 * * * *` rollup three times after a 15-minute pause would conflate three roll-up windows into one batch.
5. Then proceeds with the normal `runTick` call so the catch-up dispatch still happens.

The threshold is exactly `60_000ms` (one full minute) — the loop's own tick interval. Anything shorter than a missed boundary is normal jitter and is not flagged.

**Telemetry pull-surface.** `IJobSchedulerTelemetry` (in `Shared/IJobSchedulerTelemetry.fs`) is a side interface implemented by `InProcessJobScheduler` and registered as a singleton in DI by `ComposeJobs.registerJobScheduler`. Distributed scheduler companions implement their own (or skip registration — observers handle a missing singleton by surfacing "no telemetry available" rather than throwing). The interface is kept separate from `IJobScheduler` so adding drift state doesn't force every distributed companion to implement a notion of "missed boundary" alien to their dispatch model (Akka cluster gossip / Orleans grain activation / Hangfire distributed lock).

**Surfacing.** Two operator-facing surfaces read the telemetry:

- **`/dev/inspect`** — `JobSchedulerDiagnosticsContributor` (registered when the scheduler is registered) emits a `"Job scheduler"` panel with the current counter, last drift, and last-miss timestamp. Debug-only — gated on `ServerConfig.EnableDevEndpoints`.
- **`HealthMonitorApi.GetJobSchedulerTelemetry`** — production-safe Owner/Admin surface that the SDK's `HealthMonitorUI` admin module renders as an inline card on the "Live health" tab. The card is suppressed entirely when `HasScheduler = false` (no scheduler registered, or distributed companion that didn't ship a telemetry impl); when a miss has been observed the card draws with a yellow warning border so the signal is visible without reading the numbers.

**Operator opt-in to back-fill.** `ServerConfig.BackfillMissedTicks` (default `false`) opts the deployment into re-firing `OnEvent`-triggered jobs once on drift recovery. The fluent helper is `ServerApp.withBackfillMissedTicks true` (mirrored on every superset: `AIServerApp`, `RAGServerApp`, `FormsServerApp`, `SchedulingServerApp`). Operators opt in when their `OnEvent` work is safely re-entrant — idempotent inserts, dedup'd upserts, advisory side effects. Non-idempotent work should stay opt-out; the missed dispatches are the trade-off for at-most-once semantics.

**Verifying drift detection manually.** Set a breakpoint inside the scheduler's `ExecuteAsync` loop (or hit `Ctrl+Break` to pause the whole process), wait ≥120 seconds, then resume. Within a few seconds: the `HealthMonitorUI` panel shows a non-zero `tick_missed_count`, and the `_platform`-scope event log contains one `JobSchedulerTickMissed` entry whose `MissedTickCount` matches the number of minute boundaries inside the pause window. Without the pause, the counter stays at zero across an arbitrary uptime.

### Known follow-ups

- **Comprehensive `IJobScheduler` contract pack.** Phase 9b ships `IJobStore` contract coverage (10 tests) + `CronExpression` unit tests (13 tests). A scheduler-level contract pack covering Schedule validation cases, idempotency end-to-end, and dispatch-loop behaviour against a manually-driven tick is a follow-up.
- **Schedule-validation telemetry.** `ScheduleError` is returned to callers but not emitted as an audit event today. A future entry would record validation rejections separately from `JobScheduled` so admins can spot configuration drift.
- **Admin UI module.** The `JobApi` Fable.Remoting surface is shipped; a built-in `JobAdminUI` module mirroring `WebhookAdminUI` is a follow-up — list / create / cancel / disable / enable / re-fire from the SDK shell.
- **Distributed companion.** Phase 9c — Akka.NET reference companion + portability validation. Whatever shape that audit produces feeds back into the interfaces above.

## Data ingestion (Phase 10 — interface-first; connectors deferred)

The SDK ships the substrate for pulling data from external sources (BigQuery, Redshift, Athena, Synapse, REST APIs, in-memory test fakes) into team-scoped versioned data objects. The interfaces, the orchestrator, the in-memory test connector, the admin API surface, and the scheduled-ingestion handler are all shipped. The first concrete cloud connector (BigQuery) is deferred to a session with real GCP credentials — see `src/DataSources/BigQuery/README.md`.

### Activation

`ServerConfig.DataIngestion` is an opt-in DU. `NoDataIngestion` (default) skips registration entirely — no `IDataIngestor`, no `IDataSourceConfigStore`, no `_platform/data-sources/` blob layout, no `IDataIngestionApi` route. `EnabledDataIngestion` activates the substrate. Triggered refreshes through `IDataIngestionApi.TriggerRefresh` additionally require `JobScheduler = InProcessJobScheduler` (or any future distributed-scheduler companion) — without a scheduler, the ingestor still runs synchronously through `IDataIngestor.RunIngestion`, but the API path returns a clear error.

### Substrate

Five files form the SDK layer:

- `Shared/DataIngestionTypes.fs` — `DataSourceId` (= `string`), `DataSourceConfig`, `IngestionStatus`, `IngestionError` DU, `IngestionRun`, `ColumnInfo`, `TableSchema`. All Fable-compatible so the admin UI deserialises through `Fable.SimpleJson`.
- `Shared/DataIngestionApi.fs` — `IDataIngestionApi` Fable.Remoting record (List / Get / Save / Delete / TriggerRefresh / ListRecentRuns).
- `Server/IDataSource.fs` — connector contract + `DataSourceCallContext` (carries `ScopeId` + `Config` + optional pre-resolved `Credential`).
- `Server/IDataIngestor.fs` — orchestrator contract.
- `Server/IDataSourceConfigStore.fs` — config persistence contract.

Plus the implementations + integration:

- `Server/InMemoryDataSource.fs` — fake `IDataSource` (Kind = `"InMemory"`) for tests + dev harness.
- `Server/DataSourceConfigStore.fs` — blob-backed default at `_platform/data-sources/{scopeId}/configs/{sourceId}.json`.
- `Server/DataIngestor.fs` — default orchestrator. Resolves config → connector by `Kind` → credential via `ISecretStore.GetSecret(scopeId, config.CredentialKey)` → `Connect` → `Query` → `IDataObjectStore.Save(..., Versioned)` → `IngestionRun` blob + lifecycle event.
- `Server/DataIngestionJobHandler.fs` — `IJobHandler` registered under `"_platform.dataingestion.run"` so scheduled and triggered ingestion both flow through the Phase 9b scheduler.
- `Server/DataIngestionApiHandler.fs` — Fable.Remoting handler with Owner/Admin write gate.

### Credential-thunk pattern

The orchestrator pre-resolves the credential and passes it through `DataSourceCallContext.Credential`. Connectors that need a credential read it from there. Connectors that prefer to call `ISecretStore` directly (e.g., to refresh mid-call for long-running queries) get `ScopeId` + `config.CredentialKey` on the same context and call `secretStore.GetSecret` themselves. This mirrors `ClaudeAIProvider.fs:259-263`'s thunk pattern: credentials are never embedded in the persisted config, never captured at construction time, always resolved fresh — supports rotation without provider reconstruction.

### Persistence layout

```
Container: _platform
Configs:   data-sources/{scopeId}/configs/{sourceId}.json
Runs:      data-sources/{scopeId}/runs/{sourceId}/{ts}-{runId:N}.json
```

Mirrors the `webhooks/{scopeId}/...`, `jobs/{scopeId}/...`, `audit/{scopeId}/...`, `lineage/{scopeId}/...` layouts. ISO-ordered timestamp prefix on run blobs means `IDataIngestor.GetRecentRuns` is `List + sortDescending + truncate count + Download` — newest-first without loading every row.

### Result-bytes opacity

`IDataSource.Query` returns raw `byte[]`. The orchestrator writes them through `IDataObjectStore.Save(..., dataType = "data-ingestion", ..., policy = Versioned)` opaque to the storage layer. **Each refresh creates a new version** — Phase 7's `Versioned` policy preserves the prior result, never overwrites in place. Modules that read those bytes back are responsible for parsing (typically as CSV, JSON, or Parquet according to the connector's documented output).

The result `objectId` is derived as `_dataingestion__{sourceId}__{table}` (mirrors Phase 8's `ResultObjectId.make` pattern with double-underscore separators that don't collide with the `IDataObjectStore` blob path layout).

### Lifecycle events

Two event types written to `IEventStore` under `SourceModule = DataIngestor.DataIngestionSourceModule` (`"_platform.dataingestion"`):

| Event | When |
|---|---|
| `IngestionRunCompleted` | `IDataSource.Query` succeeded and `IDataObjectStore.Save` returned `Ok` |
| `IngestionRunFailed` | Any failure: config missing, connector unregistered, credential missing, `Connect`/`Query` returned `Error`, `Save` failed |

Webhook subscribers (Phase 6d) and audit consumers pick these up automatically. Serialised via `FableJsonConverter` so admin UIs deserialise through `Fable.SimpleJson` without an extra converter (canonical SDK pattern).

### Scheduled refresh

Cron-driven ingestion uses Phase 9b's `IJobScheduler` directly. Modules schedule against `JobHandlerName = "_platform.dataingestion.run"` with payload `{"sourceId": "...", "table": "..."}`. The handler:

1. Deserialises the payload (`PermanentFailure` on parse error — won't recover by retrying).
2. Calls `IDataIngestor.RunIngestion(scopeId, sourceId, table)`.
3. Maps `IngestionError` to `JobResult`:
   - `Succeeded` → `Success`.
   - `CredentialMissing` / `SchemaMismatch` → `PermanentFailure` (operator action required; retrying without intervention won't recover).
   - `SourceUnreachable` / `StorageFailure` / `UnexpectedFailure` → `TransientFailure` (rate limits, network blips — retry per `JobRetryPolicy`).

`IDataIngestionApi.TriggerRefresh` schedules a `Manual`-trigger `JobRegistration` with `ShardKey = sourceId` (so concurrent refreshes for the same source serialise on a future distributed scheduler) then immediately calls `TriggerOnce` so the admin-UI "Refresh now" runs within seconds rather than at the next minute tick.

### Team isolation (GP 4)

Every method on `IDataSourceConfigStore`, `IDataIngestor`, and `IDataIngestionApi` takes `ScopeId` (or resolves it from the caller's `AccessContext`). Cross-scope reads are structurally impossible — the blob layout's prefix means a scope-A `List` call cannot return scope-B configs. Run history is scope-isolated identically.

### What ships and what doesn't

| Concern | Status |
|---|---|
| Interface design (`IDataSource`, `IDataIngestor`, `IDataSourceConfigStore`) | Shipped |
| `DataSourceConfig` + `IngestionRun` types | Shipped |
| `IDataIngestionApi` Fable.Remoting surface | Shipped |
| Default orchestrator (`DataIngestor`) | Shipped |
| Default config store (blob-backed) | Shipped |
| In-memory connector (`InMemoryDataSource`) | Shipped (Kind = `"InMemory"`) |
| Scheduled-ingestion `IJobHandler` | Shipped |
| Owner/Admin write gate | Shipped |
| `IDataSourceContract` test pack + binding | Shipped (7 tests) |
| BigQuery connector | **Deferred** — `src/DataSources/BigQuery/README.md` is the placeholder |
| Redshift / Athena / Synapse connectors | **Deferred** — same gating as BigQuery |
| Admin UI module (Feliz client) | Shipped (Phase 10e — see below) |
| `RowsIngested` count | **Deferred** — connectors must opt in by counting rows during query streaming; the in-memory connector returns `None` |

## OAuth Authorization Code substrate + data-source admin UI (Phase 10e)

This substrate ships the foundation every future SaaS-API connector inherits — provider-agnostic OAuth Authorization Code with offline-access flow + per-Kind credential UI registry + connect / disconnect lifecycle.

### What this substrate is for

OAuth Authorization Code with offline-access is the canonical credential-minting flow for SaaS APIs that authenticate human users at consent time and then issue long-lived refresh tokens for the application: Google (GA4, Sheets, Drive), Microsoft (Graph, Azure AD), GitHub, Slack, Stripe, HubSpot, Salesforce, Dropbox, and so on. Without a substrate, every connector hand-rolls the same plumbing: state-token CSRF protection, `/authorize` and `/callback` endpoints, secret-store persistence, audit emission, RBAC gating. Phase 10e centralises the plumbing so a new SaaS connector implements two interfaces (`IDataSource` from Phase 10 + `IOAuthCredentialFlow` from this phase) and the wiring is automatic.

### Activation

OAuth substrate registration follows the data-ingestion gate: `ServerConfig.DataIngestion = EnabledDataIngestion` registers `IOAuthStateStore` + the cleanup `BackgroundService` and mounts `OAuthFlowHandler.routes`. Companion packages register their `IOAuthCredentialFlow` via DI (`services.AddSingleton<IOAuthCredentialFlow>(GoogleOAuthFlow.create ...)` from the composition root). Deployments without OAuth-flow companions get a working route table that returns `404 "OAuth flow 'X' is not registered"` to any /authorize attempt — the substrate stays cheap until a real flow is added.

### Two endpoints, one flow

```
GET /api/oauth/{flowName}/authorize?dataSourceId={id}
GET /api/oauth/{flowName}/callback?code=...&state=...
```

`/authorize` resolves the caller's `AccessContext`, RBAC-gates (Owner/Admin in `Team` / `MultiTeam` mode), looks up the data-source config from `IDataSourceConfigStore`, generates a CSRF state token + PKCE code verifier via `OAuthCrypto.generateState` / `generateCodeVerifier`, **pins the redirect URI in the state-store entry**, calls `flow.BuildAuthorizeUrl`, and 302-redirects the user-agent to the upstream provider.

`/callback` atomically `TryConsume`s the state entry (single-use, 10-min TTL), validates the flow + actor identity, exchanges the code via `flow.ExchangeCode` (passing the **byte-identical** redirect URI from the state entry — Google validates exact-match), persists the refresh token via `ISecretStore.SetSecret(scope.Container, "{flowName}-refresh-{dataSourceId}", token)`, writes a `CredentialMetadata` blob, emits an `OAuthConnected` audit event, and 302-redirects back to the admin UI with `?dataSourceConnected={id}`. Provider-side `?error=` (user cancels consent) early-bails and redirects with the upstream reason.

The byte-identity rule is load-bearing. `/authorize` computes the redirect URI from `TOOLUP_OAUTH_REDIRECT_BASE` (preferred — explicit) or the request's `Scheme + Host` (fallback — only correct behind a TLS-terminating proxy with `TrustForwardedHeaders` enabled), trims trailing `/`, and stamps the result into `OAuthFlowState.RedirectUri`. `/callback` reads the value back from the state entry rather than re-deriving — a load-balancer routing the callback to a different instance, a request that traversed a different path through the proxy fleet, or a deployment where the env var landed mid-flight all become non-issues.

### Substrate types

```fsharp
type IOAuthCredentialFlow =
    abstract Name: string                              // URL segment + secret-store + state-store key prefix
    abstract Descriptor: OAuthFlowDescriptor           // DisplayName / Scopes / HelpUrl for admin UI
    abstract BuildAuthorizeUrl:
        OAuthFlowContext * state: string * redirectUri: string -> Async<Result<string, OAuthError>>
    abstract ExchangeCode:
        OAuthFlowContext * code: string * redirectUri: string -> Async<Result<OAuthCredentials, OAuthError>>
    abstract RefreshAccessToken:
        OAuthFlowContext * refreshToken: string -> Async<Result<OAuthAccessToken, OAuthError>>
    abstract Revoke:
        OAuthFlowContext * refreshToken: string -> Async<Result<unit, OAuthError>>
```

`OAuthError` discriminates so the substrate handler maps each case to the right HTTP status (`StateMismatch → 400`, `ProviderRejected → 502`, `NetworkError → 503`, others → 500) and audit-event kind without introspecting strings. The catch-all case is named `OAuthFlowFailed` (not `UnexpectedFailure`) to dodge a same-namespace clash with `IngestionError.UnexpectedFailure` — F# inference picks the latest declaration when both are in scope and breaks `DataIngestor.fs` if you rename it.

### State store

`IOAuthStateStore` bridges the `/authorize` → `/callback` round-trip. The default `InMemoryOAuthStateStore` uses a `ConcurrentDictionary` keyed by token, with atomic `TryRemove` for read-and-delete and a 10-minute TTL enforced both on `TryConsume` (lazy eviction on read) and `Cleanup` (periodic sweep). `OAuthStateCleanupService` is a `BackgroundService` ticking every 60 seconds — frequent enough to keep the dictionary bounded under churn, infrequent enough to avoid lock contention against `Save` / `TryConsume`.

The in-memory implementation is single-instance only. Multi-instance deployments behind a load balancer that pins `/authorize` and `/callback` to different nodes will fail because the callback's node won't have the state entry. This is the same Phase 9c rule-4 deviation as `JobScheduler` / `ClientToolDispatch` / `AICancellationRegistry`; the distributed companion (Redis-backed, mirrors `src/NotificationChannels/Redis/` shape) is the migration path.

### Per-Kind credential UI registry

Each connector companion (`src/DataSources/<Provider>/`) registers a Feliz form keyed by its `Kind` discriminator at module load time:

```fsharp
DataSourceCredentialUIRegistry.register "GoogleAnalytics" (fun ctx ->
    // Render the GA4-specific credential inputs:
    //   - Client ID / Client Secret password fields
    //   - Connect button → IDataIngestionApi.BeginOAuth
    //   - Property selector populated from ListTables on connect
    GoogleAnalyticsCredentialUI.render ctx)
```

The built-in `DataIngestionUI` admin module looks up the renderer at row-expansion time. No registered renderer → "No credential UI registered for kind X. Import the matching connector companion's .Client.props" hint.

The registry is a mutable string-keyed map updated only at companion module-load time (single-threaded in the browser JS runtime). Same shape as `AuthUIProvider` — the SDK shell never imports any connector-specific type. Companion import-order in the client `.fsproj` decides which registration wins for duplicate keys.

### Credential metadata blob

`/callback` writes a `CredentialMetadata` JSON blob at `_platform/data-sources/{scopeId}/credentials/{dataSourceId}.json` carrying `FlowName` / `DataSourceId` / `ConnectedAt` / `LastError`. This is the projection the admin UI's `GetCredentialStatus` reads:

| Metadata state | `CredentialStatus` projection |
|---|---|
| Blob absent + `DataSourceConfig` absent | `NotConfigured` |
| Blob absent + `DataSourceConfig` present | `NeedsAuthorization` |
| Blob present, `LastError = None` | `Connected (ConnectedAt)` |
| Blob present, `LastError = Some reason` | `NeedsReauthorization reason` |

Refresh tokens themselves stay in `ISecretStore` and never round-trip to the client. The admin UI's `Disconnect` action calls `IDataIngestionApi.Disconnect`, which (a) best-effort `flow.Revoke(refreshToken)`, (b) deletes the refresh-token secret, (c) deletes the metadata blob, (d) emits an `OAuthDisconnected` audit event with `UpstreamRevoked: bool` recording the revocation outcome.

### Phase 9m preflight — TOOLUP_OAUTH_REDIRECT_BASE

`OAuthFlowValidator` is registered automatically (only when at least one `IOAuthCredentialFlow` is in DI — deployments without OAuth flows get no env-var pressure):

| Env var state | Validator outcome |
|---|---|
| Unset | `Warning` — fallback to per-request Scheme/Host derivation, prompt to set explicitly. |
| Set, not absolute HTTP/HTTPS URL | `Error` — startup fails. |
| Set to localhost while `Mode <> Anonymous` | `Warning` — almost certainly a dev URL leaked into production. |
| Set, absolute HTTP/HTTPS URL, non-localhost in non-Anonymous mode | `Ok`. |

This catches the most common OAuth-deploy gotcha class (operator forgets to set the env var, the deployment boots, /authorize fires from behind a TLS-terminating proxy, the consent screen opens correctly, the user clicks Allow, the callback redirects to `http://internal-pod-ip:5000/...` which the user-agent can't reach) at startup rather than at first connect attempt.

### Phase 9c portability audit

`IOAuthCredentialFlow`:

- **Rule 1 (identity by value):** `Name: string` — never a runtime handle.
- **Rule 2 (async at every boundary):** every method returns `Async<Result<_, OAuthError>>`.
- **Rule 3 (errors / retry as data):** `OAuthError` DU classifies failure modes; no callback parameters or supervision objects.
- **Rule 4 (stateless across calls):** all inputs through `OAuthFlowContext` + per-call args; `ISecretStore` reads happen inside method bodies, never captured at construction.
- **Rule 5 (no cross-shard ordering):** per-flow / per-data-source operations only.
- **Rule 6 (precision contract):** N/A — no scheduling primitives.

`IOAuthStateStore`:

- **Rule 1:** `Token: string` — base64url-encoded random bytes, never a runtime handle.
- **Rule 2:** every method returns `Async<_>`.
- **Rule 3:** N/A — no retries; state expiry is the failure mode, surfaced as `None` from `TryConsume`.
- **Rule 4:** the store IS the state; consumers are stateless across calls.
- **Rule 5:** per-token operations only.
- **Rule 6:** TTL documented in minutes; no sub-second guarantee.

Both interfaces clear all six rules. Distributed companions (Redis-backed `IOAuthStateStore`, a future Microsoft Graph `IOAuthCredentialFlow`) bind the same contract packs (`IOAuthStateStoreContract` and `IOAuthCredentialFlowContract`) without modification.

### Code dispatch summary

```
admin UI clicks "Connect"
    → IDataIngestionApi.BeginOAuth(id, flowName)
    → server validates RBAC + flow + config, returns
      "/api/oauth/{flowName}/authorize?dataSourceId={id}"
    → client window.location.assign(url)
    → server /authorize generates state + PKCE, stamps
      OAuthFlowState (ScopeId, Container, RedirectUri, ...)
      via IOAuthStateStore.Save, calls flow.BuildAuthorizeUrl,
      302 → upstream provider
    → user consents
    → upstream redirects to /callback?code=...&state=...
    → server TryConsume(state); validates FlowName + UserId
      against the entry; calls flow.ExchangeCode(code,
      entry.RedirectUri); secretStore.SetSecret(refresh-token);
      saveCredentialMetadata; auditLog.Record(OAuthConnected);
      302 → "/?dataSourceConnected={id}&flow={flowName}"
    → admin UI shows "Connected since {ConnectedAt}" pill
```

`Disconnect` is the inverse: best-effort `flow.Revoke`, `secretStore.DeleteSecret`, `deleteCredentialMetadata`, emit `OAuthDisconnected`.

## Dev diagnostics endpoint (Phase 9a)

`/dev/inspect` is a debug-only JSON endpoint (with a sibling HTML view at `/dev/inspect/html`) that surfaces the SDK's runtime composition for the *caller's* request. Built to shorten the time from "why doesn't my module appear?" / "why was this request denied?" to root cause, without grepping the composition root or piping `ILogger` output through `Select-String`.

### Activation gate

The endpoint is **runtime-gated only** by `ServerConfig.EnableDevEndpoints: bool` (default `false`). When `false` (production default), `compose` registers no routes and `/dev/inspect` returns `404` from the Giraffe terminal middleware. When `true`, the routes mount and the report is served.

A deployment enables it for itself with `{ ServerConfig.defaults with EnableDevEndpoints = true }`.

**History.** Before Phase 11.B (2026-05-08) the endpoint was double-gated by `#if DEBUG` (compile-time) plus the runtime flag. The compile-time gate was removed when ToolUp.Platform stopped carrying compile-time `#if DEBUG` blocks — the OSS SDK does not assume Debug vs Release configuration; the runtime flag is now the sole gate. The default-off posture preserves the safety property: production deployments never expose the endpoint without explicit operator opt-in, which is a deliberate config decision rather than a build-flag accident. Apps that previously relied on the compile-time safety net can re-establish it App-side by reading `#if DEBUG` in their own composition root and setting `EnableDevEndpoints` accordingly (this is what the reference `ToolupApp-Server` does).

### Sections in the report

```
{
  "Generated":     "2026-04-28T...",
  "BuildMode":     "Debug",
  "PlatformMode":  "Team",
  "Caller": {
    "UserId":          "u_abc123",
    "IsAnonymous":     false,
    "TeamId":          "t_acme",
    "Mode":            "Team",
    "StorageScope":    { "ScopeId": "...", "Container": "team-...", "Persist": true },
    "StorageScopeError": null,
    "Permissions":     { "MediaOptimisation": ["Read","Write"], "..." }
  },
  "Modules":            [{ "Name": "...", "DataTypes": [...], "DataTypeCount": 2, "HasConfigSchema": false }, ...],
  "TotalRouteHandlers": 12,
  "DataCatalog":        [{ "Id": "...", "DisplayName": "...", "HasSchema": true, "Producers": ["..."] }],
  "Services":           [{ "ServiceType": "ToolUp.Platform.IBlobStorage", "Lifetime": "Singleton", "Implementation": "..." }, ...]
}
```

`Caller` mirrors what an `/api/*` handler sees for the same request — `ScopeResolutionMiddleware` was widened to also run on `/dev/*` paths so the dev report and live request handlers see identical `AccessContext` / `StorageScope`. The `AuthEnforcementMiddleware` deliberately stays `/api/*`-only — diagnosing auth failures is the endpoint's job, so blocking unauthenticated `/dev/inspect` requests in `Team` mode would defeat the purpose. (Compile-time gating is the security boundary, not request auth.)

`Modules`, `TotalRouteHandlers`, and `Services` are captured at compose time before `builder.Build()` seals the `IServiceCollection`, then closed over by the route handler. The descriptor list contains type names only — `ServiceType`, `Lifetime`, `Implementation` (when known via `ImplementationType` or singleton `ImplementationInstance.GetType()`). No instances are dereferenced and no scoped/team singletons are leaked through reflection.

`DataCatalog` resolves `IDataCatalog` per request (Phase 7a) and walks every registered type. `HasSchema = true` means the producer published a `DataTypeSchema`; `Producers` lists every module name that registered the same `Id`.

### Team isolation (GP 4)

The endpoint emits the *caller's* `StorageScope` only — never another team's. Storage and event-store contents are never returned. The `Permissions` map in `Caller` reads the per-request `AccessContext` from DI (the same Scoped instance every API handler resolves). A user requesting `/dev/inspect` while logged into Team A cannot see Team B's data via this surface.

### Wire format

Hand-shaped DTO of primitives, strings, and lists. Every F# DU on the report path (`PlatformMode`, `ModulePermission`, scope-error sum types) is pre-mapped to its case-name as a `string` so the JSON is human-readable in `curl` / a browser, with no `{"Case": "X", "Fields": [...]}` shape. Serialisation goes through `Newtonsoft.Json + FableJsonConverter` (the SDK's canonical pattern; see "Consumer dependency contract" in the SDK README) for `option<T>` round-trip — `None` renders as `null`, `Some x` as the unwrapped value.

Both responses set `Cache-Control: no-store` so browser caches don't surprise developers with a stale view of mid-edit DI state.

### Try it out

```pwsh
# Build with Debug + flag enabled in your composition root, then:
curl http://localhost:5000/dev/inspect | jq .

# Filter for one section
curl http://localhost:5000/dev/inspect | jq '.Caller'
curl http://localhost:5000/dev/inspect | jq '.Modules[].Name'
curl http://localhost:5000/dev/inspect | jq '.Services[] | select(.ServiceType | contains("ITeamStore"))'

# Browser-friendly view
curl http://localhost:5000/dev/inspect/html > /tmp/inspect.html  # then open
# or just navigate: http://localhost:5000/dev/inspect/html
```

Authenticated modes: pass the same headers / cookies your normal `/api/*` traffic uses. The dev endpoint goes through `ScopeResolutionMiddleware` so it sees the same identity. For a JWT deployment: `curl -H "Authorization: Bearer ..." http://localhost:5000/dev/inspect`. For `HeaderAuthProvider` dev mode: `curl -H "X-User-Id: alice" http://localhost:5000/dev/inspect`.

### Known follow-ups

- **AI tools / RAG vectorisation surfaces.** An "AI tools registry (names only)" section is a planned addition. Achieving it without coupling core to `ToolUp.AI` needs a small `IDevDiagnosticsContributor` extension point that companions register against — deferred as a follow-up rather than coupling core to the AI companion. Same applies to per-module vectorisation handler counts (lives in `ToolUp.RAG`).
- **Per-module HTTP handler count.** `compose` receives a flattened `HttpHandler list` — by then the per-module breakdown is gone. The report emits a server-wide `TotalRouteHandlers` instead. Capturing per-module counts cleanly would extend `ServerApp.addModule` to track the contributing module's handler count separately; not worth the surface area for a debug-only endpoint.


## Diagnostic support bundle (Phase 9n)

`/dev/bundle` is the operator-facing companion to `/dev/inspect`. Where `/dev/inspect` answers "what is this deployment composed of *right now*" interactively, `/dev/bundle` produces a single tar archive bundling every signal a support engineer needs to triage a cross-companion incident, in one shot. The intended workflow:

```pwsh
# Compose a base64-armoured bundle suitable for pasting into a support ticket
curl https://your-app/dev/bundle | base64 > bundle.tar.b64

# Or save the raw tar for local inspection
curl -o diag.tar https://your-app/dev/bundle
tar -tvf diag.tar
```

### Activation gate

Same posture as `/dev/inspect` — **runtime-gated only** by `ServerConfig.EnableDevEndpoints: bool` (default `false`). The bundle handler ships in Release builds; the default-off config keeps the endpoint inert in production unless a deployment opts in. There is no compile-time `#if DEBUG` belt-and-suspenders — the runtime flag is the sole gate, by deliberate Phase 11.B policy (see the `/dev/inspect` history note above).

### Bundle contents

A single tar archive with eight entries:

| File | Content |
|------|---------|
| `manifest.json` | Bundle metadata — schema version, generation time, platform mode, caller (userId / scopeId), per-section sizes, audit-tail truncation summary, module / service counts. The first file an operator reads. |
| `inspect.json` | The full `/dev/inspect` JSON payload (caller, modules, data catalog, DI services, index consistency, composition seam, health checks, validators, lightweight profile, contributor panels). Re-runs the same `buildReport` path as the live endpoint. |
| `config.json` | Resolved `ServerConfig` serialised + redacted. Every property name matching `*ApiKey | *Token | *Secret | *Password` is replaced with `<redacted:length=N>`. |
| `audit-tail.jsonl` | Most-recent audit events under `SourceModule = "_platform.audit"`, newest first, one JSON record per line. Records carry the wrapping `ModuleEvent` metadata (`Id`, `OccurredAt`, `ScopeId`, `EventType`) plus the pre-redacted payload. |
| `validators.json` | Phase 9m preflight snapshot — last startup's `IConfigValidator` outcomes (name, status, message, elapsed). Empty when no validators are registered or `SkipPreflight = true`. |
| `health.json` | Live `IHealthCheck` probe results captured at bundle-build time (not a cached snapshot — fresh fan-out with each probe's declared timeout, same as `/dev/inspect`'s "Health checks" panel). |
| `version.json` | Companion + framework versions — `ToolUp.*` loaded-assembly names and versions, plus `FrameworkDescription` / `OSDescription` / `ProcessArchitecture` so a support ticket can correlate behaviour with a specific BCL / runtime / OS build. |
| `dependency-graph.json` | DI services with their declared constructor-parameter types. Each parameter is tagged `Registered: true|false` so a reader can see whether an SDK-resolved dependency is itself a known registration or an external/BCL type. |

### Redaction policy

Every byte that enters the tar passes through a JSON-tree walk against a single property-name suffix allowlist (case-insensitive): `apikey`, `token`, `secret`, `password`. The allowlist deliberately duplicates the one [`ConfigDriftDetector`](../../ToolUp.Platform.Server/Server/ConfigDriftDetector.fs) uses for its persisted startup snapshot — two consumers don't earn a shared module yet, but the small duplication makes a future "add a new suffix" touch a trivial two-edit operation.

`ServerConfig` does not currently carry secrets — secrets live in `ISecretStore`. The redaction pass is defence-in-depth against future fields plus a sanitiser for any user-supplied string that ends up in `inspect.json` / `audit-tail.jsonl`. Non-string secret-shaped values (an unlikely shape) are stringified first so the marker's size still conveys "there was something here, this big".

### Audit-trail scope

`audit-tail.jsonl` reads from `_platform` scope (always) plus the caller's resolved scope (when scope resolution succeeded). Cross-scope reads are structurally impossible per the `IAuditLog` contract — the bundle settles for the two scopes most likely to hold load-bearing rows: `_platform` for SDK-level events (drift, encryption-key ops, platform-admin grants, audit-sink lifecycle, rate-limit refusals) and the caller's scope for team-shaped events (file ops, permission changes, entity lifecycle, workflow transitions).

### 50 MiB cap + truncation

The bundle has a hard **50 MiB ceiling** (52,428,800 bytes). Every section except the audit tail is bounded by registered-data size (a typical SDK deployment's `inspect.json` is well under 1 MiB; `dependency-graph.json` scales with `Services` count). `audit-tail.jsonl` is the only unbounded section: if appending the next event would push the cumulative bundle past the cap (after reserving ~32 KiB for `manifest.json`), the tail truncates and the event-count gap is recorded in `manifest.json` as `AuditTail.Truncated = true`. The bundle always produces — a truncated bundle is more useful for a support ticket than a 0-byte failure. Lines are written newest-first, so the truncated window preserves the most-recent activity.

### Audit emission on access

Every successful `/dev/bundle` response emits a `DiagnosticBundleAccessed` audit row under `_platform` scope with reserved `SourceModule = "_platform.diagnostics"`. The payload records the caller's userId (or `_anonymous` if no identity resolved), the resolved `ScopeId`, the bundle's final size in bytes, and whether the 50 MiB cap forced truncation. The download is itself privileged — operators reading the audit trail can see who extracted a support bundle from this deployment, when, and how complete it was. Emission is best-effort; an `IAuditLog` write failure does not poison the response (the bundle still ships) but the failure is logged through the audit-log's internal `Warn` channel.

### Reproducibility

Same input state produces equivalent bundles modulo three deliberately-non-determined fields:

- The `Generated` timestamp in `manifest.json` (wall-clock at bundle build).
- The `OccurredAt` fields in `audit-tail.jsonl` (event recording time, fixed at event emission).
- The `ElapsedMs` field in `health.json` (per-probe latency for the fresh fan-out at bundle build).

Everything else — companion versions, config field shape, validator snapshot, DI service list, dependency graph — is derived from the deployment's runtime state and is reproducible across consecutive bundle pulls.

### Wire format

`Content-Type: application/x-tar`, `Cache-Control: no-store`, `Content-Disposition: attachment; filename="toolup-diagnostic-bundle-<yyyyMMdd-HHmmss>.tar"`. Tar archives use PAX format (`TarEntryFormat.Pax`) so filenames with non-ASCII characters survive (none of the eight section names need this today, but future contributors can extend without re-platforming).

### Try it out

```pwsh
# Default workflow — base64-armoured for ticket attachment
curl https://your-app/dev/bundle | base64 > bundle.tar.b64

# Local inspection
curl -o diag.tar https://your-app/dev/bundle
tar -tvf diag.tar                                   # list contents
tar -xvf diag.tar -O manifest.json | jq .           # read manifest
tar -xvf diag.tar -O audit-tail.jsonl | jq -c '.'   # stream audit events
tar -xvf diag.tar -O config.json | jq '.AuditLog'   # inspect a specific config field
```

A 404 here is the expected production behaviour — flip `EnableDevEndpoints = true` in your composition root (or temporarily via the same `TOOLUP_*` mechanism your deployment uses for `/dev/inspect`) to opt in.


---

> [← Prev: 5. Audit, Health & Metrics](05-audit-health-and-metrics.md) · [Index ↑](../TECHNICAL_GUIDE.md) · [Next: 7. Module Communication, Indexing & Portability →](07-module-communication-and-portability.md)
