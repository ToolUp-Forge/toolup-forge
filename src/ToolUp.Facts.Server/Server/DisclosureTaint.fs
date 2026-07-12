// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System.Collections.Generic

// ─── DisclosureTaint (Phase 562) ─────────────────────────────────────
//
// The taint walk: information-flow control as *data* on the fact
// derivation graph, layered onto the Phase 525 egress verdict. For a fact
// whose derivation includes a `Restricted(TaintPropagating)` input, the
// gate inherits the restriction — unless the path from that tainted input
// down to the fact crosses a declared declassification routine (a
// `Computed` fact whose operation is in the catalog). Each crossing is an
// auditable event (GP 6).
//
// **The graph.** A fact-to-fact derivation edge exists where a downstream
// fact's `Evidence.InputHashes` names a data-object version that an
// upstream fact *produced* as its `Series` value — the only fact→fact
// derivation the fact store records (supersession is a *within-lineage*
// edge, not a derivation, and is deliberately NOT a taint conduit: a
// correction of a value is not a derivation *from* it). Absent that
// linkage a fact has no upstream and cannot be tainted — so a deployment
// that does not chain facts through series outputs sees no taint, exactly
// as before (GP 11).
//
// **Pure core.** The walk (`analyze`) is a pure function over an explicit
// `FactDerivationGraph`, so it is unit-testable with hand-built graphs and
// carries no store dependency; `buildGraph` projects an `IFactStore`
// listing onto that graph. The gate composes the two.

/// The fact derivation graph the taint walk runs over — every fact in
/// scope keyed by id, plus the immediate-upstream adjacency (a fact id →
/// the fact ids it was derived from). `UpstreamOf` returns `[]` for a leaf
/// (or an unknown id), so the walk terminates at inputs with no recorded
/// producer.
type FactDerivationGraph = {
    Facts: Map<string, Fact>
    UpstreamOf: string -> string list
}

/// One declassification crossing found on a fact's derivation — a
/// declared routine that cleared otherwise-inherited taint. Audited on the
/// disclose path (Phase 562.C / GP 6).
type TaintCrossing = {
    /// The declassifier fact on the path.
    DeclassifierFactId: string
    OperationId: string
    Rationale: string
}

/// The taint analysis for one target fact at one egress check.
type TaintOutcome = {
    /// The target inherits an undeclassified restriction from an upstream
    /// input ⇒ the gate must deny even though the target's own disclosure
    /// permits egress. `None` when the target is clean.
    InheritedPolicyRef: string option
    /// Declassification crossings on the target's derivation (deduped by
    /// declassifier fact id). Audited when the target discloses.
    Crossings: TaintCrossing list
}

