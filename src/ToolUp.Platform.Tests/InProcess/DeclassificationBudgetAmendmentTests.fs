// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.DeclassificationBudgetAmendmentTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Facts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 679 — the co-signed budget amendment ──────────────────────
//
// Phase 675 made a declassification ceiling enforceable. It said nothing
// about how a ceiling may be RELIEVED, and in the absence of a mechanism
// the practical answer is that one party edits the declaration and
// redeploys — a change to what a deployment may disclose, made
// unilaterally, which is exactly the act the countersignature exists to
// prevent. This pack asserts the mechanism that closes it.
//
// Six properties, each of which was false before this phase:
//
//   1. **The signature binds to the exact delta.** Two amendments that
//      differ only in how much they move the ceiling are different
//      subjects, so an approval of one is worth nothing for the other.
//      Measured on the hash, and again through a live registry.
//   2. **An un-countersigned amendment has NO EFFECT.** Declared, sitting
//      in the config, approved by one of two parties — the ceiling does
//      not move and the crossing stays denied.
//   3. **Exhaustion → amendment → resumption is one audited arc**, driven
//      end to end through the real gate, with the trail carrying the
//      subject hash and the ceiling on both sides of the change.
//   4. **A retroactive breach is unrepresentable.** A lowering below
//      spend the ledger has already recorded is refused and audited; a
//      lowering that stays above it applies.
//   5. **A chain composes in one order.** An amendment naming a baseline
//      the budget has left is inert and named, never re-based onto a
//      number its signatories never saw.
//   6. **A deployment with no amendment is byte-for-byte pre-679** (GP 11
//      / GP 13) — same verdicts, same audit rows, and the registry is not
//      consulted at all, which is measured with a counting decorator
//      rather than assumed.
//
// The non-vacuity risk here is the one every "the gate refused" pack
// has: a case can pass because nothing ever disclosed. Every arc
// therefore asserts the DISCLOSED checks either side of a refusal, so a
// mechanism that applied no amendment fails on the resumption and one
// that applied every amendment fails on the refusal.

// ── Real key material ─────────────────────────────────────────────
//
// Genuine P-256 keys, as Phase 676's own pack uses. A stub signer
// returning "ok" would let every case below pass against a registry
// checking nothing cryptographic at all, and the claim under test is
// that a ceiling moves only under a real signature.

let private partyA = "party-alpha"
let private partyB = "party-beta"

type private TestSigner(parties: string list) =
    let keys =
        parties
        |> List.map (fun partyId -> partyId, ECDsa.Create(ECCurve.NamedCurves.nistP256))
        |> dict

    interface ICountersignatureSigner with
        member _.Sign(partyId, message) = async {
            match keys.TryGetValue partyId with
            | true, ec -> return Ok(Convert.ToBase64String(ec.SignData(message, HashAlgorithmName.SHA256)))
            | false, _ -> return Error $"no signing key for party '{partyId}'"
        }

        member _.Verify(partyId, message, signature) = async {
            match keys.TryGetValue partyId with
            | false, _ -> return Error $"no public key for party '{partyId}'"
            | true, ec ->
                try
                    if ec.VerifyData(message, Convert.FromBase64String signature, HashAlgorithmName.SHA256) then
                        return Ok()
                    else
                        return Error "signature did not verify"
                with _ ->
                    return Error "signature is not well formed"
        }

/// A registry decorator that counts `Status` calls. The GP 13 claim is
/// that an unamended budget consults NOTHING, and "the verdicts came out
/// the same" does not measure that — a registry read that happened and
/// changed nothing looks identical from the outside.
type private CountingRegistry(inner: ICountersignatureRegistry) =
    let mutable statusCalls = 0

    member _.StatusCalls = statusCalls

    interface ICountersignatureRegistry with
        member _.Issue request = inner.Issue request
        member _.Accept record = inner.Accept record
        member _.Records subject = inner.Records subject

        member _.Status(parties, subject, asOf) =
            statusCalls <- statusCalls + 1
            inner.Status(parties, subject, asOf)

// ── Vocabulary ────────────────────────────────────────────────────

