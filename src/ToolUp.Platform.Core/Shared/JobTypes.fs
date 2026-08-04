// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Background job substrate ────────────────────────────────────────
//
// Shared-layer types for the SDK's background-job substrate. Lives in
// the Fable-compatible `Shared/` layer so admin-UI client code can
// render the same `JobDefinition` / `JobRun` shapes the server
// persists, and so module authors can describe job schedules from any
// project that depends on `ToolUp.Platform`.
//
// **Design constraint — the portability contract is non-negotiable.**
// Every type below was shaped against the six rules so a future
// distributed companion (Akka.NET, Microsoft Orleans, Hangfire, cloud-
// managed) can implement the contract without retrofitting:
//
//   1. Identity by value. `JobId` is a `Guid`, never an `IActorRef` /
//      `IGrainReference`. Cancellation, status lookup, introspection
//      all happen by `JobId`.
//   2. Async at every boundary. (Enforced on `IJobScheduler` /
//      `IJobStore` / `IJobHandler` — see those files.)
//   3. Retry / supervision as data. `RetryPolicy` is a record carrying
//      `MaxAttempts` / backoff / dead-letter destination — no
//      callbacks, no supervision strategy types.
//   4. Stateless handlers between invocations. `JobContext` carries
//      everything a handler needs (`AccessContext`, attempt, payload,
//      trigger metadata) so the handler can run on any node and
//      survive Orleans grain deactivation / Akka.Persistence restart.
//   5. No cross-shard ordering promises. `JobDefinition.ShardKey`
//      enables affinity routing in distributed implementations;
//      ordering guarantees are limited to within a single key.
//   6. Precision declared at the lower bound. `Precision` is part of
//      `JobDefinition`; implementations that cannot honour the
//      requested granularity must reject at registration time, not
//      silently at execution.

/// Stable job identifier, value-typed (portability rule 1). Generated
/// by the scheduler at registration time and returned to the caller
/// for future cancellation / status lookups.
type JobId = Guid

/// Scheduling-granularity contract (portability rule 6).
/// Implementations that cannot honour the requested precision MUST
/// reject the `JobDefinition` at registration time rather than
/// silently delaying dispatch — `Orleans Reminders`, for instance, can
/// promise minute-level only and would reject a `Second` job at
/// `Schedule`.
type JobPrecision =
    /// Sub-minute granularity. The in-process default polls once per
    /// minute, so it does NOT support `Second` precision today (it
    /// rejects at registration via `IJobScheduler.Schedule`). Future
    /// distributed companions or a sub-minute hosted-service variant
    /// can opt in.
    | Second
    /// Minute granularity. The in-process default supports this.
    /// Distributed companions (Orleans Reminders, Hangfire's per-
    /// minute scheduler) support this.
    | Minute

/// Idempotency token. When a `JobDefinition` carries `Some` and the
/// store already holds an unfinished or recently-completed job whose
/// `Key` matches and whose creation timestamp is within `TtlSeconds`
/// of `now`, `Schedule` returns the existing `JobId` and does NOT
/// register a new job. This makes safe to re-issue the same scheduling
/// call after a transient failure without producing duplicate work.
///
/// Portability rule 1 / 4: matching is by value (`Key` string,
/// `TtlSeconds` int) so a distributed implementation can compute the
/// same lookup without sharing a process-local hash.
type IdempotencyKey = { Key: string; TtlSeconds: int }

/// Trigger discriminator. `CronTrigger` carries a 5-field cron string
/// (`Minute Hour DayOfMonth Month DayOfWeek`); `OnEvent` fires when
/// any `IEventStore` write matches the named `EventType`; `Manual`
/// jobs only run when `IJobScheduler.TriggerOnce` is called explicitly.
///
/// `Cron`-triggered jobs carry their string verbatim — the scheduler
/// validates it at registration via `CronExpression.tryParse` and
/// rejects with `ScheduleError.InvalidCron` rather than failing
/// silently at the next tick.
type Trigger =
    /// Standard 5-field cron expression. Supported syntax: `*` (any),
    /// integer values, comma lists (`1,5,9`), and step values
    /// (`*/5`). Ranges and named months / days are NOT supported in
    /// the in-process default (planned follow-up). Validation is at
    /// `Schedule` time — invalid expressions are rejected, never
    /// silently deferred.
    | CronTrigger of expression: string
    /// Fires whenever a `ModuleEvent` with this `EventType` is
    /// written to `IEventStore` in the same scope. The handler
    /// receives the originating event's `Id` in `JobContext.TriggerInfo`.
    | OnEvent of eventType: string
    /// Never fires automatically. The job sits in the store until
    /// an admin or another module calls `IJobScheduler.TriggerOnce`
    /// to dispatch it.
    | Manual

