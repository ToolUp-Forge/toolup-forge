// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open ToolUp.Platform.Usage

// ─── Phase 451 — enforcing the budget ────────────────────────────────────
//
// Two enforcement points, one guard. `ComputeBudgetGuard` owns the whole
// admit → audit → settle → meter cycle; `BudgetedComputeDispatcher` is a
// thin `IExternalComputeDispatcher` decorator over it, and the fit-enqueue
// path (`ModelFitBatch.submitBudgeted`) calls the same guard directly.
//
// **Why both, when one looks like it should be enough.** They are not two
// routes to the same seam — they are structurally unrelated paths to
// spending money, and a federated peer only ever takes the second. A peer
// `SubmitFit` lands on `ModelExecutionApi.SubmitFit`, which enqueues a fit
// job that runs `IModelFitProvider.Fit` **in this process**; it never calls
// `IExternalComputeDispatcher.Submit` at all. Budgeting only the dispatcher
// would leave the one submitter class the mechanism exists for — an agent,
// arriving over the federation seam — entirely ungoverned. Budgeting only
// the enqueue would miss every consumer that brokers heavy work to a GPU
// backend directly. Both, or neither is worth having.
//
// **A refusal never reaches the inner dispatcher.** The decorator decides
// on the spec and, when the answer is no, does not touch `inner` — the
// payload never leaves this process and no backend is asked to start work
// the deployment cannot pay for. Same shape as Phase 478's profile gate,
// and for the same reason: a check performed after the backend accepted
// the work is a check on something that has already left.
//
// **Composition order: memo OUTSIDE budget** —
// `MemoizedComputeDispatcher(BudgetedComputeDispatcher(backend))`. Phase
// 485 specified this before this phase existed, and it is load-bearing: a
// memo hit returns before the inner dispatcher is consulted at all, so it
// spends no allowance and holds no concurrency slot. The reverse order
// charges for every cache hit, which is the feature not working.
//
// **This decorator does NOT implement `IIsolatedComputeBackend`, and must
// not.** It is not a backend and has no posture of its own; Phase 485's
// header sets out why re-declaring the inner posture is wrong in both
// directions. Claiming nothing is the honest reading.
//
// **The reservation index is in-memory, and the failure that implies is
// stated rather than hidden.** `Submit` records the reservation against
// the handle so the matching `Poll` can settle it. A restart between the
// two loses the entry: the run still completes and the caller still gets
// its outcome from the backend, but the concurrency slot stays held until
// the period rolls. Failing by holding a slot refuses work; it never
// admits work it should have refused, which is the only direction this may
// fail in. Making it durable would mean a blob write per submission and a
// blob read per poll on a path Phase 485 deliberately kept free of both.

/// Phase 451 — how a deployment converts one run into abstract cost units.
///
/// **A record of functions, not an interface** (GP 12 rule 3 — behaviour
/// as data). A cost model is deployment policy that changes with a
/// contract renegotiation, not a capability a companion implements, and
/// expressing it as data means composing one is a `let`, not a type.
///
/// Both functions take the submission's advisory hints
/// (`ExternalWorkSpec.ResourceHints`, or `Map.empty` for a fit whose cost
/// is duration-only), because a hint map is the one place a caller has
/// already declared what the work needs — `"gpu" -> "1"`,
/// `"accelerator" -> "a100"` — and re-deriving that from the opaque
/// payload is exactly what forge must not do.
type ComputeCostModel = {
    /// Units reserved when a submission is admitted, before it runs.
    ///
    /// A reservation must be non-zero for a period allowance to bound
    /// anything at admission time: a model that reserved nothing would
    /// let an unbounded number of runs start against an exhausted budget
    /// and only discover it as they settled.
    Admit: Map<string, string> -> decimal
    /// Units the run is finally charged, given how long it actually ran.
    /// The difference against the reservation is what `Settle` folds in,
    /// so this may legitimately be smaller.
    Settle: Map<string, string> -> TimeSpan -> decimal
}

