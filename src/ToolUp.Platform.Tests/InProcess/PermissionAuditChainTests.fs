module ToolUp.Platform.Tests.InProcess.PermissionAuditChainTests

open System
open System.Collections.Concurrent
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── Phase 553.A/D — the in-store permission-event hash chain ────────
//
// The acceptance this pack is written against, clause by clause:
//
//   * "Deleting or mutating any permission audit record in a store makes
//     the verifier report the break AT THE RIGHT INDEX" — four cases
//     below, one per removal/edit shape, each asserting the POSITION and
//     not merely that something was reported. A verifier that says "this
//     chain is broken somewhere" is not much use to an auditor holding
//     ten thousand rows.
//   * "A deployment that never exports sees no behavioural change beyond
//     the additive chain fields" — the pre-chain boundary cases: a
//     record persisted before the field existed decodes to `Chain =
//     None` and verifies as an UNCHAINED PREFIX rather than as a break.
//   * The Phase 9t interaction — an unreadable head refuses under
//     `RefuseAction` and records unchained (never guessing a
//     predecessor) under `LogAndContinue`.
//
// **Deliberately NOT here: the scoped counterparty export.** 553.B/553.C
// re-base on Phase 677, whose `ScopedLedgerExport` / `verifyExport` ship
// the export, the digest-plus-facet witnesses for elided spans, the DSSE
// wrapping and the round-trip-to-independent-verify test. Those clauses
// of 553.D are covered by `ChainedAuditLedgerTests` — "a party's export
// discloses its own records and withholds every other party's", "an
// in-scope record downgraded to a withheld witness is detected", "a
// scoped export round-trips through the stock DSSE path". Writing a
// second export and a second verifier here is exactly the outcome 677.D
// existed to prevent, so this pack covers the substrate 677 does not
// reach: the events as they sit in a deployment's own `IEventStore`.

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Minimal recording `IEventStore`. Reads are returned in write order;
/// the chain is deliberately order-independent, and one case below
/// proves that by reversing them.
type private RecordingEventStore() =
    let written = ResizeArray<ModuleEvent>()

    member _.Events = written |> List.ofSeq

    interface IEventStore with
        member _.Write(evt) = async { written.Add evt }
        member _.ReadAll(scopeId) = async { return written |> Seq.filter (fun e -> e.ScopeId = scopeId) |> List.ofSeq }

        member _.ReadByType(scopeId, eventType) = async {
            return
                written
                |> Seq.filter (fun e -> e.ScopeId = scopeId && e.EventType = eventType)
                |> List.ofSeq
        }

        member _.ReadBySource(scopeId, sourceModule) = async {
            return
                written
                |> Seq.filter (fun e -> e.ScopeId = scopeId && e.SourceModule = sourceModule)
                |> List.ofSeq
        }

        member _.ListScopes() = async { return written |> Seq.map _.ScopeId |> Seq.distinct |> List.ofSeq }

        member _.Erase(_scopeId, _subjectUserId, _policy, _dryRun) = async {
            return Ok(Unchecked.defaultof<ErasureSummary>)
        }

/// `IEventStore` whose READS throw — the head-unreadable double. Writes
/// succeed, which is the whole point: the audit row can still land, and
/// what is in question is only whether it lands chained.
type private ReadFaultingEventStore() =
    let written = ResizeArray<ModuleEvent>()
    member _.Events = written |> List.ofSeq

    interface IEventStore with
        member _.Write(evt) = async { written.Add evt }
        member _.ReadAll(_scopeId) = async { return failwith "simulated event-store read failure" }
        member _.ReadByType(_scopeId, _eventType) = async { return failwith "simulated event-store read failure" }
        member _.ReadBySource(_scopeId, _sourceModule) = async { return failwith "simulated read failure" }
        member _.ListScopes() = async { return [] }

        member _.Erase(_scopeId, _subjectUserId, _policy, _dryRun) = async {
            return Ok(Unchecked.defaultof<ErasureSummary>)
        }

type private CapturingMetricsSink() =
    let increments = ConcurrentBag<string * Map<string, string>>()
    member _.Increments = increments |> List.ofSeq

    interface IMetricsSink with
        member _.Record(_name, _value, _tags) = ()
        member _.Increment(name, tags) = increments.Add(name, tags)
        member _.SetGauge(_name, _value, _tags) = ()