let private aggregateOp = "aggregate-over-k"
let private otherOp = "index-formula"
let private policyA = "party-a-licensed"
let private unscoped = DeclassificationBudget.UnscopedParty
let private aggregateTemplate = DeclassificationBudget.templateIdFor aggregateOp

let private roster = [ partyA; partyB ]

/// Frozen — every epoch boundary, reservation TTL and countersignature
/// validity window in this pack is crossed by moving this, never by
/// waiting.
let private frozenNow = DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero)

/// Comfortably inside the frozen clock, so a record issued at the real
/// wall clock (`Issue` stamps `IssuedAt` from `UtcNow`, which is not
/// injectable) is nonetheless IN FORCE at `frozenNow`.
let private inForceFrom = frozenNow.AddDays -1.0

let private amendment (templateId: string) (party: string) (prior: decimal) (delta: decimal) : BudgetAmendment = {
    TemplateId = templateId
    PartyId = party
    PriorCeiling = prior
    CeilingDelta = delta
}

let private freshRegistry () =
    BlobCountersignatureRegistry(InMemoryBlobStorage() :> IBlobStorage, TestSigner roster) :> ICountersignatureRegistry

let private request (acting: string) (subject: CountersignatureSubject) (action: CountersignatureAction) = {
    Subject = subject
    Roster = roster
    ActingPartyId = acting
    Action = action
    NotBefore = Some inForceFrom
    ExpiresAt = None
}

/// Put one party's record on the registry, failing loudly rather than
/// letting a refused issue read as an un-countersigned amendment.
let private issue (registry: ICountersignatureRegistry) acting subject action = async {
    match! registry.Issue(request acting subject action) with
    | Ok _ -> ()
    | Error e -> failtestf "the registry refused to issue a record: %A" e
}

/// Both parties approve the amendment — the completed ceremony.
let private countersign (registry: ICountersignatureRegistry) (a: BudgetAmendment) = async {
    let subject = BudgetAmendment.subject a
    do! issue registry partyA subject SubjectApproved
    do! issue registry partyB subject SubjectApproved
}

// ── Store + fact builders (the Phase 675 shapes) ──────────────────

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

/// One restricted contribution plus a declassifying aggregate over it —
/// the single-party shape whose budget is accounted under `_unscoped`.
let private seed (store: IFactStore) (scope: string) : Fact =
    assertFact store scope (draftOf "raw-a" (Computed("load", "1", "p")) (Series "dov-a") (Restricted policyA) [])
    |> ignore

    assertFact store scope (draftOf "agg" (Computed(aggregateOp, "1", "p")) (Scalar 42m) Surfaceable [ "dov-a" ])

let private taintCfg =
    DisclosureTaintConfig.ofLists [
        {
            PolicyRef = policyA
            Mode = TaintPropagating
            PermitSurfaces = [ FactRetrieval; FactExport ]
            ContributorScope = None
        }
    ] [
        {
            OperationId = aggregateOp
            Rationale = "aggregation over >=5 members loses individual attribution"
            AcceptingScopes = []
        }
    ]

let private budgetConfig (ledger: IPrivacyBudgetLedger) (ceiling: int) =
    DeclassificationBudgetConfig.create ledger [ DeclassificationBudget.countedCrossings aggregateOp ceiling ]
    |> DeclassificationBudgetConfig.withClock (fun () -> frozenNow)

let private rowsOf (events: IEventStore) (scope: string) (eventType: string) = async {
    let! rows = events.ReadBySource(scope, FactEvents.SourceModule)
    return rows |> List.filter (fun e -> e.EventType = eventType)
}

let private newLedger () =
    InMemoryPrivacyBudgetLedger(fun () -> frozenNow) :> IPrivacyBudgetLedger

// ── 679.A — the subject binds to the exact delta ──────────────────