/// Retry policy as data (portability rule 3). Distributed
/// implementations read these fields and map them onto their native
/// retry mechanism (Akka's `BackoffSupervisor`, Hangfire's
/// `AutomaticRetryAttribute`, Orleans' built-in retries) — no
/// `OnFailure` callback, no supervision strategy object that would
/// couple the contract to a specific runtime.
///
/// Backoff is exponential with `min(InitialBackoff * 2^(attempt-1), MaxBackoff)`.
/// `MaxAttempts` is inclusive — a value of 5 means up to 5 dispatches
/// before dead-lettering.
type JobRetryPolicy = {
    MaxAttempts: int
    InitialBackoff: TimeSpan
    MaxBackoff: TimeSpan
    /// Optional dead-letter destination — currently a free-form
    /// string identifying a downstream queue / topic / log target.
    /// `None` means "log + audit + notification only", which is the
    /// SDK default. Companions can interpret the string against
    /// their own conventions (e.g. an Akka companion routes to a
    /// dead-letter actor by name).
    DeadLetterDestination: string option
}

module JobRetryPolicy =
    /// SDK defaults: 3 attempts, 30s initial backoff, 30min cap, no
    /// dead-letter destination. Tuned for typical analytical jobs
    /// that fail because of transient resource pressure (DB lock,
    /// rate-limited upstream) and recover inside the half-hour
    /// window.
    let defaults: JobRetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromSeconds 30.0
        MaxBackoff = TimeSpan.FromMinutes 30.0
        DeadLetterDestination = None
    }

    /// Compute the delay before attempt `attempt` (1-indexed). Attempt
    /// 1 runs immediately — `delayFor 1` returns `TimeSpan.Zero`.
    /// Attempt 2 onwards uses exponential backoff capped at
    /// `MaxBackoff`. Mirrors `WebhookRetryPolicy.delayFor` so the two
    /// retry paths feel identical to operators.
    let delayFor (policy: JobRetryPolicy) (attempt: int) : TimeSpan =
        if attempt <= 1 then
            TimeSpan.Zero
        else
            let exponent = float (attempt - 1)
            let raw = policy.InitialBackoff.TotalMilliseconds * (2.0 ** exponent)
            let capped = min raw policy.MaxBackoff.TotalMilliseconds
            TimeSpan.FromMilliseconds capped

/// Lifecycle status of a single execution attempt. `JobRun.Status`
/// is one of these on each row in the run history.
type JobRunStatus =
    /// Queued but not yet picked up by a worker. Used by the in-
    /// process scheduler between detect-due and dispatch.
    | Pending
    /// Currently executing. A handler that takes longer than
    /// `RetryPolicy.MaxBackoff` before completing remains in this
    /// state until it returns.
    | Running
    /// Handler returned `JobResult.Success`. Terminal.
    | Succeeded
    /// Handler returned `JobResult.Failure` or threw, AND retries
    /// remain. Triggers another dispatch after backoff.
    | Failed
    /// Handler exhausted `RetryPolicy.MaxAttempts` consecutive
    /// failures. Terminal — emits a `JobDeadLettered` audit event
    /// and a `SystemMessage` notification at `Warning` severity.
    | DeadLettered

    // ─── Phase 319 — external hand-off states ────────────────────
    //
    // **New cases are APPENDED, deliberately.** `JobRunStatus` is a
    // persisted DU (`JobRun.Status`, `JobDefinition.LastRunStatus`)
    // written through `FableConverters`. Inserting a case mid-union
    // shifts every later case's `Tag`, so a representation that keys
    // on tag ordinal rather than case name would silently re-read
    // every historical `Failed` row as `DeadLettered`. Appending is
    // correct under BOTH representations, which is the only property
    // worth having here (GP 11).

    /// Phase 319 — the handler returned `JobResult.HandedOff` and the
    /// work is now running on an external compute backend. **Not
    /// terminal, and not occupying a scheduler execution slot**: the
    /// dispatch lease was released the moment the handler returned, so
    /// this run costs nothing while it waits. The scheduler's
    /// reconciliation pass polls the persisted
    /// `JobRun.ExternalHandle` on each tick and drives the run to a
    /// terminal state (`Succeeded` / `Failed` / `DeadLettered` /
    /// `Cancelled`).
    | AwaitingExternal

    /// Phase 319 — terminal cancellation of an externally-run attempt:
    /// the backend reported `ExternalOutcome.Cancelled` (an operator
    /// action, a pre-emption, a backend-side timeout). Distinct from
    /// `DeadLettered` because a cancellation is not a failure — it
    /// emits no dead-letter notification and does NOT bump
    /// `ConsecutiveFailures`.
    ///
    /// **Named `ExternallyCancelled`, not `Cancelled`, on purpose.**
    /// `JobStatus.Cancelled` already exists in this namespace and is
    /// declared *after* this union, so a bare `Cancelled` here would be
    /// shadowed at every unqualified call site — silently binding the
    /// job-definition status where an attempt status was meant. The
    /// name also carries the distinction that matters: `JobStatus`
    /// cancels the *definition*, this cancels one *attempt*.
    | ExternallyCancelled

