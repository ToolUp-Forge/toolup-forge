module ToolUp.Platform.Tests.InProcess.PrivacyBudgetLedgerTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 190 — the clean-room privacy-budget ledger ────────────────
//
// Phase 311 made the per-query floor structural: a gated contract's
// answers cross `CleanRoomGate`'s dispatch wrapper whether the handler
// co-operates or not. What no per-query check can see is the SERIES —
// a hundred individually-compliant answers, each above k, together
// exhausting the protection. This pack is about the two claims that
// close that gap, and about neither of them being an inference:
//
//   1. **Cumulative exhaustion.** N in-floor queries that individually
//      pass are refused once the declared ceiling is spent. Paired with
//      a negative control running the identical series with no meter
//      composed — without it, "the fourth query was withheld" would pass
//      equally against a gate that had broken and started refusing
//      everything.
//   2. **Atomicity.** N concurrent queries against a shared remainder
//      admit exactly the ceiling. Measured against a real conditional-
//      blob backend, with the SAME ledger run against a backend whose
//      conditional write ignores its precondition — the accounting
//      mechanism removed and nothing else — which over-admits by an
//      EXACT count, on an interleave the control PLACES with a
//      rendezvous (Phase 320's technique) rather than races for. That
//      pairing is what makes the green result mean something: it shows
//      the harness can see the failure it is claiming did not happen,
//      deterministically rather than when the scheduler obliges.
//
// Everything else here is the settlement contract those two rest on: a
// reservation that does not become a release is returned, a settlement
// is idempotent, an abandoned reservation is reclaimed on TTL, and
// scopes do not bleed into each other.

// ─── Fixtures ────────────────────────────────────────────────────────

/// A fixed instant. Every ledger under test reads it as "now", so a TTL
/// is crossed by moving a clock rather than by sleeping.
let private at = DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero)

let private budgetScope: BudgetScope = {
    TemplateId = "reach"
    CounterpartyPeerId = "buyer"
    Epoch = "perpetual"
}

let private spendOf (id: string) (epsilon: decimal) (scope: BudgetScope) : BudgetSpend = {
    ReservationId = id
    Scope = scope
    Epsilon = epsilon
    MethodName = "EstimateReach"
    OccurredAt = at
    ExpiresAt = at.AddMinutes 15.0
}

/// An `IBlobStorage` + `IConditionalBlobStorage` double that honours its
/// preconditions and, with `barrier > 1`, holds each `DownloadWithETag`
/// until that many callers have arrived, then releases permanently. The
/// barrier makes the CONTENTION deterministic: every concurrent reader
/// observes the same pre-write state, which is exactly the window a
/// non-atomic ledger would lose budget through. Retries pass straight
/// through, so a conforming ledger still converges.
///
/// The mechanism-ABSENT counterpart is `RendezvousBlobStorage` below,
/// which places the losing interleave rather than racing for it.
type private ProbeBlobStorage(barrier: int) =
    let inner = InMemoryBlobStorage()
    let barrierLock = obj ()
    let mutable arrived = 0

    let arrive () =
        lock barrierLock (fun () ->
            arrived <- arrived + 1
            arrived)

    let arrivals () = lock barrierLock (fun () -> arrived)

    let waitForBarrier () = async {
        if barrier > 1 then
            arrive () |> ignore
            let deadline = DateTime.UtcNow.AddSeconds 5.0

            while arrivals () < barrier && DateTime.UtcNow < deadline do
                do! Async.Sleep 5
    }

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) = async {
            do! waitForBarrier ()
            return! (inner :> IConditionalBlobStorage).DownloadWithETag(container, blobName)
        }

        member _.UploadWithETag(container, blobName, content, condition) =
            (inner :> IConditionalBlobStorage).UploadWithETag(container, blobName, content, condition)

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            (inner :> IBlobStorage).Upload(container, blobName, content)

        member _.Download(container, blobName) =
            (inner :> IBlobStorage).Download(container, blobName)

        member _.Delete(container, blobName) =
            (inner :> IBlobStorage).Delete(container, blobName)

        member _.List(container, prefix) =
            (inner :> IBlobStorage).List(container, prefix)

        member _.Exists(container, blobName) =
            (inner :> IBlobStorage).Exists(container, blobName)

        member _.GetMetadata(container, blobName) =
            (inner :> IBlobStorage).GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            (inner :> IBlobStorage).DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            (inner :> IBlobStorage).Erase(container, prefix, policy, dryRun)