let subjectTests =
    testList "Phase 679 amendment subject" [

        test "two amendments differing only in the delta are different subjects" {
            let plusFive = amendment aggregateTemplate unscoped 500m 5m
            let plusFiveThousand = amendment aggregateTemplate unscoped 500m 5000m

            let a = BudgetAmendment.subject plusFive
            let b = BudgetAmendment.subject plusFiveThousand

            Expect.equal a.SubjectId b.SubjectId "the same (budget, party) pair is one amendment chain"

            Expect.notEqual
                a.ContentHash
                b.ContentHash
                "an approval of one delta must carry nothing for another — the delta is inside the signed bytes"
        }

        test "an amendment agreed against a different baseline is a different subject" {
            let againstFiveHundred =
                BudgetAmendment.subject (amendment aggregateTemplate unscoped 500m 5m)

            let againstOneThousand =
                BudgetAmendment.subject (amendment aggregateTemplate unscoped 1000m 5m)

            Expect.notEqual
                againstFiveHundred.ContentHash
                againstOneThousand.ContentHash
                "the baseline is signed in, so a delta cannot be silently re-based onto a ceiling that has moved"
        }

        test "an amendment to one party is not an amendment to another" {
            let toA = BudgetAmendment.subject (amendment aggregateTemplate partyA 500m 5m)
            let toB = BudgetAmendment.subject (amendment aggregateTemplate partyB 500m 5m)

            Expect.notEqual toA.SubjectId toB.SubjectId "budgets are per party, so amendment chains are too"
            Expect.notEqual toA.ContentHash toB.ContentHash "and neither approval is evidence about the other"
        }

        test "the subject id is a well-formed identifier whatever the template and party contain" {
            // The real grounding-tier template id carries a colon, and a
            // party id is whatever the agreement calls a party. Both
            // reach a blob name through the registry's store, so neither
            // may be folded into the path raw.
            let subject =
                BudgetAmendment.subject (amendment "declassify:aggregate/over k" "party:one two" 5m 1m)

            Expect.stringHasLength subject.SubjectId 64 "a SHA-256 hex digest"

            Expect.isTrue
                (subject.SubjectId
                 |> Seq.forall (fun ch -> Char.IsDigit ch || (ch >= 'a' && ch <= 'f')))
                "lowercase hex only — always a legal blob path segment"
        }

        test "a decimal's scale does not change the agreement" {
            // `500m` and `500.00m` are equal under every comparison this
            // substrate makes, and .NET's `ToString` preserves the
            // difference. Two parties on differently-authored configs
            // must sign the same bytes.
            let plain = BudgetAmendment.subject (amendment aggregateTemplate unscoped 500m 5m)

            let scaled =
                BudgetAmendment.subject (amendment aggregateTemplate unscoped 500.00m 5.0m)

            Expect.equal plain.ContentHash scaled.ContentHash "the canonical encoding normalises decimal scale"
        }

        test "an amendment that moves the ceiling by nothing is refused" {
            let errors = BudgetAmendment.validate (amendment aggregateTemplate unscoped 500m 0m)

            Expect.isNonEmpty
                errors
                "a signature ceremony over a no-op would put a row in the trail asserting a change that did not happen"
        }

        test "an amendment that would seal the routine is refused" {
            let errors = BudgetAmendment.validate (amendment aggregateTemplate unscoped 5m -5m)

            Expect.isNonEmpty
                errors
                "a ceiling at or below zero admits nothing and is a sealed routine expressed by accident"
        }

        test "an amendment naming no party is refused" {
            let errors = BudgetAmendment.validate (amendment aggregateTemplate "" 5m 1m)

            Expect.isNonEmpty
                errors
                "budgets are accounted per party, so an amendment naming none would move an allowance nobody holds"
        }

        test "a healthy amendment validates" {
            Expect.equal (BudgetAmendment.validate (amendment aggregateTemplate unscoped 500m 500m)) [] "the control"
        }

        test "an empty roster is refused rather than accepted and silently inert" {
            match
                DeclassificationAmendmentConfig.tryCreate (freshRegistry ()) [] [
                    amendment aggregateTemplate unscoped 2m 2m
                ]
            with
            | Ok _ ->
                failtest
                    "an empty roster is never countersigned, so every amendment under it would be inert — and would read at the composition site exactly like one that works"
            | Error errors -> Expect.isNonEmpty errors "the refusal is reported"
        }

        test "a duplicate amendment is refused rather than chained twice" {
            let a = amendment aggregateTemplate unscoped 2m 2m

            match DeclassificationAmendmentConfig.tryCreate (freshRegistry ()) roster [ a; a ] with
            | Ok _ ->
                failtest "a duplicate can never chain, and its presence suggests an author expected it to apply twice"
            | Error errors -> Expect.isNonEmpty errors "the refusal is reported"
        }

        test "an unenforceable declaration raises at compose rather than at the first crossing" {
            Expect.throws
                (fun () ->
                    DeclassificationAmendmentConfig.create (freshRegistry ()) roster [
                        amendment aggregateTemplate unscoped 5m 0m
                    ]
                    |> ignore)
                "a control that fails late fails after something has already been disclosed"
        }
    ]

