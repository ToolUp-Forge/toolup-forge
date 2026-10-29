// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DisclosureTaintTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 562 — taint-propagating disclosure + declassification ─────
//
// A `Restricted` policy gains an opt-in `TaintPropagating` mode: anything
// derived from a restricted input inherits the restriction unless the
// derivation path crosses a declared declassification routine. Two layers
// are exercised: the pure `DisclosureTaint.analyze` walk over hand-built
// derivation graphs (inherited deny / declassified allow + crossing /
// mixed paths / Plain-mode-does-not-propagate / clean graph), and the real
// `FactDisclosureGate` over the real fact store (egress deny, declassified
// disclose + audit, the policy vocabulary the 525 resolver gained, and the
// plan-D17 byte-identical guarantee for a taint-less deployment).

// ── Fact + config builders ────────────────────────────────────────

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

/// A minimal fact with the fields the taint walk reads. `id` is used
/// verbatim (the pure walk keys on it, not the content address).
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

/// A taint config: `licensed` is a `TaintPropagating` policy permitting
/// only `FactRetrieval` directly; `aggregate-over-k` is a declassifier.
let private taintCfg =
    DisclosureTaintConfig.ofLists [
        {
            PolicyRef = "licensed"
            Mode = TaintPropagating
            PermitSurfaces = [ FactRetrieval ]
            ContributorScope = None
        }
        {
            PolicyRef = "plain-hold"
            Mode = Plain
            PermitSurfaces = [ FactRetrieval ]
            ContributorScope = None
        }
    ] [
        {
            OperationId = "aggregate-over-k"
            Rationale = "aggregation over ≥5 members loses individual attribution"
            AcceptingScopes = []
        }
    ]

// ── The pure walk (562.A/B/C over hand-built graphs) ──────────────

let pureWalkTests =
    testList "Phase 562 taint walk (pure)" [

        test "inherited deny: a fact derived from a TaintPropagating input inherits the restriction" {
            // R (Restricted licensed, produces dov-raw) → D (passthrough).
            let r =
                mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []

            let d =
                mkFact "D" "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ]

            let graph = DisclosureTaint.buildGraph [ r; d ]
            let outcome = DisclosureTaint.analyze taintCfg graph "D"

            Expect.equal outcome.InheritedPolicyRef (Some "licensed") "the derived fact inherits the source policy"
            Expect.isEmpty outcome.Crossings "no declassifier on the path"
        }

        test "declassified allow: a declassification routine on the path clears the taint and records a crossing" {
            let r =
                mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []

            // A is the declassifier, derived directly from the tainted R.
            let a =
                mkFact "A" "index" (Computed("aggregate-over-k", "1", "p")) (Scalar 7m) Surfaceable [ "dov-raw" ]

            let graph = DisclosureTaint.buildGraph [ r; a ]
            let outcome = DisclosureTaint.analyze taintCfg graph "A"

            Expect.equal outcome.InheritedPolicyRef None "the declassifier clears inherited taint"
            Expect.equal outcome.Crossings.Length 1 "exactly one crossing recorded"
            Expect.equal outcome.Crossings.Head.DeclassifierFactId "A" "the crossing names the declassifier fact"
            Expect.equal outcome.Crossings.Head.OperationId "aggregate-over-k" "the crossing names the operation"

            Expect.stringContains
                outcome.Crossings.Head.Rationale
                "aggregation"
                "the crossing carries the catalog rationale"
        }

        test "declassified-then-derived: taint cleared upstream stays cleared downstream" {
            let r =
                mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []

            let a =
                mkFact "A" "index" (Computed("aggregate-over-k", "1", "p")) (Series "dov-agg") Surfaceable [ "dov-raw" ]

            // F is derived from the *declassified* A — no residual taint.
            let f =
                mkFact "F" "report" (Computed("format", "1", "p")) (Scalar 1m) Surfaceable [ "dov-agg" ]

            let graph = DisclosureTaint.buildGraph [ r; a; f ]
            let outcome = DisclosureTaint.analyze taintCfg graph "F"

            Expect.equal outcome.InheritedPolicyRef None "a fact built on a declassified output is clean"
        }

        test "mixed paths: any tainted, undeclassified path ⇒ deny (even alongside a declassified one)" {
            let r =
                mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []

            // P: a plain passthrough (NOT a declassifier) — carries taint.
            let p =
                mkFact "P" "passthrough" (Computed("passthrough", "1", "p")) (Series "dov-pass") Surfaceable [
                    "dov-raw"
                ]

            // A: a declassifier — clears taint on its own branch.
            let a =
                mkFact "A" "index" (Computed("aggregate-over-k", "1", "p")) (Series "dov-agg") Surfaceable [ "dov-raw" ]

            // F combines both — the undeclassified P branch still taints it.
            let f =
                mkFact "F" "report" (Computed("combine", "1", "p")) (Scalar 1m) Surfaceable [ "dov-pass"; "dov-agg" ]

            let graph = DisclosureTaint.buildGraph [ r; p; a; f ]
            let outcome = DisclosureTaint.analyze taintCfg graph "F"

            Expect.equal
                outcome.InheritedPolicyRef
                (Some "licensed")
                "one undeclassified tainted path is enough to deny"
        }

        test "Plain-mode restricted inputs do not propagate taint (562.A default stays permissive)" {
            let r =
                mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "plain-hold") []

            let d =
                mkFact "D" "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ]

            let graph = DisclosureTaint.buildGraph [ r; d ]
            let outcome = DisclosureTaint.analyze taintCfg graph "D"

            Expect.equal outcome.InheritedPolicyRef None "a Plain policy governs only its own fact, never derivations"
        }

        test "an unclassified derivation graph is never tainted (empty config short-circuits)" {
            let r =
                mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []

            let d =
                mkFact "D" "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ]

            let graph = DisclosureTaint.buildGraph [ r; d ]
            let outcome = DisclosureTaint.analyze DisclosureTaintConfig.empty graph "D"

            Expect.equal outcome.InheritedPolicyRef None "no registered policy ⇒ nothing is a taint source"
        }

        test "supersession is NOT a derivation edge — a correction of a restricted fact does not taint via lineage" {
            // D declares no series-linked input from R; the only relation is
            // a supersession, which the walk deliberately ignores.
            let r =
                mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []

            let d =
                mkFact "D" "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable []

            let graph = DisclosureTaint.buildGraph [ r; d ]
            let outcome = DisclosureTaint.analyze taintCfg graph "D"

            Expect.equal outcome.InheritedPolicyRef None "no series/input linkage ⇒ no derivation edge ⇒ no taint"
        }
    ]

