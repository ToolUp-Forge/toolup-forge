# Background jobs

The Platform's job scheduler runs cron-triggered, event-triggered, and manually-triggered work in the background. The default in-process implementation handles per-`JobId` ordering, retry, and dead-letter; a distributed companion (Akka.NET / Orleans / Hangfire) can replace it for multi-instance deployments.

## `IJobScheduler` substrate

```fsharp
type IJobScheduler =
    // Compose-time handler registration. The async companion exists for
    // distributed companions that register over the network (rule 2).
    abstract RegisterHandler: name: string * handler: IJobHandler -> unit
    abstract RegisterHandlerAsync: name: string * handler: IJobHandler -> Async<Result<unit, string>>

    // Submit a job. The scheduler assigns the JobId — the caller supplies
    // registration-time intent only, never runtime state (rule 1). The
    // answer says whether a job was created (`Scheduled id`) or an
    // idempotency key recovered a live one (`Coalesced id`).
    abstract Schedule: registration: JobRegistration -> Async<Result<ScheduleOutcome, ScheduleError>>

    // Lifecycle. Cancel is terminal; Disable / Enable are reversible.
    // All three are idempotent and scope-qualified.
    abstract Cancel: scopeId: string * jobId: JobId -> Async<unit>
    abstract Disable: scopeId: string * jobId: JobId -> Async<unit>
    abstract Enable: scopeId: string * jobId: JobId -> Async<unit>

    // Reads. Get returns None for an unknown id rather than throwing.
    abstract Get: scopeId: string * jobId: JobId -> Async<JobDefinition option>
    abstract ListJobs: scopeId: string -> Async<JobDefinition list>
    abstract GetRecentRuns: scopeId: string * jobId: JobId * count: int -> Async<JobRun list>

    // Fire now, regardless of Trigger. Additive — a cron job stays on its
    // schedule. A typed refusal when the job is unknown, Cancelled or
    // Disabled, or the scheduler is stopping.
    abstract TriggerOnce: scopeId: string * jobId: JobId * byUserId: string -> Async<Result<unit, TriggerError>>

    // Called by the JobNotifyEventStore decorator on every matching write.
    abstract NotifyEventWritten: scopeId: string * eventType: string * eventId: Guid -> Async<unit>
```

Every method is scope-qualified: a `JobId` is only meaningful inside its `scopeId`, so the pair is
the key everywhere (rule 1 — identity by value, never a live handle).

`JobDefinition`:

```fsharp
type JobDefinition = {
    JobId: JobId                    // Guid; scheduler-generated; identity by value (portability rule 1)
    ScopeId: string
    Handler: string                 // logical name; looked up against the IJobHandler registry
    Payload: string                 // pre-serialised JSON; opaque to the scheduler
    Trigger: Trigger
    Idempotency: IdempotencyKey option
    RetryPolicy: JobRetryPolicy
    ShardKey: string option         // affinity hint for distributed impls; rule 5
    Precision: JobPrecision         // Second | Minute; rule 6 (precision floor)
    Status: JobStatus
    CreatedAt: DateTime
    CreatedBy: string               // audit only — the handler runs as a synthetic identity
    NextRunAt: DateTime option      // Some for CronTrigger; None for OnEvent / Manual
    LastRunAt: DateTime option
    LastRunStatus: JobRunStatus option
    LastRunError: string option
    ConsecutiveFailures: int
    Tags: Map<string, string>       // free-form; the scheduler ignores it
}

and Trigger =
    | CronTrigger of expression: string   // 5-field cron; validated at Schedule time
    | OnEvent of eventType: string
    | Manual

and JobRetryPolicy = {
    MaxAttempts: int                // inclusive — 5 means up to 5 dispatches
    InitialBackoff: TimeSpan
    MaxBackoff: TimeSpan
    DeadLetterDestination: string option
}

and JobPrecision = Second | Minute
```

`IJobHandler`:

