module ToolUp.Reporting.ReportSubscriptionApiHandler

open System
open ToolUp.Platform
open ToolUp.Reporting

// ─── Phase 534.C — the management handler ────────────────────────────
//
// Builds an `IReportSubscriptionApi` scoped to one caller. The scope is
// resolved upstream and closed over here, so no method takes one and
// none can be talked into another scope's data (GP 4).
//
// **The subscription record and the scheduler job are two halves of one
// fact, and this handler is the only place that keeps them together.**
// A subscription with no job never fires; a job with no subscription
// dispatches into a handler that finds nothing and returns `Success`
// forever. So every mutation here writes the store and then reconciles
// the scheduler: create schedules, update re-schedules, pause disables,
// resume enables, delete cancels.
//
// The order is deliberate — **store first, scheduler second**. Both
// orders can fail in the middle; this one fails towards a subscription
// that exists and is visible but has not fired yet, which an operator
// can see and re-save. The other fails towards a job firing for a
// subscription nobody can see or stop, which they cannot.
//
// **A missing scheduler is a real posture, not an error to hide.** A
// deployment may compose Reporting with no `IJobScheduler` at all —
// `ReportingCompose` emits a compose-time diagnostic naming the missing
// seam rather than leaving the feature silently dead (GP 13). When that
// is the case, this handler is not composed either, so there is no
// half-alive surface offering to create subscriptions that can never
// run.

/// Predicate answering "may the current caller manage subscriptions at
/// this scope?". Resolved per call rather than snapshotted at
/// construction, so revoking a caller's management rights takes effect
/// on their next write rather than at the next restart. (The
/// build-once / read-per-call seam mismatch is a live defect class in
/// this codebase; this seam is read-per-call by construction.)
type CanManageSubscriptions = unit -> Async<bool>

/// The refusal a management-gated call returns. One constant so the six
/// call sites cannot drift, and so a consumer can match on it. Names
/// the requirement, never the caller or the policy internals.
[<Literal>]
let SubscriptionManagementDenied =
    "report-subscription management requires an Owner / Admin caller at this scope"

/// What the handler needs beyond the store and the registry: the
/// scheduler it registers jobs against, and the retry policy those jobs
/// carry.
type ReportSubscriptionApiDeps = {
    Subscriptions: IReportSubscriptionStore
    Producers: ReportProducerRegistry
    Scheduler: IJobScheduler
    /// The policy every subscription job is registered with. The SAME
    /// value the job handler is built with — see the terminal-failure
    /// note in `ReportSubscriptionJobHandler` for why they must be one
    /// value rather than two that happen to agree.
    RetryPolicy: JobRetryPolicy
    /// Job precision. `Minute` is what the in-process scheduler
    /// supports and what a cron-scheduled report needs; a deployment on
    /// a distributed scheduler may raise it.
    Precision: JobPrecision
}

/// The scheduler job id for a subscription is derived, not stored: the
/// idempotency key is a pure function of the subscription id, so
/// re-scheduling an existing subscription returns the existing job
/// rather than accumulating one job per save. This is the same
/// mechanism `ScheduledJobDeclaration.registerWith` uses for
/// compose-time declarations, applied to a runtime-created job.
let private idempotencyFor (id: SubscriptionId) : IdempotencyKey = {
    Key = $"report-subscription-{id}"
    TtlSeconds = 60 * 60 * 24 * 365
}

let private registrationFor (deps: ReportSubscriptionApiDeps) (subscription: ReportSubscription) : JobRegistration = {
    ScopeId = subscription.ScopeId
    Handler = ReportSubscription.JobHandlerName
    Payload = ReportSubscriptionJobHandler.encodePayload subscription.Id
    Trigger = CronTrigger subscription.Schedule
    Idempotency = Some(idempotencyFor subscription.Id)
    RetryPolicy = deps.RetryPolicy
    ShardKey = Some subscription.Id
    Precision = deps.Precision
    CreatedBy = subscription.CreatedBy
    Tags =
        Map [
            "source", "report-subscription"
            "subscriptionId", subscription.Id
            "producerKey", subscription.ProducerKey
        ]
}