// ── The policy vocabulary the 525 resolver gained (562.A) ─────────

let resolverVocabularyTests =
    testList "Phase 562 policy resolver vocabulary" [

        test "a registered policy permits its declared surfaces and denies the rest" {
            let resolve = DisclosureTaintConfig.resolver taintCfg

            Expect.equal (resolve "licensed" FactRetrieval) (Some true) "licensed permits retrieval"
            Expect.equal (resolve "licensed" FactExport) (Some false) "licensed denies export"
            Expect.equal (resolve "unregistered" FactRetrieval) None "an unregistered ref stays unknown ⇒ deny"
        }

        test "isTaintPropagating distinguishes the two modes; unknown refs are not propagating" {
            Expect.isTrue (DisclosureTaintConfig.isTaintPropagating taintCfg "licensed") "licensed is taint-propagating"
            Expect.isFalse (DisclosureTaintConfig.isTaintPropagating taintCfg "plain-hold") "plain-hold is Plain"

            Expect.isFalse
                (DisclosureTaintConfig.isTaintPropagating taintCfg "nope")
                "an unknown ref is not propagating"
        }
    ]

// ── The real gate over the real store (562.C/D + audit) ───────────

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

let gateTests =
    testList "Phase 562 FactDisclosureGate taint" [

        testCaseAsync
            "a Surfaceable fact derived from a TaintPropagating input is denied at egress, naming the source policy"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let gate = FactDisclosureGate.createWithTaint taintCfg store events

            // R (Restricted licensed → Series dov-raw), D derives from it.
            let r =
                assertFact
                    store
                    scope
                    (draftOf "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") [])

            let d =
                assertFact
                    store
                    scope
                    (draftOf "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ])

            ignore r
            let! verdicts = gate.Check(scope, "user-1", FactExport, [ d.FactId ])

            Expect.equal
                (verdicts.TryFind d.FactId)
                (Some(FactNotDisclosable "licensed"))
                "the derived fact inherits the licensed restriction at egress"

            // The inherited deny is audited (GP 6) — surface, id, policy.
            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.equal denies.Length 1 "one deny row for the inherited-taint refusal"
            Expect.stringContains denies.Head.Payload "licensed" "the deny names the inherited policy"
            Expect.stringContains denies.Head.Payload "Export" "the deny names the surface"
            Expect.isFalse (denies.Head.Payload.Contains "42") "the denied value never rides the audit row"
        }

        testCaseAsync "a declassification routine on the path discloses the fact and audits the crossing (GP 6)"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let gate = FactDisclosureGate.createWithTaint taintCfg store events

            let r =
                assertFact
                    store
                    scope
                    (draftOf "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") [])

            // A: the declassifier, derived from the tainted R.
            let a =
                assertFact
                    store
                    scope
                    (draftOf "index" (Computed("aggregate-over-k", "1", "p")) (Scalar 7m) Surfaceable [ "dov-raw" ])

            ignore r
            let! verdicts = gate.Check(scope, "user-1", FactExport, [ a.FactId ])

            Expect.equal (verdicts.TryFind a.FactId) (Some FactDisclosable) "the declassified fact discloses"

            let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

            let declassifieds =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeclassifiedType)

            Expect.equal declassifieds.Length 1 "the declassification crossing is audited"
            Expect.stringContains declassifieds.Head.Payload a.FactId "the crossing names the disclosed fact"
            Expect.stringContains declassifieds.Head.Payload "aggregate-over-k" "the crossing names the operation"
            Expect.stringContains declassifieds.Head.Payload "Export" "the crossing names the surface"

            let denies =
                rows |> List.filter (fun e -> e.EventType = DisclosureEvents.DeniedType)

            Expect.isEmpty denies "a declassified disclosure is not a deny"
        }

        testCaseAsync "a directly Restricted TaintPropagating fact honours its own per-surface stance"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let gate = FactDisclosureGate.createWithTaint taintCfg store events

            // A fact classified `licensed` directly (no derivation).
            let r =
                assertFact
                    store
                    scope
                    (draftOf "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") [])

            // licensed permits FactRetrieval, denies FactExport.
            let! atRetrieval = gate.Check(scope, "user-1", FactRetrieval, [ r.FactId ])
            let! atExport = gate.Check(scope, "user-1", FactExport, [ r.FactId ])

            Expect.equal
                (atRetrieval.TryFind r.FactId)
                (Some FactDisclosable)
                "the policy vocabulary permits its declared surface"

            Expect.equal
                (atExport.TryFind r.FactId)
                (Some(FactNotDisclosable "licensed"))
                "the same policy denies an undeclared surface"
        }

        testCaseAsync
            "byte-identical without taint: a taint-less gate matches a create gate on the same store (plan D17)"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            // Same derivation chain, but no taint config composed.
            let r =
                assertFact
                    store
                    scope
                    (draftOf "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") [])

            let d =
                assertFact
                    store
                    scope
                    (draftOf "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ])

            ignore r
            let plainGate = FactDisclosureGate.create store events

            let emptyTaintGate =
                FactDisclosureGate.createWithTaint DisclosureTaintConfig.empty store events

            let! viaPlain = plainGate.Check(scope, "user-1", FactExport, [ d.FactId ])
            let! viaEmpty = emptyTaintGate.Check(scope, "user-1", FactExport, [ d.FactId ])

            Expect.equal
                (viaPlain.TryFind d.FactId)
                (Some FactDisclosable)
                "with no taint config the derived Surfaceable fact discloses (525 behaviour)"

            Expect.equal viaEmpty viaPlain "an empty taint config is byte-for-byte the Phase 525 gate"
        }
    ]

