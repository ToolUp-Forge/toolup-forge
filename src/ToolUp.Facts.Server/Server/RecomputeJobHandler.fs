// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── RecomputeJobHandler (Phase 561 — reactive fact recomputation) ────
//
// The execution tier of reactive recomputation (task 561.C). The pure
// invalidation derivation lives in `FactInvalidation.fs`; this file turns
// an invalidated fact into *work*:
//
//   - `Eager`  → `reactToDataChange` schedules + fires a recompute job
//                through `IJobScheduler` (GP 12), and this handler runs it:
//                it loads the fact, calls the deployment's `IFactRecomputer`,
//                and re-asserts through the ordinary `IFactStore.Assert`
//                path (supersession stays derived, audit is the usual one).
//   - `OnQuery`→ deferred to the read path (`FactInvalidation.recomputeNow`);
//                `reactToDataChange` records it but schedules nothing.
//   - `Manual` → the changed state is surfaced only; nothing executes.
//
// **Idempotent by construction.** The recompute job carries an idempotency
// key per fact id, so repeated invalidations of one fact coalesce to a
// single live job; and because a recompute that produces an unchanged
// value re-asserts an identical (content-addressed) tuple, `Assert` is a
// no-op — the fact base converges, it does not churn (task 561.D).
//
// **Stateless handler (GP 12 rule 4).** Everything the handler needs
// arrives via `JobContext` (`Scope` / `ScopeId`) + the deserialised payload
// (the target `FactId`); no in-memory state survives across dispatches.
//
// **Which scope form it uses (Phases 818 and 930).** A recompute job
// scheduled with a resolver-minted scope (`IJobScheduler.Schedule(scope, …)`
// with this handler's name and `payloadFor`) runs through the store's
// `ResolvedScope` members, under the scope the scheduling request resolved
// to. Since Phase 930 that is what the reactive path does whenever the data
// write it reacts to was made under a resolved scope: the write's scope rides
// the change as `DataChangeScope.Resolved` (see `ReactiveDataChange.fs`),
// `reactToResolvedChange` walks and schedules through the typed members, and
// the scheduler re-mints the scope when the job runs.
//
// **The carried arm stays, and why (Phase 930's premise check).** Two kinds of
// recompute still arrive with only a string, and neither can be typed without
// promoting that string to a resolved scope, which Phase 797 forbids:
//
//   * a write made off the request path, or into a shard other than the one
//     the request resolved (a boot seed, an ingestion or import job, a
//     cross-scope write) reaches the reactive path as
//     `DataChangeScope.Carried` and is scheduled through the string overload;
//   * a scheduler outside the platform's server tier (the Quartz companion)
//     cannot re-mint, so it hands even a typed-scheduled job back with the
//     anonymous scope on `JobContext.Scope` and the carried `ScopeId` beside it.
//
// For both, the handler keys the store on the carried `ScopeId`, as it did
// before Phase 818. Reading the anonymous shard for them instead would find
// nothing and report success, which is a silent GP 11 regression. What the
// handler never does is read a scope OTHER than the job's own shard: a typed
// scope that disagrees with the registration's `ScopeId` is refused.

/// The scope a data-object write was made under, as the reactive path
/// receives it with the change (Phase 930). The resolved form is the
/// platform's own value, carried from the write; the carried form is the
/// shard string alone. Nothing converts the second into the first.
[<RequireQualifiedAccess>]
type DataChangeScope =
    /// The write named the shard the platform resolved for the principal
    /// making it, and that resolved scope rides the change.
    | Resolved of scope: ResolvedScope
    /// Only the shard string rode the write: an off-request write (a boot
    /// seed, an ingestion or import job) or a write into a shard other than
    /// the one the request resolved.
    | Carried of scopeId: string

    /// The shard the write landed in, in either form.
    member this.ScopeId =
        match this with
        | Resolved scope -> scope.ScopeId
        | Carried scopeId -> scopeId

/// Recompute-job payload (JSON). The scope is carried by `JobContext`, so
/// the payload only names the fact to recompute.
type RecomputeJobPayload = { factId: string }

