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
            // The fact id is hex and may contain "42" by chance (Phase 1089's
            // frame gives this fact one that does), so it is cut out first.
            Expect.isFalse
                (denies.Head.Payload.Replace(d.FactId, "").Contains "42")
                "the denied value never rides the audit row"
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
                        (baseReads + 2 * List.length everySurface)
                        "the walked gate read the scope's taint index at every check"

                    Expect.equal skippedReads baseReads "the skip reads what the taint-free gate reads: no graph"
                }
    ]

// ── The disclosure-taint index (pure) ─────────────────────────────

/// A corpus exercising every edge the walk follows: a tainted producer, a
/// derived fact, a declassifier and a fact derived after it, a Plain hold,
/// an Internal fact, a human assertion, and a producer superseded in its
/// lineage (still a node).
let private indexCorpus: Fact list = [
    mkFact "R" "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []
    mkFact "D" "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ]
    mkFact "A" "index" (Computed("aggregate-over-k", "1", "p")) (Series "dov-agg") Surfaceable [ "dov-raw" ]
    mkFact "E" "export" (Computed("passthrough", "1", "p")) (Scalar 1m) Surfaceable [ "dov-agg" ]
    mkFact "M" "mixed" (Computed("join", "1", "p")) (Scalar 2m) Surfaceable [ "dov-agg"; "dov-raw" ]
    mkFact "P" "plain" (Computed("load", "1", "p")) (Series "dov-plain") (Restricted "plain-hold") []
    mkFact "Q" "fromplain" (Computed("passthrough", "1", "p")) (Scalar 3m) Surfaceable [ "dov-plain" ]
    mkFact "I" "internal" (HumanAsserted "cfo") (Scalar 4m) Disclosure.Internal []
    mkFact "R2" "raw" (Computed("load", "2", "p")) (Series "dov-raw-2") (Restricted "licensed") []
]