let private scope = "team-acme"

let private grant (affected: string) (permissions: string) = {
    UserId = "admin"
    TeamId = "acme"
    AffectedUserId = affected
    ModuleName = "reports"
    Permissions = permissions
    Chain = None
}

/// Write `n` permission changes through the real audit log into a fresh
/// store, and hand back both so a case can tamper with what landed.
let private recordGrants (grants: PermissionChangedPayload list) =
    let store = RecordingEventStore()
    let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog

    for g in grants do
        auditLog.Record(scope, PermissionChanged g) |> Async.RunSynchronously

    store, auditLog

/// Read the chain back the way an auditor does — through the query
/// seam, not from the writer's in-memory state.
let private readChain (auditLog: IAuditLog) =
    auditLog.GetAuditTrail(scope, None, Some PermissionAuditChain.chainedEventType)
    |> Async.RunSynchronously
    |> PermissionAuditChain.permissionPayloads

let private expectBreak (report: PermissionAuditChain.PermissionChainReport) =
    match report with
    | PermissionAuditChain.ChainBrokenAt b -> b
    | PermissionAuditChain.ChainIntact summary ->
        failtestf "expected a break, got an intact chain of %d record(s)" summary.ChainedCount

let private expectIntact (report: PermissionAuditChain.PermissionChainReport) =
    match report with
    | PermissionAuditChain.ChainIntact summary -> summary
    | PermissionAuditChain.ChainBrokenAt b ->
        failtestf "expected an intact chain, got %A at %d: %s" b.Kind b.Position b.Detail