[<RequireQualifiedAccess>]
module ComputeCostModel =

    /// One unit per run, whatever it does and however long it takes.
    ///
    /// **The default**, because it is the only model that is correct
    /// without knowing anything about the backend: it turns
    /// `PeriodAllowance` into "how many runs may this scope start this
    /// period", which is a sentence an operator can evaluate on day one.
    /// A duration- or price-based default would be a guess about a
    /// backend forge has never seen, presented as an accounting fact.
    let perRun: ComputeCostModel = {
        Admit = fun _ -> 1M
        Settle = fun _ _ -> 1M
    }

    /// One unit per started minute of wall-clock, reserving `reserve`
    /// units up front.
    ///
    /// Rounds **up**, so a run that took one second costs one unit rather
    /// than nothing — a model that rounded down would make a flood of
    /// short runs free, which is the cheapest way to exhaust a backend.
    let perMinute (reserve: decimal) : ComputeCostModel = {
        Admit = fun _ -> reserve
        Settle = fun _ elapsed -> max 1M (decimal (ceil elapsed.TotalMinutes))
    }

    /// Read the cost from a numeric resource hint, falling back to
    /// `fallback` when the hint is absent or unparseable.
    ///
    /// The hint is caller-supplied and therefore **not** trusted as a
    /// discount: a value below `fallback` is ignored, so a submitter
    /// cannot make its own work cheap by declaring it so. Declaring work
    /// *more* expensive is honest and is honoured.
    let fromHint (hintKey: string) (fallback: decimal) : ComputeCostModel =
        let read (hints: Map<string, string>) =
            match hints.TryFind hintKey with
            | Some raw ->
                match
                    Decimal.TryParse(raw, Globalization.NumberStyles.Number, Globalization.CultureInfo.InvariantCulture)
                with
                | true, value -> max fallback value
                | _ -> fallback
            | None -> fallback

        {
            Admit = read
            Settle = fun hints _ -> read hints
        }

/// Phase 451 — a reservation held against one admitted run, so its
/// terminal outcome can release the right slot in the right period.
type ComputeBudgetReservation = {
    ScopeId: string
    /// The period the reservation was made in. Held rather than
    /// recomputed at settle time, so a run spanning a period boundary
    /// releases the slot it actually took rather than one in the new
    /// period that was never reserved.
    PeriodKey: string
    /// Units reserved at admission.
    ReservedCost: decimal
    /// The submission's hints, for the settle-time cost computation.
    Hints: Map<string, string>
    /// When the run was admitted (UTC) — the base for elapsed wall-clock.
    AdmittedAt: DateTime
}

/// Phase 451 — the enforcement points' shared vocabulary for *which*
/// surface refused, recorded on every audit row.
[<RequireQualifiedAccess>]
module ComputeBudgetSurface =
    /// The `IExternalComputeDispatcher.Submit` decorator.
    [<Literal>]
    let ExternalCompute = "external-compute"

    /// The fit-job enqueue path (`ModelFitBatch.submitBudgeted`), which is
    /// also where a federated peer's fit submission is caught.
    [<Literal>]
    let ModelFitEnqueue = "model-fit-enqueue"