module private RecomputeJson =
    let private options = FableConverters.create ()

    let serialize (value: 'T) : string =
        JsonSerializer.Serialize(value, options)

    let tryDeserialize (json: string) : RecomputeJobPayload option =
        try
            Some(JsonSerializer.Deserialize<RecomputeJobPayload>(json, options))
        with _ ->
            None

/// Per-fact classification of what the invalidation walk did — for audit,
/// telemetry, and tests.
type InvalidationOutcome =
    /// `Eager` metric — a recompute job was scheduled (and fired) for the
    /// fact.
    | Enqueued of factId: string * jobId: JobId
    /// `OnQuery` metric — recompute deferred to the next read of the
    /// lineage; nothing scheduled now.
    | DeferredToQuery of factId: string
    /// `Manual` metric — the changed state is surfaced only; a human or an
    /// explicit trigger drives any recompute.
    | SurfacedOnly of factId: string
    /// `Eager` metric — scheduling the recompute job failed; the error is
    /// carried for diagnostics.
    | ScheduleFailed of factId: string * error: string

/// `FactInvalidation.recomputeNow` over a `ResolvedScope` (Phase 818): the
/// same recompute-then-re-assert, with the re-assert through the store's
/// typed member. The recompute engine itself (`IFactRecomputer`) keys on
/// the shard string — it computes a value, it does not read the fact base
/// through a door.
module private RecomputeUnder =
    let resolvedScope
        (store: IFactStore)
        (recomputer: IFactRecomputer)
        (scope: ResolvedScope)
        (fact: Fact)
        : Async<Result<Fact option, string>> =
        async {
            let! recomputed = recomputer.Recompute(scope.ScopeId, fact)

            match recomputed with
            | Error e -> return Error e
            | Ok None -> return Ok None
            | Ok(Some draft) ->
                let! asserted = store.Assert(scope, draft)

                match asserted with
                | Ok f -> return Ok(Some f)
                | Error e -> return Error e
        }

/// `IJobHandler` that runs one recompute: load the target fact, recompute
/// its value through the deployment's `IFactRecomputer`, and re-assert.
type RecomputeJobHandler(store: IFactStore, recomputer: IFactRecomputer, logger: ILogger) =
    interface IJobHandler with
        member _.Execute(ctx: JobContext) : Async<JobResult> = async {
            match RecomputeJson.tryDeserialize ctx.Payload with
            | None ->
                let msg =
                    sprintf "RecomputeJobHandler: malformed payload for job %A — %s" ctx.JobId ctx.Payload

                logger.Warn msg
                return JobResult.PermanentFailure msg
            | Some payload ->
                // Phase 818 — a job whose scope the scheduler re-minted
                // (scheduled through the typed `Schedule`) reads and writes
                // the fact store through the `ResolvedScope` members. A job
                // whose scope was only carried — scheduled through the
                // string `Schedule`, which is what `reactToDataChange`
                // does for a carried write, or handed back un-minted by a
                // scheduler outside the server tier — runs under the
                // anonymous scope on `ctx.Scope`, and reading the
                // anonymous shard for it would silently find nothing; its
                // carried `ScopeId` keys the store instead, exactly as
                // before this phase (GP 11). Both key one shard: the typed
                // form is the string form over `ScopeId` (Phase 797).
                let resolved =
                    not ctx.Scope.IsAnonymous || ctx.ScopeId = ResolvedScope.AnonymousScopeId

                if resolved && ctx.Scope.ScopeId <> ctx.ScopeId then
                    // Phase 930 — a typed scope naming a shard other than the
                    // job's own is never read under. A scheduler that re-mints
                    // hands back the scope it registered under, so this is a
                    // defect upstream, and retrying cannot mend it.
                    let msg =
                        sprintf
                            "RecomputeJobHandler: job %A carries scope '%s' but is registered under '%s' — refused rather than read across shards"
                            ctx.JobId
                            ctx.Scope.ScopeId
                            ctx.ScopeId

                    logger.Warn msg
                    return JobResult.PermanentFailure msg
                else
                    let! fact =
                        if resolved then
                            store.Get(ctx.Scope, payload.factId)
                        else
                            store.Get(ctx.ScopeId, payload.factId)

                    match fact with
                    | None ->
                        // The fact no longer resolves (already superseded /
                        // erased). Nothing to recompute; retrying will not
                        // change that — a no-op success, not a failure.
                        logger.Info(
                            sprintf "RecomputeJobHandler: fact %s not found — nothing to recompute" payload.factId
                        )

                        return JobResult.Success
                    | Some fact ->
                        let! result =
                            if resolved then
                                RecomputeUnder.resolvedScope store recomputer ctx.Scope fact
                            else
                                FactInvalidation.recomputeNow store recomputer ctx.ScopeId fact

                        match result with
                        | Ok _ ->
                            // Ok (Some _) re-asserted (idempotent when
                            // unchanged); Ok None means no recompute path —
                            // both are terminal success, nothing to retry.
                            return JobResult.Success
                        | Error err ->
                            let msg =
                                sprintf "RecomputeJobHandler: recompute of fact %s failed — %s" payload.factId err

                            logger.Warn msg
                            // Recompute engines / stores fail transiently
                            // (upstream busy, lock contention); retry.
                            return JobResult.TransientFailure msg
        }

/// Construction + compose registration + the reactive orchestration.
module RecomputeJobHandler =

    /// Logical handler name registered with the scheduler at compose time.
    [<Literal>]
    let HandlerName = "_facts.recompute.run"

    /// The synthesised actor recorded as `CreatedBy` on a recompute job
    /// (the invalidation walk runs with no interactive user).
    [<Literal>]
    let private Actor = "_facts.invalidation"

    /// The serialised payload targeting one fact by id.
    let payloadFor (factId: string) : string =
        RecomputeJson.serialize { factId = factId }

    /// Create the handler over the fact store + the deployment's recomputer.
    let create (store: IFactStore) (recomputer: IFactRecomputer) (logger: ILogger) : IJobHandler =
        RecomputeJobHandler(store, recomputer, logger) :> IJobHandler

    /// The compose-time declaration that registers the recompute handler
    /// with the scheduler (a `Manual` trigger — recompute jobs are fired
    /// on demand by `reactToDataChange`, never on a schedule). Fold this
    /// into `ServerApp`'s scheduled-job declarations when composing the
    /// fact tier with `RecomputePolicy.Eager` metrics in play. Handoff:
    /// the compose hookup lives in `FactsCompose.fs` (a sibling's lease) —
    /// this declaration is ready to register there.
    let declaration (store: IFactStore) (recomputer: IFactRecomputer) (logger: ILogger) : ScheduledJobDeclaration =
        ScheduledJobDeclaration.create HandlerName (create store recomputer logger) Trigger.Manual

    /// The recompute job's registration for one fact in one shard.
    let private registrationFor (scopeId: string) (fact: Fact) : JobRegistration = {
        ScopeId = scopeId
        Handler = HandlerName
        Payload = payloadFor fact.FactId
        Trigger = Trigger.Manual
        Idempotency =
            Some {
                Key = "recompute-" + fact.FactId
                TtlSeconds = 3600
            }
        RetryPolicy = JobRetryPolicy.defaults
        ShardKey = Some fact.FactId
        Precision = JobPrecision.Minute
        CreatedBy = Actor
        Tags = Map.ofList [ "origin", "fact-invalidation" ]
    }

    /// Schedule (do not yet fire) a recompute job for one invalidated fact.
    /// Idempotency keys on the fact id, so a fact invalidated repeatedly
    /// within the TTL coalesces to one live job. `ShardKey` = the fact id
    /// so a distributed scheduler serialises a fact's recomputes
    /// (portability rule 5). This is the CARRIED form: the scope is a
    /// string, so the job is scheduled through the string overload and runs
    /// with the anonymous scope on `JobContext.Scope` (Phase 818); the
    /// handler then keys the store on the carried `ScopeId`.
    let scheduleRecompute
        (scheduler: IJobScheduler)
        (scopeId: string)
        (fact: Fact)
        : Async<Result<JobId, ScheduleError>> =
        scheduler.Schedule(registrationFor scopeId fact)

    /// Schedule a recompute job for one invalidated fact under the scope the
    /// data write was RESOLVED under (Phase 930), through the typed
    /// `Schedule` overload: the scheduler persists the scope and hands it
    /// back re-minted on `JobContext.Scope` when the job runs, so the handler
    /// reads and re-asserts through the store's typed members. Same
    /// idempotency and shard key as `scheduleRecompute`.
    let scheduleRecomputeUnder
        (scheduler: IJobScheduler)
        (scope: ResolvedScope)
        (fact: Fact)
        : Async<Result<JobId, ScheduleError>> =
        scheduler.Schedule(scope, registrationFor scope.ScopeId fact)

    /// Apply each invalidated head's metric `RecomputePolicy`. `schedule`
    /// is the scope form the change arrived in; the fire and the outcome
    /// classification are the same for both.
    let private applyPolicies
        (scheduler: IJobScheduler)
        (registry: Grounding.IMetricRegistry option)
        (scopeId: string)
        (schedule: Fact -> Async<Result<JobId, ScheduleError>>)
        (heads: Fact list)
        : Async<InvalidationOutcome list> =
        async {
            let mutable outcomes = []

            for fact in heads do
                match FactInvalidation.policyFor registry fact with
                | Grounding.Eager ->
                    let! scheduled = schedule fact

                    match scheduled with
                    | Ok jobId ->
                        // Manual-trigger job — fire it now so the recompute
                        // actually runs (invalidation is the trigger). The
                        // scope id here is the job's LOOKUP key, never a
                        // scope the job runs under.
                        let! _ = scheduler.TriggerOnce(scopeId, jobId, Actor)
                        outcomes <- Enqueued(fact.FactId, jobId) :: outcomes
                    | Error err -> outcomes <- ScheduleFailed(fact.FactId, sprintf "%A" err) :: outcomes
                | Grounding.OnQuery -> outcomes <- DeferredToQuery fact.FactId :: outcomes
                | Grounding.Manual -> outcomes <- SurfacedOnly fact.FactId :: outcomes

            return List.rev outcomes
        }

    /// React to a data-object version arrival (task 561.B + 561.C): derive
    /// the invalidation set from the changed object ids (their lineage
    /// descendants included), find the current-head facts whose inputs
    /// changed, and apply each fact's metric `RecomputePolicy` — `Eager`
    /// schedules + fires a recompute job, `OnQuery` defers to the read
    /// path, `Manual` surfaces only. Returns the per-fact outcome. Purely
    /// additive: with no invalidated facts (or no scheduler-eligible
    /// policy) it schedules nothing (GP 11 / GP 13).
    ///
    /// This is the CARRIED form: the scope is the shard string a data write
    /// carried, so the walk and the schedule use the string members. A
    /// write made under a resolved scope takes `reactToResolvedChange`.
    let reactToDataChange
        (lineage: ILineageStore)
        (store: IFactStore)
        (scheduler: IJobScheduler)
        (registry: Grounding.IMetricRegistry option)
        (scopeId: string)
        (changedObjectIds: string list)
        : Async<InvalidationOutcome list> =
        async {
            let! invalidated = FactInvalidation.invalidationSet lineage scopeId changedObjectIds
            let! heads = FactInvalidation.invalidatedHeads store scopeId invalidated
            return! applyPolicies scheduler registry scopeId (scheduleRecompute scheduler scopeId) heads
        }

    /// `reactToDataChange` for a write made under a RESOLVED scope (Phase
    /// 930): the heads are read through the store's typed `Query`, and each
    /// `Eager` recompute is scheduled through the typed `Schedule`, so the
    /// job runs under the scope the write carried. Lineage is keyed on the
    /// shard string the resolved scope names; that is the typed-to-string
    /// direction, which Phase 797 allows, never the reverse.
    let reactToResolvedChange
        (lineage: ILineageStore)
        (store: IFactStore)
        (scheduler: IJobScheduler)
        (registry: Grounding.IMetricRegistry option)
        (scope: ResolvedScope)
        (changedObjectIds: string list)
        : Async<InvalidationOutcome list> =
        async {
            let! invalidated = FactInvalidation.invalidationSet lineage scope.ScopeId changedObjectIds
            let! allHeads = store.Query(scope, FactQuery.all)
            let heads = allHeads |> List.filter (FactInvalidation.isInvalidated invalidated)
            return! applyPolicies scheduler registry scope.ScopeId (scheduleRecomputeUnder scheduler scope) heads
        }

    /// Route a change to the form its scope arrived in (Phase 930).
    let reactToChange
        (lineage: ILineageStore)
        (store: IFactStore)
        (scheduler: IJobScheduler)
        (registry: Grounding.IMetricRegistry option)
        (scope: DataChangeScope)
        (changedObjectIds: string list)
        : Async<InvalidationOutcome list> =
        match scope with
        | DataChangeScope.Resolved resolved ->
            reactToResolvedChange lineage store scheduler registry resolved changedObjectIds
        | DataChangeScope.Carried scopeId -> reactToDataChange lineage store scheduler registry scopeId changedObjectIds