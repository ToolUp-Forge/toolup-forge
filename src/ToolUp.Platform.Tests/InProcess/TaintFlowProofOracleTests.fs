// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.TaintFlowProofOracleTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts

// ─── Phase 795 — the proved taint flow as oracle ─────────────────────
//
// `proofs/TaintFlow.fst` models what Phase 794 landed — the taint-label
// lattice, the label-generic fold over the transform algebra,
// `routineClears`, and the derivation walk the conjunction reads — and
// proves seven lemmas over them. `proofs/check.ps1` extracts that model
// to F# and byte-compares the result against the committed
// `proofs/oracle/TaintFlow.fs`, which this pack references and runs.
//
// **A proof is about the MODEL, and this pack is the only thing that
// says the model is about the code.** Every arm below is differential in
// the shape Phases 787 and 790 established: a value goes through
// production and through the extracted model, and the two must agree.
//
// **Two arms measure a RELATIONAL law directly rather than comparing
// outputs**, because that is the only honest way to test one. For the
// flow half: relabel one party's inputs, run the pipeline twice, and
// require the outputs to be identical wherever the computed label says
// that party did not reach them. For the trace half: the same over a
// room's ordered sequence of releases, comparing the other party's
// SLICE — so ordering and count are compared, not one output at a time.
// Both carry a sample-adequacy guard, because a relational law over a
// sample in which the premise never holds passes for free.
//
// **The go-red case is committed, and it is the blind `Join`.** An
// oracle whose fold forgets that a `Join` brings a second source into
// the pipeline reports a clean label for a frame that carries another
// party's data — the exact mistake the labelled algebra exists to make
// impossible — and the comparison must CATCH it. A differential that has
// never been shown to fail agrees with whatever it is shown.

// ─── The bridge ──────────────────────────────────────────────────────
//
// Case for case, and short on purpose: a defect here would make the
// comparison compare the wrong thing.

