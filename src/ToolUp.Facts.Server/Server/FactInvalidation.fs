// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open ToolUp.Platform

// ─── Fact invalidation (Phase 561 — reactive fact recomputation) ──────
//
// Ladder rung 2 of the continuous-intelligence ladder: data lands → the
// lineage walk (Phase 8a) marks descendant facts `InputsChanged` → a
// per-metric recompute policy (Phase 561 `Grounding.RecomputePolicy`)
// decides what executes. This file holds the **pure derivation** (which
// facts are invalidated, and the upstream-aware freshness that gives the
// `UntilUpstreamChange` policy its real trigger) plus the deployment seam
// that actually recomputes a value. The *execution* — enqueuing a
// recompute job (Eager) or recomputing inline (OnQuery) — lives in
// `RecomputeJobHandler.fs`.
//
// **Derived, never stored (law L1).** `InputsChanged` is not a mutable
// flag on any fact — it is computed per call from the set of superseded
// input identities and the fact's own `Evidence.InputHashes`. A fact is
// never mutated by invalidation; recomputation re-asserts a *new* fact
// through the ordinary `IFactStore.Assert` path, so the supersession edge
// stays derived and the audit trail is the one assert always writes.
//
// **Zero-weight when unused (GP 11 / GP 13).** A metric that declares no
// `RecomputePolicy` resolves to `Manual`; the default `IFactRecomputer`
// (`NoFactRecomputer`) recomputes nothing. A deployment that wires neither
// composes and runs byte-for-byte identically to today.

/// The deployment-supplied seam that actually recomputes a fact's value —
/// the one part reactive recomputation cannot generically provide, since
/// re-running a metric's `ProducingOperation` needs the deployment's own
/// compute engine (GP 1: the fact tier owns storage + provenance, never
/// the domain computation). Given a fact whose inputs have changed, an
/// implementation produces the freshly-computed `FactDraft` to re-assert.
///
/// **Six portability rules (GP 12).** Identity by value (`string` scope,
/// domain records); async at the boundary; failure as data
/// (`Result`, no callbacks); stateless between calls (all state arrives
/// per-call). No cross-shard ordering or timing promise is made.
type IFactRecomputer =
    /// Recompute `fact`. `Ok (Some draft)` is the freshly-computed draft to
    /// re-assert (idempotent when the recompute output is unchanged —
    /// content addressing collapses it to a no-op). `Ok None` means there
    /// is no recompute path for this fact (no engine wired for the metric's
    /// producing operation) — a *surfaced* outcome, not an error, so the
    /// stale fact simply stands. `Error` is a transient failure the caller
    /// may retry (an `Eager` recompute job maps it to `TransientFailure`).
    abstract Recompute: scopeId: string * fact: Fact -> Async<Result<FactDraft option, string>>

/// The default recomputer — recomputes nothing (`Ok None` for every
/// fact). What a deployment that composes reactive recomputation *without*
/// wiring a compute engine gets: invalidation still derives correctly and
/// the changed state is surfaced, but no value is recomputed (GP 13).
type NoFactRecomputer() =
    interface IFactRecomputer with
        member _.Recompute(_scopeId: string, _fact: Fact) : Async<Result<FactDraft option, string>> = async {
            return Ok None
        }