```fsharp
type IJobHandler =
    abstract Execute: ctx: JobContext -> Async<JobResult>

and JobContext = {
    JobId: JobId
    ScopeId: string
    AccessContext: AccessContext   // synthetic — the job's scope, not the scheduling user's rights
    Attempt: int
    Trigger: Trigger
    TriggerSource: TriggerSource   // cron tick / matched event / explicit admin trigger
    ScheduledAt: DateTime          // when the scheduler decided the run was due
    RunningAt: DateTime            // when Execute was called
    Payload: string                // the persisted payload; handlers deserialise it themselves
    DeadLetterDestination: string option
}

and JobResult =
    | Success
    | TransientFailure of error: string
    | PermanentFailure of error: string
    | HandedOff of handle: ExternalHandle  // submitted to external compute; reconciled later
```

The handler *name* is not on the interface — it is the string the scheduler registers an implementation under, so one implementation can serve several names.

Handlers receive all run state through `JobContext` (rule 4 — stateless between invocations). Constructor-captured *dependencies* are fine; anything a handler *caches* in an instance field between runs evaporates, because a distributed implementation is free to deactivate or restart the host. If a handler needs durable state, it persists through `IBlobStorage` / `IEntityStore` / etc.

## Default in-process scheduler

`InProcessJobScheduler` is the shipped default. Opt in via:

```fsharp
let config = { ServerConfig.defaults with JobScheduler = InProcessJobScheduler }
```

Implementation: a `BackgroundService` ticking every minute aligned to wall clock. Per-`JobId` `SemaphoreSlim` for concurrent-tick safety — if a job is still running when its next tick fires, the tick is skipped (not queued). Retry loop with jittered backoff per `JobRetryPolicy`.

### Cron parser

Supports five-field expressions: `minute hour day-of-month month day-of-week`.
- `*` — any value
- Literal values (`0`, `15`, `30`, `45`)
- Lists (`0,15,30,45`)
- Step values (`*/15`)

Not yet supported:
- Ranges (`9-17`) — deferred.
- Named months / days (`MON-FRI`, `JAN-DEC`) — deferred.
- Seconds field — deferred. (And `Precision = Second` is rejected at registration via rule 6.)

For richer expressions, write a custom `IJobScheduler` impl with a more capable parser (Quartz.NET supports the full Quartz Cron format).

### `OnEvent` triggers

The `JobNotifyEventStore` decorator stacks above `HookedEventStore` so `OnEvent`-triggered jobs auto-fire on every `IEventStore.Write` matching the registered `SourceModule * EventType`. The trigger fires synchronously on the write thread — handler execution itself is async.

For a job to react to "module X published event Y", register:

```fsharp
let myJob = {
    Handler = "my-handler"
    Trigger = OnEvent "EventY"
    RetryPolicy = JobRetryPolicy.defaults
    Idempotency = None
    ShardKey = None
    Precision = Minute
    Payload = payloadJson   // pre-serialised JSON, a string
    ScopeId = scopeId       // the caller's resolved scope
    CreatedBy = userId      // audit only; the server overwrites it
    Tags = Map.empty
}

scheduler.Schedule(myJob)
```

### `Manual` triggers

`Manual`-triggered jobs sit in the registry without an automatic fire path. Trigger explicitly:

```fsharp
scheduler.TriggerOnce(scopeId, myJobId, userId)
```

`TriggerOnce` takes the same `scopeId` every other scheduler method does (a
`JobId` is only meaningful inside its scope) plus the acting user, which the
scheduler records as `TriggerSource.ScheduledManually` on the resulting run.

It refuses, starting nothing and writing no run row, with a `TriggerError`: `JobNotFound`, `JobCancelled`,
`JobDisabled` (`Disable` stops dispatch, and a manual fire is a dispatch), `SchedulerStopping` (the host is
shutting the scheduler down), `QuotaExceeded` (the quota decorator) or `DispatchRefused` (the backend would not
start it). `TriggerError.message` renders each as a sentence.

Common pattern: the data-ingestion subsystem schedules a `Manual` job on `IDataIngestionApi.TriggerRefresh` and immediately calls `TriggerOnce`.

### Idempotency

