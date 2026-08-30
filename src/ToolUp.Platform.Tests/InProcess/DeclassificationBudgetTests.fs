// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeclassificationBudgetTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 675 — declassification budgets at the grounding tier ──────
//
// Phase 562 declared a declassification routine safe to cross; Phase 674
// made clearance per-party. Neither counts, and a routine that is safe to
// cross once is a routine a counterparty may cross a thousand times. This
// pack asserts the cumulative half.
//
// Five properties, each of which was false before this phase:
//
//   1. spend ACCUMULATES across a series of checks against one scope, and
//      the ceiling then binds — exercised against BOTH shipped ledger
//      implementations, so the two cannot disagree about what "exhausted"
//      means;
//   2. a ceiling breach DENIES, with the audit row a policy denial writes
//      and a ref that names the routine and no quantity (GP 6);
//   3. budgets are PER CONTRIBUTING PARTY — exhausting one party's
//      allowance leaves another's untouched;
//   4. an epsilon charge on a routine that names no `INoiseMechanism` is
//      REFUSED AT REGISTRATION, not documented against;
//   5. a deployment that declares no budget is byte-for-byte the Phase
//      674 gate — same verdicts, same audit rows (GP 11 / GP 13).
//
// The non-vacuity risk here is specific and worth naming: a budget test
// can pass because nothing was ever charged. Every accumulation case
// therefore asserts the LAST admitted check as well as the first refused
// one, so a gate that charged nothing would fail on the refusal and a
// gate that charged everything would fail on the admission.

// ── Vocabulary ────────────────────────────────────────────────────

let private partyA = "party-a"
let private partyB = "party-b"
let private policyA = "party-a-licensed"
let private policyB = "party-b-licensed"
let private aggregateOp = "aggregate-over-k"
let private noiseOp = "noised-aggregate"

let private scopedPolicy (policyRef: string) (party: string option) : DisclosurePolicy = {
    PolicyRef = policyRef
    Mode = TaintPropagating
    PermitSurfaces = [ FactRetrieval; FactExport ]
    ContributorScope = party
}

let private routine (operationId: string) (accepting: string list) : DeclassificationRoutine = {
    OperationId = operationId
    Rationale = "aggregation over >=5 members loses individual attribution"
    AcceptingScopes = accepting
}

/// A single-party (party-agnostic) vocabulary: one taint-propagating
/// policy declaring no contributor scope, cleared by the routine. This is
/// the commonest shipped shape, and the one whose budget is accounted
/// under the `_unscoped` bucket.
let private unscopedCfg =
    DisclosureTaintConfig.ofLists [ scopedPolicy policyA None ] [ routine aggregateOp [] ]

/// A two-party vocabulary in which both parties accept the routine, so a
/// joint output discloses — and charges BOTH parties.
let private twoPartyCfg =
    DisclosureTaintConfig.ofLists [ scopedPolicy policyA (Some partyA); scopedPolicy policyB (Some partyB) ] [
        routine aggregateOp [ partyA; partyB ]
    ]

// ── Store + fact builders ─────────────────────────────────────────

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private newStore () =
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let store = BlobFactStore.create (InMemoryBlobStorage()) events
    store, events

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

let private assertFact (store: IFactStore) (scope: string) (draft: FactDraft) : Fact =
    match store.Assert(scope, draft) |> Async.RunSynchronously with
    | Ok fact -> fact
    | Error e -> failtestf "assert failed: %s" e