/// Phase 889 — what the whole-store walks ask a DELEGATED metric instead of
/// enumerating its facts. A delegated population lives in a declared fact
/// table, not in the fact log, so coherence checking and invalidation take a
/// delegated path that puts the question to the table: its current run, the
/// rows for named subjects, the per-parent totals of its column. None of
/// them mints a fact or materialises the population in the fact tier.
///
/// Implemented by the delegated fact store (`DelegatedFactStore`), which is
/// also the composed `IFactStore` — so a walk handed the composed store can
/// discover the delegated paths by a type test, and a composition with no
/// delegate never sees this interface at all (GP 13).
///
/// Walks are platform-carried work (a coherence sweep, a refresh), so the
/// members take the persisted `string` scope, as the store's own walk-facing
/// overloads do.
type IDelegatedFactWalks =
    inherit IFactStore

    /// The composed delegate records, one per delegated (table, column),
    /// as declared (`Watermark = None`).
    abstract Delegates: DelegateFact list

    /// Ask the table for the delegate's current run — its watermark, row
    /// count and period reach — or `None` when the table has never
    /// committed in this scope.
    abstract CurrentRun: scopeId: string * metric: MetricRef -> Async<Result<DelegateRun option, DelegateRefusal>>

    /// Ask the table for the current run's rows of the named subjects (a
    /// bounded point read per subject). Subjects the run does not carry are
    /// simply absent from the answer.
    abstract CurrentRows:
        scopeId: string * metric: MetricRef * subjects: string list list ->
            Async<Result<DelegateTableRow list, DelegateRefusal>>

    /// Ask the table for the current run's per-parent totals of the
    /// delegate's column (one entry per parent path and period).
    abstract ChildTotals:
        scopeId: string * metric: MetricRef * periodOverlaps: TemporalExtent option ->
            Async<Result<DelegateChildTotal list, DelegateRefusal>>

