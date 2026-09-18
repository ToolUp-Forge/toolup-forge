// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MultiPartyDisclosureTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 674 — multi-party conjunction + contributor scope ─────────
//
// The Phase 562 walk carried ONE representative policy and let any declared
// declassification routine clear it. That is correct for one party and
// catastrophic for two: party A's routine would clear party B's taint, and
// A's consent would launder B's data. Phase 674 makes the verdict a
// CONJUNCTION over the contributing parties — every one must be satisfied —
// with contributor scope attached to the policy (never to the caller) and
// acceptance attached to the routine.
//
// Three properties are exercised, in both the pure walk and the real gate:
//
//   1. a joint output denies unless EVERY contributing party's policy is
//      satisfied along the lineage;
//   2. one party's declassification never releases another's restriction —
//      the survivor denies and the refusal names the unsatisfied party;
//   3. a deployment declaring no contributor scope is byte-for-byte
//      Phase 562 (GP 11), and emits no new audit row (GP 13).
//
// Fail-closed is asserted directly rather than assumed: a routine with no
// acceptance list, and one naming a party the vocabulary does not declare,
// each clear nothing.

// ── Party vocabulary ──────────────────────────────────────────────

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

/// The two-party vocabulary, parameterised by which parties accept the
/// joint routine. `[]` is the fail-closed default: no party accepts.
let private twoPartyCfg (accepting: string list) =
    DisclosureTaintConfig.ofLists [ scopedPolicy policyA partyA; scopedPolicy policyB partyB ] [
        routineAcceptedBy accepting
    ]

/// The same shape with NO contributor scope declared — the single-party
/// deployment the phase must leave byte-identical.
let private unscopedCfg =
    DisclosureTaintConfig.ofLists [
        {
            PolicyRef = policyA
            Mode = TaintPropagating
            PermitSurfaces = [ FactRetrieval ]
            ContributorScope = None
        }
    ] [ routineAcceptedBy [] ]

// ── Fact builders (pure walk) ─────────────────────────────────────

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

/// Party A's raw contribution, published as the series `dov-a`.
let private rawA =
    mkFact "RA" "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) []

/// Party B's raw contribution, published as the series `dov-b`.
let private rawB =
    mkFact "RB" "raw-b" (Computed("load", "1", "p")) (Series "dov-b") (Restricted policyB) []

/// The joint output — a declassifier-shaped operation over BOTH parties.
let private joint =
    mkFact "J" "joint" (Computed(jointOp, "1", "p")) (Scalar 42m) Surfaceable [ "dov-a"; "dov-b" ]

let private jointGraph = DisclosureTaint.buildGraph [ rawA; rawB; joint ]

// ── The pure conjunction (674.A/B) ────────────────────────────────

