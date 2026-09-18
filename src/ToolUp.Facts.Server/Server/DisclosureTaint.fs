// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System.Collections.Generic
open ToolUp.Platform

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
// **Phase 794 — computed lineage.** The graph above is INFERRED, and the
// sentence two paragraphs up states the consequence plainly: a derivation
// not routed through a `Series` output is invisible, and an invisible
// upstream carries no taint. That is a property of the projection, not of
// the computation — the transform tree that produced the frame knows what
// flowed into it. `analyzeWithLineage` therefore consumes a computed label
// where one exists (`ComputedLineage`, fed from the assembly algebra by
// `AssemblyTaint`) and uses the inferred graph only where none does. The
// fallback is not silent: it raises a `LineageInferred` finding naming the
// facts it fell back on, so a verdict resting on inference can never pass
// for one resting on computation. Verdicts are otherwise unchanged — a
// deployment wiring no transform trees is byte-for-byte Phase 674.
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
    /// Phase 794 — `InheritedPolicyRefs` as a lattice element. The same
    /// content, in the type that has a named join and executable laws; the
    /// list beside it keeps its nearest-declared-first ORDER, which is what
    /// picks `InheritedPolicyRef` and is why the Phase 562 deny ref is
    /// unchanged.
    InheritedLabel: TaintLabel
    /// Phase 794 — what the reader of this verdict must know about the
    /// EVIDENCE it rests on, over and above the verdict itself. Findings
    /// never change a verdict. The one raised here names the facts whose
    /// upstream was inferred from evidence linkage rather than computed from
    /// a transform tree, so a weaker derivation can never pass for a
    /// stronger one silently.
    Findings: DisclosureFinding list
}

/// Phase 794 — the COMPUTED side of lineage: the label a fact's own
/// transform tree computed for it, for the facts that have one.
///
/// The inferred graph beside it answers "what does the store's evidence
/// linkage suggest flowed in"; this answers "what does the computation say
/// flowed in". Where both exist the computed answer wins, because it is the
/// derivation rather than a projection of it — and, crucially, a fact with a
/// computed label needs no upstream edge at all, so a derivation that never
/// routed through a `Series` output stops being invisible.
///
/// `LabelOf` returns `None` for a fact with no computed lineage, which is
/// the fallback — recorded as a `LineageInferred` finding, never silent.
type ComputedLineage = { LabelOf: string -> TaintLabel option }

module ComputedLineage =

    /// Nothing is computed: every derivation falls back to the inferred
    /// graph and says so. What `analyze` passes, so a deployment that has
    /// wired no transform trees keeps its Phase 562 / 674 verdicts exactly
    /// and gains only the honesty about how they were reached.
    let none: ComputedLineage = { LabelOf = fun _ -> None }

    let ofMap (labels: Map<string, TaintLabel>) : ComputedLineage = { LabelOf = labels.TryFind }

    let ofList (labels: (string * TaintLabel) list) : ComputedLineage = ofMap (Map.ofList labels)

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
    ///
    /// **Phase 794 — computed lineage, with the inferred graph as a reported
    /// fallback.** Where `lineage` supplies a label a transform tree
    /// computed for a fact, that label IS the fact's upstream: the
    /// computation says what flowed in, so the inferred adjacency is neither
    /// consulted nor needed, and a derivation that never routed through a
    /// `Series` output is no longer invisible. Where it does not, the walk
    /// falls back to the inferred graph exactly as before — and records a
    /// `LineageInferred` finding naming the facts it fell back on, so no
    /// party can mistake an inference for a computation. A fact with no
    /// evidence inputs at all raises nothing: there is no derivation there to
    /// have inferred.
    let analyzeWithLineage
        (config: DisclosureTaintConfig)
        (lineage: ComputedLineage)
        (graph: FactDerivationGraph)
        (targetId: string)
        : TaintOutcome =
        // Memoised output-taint per fact id: the distinct policy refs the
        // fact's output carries, nearest-declared first. Empty ⇒ clean.
        let memo = Dictionary<string, string list>()
        // Crossings deduped by declassifier fact id.
        let crossings = Dictionary<string, TaintCrossing>()
        // Phase 674 — every contributing party seen anywhere on the walked
        // lineage, cleared or not (the contribution facet).
        let contributors = HashSet<string>()
        // Phase 794 — facts whose upstream the walk took from the inferred
        // graph because no transform tree had labelled them.
        let inferred = HashSet<string>()

        /// A fact with evidence inputs but no computed label had its upstream
        /// INFERRED. A fact with no inputs is a leaf: nothing was inferred
        /// about it, so reporting it would be noise on every clean lineage.
        let noteInferred (factId: string) (fact: Fact) =
            if not (List.isEmpty fact.Evidence.InputHashes) then
                inferred.Add factId |> ignore

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
                                match lineage.LabelOf factId with
                                | Some computed -> TaintLabel.policyRefs computed
                                | None ->
                                    noteInferred factId fact

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
        let targetFact = graph.Facts.TryFind targetId

        let inputTaint =
            match lineage.LabelOf targetId with
            | Some computed -> TaintLabel.policyRefs computed
            | None ->
                targetFact |> Option.iter (noteInferred targetId)

                graph.UpstreamOf targetId
                |> List.collect (outputTaint Set.empty)
                |> List.distinct

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
            InheritedLabel = TaintLabel.ofPolicyRefs inheritedPolicyRefs
            Findings =
                if inferred.Count = 0 then
                    []
                else
                    [ DisclosureFinding.LineageInferred(inferred |> List.ofSeq |> List.sort) ]
        }

    /// The walk with no computed lineage — every derivation taken from the
    /// inferred graph, which is what the store can offer on its own. Verdicts
    /// are byte-for-byte Phase 674; the only difference is that the outcome
    /// now SAYS its lineage was inferred.
    let analyze (config: DisclosureTaintConfig) (graph: FactDerivationGraph) (targetId: string) : TaintOutcome =
        analyzeWithLineage config ComputedLineage.none graph targetId

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