// ── Phase 794 — the taint label lattice ───────────────────────────
//
// The laws are predicates, not assertions, and that is deliberate — the
// `CommutativeCipher` pack's idiom, for its reason. A commutativity
// assertion written directly as an Expecto case passes against any
// operator that happens to agree on the two labels the case chose; a law
// quantified over the whole vocabulary does not. Each law set is therefore
// consumed twice: bound to the real operator and asserted empty, and bound
// to a deliberately-broken one to assert that the SPECIFIC law which must
// catch it fires. A law that stopped having teeth fails its control rather
// than passing everywhere.
//
// The three broken joins fail on different axes, so no single law carries
// the pack:
//   - `leftProjection` is idempotent and associative, and is neither
//     commutative nor identity-respecting;
//   - `intersecting` is idempotent, commutative and associative — it is a
//     MEET, a perfectly good lattice operator that is the wrong one — and
//     is caught only by the identity laws;
//   - `truncating` is the only one of the three that is not IDEMPOTENT,
//     which is the axis the other two pass cleanly.
//
// Two sweeps: EXHAUSTIVE over the power set of a closed vocabulary (which
// is what a deployment's label alphabet actually is), and a seeded random
// sample over a wider alphabet, whose seed is printed on failure so a
// counterexample is reproducible.