let conjunctionTests =
    testList "Phase 674 conjunction (pure walk)" [

        test "a two-party joint fact is denied when neither party accepted the routine (fail-closed)" {
            let outcome = DisclosureTaint.analyze (twoPartyCfg []) jointGraph "J"

            Expect.equal
                outcome.UnsatisfiedScopes
                [ partyA; partyB ]
                "an unaccepted routine clears no party — both restrictions survive"

            Expect.isSome outcome.InheritedPolicyRef "the joint output is tainted"
            Expect.isEmpty outcome.Crossings "a routine that cleared nothing records no crossing"
        }

        test "one party's declassification never releases the other's restriction" {
            let outcome = DisclosureTaint.analyze (twoPartyCfg [ partyA ]) jointGraph "J"

            Expect.equal
                outcome.UnsatisfiedScopes
                [ partyB ]
                "party A accepted the routine; party B did not, so B's restriction survives"

            Expect.equal outcome.InheritedPolicyRefs [ policyB ] "only party B's policy is unsatisfied"

            Expect.equal outcome.Crossings.Length 1 "the routine did clear party A, so a crossing is recorded"

            Expect.equal
                outcome.Crossings.Head.AcceptedScopes
                [ partyA ]
                "the crossing names exactly the party that accepted it"
        }

        test "a joint output discloses when EVERY contributing party accepted the routine" {
            let outcome =
                DisclosureTaint.analyze (twoPartyCfg [ partyA; partyB ]) jointGraph "J"

            Expect.isEmpty outcome.UnsatisfiedScopes "both parties are satisfied"
            Expect.equal outcome.InheritedPolicyRef None "the conjunction holds ⇒ no inherited taint"

            Expect.equal
                outcome.Crossings.Head.AcceptedScopes
                [ partyA; partyB ]
                "the crossing records both accepting parties"
        }

        test "the contribution facet names every contributing party, cleared or not" {
            let cleared =
                DisclosureTaint.analyze (twoPartyCfg [ partyA; partyB ]) jointGraph "J"

            Expect.equal
                cleared.ContributorScopes
                [ partyA; partyB ]
                "declassification clears taint, never the record of who contributed"

            let denied = DisclosureTaint.analyze (twoPartyCfg []) jointGraph "J"

            Expect.equal denied.ContributorScopes [ partyA; partyB ] "the same facet is present on a refusal"
        }

        test "mixed paths: an undeclassified branch denies even beside a fully-accepted one" {
            // PB carries party B's data through a plain passthrough — not a
            // declassifier — so B's restriction reaches the combiner intact.
            let passthroughB =
                mkFact "PB" "pass-b" (Computed("passthrough", "1", "p")) (Series "dov-pass-b") Surfaceable [ "dov-b" ]

            let acceptedJoint =
                mkFact "AJ" "agg" (Computed(jointOp, "1", "p")) (Series "dov-agg") Surfaceable [ "dov-a"; "dov-b" ]

            let combined =
                mkFact "C" "report" (Computed("combine", "1", "p")) (Scalar 1m) Surfaceable [ "dov-agg"; "dov-pass-b" ]

            let graph =
                DisclosureTaint.buildGraph [ rawA; rawB; passthroughB; acceptedJoint; combined ]

            let outcome = DisclosureTaint.analyze (twoPartyCfg [ partyA; partyB ]) graph "C"

            Expect.equal
                outcome.UnsatisfiedScopes
                [ partyB ]
                "one undeclassified path carrying B is enough to deny, whatever the other path cleared"
        }

        test "fail-closed: a routine naming an undeclared party clears nothing" {
            let outcome =
                DisclosureTaint.analyze (twoPartyCfg [ "party-that-does-not-exist" ]) jointGraph "J"

            Expect.equal
                outcome.UnsatisfiedScopes
                [ partyA; partyB ]
                "acceptance of a party the vocabulary does not declare is inert, never a wildcard"
        }

        test "an unscoped policy keeps Phase 562 semantics — any declared routine clears it" {
            let unscopedJointGraph =
                DisclosureTaint.buildGraph [
                    rawA
                    mkFact "J1" "joint" (Computed(jointOp, "1", "p")) (Scalar 42m) Surfaceable [ "dov-a" ]
                ]

            let outcome = DisclosureTaint.analyze unscopedCfg unscopedJointGraph "J1"

            Expect.equal
                outcome.InheritedPolicyRef
                None
                "a policy declaring no party is cleared by any routine, exactly as Phase 562"

            Expect.isEmpty outcome.ContributorScopes "no party declared ⇒ no contribution facet"
            Expect.isEmpty outcome.UnsatisfiedScopes "and nothing to be unsatisfied about"
        }

        test "scope resolution reads the registered policy, never anything caller-supplied" {
            let cfg = twoPartyCfg []

            Expect.equal
                (DisclosureTaintConfig.scopeOf cfg policyA)
                (Some partyA)
                "a declared policy resolves its party"

            Expect.equal
                (DisclosureTaintConfig.scopeOf cfg "not-registered")
                None
                "an unregistered ref has no party — it is not a taint source at all"

            Expect.equal
                (DisclosureTaintConfig.contributorScopes cfg)
                [ partyA; partyB ]
                "the declared party set is the composed vocabulary's, sorted and distinct"
        }
    ]

// ── The real gate over the real store (674.B/C) ───────────────────

let private newStore () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    store, events