/// One restricted contribution plus a declassifying aggregate over it.
let private seedSingleParty (store: IFactStore) (scope: string) : Fact =
    assertFact store scope (draftOf "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) [])
    |> ignore

    assertFact store scope (draftOf "agg" (Computed(aggregateOp, "1", "p")) (Scalar 42m) Surfaceable [ "dov-a" ])

/// Both parties' contributions plus a joint declassifying aggregate.
let private seedJoint (store: IFactStore) (scope: string) : Fact =
    assertFact store scope (draftOf "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) [])
    |> ignore

    assertFact store scope (draftOf "raw-b" (Computed("load", "1", "p")) (Series "dov-b") (Restricted policyB) [])
    |> ignore

    assertFact
        store
        scope
        (draftOf "joint" (Computed(aggregateOp, "1", "p")) (Scalar 42m) Surfaceable [ "dov-a"; "dov-b" ])

let private rowsOf (events: IEventStore) (scope: string) (eventType: string) = async {
    let! rows = events.ReadBySource(scope, FactEvents.SourceModule)
    return rows |> List.filter (fun e -> e.EventType = eventType)
}

// ── Ledger implementations under test ─────────────────────────────
//
// Both shipped implementations, driven through the SAME cases. The
// federation tier's contract pack already holds each to the seam's own
// contract; what is asserted here is that the grounding tier's binding
// behaves identically on either, which is the property that would break
// silently if the binding leaned on an in-memory detail.

let private frozenNow = DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)

let private ledgers: (string * (unit -> IPrivacyBudgetLedger)) list = [
    "InMemoryPrivacyBudgetLedger", (fun () -> InMemoryPrivacyBudgetLedger(fun () -> frozenNow) :> IPrivacyBudgetLedger)
    "BlobPrivacyBudgetLedger",
    fun () -> BlobPrivacyBudgetLedger(InMemoryBlobStorage(), (fun () -> frozenNow)) :> IPrivacyBudgetLedger
]

let private budgetConfig (ledger: IPrivacyBudgetLedger) (budgets: DeclassificationBudget list) =
    DeclassificationBudgetConfig.create ledger budgets
    |> DeclassificationBudgetConfig.withClock (fun () -> frozenNow)

let private gateWith (taint: DisclosureTaintConfig) (budgets: DeclassificationBudgetConfig option) store events =
    FactDisclosureGate.createConfiguredWithBudgets (Some taint) None budgets store events

// ── 675.A — the registration refusal ──────────────────────────────

let registrationTests =
    testList "Phase 675 budget registration" [

        test "an epsilon charge on a routine naming no INoiseMechanism is refused at registration" {
            // Constructed by hand rather than through `chargedEpsilon`,
            // whose signature already REQUIRES a mechanism name — the
            // point of the check is that the record is reachable anyway,
            // so the refusal has to live at registration.
            let deterministic = {
                OperationId = aggregateOp
                Charge = ChargedEpsilon(10m, 1m)
                NoiseMechanism = None
                Epoch = PerpetualBudget
                WithholdCharge = WithholdCharged
                ReservationTtl = TimeSpan.FromMinutes 15.0
            }

            match DeclassificationBudgetConfig.tryCreate (NoPrivacyBudgetLedger()) [ deterministic ] with
            | Ok _ ->
                failtest
                    "a deterministic routine was allowed to declare a chargeable epsilon — the ledger would then be accounting a quota and calling it a privacy loss"
            | Error errors ->
                Expect.isTrue
                    (errors |> List.exists (fun e -> e.Contains "INoiseMechanism"))
                    "the refusal names what is missing"

                Expect.isTrue
                    (errors |> List.exists (fun e -> e.Contains "CountedCrossings"))
                    "and names the correct declaration for a deterministic routine"
        }

        test "the same declaration raises at compose rather than at the first disclosure" {
            let deterministic = {
                OperationId = aggregateOp
                Charge = ChargedEpsilon(10m, 1m)
                NoiseMechanism = None
                Epoch = PerpetualBudget
                WithholdCharge = WithholdCharged
                ReservationTtl = TimeSpan.FromMinutes 15.0
            }

            Expect.throws
                (fun () ->
                    DeclassificationBudgetConfig.create (NoPrivacyBudgetLedger()) [ deterministic ]
                    |> ignore)
                "a privacy control that fails late fails after something has already been disclosed"
        }

        test "a routine that NAMES its noise mechanism may charge epsilon" {
            let noised =
                DeclassificationBudget.chargedEpsilon noiseOp "discrete-laplace" 10m 0.5m

            Expect.equal (DeclassificationBudget.validate noised) [] "a noise-drawing routine earns the epsilon claim"

            match DeclassificationBudgetConfig.tryCreate (NoPrivacyBudgetLedger()) [ noised ] with
            | Ok config ->
                Expect.equal
                    (DeclassificationBudgetConfig.budgetFor config noiseOp
                     |> Option.map _.NoiseMechanism)
                    (Some(Some "discrete-laplace"))
                    "the mechanism is recorded for the audit trail"
            | Error errors -> failtestf "a well-formed epsilon budget was refused: %A" errors
        }

        test "a crossing count is not spelled epsilon" {
            // The honesty property in its smallest form: the ONLY
            // constructor reachable without naming a mechanism produces a
            // charge whose case name says "count", and the policy it
            // reduces to charges exactly one unit.
            let counted = DeclassificationBudget.countedCrossings aggregateOp 5

            Expect.equal counted.Charge (CountedCrossings 5) "a deterministic routine counts crossings"
            Expect.equal counted.NoiseMechanism None "and names no mechanism"

            let policy = DeclassificationBudget.policyFor counted
            Expect.equal policy.EpsilonPerQuery 1m "one unit per crossing"
            Expect.equal policy.EpsilonCeiling 5m "against a ceiling of five crossings"
        }

        test "two budgets for one routine are refused rather than silently resolved" {
            let budgets = [
                DeclassificationBudget.countedCrossings aggregateOp 5
                DeclassificationBudget.countedCrossings aggregateOp 50
            ]

            match DeclassificationBudgetConfig.tryCreate (NoPrivacyBudgetLedger()) budgets with
            | Ok _ -> failtest "picking one of two ceilings silently would enforce a ceiling nobody declared"
            | Error errors ->
                Expect.isTrue (errors |> List.exists (fun e -> e.Contains aggregateOp)) "the refusal names the routine"
        }

        test "a ceiling at or below zero is refused as a sealed routine expressed by accident" {
            match
                DeclassificationBudgetConfig.tryCreate (NoPrivacyBudgetLedger()) [
                    DeclassificationBudget.countedCrossings aggregateOp 0
                ]
            with
            | Ok _ -> failtest "a zero ceiling admits nothing and is never what a deployment meant"
            | Error errors -> Expect.isNonEmpty errors "the refusal is reported"
        }
    ]

// ── 675.B/D — accumulation and the binding ceiling ────────────────

let private accumulationCase (name: string, mkLedger: unit -> IPrivacyBudgetLedger) =
    testList name [

        testCaseAsync "spend accumulates across a series of checks and the ceiling then binds"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let budgets =
                budgetConfig (mkLedger ()) [ DeclassificationBudget.countedCrossings aggregateOp 3 ]

            let gate = gateWith unscopedCfg (Some budgets) store events
            let agg = seedSingleParty store scope

            // Three crossings are affordable.
            for i in 1..3 do
                let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

                Expect.equal
                    (verdicts.TryFind agg.FactId)
                    (Some FactDisclosable)
                    (sprintf "crossing %d is within the declared ceiling of 3" i)

            // The fourth is not. If the binding charged nothing, this
            // case fails here; if it charged too eagerly, the loop
            // above already failed.
            let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

            Expect.equal
                (verdicts.TryFind agg.FactId)
                (Some(FactNotDisclosable(DeclassificationBudget.ExhaustedPrefix + aggregateOp)))
                "the fourth crossing exceeds the ceiling and is denied"
        }

        testCaseAsync "the exhaustion refusal is audited like any other denial, and names no quantity"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let budgets =
                budgetConfig (mkLedger ()) [ DeclassificationBudget.countedCrossings aggregateOp 1 ]

            let gate = gateWith unscopedCfg (Some budgets) store events
            let agg = seedSingleParty store scope

            let! _ = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])
            let! _ = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

            let! denies = rowsOf events scope DisclosureEvents.DeniedType
            Expect.equal denies.Length 1 "exactly the exhausted check wrote a deny row (GP 6)"

            let payload = denies.Head.Payload

            Expect.isTrue
                (payload.Contains(DeclassificationBudget.ExhaustedPrefix + aggregateOp))
                "the deny row carries the typed budget refusal ref"

            // A caller able to read back "remaining 0.4" while varying
            // its query has an oracle beside the one the taint walk
            // already refuses it.
            Expect.isFalse (payload.Contains "remaining") "no quantity rides the refusal"
        }
    ]

