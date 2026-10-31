/// The scheduling policy every `IJobScheduler` implementation shares.
///
/// An implementation of `IJobScheduler` has two halves. POLICY is what the
/// interface promises whatever runs the jobs: the `Schedule` validation chain
/// and its idempotency, the synthesis of a `JobDefinition` from a
/// `JobRegistration`, what a status transition writes, and which jobs a
/// manual fire or an event may dispatch. PROJECTION is the implementation's
/// own: a tick loop and a lease for the in-process default, Quartz job
/// details and triggers for the Quartz companion. This module is the policy
/// half, written once over `IJobStore`, so two schedulers cannot disagree
/// about it; each implementation keeps only its projection.
module ToolUp.Platform.SchedulerPolicy

open System
open ToolUp.Platform

/// `NextRunAt` for a trigger, after `after`: the next cron occurrence, or
/// `None` for an `OnEvent` / `Manual` job, which has no schedulable advance
/// time. Computed with forge's own cron parser on every implementation,
/// because `NextRunAt` is a field of the SHARED definition that an admin UI
/// renders and `IJobStore.DueJobs` selects on.
let computeNextRunAt (trigger: Trigger) (after: DateTime) : DateTime option =
    match trigger with
    | CronTrigger expression ->
        match CronExpression.tryParse expression with
        | Ok cron -> CronExpression.nextRunAfter cron after
        | Error _ -> None
    | OnEvent _
    | Manual -> None

/// Is `job` still due for the tick at `tickAt`? The predicate
/// `IJobStore.DueJobs` selects on (`NextRunAt <= now`), so a job this answers
/// `false` for would not be in a fresh `DueJobs` listing at that instant.
/// `None` is never due.
let stillDueAt (tickAt: DateTime) (job: JobDefinition) : bool =
    match job.NextRunAt with
    | Some next -> next <= tickAt
    | None -> false

/// The `Schedule` validation chain, first failure wins: the cron expression
/// parses, the handler is registered, the precision is supported.
///
/// `Second` is rejected by every implementation, and that is a statement
/// about the TRIGGER VOCABULARY: `Trigger.CronTrigger` is five-field crontab,
/// whose finest granularity is one minute, so there is no way to ask for a
/// sub-minute schedule through this interface (portability rule 6).
let validate (isRegistered: string -> bool) (registration: JobRegistration) : Result<unit, ScheduleError> =
    let cronCheck =
        match registration.Trigger with
        | CronTrigger expression ->
            match CronExpression.tryParse expression with
            | Ok _ -> Ok()
            | Error reason -> Error(InvalidCron(expression, reason))
        | _ -> Ok()

    match cronCheck with
    | Error e -> Error e
    | Ok() ->
        if not (isRegistered registration.Handler) then
            Error(HandlerNotRegistered registration.Handler)
        elif registration.Precision = Second then
            Error(PrecisionUnsupported(Second, [ Minute ]))
        else
            Ok()

/// The definition a registration becomes under `jobId` at `now`: `Active`,
/// with no run history and `NextRunAt` computed from its trigger.
let definitionFor (jobId: JobId) (registration: JobRegistration) (now: DateTime) : JobDefinition = {
    JobId = jobId
    ScopeId = registration.ScopeId
    Handler = registration.Handler
    Payload = registration.Payload
    Trigger = registration.Trigger
    Idempotency = registration.Idempotency
    RetryPolicy = registration.RetryPolicy
    ShardKey = registration.ShardKey
    Precision = registration.Precision
    Status = Active
    CreatedAt = now
    CreatedBy = registration.CreatedBy
    NextRunAt = computeNextRunAt registration.Trigger now
    LastRunAt = None
    LastRunStatus = None
    LastRunError = None
    ConsecutiveFailures = 0
    Tags = registration.Tags
    LastRunWarning = None
}