let private assertFact (store: IFactStore) (scope: string) (draft: FactDraft) : Fact =
    match store.Assert(scope, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

let private draftOf
    (metric: string)
    (method: MethodRef)
    (value: FactValue)
    (disclosure: Disclosure)
    (inputHashes: string list)
    : FactDraft =
    {
        Subject = {
            Hierarchy = "geography"
            Path = [ "uk" ]
        }
        Metric = MetricRef metric
        Value = value
        Period = q2
        Method = method
        Evidence = {
            ResultRef = None
            InputHashes = inputHashes
            TriggerRef = None
        }
        Confidence = None
        Disclosure = disclosure
    }

/// Seed both parties' contributions plus the joint output derived from
/// both, and return the joint fact.
let private seedJoint (store: IFactStore) (scope: string) : Fact =
    assertFact store scope (draftOf "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) [])
    |> ignore

    assertFact store scope (draftOf "raw-b" (Computed("load", "1", "p")) (Series "dov-b") (Restricted policyB) [])
    |> ignore

    assertFact store scope (draftOf "joint" (Computed(jointOp, "1", "p")) (Scalar 42m) Surfaceable [ "dov-a"; "dov-b" ])

let private rowsOf (events: IEventStore) (scope: string) (eventType: string) = async {
    let! rows = events.ReadBySource(scope, FactEvents.SourceModule)
    return rows |> List.filter (fun e -> e.EventType = eventType)
}

let gateTests =
    testList "Phase 674 FactDisclosureGate conjunction" [

        testCaseAsync "a two-party joint fact is denied at egress, naming every unsatisfied party"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let gate = FactDisclosureGate.createWithTaint (twoPartyCfg []) store events
            let joint = seedJoint store scope

            let! verdicts = gate.Check(scope, "user-1", FactExport, [ joint.FactId ])

            Expect.equal
                (verdicts.TryFind joint.FactId)
                (Some(FactNotDisclosable "multi-party-unsatisfied:party-a,party-b"))
                "the refusal names the unsatisfied parties, not the counterparty's policy internals"

            let! denies = rowsOf events scope DisclosureEvents.DeniedType
            Expect.equal denies.Length 1 "one deny row for the conjunction refusal"

            Expect.stringContains denies.Head.Payload partyA "the deny carries the per-party contribution facet"
            Expect.stringContains denies.Head.Payload partyB "both contributing parties are named"
            Expect.isFalse (denies.Head.Payload.Contains "42") "the denied value never rides the audit row"
        }

        testCaseAsync "one party's acceptance is not enough — the other party's restriction still denies"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let gate = FactDisclosureGate.createWithTaint (twoPartyCfg [ partyA ]) store events
            let joint = seedJoint store scope

            let! verdicts = gate.Check(scope, "user-1", FactExport, [ joint.FactId ])

            Expect.equal
                (verdicts.TryFind joint.FactId)
                (Some(FactNotDisclosable "multi-party-unsatisfied:party-b"))
                "party A's declassification does not launder party B's data"

            let! accessed = rowsOf events scope DisclosureEvents.AccessedType
            Expect.isEmpty accessed "a refusal is not an access"
        }

        testCaseAsync "with every party's acceptance the joint output discloses, and both facets are audited"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let gate =
                FactDisclosureGate.createWithTaint (twoPartyCfg [ partyA; partyB ]) store events

            let joint = seedJoint store scope

            let! verdicts = gate.Check(scope, "user-1", FactExport, [ joint.FactId ])

            Expect.equal
                (verdicts.TryFind joint.FactId)
                (Some FactDisclosable)
                "the conjunction is satisfied, so the joint output discloses"

            let! declassifieds = rowsOf events scope DisclosureEvents.DeclassifiedType
            Expect.equal declassifieds.Length 1 "the crossing is audited"

            Expect.stringContains declassifieds.Head.Payload jointOp "the crossing names the declassifying operation"

            Expect.stringContains declassifieds.Head.Payload partyB "the crossing records the accepting parties"

            let! accessed = rowsOf events scope DisclosureEvents.AccessedType
            Expect.equal accessed.Length 1 "the per-party access row is written (674.C)"
            Expect.stringContains accessed.Head.Payload partyA "the access row names each contributing party"
            Expect.stringContains accessed.Head.Payload partyB "including the second party"
            Expect.isFalse (accessed.Head.Payload.Contains "42") "the disclosed value never rides the audit row"

            let! denies = rowsOf events scope DisclosureEvents.DeniedType
            Expect.isEmpty denies "a satisfied conjunction is not a deny"
        }

        testCaseAsync "a directly-classified party fact disclosing at its own permitted surface is an access"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let gate = FactDisclosureGate.createWithTaint (twoPartyCfg []) store events

            let ra =
                assertFact
                    store
                    scope
                    (draftOf "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) [])

            // policyA permits FactRetrieval and nothing else.
            let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ ra.FactId ])

            Expect.equal
                (verdicts.TryFind ra.FactId)
                (Some FactDisclosable)
                "the party's own policy permits its declared surface"

            let! accessed = rowsOf events scope DisclosureEvents.AccessedType
            Expect.equal accessed.Length 1 "one party's data left, so one access row"
            Expect.stringContains accessed.Head.Payload partyA "attributed to the contributing party"
        }

        testCaseAsync "single-party deployments are byte-identical: same verdicts, and no new audit row"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            assertFact
                store
                scope
                (draftOf "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) [])
            |> ignore

            let derived =
                assertFact
                    store
                    scope
                    (draftOf "joint" (Computed(jointOp, "1", "p")) (Scalar 42m) Surfaceable [ "dov-a" ])

            let unscopedGate = FactDisclosureGate.createWithTaint unscopedCfg store events

            let! verdicts = unscopedGate.Check(scope, "user-1", FactExport, [ derived.FactId ])

            Expect.equal
                (verdicts.TryFind derived.FactId)
                (Some FactDisclosable)
                "the routine clears the unscoped policy, exactly as Phase 562"

            let! accessed = rowsOf events scope DisclosureEvents.AccessedType

            Expect.isEmpty
                accessed
                "no contributor scope declared ⇒ no access row ⇒ a pre-674 deployment sees nothing new (GP 13)"

            // And a taint-less gate over the same store is unchanged too.
            let emptyGate =
                FactDisclosureGate.createWithTaint DisclosureTaintConfig.empty store events

            let! viaEmpty = emptyGate.Check(scope, "user-1", FactExport, [ derived.FactId ])
            Expect.equal viaEmpty verdicts "an empty taint config remains the Phase 525 gate"
        }
    ]