/// An `IConditionalBlobStorage` whose conditional write ignores its
/// precondition — the download-modify-upload the real ledger refuses to
/// ship, reached without editing the ledger — and which HOLDS the first
/// writer between its read and its write, the Phase 320 rendezvous
/// (`ExternalCallbackTests.RendezvousDispatcher`, tests 320.D).
///
/// **Why parking inside `UploadWithETag` lands the interleave in the
/// right place.** The ledger's `transact` reads the document, applies
/// the accounting decision, *then* writes. A caller held here has
/// already read and decided against a view that is about to go stale —
/// exactly the window a non-atomic read-modify-write loses budget
/// through. A second caller runs to completion inside that window, so
/// the over-admission is PLACED, not raced for: the barrier control
/// this replaced fired eight workers and hoped they interleaved, and
/// failed on Phase 320's verification run precisely because under
/// machine load they did not.
type private RendezvousBlobStorage() =
    let inner = InMemoryBlobStorage()
    let reached = new System.Threading.ManualResetEventSlim(false)
    let release = new System.Threading.ManualResetEventSlim(false)
    let holdGate = obj ()
    let mutable held = false

    /// `true` for exactly the first writer; later writes (the second
    /// caller's, and both settlements) pass straight through.
    let tryHold () =
        lock holdGate (fun () ->
            if held then
                false
            else
                held <- true
                true)

    /// Block until the first writer has read its view and reached its
    /// write.
    member _.WaitUntilWriting(timeoutMs: int) =
        if not (reached.Wait timeoutMs) then
            failtest "the held caller never reached its write"

    /// Let the held writer land its now-stale view.
    member _.Release() = release.Set()

    interface IConditionalBlobStorage with
        member _.DownloadWithETag(container, blobName) =
            (inner :> IConditionalBlobStorage).DownloadWithETag(container, blobName)

        member _.UploadWithETag(container, blobName, content, _condition) = async {
            if tryHold () then
                reached.Set()

                if not (release.Wait 30_000) then
                    failtest "the rendezvous was never released"

            // The mechanism removed: write regardless of what the
            // precondition says the caller observed.
            let! written = (inner :> IBlobStorage).Upload(container, blobName, content)

            return
                match written with
                | Ok etag -> Ok etag
                | Error message -> Error(ConditionalWriteFailure message)
        }

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            (inner :> IBlobStorage).Upload(container, blobName, content)

        member _.Download(container, blobName) =
            (inner :> IBlobStorage).Download(container, blobName)

        member _.Delete(container, blobName) =
            (inner :> IBlobStorage).Delete(container, blobName)

        member _.List(container, prefix) =
            (inner :> IBlobStorage).List(container, prefix)

        member _.Exists(container, blobName) =
            (inner :> IBlobStorage).Exists(container, blobName)

        member _.GetMetadata(container, blobName) =
            (inner :> IBlobStorage).GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            (inner :> IBlobStorage).DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            (inner :> IBlobStorage).Erase(container, prefix, policy, dryRun)

/// An `IBlobStorage` with NO conditional-write capability at all — the
/// backend `BlobPrivacyBudgetLedger` must refuse at construction.
type private PlainBlobStorage() =
    let inner = InMemoryBlobStorage()

    interface IBlobStorage with
        member _.Upload(container, blobName, content) =
            (inner :> IBlobStorage).Upload(container, blobName, content)

        member _.Download(container, blobName) =
            (inner :> IBlobStorage).Download(container, blobName)

        member _.Delete(container, blobName) =
            (inner :> IBlobStorage).Delete(container, blobName)

        member _.List(container, prefix) =
            (inner :> IBlobStorage).List(container, prefix)

        member _.Exists(container, blobName) =
            (inner :> IBlobStorage).Exists(container, blobName)

        member _.GetMetadata(container, blobName) =
            (inner :> IBlobStorage).GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            (inner :> IBlobStorage).DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            (inner :> IBlobStorage).Erase(container, prefix, policy, dryRun)

let private run x = Async.RunSynchronously x

// ─── 1. The policy is data, and its derivations are pure ─────────────