/// Top-level job state. `Active` is the steady state; `Disabled`
/// stops dispatch without deleting (admin can re-enable);
/// `Cancelled` is a terminal "do not run" marker that the cleanup
/// task can prune later.
type JobStatus =
    | Active
    | Disabled
    | Cancelled

/// Persisted job definition. Owns the schedule (`Trigger`),
/// dispatch policy (`RetryPolicy`, `Idempotency`), and current
/// runtime state (`NextRunAt`, `LastRunStatus`, `ConsecutiveFailures`).
///
/// One blob per job under `_platform/jobs/{scopeId}/{jobId}.json`,
/// matching the `_platform/audit/`, `_platform/events/`,
/// `_platform/webhooks/` precedents. All mutation goes through
/// `IJobStore.Update`; the scheduler itself never mutates the in-
/// memory record outside the store boundary.
type JobDefinition = {
    JobId: JobId
    ScopeId: string
    /// Logical handler name registered with the scheduler at compose
    /// time (`IJobScheduler.RegisterHandler`). The scheduler looks
    /// the implementation up by name on every dispatch — handlers
    /// live in DI / module registration, not in the persisted record.
    Handler: string
    /// Pre-serialised payload string. Modules serialise to JSON
    /// before scheduling and re-deserialise inside their handler
    /// — keeps the store free of `obj`-typed columns and makes the
    /// payload Fable-compatible. The SDK ships no `obj`-blob path.
    Payload: string
    Trigger: Trigger
    Idempotency: IdempotencyKey option
    RetryPolicy: JobRetryPolicy
    /// Affinity hint for distributed implementations (portability
    /// rule 5). The in-process default ignores it; an Akka cluster-
    /// sharded scheduler routes jobs with the same `ShardKey` to
    /// the same node so they execute serially. Ordering guarantees
    /// are limited to within a single key.
    ShardKey: string option
    Precision: JobPrecision
    Status: JobStatus
    CreatedAt: DateTime
    /// `userId` of the principal that called `Schedule`. Audit and
    /// admin-UI display only — at execution time the handler runs
    /// with a system-synthesised `AccessContext` for the job's
    /// scope, not with this user's permissions (required for
    /// cron-driven jobs that run when no user is online).
    CreatedBy: string
    /// Computed from `Trigger`. `Some t` for `CronTrigger`,
    /// `None` for `OnEvent` / `Manual` (event-driven and manual
    /// jobs have no schedulable advance time).
    NextRunAt: DateTime option
    LastRunAt: DateTime option
    LastRunStatus: JobRunStatus option
    LastRunError: string option
    /// Bumped on every `DeadLettered` outcome; reset to `0` on
    /// `Succeeded`. `IJobScheduler` honours this for auto-disable
    /// the same way `WebhookDispatcher` does for subscriptions
    /// (`DisableAfterConsecutiveFailures` is a planned follow-up,
    /// not in the first cut).
    ConsecutiveFailures: int
    /// Free-form metadata the scheduler ignores; admin UIs and
    /// downstream telemetry can read it. E.g. `["origin", "module-X"]`,
    /// `["replay-of", "<old jobId>"]`.
    Tags: Map<string, string>
}

