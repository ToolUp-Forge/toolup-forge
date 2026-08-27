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
    // registration-time intent only, never runtime state (rule 1).
    abstract Schedule: registration: JobRegistration -> Async<Result<JobId, ScheduleError>>

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
    // schedule. Error when the job is unknown or Cancelled.
    abstract TriggerOnce: scopeId: string * jobId: JobId * byUserId: string -> Async<Result<unit, string>>

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

```fsharp skip=fragment
let myJob = {
    JobId = "react-to-y"
    HandlerName = "my-handler"
    Trigger = OnEvent("MyModule", "EventY")
    Retry = JobRetryPolicy.defaults
    IdempotencyKey = None
    Precision = Minute
    Payload = ...
    ScopeId = ...
    CreatedBy = ...
}
scheduler.Schedule(myJob)
```

### `Manual` triggers

`Manual`-triggered jobs sit in the registry without an automatic fire path. Trigger explicitly:

```fsharp skip=fragment
scheduler.TriggerOnce(myJobId)
```

Common pattern: the data-ingestion subsystem schedules a `Manual` job on `IDataIngestionApi.TriggerRefresh` and immediately calls `TriggerOnce`.

### Idempotency

`JobDefinition.Idempotency` carries an `IdempotencyKey` — a caller-supplied string plus a TTL in
**seconds**. If a job is registered whose `Key` matches a live job in the same scope inside the TTL
window, `Schedule` returns the *existing* `JobId` and registers nothing new. The caller cannot tell
from the return value whether the job is brand-new or recovered — by design: the contract guarantees
"submitting twice = the job runs once". Useful for "schedule this job, but only once per (user,
action) within 5 minutes".

```fsharp
let idempotency: IdempotencyKey option =
    Some { Key = "refresh-{userId}-{datasourceId}"; TtlSeconds = 300 }
```

The lookup is per-scope.

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

```fsharp skip=fragment
do! ctx.Progress.Report(ProgressCheckpoint.create (Some 0.37) "materialising embeddings")
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
implementation can serve several names. Dependencies are captured by the handler's **constructor** at
compose time; `JobContext` carries the run's state (payload, scope, attempt, trigger), never a
service provider.

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

```fsharp skip=fragment
ServerModule.create "my-module"
|> ServerModule.withJobHandler ("my-handler", MyJobHandler() :> IJobHandler, Manual)
|> ...

ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with JobScheduler = InProcessJobScheduler }
|> ServerApp.addModule myModule
|> ...
```

The handler registry is keyed by the registered name. Handler lookup at trigger time uses `JobDefinition.Handler`; if the lookup fails, the job is marked failed immediately (no retry — the deployment can't suddenly grow a missing handler).

## Common patterns

### Daily summary email

```fsharp skip=fragment
let summaryJob = {
    JobId = "daily-summary"
    HandlerName = "summary-email"
    Trigger = Cron (CronExpression.parse "0 8 * * *")  // 08:00 UTC every day
    Retry = { MaxAttempts = 3; BackoffSeconds = [60; 300; 900]; DeadLetterAfter = None }
    IdempotencyKey = None
    Precision = Minute
    Payload = Json.serialize { TeamId = teamId } |> Encoding.UTF8.GetBytes
    ScopeId = ...
    CreatedBy = ...
}
```

The handler resolves recipients via `ITeamStore`, builds a summary via the relevant module's API, and publishes a `TransactionalEmail` notification.

### On-event index refresh

```fsharp skip=fragment
let reindexJob = {
    JobId = "reindex-on-document-upload"
    HandlerName = "reindex-handler"
    Trigger = OnEvent("KnowledgeBase", "DocumentUploaded")
    Retry = JobRetryPolicy.defaults
    IdempotencyKey = None
    Precision = Minute
    Payload = ...  // empty; handler reads from the event store
    ScopeId = ...
    CreatedBy = ...
}
```

The handler reads recent `DocumentUploaded` events from `IEventStore` and chunks + embeds the new documents.

### Stale-record cleanup

```fsharp skip=fragment
let cleanupJob = {
    JobId = "cleanup-stale"
    HandlerName = "stale-cleanup"
    Trigger = Cron (CronExpression.parse "0 3 * * 0")  // 03:00 UTC every Sunday
    Retry = JobRetryPolicy.singleAttempt
    IdempotencyKey = None
    Precision = Minute
    Payload = ...
    ScopeId = ...
    CreatedBy = ...
}
```

The handler walks `IEntityStore` for records older than N days and soft-deletes them.

## Data ingestion handler (built-in)

The data-ingestion subsystem registers `DataIngestionJobHandler` with `HandlerName = "_platform.dataingestion.run"`. Triggered + scheduled `IDataIngestor.Run` calls flow through this handler — refresh on schedule, refresh on demand, refresh on event, all through the same machinery.

```fsharp skip=fragment
// Triggered refresh:
let! _ = dataIngestionApi.TriggerRefresh datasourceId
// Internally schedules a Manual job + calls TriggerOnce.
```

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

```fsharp skip=fragment
ServerConfig.JobScheduler = NoJobScheduler | InProcessJobScheduler
ServerConfig.JobProgress = NoJobProgress | EnabledJobProgress
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