/// Phase 794 — the bridge between the dataset-assembly transform algebra
/// and the taint lattice: the one place the two tiers meet.
///
/// The algebra's fold (`AssemblyLabelling`) is generic in the label and
/// knows nothing about disclosure; the lattice (`TaintLabel`) knows nothing
/// about frames. This module supplies one to the other, so there is exactly
/// one join in the estate and the transform tier never grew a second.
module AssemblyTaint =

    /// The labelling the algebra's fold runs over: the lattice, plus the
    /// caller's declared label per source.
    let labelling (labelOfSource: AssemblySource -> TaintLabel) : TransformLabelling<TaintLabel> = {
        Bottom = TaintLabel.bottom
        Join = TaintLabel.join
        LabelOfSource = labelOfSource
    }

    /// A per-source labelling from declared `(source, policy refs)` pairs,
    /// keyed by the source's provenance identity so two bindings that name
    /// the same vintage agree.
    ///
    /// A source nobody declared labels as `bottom` — a positive statement
    /// that it carries no restricted data, not an oversight the model can
    /// detect. That is why the declaration is compose-time data: which party
    /// a source belongs to is knowledge the composition has and the frame
    /// does not.
    let labelOfSources (sourceLabels: (AssemblySource * string list) list) : AssemblySource -> TaintLabel =
        let byIdentity =
            sourceLabels
            |> List.map (fun (source, policyRefs) ->
                AssemblyProvenance.sourceIdentity source, TaintLabel.ofPolicyRefs policyRefs)
            |> Map.ofList

        fun source ->
            byIdentity.TryFind(AssemblyProvenance.sourceIdentity source)
            |> Option.defaultValue TaintLabel.bottom

    /// The label a labelled node's output frame carries — the join of
    /// everything that flowed into it.
    let labelOf (node: LabelledTransform<TaintLabel>) : TaintLabel = node.Label

    /// The label a single transform CONTRIBUTES, as a function of the node
    /// alone. Only `Join` contributes anything: it is where a second party
    /// enters the pipeline.
    let contributedBy (labelOfSource: AssemblySource -> TaintLabel) (transform: AssemblyTransform) : TaintLabel =
        AssemblyLabelling.contributedBy (labelling labelOfSource) transform

    /// The labelled pipeline of a spec: every node with the label its output
    /// carries, and the label of the frame the spec produces.
    let ofSpec (labelOfSource: AssemblySource -> TaintLabel) (spec: DatasetAssemblySpec) =
        AssemblyLabelling.ofSpec (labelling labelOfSource) spec

    /// The label of the frame a spec produces — what a fact asserted from any
    /// of its output versions inherits.
    let outputLabel (labelOfSource: AssemblySource -> TaintLabel) (spec: DatasetAssemblySpec) : TaintLabel =
        AssemblyLabelling.outputLabelOf (labelling labelOfSource) spec

    /// Declassification of a computed label under a declared routine — the
    /// one operator that lowers it, entitled exactly as far as
    /// `DisclosureTaintConfig.routineClears` says. The walk above filters its
    /// ordered ref list by the same predicate; that the two agree on every
    /// label is a stated law rather than an assumption.
    let declassify (config: DisclosureTaintConfig) (routine: DeclassificationRoutine) (label: TaintLabel) : TaintLabel =
        TaintLabel.narrowsTo (DisclosureTaintConfig.routineClears config routine) label

    /// The computed lineage for facts asserted from labelled assemblies:
    /// each fact takes the output label of the spec that produced it. The
    /// `ComputedLineage` the walk consumes, and the reason those facts raise
    /// no `LineageInferred` finding.
    let lineageOf
        (labelOfSource: AssemblySource -> TaintLabel)
        (producedBy: (string * DatasetAssemblySpec) list)
        : ComputedLineage =
        producedBy
        |> List.map (fun (factId, spec) -> factId, outputLabel labelOfSource spec)
        |> ComputedLineage.ofList