let accumulationTests =
    testList "Phase 675 budget accumulation (both ledgers)" (ledgers |> List.map accumulationCase)

// ── 675.B — per-party accounting, and what is NOT charged ─────────

let bindingTests =
    testList "Phase 675 FactDisclosureGate budget binding" [

        testCaseAsync "budgets are per contributing party — one party's exhaustion is not another's"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let ledger =
                InMemoryPrivacyBudgetLedger(fun () -> frozenNow) :> IPrivacyBudgetLedger

            let budgets =
                budgetConfig ledger [ DeclassificationBudget.countedCrossings aggregateOp 2 ]

            let gate = gateWith twoPartyCfg (Some budgets) store events
            let joint = seedJoint store scope

            // Two joint crossings: each charges party A AND party B once.
            for _ in 1..2 do
                let! verdicts = gate.Check(scope, "user-1", FactExport, [ joint.FactId ])
                Expect.equal (verdicts.TryFind joint.FactId) (Some FactDisclosable) "within both parties' ceilings"

            // Read both parties' ledgers directly: each spent two, not
            // one, and not four. A binding that charged a single shared
            // bucket would show 4 on one scope and 0 on the other.
            let readFor party = async {
                let budget = DeclassificationBudget.countedCrossings aggregateOp 2
                let scopeKey = DeclassificationBudget.scopeFor budget party frozenNow
                return! ledger.RemainingBudget(scopeKey, 2m)
            }

            let! a = readFor partyA
            let! b = readFor partyB

            Expect.equal a.EpsilonCommitted 2m "party A was charged once per crossing"
            Expect.equal b.EpsilonCommitted 2m "and party B independently, out of its own allowance"
            Expect.equal a.QueryCount 2 "two settled crossings against party A"
        }

        testCaseAsync "a fact denied on other grounds spends nothing"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let ledger =
                InMemoryPrivacyBudgetLedger(fun () -> frozenNow) :> IPrivacyBudgetLedger

            let budgets =
                budgetConfig ledger [ DeclassificationBudget.countedCrossings aggregateOp 5 ]

            // Party B does NOT accept the routine, so the conjunction
            // denies. The gate answered nothing about the protected data,
            // so no party's allowance may move.
            let unacceptedCfg =
                DisclosureTaintConfig.ofLists [ scopedPolicy policyA (Some partyA); scopedPolicy policyB (Some partyB) ] [
                    routine aggregateOp [ partyA ]
                ]

            let gate = gateWith unacceptedCfg (Some budgets) store events
            let joint = seedJoint store scope

            let! verdicts = gate.Check(scope, "user-1", FactExport, [ joint.FactId ])

            Expect.equal
                (verdicts.TryFind joint.FactId)
                (Some(FactNotDisclosable "multi-party-unsatisfied:party-b"))
                "the Phase 674 conjunction still decides this, not the budget"

            let budget = DeclassificationBudget.countedCrossings aggregateOp 5
            let! a = ledger.RemainingBudget(DeclassificationBudget.scopeFor budget partyA frozenNow, 5m)

            Expect.equal a.EpsilonCommitted 0m "a disclosure that never happened costs nobody anything"
            Expect.equal a.EpsilonReserved 0m "and leaks no open reservation"
        }

        testCaseAsync "a routine with no declared budget crosses freely"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            let ledger =
                InMemoryPrivacyBudgetLedger(fun () -> frozenNow) :> IPrivacyBudgetLedger

            // A budget for a DIFFERENT routine: declared, valid, and
            // never reached.
            let budgets =
                budgetConfig ledger [ DeclassificationBudget.countedCrossings noiseOp 1 ]

            let gate = gateWith unscopedCfg (Some budgets) store events
            let agg = seedSingleParty store scope

            for _ in 1..5 do
                let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

                Expect.equal
                    (verdicts.TryFind agg.FactId)
                    (Some FactDisclosable)
                    "an unbudgeted routine is uncounted, exactly as before Phase 675"
        }

        // The settlement policy is asserted at the reserve/settle seam
        // rather than through the gate, deliberately. Driving it from the
        // gate would need a derivation that refuses PART-WAY through its
        // own crossings, which the Phase 562 walk does not produce (it
        // stops at the first declassifier), so a gate-level case would
        // have asserted a path it never took — a green that proves
        // nothing. The seam is where the policy actually lives.
        testCaseAsync "a denied crossing is charged or returned per the routine's own WithholdCharge"
        <| async {
            let crossing: TaintCrossing = {
                DeclassifierFactId = "D"
                OperationId = aggregateOp
                Rationale = "aggregation over >=5 members"
                AcceptedScopes = []
            }

            let settleUnder (charge: WithholdCharge) (disclosed: bool) = async {
                let ledger =
                    InMemoryPrivacyBudgetLedger(fun () -> frozenNow) :> IPrivacyBudgetLedger

                let budget =
                    DeclassificationBudget.countedCrossings aggregateOp 5
                    |> DeclassificationBudget.withWithholdCharge charge

                let config = budgetConfig ledger [ budget ]
                let! outcome = DeclassificationBudgetGate.reserve config [ crossing ]

                match outcome with
                | CrossingsRefused(policyRef, _) -> return failtestf "an unspent budget refused: %s" policyRef
                | CrossingsHeld held ->
                    Expect.equal held.Length 1 "one crossing, one unscoped party, one hold"
                    do! DeclassificationBudgetGate.settle config disclosed held

                    let! reading =
                        ledger.RemainingBudget(
                            DeclassificationBudget.scopeFor budget DeclassificationBudget.UnscopedParty frozenNow,
                            5m
                        )

                    return reading
            }

            let! chargedDeny = settleUnder WithholdCharged false
            let! freeDeny = settleUnder WithholdFree false
            let! released = settleUnder WithholdCharged true

            Expect.equal
                chargedDeny.EpsilonCommitted
                1m
                "a refusal discloses a bit, so the strict default charges it — that is what closes the free-probe channel"

            Expect.equal freeDeny.EpsilonCommitted 0m "WithholdFree returns the charge, and is strictly weaker"
            Expect.equal released.EpsilonCommitted 1m "a released disclosure always commits"

            for reading in [ chargedDeny; freeDeny; released ] do
                Expect.equal reading.EpsilonReserved 0m "settlement never leaves a reservation open"
        }
    ]