let private indexLawTests =
    testList "the disclosure-taint index" [

        test "the walk over the index is the walk over the facts, for every fact" {
            let graph = DisclosureTaint.buildGraph indexCorpus
            let index = DisclosureTaintIndex.ofFacts indexCorpus
            let upstream = DisclosureTaintIndex.upstream index

            for f in indexCorpus do
                Expect.equal
                    (DisclosureTaint.analyzeIndex taintCfg index upstream f.FactId)
                    (DisclosureTaint.analyze taintCfg graph f.FactId)
                    (sprintf "the outcome for %s" f.FactId)

            // Not vacuous: the corpus taints, clears and records a crossing.
            let outcomes =
                indexCorpus
                |> List.map (fun f -> DisclosureTaint.analyzeIndex taintCfg index upstream f.FactId)

            Expect.exists outcomes (fun o -> o.InheritedPolicyRef = Some "licensed") "something inherits"
            Expect.exists outcomes (fun o -> not (List.isEmpty o.Crossings)) "something is declassified"
        }

        test "apply is idempotent, commutative and associative, so the order writes land in cannot matter" {
            let deltas =
                indexCorpus |> List.map (fun f -> DisclosureTaintIndex.deltaOfFacts [ f ])

            let applyAll ds =
                ds
                |> List.fold (fun index d -> DisclosureTaintIndex.apply d index) DisclosureTaintIndex.empty

            let forward = applyAll deltas
            Expect.equal forward (DisclosureTaintIndex.ofFacts indexCorpus) "one write per fact is the rebuild"
            Expect.equal (applyAll (List.rev deltas)) forward "reversed order"
            Expect.equal (applyAll (deltas @ deltas)) forward "every delta twice"

            let a = applyAll (List.take 3 deltas)
            let b = applyAll (deltas |> List.skip 3 |> List.take 3)
            let c = applyAll (List.skip 6 deltas)

            Expect.equal
                (DisclosureTaintIndex.union (DisclosureTaintIndex.union a b) c)
                (DisclosureTaintIndex.union a (DisclosureTaintIndex.union b c))
                "union is associative"

            Expect.equal (DisclosureTaintIndex.union a b) (DisclosureTaintIndex.union b a) "union is commutative"
            Expect.equal (DisclosureTaintIndex.union a a) a "union is idempotent"
        }

        test "two payloads of one address join fail-closed: both policies taint, and two operations declassify nothing" {
            let clean =
                mkFact "X" "x" (Computed("aggregate-over-k", "1", "p")) (Series "dov-x") Surfaceable [ "dov-raw" ]

            let other = {
                clean with
                    Method = Computed("load", "1", "p")
                    Disclosure = Restricted "licensed"
            }

            let joined =
                DisclosureTaintIndex.ofFacts [
                    indexCorpus.Head // R, the tainted producer of dov-raw
                    clean
                    other
                    mkFact "Y" "y" (Computed("passthrough", "1", "p")) (Scalar 1m) Surfaceable [ "dov-x" ]
                ]

            let node = DisclosureTaintIndex.tryNode "X" joined |> Option.get
            Expect.equal node.Policies [ "licensed" ] "the restricted payload's policy is kept"
            Expect.equal node.Operations [ "aggregate-over-k"; "load" ] "both operations are kept"

            let outcome =
                DisclosureTaint.analyzeIndex taintCfg joined (DisclosureTaintIndex.upstream joined) "Y"

            Expect.equal outcome.InheritedPolicyRef (Some "licensed") "an ambiguous declassifier clears nothing"
            Expect.isEmpty outcome.Crossings "and records no crossing"
        }

        test "a write plan's delta is a node for every fact it stores, asserted or superseding" {
            let drafts = [
                draftOf "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") []
                draftOf "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ]
            ]

            let request =
                match FactWrite.prepare WriteShape.BatchAssert drafts with
                | Ok request -> request
                | Error _ -> failtest "the drafts are well-formed"

            let now = DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
            let first = FactWrite.plan now (fun _ -> false) [] request

            let delta = DisclosureTaintIndex.deltaOf first
            Expect.equal (delta.Entries |> List.map fst) (first.Facts |> List.map _.FactId) "one entry per stored fact"

            // A superseding write: the new version is a node, and the old one
            // is not removed.
            let v2 = [
                {
                    drafts.Head with
                        Evidence = {
                            drafts.Head.Evidence with
                                InputHashes = [ "dov-in-2" ]
                        }
                }
            ]

            let request2 =
                match FactWrite.prepare WriteShape.BatchAssert v2 with
                | Ok request -> request
                | Error _ -> failtest "the draft is well-formed"

            let stored = first.Facts |> List.map _.FactId |> Set.ofList

            let second =
                FactWrite.plan (now.AddMinutes 1.0) stored.Contains first.Facts request2

            Expect.isNonEmpty second.Supersessions "the second write supersedes"

            let index =
                DisclosureTaintIndex.empty
                |> DisclosureTaintIndex.apply delta
                |> DisclosureTaintIndex.apply (DisclosureTaintIndex.deltaOf second)

            Expect.equal index (DisclosureTaintIndex.ofFacts (first.Facts @ second.Facts)) "the rebuild of both writes"

            // An idempotent write stores nothing and adds nothing.
            let all = Set.union stored (second.Facts |> List.map _.FactId |> Set.ofList)
            let idempotent = FactWrite.plan (now.AddMinutes 2.0) all.Contains [] request
            Expect.isEmpty (DisclosureTaintIndex.deltaOf idempotent).Entries "an idempotent write adds no entry"
        }

        test "restrictTo drops the nodes of addresses a store no longer holds" {
            let index = DisclosureTaintIndex.ofFacts indexCorpus
            let kept = DisclosureTaintIndex.restrictTo (Set.ofList [ "R"; "D" ]) index
            Expect.equal (DisclosureTaintIndex.factIds kept) (Set.ofList [ "R"; "D" ]) "only the held addresses"

            Expect.equal
                (DisclosureTaintIndex.ofEntries (DisclosureTaintIndex.toEntries index))
                index
                "the persisted entry list round-trips"
        }
    ]

// ── The four stages (pure) ────────────────────────────────────────

let private row (disclosure: Disclosure) : HeldFactRow option =
    Some {
        FactId = "F"
        Metric = "m"
        Disclosure = disclosure
    }

let private outcomeOf (refs: string list) (crossings: TaintCrossing list) (scopes: string list) : TaintOutcome = {
    InheritedPolicyRef = List.tryHead refs
    InheritedPolicyRefs = refs
    UnsatisfiedScopes = []
    ContributorScopes = scopes
    Crossings = crossings
    InheritedLabel = TaintLabel.ofPolicyRefs refs
    Findings = []
}