/// A three-ref alphabet: 8 labels, 512 associativity triples. Small enough
/// to check exhaustively, wide enough that a two-ref-blind operator cannot
/// hide.
let private latticeAlphabet = [ "licensed"; "party-a"; "party-b" ]

let private exhaustiveLabels = TaintLabel.vocabulary latticeAlphabet

/// The entitlement predicate a party-A-accepting routine would hand
/// `narrowsTo`: it clears exactly one ref of the alphabet, so a narrowing
/// that clears too much and one that clears too little are both visible.
let private clearsPartyA (policyRef: string) = policyRef = "party-a"

let private leftProjection (a: TaintLabel) (_: TaintLabel) = a

let private intersecting (a: TaintLabel) (b: TaintLabel) =
    TaintLabel.policyRefs a
    |> List.filter (fun policyRef -> TaintLabel.contains policyRef b)
    |> TaintLabel.ofPolicyRefs

let private truncating (a: TaintLabel) (b: TaintLabel) =
    TaintLabel.join a b
    |> TaintLabel.policyRefs
    |> List.truncate 1
    |> TaintLabel.ofPolicyRefs

let private raisingNarrow (_: string -> bool) (label: TaintLabel) =
    TaintLabel.join label (TaintLabel.ofPolicyRef "smuggled-in")

let private erasingNarrow (_: string -> bool) (_: TaintLabel) = TaintLabel.bottom

let private inertNarrow (_: string -> bool) (label: TaintLabel) = label

/// A reproducible sample of labels over a wider alphabet than the
/// exhaustive sweep can afford.
let private sampledLabels (seed: int) (count: int) : TaintLabel list =
    let alphabet = [| "p-a"; "p-b"; "p-c"; "p-d"; "p-e"; "p-f" |]
    let rng = Random(seed)

    [
        for _ in 1..count -> alphabet |> Array.filter (fun _ -> rng.Next 2 = 0) |> TaintLabel.ofPolicyRefs
    ]

let private mentions (fragment: string) (failures: string list) =
    failures |> List.exists (fun failure -> failure.Contains fragment)