// ── 675.B — the byte-identical floor (GP 11 / GP 13) ──────────────

let inertTests =
    testList "Phase 675 no-budget deployments are byte-for-byte pre-675" [

        testCaseAsync "verdicts and audit rows are identical with no budget config composed"
        <| async {
            let run (budgets: DeclassificationBudgetConfig option) = async {
                let store, events = newStore ()
                let scope = "team-" + Guid.NewGuid().ToString("N")
                let gate = gateWith twoPartyCfg budgets store events
                let joint = seedJoint store scope

                let! verdicts = gate.Check(scope, "user-1", FactExport, [ joint.FactId ])
                let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

                return
                    verdicts.TryFind joint.FactId,
                    rows |> List.map _.EventType |> List.sort,
                    rows
                    |> List.filter (fun r -> r.EventType <> FactEvents.AssertedType)
                    |> List.length
            }

            let! withoutBudgets = run None

            // An EMPTY config is treated as absent, deliberately: a
            // deployment that composed the knob and declared nothing pays
            // nothing.
            let! withEmpty = run (Some(budgetConfig (InMemoryPrivacyBudgetLedger(fun () -> frozenNow)) []))

            Expect.equal withEmpty withoutBudgets "an empty budget config is indistinguishable from none"

            // And a config declaring a budget for a routine this
            // derivation never crosses changes nothing either.
            let! withUnrelated =
                run (
                    Some(
                        budgetConfig (InMemoryPrivacyBudgetLedger(fun () -> frozenNow)) [
                            DeclassificationBudget.countedCrossings "some-other-routine" 1
                        ]
                    )
                )

            Expect.equal withUnrelated withoutBudgets "an unreached budget changes no verdict and writes no row"
        }

        test "the pre-675 gate constructions still compile and behave as before" {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")

            // Every pre-675 entry point, exercised so a widened
            // constructor cannot silently change what they build.
            let plain = FactDisclosureGate.create store events
            let tainted = FactDisclosureGate.createWithTaint unscopedCfg store events

            let configured =
                FactDisclosureGate.createConfigured (Some unscopedCfg) None store events

            let agg = seedSingleParty store scope

            let verdictOf (gate: IFactDisclosureGate) =
                gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])
                |> Async.RunSynchronously
                |> Map.tryFind agg.FactId

            Expect.equal (verdictOf tainted) (Some FactDisclosable) "createWithTaint is unchanged"
            Expect.equal (verdictOf configured) (verdictOf tainted) "createConfigured agrees with it"

            Expect.equal
                (verdictOf plain)
                (Some FactDisclosable)
                "and the bare gate still passes a Surfaceable fact, having no taint walk to run"
        }
    ]

let tests =
    testList "Phase 675 declassification budgets" [ registrationTests; accumulationTests; bindingTests; inertTests ]