`JobDefinition.Idempotency` carries an `IdempotencyKey` — a caller-supplied string plus a TTL in
**seconds**. If a job is registered whose `Key` matches a live job in the same scope inside the TTL
window, `Schedule` answers `Coalesced` with the *existing* `JobId` and registers nothing new; a job it
created is `Scheduled`. The caller can tell the two apart, which is what keeps "submitting twice = the job
runs once" true for a caller that also fires what it schedules. Useful for "schedule this job, but only
once per (user, action) within 5 minutes".

```fsharp
let idempotency: IdempotencyKey option =
    Some { Key = "refresh-{userId}-{datasourceId}"; TtlSeconds = 300 }
```

The lookup is per-scope.

#### Schedule-then-trigger: what the key names decides what to fire

Idempotency covers `Schedule`, not what a caller does with the `JobId` it returns. `TriggerOnce` is
not idempotent either: a manual trigger is a request for a run, and a second one queues behind the
first rather than being dropped. So a caller that schedules a `Manual` job under an idempotency key
and then fires whatever `Schedule` returned runs the job again every time the key coalesces.

`Coalesced` does **not** mean "already pending". The store matches a key against when a job was
*created*, inside the TTL, whatever has happened to the job since, so a coalesced job may be
pending, running, or long finished. Which fire is right depends on what the key names:

- **The key names the cause** — the change, the request, the batch item, so that two registrations
  with one key are the same work. Fire only a `Scheduled` job; a coalesced one was fired when it was
  created, and firing it again runs the same work twice:

```fsharp skip=fragment
match! scheduler.Schedule registration with
| Ok(Scheduled jobId) -> do! fire jobId
| Ok(Coalesced _) -> ()   // this cause again: its job was fired when it was created
| Error e -> log e
```

- **The key names a subject that is worked repeatedly** — one job per fact, one per report, reused
  as a vessel. A coalesced job may have run already for an earlier cause, and firing it is how the
  new cause gets run. Fire both. On the in-process scheduler one job per subject also serialises the
  subject's runs: a manual fire waits for the job's dispatch lease, and `ShardKey` is ignored, so a
  job per cause would let two runs of one subject overlap and the older finish last.

Every call site in the SDK that schedules and then fires (Phase 998):

| Site | Key | Fires | Pin |
|---|---|---|---|
| `GroundedNarrativeTrigger.enqueueFor` (data arrival) | run + change | only `Scheduled` | `GroundedNarrativeTests` — "double run, first site": a replayed change runs the narrative once on the in-process scheduler; a different change runs it again |
| `RecomputeJobHandler.applyPolicies` (an `Eager` recompute) | fact (vessel) | both | `ReactiveRecomputeComposeTests` — "schedule-then-trigger, second site": a different change inside the TTL fires the fact's one job again |
| `NarrativePromptHandler` regenerate on demand | run (vessel) | both | — an explicit "regenerate now" |
| `DataSubjectRequestApiHandler` DSR export | request id | both | — |
| `ModelFitJobHandler` batch item | batch + index | both | — |
| `TenantLifecycleAggregator` lifecycle phase | phase + scope | both | — |
| `DerivativeJobs` derivative render | content hash + derivative | both | — the handler re-checks its cache, so a second run renders nothing |
| `JsonRpcPeerHost`, `ModelExecutionPeerContract`, `DataIngestionApiHandler`, `JobSchedulerBuildOrchestrator`, `IngestionService`, `ReportingAITools` | none | — | a registration without a key never coalesces |

The recompute's vessel has one known cost: a replay of the *same* change fires the fact's job again,
and the recompute runs a second time. It asserts an identical tuple, so `Assert` is a no-op and no
data object changes, which leaves the narrative trigger nothing to react to: one data-arrival change
still yields one narrative run. The four cause-keyed rows that fire both re-run their work on a
replayed request inside the TTL, which is also what lets a request re-run a job whose earlier run
failed. Closing that without losing the retry is a scheduler-contract change — a trigger absorbed by
a run of the same job that has not yet started — and is not made here.

### Lifecycle events

Five events emit to `IEventStore` under `SourceModule = "_platform.jobs"`:

- `JobRegistered` — `Schedule` returned `Ok`.
- `JobTriggered` — handler invocation started.
- `JobSucceeded` — handler returned `Succeeded`.
- `JobFailed` — handler returned `Failed`; will retry if attempts remain.
- `JobDeadLettered` — final failure after retries exhausted. Also triggers a `SystemMessage`-Warning notification to scope admins.