let latticeLawTests =
    testList "Phase 794 taint label lattice" [
        testCase "the label vocabulary is the taint-propagating policies, and only those"
        <| fun () ->
            Expect.equal
                (DisclosureTaintConfig.taintVocabulary taintCfg)
                [ "licensed" ]
                "a Plain policy never taints, so it is not part of the label alphabet"

        testCase "the power set of a closed vocabulary is every label it can express"
        <| fun () ->
            Expect.equal (List.length exhaustiveLabels) 8 "three refs admit 2^3 labels"

            Expect.equal
                (List.length (List.distinct exhaustiveLabels))
                8
                "and they are distinct — a label is a set, so no two enumerations collide"

            Expect.isTrue (List.contains TaintLabel.bottom exhaustiveLabels) "bottom is one of them"

        testCase "the join obeys every lattice law, exhaustively over the closed vocabulary"
        <| fun () ->
            let failures = TaintLabel.joinLawFailures exhaustiveLabels

            Expect.isEmpty failures (sprintf "join law violations over all 8 labels: %A" failures)

        testCase "the join obeys every lattice law over a seeded random sample"
        <| fun () ->
            for seed in [ 794; 1; 20260917 ] do
                let failures = TaintLabel.joinLawFailures (sampledLabels seed 16)

                Expect.isEmpty failures (sprintf "join law violations at seed %d: %A" seed failures)

        testCase "go-red: a left-projecting join is caught by the identity and commutativity laws"
        <| fun () ->
            let failures = TaintLabel.joinLawFailuresOf leftProjection exhaustiveLabels

            Expect.isTrue (mentions "LEFT identity" failures) "a projection discards bottom's identity"
            Expect.isTrue (mentions "not commutative" failures) "and it is order-dependent"
            Expect.isTrue (mentions "does not cover its parts" failures) "and it loses the right label entirely"

        testCase "go-red: an intersecting join is a MEET — caught by the identity laws alone"
        <| fun () ->
            let failures = TaintLabel.joinLawFailuresOf intersecting exhaustiveLabels

            Expect.isTrue (mentions "identity" failures) "intersecting with bottom is bottom, not the label"

            Expect.isFalse
                (mentions "not commutative" failures)
                "intersection IS commutative — which is exactly why a commutativity test alone proves nothing"

            Expect.isFalse (mentions "not associative" failures) "and associative, and idempotent"

        testCase "go-red: a truncating join is caught by idempotence — the axis the other two controls pass"
        <| fun () ->
            let failures = TaintLabel.joinLawFailuresOf truncating exhaustiveLabels

            Expect.isTrue (mentions "not idempotent" failures) "truncation loses refs a label already carried"

            Expect.isEmpty
                (TaintLabel.joinLawFailuresOf leftProjection exhaustiveLabels
                 |> List.filter (fun failure -> failure.Contains "not idempotent"))
                "left projection is idempotent, so idempotence alone would not catch it"

            Expect.isEmpty
                (TaintLabel.joinLawFailuresOf intersecting exhaustiveLabels
                 |> List.filter (fun failure -> failure.Contains "not idempotent"))
                "and so is intersection — no single law carries this pack"

        testCase "narrowing obeys every declassification law, exhaustively"
        <| fun () ->
            let failures = TaintLabel.narrowingLawFailures clearsPartyA exhaustiveLabels

            Expect.isEmpty failures (sprintf "narrowing law violations: %A" failures)

        testCase "narrowing obeys every declassification law over a seeded random sample"
        <| fun () ->
            for seed in [ 794; 7; 20260917 ] do
                let clearsC (policyRef: string) = policyRef = "p-c"
                let failures = TaintLabel.narrowingLawFailures clearsC (sampledLabels seed 16)

                Expect.isEmpty failures (sprintf "narrowing law violations at seed %d: %A" seed failures)

        testCase "go-red: a narrowing that RAISES the label is caught"
        <| fun () ->
            let failures =
                TaintLabel.narrowingLawFailuresOf raisingNarrow clearsPartyA exhaustiveLabels

            Expect.isTrue (mentions "did not LOWER" failures) "adding a ref is not declassification"

        testCase "go-red: a narrowing that clears everything regardless of entitlement is caught"
        <| fun () ->
            let failures =
                TaintLabel.narrowingLawFailuresOf erasingNarrow clearsPartyA exhaustiveLabels

            Expect.isTrue
                (mentions "entitled to clear nothing still lowered" failures)
                "this is the laundering shape: a routine no party accepted must clear nothing"

        testCase "go-red: a narrowing that ignores its entitlement entirely is caught"
        <| fun () ->
            let failures =
                TaintLabel.narrowingLawFailuresOf inertNarrow clearsPartyA exhaustiveLabels

            Expect.isTrue
                (mentions "entitled to clear everything left taint behind" failures)
                "an inert narrowing declassifies nothing it was entitled to"

            Expect.isTrue (mentions "cleared the wrong refs" failures) "and leaves the ref the routine covered"

        testCase "narrowing is the walk's partition — the lattice and the walk agree on every label"
        <| fun () ->
            for label in exhaustiveLabels do
                let viaLattice = TaintLabel.narrowsTo clearsPartyA label

                let viaPartition =
                    TaintLabel.policyRefs label
                    |> List.partition clearsPartyA
                    |> snd
                    |> TaintLabel.ofPolicyRefs

                Expect.equal
                    viaLattice
                    viaPartition
                    (sprintf "narrowsTo and the walk's own partition disagree at %s" (TaintLabel.render label))

                Expect.equal
                    (TaintLabel.clearedBy clearsPartyA label)
                    (TaintLabel.policyRefs label |> List.filter clearsPartyA)
                    "and clearedBy names exactly what was dropped"

        testCase "narrowing is entitled exactly as routineClears says"
        <| fun () ->
            let cfg =
                DisclosureTaintConfig.ofLists [
                    {
                        PolicyRef = "a-licensed"
                        Mode = TaintPropagating
                        PermitSurfaces = [ FactRetrieval ]
                        ContributorScope = Some "party-a"
                    }
                    {
                        PolicyRef = "b-licensed"
                        Mode = TaintPropagating
                        PermitSurfaces = [ FactRetrieval ]
                        ContributorScope = Some "party-b"
                    }
                ] []

            let acceptedByA = {
                OperationId = "joint-aggregate"
                Rationale = "aggregation over >=5 members loses individual attribution"
                AcceptingScopes = [ "party-a" ]
            }

            let joint = TaintLabel.ofPolicyRefs [ "a-licensed"; "b-licensed" ]
            let narrowed = AssemblyTaint.declassify cfg acceptedByA joint

            Expect.isFalse (TaintLabel.contains "a-licensed" narrowed) "the accepting party's own label is cleared"

            Expect.isTrue
                (TaintLabel.contains "b-licensed" narrowed)
                "and the other party's survives — one party's consent never launders another's data"
    ]

