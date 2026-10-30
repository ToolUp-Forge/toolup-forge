/// How a job's run ends, as one function.
///
/// Every way an attempt can end — a handler's four results, the dispatch
/// loop spending its last attempt, a backend's terminal outcome for an
/// external hand-off (by poll or by callback), and the reconciliation pass
/// abandoning a run it cannot resolve — is a `RunOutcome` given to one pure
/// `transition`, which answers the `Transition`: the run row, the lifecycle
/// events, the dead-letter notification, the folded definition and what
/// happens next. One effectful `apply` performs a transition in a fixed
/// order — row, events, notification, definition — every time, so two paths
/// that must end a run the same way cannot drift: they are the same function.
///
/// The scheduler implementations differ only in who owns an attempt
/// (`TransitionOrigin`): the in-process dispatch loop retries inside its own
/// loop, a backend such as Quartz re-fires the job itself, and an external
/// outcome or a reconciliation abandonment ends a run the loop already left.
module ToolUp.Platform.JobLifecycle

open System
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Lifecycle event payloads ────────────────────────────────────
//
// Persisted to `IEventStore` under `SourceModule = "_platform.jobs"` with
// `FableConverters`, so the admin UI deserialises them through
// `Fable.SimpleJson` without an extra converter.
//
// An external run that completes emits exactly the `JobCompleted` /
// `JobFailed` / `JobDeadLettered` payload an in-process run emits: an admin
// UI or downstream consumer counts a completion one way. The external detail
// (which backend, which handle, what it said, where the result landed) rides
// on its own additive event types instead.

type JobCompletedPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    DurationMs: int64
}

type JobFailedPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    Error: string
    DurationMs: int64
}

type JobDeadLetteredPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    Error: string
    Attempts: int
}

/// Emitted when a handler returns `JobResult.HandedOff` and the run enters
/// `AwaitingExternal`: the point the scheduler stopped occupying a slot and
/// started owning a handle.
type JobExternalHandedOffPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    /// `ExternalHandle.HandleId` — the platform-minted identity.
    HandleId: Guid
    /// `ExternalHandle.Backend` — which dispatcher accepted the work.
    Backend: string
    /// `ExternalHandle.NativeRef` — the backend's own opaque token,
    /// echoed for operator correlation and never parsed.
    NativeRef: string
    SubmittedAt: DateTime
}

/// Emitted on every resolution that moved a run out of `AwaitingExternal`,
/// alongside (not instead of) the standard terminal event. Carries the
/// external-side detail the shared payloads have no field for: the outcome
/// label, the backend's result reference on success, and how long the run
/// waited.
type JobExternalReconciledPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
    HandleId: Guid
    Backend: string
    NativeRef: string
    /// `ExternalOutcome.label` — `"succeeded"` / `"failed"` /
    /// `"cancelled"`.
    Outcome: string
    /// The backend's opaque reference to the result, present only for
    /// `ExternalOutcome.Succeeded`. Echoed, never dereferenced.
    ResultRef: string option
    /// Backend-reported failure message, present only for
    /// `ExternalOutcome.Failed`.
    Error: string option
    /// Whether the backend called the failure retriable — the input to the
    /// retry-vs-dead-letter decision, which an operator diagnosing a
    /// dead-letter needs to see.
    Retriable: bool option
    /// Wall-clock the run spent awaiting the backend, submit to
    /// reconciliation.
    AwaitedMs: int64
}

/// Emitted when a run is abandoned because its `AwaitingExternal` row
/// carries no handle: hand-edited state, or a store that dropped the field on
/// a round-trip. Leaving the run awaiting forever, un-pollable and
/// un-completable, is the one outcome an operator could neither see nor act
/// on.
type JobExternalHandleMissingPayload = {
    JobId: JobId
    ScopeId: string
    Handler: string
    RunId: Guid
    Attempt: int
}

let private jsonOptions = FableConverters.create ()