[<Tests>]
let tests =
    testList "Phase 553 — permission audit chain" [

        // ── The chain forms at all ──────────────────────────────────

        test "recorded permission events chain in order, genesis first" {
            let _, auditLog =
                recordGrants [ grant "alice" "Read"; grant "bob" "Write"; grant "carol" "Admin" ]

            let payloads = readChain auditLog

            Expect.equal payloads.Length 3 "three permission records"

            let summary = expectIntact (PermissionAuditChain.verify scope payloads)
            Expect.equal summary.ChainedCount 3 "all three are chained"
            Expect.equal summary.UnchainedCount 0 "none is unchained"
            Expect.isSome summary.Head "the walk reached a head"

            let genesisRecords =
                payloads
                |> List.filter (fun p ->
                    p.Chain
                    |> Option.exists (fun c -> c.PrevHash = PermissionAuditChain.genesisHash))

            Expect.equal genesisRecords.Length 1 "exactly one record claims the genesis predecessor"
        }

        test "verification is independent of the order the store returns records in" {
            let _, auditLog =
                recordGrants [ grant "alice" "Read"; grant "bob" "Write"; grant "carol" "Admin" ]

            let payloads = readChain auditLog

            let forwards = expectIntact (PermissionAuditChain.verify scope payloads)
            let backwards = expectIntact (PermissionAuditChain.verify scope (List.rev payloads))

            Expect.equal backwards.Head forwards.Head "the same head is derived from the reversed set"
            Expect.equal backwards.ChainedCount forwards.ChainedCount "the same count"
        }

        test "a record's hash commits to its predecessor, not only to its own fields" {
            let payload = grant "alice" "Read"

            let underGenesis =
                PermissionAuditChain.contentHash scope PermissionAuditChain.genesisHash payload

            let underOther =
                PermissionAuditChain.contentHash scope (String.replicate 64 "a") payload

            Expect.notEqual underOther underGenesis "the same content under a different predecessor hashes differently"

            Expect.equal
                (PermissionAuditChain.contentHash scope PermissionAuditChain.genesisHash payload)
                underGenesis
                "and the hash is deterministic across calls"

            Expect.equal underGenesis.Length 64 "bare 64-hex, matching the substrate's other digests"

            Expect.isTrue
                (underGenesis |> Seq.forall (fun c -> Char.IsDigit c || (c >= 'a' && c <= 'f')))
                "lowercase hex"
        }

        test "the same content in a different SCOPE hashes differently" {
            let payload = grant "alice" "Read"

            Expect.notEqual
                (PermissionAuditChain.contentHash "team-other" PermissionAuditChain.genesisHash payload)
                (PermissionAuditChain.contentHash scope PermissionAuditChain.genesisHash payload)
                "the scope is inside the framing, so a record cannot be replayed into another tenant's chain"
        }

        // ── Tamper detection, positioned ────────────────────────────

        test "mutating a record's content is reported as TamperedRecord at its index" {
            let _, auditLog =
                recordGrants [ grant "alice" "Read"; grant "bob" "Write"; grant "carol" "Admin" ]

            let payloads = readChain auditLog
            let intact = expectIntact (PermissionAuditChain.verify scope payloads)
            Expect.equal intact.ChainedCount 3 "precondition: the chain is intact before tampering"

            // Escalate the SECOND record's grant while leaving its stored
            // hash alone — the edit an insider would actually make.
            let victim = payloads |> List.find (fun p -> p.AffectedUserId = "bob")

            let tampered =
                payloads
                |> List.map (fun p ->
                    if p.AffectedUserId = "bob" then
                        { p with Permissions = "Admin" }
                    else
                        p)

            Expect.equal victim.Permissions "Write" "precondition: the victim held Write"

            let broken = expectBreak (PermissionAuditChain.verify scope tampered)
            Expect.equal broken.Kind PermissionAuditChain.TamperedRecord "an in-place edit"
            Expect.equal broken.Position 1 "reported at the edited record's own index, not at the end"
        }

        test "deleting a middle record strands the tail and is reported at the gap" {
            let _, auditLog =
                recordGrants [ grant "alice" "Read"; grant "bob" "Write"; grant "carol" "Admin" ]

            let payloads = readChain auditLog
            let survivors = payloads |> List.filter (fun p -> p.AffectedUserId <> "bob")

            let broken = expectBreak (PermissionAuditChain.verify scope survivors)
            Expect.equal broken.Kind PermissionAuditChain.OrphanedRecords "the record after the gap joins to nothing"
            Expect.equal broken.Position 1 "reported where the chain stops — the deleted record's index"
        }

        test "deleting the FIRST record is reported as MissingGenesis, not as a generic gap" {
            let _, auditLog = recordGrants [ grant "alice" "Read"; grant "bob" "Write" ]
            let payloads = readChain auditLog
            let survivors = payloads |> List.filter (fun p -> p.AffectedUserId <> "alice")

            let broken = expectBreak (PermissionAuditChain.verify scope survivors)

            Expect.equal
                broken.Kind
                PermissionAuditChain.MissingGenesis
                "truncating from the front is its own finding — an auditor acts on it differently"

            Expect.equal broken.Position 0 "at the front"
        }

        test "deleting the LAST record is detected — a chain cannot be quietly shortened" {
            let _, auditLog =
                recordGrants [ grant "alice" "Read"; grant "bob" "Write"; grant "carol" "Admin" ]

            let payloads = readChain auditLog

            // The tail is the record nothing names as a predecessor.
            let head =
                match PermissionAuditChain.readHead payloads with
                | PermissionAuditChain.HeadAt h -> h
                | other -> failtestf "expected a single head, got %A" other

            let survivors =
                payloads
                |> List.filter (fun p -> p.Chain |> Option.forall (fun c -> c.ContentHash <> head))

            let summary = expectIntact (PermissionAuditChain.verify scope survivors)

            // Truncation from the BACK leaves a self-consistent shorter
            // chain, so it is caught by the head moving, not by a break.
            // Stating that honestly is the point: the in-store chain
            // detects it only against a head recorded elsewhere — which
            // is precisely what Phase 677's SIGNED head count is for.
            Expect.equal summary.ChainedCount 2 "the shortened chain is internally consistent"
            Expect.notEqual summary.Head (Some head) "but its head is no longer the one an observer recorded"
        }

        test "two records claiming one predecessor are reported as a fork" {
            let _, auditLog = recordGrants [ grant "alice" "Read"; grant "bob" "Write" ]
            let payloads = readChain auditLog

            let genesisRecord =
                payloads
                |> List.find (fun p ->
                    p.Chain
                    |> Option.exists (fun c -> c.PrevHash = PermissionAuditChain.genesisHash))

            // A substitute chained onto the SAME predecessor — what a
            // concurrent second writer, or a replaced record, leaves.
            let substitute =
                PermissionAuditChain.link scope PermissionAuditChain.genesisHash (grant "mallory" "Admin")

            let broken =
                expectBreak (PermissionAuditChain.verify scope (substitute :: payloads))

            Expect.equal broken.Kind PermissionAuditChain.ForkedChain "two successors for one predecessor"
            Expect.equal broken.Position 0 "at the contested position"

            Expect.stringContains
                broken.Detail
                (genesisRecord.Chain |> Option.map _.ContentHash |> Option.defaultValue "<none>")
                "the detail names the competing records rather than just counting them"
        }

        // ── Pre-chain boundary (the upgraded deployment) ────────────

        test "records written before the chain existed verify as an unchained prefix, not a break" {
            let legacy = [ grant "old-1" "Read"; grant "old-2" "Write" ] // Chain = None, as a pre-553 row decodes
            let _, auditLog = recordGrants [ grant "alice" "Read"; grant "bob" "Write" ]
            let chained = readChain auditLog

            let summary = expectIntact (PermissionAuditChain.verify scope (legacy @ chained))
            Expect.equal summary.UnchainedCount 2 "the pre-chain rows are counted, not absorbed"
            Expect.equal summary.ChainedCount 2 "and the chain over the new rows still verifies"
        }

        test "a scope holding ONLY pre-chain records verifies clean with no head" {
            let summary =
                expectIntact (PermissionAuditChain.verify scope [ grant "old-1" "Read"; grant "old-2" "Write" ])

            Expect.equal summary.ChainedCount 0 "nothing is chained"
            Expect.equal summary.UnchainedCount 2 "everything is accounted for"
            Expect.isNone summary.Head "and there is no head to report"
        }

        test "a persisted payload that OMITS the Chain field decodes to None rather than throwing" {
            // The FableConverters null-absorption discipline, exercised
            // against the exact bytes a pre-553 deployment wrote: no
            // `Chain` property at all. This is the case that makes the
            // field additive in practice rather than only on paper.
            let store = RecordingEventStore()

            let legacyJson =
                """{"UserId":"admin","TeamId":"acme","AffectedUserId":"alice","ModuleName":"reports","Permissions":"Read"}"""

            (store :> IEventStore).Write(Events.create scope AuditSourceModule.value "PermissionChanged" legacyJson)
            |> Async.RunSynchronously

            let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog
            let payloads = readChain auditLog

            Expect.equal payloads.Length 1 "the legacy row decoded"
            Expect.isNone payloads.Head.Chain "the absent field reads as None"
            Expect.equal payloads.Head.AffectedUserId "alice" "and the rest of the payload survived"
        }

        test "a new record chains onto the head even when pre-chain records are present" {
            let store = RecordingEventStore()

            let legacyJson =
                """{"UserId":"admin","TeamId":"acme","AffectedUserId":"legacy","ModuleName":"reports","Permissions":"Read"}"""

            (store :> IEventStore).Write(Events.create scope AuditSourceModule.value "PermissionChanged" legacyJson)
            |> Async.RunSynchronously

            let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog

            auditLog.Record(scope, PermissionChanged(grant "alice" "Read"))
            |> Async.RunSynchronously

            auditLog.Record(scope, PermissionChanged(grant "bob" "Write"))
            |> Async.RunSynchronously

            let summary = expectIntact (PermissionAuditChain.verify scope (readChain auditLog))
            Expect.equal summary.UnchainedCount 1 "the legacy row stays outside the chain"
            Expect.equal summary.ChainedCount 2 "the chain starts at the first record written after the upgrade"
        }

        // ── Phase 9t interaction — an unreadable head never guesses ──

        test "an unreadable chain head records the event UNCHAINED under LogAndContinue" {
            let store = ReadFaultingEventStore()
            let sink = CapturingMetricsSink()

            let auditLog =
                AuditLog.EventStoreAuditLog(store, silentLogger, (fun () -> sink :> IMetricsSink), LogAndContinue)
                :> IAuditLog

            auditLog.Record(scope, PermissionChanged(grant "alice" "Read"))
            |> Async.RunSynchronously

            Expect.equal store.Events.Length 1 "the audit row still landed — the action is not un-audited"

            let increments =
                sink.Increments
                |> List.filter (fun (name, _) -> name = AuditLog.AuditMetrics.ChainHeadUnreadableTotal)

            Expect.equal increments.Length 1 "the lost link is counted, not silent"
            Expect.equal (snd increments.Head |> Map.tryFind "reason") (Some "read_failed") "tagged with the cause"

            Expect.isFalse
                (store.Events.Head.Payload.Contains "\"PrevHash\"")
                "and no predecessor was invented — an unchained row, never a guessed link"
        }

        test "an unreadable chain head REFUSES the action under RefuseAction" {
            let store = ReadFaultingEventStore()
            let sink = CapturingMetricsSink()

            let auditLog =
                AuditLog.EventStoreAuditLog(store, silentLogger, (fun () -> sink :> IMetricsSink), RefuseAction)
                :> IAuditLog

            Expect.throwsT<AuditLog.AuditWriteRefusedException>
                (fun () ->
                    auditLog.Record(scope, PermissionChanged(grant "alice" "Read"))
                    |> Async.RunSynchronously)
                "a deployment that would rather fail than be un-audited would rather fail than be un-evidenced"

            Expect.equal store.Events.Length 0 "and nothing was written"
        }

        test "a forked chain makes the head unreadable rather than picking a side" {
            let a =
                PermissionAuditChain.link scope PermissionAuditChain.genesisHash (grant "alice" "Read")

            let b =
                PermissionAuditChain.link scope PermissionAuditChain.genesisHash (grant "bob" "Write")

            match PermissionAuditChain.readHead [ a; b ] with
            | PermissionAuditChain.HeadForked candidates -> Expect.equal candidates.Length 2 "both heads are surfaced"
            | other -> failtestf "expected HeadForked, got %A" other
        }

        test "a chain in which every record is referenced has no head and is refused" {
            // Constructed, not appendable: each record names the other as
            // its predecessor. An honest writer cannot produce this, which
            // is why it is a refusal rather than an answer.
            let a = {
                grant "alice" "Read" with
                    Chain = Some { PrevHash = "b"; ContentHash = "a" }
            }

            let b = {
                grant "bob" "Write" with
                    Chain = Some { PrevHash = "a"; ContentHash = "b" }
            }

            Expect.equal (PermissionAuditChain.readHead [ a; b ]) PermissionAuditChain.HeadUnanchored "no head exists"
        }

        // ── Non-permission events are untouched (GP 11 / GP 13) ─────

        test "a non-permission audit event costs no head read and gains no chain fields" {
            let store = ReadFaultingEventStore()

            let auditLog =
                AuditLog.EventStoreAuditLog(store, silentLogger, failurePolicy = RefuseAction) :> IAuditLog

            // Reads throw in this store, so a non-permission event that
            // tried to read the head would refuse. It must not.
            auditLog.Record(
                scope,
                UserLoggedIn {
                    UserId = "alice"
                    AuthProvider = "Header"
                }
            )
            |> Async.RunSynchronously

            Expect.equal store.Events.Length 1 "recorded without consulting the chain"
        }

        test "a caller that supplies its own link is taken at its word" {
            // The replay / re-emission path re-writes rows that were
            // already chained; re-chaining them onto the CURRENT head
            // would rewrite history.
            let store = RecordingEventStore()
            let auditLog = AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog

            let preChained =
                PermissionAuditChain.link scope PermissionAuditChain.genesisHash (grant "alice" "Read")

            auditLog.Record(scope, PermissionChanged preChained) |> Async.RunSynchronously
            let payloads = readChain auditLog

            Expect.equal
                (payloads.Head.Chain |> Option.map _.ContentHash)
                (preChained.Chain |> Option.map _.ContentHash)
                "the supplied link survived untouched"
        }

        // ── The auditor's own entry point ───────────────────────────

        testCaseAsync
            "verifyScope reads the trail back through IAuditLog and verifies it"
            (async {
                let _, auditLog = recordGrants [ grant "alice" "Read"; grant "bob" "Write" ]
                let! report = PermissionAuditChain.verifyScope auditLog scope

                match report with
                | PermissionAuditChain.ChainIntact summary ->
                    Expect.equal summary.ChainedCount 2 "both records verified"
                | PermissionAuditChain.ChainBrokenAt b -> failtestf "unexpected break %A at %d" b.Kind b.Position
            })
    ]