let private crossing: TaintCrossing = {
    DeclassifierFactId = "A"
    OperationId = "aggregate-over-k"
    Rationale = "r"
    AcceptedScopes = []
}

let private stageTests =
    testList "the disclosure gate's four stages" [

        test "viewer: an id the scope does not hold is denied, never disclosed" {
            Expect.equal
                (DisclosureStages.viewer DisclosurePolicyResolver.denyUnknown None FactExport None None)
                (FactNotDisclosable "unknown-fact")
                "unknown-fact"
        }

        test "viewer: a fact's own classification decides, through the resolver" {
            let resolve = DisclosureTaintConfig.resolver taintCfg

            Expect.equal
                (DisclosureStages.viewer resolve None FactExport None (row Surfaceable))
                FactDisclosable
                "Surfaceable discloses"

            Expect.equal
                (DisclosureStages.viewer resolve None FactExport None (row (Restricted "licensed")))
                (FactNotDisclosable "licensed")
                "licensed is not permitted at export"

            Expect.equal
                (DisclosureStages.viewer resolve None FactRetrieval None (row (Restricted "licensed")))
                FactDisclosable
                "licensed is permitted at retrieval"
        }

        test "viewer: with the viewer-aware facet a Restricted fact with no readable viewer denies as unreadable" {
            let aware: ViewerAwareDisclosurePolicyResolver = fun _ _ _ -> Some true
            let restricted = row (Restricted "team-only")

            Expect.isTrue (DisclosureStages.needsViewer (Some aware) restricted) "a Restricted fact needs the viewer"
            Expect.isFalse (DisclosureStages.needsViewer (Some aware) (row Surfaceable)) "a Surfaceable one does not"
            Expect.isFalse (DisclosureStages.needsViewer None restricted) "nor does any fact without the facet"

            for unread in [ None; Some(Error "level unreadable") ] do
                Expect.equal
                    (DisclosureStages.viewer
                        DisclosurePolicyResolver.denyUnknown
                        (Some aware)
                        FactExport
                        unread
                        restricted)
                    (FactNotDisclosable ViewerAwareDisclosure.UnreadableRef)
                    "fail closed"

            Expect.equal
                (DisclosureStages.viewer
                    DisclosurePolicyResolver.denyUnknown
                    (Some aware)
                    FactExport
                    (Some(Ok(DisclosureViewer.leastPrivileged TeamVisible)))
                    restricted)
                FactDisclosable
                "a resolved viewer is decided for"
        }

        test "purpose: no taxonomy refuses nothing; an unclaimed or out-of-set claim refuses with the allowed set" {
            let config =
                DisclosurePurposeConfig.ofLists
                    {
                        Version = "v1"
                        Purposes = [
                            {
                                PurposeId = "audit"
                                Description = "d"
                            }
                        ]
                    }
                    [ FactExport, [ "audit" ] ]

            Expect.isNone (DisclosureStages.purpose None None FactExport) "no taxonomy"
            Expect.isNone (DisclosureStages.purpose (Some config) (Some "audit") FactExport) "an allowed claim"

            Expect.equal
                (DisclosureStages.purpose (Some config) (Some "sales") FactExport)
                (Some "purpose-denied:sales (allowed: audit)")
                "out of set"

            Expect.equal
                (DisclosureStages.purpose (Some config) None FactRetrieval)
                (Some "purpose-unclaimed (allowed: none)")
                "unclaimed, at a surface serving none"
        }

        test "taint: only a disclosed fact is walked, and a surviving restriction denies" {
            let walked = ref 0

            let walk (refs: string list) =
                Some(fun (_: string) ->
                    walked.Value <- walked.Value + 1
                    outcomeOf refs [] [])

            Expect.equal (DisclosureStages.taint None "F" FactDisclosable) (FactDisclosable, None) "no walk composed"

            let denied, none =
                DisclosureStages.taint (walk [ "licensed" ]) "F" (FactNotDisclosable "own")

            Expect.equal (denied, none, walked.Value) (FactNotDisclosable "own", None, 0) "a denied fact is not walked"

            let verdict, outcome =
                DisclosureStages.taint (walk [ "licensed" ]) "F" FactDisclosable

            Expect.equal verdict (FactNotDisclosable "licensed") "the inherited ref denies"
            Expect.isSome outcome "the walk is kept for the audit"

            let clean, _ = DisclosureStages.taint (walk []) "F" FactDisclosable
            Expect.equal clean FactDisclosable "a clean derivation discloses"
        }

        test "budget: only a released, declassified fact owes a reservation; a refusal denies under its ref" {
            let crossed = Some(outcomeOf [] [ crossing ] [])
            Expect.equal (DisclosureStages.crossings FactDisclosable crossed) [ crossing ] "a release that crossed"
            Expect.isEmpty (DisclosureStages.crossings (FactNotDisclosable "x") crossed) "a denial owes nothing"
            Expect.isEmpty (DisclosureStages.crossings FactDisclosable None) "no walk, no crossing"

            Expect.equal (DisclosureStages.budget FactDisclosable None) FactDisclosable "nothing asked"

            Expect.equal
                (DisclosureStages.budget FactDisclosable (Some(CrossingsHeld [])))
                FactDisclosable
                "an affordable crossing"

            Expect.equal
                (DisclosureStages.budget FactDisclosable (Some(CrossingsRefused("budget-exhausted:k", []))))
                (FactNotDisclosable "budget-exhausted:k")
                "a refused routine"
        }

        test "audit rows: a denial is one row; a disclosure is a row per crossing and an access row per party" {
            let context = {
                Surface = FactExport
                Principal = "u"
                Purpose = ""
                TaxonomyVersion = ""
            }

            let read = {
                FactId = "F"
                Metric = "m"
                Verdict = FactNotDisclosable "licensed"
                Taint = None
                ContributorScopes = []
            }

            match DisclosureStages.auditRows context read with
            | [ DisclosureAuditRow.Denied denied ] ->
                Expect.equal
                    (denied.PolicyRef, denied.Surface)
                    ("licensed", "Export")
                    "the denial names ref and surface"
            | other -> failtestf "one denial row, got %A" other

            let disclosed = {
                read with
                    Verdict = FactDisclosable
                    Taint = Some(outcomeOf [] [ crossing ] [ "party-a" ])
                    ContributorScopes = [ "party-a" ]
            }

            match DisclosureStages.auditRows context disclosed with
            | [ DisclosureAuditRow.Declassified d; DisclosureAuditRow.Accessed a ] ->
                Expect.equal d.DeclassifierFactId "A" "the crossing row"
                Expect.equal a.ContributorScopes [ "party-a" ] "the access row"
            | other -> failtestf "a crossing row and an access row, got %A" other

            Expect.isEmpty
                (DisclosureStages.auditRows context { read with Verdict = FactDisclosable })
                "a party-free, uncrossed disclosure writes nothing"
        }
    ]