/// Phase 451 — the admit → audit → settle → meter cycle, shared by both
/// enforcement points so they cannot drift.
///
/// `audit` is optional but effectively expected: a refusal nobody can see
/// afterwards is a support ticket with no answer (GP 6). `meter` is the
/// Phase 9d integration, supplied as `IUsageLog.Record` by composition —
/// a function rather than the interface so this file does not have to
/// compile after `IUsageLog.fs`, and so a deployment can meter somewhere
/// else without implementing a store.
type ComputeBudgetGuard
    (
        store: IComputeBudgetStore,
        ?costModel: ComputeCostModel,
        ?audit: IAuditLog,
        ?meter: UsageRecord -> Async<unit>,
        ?warnThreshold: decimal,
        ?logger: ILogger,
        ?clock: unit -> DateTime
    ) =

    let costModel = defaultArg costModel ComputeCostModel.perRun
    let now = defaultArg clock (fun () -> DateTime.UtcNow)

    /// Fraction of the period allowance at which an admitted submission
    /// emits `ComputeBudgetWarning`. 0.8 — late enough not to cry wolf,
    /// early enough that an operator raising a budget still has a fifth
    /// of it to do so in.
    let warnThreshold = defaultArg warnThreshold 0.8M

    let record (scopeId: string) (event: AuditEvent) = async {
        match audit with
        | Some log ->
            do! log.Record(scopeId, event)
            return ()
        | None -> return ()
    }

    /// Scopes already warned in a given period, so the crossing is
    /// reported once rather than on every subsequent submission. Keyed by
    /// `scope|period`, so it self-expires when the period rolls (the new
    /// period is a new key that has never been warned).
    let warned = ConcurrentDictionary<string, bool>()

    /// The denial as an `ExternalComputeError`, for the one seam whose
    /// signature predates this phase and cannot carry the typed shape.
    ///
    /// **Terminal, never retriable.** `Retriable` is the retry decision as
    /// data (GP 12 rule 3), and re-submitting an identical over-budget
    /// request cannot succeed — the allowance does not refill on the
    /// timescale a retry loop operates on, so a caller that retried would
    /// convert one refusal into a hot loop against a budget that is by
    /// definition already exhausted.
    static member toComputeError(denial: ComputeBudgetDenial) : ExternalComputeError =
        ExternalComputeError.terminal (ComputeBudgetDenial.describe denial)

    /// The cost model in force — exposed so a caller can reason about
    /// what a reservation will cost before making one.
    member _.CostModel = costModel

    /// Ask whether one run may start, and reserve it if so.
    ///
    /// `declaredDuration` is the submission's own wall-clock bound
    /// (`ExternalWorkSpec.Timeout`, `None` for a fit). `kind` and
    /// `submittedBy` are recorded on the audit row only.
    ///
    /// `Ok` carries the reservation the caller must hand back to `Settle`
    /// when the run reaches a terminal outcome, plus — for the dispatcher
    /// — the effective duration the submission should carry once the
    /// run-duration cap has been applied.
    member _.Admit
        (
            scopeId: string,
            submitter: SubmitterClass,
            kind: string,
            submittedBy: string,
            surface: string,
            hints: Map<string, string>,
            declaredDuration: TimeSpan option
        ) : Async<Result<ComputeBudgetReservation * TimeSpan option, ComputeBudgetDenial>> =
        async {
            let! budget = store.GetBudget scopeId
            let limits = ComputeBudget.limitsFor submitter budget

            if ComputeBudgetLimits.isUnrestricted limits then
                // Nothing constrains this class. One map lookup, then the
                // caller's own path — no usage read, no write, no audit
                // (GP 13). This is the branch every deployment that
                // enables budgets for ONE class takes for the others.
                return
                    Ok(
                        {
                            ScopeId = scopeId
                            PeriodKey = ComputeBudgetPeriod.key budget.Period (now ())
                            ReservedCost = 0M
                            Hints = hints
                            AdmittedAt = now ()
                        },
                        declaredDuration
                    )
            else

                let periodKey = ComputeBudgetPeriod.key budget.Period (now ())
                let cost = costModel.Admit hints

                let decide (usage: ComputeBudgetUsage) =
                    ComputeBudgetPolicy.admit scopeId submitter limits usage declaredDuration cost

                match! store.Admit(scopeId, periodKey, cost, decide) with
                | Error denial ->
                    do!
                        record
                            scopeId
                            (ComputeBudgetDenied {
                                Denial = denial
                                Surface = surface
                                Kind = kind
                                SubmittedBy = submittedBy
                                RefusedAt = now ()
                            })

                    match logger with
                    | Some log -> log.Warn(ComputeBudgetDenial.describe denial)
                    | None -> ()

                    return Error denial
                | Ok usage ->
                    // Threshold warning on the ADMITTED path — a leading
                    // indicator, emitted once per scope+period crossing.
                    if
                        limits.PeriodAllowance > 0M
                        && usage.Spent >= limits.PeriodAllowance * warnThreshold
                    then
                        let warnKey = scopeId + "|" + periodKey

                        if warned.TryAdd(warnKey, true) then
                            do!
                                record
                                    scopeId
                                    (ComputeBudgetWarning {
                                        ScopeId = scopeId
                                        SubmitterClass = SubmitterClass.label submitter
                                        PeriodKey = periodKey
                                        Quota = limits.PeriodAllowance
                                        Spent = usage.Spent
                                        Threshold = warnThreshold
                                        Surface = surface
                                        ObservedAt = now ()
                                    })

                    return
                        Ok(
                            {
                                ScopeId = scopeId
                                PeriodKey = periodKey
                                ReservedCost = cost
                                Hints = hints
                                AdmittedAt = now ()
                            },
                            ComputeBudgetPolicy.effectiveDuration limits declaredDuration
                        )
        }

    /// Give `reservation` back in full — the run never started.
    ///
    /// **Distinct from `Settle`, and the difference is not cosmetic.**
    /// `Settle` charges what a run that HAPPENED actually cost, which under
    /// the default flat per-run model is exactly what was reserved, so
    /// settling an unstarted run leaves the whole reservation spent. That
    /// is the wrong answer twice over: a caller retrying a batch the budget
    /// refused would burn allowance on every attempt without ever running
    /// anything, and a backend having a bad afternoon would consume a
    /// scope's whole period allowance in refusals.
    ///
    /// Nothing is metered — the Phase 9d ledger records consumption, and
    /// nothing was consumed.
    member _.Release(reservation: ComputeBudgetReservation) : Async<unit> = async {
        if reservation.ReservedCost = 0M then
            return ()
        else
            do! store.Settle(reservation.ScopeId, reservation.PeriodKey, -reservation.ReservedCost)
    }

    /// Release `reservation` on a terminal outcome, folding the actual
    /// cost in and metering it (Phase 9d).
    ///
    /// `elapsed` defaults to the wall-clock since admission. A caller that
    /// knows better — a fit provider reports its own deterministic
    /// duration — passes it explicitly, which matters because a
    /// reproducible fit's cost must not vary with how loaded the box was.
    member _.Settle(reservation: ComputeBudgetReservation, ?elapsed: TimeSpan) : Async<unit> = async {
        // A zero reservation is the unrestricted fast path — nothing was
        // reserved, so there is nothing to release and nothing to meter.
        if reservation.ReservedCost = 0M then
            return ()
        else
            let ran = defaultArg elapsed (now () - reservation.AdmittedAt)
            let actual = costModel.Settle reservation.Hints ran

            do! store.Settle(reservation.ScopeId, reservation.PeriodKey, actual - reservation.ReservedCost)

            match meter with
            | Some emit ->
                do!
                    emit {
                        RecordId = Guid.NewGuid()
                        ScopeId = reservation.ScopeId
                        ResourceKind = ResourceKinds.computeUnits
                        Quantity = actual
                        Unit = "units"
                        Origin = None
                        Metadata = Map [ "period", reservation.PeriodKey ]
                        Timestamp = now ()
                    }
            | None -> return ()
    }