let policyTests =
    testList "Phase 190 — the declared policy" [

        test "the default policy is the strict reading: perpetual, and refusals are charged" {
            let policy = PrivacyBudgetPolicy.create 50m 1m

            Expect.equal policy.Epoch PerpetualBudget "the ceiling must bound lifetime disclosure unless told otherwise"

            Expect.equal
                policy.WithholdCharge
                WithholdCharged
                "a free refusal is a counting oracle — charging it is the default"
        }

        test "per-method epsilon overrides the flat charge, and only for that method" {
            let policy =
                PrivacyBudgetPolicy.create 50m 1m
                |> PrivacyBudgetPolicy.withMethodEpsilon "Histogram" 4m

            Expect.equal (PrivacyBudgetPolicy.epsilonFor "Histogram" policy) 4m "the override applies"
            Expect.equal (PrivacyBudgetPolicy.epsilonFor "EstimateReach" policy) 1m "…and nothing else moves"
        }

        test "epoch labels are UTC, fixed-format and ordinal-comparable" {
            let perpetual = PrivacyBudgetPolicy.create 1m 1m
            let daily = perpetual |> PrivacyBudgetPolicy.withEpoch DailyBudget
            let monthly = perpetual |> PrivacyBudgetPolicy.withEpoch MonthlyBudget

            // A non-UTC offset landing on a different local date, so a
            // label derived from local time would be visibly wrong.
            let late = DateTimeOffset(2026, 8, 3, 23, 30, 0, TimeSpan.FromHours -6.0)

            Expect.equal (PrivacyBudgetPolicy.epochLabel late perpetual) "perpetual" "a perpetual budget has one epoch"
            Expect.equal (PrivacyBudgetPolicy.epochLabel late daily) "2026-08-04" "the daily label is the UTC date"
            Expect.equal (PrivacyBudgetPolicy.epochLabel late monthly) "2026-08" "the monthly label is the UTC month"
        }

        test "a refilling epoch mints a different scope, which is the weakening it declares" {
            let daily =
                PrivacyBudgetPolicy.create 1m 1m |> PrivacyBudgetPolicy.withEpoch DailyBudget

            let day1 = PrivacyBudgetPolicy.scopeFor "reach" "buyer" at daily
            let day2 = PrivacyBudgetPolicy.scopeFor "reach" "buyer" (at.AddDays 1.0) daily

            Expect.notEqual
                day1
                day2
                "a new day is a new budget — the documented weakening, asserted rather than assumed"

            Expect.equal day1.TemplateId "reach" "the template is part of the key"
            Expect.equal day1.CounterpartyPeerId "buyer" "…and so is the counterparty"
        }
    ]

// ─── 2. The accounting contract, over every shipped ledger ───────────

/// The same assertions against each implementation, so two ledgers
/// cannot disagree about what "exhausted" means.
let private ledgerCases: (string * (unit -> IPrivacyBudgetLedger)) list = [
    "InMemoryPrivacyBudgetLedger", (fun () -> InMemoryPrivacyBudgetLedger(fun () -> at) :> IPrivacyBudgetLedger)
    "BlobPrivacyBudgetLedger",
    (fun () -> BlobPrivacyBudgetLedger(ProbeBlobStorage 1, (fun () -> at)) :> IPrivacyBudgetLedger)
]