// ── The check no longer loads the scope ───────────────────────────

/// An `IFactStore` decorator that counts the whole-scope listings reaching
/// the store: a `Query` with `IncludeSuperseded`, which is what the taint
/// stage loaded per check before the index.
type private QueryCountingStore(inner: IFactStore) =
    let gate = obj ()
    let mutable wholeScope = 0

    let count (query: FactQuery) =
        if query.IncludeSuperseded then
            lock gate (fun () -> wholeScope <- wholeScope + 1)

    member _.WholeScopeQueries = lock gate (fun () -> wholeScope)

    // A decorator that hides its inner store's index makes the gate list the
    // scope, so this one forwards it, as a production decorator should.
    interface IDisclosureTaintIndexSource with
        member _.ReadTaintIndex(scopeId: string) =
            (inner :?> IDisclosureTaintIndexSource).ReadTaintIndex scopeId

        member _.ReadTaintIndex(scope: ResolvedScope) =
            (inner :?> IDisclosureTaintIndexSource).ReadTaintIndex scope

    interface IFactStore with
        member _.Assert(scopeId: string, draft) = inner.Assert(scopeId, draft)
        member _.Assert(scope: ResolvedScope, draft) = inner.Assert(scope, draft)
        member _.AssertBatch(scopeId: string, drafts) = inner.AssertBatch(scopeId, drafts)
        member _.AssertBatch(scope: ResolvedScope, drafts) = inner.AssertBatch(scope, drafts)
        member _.Get(scopeId: string, factId) = inner.Get(scopeId, factId)
        member _.Get(scope: ResolvedScope, factId) = inner.Get(scope, factId)

        member _.Query(scopeId: string, query) =
            count query
            inner.Query(scopeId, query)

        member _.Query(scope: ResolvedScope, query) =
            count query
            inner.Query(scope, query)

        member _.QueryWithCompetition(scopeId: string, query) =
            inner.QueryWithCompetition(scopeId, query)

        member _.QueryWithCompetition(scope: ResolvedScope, query) =
            inner.QueryWithCompetition(scope, query)

        member _.QuerySupersessionChain(scopeId: string, factId) =
            inner.QuerySupersessionChain(scopeId, factId)

        member _.QuerySupersessionChain(scope: ResolvedScope, factId) =
            inner.QuerySupersessionChain(scope, factId)

        member _.QueryPopulation(scopeId: string, query) = inner.QueryPopulation(scopeId, query)
        member _.QueryPopulation(scope: ResolvedScope, query) = inner.QueryPopulation(scope, query)