/// Persist a new job under `jobId`, then `announce` it (the in-process
/// scheduler writes its `JobScheduled` event; an implementation with no event
/// store announces nothing). A store failure is `StorageFailure`.
let persistNew
    (store: IJobStore)
    (announce: JobDefinition -> Async<unit>)
    (jobId: JobId)
    (registration: JobRegistration)
    : Async<Result<ScheduleOutcome, ScheduleError>> =
    async {
        let definition = definitionFor jobId registration DateTime.UtcNow

        try
            do! store.Save definition
            do! announce definition
            return Ok(Scheduled jobId)
        with ex ->
            return Error(ScheduleError.StorageFailure ex.Message)
    }

/// Both `Schedule` overloads, once each has settled the registration's scope:
/// validate, honour idempotency, persist. A live job recovered by its key is
/// `Coalesced` and keeps the token it was issued; otherwise a fresh id is
/// minted and `tagsFor` settles the persisted tags for it (the typed overload
/// seals its scope token for that id).
let scheduleValidated
    (store: IJobStore)
    (isRegistered: string -> bool)
    (announce: JobDefinition -> Async<unit>)
    (tagsFor: JobId -> Result<Map<string, string>, string>)
    (registration: JobRegistration)
    : Async<Result<ScheduleOutcome, ScheduleError>> =
    async {
        match validate isRegistered registration with
        | Error e -> return Error e
        | Ok() ->
            let createNew () = async {
                let jobId = Guid.NewGuid()

                match tagsFor jobId with
                | Error reason -> return Error(ScheduleError.StorageFailure reason)
                | Ok tags -> return! persistNew store announce jobId { registration with Tags = tags }
            }

            match registration.Idempotency with
            | Some key ->
                match! store.FindByIdempotencyKey(registration.ScopeId, key.Key, key.TtlSeconds, DateTime.UtcNow) with
                | Some existing -> return Ok(Coalesced existing)
                | None -> return! createNew ()
            | None -> return! createNew ()
    }

/// A status transition's read-decide-write: nothing for an unknown job or one
/// already in `status`; otherwise the job in `status`, with `NextRunAt`
/// re-armed from its trigger on `Active` and cleared on anything else.
/// Unguarded: an implementation that must exclude a concurrent dispatch holds
/// its own lock around it.
let setStatus (store: IJobStore) (scopeId: string) (jobId: JobId) (status: JobStatus) : Async<unit> = async {
    match! store.Get(scopeId, jobId) with
    | None -> ()
    | Some job when job.Status = status -> ()
    | Some job ->
        do!
            store.Update {
                job with
                    Status = status
                    NextRunAt =
                        match status with
                        | Active -> computeNextRunAt job.Trigger DateTime.UtcNow
                        | _ -> None
            }
}

/// The job a manual `TriggerOnce` may dispatch, or why it may not: an unknown
/// job, a `Cancelled` one, and a `Disabled` one (`Disable` stops dispatch,
/// and a manual fire is a dispatch) are refused before anything is started.
let triggerable (store: IJobStore) (scopeId: string) (jobId: JobId) : Async<Result<JobDefinition, TriggerError>> = async {
    match! store.Get(scopeId, jobId) with
    | None -> return Error(TriggerError.JobNotFound(scopeId, jobId))
    | Some job when job.Status = Cancelled -> return Error TriggerError.JobCancelled
    | Some job when job.Status = Disabled -> return Error TriggerError.JobDisabled
    | Some job -> return Ok job
}

/// The jobs an event written in `scopeId` dispatches: every `Active` job whose
/// `OnEvent` trigger names `eventType`.
let eventTriggered (store: IJobStore) (scopeId: string) (eventType: string) : Async<JobDefinition list> = async {
    let! jobs = store.ListJobs scopeId

    return
        jobs
        |> List.filter (fun job ->
            job.Status = Active
            && match job.Trigger with
               | OnEvent declared -> declared = eventType
               | _ -> false)
}