let ledgerContractTests =
    testList "Phase 190 — the accounting contract" [
        for name, build in ledgerCases do
            testList name [

                test "epsilon accumulates across a series and the ceiling refuses the query that would breach it" {
                    let ledger = build ()

                    for i in 1..3 do
                        match run (ledger.ReserveBudget(spendOf $"r{i}" 1m budgetScope, 3m)) with
                        | BudgetReserved(spend, _) -> run (ledger.RecordSpend(spend, SpendCommitted))
                        | BudgetRefused refusal -> failtestf "query %d was refused in-budget: %A" i refusal

                    let audited = run (ledger.RemainingBudget(budgetScope, 3m))

                    Expect.equal audited.EpsilonCommitted 3m "the series accumulated"
                    Expect.equal audited.QueryCount 3 "…and each committed query was counted"

                    Expect.equal
                        (audited.EpsilonCeiling - audited.EpsilonCommitted - audited.EpsilonReserved)
                        0m
                        "the budget is spent, and the reading says so"

                    match run (ledger.ReserveBudget(spendOf "r4" 1m budgetScope, 3m)) with
                    | BudgetRefused(BudgetExhausted(requested, remaining, ceiling)) ->
                        Expect.equal requested 1m "the refusal names what was asked for"
                        Expect.equal remaining 0m "…what was left"
                        Expect.equal ceiling 3m "…and the ceiling it was measured against"
                    | outcome -> failtestf "the fourth query must be refused, got %A" outcome
                }

                test "a charge larger than the remainder is refused whole — the budget is never overspent" {
                    let ledger = build ()

                    match run (ledger.ReserveBudget(spendOf "big" 2m budgetScope, 3m)) with
                    | BudgetReserved(spend, _) -> run (ledger.RecordSpend(spend, SpendCommitted))
                    | outcome -> failtestf "expected a reservation, got %A" outcome

                    match run (ledger.ReserveBudget(spendOf "bigger" 2m budgetScope, 3m)) with
                    | BudgetRefused(BudgetExhausted(_, remaining, _)) ->
                        Expect.equal remaining 1m "1 remains, and a 2-unit query does not get a partial answer"
                    | outcome -> failtestf "expected an exhaustion refusal, got %A" outcome

                    let audited = run (ledger.RemainingBudget(budgetScope, 3m))

                    Expect.equal audited.EpsilonCommitted 2m "the refused query left the ledger exactly as it found it"
                }

                test "a returned reservation gives the epsilon back; a committed one does not" {
                    let ledger = build ()

                    match run (ledger.ReserveBudget(spendOf "returned" 2m budgetScope, 3m)) with
                    | BudgetReserved(spend, remaining) ->
                        Expect.equal remaining 1m "the reservation is held before the answer is computed"

                        let held = run (ledger.RemainingBudget(budgetScope, 3m))

                        Expect.equal held.EpsilonReserved 2m "…and the hold is visible to an auditor while it is open"

                        run (ledger.RecordSpend(spend, SpendReturned "no answer was produced"))
                    | outcome -> failtestf "expected a reservation, got %A" outcome

                    let audited = run (ledger.RemainingBudget(budgetScope, 3m))
                    Expect.equal audited.EpsilonCommitted 0m "nothing was disclosed, so nothing is owed"
                    Expect.equal audited.EpsilonReserved 0m "…and the hold is released"
                    Expect.equal audited.QueryCount 0 "a returned reservation is not a query"
                }

                test "settling twice is a no-op, not a double charge or a double refund" {
                    let ledger = build ()

                    let spend =
                        match run (ledger.ReserveBudget(spendOf "once" 1m budgetScope, 3m)) with
                        | BudgetReserved(s, _) -> s
                        | outcome -> failtestf "expected a reservation, got %A" outcome

                    run (ledger.RecordSpend(spend, SpendCommitted))
                    run (ledger.RecordSpend(spend, SpendCommitted))
                    run (ledger.RecordSpend(spend, SpendReturned "a retry that must not credit"))

                    let audited = run (ledger.RemainingBudget(budgetScope, 3m))
                    Expect.equal audited.EpsilonCommitted 1m "the charge stands exactly once"
                    Expect.equal audited.QueryCount 1 "…and the query is counted exactly once"
                }

                test "distinct counterparties and distinct templates hold distinct budgets" {
                    let ledger = build ()

                    let rival = {
                        budgetScope with
                            CounterpartyPeerId = "rival"
                    }

                    let elsewhere = {
                        budgetScope with
                            TemplateId = "overlap"
                    }

                    match run (ledger.ReserveBudget(spendOf "mine" 1m budgetScope, 1m)) with
                    | BudgetReserved(s, _) -> run (ledger.RecordSpend(s, SpendCommitted))
                    | outcome -> failtestf "expected a reservation, got %A" outcome

                    Expect.equal
                        (run (ledger.RemainingBudget(rival, 1m))).EpsilonCommitted
                        0m
                        "one counterparty's spend must not deplete another's budget"

                    Expect.equal
                        (run (ledger.RemainingBudget(elsewhere, 1m))).EpsilonCommitted
                        0m
                        "…nor one template's another's"

                    match run (ledger.ReserveBudget(spendOf "theirs" 1m rival, 1m)) with
                    | BudgetReserved _ -> ()
                    | outcome -> failtestf "the rival's own budget must be intact, got %A" outcome
                }
            ]

        test "an abandoned reservation is reclaimed once its TTL passes" {
            // The crash case: a process dies between reserving and
            // settling. Without the reclaim the epsilon is stranded and
            // the budget shrinks permanently for a query nobody answered.
            let clock = ref at

            let ledger =
                InMemoryPrivacyBudgetLedger(fun () -> clock.Value) :> IPrivacyBudgetLedger

            match run (ledger.ReserveBudget(spendOf "abandoned" 3m budgetScope, 3m)) with
            | BudgetReserved _ -> ()
            | outcome -> failtestf "expected a reservation, got %A" outcome

            // Still held while the TTL is live.
            clock.Value <- at.AddMinutes 5.0

            match run (ledger.ReserveBudget(spendOf "next" 1m budgetScope, 3m)) with
            | BudgetRefused(BudgetExhausted _) -> ()
            | outcome -> failtestf "an open reservation must still hold its epsilon, got %A" outcome

            // Past the TTL the hold is reclaimed by the next
            // reservation's own read-modify-write — no sweeper, no
            // scheduler dependency (GP 13).
            clock.Value <- at.AddMinutes 20.0

            match run (ledger.ReserveBudget(spendOf "later" 1m budgetScope, 3m)) with
            | BudgetReserved _ -> ()
            | outcome -> failtestf "the expired hold must be reclaimed, got %A" outcome
        }

        test "the blob ledger refuses a backend without conditional writes, at construction" {
            // The same posture BlobPeerReplayGuard takes, for the same
            // reason: a ledger that races reads as defended and is not,
            // and an operator should see that when they wire it rather
            // than at the first peer call.
            Expect.throwsT<ArgumentException>
                (fun () -> BlobPrivacyBudgetLedger(PlainBlobStorage()) |> ignore)
                "a non-conditional backend must be refused loudly"

            Expect.isNone
                (BlobPrivacyBudgetLedger.TryCreate(PlainBlobStorage()))
                "…and the probing form must decline rather than build a racy ledger"

            Expect.isSome
                (BlobPrivacyBudgetLedger.TryCreate(ProbeBlobStorage 1))
                "CONTROL — a conditional backend still builds, so the refusal is a check and not a blanket"
        }

        test "the no-op default never refuses and says plainly that it accounts nothing" {
            let ledger = NoPrivacyBudgetLedger() :> IPrivacyBudgetLedger

            for i in 1..50 do
                match run (ledger.ReserveBudget(spendOf $"n{i}" 10m budgetScope, 1m)) with
                | BudgetReserved(spend, _) -> run (ledger.RecordSpend(spend, SpendCommitted))
                | outcome -> failtestf "the no-op ledger must never refuse, got %A" outcome

            let audited = run (ledger.RemainingBudget(budgetScope, 1m))

            Expect.equal audited.EpsilonCommitted 0m "500 epsilon against a ceiling of 1 accumulated nothing"
            Expect.isFalse ledger.IsDurable "…and it does not claim to be durable"
        }
    ]