module DisclosureTaint =

    /// The declassification routine a fact's method declares, when any — a
    /// `Computed` fact whose operation id is in the catalog. A
    /// `HumanAsserted` / `Imported` fact is never a declassifier (a routine
    /// is a deterministic operation, plan 562.B).
    let declassifierOf (config: DisclosureTaintConfig) (fact: Fact) : DeclassificationRoutine option =
        match fact.Method with
        | Computed(operationId, _, _) -> DisclosureTaintConfig.declassifierFor config operationId
        | HumanAsserted _
        | Imported _ -> None

    /// Whether a fact is a taint *source* — directly `Restricted` under a
    /// policy declared `TaintPropagating`.
    let private taintSourcePolicy (config: DisclosureTaintConfig) (fact: Fact) : string option =
        match fact.Disclosure with
        | Restricted policyRef when DisclosureTaintConfig.isTaintPropagating config policyRef -> Some policyRef
        | _ -> None

    /// Analyse the taint reaching `targetId` in `graph` under `config`.
    ///
    /// Taint of a fact's *output* propagates downstream along derivation
    /// edges: a fact's output is tainted iff it is a taint source itself OR
    /// any input's output is tainted — *unless* the fact is a declassifier,
    /// whose output is always clean (and whose clearing of a tainted input
    /// is recorded as a crossing). The target's verdict is driven by its
    /// *inputs*: it inherits taint iff an input's output is tainted and the
    /// target is not itself a declassifier. The target's own disclosure is
    /// handled by the Phase 525 resolver, not here — so a directly-
    /// classified fact permitted at the surface is not self-denied.
    let analyze (config: DisclosureTaintConfig) (graph: FactDerivationGraph) (targetId: string) : TaintOutcome =
        // Memoised output-taint per fact id: `Some policyRef` when the
        // fact's output carries taint (naming a representative source
        // policy), `None` when clean. Declassifier outputs are always
        // clean.
        let memo = Dictionary<string, string option>()
        // Crossings deduped by declassifier fact id.
        let crossings = Dictionary<string, TaintCrossing>()

        let recordCrossing (factId: string) (routine: DeclassificationRoutine) =
            if not (crossings.ContainsKey factId) then
                crossings[factId] <- {
                    DeclassifierFactId = factId
                    OperationId = routine.OperationId
                    Rationale = routine.Rationale
                }

        let rec outputTaint (visiting: Set<string>) (factId: string) : string option =
            match memo.TryGetValue factId with
            | true, cached -> cached
            | _ ->
                // Cycle guard — the content-addressed store is acyclic, but
                // never let a malformed graph loop.
                if visiting.Contains factId then
                    None
                else
                    let result =
                        match graph.Facts.TryFind factId with
                        // An input with no visible producing fact contributes
                        // no taint (it is not a taint source we can see).
                        | None -> None
                        | Some fact ->
                            let visiting' = Set.add factId visiting

                            let inputTaint = graph.UpstreamOf factId |> List.tryPick (outputTaint visiting')

                            match declassifierOf config fact with
                            | Some routine ->
                                // A declassifier clears taint; record the
                                // crossing when it actually had tainted input.
                                if inputTaint.IsSome then
                                    recordCrossing factId routine

                                None
                            | None ->
                                // Own source first (so the deny names the
                                // nearest declared policy), else inherited.
                                match taintSourcePolicy config fact with
                                | Some _ as src -> src
                                | None -> inputTaint

                    memo[factId] <- result
                    result

        // The target's inherited taint = any input's output taint, cleared
        // if the target is itself a declassifier (its output is clean and
        // the crossing is recorded).
        let inputTaint = graph.UpstreamOf targetId |> List.tryPick (outputTaint Set.empty)

        let inheritedPolicyRef =
            match graph.Facts.TryFind targetId |> Option.bind (declassifierOf config) with
            | Some routine ->
                if inputTaint.IsSome then
                    recordCrossing targetId routine

                None
            | None -> inputTaint

        {
            InheritedPolicyRef = inheritedPolicyRef
            Crossings = crossings.Values |> List.ofSeq
        }

    /// Project a fact listing onto a `FactDerivationGraph`. The upstream
    /// adjacency links a fact's `Evidence.InputHashes` to the facts whose
    /// `Series` value produced those data-object versions — the store's
    /// only fact→fact derivation signal. Facts with no such linkage are
    /// leaves (empty upstream), so a deployment that does not chain facts
    /// through series outputs has an edgeless graph and sees no taint.
    let buildGraph (facts: Fact list) : FactDerivationGraph =
        let byId = facts |> List.map (fun f -> f.FactId, f) |> Map.ofList

        // data-object version id → the fact that produced it as a Series
        // value. Last writer wins on a duplicate version (acceptable — a
        // version is content-addressed, so two producers of one version
        // are the same assertion).
        let producerOfVersion =
            facts
            |> List.choose (fun f ->
                match f.Value with
                | Series version -> Some(version, f.FactId)
                | _ -> None)
            |> Map.ofList

        let upstreamOf (factId: string) : string list =
            match byId.TryFind factId with
            | None -> []
            | Some fact ->
                fact.Evidence.InputHashes
                |> List.choose producerOfVersion.TryFind
                |> List.distinct

        {
            Facts = byId
            UpstreamOf = upstreamOf
        }