/// A tainted lineage (R restricted `licensed`, D derived from it) in a fresh
/// scope over a counting blob store, with the gate over a counting fact store.
let private taintedScope () =
    let blobs = FactStoreTests.CountingBlobStorage(InMemoryBlobStorage())
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let inner = BlobFactStore.create blobs events
    let scope = "team-" + Guid.NewGuid().ToString("N")

    assertFact inner scope (draftOf "raw" (Computed("load", "1", "p")) (Series "dov-raw") (Restricted "licensed") [])
    |> ignore

    let d =
        assertFact
            inner
            scope
            (draftOf "derived" (Computed("passthrough", "1", "p")) (Scalar 42m) Surfaceable [ "dov-raw" ])

    for i in 1..5 do
        assertFact inner scope (draftOf (sprintf "other-%d" i) (HumanAsserted "a") (Scalar(decimal i)) Surfaceable [])
        |> ignore

    let counted = QueryCountingStore(inner)
    blobs, counted, events, scope, d

let private scopeLoadTests =
    testList "the taint stage reads an index, not the scope" [

        testCaseAsync "one check with taint composed performs no whole-scope query"
        <| async {
            let _, counted, events, scope, d = taintedScope ()
            let gate = FactDisclosureGate.createWithTaint taintCfg counted events
            let! verdicts = gate.Check(scope, "user-1", FactExport, [ d.FactId ])

            Expect.equal (verdicts.TryFind d.FactId) (Some(FactNotDisclosable "licensed")) "the inherited deny stands"
            Expect.equal counted.WholeScopeQueries 0 "the check did not list the scope through the store"
        }

        testCaseAsync "two consecutive checks load the scope zero times, and the second reads no fact at all"
        <| async {
            let blobs, counted, events, scope, d = taintedScope ()
            let gate = FactDisclosureGate.createWithTaint taintCfg counted events
            let! _ = gate.Check(scope, "user-1", FactExport, [ d.FactId ])
            blobs.Reset()
            let! verdicts = gate.Check(scope, "user-1", FactExport, [ d.FactId ])

            Expect.equal (verdicts.TryFind d.FactId) (Some(FactNotDisclosable "licensed")) "the inherited deny stands"
            Expect.equal counted.WholeScopeQueries 0 "neither check listed the scope through the store"

            // The judged fact's own read, and the index: never the seven
            // facts of the scope.
            Expect.isLessThanOrEqual blobs.Downloads 2 "the second check read the judged fact and the index"
        }
    ]

// ── A fact computed from named facts ──────────────────────────────
//
// A derived fact whose evidence names its inputs (`fact:<id>`) carries the
// join of their labels as a computed lineage: a taint-propagating input
// taints it at every door, a routine registered for its own operation clears
// it, and nothing else does.

let private derivedCfg (routines: string list) =
    DisclosureTaintConfig.ofLists
        [
            {
                PolicyRef = "licensed"
                Mode = TaintPropagating
                PermitSurfaces = [ FactRetrieval ]
                ContributorScope = None
            }
            {
                PolicyRef = "licensed-b"
                Mode = TaintPropagating
                PermitSurfaces = [ FactRetrieval ]
                ContributorScope = None
            }
            plainPolicy "plain-hold" [ FactRetrieval ]
        ]
        (routines
         |> List.map (fun operationId -> {
             OperationId = operationId
             Rationale = "an approved derivation"
             AcceptingScopes = []
         }))

let private derivedMethod =
    Computed(PipelineAssertion.OperationId, "0.42.0", "pipeline-hash")