// ─── 3. Atomicity, measured — with the mechanism, and without it ─────

/// Fire `workers` concurrent one-unit reservations at `ledger` against
/// `ceiling`, committing each that is admitted, and report how many were.
let private admitUnderContention (ledger: IPrivacyBudgetLedger) (ceiling: decimal) (workers: int) =
    [ 1..workers ]
    |> List.map (fun i -> async {
        match! ledger.ReserveBudget(spendOf $"w{i}" 1m budgetScope, ceiling) with
        | BudgetReserved(spend, _) ->
            do! ledger.RecordSpend(spend, SpendCommitted)
            return 1
        | BudgetRefused _ -> return 0
    })
    |> Async.Parallel
    |> run
    |> Array.sum

let atomicityTests =
    testList "Phase 190 — concurrent queries cannot both spend the last unit" [

        test "the conditional-write ledger admits exactly the ceiling under forced contention" {
            // The barrier holds all eight readers until every one has
            // observed the same pre-write state — the exact window a
            // download-modify-upload loses budget through. A compare-and-
            // swap ledger survives it: the losing uploads fail their
            // precondition, retry against fresher state, and the ceiling
            // holds.
            let workers = 8
            let blobs = ProbeBlobStorage workers
            let ledger = BlobPrivacyBudgetLedger(blobs, (fun () -> at)) :> IPrivacyBudgetLedger

            let admitted = admitUnderContention ledger 3m workers

            Expect.equal admitted 3 "exactly the declared ceiling may be admitted, however the queries interleave"

            let audited = run (ledger.RemainingBudget(budgetScope, 3m))

            Expect.equal audited.EpsilonCommitted 3m "…and the stored accounting agrees with what was admitted"
        }

        test "CONTROL — the same ledger over a backend that ignores its precondition over-admits" {
            // The accounting mechanism removed and nothing else: the
            // same ledger, the same accounting decision — only the
            // backend's conditional write no longer conditions on
            // anything, and the losing interleave is PLACED rather than
            // raced for (the Phase 320 rendezvous). The first caller
            // reads the empty document and is parked at its write; the
            // second spends the whole ceiling inside that window; the
            // first then lands its stale write. If this admitted only
            // the ceiling, the case above would be measuring nothing.
            let blobs = RendezvousBlobStorage()
            let ledger = BlobPrivacyBudgetLedger(blobs, (fun () -> at)) :> IPrivacyBudgetLedger

            let admit (i: int) = async {
                match! ledger.ReserveBudget(spendOf $"w{i}" 1m budgetScope, 1m) with
                | BudgetReserved(spend, _) ->
                    do! ledger.RecordSpend(spend, SpendCommitted)
                    return 1
                | BudgetRefused _ -> return 0
            }

            // The first caller reads "nothing spent" and parks at its
            // write, holding a view that is about to go stale.
            let held = admit 1 |> Async.StartAsTask
            blobs.WaitUntilWriting 5_000

            // The second caller spends the last (and only) unit while
            // the first is parked.
            let second = run (admit 2)

            // Release the stale write and let the first caller finish.
            blobs.Release()

            if not (held.Wait 30_000) then
                failtest "the held caller never completed after release"

            Expect.equal second 1 "the second caller must spend the last unit while the first is parked"

            Expect.equal
                held.Result
                1
                "…and the released stale write must be admitted too — nothing conditioned on the view it read"

            Expect.equal
                (held.Result + second)
                2
                "a ceiling of 1 admitted exactly 2 — the placed lost update; if this were 1, the atomicity probe above proves nothing"

            let audited = run (ledger.RemainingBudget(budgetScope, 1m))

            Expect.equal
                audited.EpsilonCommitted
                1m
                "…and the book records only one of them: the stale write overwrote the second caller's settled spend, which is what a lost update is"
        }

        test "the in-memory ledger holds the ceiling under contention too" {
            // Its atomicity is a monitor rather than a compare-and-swap,
            // and the claim is the same, so it is measured the same way.
            let ledger = InMemoryPrivacyBudgetLedger(fun () -> at) :> IPrivacyBudgetLedger
            let admitted = admitUnderContention ledger 5m 32

            Expect.equal admitted 5 "32 concurrent queries against a ceiling of 5 admit exactly 5"
        }
    ]