// ── The gate's per-check fan-out is bounded ─────────────────────────
//
// A check over N facts started N reads together. The bound below holds at
// the default and at a composed degree; the probe first proves the reads
// do overlap, so a bound cannot hold vacuously.

/// Delays every fact-blob download and records the most in flight at once.
type private InFlightProbe(inner: ToolUp.Platform.BlobStorage.IBlobStorage) =
    let gate = obj ()
    let mutable inFlight = 0
    let mutable maxInFlight = 0

    member _.MaxInFlight = lock gate (fun () -> maxInFlight)
    member _.Reset() = lock gate (fun () -> maxInFlight <- 0)

    interface ToolUp.Platform.BlobStorage.IBlobStorage with
        member _.CanComposeFrom = false

        member _.ComposeFrom(_, _, _) =
            ToolUp.Platform.BlobStorage.composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = async {
            if blobName.StartsWith("_facts/", StringComparison.Ordinal) then
                lock gate (fun () ->
                    inFlight <- inFlight + 1
                    maxInFlight <- max maxInFlight inFlight)

                try
                    do! Async.Sleep 25
                    return! inner.Download(container, blobName)
                finally
                    lock gate (fun () -> inFlight <- inFlight - 1)
            else
                return! inner.Download(container, blobName)
        }

        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let private batchOf (store: IFactStore) (scope: string) (n: int) = [
    for i in 1..n ->
        (assertFact store scope (draftOf (sprintf "m%03d" i) (Computed("load", "1", "p")) (Scalar 1m) Surfaceable []))
            .FactId
]

let fanOutTests =
    testList "the gate's fan-out is bounded" [

        testCaseAsync "a batch of N facts holds at most the default number of reads in flight"
        <| async {
            let probe = InFlightProbe(InMemoryBlobStorage())
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create probe events
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let ids = batchOf store scope 64
            probe.Reset()

            let gate = FactDisclosureGate.create store events
            let! verdicts = gate.Check(scope, "user-1", FactRetrieval, ids)
            Expect.equal verdicts.Count 64 "every fact was decided"

            Expect.isGreaterThan probe.MaxInFlight 1 "the reads did overlap"

            Expect.isLessThanOrEqual
                probe.MaxInFlight
                DisclosureFanOut.DefaultMaxConcurrentReads
                "never more than the default bound at once"
        }

        testCaseAsync "a composed degree bounds the fan-out to it"
        <| async {
            let probe = InFlightProbe(InMemoryBlobStorage())
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let store = BlobFactStore.create probe events
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let ids = batchOf store scope 32
            probe.Reset()

            let gate = (FactDisclosureGate(store, events)).WithFanOut { MaxConcurrentReads = 3 }

            let! verdicts = gate.Check(scope, "user-1", FactRetrieval, ids)
            Expect.equal verdicts.Count 32 "every fact was decided"
            Expect.isGreaterThan probe.MaxInFlight 1 "the reads did overlap"
            Expect.isLessThanOrEqual probe.MaxInFlight 3 "never more than the composed bound at once"
        }
    ]

// ── Phase 1134 — an inert config decides exactly as a walked one ──
//
// A config whose policies are all `Plain`, unscoped, and that registers no
// declassifier no longer builds the derivation graph. The skip must change
// no verdict and no audit row, so each corpus is judged twice — by the
// shipped gate and by the same gate under the pre-1134 rule
// (`WalkingInertConfigs`, which walks whenever any config is composed) — on
// every egress surface, in the id form and the row form, and the verdict
// maps and the audit streams are compared whole. The corpus carries the
// shapes the 562 taint, 674 multi-party, 675 budget and 525 egress suites
// judge: a restricted source and the facts derived from it, a
// declassifier-shaped `Computed` operation, a joint output over two
// parties' inputs, `Internal` and unregistered refs, and the consumer
// deployment's own registration.

/// Every egress surface, so no surface's stance is left out of the
/// comparison (the union is nullary throughout).
let private everySurface: FactEgressSurface list =
    Microsoft.FSharp.Reflection.FSharpType.GetUnionCases typeof<FactEgressSurface>
    |> Array.map (fun case -> Microsoft.FSharp.Reflection.FSharpValue.MakeUnion(case, [||]) :?> FactEgressSurface)
    |> List.ofArray