// ── 679.B — the ceiling moves only under a live countersignature ──

let applicationTests =
    testList "Phase 679 amendment application" [

        testCaseAsync "an amendment with no countersignature at all leaves the ceiling where it was"
        <| async {
            let registry = freshRegistry ()
            let raise' = amendment aggregateTemplate unscoped 2m 2m

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ raise' ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 2m "declared, unmoved"

            match resolved.Resolutions |> List.map _.Outcome with
            | [ AmendmentNotCountersigned _ ] -> ()
            | other -> failtestf "expected the amendment to be reported as awaiting agreement, got %A" other
        }

        testCaseAsync "one party's approval is not the roster's"
        <| async {
            let registry = freshRegistry ()
            let raise' = amendment aggregateTemplate unscoped 2m 2m
            do! issue registry partyA (BudgetAmendment.subject raise') SubjectApproved

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ raise' ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal
                resolved.Ceiling
                2m
                "nothing about what the deployment may disclose changes without EVERY party's signature"

            match resolved.Resolutions |> List.map _.Outcome with
            | [ AmendmentNotCountersigned(CountersignaturePending awaiting) ] ->
                Expect.equal awaiting [ partyB ] "and the row names who is missing"
            | other -> failtestf "expected a pending verdict naming party-beta, got %A" other
        }

        testCaseAsync "the full roster's approval over the exact delta moves the ceiling"
        <| async {
            let registry = freshRegistry ()
            let raise' = amendment aggregateTemplate unscoped 2m 2m
            do! countersign registry raise'

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ raise' ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 4m "the countersigned delta is in force"

            match resolved.Resolutions |> List.map _.Outcome with
            | [ AmendmentApplied _ ] -> ()
            | other -> failtestf "expected the amendment to be applied, got %A" other
        }

        testCaseAsync "approvals gathered for one delta do not carry to another"
        <| async {
            let registry = freshRegistry ()
            do! countersign registry (amendment aggregateTemplate unscoped 2m 2m)

            // The SAME chain, the SAME baseline, a bigger ask. Every
            // approval on the registry names the other one.
            let bigger = amendment aggregateTemplate unscoped 2m 2000m

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ bigger ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 2m "an edit to the delta is structurally unapproved"
        }

        testCaseAsync "a revocation by any party takes the ceiling back on the next resolution"
        <| async {
            let registry = freshRegistry ()
            let raise' = amendment aggregateTemplate unscoped 2m 2m
            do! countersign registry raise'

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ raise' ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value

            let! before = DeclassificationAmendments.resolve config budget unscoped frozenNow
            Expect.equal before.Ceiling 4m "in force before the withdrawal — the control"

            do! issue registry partyB (BudgetAmendment.subject raise') SubjectRevoked

            let! after = DeclassificationAmendments.resolve config budget unscoped frozenNow
            Expect.equal after.Ceiling 2m "an amendment applies only WHILE its countersignature is live"

            match after.Resolutions |> List.map _.Outcome with
            | [ AmendmentNotCountersigned(CountersignatureRevoked(party, _)) ] ->
                Expect.equal party partyB "the row names who withdrew"
            | other -> failtestf "expected a revoked verdict, got %A" other
        }

        testCaseAsync "an amendment naming a baseline the budget has left is inert and named"
        <| async {
            let registry = freshRegistry ()
            // Agreed against 500 — a ceiling this budget never had.
            let stale = amendment aggregateTemplate unscoped 500m 100m
            do! countersign registry stale

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ stale ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 2m "a delta re-based onto a different baseline is a change nobody signed"

            match resolved.Resolutions |> List.map _.Outcome with
            | [ AmendmentBaselineMoved inForce ] -> Expect.equal inForce 2m "and the row says what IS in force"
            | other -> failtestf "expected a baseline-moved verdict, got %A" other
        }

        testCaseAsync "a chain of amendments composes in declaration order"
        <| async {
            let registry = freshRegistry ()
            let first = amendment aggregateTemplate unscoped 2m 2m
            let second = amendment aggregateTemplate unscoped 4m 6m
            do! countersign registry first
            do! countersign registry second

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ first; second ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 10m "2 → 4 → 10, each step a compare-and-set against the one before"

            Expect.equal
                (resolved.Resolutions |> List.map (fun r -> r.CeilingBefore, r.CeilingAfter))
                [ 2m, 4m; 4m, 10m ]
                "and the trail carries the ceiling on both sides of each step"
        }

        testCaseAsync "an amendment to one party does not move another party's ceiling"
        <| async {
            let registry = freshRegistry ()
            let toUnscoped = amendment aggregateTemplate unscoped 2m 8m
            do! countersign registry toUnscoped

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ toUnscoped ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value

            let! amended = DeclassificationAmendments.resolve config budget unscoped frozenNow
            Expect.equal amended.Ceiling 10m "the named party's allowance moved — the control"

            let! other = DeclassificationAmendments.resolve config budget partyA frozenNow
            Expect.equal other.Ceiling 2m "raising one party's allowance never raises another's"
            Expect.isEmpty other.Resolutions "and no amendment was even considered for it"
        }

        testCaseAsync "an amendment naming another routine leaves this one alone"
        <| async {
            let registry = freshRegistry ()

            let elsewhere =
                amendment (DeclassificationBudget.templateIdFor otherOp) unscoped 2m 8m

            do! countersign registry elsewhere

            let config =
                budgetConfig (newLedger ()) 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ elsewhere ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 2m "a budget is amended by its own template id, never another's"
        }
    ]

// ── 679.A — a retroactive breach is unrepresentable ───────────────

let loweringTests =
    testList "Phase 679 amendment lowering" [

        testCaseAsync "a lowering below spend already recorded is refused"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let ledger = newLedger ()
            let registry = freshRegistry ()

            // Spend four of a declared four, through the real gate, so
            // the recorded spend under test is real accounting rather
            // than a number written into a stub.
            let spendConfig = budgetConfig ledger 4

            let gate =
                FactDisclosureGate.createConfiguredWithBudgets (Some taintCfg) None (Some spendConfig) store events

            let agg = seed store scope

            for _ in 1..4 do
                let! _ = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])
                ()

            let lower = amendment aggregateTemplate unscoped 4m -2m
            do! countersign registry lower

            let config =
                budgetConfig ledger 4
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ lower ]
                    |> DeclassificationAmendmentConfig.withAudit (EventStoreAmendmentAudit(events, scope))
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal
                resolved.Ceiling
                4m
                "a ceiling below spend already recorded would declare a breach for disclosures that were permitted when they happened"

            match resolved.Resolutions |> List.map _.Outcome with
            | [ AmendmentBelowRecordedSpend(proposed, recorded) ] ->
                Expect.equal proposed 2m "the row names what was asked for"
                Expect.equal recorded 4m "and what has already been spent"
            | other -> failtestf "expected a below-recorded-spend refusal, got %A" other

            let! refusals = rowsOf events scope DeclassificationBudgetEvents.AmendmentRefusedType
            Expect.equal refusals.Length 1 "the refusal is audited — it is not silent"

            let refusedHash = (BudgetAmendment.subject lower).ContentHash

            Expect.isTrue
                (refusals.Head.Payload.Contains refusedHash)
                "and the row carries the subject hash the roster signed"
        }

        testCaseAsync "a lowering that stays above recorded spend applies"
        <| async {
            let store, _ = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let ledger = newLedger ()
            let registry = freshRegistry ()

            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let spendConfig = budgetConfig ledger 4

            let gate =
                FactDisclosureGate.createConfiguredWithBudgets (Some taintCfg) None (Some spendConfig) store events

            let agg = seed store scope

            // One crossing spent of four.
            let! _ = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

            let lower = amendment aggregateTemplate unscoped 4m -2m
            do! countersign registry lower

            let config =
                budgetConfig ledger 4
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ lower ]
                )

            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 2m "tightening above what has been spent is a legitimate, countersigned act"
        }
    ]