/// Find the scheduler job backing a subscription. Derived by scanning
/// the scope's jobs for the subscription tag rather than by persisting
/// a `JobId` on the record: a `JobId` on the subscription would be a
/// second copy of a fact the scheduler already owns, and the two would
/// disagree the first time a job was recreated after a store wipe.
let private findJob
    (deps: ReportSubscriptionApiDeps)
    (scopeId: string)
    (id: SubscriptionId)
    : Async<JobDefinition option> =
    async {
        let! jobs = deps.Scheduler.ListJobs scopeId

        return
            jobs
            |> List.tryFind (fun job ->
                job.Handler = ReportSubscription.JobHandlerName
                && job.Tags.TryFind "subscriptionId" = Some id)
    }

/// Register (or re-register) the job for a subscription and align its
/// enabled state. Idempotent: the stable idempotency key means a second
/// call returns the existing job rather than creating a duplicate.
let private syncJob
    (deps: ReportSubscriptionApiDeps)
    (subscription: ReportSubscription)
    : Async<Result<unit, SubscriptionError>> =
    async {
        let! scheduled = deps.Scheduler.Schedule(registrationFor deps subscription)

        match scheduled with
        | Result.Error(InvalidCron(expr, reason)) -> return Error(InvalidSchedule(expr, reason))
        | Result.Error err -> return Error(SchedulerUnavailable(sprintf "%A" err))
        | Result.Ok jobId ->
            if subscription.Enabled then
                do! deps.Scheduler.Enable(subscription.ScopeId, jobId)
            else
                do! deps.Scheduler.Disable(subscription.ScopeId, jobId)

            return Ok()
    }

