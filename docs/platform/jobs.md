# Background jobs

The Platform's job scheduler runs cron-triggered, event-triggered, and manually-triggered work in the background. The default in-process implementation handles per-`JobId` ordering, retry, and dead-letter; a distributed companion (Akka.NET / Orleans / Hangfire) can replace it for multi-instance deployments.

## `IJobScheduler` substrate

```fsharp
type IJobScheduler =
    abstract Schedule: JobDefinition -> Async<Result<unit, ScheduleError>>
    abstract Unschedule: jobId: JobId -> Async<unit>
    abstract TriggerOnce: jobId: JobId -> Async<unit>
    abstract GetSchedule: jobId: JobId -> Async<JobDefinition option>
    abstract ListSchedules: scopeId: string -> Async<JobDefinition list>
    abstract GetRunHistory: jobId: JobId -> Async<JobRun list>
```

`JobDefinition`:

```fsharp
type JobDefinition = {
    JobId: JobId               // string; caller-supplied; identity by value (portability rule 1)
    HandlerName: string        // string; looked up against IJobHandler registry
    Trigger: Trigger
    Retry: JobRetryPolicy
    IdempotencyKey: IdempotencyKey option
    Precision: JobPrecision    // Second | Minute; rule 6 (precision floor)
    Payload: byte[]            // opaque to scheduler; handler interprets
    ScopeId: string
    CreatedBy: string
}

and Trigger =
    | Cron of CronExpression
    | OnEvent of SourceModule: string * EventType: string
    | Manual

and JobRetryPolicy = {
    MaxAttempts: int
    BackoffSeconds: int list
    DeadLetterAfter: TimeSpan option
}

and JobPrecision = Second | Minute
```

`IJobHandler`:

```fsharp
type IJobHandler =
    abstract HandlerName: string
    abstract Execute: ctx: JobContext -> payload: byte[] -> Async<JobResult>

and JobContext = {
    JobId: JobId
    ScopeId: string
    RunId: Guid
    Attempt: int
    Trigger: Trigger
    AccessContext: AccessContext  // synthetic — handler runs as the CreatedBy identity
    Services: IServiceProvider
}

and JobResult =
    | Succeeded
    | Failed of reason: string
    | Retry of after: TimeSpan option
```

Handlers receive all state via parameters (rule 4 — stateless between invocations). Anything cached in handler instance fields evaporates between runs. If a handler needs durable state, it persists through `IBlobStorage` / `IEntityStore` / etc.

## Default in-process scheduler

`InProcessJobScheduler` is the shipped default. Opt in via:

```fsharp
ServerConfig.JobScheduler = InProcessJobScheduler
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

`IdempotencyKey` is a caller-supplied string with a TTL. If a job is registered with the same `(JobId, IdempotencyKey)` within the TTL window, registration is a no-op (returns `Result.Ok ()`). Useful for "schedule this job, but only once per (user, action) within 5 minutes".

```fsharp
IdempotencyKey = Some { Key = "refresh-{userId}-{datasourceId}"; Ttl = TimeSpan.FromMinutes 5. }
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
type IJobApi = {
    Schedule: JobDefinition -> Async<Result<unit, ScheduleError>>
    Unschedule: JobId -> Async<unit>
    TriggerOnce: JobId -> Async<unit>
    GetSchedule: JobId -> Async<JobDefinition option>
    ListSchedules: unit -> Async<JobDefinition list>
    GetRunHistory: JobId -> Async<JobRun list>
}
```

Write paths (`Schedule` / `Unschedule` / `TriggerOnce`) are gated by `TeamRole.Owner | Admin`. `ScopeId` and `CreatedBy` on incoming `JobDefinition`s are overwritten server-side from the caller's `AccessContext` — clients can't impersonate.

## Writing a job handler

```fsharp
type MyJobHandler() =
    interface IJobHandler with
        member _.HandlerName = "my-handler"
        member _.Execute(ctx, payload) = async {
            let input = Json.deserialize<MyJobPayload> (Text.Encoding.UTF8.GetString payload)
            try
                // Do the work. Use ctx.Services to resolve dependencies.
                let blobStore = ctx.Services.GetRequiredService<IBlobStorage>()
                let! result = doWork blobStore input
                return Succeeded
            with
            | :? TransientException -> return Retry None  // backoff per policy
            | ex -> return Failed ex.Message
        }
```

Register handlers in the composition root:

```fsharp skip=fragment
ServerApp.empty
|> ServerApp.withConfig { ServerConfig.defaults with JobScheduler = InProcessJobScheduler }
|> ServerApp.withJobHandler (MyJobHandler() :> IJobHandler)
|> ...
```

The handler registry is a `Map<HandlerName, IJobHandler>`. Handler lookup at trigger time uses `JobDefinition.HandlerName`; if the lookup fails, the job is marked `Failed` immediately (no retry — the deployment can't suddenly grow a missing handler).

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