// ── 679.C — exhaustion → amendment → resumption, end to end ───────

let arcTests =
    testList "Phase 679 exhaustion-amendment-resumption arc" [

        testCaseAsync "the whole arc runs through the real gate and is audited end to end"
        <| async {
            let store, events = newStore ()
            let scope = "team-" + Guid.NewGuid().ToString("N")
            let ledger = newLedger ()
            let registry = freshRegistry ()
            let raise' = amendment aggregateTemplate unscoped 2m 2m

            let config =
                budgetConfig ledger 2
                |> DeclassificationBudgetConfig.withAmendments (
                    DeclassificationAmendmentConfig.create registry roster [ raise' ]
                    |> DeclassificationAmendmentConfig.withAudit (EventStoreAmendmentAudit(events, scope))
                )

            let gate =
                FactDisclosureGate.createConfiguredWithBudgets (Some taintCfg) None (Some config) store events

            let agg = seed store scope

            // ── Exhaustion. Two crossings are affordable; the third is
            // not, and the amendment sitting un-countersigned in the
            // config changes nothing.
            for i in 1..2 do
                let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

                Expect.equal
                    (verdicts.TryFind agg.FactId)
                    (Some FactDisclosable)
                    (sprintf "crossing %d is within the declared ceiling of 2" i)

            let! exhausted = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

            Expect.equal
                (exhausted.TryFind agg.FactId)
                (Some(FactNotDisclosable(DeclassificationBudget.ExhaustedPrefix + aggregateOp)))
                "a declared amendment that nobody has signed relieves nothing"

            // ── Amendment. Both parties approve the exact delta.
            do! countersign registry raise'

            // ── Resumption.
            for i in 3..4 do
                let! verdicts = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

                Expect.equal
                    (verdicts.TryFind agg.FactId)
                    (Some FactDisclosable)
                    (sprintf "crossing %d is within the amended ceiling of 4" i)

            // ── And the amended ceiling binds in its turn. Without this
            // the case would pass against a mechanism that removed the
            // ceiling rather than raising it.
            let! exhaustedAgain = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])

            Expect.equal
                (exhaustedAgain.TryFind agg.FactId)
                (Some(FactNotDisclosable(DeclassificationBudget.ExhaustedPrefix + aggregateOp)))
                "the amended ceiling is a ceiling, not an exemption"

            // ── The trail. The effective history of the budget is
            // reconstructable from these rows plus the declaration.
            let! applied = rowsOf events scope DeclassificationBudgetEvents.AmendedType

            Expect.isNonEmpty applied "every application is audited"

            let expectedHash = (BudgetAmendment.subject raise').ContentHash

            Expect.isTrue
                (applied |> List.forall (fun row -> row.Payload.Contains expectedHash))
                "each row carries the subject hash, joining it to the countersignature records"

            Expect.isTrue
                (applied
                 |> List.forall (fun row -> row.Payload.Contains "\"Outcome\":\"Applied\""))
                "under the stable outcome name"

            // The rows say what the ceiling was on both sides, which is
            // what makes the effective history derivable.
            Expect.isTrue (applied |> List.forall (fun row -> row.Payload.Contains "\"CeilingBefore\":2")) "from 2"

            Expect.isTrue (applied |> List.forall (fun row -> row.Payload.Contains "\"CeilingAfter\":4")) "to 4"

            // Nothing was recorded before the parties agreed: the two
            // pre-amendment crossings and the exhausted one wrote no
            // amendment row at all.
            let! refusals = rowsOf events scope DeclassificationBudgetEvents.AmendmentRefusedType
            Expect.isEmpty refusals "an amendment merely awaiting a signature is the resting state, not an event"
        }
    ]