/// The pure invalidation derivation + the OnQuery inline recompute path.
module FactInvalidation =

    /// Whether a fact's inputs have changed — any of its
    /// `Evidence.InputHashes` names a superseded input identity. Pure and
    /// derived (law L1); this is the `InputsChanged` state, computed, never
    /// stored.
    let isInvalidated (invalidatedInputs: Set<string>) (fact: Fact) : bool =
        fact.Evidence.InputHashes |> List.exists invalidatedInputs.Contains

    /// The set of input identities a data-object change invalidates — the
    /// changed object versions themselves, unioned with every lineage
    /// descendant (Phase 8a `ILineageStore.GetDescendants`). A fact
    /// computed directly from a changed object is invalidated; so is a fact
    /// computed from an object *derived from* a changed one, because that
    /// intermediate was itself produced from the now-stale input. Input
    /// identities and lineage object ids are the same string space (a fact
    /// cites a data-object version by its content hash, which is the
    /// lineage node's `ObjectId`).
    let invalidationSet
        (lineage: ILineageStore)
        (scopeId: string)
        (changedObjectIds: string list)
        : Async<Set<string>> =
        async {
            let! graphs =
                changedObjectIds
                |> List.map (fun objId -> lineage.GetDescendants(scopeId, objId))
                |> Async.Parallel

            let descendants =
                graphs
                |> Array.collect (fun g -> g.Nodes |> List.map _.ObjectId |> List.toArray)
                |> Set.ofArray

            return Set.union (Set.ofList changedObjectIds) descendants
        }

    /// The current-head facts in scope whose inputs have changed — the
    /// invalidation walk's result (task 561.B). Only current heads are
    /// returned: an already-superseded fact needs no re-invalidation (its
    /// successor is the live head). A method-less, now-visible query
    /// (`FactQuery.all`) yields the heads; `isInvalidated` filters them.
    let invalidatedHeads (store: IFactStore) (scopeId: string) (invalidatedInputs: Set<string>) : Async<Fact list> = async {
        let! heads = store.Query(scopeId, FactQuery.all)
        return heads |> List.filter (isInvalidated invalidatedInputs)
    }

    /// The effective `RecomputePolicy` for a fact — its metric's declared
    /// policy, or `Manual` when the metric is unregistered or declares
    /// none (GP 11). `None` registry (a deployment with no grounding
    /// vocabulary) is `Manual` for every fact.
    let policyFor (registry: Grounding.IMetricRegistry option) (fact: Fact) : Grounding.RecomputePolicy =
        registry
        |> Option.bind (fun reg -> reg.TryGetMetric fact.Metric.Value)
        |> Option.bind _.RecomputePolicy
        |> Grounding.RecomputePolicy.resolve

    /// Upstream-aware freshness — the real trigger Phase 561 gives the
    /// `UntilUpstreamChange` policy. `Freshness.derive` (Phase 520)
    /// degrades `UntilUpstreamChange` to `UntilSuperseded` because Stage 0
    /// had no invalidation signal; the signal exists now. When a *current*
    /// fact's inputs have changed under an `UntilUpstreamChange` metric it
    /// is stale from `now`; every other case defers to `Freshness.derive`
    /// unchanged — and `inputsChanged = false` is byte-for-byte the
    /// Stage-0 behaviour (GP 11).
    let deriveFreshness
        (policy: Grounding.StalenessPolicy)
        (fact: Fact)
        (isCurrent: bool)
        (inputsChanged: bool)
        (now: System.DateTime)
        : FactFreshness =
        match policy with
        | Grounding.UntilUpstreamChange when isCurrent && inputsChanged -> Stale now
        | _ -> Freshness.derive policy fact isCurrent now

    /// OnQuery inline recompute (task 561.C, the `OnQuery` arm): recompute
    /// a stale fact on the read path. `Ok (Some fact)` is the re-asserted
    /// head (the same fact when the recompute output was unchanged —
    /// content addressing makes `Assert` idempotent, so a re-assert of an
    /// identical tuple returns the existing fact and writes nothing).
    /// `Ok None` means there was no recompute path (no engine) and the
    /// existing fact stands. `Error` propagates a recompute / store
    /// failure. Never mutates a stored fact — recomputation is a fresh
    /// `Assert` and supersession stays derived.
    let recomputeNow
        (store: IFactStore)
        (recomputer: IFactRecomputer)
        (scopeId: string)
        (fact: Fact)
        : Async<Result<Fact option, string>> =
        async {
            let! recomputed = recomputer.Recompute(scopeId, fact)

            match recomputed with
            | Error e -> return Error e
            | Ok None -> return Ok None
            | Ok(Some draft) ->
                let! asserted = store.Assert(scopeId, draft)

                match asserted with
                | Ok f -> return Ok(Some f)
                | Error e -> return Error e
        }

    // ── The delegated path (Phase 889) ─────────────────────────────

    /// Whether a fact was minted from a run of the delegate's table OTHER
    /// than the current one — the delegated `InputsChanged` state. A minted
    /// fact names its run by watermark token among its input hashes, and a
    /// commit advancing the table's watermark changes that input; so this is
    /// `isInvalidated` over "every run of this table but `currentToken`",
    /// derived per call and never stored (law L1).
    let isMintedUnderStaleRun (delegateFact: DelegateFact) (currentToken: string) (fact: Fact) : bool =
        fact.Metric = delegateFact.Metric
        && DelegateFact.covers delegateFact fact.Subject
        && Fact.methodIdentity fact.Method = Fact.methodIdentity delegateFact.Method
        && fact.Evidence.InputHashes
           |> List.exists (fun input -> DelegateFact.isRunToken delegateFact.TableId input && input <> currentToken)

    /// The delegated invalidation walk: the current heads minted from a
    /// superseded run of the delegate's table. The read is narrowed to the
    /// delegated metric and its method, so it enumerates what was QUOTED —
    /// never the population, which the fact tier does not hold — and the
    /// table is asked nothing but its current watermark (the caller's
    /// `currentToken`).
    let staleDelegatedHeads
        (store: IFactStore)
        (scopeId: string)
        (delegateFact: DelegateFact)
        (currentToken: string)
        : Async<Fact list> =
        async {
            let! heads =
                store.Query(
                    scopeId,
                    {
                        FactQuery.all with
                            Metric = Some delegateFact.Metric
                            Method = Some delegateFact.Method
                    }
                )

            return heads |> List.filter (isMintedUnderStaleRun delegateFact currentToken)
        }