/// Build a per-scope `IReportSubscriptionApi`.
///
/// `principal` is the resolved caller stamped into `CreatedBy` — the
/// same value the audit trail attributes the subscription's runs to.
let create (deps: ReportSubscriptionApiDeps) (principal: string) (scopeId: string) : IReportSubscriptionApi =
    let save
        (id: SubscriptionId)
        (createdBy: string)
        (createdAt: DateTimeOffset)
        (lastRun: SubscriptionRunOutcome)
        (request: NewReportSubscription)
        =
        async {
            match ReportSubscriptionStore.validate deps.Producers scopeId id createdBy createdAt lastRun request with
            | Error e -> return Error e
            | Ok subscription ->
                let! stored = deps.Subscriptions.Save(scopeId, subscription)

                match stored with
                | Result.Error e -> return Error(SubscriptionStorageFailure e)
                | Result.Ok persisted ->
                    let! synced = syncJob deps persisted

                    return
                        match synced with
                        | Ok() -> Ok persisted
                        | Error e -> Error e
        }

    {
        ListProducers = fun () -> async { return deps.Producers.Descriptors }

        ListSubscriptions = fun () -> deps.Subscriptions.List scopeId

        CreateSubscription =
            fun request -> save (Guid.NewGuid().ToString "N") principal DateTimeOffset.UtcNow NeverRun request

        UpdateSubscription =
            fun (id, request) -> async {
                let! existing = deps.Subscriptions.Get(scopeId, id)

                match existing with
                | None -> return Error(SubscriptionNotFound id)
                // The stored provenance and run history win over
                // anything the caller sends: an update edits what the
                // owner authored, never who created it or what it has
                // already done.
                | Some current -> return! save id current.CreatedBy current.CreatedAt current.LastRun request
            }

        SetSubscriptionEnabled =
            fun (id, enabled) -> async {
                let! existing = deps.Subscriptions.Get(scopeId, id)

                match existing with
                | None -> return Error(SubscriptionNotFound id)
                | Some current ->
                    let updated = { current with Enabled = enabled }
                    let! stored = deps.Subscriptions.Save(scopeId, updated)

                    match stored with
                    | Result.Error e -> return Error(SubscriptionStorageFailure e)
                    | Result.Ok persisted ->
                        let! job = findJob deps scopeId id

                        match job with
                        | Some definition ->
                            if enabled then
                                do! deps.Scheduler.Enable(scopeId, definition.JobId)
                            else
                                do! deps.Scheduler.Disable(scopeId, definition.JobId)

                            return Ok persisted
                        // No job — the subscription was created while no
                        // scheduler was reachable, or the store outlived
                        // the scheduler's state. Re-registering is the
                        // repair, and it is what an operator expects
                        // "resume" to do.
                        | None ->
                            let! synced = syncJob deps persisted

                            return
                                match synced with
                                | Ok() -> Ok persisted
                                | Error e -> Error e
            }

        DeleteSubscription =
            fun id -> async {
                let! existing = deps.Subscriptions.Get(scopeId, id)

                match existing with
                | None -> return Error(SubscriptionNotFound id)
                | Some _ ->
                    // Cancel first here, unlike every other mutation:
                    // the failure this order risks is a cancelled job
                    // whose subscription still exists — visible, and
                    // repaired by re-saving. The other order risks a
                    // job firing for a subscription nobody can see.
                    let! job = findJob deps scopeId id

                    match job with
                    | Some definition -> do! deps.Scheduler.Cancel(scopeId, definition.JobId)
                    | None -> ()

                    do! deps.Subscriptions.Delete(scopeId, id)
                    return Ok()
            }

        RunSubscriptionNow =
            fun id -> async {
                let! existing = deps.Subscriptions.Get(scopeId, id)

                match existing with
                | None -> return Error(SubscriptionNotFound id)
                | Some _ ->
                    let! job = findJob deps scopeId id

                    match job with
                    | None ->
                        return
                            Error(
                                SchedulerUnavailable
                                    "this subscription has no registered job — save it again to re-register"
                            )
                    | Some definition ->
                        let! fired = deps.Scheduler.TriggerOnce(scopeId, definition.JobId, principal)

                        return
                            match fired with
                            | Result.Ok() -> Ok()
                            | Result.Error e -> Error(SchedulerUnavailable e)
            }
    }

/// Wrap an `IReportSubscriptionApi` so every mutating method also
/// consults the deployment's management predicate. Reads
/// (`ListProducers`, `ListSubscriptions`) are untouched: seeing which
/// reports exist and whether they are working is the ordinary operator
/// question, and the attribute gate already refuses anonymous callers.
///
/// A decorator rather than a constructor parameter, following Phase
/// 619 exactly: the deployment's answer to "may this caller manage
/// subscriptions?" is its own role model, which forge cannot express,
/// and a decorator composes without a combinatorial set of factories.
///
/// ```fsharp
/// ReportSubscriptionApiHandler.create deps principal scopeId
/// |> ReportSubscriptionApiHandler.withManagementGate canManage
/// ```
let withManagementGate (canManage: CanManageSubscriptions) (api: IReportSubscriptionApi) : IReportSubscriptionApi =
    let gated (denied: 'e) (body: unit -> Async<Result<'a, 'e>>) = async {
        let! permitted = canManage ()

        if permitted then return! body () else return Error denied
    }

    let denial = SubscriptionNotAuthorised SubscriptionManagementDenied

    {
        api with
            CreateSubscription = fun request -> gated denial (fun () -> api.CreateSubscription request)
            UpdateSubscription = fun args -> gated denial (fun () -> api.UpdateSubscription args)
            SetSubscriptionEnabled = fun args -> gated denial (fun () -> api.SetSubscriptionEnabled args)
            DeleteSubscription = fun id -> gated denial (fun () -> api.DeleteSubscription id)
            RunSubscriptionNow = fun id -> gated denial (fun () -> api.RunSubscriptionNow id)
    }