/// A lifecycle payload as the event store holds it.
let internal serialize (value: 'T) : string =
    JsonSerializer.Serialize(value, jsonOptions)

// ─── The model ───────────────────────────────────────────────────

/// Why a run is abandoned rather than resolved.
type AbandonReason =
    /// The job's definition was deleted while its external work ran.
    | DefinitionGone
    /// The `AwaitingExternal` row carries no handle, so it cannot be polled.
    | HandleMissing

/// Every way a run can end. `JobResult` (handler-facing) and
/// `ExternalOutcome` (backend-facing) stay the two wire vocabularies; this is
/// the scheduler's one internal vocabulary for both.
type RunOutcome =
    | Completed
    | Retriable of error: string
    | Terminal of error: string
    | HandedOff of handle: ExternalHandle
    | ExternallyCancelled
    | Abandoned of reason: AbandonReason
    /// The dispatch loop spent its last attempt on a retriable failure; the
    /// failure is promoted to a dead letter on a row of its own.
    | RetriesExhausted

module RunOutcome =
    /// A handler's result as a run outcome.
    let ofJobResult (result: JobResult) : RunOutcome =
        match result with
        | Success -> Completed
        | TransientFailure error -> Retriable error
        | PermanentFailure error -> Terminal error
        | JobResult.HandedOff handle -> HandedOff handle

    /// A backend's outcome as a run outcome; `None` while it is still pending
    /// or running, which ends nothing.
    let ofExternal (outcome: ExternalOutcome) : RunOutcome option =
        match outcome with
        | ExternalOutcome.Succeeded _ -> Some Completed
        | ExternalOutcome.Failed error when error.Retriable -> Some(Retriable error.Message)
        | ExternalOutcome.Failed error -> Some(Terminal error.Message)
        | ExternalOutcome.Cancelled -> Some ExternallyCancelled
        | ExternalOutcome.Pending
        | ExternalOutcome.Running _ -> None

/// Who owns the attempt a transition ends. It decides three things and
/// nothing else: how long the attempt took, whether a retriable failure is
/// retried inside the dispatch loop or folded for someone else to retry, and
/// whether the fold re-advances `NextRunAt`.
type TransitionOrigin =
    /// An attempt the in-process dispatch loop ran for `elapsedMs`. The loop
    /// owns the retries: a retriable failure folds nothing and continues the
    /// loop, and the fold re-computes `NextRunAt` from the trigger.
    /// `priorError` is the error an earlier attempt of the same loop left.
    | Loop of elapsedMs: int64 * priorError: string option
    /// An attempt a backend fired for `elapsedMs` and retries itself (the
    /// Quartz companion): a retriable failure is folded and re-fired by the
    /// backend, and the fold takes the backend's next fire time, keeping the
    /// definition's own when the backend has none.
    | Backend of elapsedMs: int64 * nextFireAt: DateTime option
    /// A backend's terminal outcome for an external hand-off, by poll or by
    /// callback. The hand-off dispatch already advanced `NextRunAt`, so this
    /// never re-advances it.
    | External of handle: ExternalHandle * outcome: ExternalOutcome
    /// The reconciliation pass abandoning an awaiting run it cannot resolve.
    /// Never re-advances `NextRunAt`.
    | Reconciliation

/// What the caller does after applying a transition.
type Continuation =
    /// The run has ended.
    | Stop
    /// Retry at this attempt number: the dispatch loop continues, an external
    /// outcome starts a dispatch at it, a backend re-fires.
    | RetryAt of attempt: int
    /// The run awaits external work under this handle.
    | Await of handle: ExternalHandle
    /// The dispatch loop spent its last attempt: apply `RetriesExhausted`.
    | Exhausted

/// One lifecycle event, its payload serialised.
type LifecycleEvent = { EventType: string; Payload: string }

/// Everything one run-ending takes, as data.
type Transition = {
    /// The scope every effect is written under.
    ScopeId: string
    /// The run row to record — the attempt's terminal (or awaiting) row.
    /// `None` only when no attempt ran at all.
    Row: JobRun option
    /// The lifecycle events, in the order they are written.
    Events: LifecycleEvent list
    /// The dead-letter notification text, when the run dead-lettered.
    Notify: string option
    /// The definition with the run folded in; `None` when nothing is folded
    /// (a retry inside the dispatch loop, or a definition that is gone).
    Definition: JobDefinition option
    Continue: Continuation
}

/// Why a dispatch did not run the job it won the lease for. Each is a skip,
/// not a failure: nothing is written.
type Skip =
    /// The definition no longer exists.
    | Gone
    /// The job is not `Active`.
    | Inactive of status: JobStatus
    /// A cron tick for an occurrence a sibling replica already ran.
    | NotDue of nextRunAt: DateTime option
    /// An external hand-off from this job is still outstanding.
    | Outstanding of awaiting: JobRun
    /// No handler is registered under the job's name.
    | HandlerMissing of handler: string

/// The lease every hold on a job's dispatch lock is taken for. One lock id
/// per job serves them all; the times differ because the holds do.
type LeasePolicy = {
    /// A dispatch, held across the whole retry loop including its backoff
    /// sleeps. Generous: a lease that lapsed mid-loop would admit a second
    /// dispatcher for the same job.
    Dispatch: TimeSpan
    /// Resolving an external outcome, by the reconciliation pass or a
    /// completion callback.
    Resolve: TimeSpan
    /// A status transition (`Cancel` / `Disable` / `Enable`) or a scope
    /// re-issue: one read and one write.
    Transition: TimeSpan
    /// How long a status transition waits for the lease before refusing,
    /// rather than parking behind a dispatch's backoff sleep.
    TransitionCeiling: TimeSpan
}

module LeasePolicy =
    let defaults: LeasePolicy = {
        Dispatch = TimeSpan.FromHours 1.0
        Resolve = TimeSpan.FromMinutes 5.0
        Transition = TimeSpan.FromMinutes 1.0
        TransitionCeiling = TimeSpan.FromSeconds 5.0
    }

// ─── The guards ──────────────────────────────────────────────────

/// Decide, under the dispatch lease, whether `current` runs: the job exists,
/// is `Active`, is still due when a cron tick asked, has no external hand-off
/// outstanding, and has a handler. Answers the job with its handler, or the
/// first guard that refused.
let admit
    (lookup: string -> 'handler option)
    (source: TriggerSource)
    (scheduledAt: DateTime)
    (outstanding: JobRun option)
    (current: JobDefinition option)
    : Result<JobDefinition * 'handler, Skip> =
    match current with
    | None -> Error Gone
    | Some job when job.Status <> Active -> Error(Inactive job.Status)
    | Some job when source = ScheduledByCron && not (SchedulerPolicy.stillDueAt scheduledAt job) ->
        Error(NotDue job.NextRunAt)
    | Some _ when outstanding.IsSome -> Error(Outstanding outstanding.Value)
    | Some job ->
        match lookup job.Handler with
        | None -> Error(HandlerMissing job.Handler)
        | Some handler -> Ok(job, handler)

// ─── The transition ──────────────────────────────────────────────

let private event (eventType: string) (payload: 'T) : LifecycleEvent = {
    EventType = eventType
    Payload = serialize payload
}

/// The dead-letter notification, as every scheduler words it.
let deadLetterText (job: JobDefinition) (error: string) : string =
    $"Job {job.Handler} failed permanently after {job.RetryPolicy.MaxAttempts} attempts: {error}"

let private abandonedText (reason: AbandonReason) : string =
    match reason with
    | DefinitionGone -> "job definition no longer exists; external hand-off abandoned"
    | HandleMissing -> "external hand-off recorded no handle; the run cannot be polled"

let private exhaustedText = "max attempts reached"

/// The transition for a run whose definition no longer exists: the row
/// leaves `AwaitingExternal` dead-lettered, and with no definition there is
/// nothing to announce or fold.
let private orphaned (run: JobRun) (now: DateTime) : Transition = {
    ScopeId = run.ScopeId
    Row =
        Some {
            run with
                Status = DeadLettered
                CompletedAt = Some now
                Error = Some(abandonedText DefinitionGone)
                DurationMs = Some(int64 (now - run.StartedAt).TotalMilliseconds)
        }
    Events = []
    Notify = None
    Definition = None
    Continue = Stop
}

/// The transition that ends `run` of `job` with `outcome` at `now`. Pure.
///
/// `job` is the definition the caller read; `None` means it no longer exists,
/// and then every outcome is the `DefinitionGone` abandonment. `run` is the
/// attempt's row as it stands — the `Running` row the dispatch loop wrote, or
/// the `AwaitingExternal` row a resolution found. For `RetriesExhausted` it
/// is the fresh row the promotion is recorded on.
let transition
    (origin: TransitionOrigin)
    (job: JobDefinition option)
    (run: JobRun)
    (now: DateTime)
    (outcome: RunOutcome)
    : Transition =
    match job, outcome with
    | None, _
    | Some _, Abandoned DefinitionGone -> orphaned run now
    | Some job, _ ->
        let policy = job.RetryPolicy
        let scope = run.ScopeId

        let durationMs =
            match origin with
            | Loop(elapsedMs, _)
            | Backend(elapsedMs, _) -> elapsedMs
            | External _
            | Reconciliation -> int64 (now - run.StartedAt).TotalMilliseconds

        let priorError =
            match origin with
            | Loop(_, prior) -> prior
            | Backend _
            | External _
            | Reconciliation -> None

        let ended status error = {
            run with
                Status = status
                CompletedAt = Some now
                Error = error
                DurationMs = Some durationMs
        }

        let fold status error failures = {
            job with
                NextRunAt =
                    match origin with
                    | Loop _ -> SchedulerPolicy.computeNextRunAt job.Trigger now
                    | Backend(_, Some next) -> Some next
                    | Backend(_, None)
                    | External _
                    | Reconciliation -> job.NextRunAt
                LastRunAt = Some now
                LastRunStatus = Some status
                LastRunError = error
                ConsecutiveFailures = failures
        }

        /// The external-side companion event, for an outcome a backend reported.
        let reconciled =
            match origin with
            | External(handle, reported) -> [
                event "JobExternalReconciled" {
                    JobId = run.JobId
                    ScopeId = scope
                    Handler = job.Handler
                    RunId = run.RunId
                    Attempt = run.Attempt
                    HandleId = handle.HandleId
                    Backend = handle.Backend
                    NativeRef = handle.NativeRef
                    Outcome = ExternalOutcome.label reported
                    ResultRef =
                        match reported with
                        | ExternalOutcome.Succeeded resultRef -> Some resultRef
                        | _ -> None
                    Error =
                        match reported with
                        | ExternalOutcome.Failed error -> Some error.Message
                        | _ -> None
                    Retriable =
                        match reported with
                        | ExternalOutcome.Failed error -> Some error.Retriable
                        | _ -> None
                    AwaitedMs = durationMs
                }
              ]
            | Loop _
            | Backend _
            | Reconciliation -> []

        let deadLettered (row: JobRun) (error: string) (attempts: int) (preceding: LifecycleEvent list) = {
            ScopeId = scope
            Row = Some row
            Events =
                preceding
                @ [
                    event "JobDeadLettered" {
                        JobId = run.JobId
                        ScopeId = scope
                        Handler = job.Handler
                        Error = error
                        Attempts = attempts
                    }
                ]
            Notify = Some(deadLetterText job error)
            Definition = Some(fold DeadLettered (Some error) (job.ConsecutiveFailures + 1))
            Continue = Stop
        }

        let failed (error: string) =
            event "JobFailed" {
                JobId = run.JobId
                ScopeId = scope
                Handler = job.Handler
                RunId = run.RunId
                Attempt = run.Attempt
                Error = error
                DurationMs = durationMs
            }

        let attemptsRemain = run.Attempt < policy.MaxAttempts

        match outcome with
        | Completed -> {
            ScopeId = scope
            Row = Some(ended Succeeded None)
            Events =
                event "JobCompleted" {
                    JobId = run.JobId
                    ScopeId = scope
                    Handler = job.Handler
                    RunId = run.RunId
                    Attempt = run.Attempt
                    DurationMs = durationMs
                }
                :: reconciled
            Notify = None
            Definition = Some(fold Succeeded priorError 0)
            Continue = Stop
          }

        | Retriable error ->
            match origin with
            | Loop _ -> {
                // The loop folds once, when it ends; a retry inside it is the
                // row and the event only.
                ScopeId = scope
                Row = Some(ended Failed (Some error))
                Events = [ failed error ]
                Notify = None
                Definition = None
                Continue =
                    if attemptsRemain then
                        RetryAt(run.Attempt + 1)
                    else
                        Exhausted
              }
            | Backend _
            | External _
            | Reconciliation when attemptsRemain -> {
                ScopeId = scope
                Row = Some(ended Failed (Some error))
                Events = reconciled @ [ failed error ]
                Notify = None
                Definition = Some(fold Failed (Some error) job.ConsecutiveFailures)
                Continue = RetryAt(run.Attempt + 1)
              }
            | Backend _
            | External _
            | Reconciliation ->
                // The retry budget is spent: dead-letter this attempt, as
                // `Terminal` does.
                deadLettered (ended DeadLettered (Some error)) error run.Attempt reconciled

        | Terminal error -> deadLettered (ended DeadLettered (Some error)) error run.Attempt reconciled

        | RetriesExhausted ->
            let error = priorError |> Option.defaultValue exhaustedText

            let row = {
                ended DeadLettered priorError with
                    Attempt = policy.MaxAttempts
            }

            {
                deadLettered row error policy.MaxAttempts reconciled with
                    Definition = Some(fold DeadLettered priorError (job.ConsecutiveFailures + 1))
            }

        | HandedOff handle -> {
            // The attempt has NOT ended: the row carries no `CompletedAt` and
            // no `DurationMs`, and the hand-off is not a failure, so the retry
            // budget and `ConsecutiveFailures` are untouched.
            ScopeId = scope
            Row =
                Some {
                    run with
                        Status = AwaitingExternal
                        CompletedAt = None
                        Error = None
                        DurationMs = None
                        ExternalHandle = Some handle
                }
            Events = [
                event "JobExternalHandedOff" {
                    JobId = run.JobId
                    ScopeId = scope
                    Handler = job.Handler
                    RunId = run.RunId
                    Attempt = run.Attempt
                    HandleId = handle.HandleId
                    Backend = handle.Backend
                    NativeRef = handle.NativeRef
                    SubmittedAt = handle.SubmittedAt
                }
            ]
            Notify = None
            Definition = Some(fold AwaitingExternal priorError job.ConsecutiveFailures)
            Continue = Await handle
          }

        | ExternallyCancelled -> {
            // Terminal, but not a failure: no notification and no
            // `ConsecutiveFailures` bump — a cancelled attempt says nothing
            // about the job's health.
            ScopeId = scope
            Row = Some(ended JobRunStatus.ExternallyCancelled None)
            Events = reconciled
            Notify = None
            Definition = Some(fold JobRunStatus.ExternallyCancelled None job.ConsecutiveFailures)
            Continue = Stop
          }

        | Abandoned DefinitionGone -> orphaned run now

        | Abandoned HandleMissing ->
            let error = abandonedText HandleMissing

            deadLettered (ended DeadLettered (Some error)) error run.Attempt [
                event "JobExternalHandleMissing" {
                    JobId = run.JobId
                    ScopeId = scope
                    Handler = job.Handler
                    RunId = run.RunId
                    Attempt = run.Attempt
                }
            ]

/// The fold for a dispatch loop that ran no attempt at all (a retry policy
/// whose budget is below the first attempt): the definition records the run
/// as `Pending` and re-advances `NextRunAt`, with no row and no event.
let unattempted (job: JobDefinition) (now: DateTime) : Transition = {
    ScopeId = job.ScopeId
    Row = None
    Events = []
    Notify = None
    Definition =
        Some {
            job with
                NextRunAt = SchedulerPolicy.computeNextRunAt job.Trigger now
                LastRunAt = Some now
                LastRunStatus = Some Pending
                LastRunError = None
        }
    Continue = Stop
}

// ─── Applying a transition ───────────────────────────────────────

/// Where a transition's effects go.
type LifecycleEffects = {
    RecordRun: JobRun -> Async<unit>
    /// Write one lifecycle event under the scope.
    Emit: string -> LifecycleEvent -> Async<unit>
    /// Publish the dead-letter notification text under the scope.
    Notify: string -> string -> Async<unit>
    Update: JobDefinition -> Async<unit>
}

/// No gate between the row and the rest.
let ungated: Async<bool> = async.Return true

/// Perform `transition`: the row, then `gate`, then — only when the gate
/// answered `true` — the events, the notification and the definition, in that
/// order, every time. Answers whether the gate let the rest through.
///
/// The row is durable before anything announces it, and the gate sits
/// between them: an external outcome's exactly-once claim (the row is
/// idempotent and must be written before the one-shot claim, or a failure
/// between them strands the run), or a hand-off's handle registration (the
/// awaiting row is durable before a callback can route to it).
let apply (effects: LifecycleEffects) (gate: Async<bool>) (transition: Transition) : Async<bool> = async {
    match transition.Row with
    | Some row -> do! effects.RecordRun row
    | None -> ()

    let! proceed = gate

    if proceed then
        for e in transition.Events do
            do! effects.Emit transition.ScopeId e

        match transition.Notify with
        | Some text -> do! effects.Notify transition.ScopeId text
        | None -> ()

        match transition.Definition with
        | Some definition -> do! effects.Update definition
        | None -> ()

    return proceed
}