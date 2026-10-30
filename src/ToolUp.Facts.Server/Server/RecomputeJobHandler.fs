// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

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
// **One scope form.** A recompute job runs under the `ResolvedScope` the
// platform's runner re-minted for it at execution — the job's scope crosses
// its persistence boundary as the carrier's sealed key, never as a string the
// handler trusts. A job that carries no re-minted scope (one scheduled before
// the carriers were typed, or by a scheduler that cannot re-mint) is REFUSED
// by name: reading its carried shard id would promote a string to a scope,
// and reading the anonymous shard would silently find nothing.

/// Recompute-job payload (JSON). The scope is carried by `JobContext`, so
/// the payload only names the fact to recompute.
type RecomputeJobPayload = { factId: string }

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
    /// `Eager` metric — scheduling the recompute job failed, or (Phase 1105)
    /// it was scheduled and its `TriggerOnce` was refused, so a `Manual`
    /// job that never fires on its own has not started; the error is
    /// carried for diagnostics.
    | ScheduleFailed of factId: string * error: string

/// Construction + compose registration + the reactive orchestration.
[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module RecomputeJobHandler =

    /// Logical handler name registered with the scheduler at compose time.
    [<Literal>]
    let HandlerName = "_facts.recompute.run"

    /// The recompute job: load the target fact, recompute its value through
    /// the deployment's `IFactRecomputer`, and re-assert.
    let job (store: IFactStore) (recomputer: IFactRecomputer) (logger: ILogger) : RunJob<RecomputeJobPayload> =
        RunJob.ofPayload HandlerName (fun ctx payload -> async {
            if ctx.Scope.IsAnonymous && ctx.ScopeId <> ResolvedScope.AnonymousScopeId then
                // No re-minted scope for a job registered under a real shard:
                // an unmigrated payload. Refused by name — never read under
                // its carried string, never under the anonymous shard.
                return
                    Error(
                        RunFailure.Terminal(
                            sprintf
                                "job %A is registered under '%s' but carries no resolver-minted scope — refused; re-schedule it under a resolved scope"
                                ctx.JobId
                                ctx.ScopeId
                        )
                    )
            elif ctx.Scope.ScopeId <> ctx.ScopeId then
                // A typed scope naming a shard other than the job's own is
                // never read under. A scheduler that re-mints hands back the
                // scope it registered under, so this is a defect upstream,
                // and retrying cannot mend it.
                return
                    Error(
                        RunFailure.Terminal(
                            sprintf
                                "job %A carries scope '%s' but is registered under '%s' — refused rather than read across shards"
                                ctx.JobId
                                ctx.Scope.ScopeId
                                ctx.ScopeId
                        )
                    )
            else
                match! store.Get(ctx.Scope, payload.factId) with
                | Error fault ->
                    return
                        Error(
                            RunFailure.Retryable(
                                sprintf
                                    "recompute of fact %s could not read it — %s"
                                    payload.factId
                                    (FactStoreError.describe fault)
                            )
                        )
                | Ok None ->
                    // The fact no longer resolves (already superseded /
                    // erased). Nothing to recompute; retrying will not
                    // change that — a no-op success, not a failure.
                    logger.Info(sprintf "%s: fact %s not found — nothing to recompute" HandlerName payload.factId)
                    return Ok()
                | Ok(Some fact) ->
                    match! FactInvalidation.recomputeNow store recomputer ctx.Scope fact with
                    // Ok (Some _) re-asserted (idempotent when unchanged);
                    // Ok None means no recompute path — both are terminal
                    // success, nothing to retry.
                    | Ok _ -> return Ok()
                    // Recompute engines / stores fail transiently (upstream
                    // busy, lock contention); retry.
                    | Error err ->
                        return
                            Error(RunFailure.Retryable(sprintf "recompute of fact %s failed — %s" payload.factId err))
        })

    /// The synthesised actor recorded as `CreatedBy` on a recompute job
    /// (the invalidation walk runs with no interactive user).
    [<Literal>]
    let private Actor = "_facts.invalidation"

    /// The serialised payload targeting one fact by id.
    let payloadFor (factId: string) : string =
        JobPayload.serialise { factId = factId }

    /// Create the handler over the fact store + the deployment's recomputer.
    let create (store: IFactStore) (recomputer: IFactRecomputer) (logger: ILogger) : IJobHandler =
        RunJob.handler logger (job store recomputer logger)

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

    /// Schedule (do not yet fire) a recompute job for one invalidated fact
    /// under the scope the data write was resolved under: the scheduler
    /// carries the scope and hands it back re-minted on `JobContext.Scope`
    /// when the job runs. Idempotency keys on the fact id, so a fact
    /// invalidated repeatedly within the TTL coalesces to one live job.
    /// `ShardKey` = the fact id so a distributed scheduler serialises a
    /// fact's recomputes (portability rule 5).
    let scheduleRecompute
        (scheduler: IJobScheduler)
        (scope: ResolvedScope)
        (fact: Fact)
        : Async<Result<ScheduleOutcome, ScheduleError>> =
        scheduler.Schedule(scope, registrationFor scope.ScopeId fact)

    /// Apply each invalidated head's metric `RecomputePolicy`.
    let private applyPolicies
        (scheduler: IJobScheduler)
        (registry: Grounding.IMetricRegistry option)
        (scope: ResolvedScope)
        (heads: Fact list)
        : Async<InvalidationOutcome list> =
        async {
            let scopeId = scope.ScopeId
            let mutable outcomes = []

            for fact in heads do
                match FactInvalidation.policyFor registry fact with
                | Grounding.Eager ->
                    let! scheduled = scheduleRecompute scheduler scope fact

                    match scheduled with
                    | Ok(Scheduled jobId | Coalesced jobId) ->
                        // Manual-trigger job — fire it now so the recompute
                        // actually runs (invalidation is the trigger). The
                        // scope id here is the job's LOOKUP key, never a
                        // scope the job runs under. Phase 1105 — a refused
                        // trigger is not `Enqueued`: the job never fires on
                        // its own, so it is reported as not started (and the
                        // next invalidation of the fact coalesces onto the
                        // same job and triggers it again).
                        match! scheduler.TriggerOnce(scopeId, jobId, Actor) with
                        | Ok() -> outcomes <- Enqueued(fact.FactId, jobId) :: outcomes
                        | Error refusal ->
                            let error =
                                sprintf
                                    "recompute job %O was scheduled but its trigger was refused: %s"
                                    jobId
                                    (TriggerError.message refusal)

                            outcomes <- ScheduleFailed(fact.FactId, error) :: outcomes
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
    /// The one recompute path: the heads are read under the resolved scope
    /// the write was made under, and each `Eager` recompute is scheduled
    /// under it, so the job runs under the same scope. Lineage is keyed on
    /// the shard id the resolved scope names (the typed-to-string direction,
    /// never the reverse). A store fault on the walk is raised
    /// (`FactStoreFault`), never read as nothing to invalidate.
    let reactToDataChange
        (lineage: ILineageStore)
        (store: IFactStore)
        (scheduler: IJobScheduler)
        (registry: Grounding.IMetricRegistry option)
        (scope: ResolvedScope)
        (changedObjectIds: string list)
        : Async<InvalidationOutcome list> =
        async {
            let! invalidated = FactInvalidation.invalidationSet lineage scope.ScopeId changedObjectIds

            let! heads =
                FactInvalidation.invalidatedHeads store scope invalidated
                |> FactStoreResult.orRaiseAsync

            return! applyPolicies scheduler registry scope heads
        }