/// Per-attempt run history row. One per dispatch attempt — a job that
/// succeeds on the first try produces one `JobRun`, a job that
/// dead-letters after 3 attempts produces 3 (Pending → Running →
/// Failed, then a 4th `DeadLettered`).
///
/// Persisted alongside the definition under
/// `_platform/jobs/{scopeId}/{jobId}/runs/{runId}.json`.
type JobRun = {
    RunId: Guid
    JobId: JobId
    ScopeId: string
    Attempt: int
    StartedAt: DateTime
    CompletedAt: DateTime option
    Status: JobRunStatus
    Error: string option
    DurationMs: int64 option
    /// Phase 319 — the external-compute handle this attempt handed off
    /// to, `Some` only while `Status = AwaitingExternal` and on the
    /// terminal row that reconciliation wrote for it. `None` for every
    /// ordinary in-process attempt.
    ///
    /// **The handle is persisted here, and that is the whole point of
    /// the field.** Restart durability is not a nice-to-have for this
    /// state: the run is waiting on work that outlives the process, so
    /// a handle held only in memory would strand the external job on
    /// the first deploy — the scheduler would have no way to ask the
    /// backend what became of it, and no way to complete or fail the
    /// run. Reconciliation reads exactly this field.
    ///
    /// **Additive-field back-compat (GP 11).** A `JobRun` blob written
    /// before Phase 319 carries no `ExternalHandle` property at all.
    /// F# `None` is represented as `null`, and the converter yields
    /// `null` for an absent reference-typed field, so a legacy row
    /// deserialises to `None` — the correct value, not a broken one.
    /// That coincidence is load-bearing enough to be pinned by a test
    /// rather than trusted (`FableConverters` has bitten this estate
    /// both ways: a missing list field arrives as `null` where `[]` was
    /// assumed, and `Fable.SimpleJson` throws outright on the client).
    ExternalHandle: ExternalHandle option
}

/// What caused this run. The handler reads this to distinguish a
/// scheduled dispatch from an event-driven dispatch from an explicit
/// admin trigger. Carries enough context for an event-driven job
/// to retrieve the originating event from `IEventStore`.
type TriggerSource =
    /// Cron tick fired this run.
    | ScheduledByCron
    /// `OnEvent` matched this run. Handler can read the originating
    /// event by id from `IEventStore`.
    | ScheduledByEvent of eventType: string * eventId: Guid
    /// `IJobScheduler.TriggerOnce` was called.
    | ScheduledManually of byUserId: string

/// Job execution context — passed to every `IJobHandler.Execute` call
/// (portability rule 4: stateless handlers receive all state via this
/// record). The scheduler synthesises a fresh `JobContext` on every
/// dispatch attempt; handlers never observe in-memory state from
/// previous attempts.
type JobContext = {
    JobId: JobId
    ScopeId: string
    /// System-synthesised access context for the job's scope. Carries
    /// the team / individual scope but NOT the scheduling user's
    /// permissions — cron jobs run when no user is online, so a
    /// permission-bound `AccessContext` would leak the original
    /// caller's authority. Handlers requiring a specific user
    /// principal must capture it in `Payload` and re-resolve.
    AccessContext: AccessContext
    Attempt: int
    Trigger: Trigger
    TriggerSource: TriggerSource
    /// When the scheduler decided this run was due. Used for SLA
    /// telemetry — `RunningAt - ScheduledAt = queue latency`.
    ScheduledAt: DateTime
    /// When `Execute` was called. Set by the scheduler immediately
    /// before invoking the handler.
    RunningAt: DateTime
    /// The job's persisted payload. Handlers deserialise from this
    /// string into their own DTO shape. Empty string is permitted
    /// for parameter-less jobs.
    Payload: string
    /// Optional dead-letter destination from `RetryPolicy`. Handlers
    /// can route to it directly when they choose to mark a run as
    /// permanently failed (e.g., a malformed payload they know cannot
    /// recover) by returning `JobResult.PermanentFailure`.
    DeadLetterDestination: string option
}