let private plainPolicy (policyRef: string) (surfaces: FactEgressSurface list) : DisclosurePolicy = {
    PolicyRef = policyRef
    Mode = Plain
    PermitSurfaces = surfaces
    ContributorScope = None
}

/// The inert configs, each one the corpus's vocabulary with nothing that
/// can taint: the consumer deployment's registration exactly; the 562
/// vocabulary with `licensed` made `Plain` and its routine dropped; and the
/// 674 two-party vocabulary made `Plain` and unscoped.
let private inertConfigs: (string * DisclosureTaintConfig) list = [
    "the consumer registration (one Plain policy, four surfaces)",
    DisclosureTaintConfig.ofLists [
        plainPolicy "trade-figures" [ FactRetrieval; FactToolResult; FactNarrativePublication; FactBrowse ]
    ] []
    "the 562 vocabulary, Plain and undeclassified",
    DisclosureTaintConfig.ofLists [
        plainPolicy "licensed" [ FactRetrieval ]
        plainPolicy "plain-hold" [ FactRetrieval ]
    ] []
    "the 674 two-party vocabulary, Plain and unscoped",
    DisclosureTaintConfig.ofLists [
        plainPolicy "party-a-licensed" [ FactRetrieval; FactExport ]
        plainPolicy "party-b-licensed" [ FactRetrieval; FactExport ]
        plainPolicy "trade-figures" [ FactBrowse ]
    ] []
]

/// The corpus, asserted into one scope: what each suite's fixtures build.
let private equalityCorpus (store: IFactStore) (scope: string) : string list =
    let put metric method value disclosure inputs =
        (assertFact store scope (draftOf metric method value disclosure inputs)).FactId

    [
        // 562 — a restricted source, a passthrough and a declassifier-shaped
        // aggregate over it, and a Plain-held source with a fact over both.
        put "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []
        put "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ]
        put "index" (Computed("aggregate-over-k", "1", "p")) (Scalar 7m) Surfaceable [ "dov-raw" ]
        put "held" (Computed("load", "1", "p")) (Series "dov-held") (Restricted "plain-hold") []
        put "mixed" (Computed("passthrough", "1", "p")) (Scalar 3m) Surfaceable [ "dov-raw"; "dov-held" ]
        // 674 / 675 — two parties' inputs and the joint aggregate over both.
        put "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted "party-a-licensed") []
        put "raw-b" (Computed("load", "1", "p")) (Series "dov-b") (Restricted "party-b-licensed") []
        put "joint" (Computed("aggregate-over-k", "1", "p")) (Scalar 11m) Surfaceable [ "dov-a"; "dov-b" ]
        // 525 egress — the consumer's policy, Internal, an unregistered ref, a leaf.
        put "trade" (HumanAsserted "analyst") (Scalar 5m) (Restricted "trade-figures") []
        put "internal" (HumanAsserted "analyst") (Scalar 6m) Disclosure.Internal []
        put "unknown" (HumanAsserted "analyst") (Scalar 8m) (Restricted "unregistered") []
        put "leaf" (HumanAsserted "analyst") (Scalar 9m) Surfaceable []
        // An id the scope does not hold.
        "no-such-fact"
    ]

let private auditRows (events: IEventStore) (scope: string) = async {
    let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

    return
        rows
        |> List.filter (fun e -> e.EventType.StartsWith("FactDisclosure", StringComparison.Ordinal))
        |> List.map (fun e -> e.EventType, e.Payload)
}

/// The corpus judged by a gate made from `make` on every surface, in the id
/// and row forms: the verdicts, the audit rows the judging wrote, and the
/// fact-blob downloads it cost.
let private judged
    (counting: FactStoreTests.CountingBlobStorage)
    (store: IFactStore)
    (scope: string)
    (ids: string list)
    (make: IEventStore -> FactDisclosureGate)
    =
    async {
        let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
        let gate = make events

        let resolved =
            StorageScopeResolver.ScopeResolution.ofStorageScope {
                ScopeId = scope
                Container = scope
                Persist = true
            }

        let! held = store.Query(scope, FactQuery.all)
        let rows = held |> List.map HeldFactRow.ofFact
        counting.Reset()
        let verdicts = ResizeArray()

        for surface in everySurface do
            let! byId = (gate :> IFactDisclosureGate).Check(scope, "user-1", surface, ids)
            let! byRows = (gate :> IHeldFactRowDisclosureGate).CheckHeldRows(resolved, "user-1", surface, ids, rows)
            verdicts.Add(surface, byId, byRows)

        let downloads = counting.Downloads
        let! audit = auditRows events scope
        return List.ofSeq verdicts, audit, downloads
    }