/// Phase 451 — an `IExternalComputeDispatcher` that refuses a submission
/// the scope's compute budget cannot pay for, and settles the reservation
/// when the run reaches a terminal outcome.
///
/// Wraps ANY dispatcher — a companion backend, Phase 478's profile gate,
/// Phase 484's router. Compose it **inside** Phase 485's memo
/// (`MemoizedComputeDispatcher(BudgetedComputeDispatcher(backend))`) so a
/// cache hit costs no allowance; see the file header.
type BudgetedComputeDispatcher(inner: IExternalComputeDispatcher, guard: ComputeBudgetGuard, ?maxTracked: int) =

    /// Reservations awaiting a terminal `Poll`, keyed by handle id.
    /// Bounded so a deployment that never polls its handles cannot grow
    /// the process; the oldest entries drain FIFO. An evicted entry
    /// leaks its concurrency slot until the period rolls — the same
    /// failure the file header describes for a restart, in the same safe
    /// direction.
    let cap = defaultArg maxTracked 10_000
    let reservations = ConcurrentDictionary<Guid, ComputeBudgetReservation>()
    let order = ConcurrentQueue<Guid>()

    /// Bound a single `track`'s eviction work. One call adds one entry, so
    /// steady state removes one victim; the batch caps the catch-up drain
    /// after a concurrent burst. A residue is fine — the next call
    /// continues it.
    let drainBatch = 64

    let track (handleId: Guid) (reservation: ComputeBudgetReservation) =
        reservations[handleId] <- reservation
        order.Enqueue handleId

        let mutable drained = 0
        let mutable draining = true

        while draining && reservations.Count > cap do
            let mutable victim = Unchecked.defaultof<Guid>

            if order.TryDequeue &victim then
                reservations.TryRemove victim |> ignore
                drained <- drained + 1
                draining <- drained < drainBatch
            else
                // Cap-race: over cap with a momentarily empty queue.
                // Accept the transient over-cap rather than spinning, and
                // NEVER wipe — every entry discarded here is a
                // concurrency slot leaked until the period rolls (Phase
                // 328's discipline, learned on the idempotency store).
                draining <- false

    interface IExternalComputeDispatcher with
        /// The inner dispatcher's label, unchanged. The budget mints no
        /// handles of its own, and a handle must poll against a
        /// dispatcher that answers to the name it carries.
        member _.Backend = inner.Backend

        member _.Submit(scopeId: string, spec: ExternalWorkSpec) = async {
            match!
                guard.Admit(
                    scopeId,
                    spec.SubmitterClass,
                    spec.Kind,
                    "",
                    ComputeBudgetSurface.ExternalCompute,
                    spec.ResourceHints,
                    spec.Timeout
                )
            with
            | Error denial ->
                // The inner dispatcher is not consulted at all. The
                // payload never leaves this process.
                return Error(ComputeBudgetGuard.toComputeError denial)
            | Ok(reservation, effectiveTimeout) ->
                // The run-duration cap is applied by CLAMPING the spec's
                // timeout, so a submission that declared none is bounded
                // by the budget rather than by the backend's default.
                let governed = { spec with Timeout = effectiveTimeout }

                match! inner.Submit(scopeId, governed) with
                | Ok handle ->
                    track handle.HandleId reservation
                    return Ok handle
                | Error error ->
                    // The backend refused, so nothing is running and
                    // nothing was consumed: the reservation is given back
                    // in FULL rather than settled. Settling would charge a
                    // completed run's cost for work that never started, and
                    // a backend having a bad afternoon would then exhaust a
                    // scope's whole period allowance in refusals.
                    do! guard.Release reservation
                    return Error error
        }

        member _.Poll(handle: ExternalHandle) = async {
            let! outcome = inner.Poll handle

            if ExternalOutcome.isTerminal outcome then
                // `TryRemove` rather than a read-then-remove: a
                // concurrent poll of the same handle — which Phase 319's
                // reconciliation makes ordinary — must settle exactly
                // once, and only the caller that wins the removal does.
                match reservations.TryRemove handle.HandleId with
                | true, reservation -> do! guard.Settle reservation
                | _ -> ()

            return outcome
        }

        member _.Cancel(handle: ExternalHandle) = async {
            do! inner.Cancel handle

            // Cancel is a REQUEST, not a terminal state — the backend may
            // still be tearing down and the handle resolves to `Cancelled`
            // on a later poll. Settling here would release the slot while
            // the work is still running, so the reservation is left for
            // `Poll` to settle when the outcome is actually terminal.
            return ()
        }