These events feed the audit log. Operators query the audit trail for per-job history.

A sixth type, `JobProgressCheckpoint`, joins them under the same `SourceModule` when a deployment opts into [progress checkpoints](external-compute.md#progress-checkpoints) — deliberately the same stream, so one `ReadBySource` returns a run's whole story rather than two a reader has to join. It is emitted only for checkpoints the reporter marked `Durable = true` and for the terminal one; a live progress bar rides `INotificationChannel` instead and costs no blob write.

### Progress checkpoints

A handler with a long body reports intermediate progress through `ctx.Progress`:

```fsharp
async {
    // … part-way through the handler's `Execute` body …
    do! ctx.Progress.Report(ProgressCheckpoint.create (Some 0.37) "materialising embeddings")
}
```

Off by default (`ServerConfig.JobProgress = NoJobProgress`), in which case the call is a no-op costing one interface dispatch (GP 13). The checkpoint model, the reserved `_platform.jobs.progress` notification key, and the coalescing rule that never sheds the terminal checkpoint are documented in [`external-compute.md`](external-compute.md#progress-checkpoints), alongside the reconciliation poll that gives externally-run jobs progress with no handler code.

## `JobApi` ToolUp.Remoting surface

When the scheduler is enabled, the SDK auto-injects `JobApi`:

```fsharp
type JobApi = {
    // Reads — available to any team member.
    ListJobs: unit -> Async<JobDefinition list>
    GetJob: Guid -> Async<JobDefinition option>
    GetRecentRuns: Guid * int -> Async<JobRun list>   // count capped server-side at 50

    // Writes.
    Schedule: JobRegistration -> Async<Result<Guid, ScheduleError>>
    Cancel: Guid -> Async<Result<unit, string>>
    Disable: Guid -> Async<Result<unit, string>>
    Enable: Guid -> Async<Result<unit, string>>
    TriggerOnce: Guid -> Async<Result<unit, string>>
}
```

No `scopeId` parameter appears on the wire: the handler resolves the caller's scope from their
`AccessContext` and passes it to `IJobScheduler` itself, which is why the remoting record is the
scheduler interface with that argument removed rather than a different shape.

Write paths (`Schedule` / `Cancel` / `Disable` / `Enable` / `TriggerOnce`) are gated by `TeamRole.Owner | Admin` in `Team` / `MultiTeam` mode; other modes are ungated, since a single-user deployment owns its own scope. `ScopeId` and `CreatedBy` on an incoming `JobRegistration` are overwritten server-side from the caller's `AccessContext` — clients can't impersonate.

## Writing a job handler

`IJobHandler` is a single method — `Execute: ctx: JobContext -> Async<JobResult>`. The handler name
is *not* on the interface: it is the string the scheduler registers the implementation under, so one
implementation can serve several names. Dependencies are captured when the handler is built at
compose time; `JobContext` carries the run's state (payload, scope, attempt, trigger), never a
service provider.

**A platform job is written as a `RunJob<'Req>`** — a name, a parse and a run — and
`RunJob.handler` turns it into the `IJobHandler`. The handler decides the one question every job
used to decide for itself: a payload that does not parse is `PermanentFailure` (the same bytes will
not parse on the next attempt), a run that returns `RunFailure.Terminal` is `PermanentFailure`, and
a run that returns `RunFailure.Retryable` is `TransientFailure`, which the scheduler's retry policy
takes from there. `RunJob.ofPayload` parses with `JobPayload`, the SDK's one job-payload codec (the
standard `FableConverters` JSON); write the payload you schedule with `JobPayload.serialise`.

```fsharp
let myJob: RunJob<MyJobPayload> =
    RunJob.ofPayload "my-handler" (fun _ctx input -> async {
        try
            do! doWork input
            return Ok()
        with ex ->
            return Error(RunFailure.Retryable ex.Message) // retried per RetryPolicy
    })

let myHandler (logger: ILogger) : IJobHandler = RunJob.handler logger myJob
```

Every job in the grounding and modelling plane is written this way — the fit, batch-item, scoring,
evaluation, re-evaluation sweep, promotion, dataset re-vintage and fact recompute jobs — and each
keeps its terminal-versus-retryable split as one `failureOf` beside it. Their handler names never
change: they are persisted on every queued job. For the kinds of run a scheduler executes,
`RunKind.handlerName` is the source of the name (`RunKind.ModelFit` →
`_platform.modelfit.run`, `RunKind.Evaluation` → `_platform.modeleval.run`,
`RunKind.PromotionDecision` → `_platform.modelpromotion.evaluate`, `RunKind.Revintage` →
`_platform.dataset.revintage`); a kind that runs inline inside another run has none.

Implementing the interface by hand is still legal, and is the shape for a handler that returns a
`JobResult` `RunJob` does not produce (handing off to an external backend, say):

```fsharp
type MyJobHandler() =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            // A malformed payload will not recover on retry — terminal, not transient.
            match tryParsePayload ctx.Payload with
            | Error e -> return PermanentFailure $"malformed MyJobPayload: {e}"
            | Ok input ->
                try
                    do! doWork input
                    return Success
                with ex ->
                    return TransientFailure ex.Message  // retried per RetryPolicy
        }
```

Declare handlers on the module that owns them; the composition root only has to enable the scheduler:

```fsharp
let myModule =
    ServerModule.create "my-module"
    |> ServerModule.withJobHandler ("my-handler", MyJobHandler() :> IJobHandler, Manual)

ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with JobScheduler = InProcessJobScheduler }
|> ServerApp.addModule myModule
|> ServerApp.run
```

The handler registry is keyed by the registered name. Handler lookup at trigger time uses `JobDefinition.Handler`; if the lookup fails, the job is marked failed immediately (no retry — the deployment can't suddenly grow a missing handler).

## Common patterns

### Daily summary email

```fsharp
let summaryJob = {
    Handler = "summary-email"
    Trigger = CronTrigger "0 8 * * *"  // 08:00 UTC every day
    RetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromMinutes 1.0
        MaxBackoff = TimeSpan.FromMinutes 15.0
        DeadLetterDestination = None
    }
    Idempotency = None
    ShardKey = None
    Precision = Minute
    Payload = serialiseTeamPayload teamId   // Payload is pre-serialised JSON, a string
    ScopeId = scopeId
    CreatedBy = userId
    Tags = Map.empty
}
```

The handler resolves recipients via `ITeamStore`, builds a summary via the relevant module's API, and publishes a `TransactionalEmail` notification.

### On-event index refresh

```fsharp
let reindexJob = {
    Handler = "reindex-handler"
    Trigger = OnEvent "DocumentUploaded"
    RetryPolicy = JobRetryPolicy.defaults
    Idempotency = None
    ShardKey = None
    Precision = Minute
    Payload = ""  // empty; handler reads from the event store
    ScopeId = scopeId
    CreatedBy = userId
    Tags = Map.empty
}
```

The handler reads recent `DocumentUploaded` events from `IEventStore` and chunks + embeds the new documents.

### Stale-record cleanup

```fsharp
let cleanupJob = {
    Handler = "stale-cleanup"
    Trigger = CronTrigger "0 3 * * 0"  // 03:00 UTC every Sunday
    RetryPolicy = { JobRetryPolicy.defaults with MaxAttempts = 1 }
    Idempotency = None
    ShardKey = None
    Precision = Minute
    Payload = payloadJson
    ScopeId = scopeId
    CreatedBy = userId
    Tags = Map.empty
}
```

The handler walks `IEntityStore` for records older than N days and soft-deletes them.

## Data ingestion handler (built-in)

The data-ingestion subsystem registers `DataIngestionJobHandler` with `HandlerName = "_platform.dataingestion.run"`. Triggered + scheduled `IDataIngestor.Run` calls flow through this handler — refresh on schedule, refresh on demand, refresh on event, all through the same machinery.

```fsharp
async {
    // Triggered refresh of ONE table — the argument is a tuple of
    // (data-source id, table name).
    let! result = dataIngestionApi.TriggerRefresh(datasourceId, "orders")
    // Internally schedules a Manual job + calls TriggerOnce; `result` is
    // `Ok jobId` for the attempt it queued.
    return result
}
```

### Declared data-source deliveries are a plan (Phase 1095)

A data source a deployment declares with a delivery (`ScheduledPull` on a cadence, or `Watched` at a location) is
scheduled at startup by `DeclaredDelivery.withDeclaredDataSources` (`ToolUp.DataSources.Common`). The startup seeding
is three steps, and only the last one has an effect:

1. **`DeclaredDelivery.observe`** reads what the world answers: each principal's resolution, each watched location
   (through the connector's declared `Capabilities.Watchable`), the ledger of where sources were seeded, the live
   declared jobs, and the configs already seeded.
2. **`DeclaredDelivery.plan declared observed`** is pure. It runs every check — shape, principal, persistent scope,
   location, and whether a kept job can be re-scoped — and returns either every refusal or a `DeliveryPlan`:
   `Seed` (configs to write), `Register` (jobs to schedule, one per pulled table or one watch, each keyed
   `declared-data-delivery-{sourceId}-{jobKey}@{cadence}`), `Restamp` (kept jobs whose carried scope is re-issued),
   `Cancel`, `Retire` (configs to delete), and the ledger before and after. A plan over an unchanged declaration and
   ledger registers, cancels and retires nothing.
3. **`DeclaredDelivery.execute`** is the one effectful step: it performs `Seed`, `Register`, `Restamp`, `Cancel` and
   `Retire` in that order, then writes the ledger if it changed. A step that fails is a `SeedFailed` naming its
   source; that source keeps its old rows and retires nothing, so the next start finishes the job.

`DeclaredDelivery.seed` is the three in order, so a refusal the plan can see never leaves another source half-seeded,
and a test (or an operator tool) can inspect the plan without running it.

## Limits

### Single-instance default

`InProcessJobScheduler` lives in one process. Multi-instance deployments need a distributed companion — without one, two app nodes would both fire the same cron tick, leading to double execution. The architectural plan is for an Akka.NET-backed companion at `src/JobScheduler/Akka/` (reserved directory; impl deferred). Any companion satisfies the six portability rules and passes `IJobSchedulerContract` (15 tests).

### Minute precision

`Precision = Second` is rejected at registration. The default tick is wall-clock-aligned every minute; sub-minute precision would require a different scheduling architecture. Custom impls can honour sub-second; the SDK floor is minute.

### No cross-shard ordering

Jobs with the same `JobId` execute in order (rule 5). Across different `JobId`s no ordering promise exists. Don't write Job B that depends on Job A's completion if they have different `JobId`s — use `OnEvent` to chain instead.

### Cron expressions are limited

Five-field, `*` / values / commas / `*/N`. Not POSIX cron, not Quartz cron. For richer expressions, write a custom scheduler.

## Configuration

```fsharp
let config = {
    ServerConfig.defaults with
        // NoJobScheduler (the default) | InProcessJobScheduler
        JobScheduler = InProcessJobScheduler
        // NoJobProgress (the default) | EnabledJobProgress
        JobProgress = EnabledJobProgress
}
```

Environment variables:
- `TOOLUP_JOBS_ENABLED=1` — the reference deployment reads this and sets `JobScheduler = InProcessJobScheduler` accordingly.

Health probe:
- `JobSchedulerHealth` — verifies the background service is running. Auto-registered when the scheduler is enabled.

Audit emission:
- Five lifecycle events under `_platform.jobs` (above), plus `JobProgressCheckpoint` when progress is enabled. Replicated by audit sinks for compliance retention.

## Distributed companion roadmap

The single-instance limitation is the largest gap in the SDK's production story. A distributed companion (Akka.NET or Orleans is the strawman) is reserved at `src/JobScheduler/Akka/` and tracked. The contract test pack (`IJobSchedulerContract`) is the conformance bar — a passing impl is a drop-in replacement.

Until that ships, multi-instance deployments either pin the scheduler to one designated leader node, or use an external scheduler (cron + k8s CronJob + a small REST endpoint that calls `TriggerOnce`).
