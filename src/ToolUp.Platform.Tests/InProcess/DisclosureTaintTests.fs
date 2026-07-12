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
        }
        {
            PolicyRef = "plain-hold"
            Mode = Plain
            PermitSurfaces = [ FactRetrieval ]
        }
    ] [
        {
            OperationId = "aggregate-over-k"
            Rationale = "aggregation over ≥5 members loses individual attribution"
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

let tests =
    testList "Phase 562 taint-propagating disclosure" [ pureWalkTests; resolverVocabularyTests; gateTests ]