// ─── 4. End to end, through the structural gate ──────────────────────

/// NOT `private`: `JsonRpcPeerHost.contract` reflects via
/// `FSharpType.IsRecord` without the private-representation flag.
type ReachContract = {
    EstimateReach: string -> Async<CohortResult>
}

let private cell label count : PrivacyCell = {
    Label = label
    Count = count
    Value = None
}

let private cleanRoomFloor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count ]
}

let private template: CleanRoomTemplate = {
    TemplateId = "reach"
    // `Absent` is deliberately on the surface and absent from the
    // contract, so a dispatch failure can be reached WITHOUT tripping
    // the surface invariant — that is the refund path.
    AllowedMethods = Set.ofList [ "EstimateReach"; "Absent" ]
    Floor = cleanRoomFloor
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

[<Literal>]
let private contractId = "example.reach"

let private callContext: PeerCallContext = {
    Peer = {
        PeerId = "buyer"
        DisplayName = "Buyer"
    }
    User = Anonymous
    ContractVersion = v1
    Route = [ "buyer" ]
    RootRequestId = "root-190"
    ParentRequestId = None
    HopsRemaining = 4
}

type private DecisionSink() =
    let rows = ResizeArray<PeerCleanRoomDecisionPayload>()
    member _.Rows = List.ofSeq rows

    member _.Sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { rows.Add payload }

/// A handler answering a cohort of 50 — comfortably above the k-floor,
/// so every refusal in this section is the BUDGET's and never the
/// floor's.
let private inFloorImpl: ReachContract = {
    EstimateReach =
        fun _ -> async {
            return {
                Shape = Count
                Cells = [ cell "all" 50 ]
            }
        }
}

/// A handler answering a cohort of 7 — below k, so the gate withholds on
/// the floor. Exercises the withhold-charge policy.
let private subFloorImpl: ReachContract = {
    EstimateReach =
        fun _ -> async {
            return {
                Shape = Count
                Cells = [ cell "all" 7 ]
            }
        }
}

let private registrationFor (impl: ReachContract) =
    (JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] None impl).Registration

let private meterFor (ledger: IPrivacyBudgetLedger) (policy: PrivacyBudgetPolicy) =
    PrivacyBudgetMeter.create ledger policy
    |> PrivacyBudgetMeter.withClock (fun () -> at)

