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
// **Phase 674 — the conjunction.** Taint is carried as the SET of
// contributing policies rather than one representative, and a policy may
// declare a contributor scope (a party). A declassification routine then
// clears only the parties that accepted it, so a joint fact discloses only
// when the path satisfies EVERY contributing party's policy. Fail-closed:
// a party-scoped policy needs an explicit acceptance, and an unscoped
// policy behaves exactly as Phase 562, so a deployment declaring no scope
// is unchanged.
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
    /// Phase 674 — the contributing parties whose taint this crossing
    /// actually cleared (the routine's accepting scopes ∩ what reached it).
    /// Empty when the cleared policies declare no party — the Phase 562
    /// shape, in which no party accepted anything.
    AcceptedScopes: string list
}

/// The taint analysis for one target fact at one egress check.
type TaintOutcome = {
    /// The target inherits an undeclassified restriction from an upstream
    /// input ⇒ the gate must deny even though the target's own disclosure
    /// permits egress. `None` when the target is clean.
    ///
    /// Phase 674 keeps this as the *representative* of `InheritedPolicyRefs`
    /// (the nearest declared policy first) so the single-party verdict and
    /// its deny ref are byte-for-byte Phase 562.
    InheritedPolicyRef: string option
    /// Phase 674 — EVERY tainting policy that reaches the target and that
    /// no accepted declassification cleared. The conjunction is exactly
    /// "this list is empty": a path satisfies every contributing party's
    /// policy or the gate denies.
    InheritedPolicyRefs: string list
    /// Phase 674 — the contributing parties among `InheritedPolicyRefs`,
    /// distinct and sorted. These are the parties whose consent is missing;
    /// the deny names them.
    UnsatisfiedScopes: string list
    /// Phase 674 — every contributing party whose restricted data reaches
    /// the target's lineage at all, cleared or not (the contribution facet,
    /// plan D4). Distinct and sorted.
    ContributorScopes: string list
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
    /// which clears the taint its routine is entitled to clear (and whose
    /// clearing is recorded as a crossing). The target's verdict is driven
    /// by its *inputs*: it inherits whatever taint survives. The target's
    /// own disclosure is handled by the Phase 525 resolver, not here — so a
    /// directly-classified fact permitted at the surface is not self-denied.
    ///
    /// **Phase 674 — the conjunction.** Taint is carried as the SET of
    /// contributing policies rather than one representative, and a
    /// declassifier clears only the policies its `AcceptingScopes` entitles
    /// it to (`DisclosureTaintConfig.routineClears`). Two consequences, both
    /// the point of the phase:
    ///
    ///  - a routine accepted by party A does **not** clear party B's taint —
    ///    B's restriction survives A's declassification and the target
    ///    denies, so one party's consent can never launder another's data
    ///    (plan D3);
    ///  - the verdict is a conjunction over the contributing parties: every
    ///    one must be satisfied, and any survivor denies.
    ///
    /// Fail-closed throughout: a party-scoped policy is cleared only by an
    /// explicit acceptance, so an absent, unknown or unevaluable acceptance
    /// denies. An **unscoped** policy is cleared by any declared routine —
    /// exactly Phase 562 — so a deployment that declares no contributor
    /// scope is byte-for-byte unchanged (GP 11 / GP 13).
    let analyze (config: DisclosureTaintConfig) (graph: FactDerivationGraph) (targetId: string) : TaintOutcome =
        // Memoised output-taint per fact id: the distinct policy refs the
        // fact's output carries, nearest-declared first. Empty ⇒ clean.
        let memo = Dictionary<string, string list>()
        // Crossings deduped by declassifier fact id.
        let crossings = Dictionary<string, TaintCrossing>()
        // Phase 674 — every contributing party seen anywhere on the walked
        // lineage, cleared or not (the contribution facet).
        let contributors = HashSet<string>()

        let noteContributor (policyRef: string) =
            match DisclosureTaintConfig.scopeOf config policyRef with
            | Some party -> contributors.Add party |> ignore
            | None -> ()

        let recordCrossing (factId: string) (routine: DeclassificationRoutine) (cleared: string list) =
            if not (crossings.ContainsKey factId) then
                crossings[factId] <- {
                    DeclassifierFactId = factId
                    OperationId = routine.OperationId
                    Rationale = routine.Rationale
                    AcceptedScopes =
                        cleared
                        |> List.choose (DisclosureTaintConfig.scopeOf config)
                        |> List.distinct
                        |> List.sort
                }

        let rec outputTaint (visiting: Set<string>) (factId: string) : string list =
            match memo.TryGetValue factId with
            | true, cached -> cached
            | _ ->
                // Cycle guard — the content-addressed store is acyclic, but
                // never let a malformed graph loop.
                if visiting.Contains factId then
                    []
                else
                    let result =
                        match graph.Facts.TryFind factId with
                        // An input with no visible producing fact contributes
                        // no taint (it is not a taint source we can see).
                        | None -> []
                        | Some fact ->
                            let visiting' = Set.add factId visiting

                            let inputTaint =
                                graph.UpstreamOf factId |> List.collect (outputTaint visiting') |> List.distinct

                            match declassifierOf config fact with
                            | Some routine ->
                                // A declassifier clears only what its
                                // accepting scopes entitle it to; anything
                                // else flows on. The crossing is recorded
                                // when it actually cleared something.
                                let cleared, retained =
                                    inputTaint
                                    |> List.partition (DisclosureTaintConfig.routineClears config routine)

                                if not (List.isEmpty cleared) then
                                    recordCrossing factId routine cleared

                                retained
                            | None ->
                                // Own source first (so the deny names the
                                // nearest declared policy), then inherited —
                                // a fact can be a source AND carry upstream
                                // taint, and the conjunction needs both.
                                match taintSourcePolicy config fact with
                                | Some src -> src :: inputTaint |> List.distinct
                                | None -> inputTaint

                    // Every policy this fact's output carries is a
                    // contribution; a ref cleared upstream by a declassifier
                    // was already noted at the source fact that minted it,
                    // so clearing never erases the contribution facet.
                    for policyRef in result do
                        noteContributor policyRef

                    memo[factId] <- result
                    result

        // The target's inherited taint = the union of its inputs' output
        // taint, less whatever the target's own routine (if it is a
        // declassifier) is entitled to clear.
        let inputTaint =
            graph.UpstreamOf targetId
            |> List.collect (outputTaint Set.empty)
            |> List.distinct

        let targetFact = graph.Facts.TryFind targetId

        let inheritedPolicyRefs =
            match targetFact |> Option.bind (declassifierOf config) with
            | Some routine ->
                let cleared, retained =
                    inputTaint
                    |> List.partition (DisclosureTaintConfig.routineClears config routine)

                if not (List.isEmpty cleared) then
                    recordCrossing targetId routine cleared

                retained
            | None -> inputTaint

        // The target's own registered policy is a contribution too — a
        // party's own fact disclosed under its own policy is still that
        // party's data leaving (the audit facet, plan D4). Its egress
        // stance is the Phase 525 resolver's business, not the walk's.
        match targetFact with
        | Some fact ->
            match fact.Disclosure with
            | Restricted policyRef -> noteContributor policyRef
            | _ -> ()
        | None -> ()

        {
            InheritedPolicyRef = List.tryHead inheritedPolicyRefs
            InheritedPolicyRefs = inheritedPolicyRefs
            UnsatisfiedScopes =
                inheritedPolicyRefs
                |> List.choose (DisclosureTaintConfig.scopeOf config)
                |> List.distinct
                |> List.sort
            ContributorScopes = contributors |> List.ofSeq |> List.sort
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