let private toModelOpt (o: 'a option) : TaintFlow.opt<'a> =
    match o with
    | Some x -> TaintFlow.OSome x
    | None -> TaintFlow.ONone

/// A label out of production. `TaintLabel.policyRefs` is the set's own
/// contents, so nothing is invented on the way in.
let private toModelLabel (label: TaintLabel) : TaintFlow.label = TaintLabel.policyRefs label

/// …and back. `TaintLabel.ofPolicyRefs` is `Set.ofSeq`, which is exactly
/// the normalisation the model's label equality stands for: the model
/// carries a list because F* has no set here, and the read-back is where
/// that list becomes production's own type again.
let private ofModelLabel (label: TaintFlow.label) : TaintLabel = TaintLabel.ofPolicyRefs label

let private toModelRoutine (routine: DeclassificationRoutine) : TaintFlow.routine = {
    accepting_scopes = routine.AcceptingScopes
}

/// The transform DU, case for case. The payload fields of the four
/// non-`Join` cases are dropped because `AssemblyLabelling.contributedBy`
/// reads none of them; `Join`'s right source is kept because it is the
/// one input the labelling does read.
let private toModelTransform (transform: AssemblyTransform) : TaintFlow.transform<AssemblySource> =
    match transform with
    | AssemblyTransform.Join(right, _, _, _) -> TaintFlow.TJoin right
    | AssemblyTransform.Resample _ -> TaintFlow.TResample
    | AssemblyTransform.Lag _ -> TaintFlow.TLag
    | AssemblyTransform.Window _ -> TaintFlow.TWindow
    | AssemblyTransform.Filter _ -> TaintFlow.TFilter

let private toModelLabelling (labelOfSource: AssemblySource -> TaintLabel) : TaintFlow.labelling<AssemblySource> =
    fun source -> toModelLabel (labelOfSource source)

/// The model's labelled pipeline, read back into production's record so
/// the two can be compared structurally — every node's label AND what it
/// contributed, not merely the output.
let private modelLabelAssembly
    (labelOfSource: AssemblySource -> TaintLabel)
    (baseSource: AssemblySource)
    (transforms: AssemblyTransform list)
    : LabelledAssembly<TaintLabel> =
    let assembled =
        TaintFlow.label_assembly (toModelLabelling labelOfSource) baseSource (transforms |> List.map toModelTransform)

    {
        BaseLabel = ofModelLabel assembled.la_base_label
        Nodes =
            List.zip transforms assembled.la_nodes
            |> List.map (fun (transform, node) -> {
                Transform = transform
                Label = ofModelLabel node.lt_label
                Contributed = ofModelLabel node.lt_contributed
            })
        OutputLabel = ofModelLabel assembled.la_output_label
    }

/// **The go-red oracle.** The model's fold with the `Join` contribution
/// blinded: every case contributes the identity, so a second source's
/// label never enters the pipeline and the output reports whatever the
/// base alone carried. Everything else — the node list, the ordering, the
/// join itself — is the model's.
let private blindJoinLabelAssembly
    (labelOfSource: AssemblySource -> TaintLabel)
    (baseSource: AssemblySource)
    (transforms: AssemblyTransform list)
    : LabelledAssembly<TaintLabel> =
    let blind (_: AssemblySource) = TaintLabel.bottom

    let assembled =
        TaintFlow.label_assembly (toModelLabelling blind) baseSource (transforms |> List.map toModelTransform)

    {
        BaseLabel = labelOfSource baseSource
        Nodes =
            List.zip transforms assembled.la_nodes
            |> List.map (fun (transform, _) -> {
                Transform = transform
                Label = labelOfSource baseSource
                Contributed = TaintLabel.bottom
            })
        OutputLabel = labelOfSource baseSource
    }

// ─── The two-party vocabulary ────────────────────────────────────────

let private partyA = "party-a"
let private partyB = "party-b"
let private policyA = "party-a-licensed"
let private policyB = "party-b-licensed"
let private jointOp = "joint-aggregate"

let private scopedPolicy (policyRef: string) (party: string) : DisclosurePolicy = {
    PolicyRef = policyRef
    Mode = TaintPropagating
    PermitSurfaces = [ FactRetrieval ]
    ContributorScope = Some party
}

let private routineAcceptedBy (scopes: string list) : DeclassificationRoutine = {
    OperationId = jointOp
    Rationale = "aggregation over >=5 members loses individual attribution"
    AcceptingScopes = scopes
}

let private twoPartyCfg (accepting: string list) =
    DisclosureTaintConfig.ofLists [ scopedPolicy policyA partyA; scopedPolicy policyB partyB ] [
        routineAcceptedBy accepting
    ]

/// The same shape with no contributor scope declared — the single-party
/// deployment, in which any declared routine clears.
let private unscopedCfg =
    DisclosureTaintConfig.ofLists [
        {
            PolicyRef = policyA
            Mode = TaintPropagating
            PermitSurfaces = [ FactRetrieval ]
            ContributorScope = None
        }
    ] [ routineAcceptedBy [] ]

let private configs = [
    "neither party accepts", twoPartyCfg []
    "only A accepts", twoPartyCfg [ partyA ]
    "only B accepts", twoPartyCfg [ partyB ]
    "both accept", twoPartyCfg [ partyA; partyB ]
    "an undeclared party accepts", twoPartyCfg [ "party-z" ]
    "unscoped (Phase 562 shape)", unscopedCfg
    "inert", DisclosureTaintConfig.empty
]

let private scopeOfIn (config: DisclosureTaintConfig) : string -> TaintFlow.opt<string> =
    fun policyRef -> toModelOpt (DisclosureTaintConfig.scopeOf config policyRef)

// ─── The sources, and the pipelines over them ────────────────────────

let private sourceOf (datasetId: string) : AssemblySource =
    AssemblySource.DatasetVersion {
        ScopeId = "clean-room"
        DatasetId = datasetId
        Version = 1
    }

let private sourceA = sourceOf "party-a-raw"
let private sourceB = sourceOf "party-b-raw"

/// A source carrying no restricted data — the declaration that makes
/// "unlabelled" a positive statement rather than an omission.
let private sourceOpen = sourceOf "public-reference"

let private declaredLabels =
    AssemblyTaint.labelOfSources [ sourceA, [ policyA ]; sourceB, [ policyB ]; sourceOpen, [] ]

let private joinablePool = [| sourceA; sourceB; sourceOpen |]

/// Phase 794.D's generator, reproduced: a pipeline and, beside it, the
/// sources it actually reads.
let private generateTree (seed: int) (length: int) : AssemblySource * AssemblyTransform list =
    let rng = Random(seed)
    let baseSource = joinablePool[rng.Next joinablePool.Length]

    let transforms = [
        for _ in 1..length ->
            match rng.Next 5 with
            | 0 ->
                let right = joinablePool[rng.Next joinablePool.Length]
                AssemblyTransform.Join(right, [ "unit" ], [ "unit" ], AssemblyJoinKind.Inner)
            | 1 -> AssemblyTransform.Lag("value", 1, [ "unit" ], "period", "value_lag1")
            | 2 ->
                AssemblyTransform.Window("value", 3, TimeSeriesAggregation.Average, [ "unit" ], "period", "value_roll")
            | 3 ->
                AssemblyTransform.Filter [
                    {
                        Column = "value"
                        Op = DatasetFilterOp.Gt
                        Value = DatasetValue.Float 0.0
                    }
                ]
            | _ ->
                AssemblyTransform.Resample(
                    "period",
                    TimeSpan.FromDays 1.0,
                    [ "unit" ],
                    [
                        {
                            Column = "value"
                            Aggregation = TimeSeriesAggregation.Sum
                            As = "value"
                        }
                    ]
                )
    ]

    baseSource, transforms

/// One pipeline, named by what a disagreement should report.
type private Pipeline = {
    Name: string
    Base: AssemblySource
    Transforms: AssemblyTransform list
}

let private generatedPipelines: Pipeline list = [
    for seed in [ 795; 794; 11; 2026; 5 ] do
        for length in 0..6 do
            let baseSource, transforms = generateTree (seed + length) length

            {
                Name = sprintf "generated seed %d length %d" seed length
                Base = baseSource
                Transforms = transforms
            }
]

/// The hand-written pipelines the fixtures pin: the two-party join, each
/// party alone, and the public-only pipeline whose label is `bottom`.
let private fixturePipelines: Pipeline list = [
    {
        Name = "the two-party joint panel"
        Base = sourceA
        Transforms = [
            AssemblyTransform.Join(sourceB, [ "unit" ], [ "unit" ], AssemblyJoinKind.Inner)
        ]
    }
    {
        Name = "party A alone, folded"
        Base = sourceA
        Transforms = [
            AssemblyTransform.Lag("value", 1, [ "unit" ], "period", "value_lag1")
            AssemblyTransform.Filter [
                {
                    Column = "value"
                    Op = DatasetFilterOp.Gt
                    Value = DatasetValue.Float 0.0
                }
            ]
        ]
    }
    {
        Name = "party B joined onto the public reference"
        Base = sourceOpen
        Transforms = [
            AssemblyTransform.Join(sourceB, [ "unit" ], [ "unit" ], AssemblyJoinKind.Inner)
        ]
    }
    {
        Name = "the public reference alone"
        Base = sourceOpen
        Transforms = [ AssemblyTransform.Filter [] ]
    }
    {
        Name = "both parties joined onto the public reference"
        Base = sourceOpen
        Transforms = [
            AssemblyTransform.Join(sourceA, [ "unit" ], [ "unit" ], AssemblyJoinKind.Inner)
            AssemblyTransform.Resample(
                "period",
                TimeSpan.FromDays 1.0,
                [ "unit" ],
                [
                    {
                        Column = "value"
                        Aggregation = TimeSeriesAggregation.Sum
                        As = "value"
                    }
                ]
            )
            AssemblyTransform.Join(sourceB, [ "unit" ], [ "unit" ], AssemblyJoinKind.Inner)
        ]
    }
]

let private allPipelines = fixturePipelines @ generatedPipelines

// ─── The semantics the relational arms run over ──────────────────────
//
// A frame is a TRANSCRIPT — the free term over the two step functions.
// It is the most discriminating semantics there is: nothing is combined,
// nothing is lost, so any dependence of the output on a source's frame
// shows up as a literal difference in the string. The theorem holds for
// every semantics, and this is the one that would expose a violation of
// it if there were one.

let private tagOf (transform: AssemblyTransform) =
    match transform with
    | AssemblyTransform.Join _ -> "join"
    | AssemblyTransform.Resample _ -> "resample"
    | AssemblyTransform.Lag _ -> "lag"
    | AssemblyTransform.Window _ -> "window"
    | AssemblyTransform.Filter _ -> "filter"

let private modelTagOf (transform: TaintFlow.transform<AssemblySource>) =
    match transform with
    | TaintFlow.TJoin _ -> "join"
    | TaintFlow.TResample -> "resample"
    | TaintFlow.TLag -> "lag"
    | TaintFlow.TWindow -> "window"
    | TaintFlow.TFilter -> "filter"

let private transcript: TaintFlow.semantics<AssemblySource, string> = {
    step_fold = fun transform working -> sprintf "%s(%s)" (modelTagOf transform) working
    step_join = fun _ working right -> sprintf "join(%s,%s)" working right
}

/// The two input assignments. They differ at party A's source and
/// NOWHERE else, which is exactly the model's `agree_off` for `policyA`:
/// every source whose declared label does not carry `policyA` holds the
/// same frame in both.
let private assignment (aRows: string) : TaintFlow.assignment<AssemblySource, string> =
    fun source ->
        if source = sourceA then aRows
        elif source = sourceB then "B-rows"
        else "public-rows"

let private assignA1 = assignment "A-rows-one"
let private assignA2 = assignment "A-rows-two"

// ─── The derivation bridge ───────────────────────────────────────────

/// Production's `taintSourcePolicy`, which is `private` — mirrored here
/// rather than reached for. A fact is a taint SOURCE when it is directly
/// `Restricted` under a policy the config declares `TaintPropagating`; a
/// `Plain` policy and an unregistered ref are not taint sources at all.
let private ownPolicyOf (config: DisclosureTaintConfig) (fact: Fact) : string option =
    match fact.Disclosure with
    | Restricted policyRef when DisclosureTaintConfig.isTaintPropagating config policyRef -> Some policyRef
    | _ -> None

let private modelRoutineOf (config: DisclosureTaintConfig) (fact: Fact) : TaintFlow.opt<TaintFlow.routine> =
    match DisclosureTaint.declassifierOf config fact with
    | Some routine -> TaintFlow.OSome(toModelRoutine routine)
    | None -> TaintFlow.ONone

/// A fact's derivation, unfolded from the graph. Production memoises and
/// guards against a cycle; a memo cannot change a value, and the guard is
/// mirrored here — a revisited id becomes a leaf, whose taint is
/// `bottom`, which is what production's `visiting` check returns.
let rec private toDerivation
    (config: DisclosureTaintConfig)
    (graph: FactDerivationGraph)
    (visiting: Set<string>)
    (factId: string)
    : TaintFlow.derivation =
    match graph.Facts.TryFind factId with
    | None -> TaintFlow.DNode(TaintFlow.ONone, TaintFlow.ONone, [])
    | Some _ when visiting.Contains factId -> TaintFlow.DNode(TaintFlow.ONone, TaintFlow.ONone, [])
    | Some fact ->
        let visiting' = Set.add factId visiting

        TaintFlow.DNode(
            toModelOpt (ownPolicyOf config fact),
            modelRoutineOf config fact,
            graph.UpstreamOf factId |> List.map (toDerivation config graph visiting')
        )

/// The TARGET's derivation. Its own policy is deliberately `ONone`:
/// `InheritedPolicyRefs` is the taint the target INHERITED, and
/// production's top-level computation never adds the target's own source
/// policy to it — the target's own disclosure is the Phase 525 resolver's
/// business, not the walk's.
let private toRootDerivation (config: DisclosureTaintConfig) (graph: FactDerivationGraph) (targetId: string) =
    match graph.Facts.TryFind targetId with
    | None -> TaintFlow.DNode(TaintFlow.ONone, TaintFlow.ONone, [])
    | Some fact ->
        TaintFlow.DNode(
            TaintFlow.ONone,
            modelRoutineOf config fact,
            graph.UpstreamOf targetId
            |> List.map (toDerivation config graph (Set.singleton targetId))
        )

// ─── The fact fixtures ───────────────────────────────────────────────

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private mkFact
    (id: string)
    (metric: string)
    (method: MethodRef)
    (value: FactValue)
    (disclosure: Disclosure)
    (inputHashes: string list)
    : Fact =
    {
        FactId = id
        Subject = {
            Hierarchy = "geography"
            Path = [ "uk" ]
        }
        Metric = MetricRef metric
        Value = value
        Period = q2
        AsOf = DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc)
        Method = method
        Evidence = {
            ResultRef = None
            InputHashes = inputHashes
            TriggerRef = None
        }
        Confidence = None
        Supersedes = None
        Disclosure = disclosure
    }

let private rawA =
    mkFact "RA" "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) []

let private rawB =
    mkFact "RB" "raw-b" (Computed("load", "1", "p")) (Series "dov-b") (Restricted policyB) []

let private joint =
    mkFact "J" "joint" (Computed(jointOp, "1", "p")) (Series "dov-j") Surfaceable [ "dov-a"; "dov-b" ]

/// A second hop past the declassifier, so the walk is more than one level
/// deep and a cleared ref has somewhere to stay cleared.
let private downstream =
    mkFact "D" "downstream" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-j" ]

/// A fact that is a taint source AND carries upstream taint — the shape
/// the conjunction needs both halves of.
let private mixed =
    mkFact "M" "mixed" (Computed("passthrough", "1", "p")) (Series "dov-m") (Restricted policyB) [ "dov-a" ]

let private afterMixed =
    mkFact "AM" "after-mixed" (Computed(jointOp, "1", "p")) (Scalar 7m) Surfaceable [ "dov-m" ]

/// A derivation with no declassifier anywhere — nothing is ever cleared.
let private undeclassified =
    mkFact "U" "undeclassified" (Computed("passthrough", "1", "p")) (Scalar 1m) Surfaceable [ "dov-a"; "dov-b" ]

type private Walk = {
    Name: string
    Facts: Fact list
    Target: string
}

let private walks: Walk list = [
    {
        Name = "two-party join through the routine"
        Facts = [ rawA; rawB; joint ]
        Target = "J"
    }
    {
        Name = "two-party join, one hop downstream of the routine"
        Facts = [ rawA; rawB; joint; downstream ]
        Target = "D"
    }
    {
        Name = "no declassifier anywhere"
        Facts = [ rawA; rawB; undeclassified ]
        Target = "U"
    }
    {
        Name = "a fact that is a source AND carries upstream taint"
        Facts = [ rawA; mixed; afterMixed ]
        Target = "AM"
    }
    {
        Name = "one party alone through the routine"
        Facts = [ rawA; joint ]
        Target = "J"
    }
    {
        Name = "a leaf with no upstream at all"
        Facts = [ rawA ]
        Target = "RA"
    }
    {
        Name = "a target the graph does not hold"
        Facts = [ rawA; rawB ]
        Target = "absent"
    }
]

// ─── The rooms ───────────────────────────────────────────────────────

let private releaseOf (pipeline: Pipeline) (routine: DeclassificationRoutine option) (observers: string list) =
    ({
        rel_base = pipeline.Base
        rel_transforms = pipeline.Transforms |> List.map toModelTransform
        rel_routine =
            match routine with
            | Some r -> TaintFlow.OSome(toModelRoutine r)
            | None -> TaintFlow.ONone
        rel_observers = observers
    }
    : TaintFlow.release<AssemblySource>)

let private pipelineNamed (name: string) =
    fixturePipelines |> List.find (fun p -> p.Name = name)

/// A room party B may watch in full: every release B observes is computed
/// without party A's data.
let private bFreeRoom: TaintFlow.room<AssemblySource> = [
    releaseOf (pipelineNamed "party B joined onto the public reference") None [ partyB ]
    releaseOf (pipelineNamed "the public reference alone") None [ partyA; partyB ]
    // A release carrying party A's data, which party B does NOT observe:
    // the scoped segment B is outside, which drops out of B's slice.
    releaseOf (pipelineNamed "party A alone, folded") None [ partyA ]
    releaseOf (pipelineNamed "party B joined onto the public reference") None [ partyA; partyB ]
]

/// The same room with a release party B DOES observe that was computed
/// from party A's data. The premise fails here, and the conclusion fails
/// with it — which is what makes the guarded arm above non-vacuous.
let private bExposedRoom: TaintFlow.room<AssemblySource> =
    bFreeRoom
    @ [
        releaseOf (pipelineNamed "the two-party joint panel") None [ partyA; partyB ]
    ]

/// The amendment: two releases appended — one party B does not observe,
/// and one public release it does. Party B's view of the amended room is
/// therefore its view of the baseline with one public value appended, so
/// the refinement's `rebuild` is exhibitable and the amendment inherits
/// the baseline's noninterference.
let private amendedRoom: TaintFlow.room<AssemblySource> =
    bFreeRoom
    @ [
        releaseOf (pipelineNamed "the two-party joint panel") (Some(routineAcceptedBy [ partyA; partyB ])) [ partyA ]
        releaseOf (pipelineNamed "the public reference alone") None [ partyA; partyB ]
    ]

let private publicTail =
    TaintFlow.run
        transcript
        assignA1
        (pipelineNamed "the public reference alone").Base
        ((pipelineNamed "the public reference alone").Transforms
         |> List.map toModelTransform)

let private rebuild (baselineSlice: string list) (tail: string) = baselineSlice @ [ tail ]

let private runRoom (scopeOf: string -> TaintFlow.opt<string>) assign room =
    TaintFlow.run_room transcript scopeOf (toModelLabelling declaredLabels) assign room

let private sliceFor (scopeOf: string -> TaintFlow.opt<string>) (party: string) assign room =
    TaintFlow.slice_tr party (runRoom scopeOf assign room)

// ─── The pack ────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 795 - the proved taint flow as oracle" [

        testCase "the covered set is not vacuous"
        <| fun () ->
            // Most arms below are an `isEmpty` over a computed set, which
            // an empty population satisfies trivially - so the population
            // is asserted first, and so is the one thing every relational
            // arm depends on: that the sample contains BOTH pipelines a
            // party reaches and pipelines it does not.
            Expect.isGreaterThan (List.length allPipelines) 30 "the fixture and generated pipelines should all be here"
            Expect.isGreaterThan (List.length walks) 5 "and the derivation fixtures beside them"

            let carries, free =
                allPipelines
                |> List.partition (fun p ->
                    TaintLabel.contains
                        policyA
                        (AssemblyTaint.outputLabel declaredLabels {
                            Scope = "clean-room"
                            Base = p.Base
                            Transforms = p.Transforms
                            Split = None
                            OutputDatasetId = "out"
                            OutputRoles = []
                            Policy = Versioned
                        }))

            Expect.isNonEmpty
                carries
                "some pipeline must CARRY party A's label, or the go-red case has nothing to blind"

            Expect.isNonEmpty
                free
                "some pipeline must NOT carry party A's label, or the relational law is never exercised at all"

            Expect.isTrue
                (free
                 |> List.exists (fun p ->
                     p.Transforms
                     |> List.exists (function
                         | AssemblyTransform.Join _ -> true
                         | _ -> false)))
                "and at least one of those must contain a Join, or the label only ever tracked a single source"

        testCase "the extracted lattice agrees with production over the declared vocabulary"
        <| fun () ->
            // The lattice first, on its own: the fold below takes the join
            // as given, so a drifted join would be invisible to it
            // whenever both sides were handed the same wrong label.
            let vocabulary = TaintLabel.vocabulary [ policyA; policyB; "party-c-licensed" ]

            Expect.equal (List.length vocabulary) 8 "the power set of a three-ref vocabulary"
            Expect.equal TaintLabel.bottom (ofModelLabel TaintFlow.bottom) "bottom must agree"

            let found = [
                for a in vocabulary do
                    if TaintLabel.isBottom a <> TaintFlow.is_bottom (toModelLabel a) then
                        yield sprintf "isBottom %s disagrees" (TaintLabel.render a)

                    for b in vocabulary do
                        let production = TaintLabel.join a b
                        let model = ofModelLabel (TaintFlow.join (toModelLabel a) (toModelLabel b))

                        if production <> model then
                            yield
                                sprintf
                                    "join %s / %s: production %s, model %s"
                                    (TaintLabel.render a)
                                    (TaintLabel.render b)
                                    (TaintLabel.render production)
                                    (TaintLabel.render model)

                        if TaintLabel.below a b <> TaintFlow.below (toModelLabel a) (toModelLabel b) then
                            yield sprintf "below %s / %s disagrees" (TaintLabel.render a) (TaintLabel.render b)

                    for clearA in [ true; false ] do
                        for clearB in [ true; false ] do
                            let clears (policyRef: string) =
                                (policyRef = policyA && clearA) || (policyRef = policyB && clearB)

                            let production = TaintLabel.narrowsTo clears a
                            let model = ofModelLabel (TaintFlow.narrows_to clears (toModelLabel a))

                            if production <> model then
                                yield
                                    sprintf
                                        "narrowsTo %s (%b/%b): production %s, model %s"
                                        (TaintLabel.render a)
                                        clearA
                                        clearB
                                        (TaintLabel.render production)
                                        (TaintLabel.render model)
            ]

            Expect.isEmpty
                found
                (sprintf "the proved lattice and the shipped one must agree:\n  %s" (String.concat "\n  " found))

        testCase "the extracted entitlement agrees with production over every policy and acceptance set"
        <| fun () ->
            let found = [
                for configName, config in configs do
                    for accepting in [ []; [ partyA ]; [ partyB ]; [ partyA; partyB ]; [ "party-z" ] ] do
                        let routine = routineAcceptedBy accepting

                        for policyRef in [ policyA; policyB; "unregistered" ] do
                            let production = DisclosureTaintConfig.routineClears config routine policyRef

                            let model =
                                TaintFlow.routine_clears (scopeOfIn config) (toModelRoutine routine) policyRef

                            if production <> model then
                                yield
                                    sprintf
                                        "%s / accepting %A / %s: production %b, model %b"
                                        configName
                                        accepting
                                        policyRef
                                        production
                                        model
            ]

            Expect.isEmpty
                found
                (sprintf
                    "the proved entitlement predicate and the shipped one must agree:\n  %s"
                    (String.concat "\n  " found))

        testCase "the extracted labelling agrees with production over every pipeline"
        <| fun () ->
            // The whole labelled assembly, not just its output: every
            // node's label AND what that node contributed, because the
            // contribution is where a second party enters and it is the
            // thing the go-red case forgets.
            let found =
                allPipelines
                |> List.choose (fun pipeline ->
                    let production =
                        AssemblyLabelling.label
                            (AssemblyTaint.labelling declaredLabels)
                            pipeline.Base
                            pipeline.Transforms

                    let model = modelLabelAssembly declaredLabels pipeline.Base pipeline.Transforms

                    if production = model then
                        None
                    else
                        Some(
                            sprintf
                                "%s\n    production: base=%s nodes=%A out=%s\n    model:      base=%s nodes=%A out=%s"
                                pipeline.Name
                                (TaintLabel.render production.BaseLabel)
                                (production.Nodes
                                 |> List.map (fun n -> TaintLabel.render n.Label, TaintLabel.render n.Contributed))
                                (TaintLabel.render production.OutputLabel)
                                (TaintLabel.render model.BaseLabel)
                                (model.Nodes
                                 |> List.map (fun n -> TaintLabel.render n.Label, TaintLabel.render n.Contributed))
                                (TaintLabel.render model.OutputLabel)
                        ))

            Expect.isEmpty
                found
                (sprintf
                    "the proved fold and the shipped one must agree on every node's label and contribution:\n  %s"
                    (String.concat "\n  " found))

        testCase "the extracted derivation walk agrees with production's inherited label"
        <| fun () ->
            let found = [
                for configName, config in configs do
                    for walk in walks do
                        let graph = DisclosureTaint.buildGraph walk.Facts
                        let production = (DisclosureTaint.analyze config graph walk.Target).InheritedLabel

                        let model =
                            ofModelLabel (
                                TaintFlow.taint_of (scopeOfIn config) (toRootDerivation config graph walk.Target)
                            )

                        if production <> model then
                            yield
                                sprintf
                                    "%s / %s: production %s, model %s"
                                    configName
                                    walk.Name
                                    (TaintLabel.render production)
                                    (TaintLabel.render model)
            ]

            Expect.isEmpty
                found
                (sprintf
                    "the proved walk and the shipped one must agree on the inherited label:\n  %s"
                    (String.concat "\n  " found))

        testCase "the conjunction is sound where production reports it clear"
        <| fun () ->
            // `conjunction_sound`'s operational face. Wherever production
            // reports an EMPTY inherited set, every party that minted a
            // taint source in that derivation must have been dropped, and
            // every drop in it must have been entitled - computed with the
            // model's own predicates, which is what makes this the lemma
            // running rather than a restatement of it.
            let mutable clearCases = 0

            let found = [
                for configName, config in configs do
                    for walk in walks do
                        let graph = DisclosureTaint.buildGraph walk.Facts
                        let outcome = DisclosureTaint.analyze config graph walk.Target
                        let derivation = toRootDerivation config graph walk.Target
                        let scopeOf = scopeOfIn config

                        if List.isEmpty outcome.InheritedPolicyRefs then
                            for policyRef in [ policyA; policyB ] do
                                if TaintFlow.reaches policyRef derivation then
                                    clearCases <- clearCases + 1

                                    if not (TaintFlow.drop_occurred scopeOf policyRef derivation) then
                                        yield
                                            sprintf
                                                "%s / %s: %s reached and is absent, but no drop was found"
                                                configName
                                                walk.Name
                                                policyRef

                                    if not (TaintFlow.drops_are_entitled scopeOf policyRef derivation) then
                                        yield
                                            sprintf
                                                "%s / %s: a drop of %s was not entitled"
                                                configName
                                                walk.Name
                                                policyRef
            ]

            Expect.isEmpty
                found
                (sprintf
                    "the conjunction must be sound wherever production reports it clear:\n  %s"
                    (String.concat "\n  " found))

            Expect.isGreaterThan
                clearCases
                0
                "no fixture had a party reach a derivation that then cleared, so the soundness arm proved nothing"

        testCase "the relational law holds where the label says it must - measured directly"
        <| fun () ->
            // `flow_noninterference`, run rather than restated: relabel
            // party A's input rows and require the OUTPUT to be identical
            // wherever production's own computed label says party A did
            // not reach it. The frame is a transcript, so any dependence
            // at all would show as a literal difference.
            let mutable measured = 0

            let found =
                allPipelines
                |> List.choose (fun pipeline ->
                    let outLabel =
                        (AssemblyLabelling.label
                            (AssemblyTaint.labelling declaredLabels)
                            pipeline.Base
                            pipeline.Transforms)
                            .OutputLabel

                    if TaintLabel.contains policyA outLabel then
                        None
                    else
                        measured <- measured + 1
                        let transforms = pipeline.Transforms |> List.map toModelTransform
                        let one = TaintFlow.run transcript assignA1 pipeline.Base transforms
                        let two = TaintFlow.run transcript assignA2 pipeline.Base transforms

                        if one = two then
                            None
                        else
                            Some(
                                sprintf "%s\n    under A-rows-one: %s\n    under A-rows-two: %s" pipeline.Name one two
                            ))

            Expect.isEmpty
                found
                (sprintf
                    "a pipeline whose label does not carry party A produced different outputs when party A's rows changed:\n  %s"
                    (String.concat "\n  " found))

            Expect.isGreaterThan measured 10 "the relational sample must be non-vacuous"

        testCase "the differential CATCHES a blind Join label - the go-red case"
        <| fun () ->
            // An oracle whose fold forgets that a `Join` brings in a
            // second source. It must fail BOTH ways it can: against
            // production's labelling, and - the one that matters - against
            // the relational law, because a blinded label reports a frame
            // as free of party A while the frame demonstrably carries
            // party A's data.
            let labelDisagreements =
                allPipelines
                |> List.filter (fun pipeline ->
                    let production =
                        AssemblyLabelling.label
                            (AssemblyTaint.labelling declaredLabels)
                            pipeline.Base
                            pipeline.Transforms

                    production
                    <> blindJoinLabelAssembly declaredLabels pipeline.Base pipeline.Transforms)

            Expect.isNonEmpty
                labelDisagreements
                "a fold that forgets a Join's contribution must be caught by the labelling comparison: if this passes, the \
                 comparison is agreeing with whatever it is shown and every other arm in this pack is worthless"

            let relationalBreaks =
                allPipelines
                |> List.choose (fun pipeline ->
                    let blindLabel =
                        (blindJoinLabelAssembly declaredLabels pipeline.Base pipeline.Transforms).OutputLabel

                    if TaintLabel.contains policyA blindLabel then
                        None
                    else
                        let transforms = pipeline.Transforms |> List.map toModelTransform
                        let one = TaintFlow.run transcript assignA1 pipeline.Base transforms
                        let two = TaintFlow.run transcript assignA2 pipeline.Base transforms
                        if one = two then None else Some pipeline.Name)

            Expect.isNonEmpty
                relationalBreaks
                "under a blind Join label the relational law must BREAK on the same sample - a pipeline reported free of \
                 party A whose output moves when party A's rows move. If it does not, the relational arm is vacuous."

        testCase "and a second party entering at a Join is what it catches"
        <| fun () ->
            // The mechanism, pinned rather than left to the population:
            // one Join, party B onto party A.
            let pipeline = pipelineNamed "the two-party joint panel"

            let production =
                AssemblyLabelling.label (AssemblyTaint.labelling declaredLabels) pipeline.Base pipeline.Transforms

            let honest = modelLabelAssembly declaredLabels pipeline.Base pipeline.Transforms
            let blind = blindJoinLabelAssembly declaredLabels pipeline.Base pipeline.Transforms

            Expect.equal
                (TaintLabel.policyRefs production.OutputLabel)
                [ policyA; policyB ]
                "production carries both parties past the join"

            Expect.equal
                (TaintLabel.policyRefs honest.OutputLabel)
                [ policyA; policyB ]
                "the honest oracle agrees - this is the labelled algebra running"

            Expect.equal
                (TaintLabel.policyRefs blind.OutputLabel)
                [ policyA ]
                "the blind oracle loses party B, which is the defect the comparison exists to see"

            Expect.notEqual production blind "and the comparison reports exactly that"

        testCase "the sliced trace is identical where the other party observes nothing computed from it"
        <| fun () ->
            // `trace_noninterference`, run over a SEQUENCE: party B's view
            // of the room - the values it received, in order - must not
            // move when party A's rows move. Ordering and count are
            // compared because the slice is a list.
            let scopeOf = scopeOfIn (twoPartyCfg [])
            let labelling = toModelLabelling declaredLabels

            Expect.isTrue
                (TaintFlow.q_free_of labelling policyA partyB bFreeRoom)
                "the guarded room's premise must hold, or the arm below proves nothing"

            let one = sliceFor scopeOf partyB assignA1 bFreeRoom
            let two = sliceFor scopeOf partyB assignA2 bFreeRoom

            Expect.equal one two "party B's slice moved when party A's rows moved"
            Expect.isGreaterThan (List.length one) 2 "party B must actually observe something, or the slice is empty"

            Expect.isLessThan
                (List.length one)
                (List.length (runRoom scopeOf assignA1 bFreeRoom))
                "and some release must have dropped out of party B's slice, or the slice is the whole trace"

            // The premise doing work: a room where party B DOES observe a
            // release computed from party A's data. `q_free_of` is false,
            // and the slice moves - so the condition is not decorative.
            Expect.isFalse
                (TaintFlow.q_free_of labelling policyA partyB bExposedRoom)
                "the exposed room must fail the premise"

            Expect.notEqual
                (sliceFor scopeOf partyB assignA1 bExposedRoom)
                (sliceFor scopeOf partyB assignA2 bExposedRoom)
                "and its slice must MOVE when party A's rows move, or the premise is guarding nothing"

        testCase "an amended room whose slice is a function of the baseline's leaks no more"
        <| fun () ->
            // `refinement_leaks_no_more`. The obligation is to EXHIBIT the
            // rebuild; this is the exhibition, measured: party B's view of
            // the amended room is its view of the baseline with one public
            // value appended, under both assignments. The lemma then says
            // the amendment inherits the baseline's noninterference, and
            // the last assertion is that inheritance holding.
            let scopeOf = scopeOfIn (twoPartyCfg [ partyA; partyB ])

            for name, assign in [ "A-rows-one", assignA1; "A-rows-two", assignA2 ] do
                Expect.equal
                    (sliceFor scopeOf partyB assign amendedRoom)
                    (rebuild (sliceFor scopeOf partyB assign bFreeRoom) publicTail)
                    (sprintf "under %s the amended slice must be the rebuild of the baseline slice" name)

            Expect.notEqual
                (sliceFor scopeOf partyB assignA1 amendedRoom)
                (sliceFor scopeOf partyB assignA1 bFreeRoom)
                "the amendment must actually change party B's view, or the refinement is the identity"

            Expect.equal
                (sliceFor scopeOf partyB assignA1 amendedRoom)
                (sliceFor scopeOf partyB assignA2 amendedRoom)
                "and the amended room must be noninterfering, inherited from the baseline"

        testCase "the model never throws, on any pipeline, walk or room"
        <| fun () ->
            // The lemmas' operational face: every definition in the model
            // is total, but the extraction and the bridge are trusted
            // rather than proved, and this is the arm that would catch
            // either turning a total function into an exception.
            let scopeOf = scopeOfIn (twoPartyCfg [ partyA ])

            let threw = [
                for pipeline in allPipelines do
                    try
                        modelLabelAssembly declaredLabels pipeline.Base pipeline.Transforms |> ignore

                        TaintFlow.run
                            transcript
                            assignA1
                            pipeline.Base
                            (pipeline.Transforms |> List.map toModelTransform)
                        |> ignore
                    with ex ->
                        yield sprintf "%s: %s %s" pipeline.Name (ex.GetType().Name) ex.Message

                for configName, config in configs do
                    for walk in walks do
                        try
                            let graph = DisclosureTaint.buildGraph walk.Facts

                            TaintFlow.taint_of (scopeOfIn config) (toRootDerivation config graph walk.Target)
                            |> ignore
                        with ex ->
                            yield sprintf "%s / %s: %s %s" configName walk.Name (ex.GetType().Name) ex.Message

                for name, room in [ "b-free", bFreeRoom; "b-exposed", bExposedRoom; "amended", amendedRoom ] do
                    try
                        sliceFor scopeOf partyB assignA1 room |> ignore
                    with ex ->
                        yield sprintf "%s: %s %s" name (ex.GetType().Name) ex.Message
            ]

            Expect.isEmpty threw (sprintf "the extracted model threw:\n  %s" (String.concat "\n  " threw))
    ]