/// Handler outcome. `Success` finalises the run as `Succeeded` and
/// resets `ConsecutiveFailures`. `TransientFailure` triggers retry
/// per `RetryPolicy`. `PermanentFailure` skips remaining retries and
/// goes straight to `DeadLettered` — reserve for shapes the handler
/// knows can never succeed (malformed payload, missing prerequisite).
///
/// Phase 319 adds `HandedOff`, the fourth outcome: "I did not do the
/// work, I arranged for it to be done elsewhere, here is the receipt."
type JobResult =
    | Success
    | TransientFailure of error: string
    | PermanentFailure of error: string

    /// Phase 319 — the handler submitted the work to an external compute
    /// backend and is returning the `ExternalHandle` **instead of a
    /// result**. The attempt is neither a success nor a failure yet: the
    /// scheduler records the run as `AwaitingExternal`, persists the
    /// handle, releases the dispatch lease, and reconciles the handle to
    /// a terminal outcome on later ticks.
    ///
    /// This is the case that makes the hand-off *non-blocking*. Without
    /// it a handler wanting external compute had to `Submit` and then
    /// poll in its own body, holding a dispatch lease for the entire
    /// remote duration — hours, for a training run — and losing the
    /// whole submission to a restart because nothing durable recorded
    /// that the remote work existed.
    ///
    /// **Additive (GP 11).** A handler that never returns this case is
    /// byte-unaffected: it is a new case at the END of the union (see
    /// `JobRunStatus` for why the position matters), and every existing
    /// `Success` / `TransientFailure` / `PermanentFailure` path in the
    /// scheduler is untouched.
    | HandedOff of handle: ExternalHandle

/// Caller-supplied scheduling request. Differs from `JobDefinition`
/// only in fields the scheduler computes itself — `JobId`,
/// `CreatedAt`, `NextRunAt`, `LastRunAt`, `LastRunStatus`,
/// `LastRunError`, `ConsecutiveFailures`, `Status`. Callers fill in
/// the registration-time intent; the scheduler stamps the rest.
///
/// Lives in the shared layer so the Fable admin UI and module client
/// code can construct registrations and submit them through the
/// `JobApi` Fable.Remoting surface.
type JobRegistration = {
    /// Logical scope to register the job under. The scheduler stamps
    /// this verbatim into `JobDefinition.ScopeId`. The handler-side
    /// `JobApi` validates that this matches the caller's resolved
    /// scope before forwarding to `IJobScheduler`.
    ScopeId: string
    Handler: string
    Payload: string
    Trigger: Trigger
    Idempotency: IdempotencyKey option
    RetryPolicy: JobRetryPolicy
    ShardKey: string option
    Precision: JobPrecision
    /// User id of the principal that scheduled the job. Stamped into
    /// `JobDefinition.CreatedBy` for audit. The handler overwrites
    /// any caller-supplied value with the resolved
    /// `AccessContext.UserId` so the wire field is informational
    /// only — callers can still read it through the admin UI.
    CreatedBy: string
    Tags: Map<string, string>
}

/// Why a `Schedule` call failed. Distinguishes user errors (invalid
/// cron, unknown handler, unsupported precision) from infrastructure
/// errors (storage unreachable). Admin UIs render the cases
/// differently — invalid cron is recoverable client-side, storage
/// failure needs operator escalation.
type ScheduleError =
    /// `Trigger.CronTrigger` carried an expression that did not
    /// parse against the supported subset (`*`, integers, commas,
    /// `*/N`). The string is round-tripped so the admin UI can
    /// highlight the offending part.
    | InvalidCron of expression: string * reason: string
    /// `JobDefinition.Handler` is not registered with this scheduler.
    /// The scheduler maintains the registered-handler set at
    /// `RegisterHandler` time so this is a registration miss, not
    /// a transient lookup failure.
    | HandlerNotRegistered of name: string
    /// Implementation cannot honour the requested precision
    /// (portability rule 6). The in-process default rejects `Second`;
    /// future minute-only distributed companions reject `Second`
    /// similarly.
    | PrecisionUnsupported of supplied: JobPrecision * supported: JobPrecision list
    /// Persistence layer threw or returned an unexpected error.
    /// Wraps the inner message verbatim for diagnostics.
    | StorageFailure of string