let private inertEqualityTests =
    testList "Phase 1134 an inert config decides as the walked gate did" [
        test "the predicate: Plain, unscoped, undeclassified is inert; each live ingredient is not" {
            let consumer = snd inertConfigs.Head
            let policy = consumer.Policies |> Map.toList |> List.head |> snd

            Expect.isTrue (DisclosureTaintConfig.isTaintInert DisclosureTaintConfig.empty) "empty is inert"
            Expect.isTrue (DisclosureTaintConfig.isTaintInert consumer) "the consumer registration is inert"
            Expect.isFalse (DisclosureTaintConfig.isTaintInert taintCfg) "the 562 vocabulary is live"

            Expect.isFalse
                (DisclosureTaintConfig.isTaintInert (
                    DisclosureTaintConfig.ofLists [ { policy with Mode = TaintPropagating } ] []
                ))
                "a TaintPropagating policy makes it live"

            Expect.isFalse
                (DisclosureTaintConfig.isTaintInert (
                    DisclosureTaintConfig.ofLists [
                        {
                            policy with
                                ContributorScope = Some "party-a"
                        }
                    ] []
                ))
                "a contributor scope makes it live"

            Expect.isFalse
                (DisclosureTaintConfig.isTaintInert (
                    DisclosureTaintConfig.ofLists [ policy ] [
                        {
                            OperationId = "aggregate-over-k"
                            Rationale = "r"
                            AcceptingScopes = []
                        }
                    ]
                ))
                "a declassification routine makes it live"
        }

        for name, config in inertConfigs do
            for budgeted in [ false; true ] do
                let label = if budgeted then " with a crossing budget composed" else ""

                testCaseAsync (sprintf "%s%s: the same verdicts and audit rows, without the graph" name label)
                <| async {
                    let counting = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
                    let factEvents = InMemoryEventStore.InMemoryEventStore() :> IEventStore
                    let store = BlobFactStore.create counting factEvents
                    let scope = "team-" + Guid.NewGuid().ToString("N")
                    let ids = equalityCorpus store scope
                    let factCount = List.length ids - 1

                    // A budget on the routine the corpus's aggregates name (675):
                    // with nothing tainted there is no crossing, so nothing charges.
                    let budgets () =
                        if budgeted then
                            Some(
                                DeclassificationBudgetConfig.create
                                    (InMemoryPrivacyBudgetLedger(fun () -> DateTimeOffset.UtcNow))
                                    [ DeclassificationBudget.countedCrossings "aggregate-over-k" 1 ]
                            )
                        else
                            None

                    let gateOn (events: IEventStore) =
                        FactDisclosureGate(store, events, taint = config, ?budgets = budgets ())

                    let! skipped, skippedAudit, skippedReads = judged counting store scope ids gateOn

                    let! walked, walkedAudit, walkedReads =
                        judged counting store scope ids (fun events -> (gateOn events).WalkingInertConfigs())

                    // The Phase 525 gate's own cost over the same checks: the id
                    // form's reads of the ids it judges, and no graph.
                    let! _, _, baseReads =
                        judged counting store scope ids (fun events -> FactDisclosureGate(store, events))

                    Expect.equal skipped walked "every verdict, on every surface, in both forms"

                    // The gate judges a check's facts concurrently, so rows are
                    // compared as a multiset: same rows, field for field.
                    Expect.equal (List.sort skippedAudit) (List.sort walkedAudit) "every audit row, field for field"

                    // Not vacuous: the corpus both denies and discloses, the
                    // walked gate really loaded the graph, and the skip did not.
                    let all =
                        skipped
                        |> List.collect (fun (_, byId, byRows) -> Map.toList byId @ Map.toList byRows)
                        |> List.map snd

                    Expect.contains all FactDisclosable "something discloses"
                    Expect.isTrue (all |> List.exists (fun v -> v <> FactDisclosable)) "something is denied"
                    Expect.isNonEmpty skippedAudit "the denials were audited"

                    Expect.isGreaterThanOrEqual
                        walkedReads
                        (2 * List.length everySurface * factCount)
                        "the walked gate loaded the scope at every check"

                    Expect.equal skippedReads baseReads "the skip reads what the taint-free gate reads: no graph"
                }
    ]

let tests =
    testList "Phase 562 taint-propagating disclosure" [
        pureWalkTests
        resolverVocabularyTests
        gateTests
        latticeLawTests
        fanOutTests
        inertEqualityTests
    ]