let private ledgerAt () =
    InMemoryPrivacyBudgetLedger(fun () -> at) :> IPrivacyBudgetLedger

let private meteredGate (sink: DecisionSink) (meter: PrivacyBudgetMeter option) (impl: ReachContract) =
    (CleanRoomGate.wrapMetered (CleanRoomBroker.create ()) template None meter sink.Sink (registrationFor impl))
        .Registration

let private call (registration: PeerContractRegistration) (methodName: string) =
    registration.Dispatch callContext methodName "[\"any\"]" |> run

/// Run `n` identical in-floor queries; report how many reached the wire.
let private released (registration: PeerContractRegistration) n =
    [ 1..n ]
    |> List.sumBy (fun _ ->
        match call registration "EstimateReach" with
        | Ok _ -> 1
        | Error _ -> 0)

let gateTests =
    testList "Phase 190 — the gate refuses a series the floor would pass" [

        test "three in-floor queries release and the fourth is withheld, once the ceiling is spent" {
            let ledger = ledgerAt ()
            let policy = PrivacyBudgetPolicy.create 3m 1m
            let sink = DecisionSink()
            let registration = meteredGate sink (Some(meterFor ledger policy)) inFloorImpl

            Expect.equal (released registration 3) 3 "every in-budget query clears the floor and is released"

            match call registration "EstimateReach" with
            | Error(PeerCleanRoomWithheld id) ->
                Expect.equal id template.TemplateId "the refusal names the template and nothing quantitative"
            | Error e -> failtestf "expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "a query past the declared ceiling reached the wire: %s" payload

            let refusals = sink.Rows |> List.filter (fun r -> not r.Released)
            Expect.hasLength refusals 1 "exactly one decision was a withhold"

            Expect.stringContains
                (List.head refusals).Reason
                "privacy budget"
                "…and the receiver-side row says why, where the quantities are allowed to live"

            let audited =
                run (ledger.RemainingBudget(PrivacyBudgetPolicy.scopeFor "reach" "buyer" at policy, 3m))

            Expect.equal audited.EpsilonCommitted 3m "the audit reading matches the three answers that shipped"
        }

        test "CONTROL — the identical series with no meter composed releases every query" {
            // Without this half, the case above would pass equally
            // against a gate that had broken and started refusing
            // everything after three calls.
            let sink = DecisionSink()
            let registration = meteredGate sink None inFloorImpl

            Expect.equal
                (released registration 8)
                8
                "an unmetered gate is the pre-190 gate: the floor, and nothing cumulative"

            Expect.isEmpty
                (sink.Rows |> List.filter (fun r -> not r.Released))
                "…and nothing was withheld, so the refusals above were the budget's"
        }

        test "CONTROL — a metered gate with a ceiling above the series releases every query" {
            // The other direction: the meter is composed and does not
            // interfere until it binds, so the refusal above is
            // exhaustion and not the mere presence of a ledger.
            let registration =
                meteredGate
                    (DecisionSink())
                    (Some(meterFor (ledgerAt ()) (PrivacyBudgetPolicy.create 100m 1m)))
                    inFloorImpl

            Expect.equal (released registration 8) 8 "a composed budget that is not exhausted changes nothing"
        }

        test "the debit is durable BEFORE the answer, so no query is answered on credit" {
            // The ordering claim, measured from inside: the handler
            // observes the reservation already held.
            let ledger = ledgerAt ()
            let policy = PrivacyBudgetPolicy.create 3m 1m
            let scope = PrivacyBudgetPolicy.scopeFor "reach" "buyer" at policy
            let observed = ResizeArray<decimal>()

            let impl: ReachContract = {
                EstimateReach =
                    fun _ -> async {
                        let! reading = ledger.RemainingBudget(scope, 3m)
                        observed.Add reading.EpsilonReserved

                        return {
                            Shape = Count
                            Cells = [ cell "all" 50 ]
                        }
                    }
            }

            let registration = meteredGate (DecisionSink()) (Some(meterFor ledger policy)) impl

            match call registration "EstimateReach" with
            | Ok _ -> ()
            | Error e -> failtestf "the in-budget query must be released, got %A" e

            Expect.sequenceEqual
                observed
                [ 1m ]
                "the epsilon was held before the handler ran — a release that is not already debited is a free query"
        }

        test "a withheld answer is charged by default, and free only when the policy says so" {
            // A withhold discloses one bit ("below the floor"). Charging
            // it is what stops a counterparty binary-searching a cohort
            // size for nothing.
            let charged = ledgerAt ()
            let free = ledgerAt ()
            let chargedPolicy = PrivacyBudgetPolicy.create 3m 1m

            let freePolicy =
                chargedPolicy |> PrivacyBudgetPolicy.withWithholdCharge WithholdFree

            let scope = PrivacyBudgetPolicy.scopeFor "reach" "buyer" at chargedPolicy

            let chargedGate =
                meteredGate (DecisionSink()) (Some(meterFor charged chargedPolicy)) subFloorImpl

            let freeGate =
                meteredGate (DecisionSink()) (Some(meterFor free freePolicy)) subFloorImpl

            Expect.equal (released chargedGate 2) 0 "the sub-k answers are withheld by the floor, as before"
            Expect.equal (released freeGate 2) 0 "…under both policies"

            Expect.equal
                (run (charged.RemainingBudget(scope, 3m))).EpsilonCommitted
                2m
                "WithholdCharged bills the probe"

            Expect.equal
                (run (free.RemainingBudget(scope, 3m))).EpsilonCommitted
                0m
                "WithholdFree does not — the documented weakening, asserted"
        }

        test "a dispatch that produces no answer returns its reservation" {
            // Nothing about the protected data was disclosed, so nothing
            // is owed — the mirror of the ordering rule above, and what
            // stops a flaky handler eroding a budget nobody spent.
            let ledger = ledgerAt ()
            let policy = PrivacyBudgetPolicy.create 3m 1m
            let scope = PrivacyBudgetPolicy.scopeFor "reach" "buyer" at policy

            let registration =
                meteredGate (DecisionSink()) (Some(meterFor ledger policy)) inFloorImpl

            match call registration "Absent" with
            | Error(PeerMethodNotFound _) -> ()
            | outcome -> failtestf "expected the dispatch to fail on a method the contract lacks, got %A" outcome

            let audited = run (ledger.RemainingBudget(scope, 3m))
            Expect.equal audited.EpsilonCommitted 0m "a failed dispatch is not a disclosure"
            Expect.equal audited.EpsilonReserved 0m "…and its hold was returned, not stranded"
        }

        test "the ledger is never consulted on an unmetered gate (GP 13)" {
            // The zero-cost claim, asserted rather than assumed: a
            // composition that declares no budget must not reach a ledger
            // at all, even one that exists.
            let touched = ref 0

            let bump () =
                lock touched (fun () -> touched.Value <- touched.Value + 1)

            let counting =
                { new IPrivacyBudgetLedger with
                    member _.IsDurable = false

                    member _.ReserveBudget(spend, ceiling) = async {
                        bump ()
                        return BudgetReserved(spend, ceiling)
                    }

                    member _.RecordSpend(_, _) = async { bump () }

                    member _.RemainingBudget(scope, ceiling) = async {
                        bump ()

                        return {
                            Scope = scope
                            EpsilonCeiling = ceiling
                            EpsilonCommitted = 0m
                            EpsilonReserved = 0m
                            QueryCount = 0
                        }
                    }
                }

            let unmetered = meteredGate (DecisionSink()) None inFloorImpl
            Expect.equal (released unmetered 4) 4 "the unmetered gate releases"
            Expect.equal touched.Value 0 "…having read no ledger"

            // CONTROL — the same ledger IS read once a meter is composed,
            // so the zero above is the absence of a call and not a broken
            // double.
            let metered =
                meteredGate (DecisionSink()) (Some(meterFor counting (PrivacyBudgetPolicy.create 100m 1m))) inFloorImpl

            Expect.equal (released metered 1) 1 "the metered gate releases too"
            Expect.isGreaterThan touched.Value 0 "…and this time the ledger was consulted"
        }

        test "wrapApproved is wrapMetered with no meter — one gate, not two" {
            // The drift guard the file's own argument rests on: a second
            // implementation of "the gate" is how a path that enforces
            // slightly less appears.
            let sink = DecisionSink()

            let viaApproved =
                (CleanRoomGate.wrapApproved
                    (CleanRoomBroker.create ())
                    template
                    None
                    sink.Sink
                    (registrationFor inFloorImpl))
                    .Registration

            let viaMetered = meteredGate (DecisionSink()) None inFloorImpl

            Expect.equal (released viaApproved 3) (released viaMetered 3) "both routes release identically"
            Expect.equal viaApproved.ContractId viaMetered.ContractId "…over the same registration"
        }
    ]