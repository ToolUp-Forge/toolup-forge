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
// arrives via `JobContext` (`ScopeId`) + the deserialised payload (the
// target `FactId`); no in-memory state survives across dispatches.

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
                let! fact = store.Get(ctx.ScopeId, payload.factId)

                match fact with
                | None ->
                    // The fact no longer resolves (already superseded /
                    // erased). Nothing to recompute; retrying will not
                    // change that — a no-op success, not a failure.
                    logger.Info(sprintf "RecomputeJobHandler: fact %s not found — nothing to recompute" payload.factId)

                    return JobResult.Success
                | Some fact ->
                    let! result = FactInvalidation.recomputeNow store recomputer ctx.ScopeId fact

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

    /// Schedule (do not yet fire) a recompute job for one invalidated fact.
    /// Idempotency keys on the fact id, so a fact invalidated repeatedly
    /// within the TTL coalesces to one live job. `ShardKey` = the fact id
    /// so a distributed scheduler serialises a fact's recomputes
    /// (portability rule 5).
    let scheduleRecompute
        (scheduler: IJobScheduler)
        (scopeId: string)
        (fact: Fact)
        : Async<Result<JobId, ScheduleError>> =
        scheduler.Schedule {
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

    /// React to a data-object version arrival (task 561.B + 561.C): derive
    /// the invalidation set from the changed object ids (their lineage
    /// descendants included), find the current-head facts whose inputs
    /// changed, and apply each fact's metric `RecomputePolicy` — `Eager`
    /// schedules + fires a recompute job, `OnQuery` defers to the read
    /// path, `Manual` surfaces only. Returns the per-fact outcome. Purely
    /// additive: with no invalidated facts (or no scheduler-eligible
    /// policy) it schedules nothing (GP 11 / GP 13).
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

            let mutable outcomes = []

            for fact in heads do
                match FactInvalidation.policyFor registry fact with
                | Grounding.Eager ->
                    let! scheduled = scheduleRecompute scheduler scopeId fact

                    match scheduled with
                    | Ok jobId ->
                        // Manual-trigger job — fire it now so the recompute
                        // actually runs (invalidation is the trigger).
                        let! _ = scheduler.TriggerOnce(scopeId, jobId, Actor)
                        outcomes <- Enqueued(fact.FactId, jobId) :: outcomes
                    | Error err -> outcomes <- ScheduleFailed(fact.FactId, sprintf "%A" err) :: outcomes
                | Grounding.OnQuery -> outcomes <- DeferredToQuery fact.FactId :: outcomes
                | Grounding.Manual -> outcomes <- SurfacedOnly fact.FactId :: outcomes

            return List.rev outcomes
        }