let derivedFactTests =
    testList "a fact computed from named facts" [

        test "its label is the join of its inputs' labels, computed rather than inferred" {
            let a =
                mkFact "a" "raw" (Computed("load", "1", "p")) (Scalar 5m) (Restricted "licensed") []

            let b =
                mkFact "b" "raw" (Computed("load", "1", "p")) (Scalar 6m) (Restricted "licensed-b") []

            let c =
                mkFact "c" "raw" (Computed("load", "1", "p")) (Scalar 7m) (Restricted "plain-hold") []

            let d =
                mkFact
                    "d"
                    "total"
                    derivedMethod
                    (Scalar 18m)
                    Surfaceable
                    ([ "a"; "b"; "c" ] |> List.map FactInput.ofFactId)

            let outcome =
                DisclosureTaint.analyze (derivedCfg []) (DisclosureTaint.buildGraph [ a; b; c; d ]) "d"

            Expect.equal
                outcome.InheritedLabel
                (TaintLabel.joinAll [
                    TaintLabel.ofPolicyRef "licensed"
                    TaintLabel.ofPolicyRef "licensed-b"
                    TaintLabel.bottom
                ])
                "both taint-propagating inputs reach it; the plain one does not"

            Expect.equal outcome.InheritedPolicyRef (Some "licensed") "the nearest input names the deny"
            Expect.isEmpty outcome.Findings "the lineage was computed, so nothing was inferred"
        }

        test "over clean inputs it carries nothing" {
            let a = mkFact "a" "raw" (Computed("load", "1", "p")) (Scalar 5m) Surfaceable []

            let d =
                mkFact "d" "total" derivedMethod (Scalar 5m) Surfaceable [ FactInput.ofFactId "a" ]

            let outcome =
                DisclosureTaint.analyze (derivedCfg []) (DisclosureTaint.buildGraph [ a; d ]) "d"

            Expect.equal outcome.InheritedLabel TaintLabel.bottom "clean"
            Expect.isEmpty outcome.Findings "and computed"
        }

        testCaseAsync "it is barred at every door its restricted input is, and only its own routine clears it"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let input =
                assertFact
                    store
                    scope
                    (draftOf "raw" (Computed("load", "1", "p")) (Scalar 5m) (Restricted "licensed") [])

            let derived =
                assertFact
                    store
                    scope
                    (draftOf "total" derivedMethod (Scalar 5m) Surfaceable [ FactInput.ofFactId input.FactId ])

            let verdictsUnder (config: DisclosureTaintConfig) (factId: string) = async {
                let gate = FactDisclosureGate.createWithTaint config store events

                let! verdicts =
                    everySurface
                    |> List.map (fun surface -> async {
                        let! v = gate.Check(scope, "user-1", surface, [ factId ])
                        return surface, v.TryFind factId
                    })
                    |> Async.Sequential

                return List.ofArray verdicts
            }

            let! inputVerdicts = verdictsUnder (derivedCfg []) input.FactId
            let! untouched = verdictsUnder (derivedCfg []) derived.FactId

            for (surface, verdict) in inputVerdicts do
                if verdict <> Some FactDisclosable then
                    Expect.equal
                        (untouched |> List.find (fst >> (=) surface) |> snd)
                        (Some(FactNotDisclosable "licensed"))
                        (sprintf "barred at %A, where its input is" surface)

            Expect.isTrue
                (untouched |> List.forall (snd >> (=) (Some(FactNotDisclosable "licensed"))))
                "and at every other door: taint denies wherever it reaches"

            let! otherRoutine = verdictsUnder (derivedCfg [ "aggregate-over-k" ]) derived.FactId

            Expect.isTrue
                (otherRoutine |> List.forall (snd >> (=) (Some(FactNotDisclosable "licensed"))))
                "a routine for another operation clears nothing"

            let! ownRoutine = verdictsUnder (derivedCfg [ PipelineAssertion.OperationId ]) derived.FactId

            Expect.isTrue
                (ownRoutine |> List.forall (snd >> (=) (Some FactDisclosable)))
                (sprintf "the routine for its own operation clears it: %A" ownRoutine)
        }
    ]

let tests =
    testList "Phase 562 taint-propagating disclosure" [
        derivedFactTests
        indexLawTests
        stageTests
        scopeLoadTests
        pureWalkTests
        resolverVocabularyTests
        gateTests
        latticeLawTests
        fanOutTests
        inertEqualityTests
    ]