// ── Phase 794 — the labelled transform algebra over two parties ───
//
// Phase 674 asked whether the conjunction holds over the lineage the store
// can SEE. This asks where that lineage comes from. The taint walk's graph
// is inferred — a fact's `Evidence.InputHashes` matched against whichever
// fact published that data-object version as a `Series` — so a derivation
// routed any other way is invisible, and an invisible upstream carries no
// taint at all. The transform tree that actually produced the frame knows
// better, and Phase 794 lets the walk consume it.
//
// The generated trees below are the property's subject: arbitrary
// pipelines over two parties' sources, with `Join` the case where a second
// party enters. The ORACLE is computed independently of the fold — the set
// of sources the generator actually used — so the test compares two
// derivations of the same fact rather than the fold against itself. Seeds
// are printed on failure so any counterexample is reproducible; there is no
// property-testing dependency in this repository and adding one is a CPM +
// supply-chain change this phase has no business making.

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

/// A pipeline and, beside it, the sources it actually reads — the oracle
/// the fold is checked against.
let private generateTree (seed: int) (length: int) : AssemblySource * AssemblyTransform list * AssemblySource list =
    let rng = Random(seed)
    let baseSource = joinablePool[rng.Next joinablePool.Length]
    let read = ResizeArray [ baseSource ]

    let transforms = [
        for _ in 1..length ->
            match rng.Next 5 with
            | 0 ->
                let right = joinablePool[rng.Next joinablePool.Length]
                read.Add right
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

    baseSource, transforms, List.ofSeq read

let private labelling = AssemblyTaint.labelling declaredLabels

/// The orphan lineage: a fact whose evidence names two data objects that no
/// fact in the graph published as a `Series`. The inferred projection finds
/// no upstream for it at all — this is the derivation the file header calls
/// invisible, made concrete.
let private orphan =
    mkFact "O" "orphan" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-a"; "dov-b" ]

let private orphanGraph = DisclosureTaint.buildGraph [ orphan ]

let private jointLabel = TaintLabel.ofPolicyRefs [ policyA; policyB ]

let labelledLineageTests =
    testList "Phase 794 labelled transform algebra" [

        test "a generated pipeline's output label is the join of every source it reads" {
            for seed in [ 794; 11; 2026 ] do
                for length in [ 1; 3; 7 ] do
                    let baseSource, transforms, read = generateTree seed length
                    let labelled = AssemblyLabelling.label labelling baseSource transforms
                    let oracle = read |> List.map declaredLabels |> TaintLabel.joinAll

                    Expect.equal
                        labelled.OutputLabel
                        oracle
                        (sprintf "seed %d length %d: the fold disagrees with the sources it read" seed length)
        }

        test "every node carries the join of its inputs, and a label never falls along a pipeline" {
            for seed in [ 794; 11; 2026 ] do
                let baseSource, transforms, _ = generateTree seed 8
                let labelled = AssemblyLabelling.label labelling baseSource transforms

                let running =
                    labelled.Nodes
                    |> List.scan
                        (fun incoming node ->
                            Expect.equal
                                node.Label
                                (TaintLabel.join incoming node.Contributed)
                                (sprintf "seed %d: a node's label is not the join of its inputs" seed)

                            Expect.isTrue
                                (TaintLabel.below incoming node.Label)
                                (sprintf "seed %d: a transform LOWERED a label — only a declassifier may" seed)

                            node.Label)
                        labelled.BaseLabel

                Expect.equal
                    (List.last running)
                    labelled.OutputLabel
                    (sprintf "seed %d: the running join does not reach the output label" seed)
        }

        test "only a Join contributes a second party; every other case contributes nothing" {
            for seed in [ 794; 11; 2026 ] do
                let baseSource, transforms, _ = generateTree seed 8
                let labelled = AssemblyLabelling.label labelling baseSource transforms

                for node in labelled.Nodes do
                    match node.Transform with
                    | AssemblyTransform.Join(right, _, _, _) ->
                        Expect.equal
                            node.Contributed
                            (declaredLabels right)
                            "a Join carries the right source's policies — both parties, from this node on"
                    | _ ->
                        Expect.equal
                            node.Contributed
                            TaintLabel.bottom
                            "an information-losing fold is not a declassifier: it contributes nothing and clears nothing"
        }

        test "a Q-visible output over a tree with no declassifier naming P carries P's label" {
            for seed in [ 794; 11; 2026; 5 ] do
                let baseSource, transforms, read = generateTree seed 6

                // Force party P into the tree so the property has a subject.
                let withP =
                    AssemblyTransform.Join(sourceA, [ "unit" ], [ "unit" ], AssemblyJoinKind.Inner)
                    :: transforms

                let labelled = AssemblyLabelling.label labelling baseSource withP

                Expect.isTrue
                    (TaintLabel.contains policyA labelled.OutputLabel)
                    (sprintf "seed %d: party A's data entered the pipeline and its label did not survive" seed)

                // And the walk agrees: a fact taking that label as its
                // computed lineage denies, naming party A as unsatisfied,
                // because no routine in this config accepted anything.
                let lineage = ComputedLineage.ofList [ "O", labelled.OutputLabel ]

                let outcome =
                    DisclosureTaint.analyzeWithLineage (twoPartyCfg []) lineage orphanGraph "O"

                Expect.isTrue
                    (List.contains partyA outcome.UnsatisfiedScopes)
                    (sprintf "seed %d: party A's consent is missing and the verdict does not say so" seed)

                ignore read
        }

        test "an inferred lineage is reported on the verdict, never silently" {
            let outcome = DisclosureTaint.analyze (twoPartyCfg []) jointGraph "J"

            Expect.equal
                outcome.Findings
                [ DisclosureFinding.LineageInferred [ "J" ] ]
                "the joint fact's upstream came from evidence linkage, and the outcome says so"

            Expect.equal
                outcome.UnsatisfiedScopes
                [ partyA; partyB ]
                "and the Phase 674 verdict is unchanged — a finding qualifies evidence, it never decides"
        }

        test "a leaf raises no finding — there is no derivation there to have inferred" {
            let leafGraph = DisclosureTaint.buildGraph [ rawA ]
            let outcome = DisclosureTaint.analyze (twoPartyCfg []) leafGraph "RA"

            Expect.isEmpty
                outcome.Findings
                "a fact with no evidence inputs was neither computed nor inferred, so reporting it would be noise"
        }

        test "a computed lineage raises no finding, and its label drives the verdict" {
            let lineage = ComputedLineage.ofList [ "J", jointLabel ]

            let outcome =
                DisclosureTaint.analyzeWithLineage (twoPartyCfg [ partyA ]) lineage jointGraph "J"

            Expect.isEmpty outcome.Findings "nothing was inferred — the transform tree said what flowed in"

            Expect.equal
                outcome.UnsatisfiedScopes
                [ partyB ]
                "party A accepted the routine, party B did not — exactly the Phase 674 conjunction"

            Expect.equal
                outcome.InheritedLabel
                (TaintLabel.ofPolicyRef policyB)
                "and the surviving taint is available as a lattice element"
        }

        test "the invisible derivation: computed lineage taints what the inferred graph cannot see" {
            let inferredOnly = DisclosureTaint.analyze (twoPartyCfg []) orphanGraph "O"

            Expect.equal
                inferredOnly.InheritedPolicyRef
                None
                "no fact published those data objects as a Series, so the inference finds no upstream at all"

            Expect.equal
                inferredOnly.Findings
                [ DisclosureFinding.LineageInferred [ "O" ] ]
                "which is precisely the case the finding exists to surface"

            let computed =
                DisclosureTaint.analyzeWithLineage
                    (twoPartyCfg [])
                    (ComputedLineage.ofList [ "O", jointLabel ])
                    orphanGraph
                    "O"

            Expect.equal
                computed.UnsatisfiedScopes
                [ partyA; partyB ]
                "the computation says both parties' data flowed in, and the verdict follows the computation"

            Expect.isEmpty computed.Findings "and nothing was inferred"
        }

        test "a computed lineage for an unrelated fact leaves an existing verdict identical" {
            let unrelated = ComputedLineage.ofList [ "somewhere-else", jointLabel ]

            let before = DisclosureTaint.analyze (twoPartyCfg [ partyA ]) jointGraph "J"

            let after =
                DisclosureTaint.analyzeWithLineage (twoPartyCfg [ partyA ]) unrelated jointGraph "J"

            Expect.equal after.InheritedPolicyRef before.InheritedPolicyRef "the deny ref is unchanged"
            Expect.equal after.InheritedPolicyRefs before.InheritedPolicyRefs "and every surviving policy"
            Expect.equal after.UnsatisfiedScopes before.UnsatisfiedScopes "and the unsatisfied parties"
            Expect.equal after.ContributorScopes before.ContributorScopes "and the contribution facet"
            Expect.equal after.Crossings before.Crossings "and the crossings"
        }

        test "a spec's output label is what a fact asserted from it inherits" {
            let spec: DatasetAssemblySpec = {
                Scope = "clean-room"
                Base = sourceA
                Transforms = [
                    AssemblyTransform.Join(sourceB, [ "unit" ], [ "unit" ], AssemblyJoinKind.Inner)
                ]
                Split = None
                OutputDatasetId = "joint-panel"
                OutputRoles = []
                Policy = Versioned
            }

            Expect.equal
                (AssemblyTaint.outputLabel declaredLabels spec)
                jointLabel
                "a two-party join produces a two-party label"

            Expect.equal
                (AssemblyLabelling.sourcesOf spec)
                [ sourceA; sourceB ]
                "and the sources it reads are enumerable without re-walking the DU"

            let lineage = AssemblyTaint.lineageOf declaredLabels [ "O", spec ]

            let outcome =
                DisclosureTaint.analyzeWithLineage (twoPartyCfg []) lineage orphanGraph "O"

            Expect.equal
                outcome.UnsatisfiedScopes
                [ partyA; partyB ]
                "and a fact asserted from that spec inherits both parties' restrictions"
        }
    ]

let tests =
    testList "Phase 674 multi-party disclosure conjunction" [ conjunctionTests; gateTests; labelledLineageTests ]