// ── GP 11 / GP 13 — the unamended deployment pays nothing ─────────

let costTests =
    testList "Phase 679 cost when unused" [

        testCaseAsync "a budget with no amendment facet behaves exactly as it did before"
        <| async {
            /// Drive the same five checks and return the verdicts plus the
            /// (type, payload) of every audit row, so a difference in
            /// EITHER shows up.
            let run (withAmendments: bool) = async {
                let store, events = newStore ()
                let scope = "team-fixed"
                let registry = CountingRegistry(freshRegistry ())

                let baseConfig = budgetConfig (newLedger ()) 2

                let config =
                    if withAmendments then
                        baseConfig
                        |> DeclassificationBudgetConfig.withAmendments (
                            // Declared, countersigned, and about a
                            // DIFFERENT routine — so the facet is armed
                            // and this budget is still untouched.
                            DeclassificationAmendmentConfig.create registry roster [
                                amendment (DeclassificationBudget.templateIdFor otherOp) unscoped 2m 8m
                            ]
                        )
                    else
                        baseConfig

                let gate =
                    FactDisclosureGate.createConfiguredWithBudgets (Some taintCfg) None (Some config) store events

                let agg = seed store scope

                let mutable verdicts = []

                for _ in 1..5 do
                    let! result = gate.Check(scope, "user-1", FactRetrieval, [ agg.FactId ])
                    verdicts <- (result.TryFind agg.FactId) :: verdicts

                let! rows = events.ReadBySource(scope, FactEvents.SourceModule)

                // The seeding rows carry a wall-clock `AsOf` and are not
                // what this comparison is about; every disclosure and
                // amendment payload is timestamp-free by design, so those
                // compare byte for byte.
                let comparable =
                    rows
                    |> List.filter (fun e ->
                        e.EventType <> FactEvents.AssertedType
                        && e.EventType <> FactEvents.SupersededType)
                    |> List.map (fun e -> e.EventType, e.Payload)
                    |> List.sort

                return List.rev verdicts, comparable, registry.StatusCalls
            }

            let! plainVerdicts, plainRows, _ = run false
            let! armedVerdicts, armedRows, statusCalls = run true

            Expect.equal armedVerdicts plainVerdicts "same verdicts (GP 11)"
            Expect.equal armedRows plainRows "same audit rows, payload for payload (GP 11)"

            Expect.isNonEmpty armedRows "and the comparison is between two real stories, not two empty ones"

            // The claim the two lines above cannot make: that nothing was
            // consulted. A registry read that happened and changed
            // nothing looks identical from the outside.
            Expect.equal statusCalls 0 "a crossing with no amendment declared for its pair consults no registry (GP 13)"

            // Non-vacuity: the run has to have exercised the budget, or
            // the equality above is between two empty stories.
            Expect.equal
                (armedVerdicts |> List.filter (fun v -> v = Some FactDisclosable) |> List.length)
                2
                "two crossings disclosed"

            Expect.equal
                (armedVerdicts
                 |> List.filter (fun v ->
                     v = Some(FactNotDisclosable(DeclassificationBudget.ExhaustedPrefix + aggregateOp)))
                 |> List.length)
                3
                "and three were refused on the ceiling"
        }

        testCaseAsync "resolving a budget with no amendment config consults nothing and returns the declaration"
        <| async {
            let config = budgetConfig (newLedger ()) 7
            let budget = (DeclassificationBudgetConfig.budgetFor config aggregateOp).Value
            let! resolved = DeclassificationAmendments.resolve config budget unscoped frozenNow

            Expect.equal resolved.Ceiling 7m "the declared ceiling"
            Expect.isEmpty resolved.Resolutions "and nothing was resolved"
        }
    ]