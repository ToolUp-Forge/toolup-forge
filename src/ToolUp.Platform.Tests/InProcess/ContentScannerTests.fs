module ToolUp.Platform.Tests.InProcess.ContentScannerTests

open System
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open ToolUp.Platform.ContentScanners.ClamAv
open ToolUp.Platform.ContentScanners.ClamAv.ClamAvContentScanner
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 515 — upload-boundary content scanning ────────────────────
//
// Two arms, following the Pgvector / Redis / Tesseract companion
// precedent:
//
//  • **Structural arm (always on).** Everything provable with no daemon
//    anywhere near the machine — which is the load-bearing majority of
//    the phase. The phase's real claims are not "ClamAV detects EICAR"
//    (ClamAV's own suite covers that) but:
//      – an upload the scanner rejects is REFUSED, before anything is
//        persisted, with the reason in the audit trail;
//      – a deployment that composed no scanner behaves exactly as it did
//        before this phase existed (GP 11 / GP 13);
//      – a scanner that cannot answer resolves per the deployment's
//        fail-open / fail-closed choice, not per the seam's guess.
//    All three are about the KnowledgeBase upload path and the seam, and
//    all three are provable offline with a fake scanner.
//    The ClamAV companion's own reply grammar and frame encoding are
//    likewise pure, so they are pinned here too.
//
//  • **Live arm (env-gated on `TOOLUP_CLAMAV_HOST`).** Real daemon, real
//    signature database, the real EICAR string over a real socket.
//    Reported **Pending** when the variable is unset, so a fresh
//    checkout and CI are green without provisioning anything. No Docker,
//    no Testcontainers.
//
// **On the EICAR string.** It is the industry-standard, deliberately
// harmless antivirus test file — every scanner detects it by agreement,
// and it is not malware. It is assembled here from fragments at run time
// rather than written as a literal so that this repository does not
// itself trip a naive scanner running over the source tree.

[<Literal>]
let private ClamAvHostEnvVar = "TOOLUP_CLAMAV_HOST"

[<Literal>]
let private ClamAvPortEnvVar = "TOOLUP_CLAMAV_PORT"

let private clamAvHost =
    match Environment.GetEnvironmentVariable ClamAvHostEnvVar with
    | null
    | "" -> None
    | host -> Some host

let private clamAvPort =
    match Environment.GetEnvironmentVariable ClamAvPortEnvVar with
    | null
    | "" -> 3310
    | raw ->
        match Int32.TryParse raw with
        | true, p -> p
        | _ -> 3310

/// The EICAR test string, assembled from fragments (see the header note).
let private eicarBytes () : byte[] =
    let parts = [
        "X5O!P%@AP[4"
        @"\PZX54(P^)7CC)7}$EICAR-STANDARD-"
        "ANTIVIRUS-TEST-FILE!$H+H*"
    ]

    Encoding.ASCII.GetBytes(String.Concat parts)

// ─── Fakes ───────────────────────────────────────────────────────────

/// A scanner that returns a fixed verdict and counts its invocations —
/// the invocation count is what proves the GP-13 "no scanner composed ⇒
/// nothing happens" claim, which a verdict alone cannot.
type private FakeScanner(verdict: ScanVerdict) =
    let mutable calls = 0
    member _.Calls = calls

    interface IContentScanner with
        member _.Name = "fake"

        member _.Scan(_, _) = async {
            Threading.Interlocked.Increment &calls |> ignore
            return verdict
        }

/// Captures every audit row so the test can assert on the verdict trail.
type private CapturingAuditLog() =
    let rows = ResizeArray<string * AuditEvent>()
    member _.Rows = List.ofSeq rows

    member this.Scans =
        rows
        |> Seq.choose (fun (_, e) ->
            match e with
            | ContentScanned p -> Some p
            | _ -> None)
        |> List.ofSeq

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock rows (fun () -> rows.Add(scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

// ─── Harness ─────────────────────────────────────────────────────────

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private noopNotifications =
    { new INotificationChannel with
        member _.Publish(_, _) = async.Return()

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()
    }

let private mkDeps
    (storage: IBlobStorage)
    (scanner: IContentScanner option)
    (policy: ContentScanPolicy)
    (audit: IAuditLog option)
    (container: string)
    : KnowledgeApiDeps =
    {
        Storage = storage
        Queue = IngestionQueue()
        OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
        TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
        Notifications = noopNotifications
        Logger = noopLogger
        Scope = {
            ScopeId = container
            Container = container
            Persist = true
        }
        UserId = "user-1"
        VectorScope = Deployment
        VectorStore = None
        IndexLifecycle = None
        EventStore = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
        OriginalResolver = createDefault ()
        AuditLog = audit
        RecordEnqueue = ignore
        PublishInventory = fun () -> async.Return()
        MarkIngestionFailed = fun _ _ _ -> async.Return()
        EnsureContextWriteAllowed = fun () -> async { return Ok() }
        ScopeResolvedFromRequest = true
        UploadPolicy = KnowledgeUploadPolicy.permissive
        DedupPolicy = KnowledgeDedupPolicy.enabled
        VersioningPolicy = KnowledgeVersioningPolicy.disabled
        QuotaPolicy = KnowledgeQuotaPolicy.unlimited
        RetentionPolicy = KnowledgeRetentionPolicy.retainForever
        ContentScanner = scanner
        ScanPolicy = policy
        DisclosureGate = None
    }

let private isRejected =
    function
    | UploadRejected _ -> true
    | _ -> false

let private rejectionReason status =
    match status with
    | UploadRejected r -> r
    | other -> failtestf "expected UploadRejected, got %A" other

let private benignCsv = Encoding.UTF8.GetBytes "name,score\na,1\nb,2"

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "Phase 515 — upload content scanning" [

        // ── The seam itself ──────────────────────────────────────────

        testCaseAsync "AllowAllContentScanner admits every payload"
        <| async {
            let scanner = AllowAllContentScanner() :> IContentScanner
            let! verdict = scanner.Scan(eicarBytes (), "eicar.txt")

            Expect.equal verdict ScanClean "the no-op default is a true no-op — it inspects nothing"
            Expect.equal scanner.Name "allow-all" "stable name for logs and audit rows"
        }

        testCaseAsync "evaluate: a rejected verdict is always a refusal, under either error policy"
        <| async {
            for policy in [ ContentScanPolicy.failOpen; ContentScanPolicy.failClosed ] do
                let scanner = FakeScanner(ScanRejected "Eicar-Test-Signature") :> IContentScanner
                let! _, refusal = ContentScan.evaluate scanner policy benignCsv "f.csv"

                Expect.isSome refusal "no error policy softens an explicit rejection"

                Expect.stringContains
                    (Option.get refusal)
                    "Eicar-Test-Signature"
                    "the scanner's reason reaches the refusal message"
        }

        testCaseAsync "evaluate: an unavailable scanner refuses under fail-closed and admits under fail-open"
        <| async {
            let scanner = FakeScanner(ScanUnavailable "connection refused") :> IContentScanner

            let! _, closed = ContentScan.evaluate scanner ContentScanPolicy.failClosed benignCsv "f.csv"
            Expect.isSome closed "fail-closed refuses what it could not scan"

            let! _, opened = ContentScan.evaluate scanner ContentScanPolicy.failOpen benignCsv "f.csv"
            Expect.isNone opened "fail-open admits what it could not scan"
        }

        test "ContentScanPolicy defaults to fail-closed" {
            Expect.equal
                ContentScanPolicy.defaults.OnScanError
                FailClosedOnScanError
                "a deployment that composed a scanner has said unscanned content is unacceptable"
        }

        // ── The upload path ──────────────────────────────────────────

        testCaseAsync "a rejected upload is refused, persists nothing, and is audited with the reason"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let scanner = FakeScanner(ScanRejected "Eicar-Test-Signature")

            let deps =
                mkDeps
                    storage
                    (Some(scanner :> IContentScanner))
                    ContentScanPolicy.failClosed
                    (Some(audit :> IAuditLog))
                    "team-scan"

            let! doc = uploadDocument deps (eicarBytes ()) "sample.txt"

            Expect.isTrue (isRejected doc.Status) "the scanner's rejection refuses the upload"

            Expect.stringContains
                (rejectionReason doc.Status)
                "Eicar-Test-Signature"
                "the refusal names what the scanner found, so it is investigable"

            // Nothing persisted: no index entry, no raw blob (GP 9 — a
            // refusal stores nothing).
            let! index = loadIndex storage "team-scan"
            Expect.isEmpty index "a scan refusal writes no index entry"

            let! raw = storage.Download("team-scan", sprintf "knowledge/%s/sample.txt" doc.Id)
            Expect.isTrue (Result.isError raw) "a scan refusal writes no raw blob"

            match audit.Scans with
            | [ row ] ->
                Expect.equal row.Verdict "rejected" "the verdict label is on the row"
                Expect.isTrue row.Refused "the row records that the platform acted on the verdict"
                Expect.equal row.FileName "sample.txt" "the row names the file"
                Expect.equal row.ScannerName "fake" "the row names the scanner that spoke"
                Expect.equal row.ScopeId "team-scan" "the row is filed under the caller's scope"
                Expect.isSome row.Reason "the row carries the scanner's reason"
                Expect.equal row.SizeBytes (int64 (eicarBytes ()).Length) "the row carries the payload size"

                Expect.isFalse
                    (String.IsNullOrWhiteSpace row.ContentHash)
                    "the row carries a digest — a verdict with no handle on WHAT was scanned is not investigable"
            | other -> failtestf "expected exactly one ContentScanned row, got %A" other
        }

        testCaseAsync "a clean verdict admits the upload and is still audited"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let scanner = FakeScanner ScanClean

            let deps =
                mkDeps
                    storage
                    (Some(scanner :> IContentScanner))
                    ContentScanPolicy.failClosed
                    (Some(audit :> IAuditLog))
                    "team-clean"

            let! doc = uploadDocument deps benignCsv "ok.csv"

            Expect.isFalse (isRejected doc.Status) "a clean verdict does not refuse"
            Expect.equal scanner.Calls 1 "the scanner was consulted exactly once"

            match audit.Scans with
            | [ row ] ->
                Expect.equal row.Verdict "clean" "a clean scan is recorded too"

                Expect.isFalse
                    row.Refused
                    "a clean row is not a refusal — the two fields come apart and both are needed"

                Expect.isNone row.Reason "a clean verdict carries no reason"
            | other -> failtestf "expected exactly one ContentScanned row, got %A" other
        }

        testCaseAsync "fail-closed refuses an unavailable scanner; fail-open admits it, and the row says so"
        <| async {
            // Fail-closed.
            let closedStorage = InMemoryBlobStorage() :> IBlobStorage
            let closedAudit = CapturingAuditLog()

            let closedDeps =
                mkDeps
                    closedStorage
                    (Some(FakeScanner(ScanUnavailable "connection refused") :> IContentScanner))
                    ContentScanPolicy.failClosed
                    (Some(closedAudit :> IAuditLog))
                    "team-closed"

            let! closedDoc = uploadDocument closedDeps benignCsv "ok.csv"
            Expect.isTrue (isRejected closedDoc.Status) "fail-closed refuses an upload it could not scan"

            let! closedIndex = loadIndex closedStorage "team-closed"
            Expect.isEmpty closedIndex "the fail-closed refusal persists nothing"

            match closedAudit.Scans with
            | [ row ] ->
                Expect.equal row.Verdict "unavailable" "the row distinguishes 'could not scan' from 'rejected'"
                Expect.isTrue row.Refused "under fail-closed the unavailable verdict was acted on"
            | other -> failtestf "expected exactly one ContentScanned row, got %A" other

            // Fail-open, same verdict.
            let openStorage = InMemoryBlobStorage() :> IBlobStorage
            let openAudit = CapturingAuditLog()

            let openDeps =
                mkDeps
                    openStorage
                    (Some(FakeScanner(ScanUnavailable "connection refused") :> IContentScanner))
                    ContentScanPolicy.failOpen
                    (Some(openAudit :> IAuditLog))
                    "team-open"

            let! openDoc = uploadDocument openDeps benignCsv "ok.csv"
            Expect.isFalse (isRejected openDoc.Status) "fail-open admits an upload it could not scan"

            match openAudit.Scans with
            | [ row ] ->
                Expect.equal row.Verdict "unavailable" "the same verdict is recorded"

                Expect.isFalse
                    row.Refused
                    "under fail-open the upload was ADMITTED UNSCANNED — the row must make that findable without re-deriving the policy"
            | other -> failtestf "expected exactly one ContentScanned row, got %A" other
        }

        testCaseAsync "no scanner composed: the upload path is unchanged and emits no scan row (GP 11 / GP 13)"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()

            let deps =
                mkDeps storage None ContentScanPolicy.defaults (Some(audit :> IAuditLog)) "team-none"

            // The same payload a composed scanner would have rejected.
            let! doc = uploadDocument deps (eicarBytes ()) "sample.txt"

            Expect.isFalse
                (isRejected doc.Status)
                "with no scanner composed the upload is admitted exactly as it was before Phase 515"

            Expect.isEmpty audit.Scans "no scanner composed ⇒ no ContentScanned row at all"

            let! index = loadIndex storage "team-none"
            Expect.equal (index |> List.map _.Id) [ doc.Id ] "the document was indexed, as pre-515"
        }

        testCaseAsync "the scan runs before persistence — an already-refused upload is never scanned"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let scanner = FakeScanner ScanClean

            // A 1-byte cap makes the pure Phase 119 size check refuse
            // first; the scanner must not be consulted at all.
            let deps = {
                mkDeps storage (Some(scanner :> IContentScanner)) ContentScanPolicy.failClosed None "team-order" with
                    UploadPolicy = {
                        KnowledgeUploadPolicy.permissive with
                            MaxUploadBytes = Some 1L
                    }
            }

            let! doc = uploadDocument deps benignCsv "big.csv"

            Expect.isTrue (isRejected doc.Status) "the size cap refuses first"

            Expect.equal
                scanner.Calls
                0
                "there is nothing to learn from streaming a payload to a scanner when the upload is already refused"
        }

        // ── ClamAV companion: pure protocol surface ──────────────────

        test "clamd reply grammar maps to verdicts conservatively" {
            Expect.equal (parseReply "stream: OK\000") ScanClean "an explicit OK is the only clean verdict"

            match parseReply "stream: Eicar-Test-Signature FOUND\000" with
            | ScanRejected reason ->
                Expect.stringContains reason "Eicar-Test-Signature" "the signature name is carried through"
            | other -> failtestf "FOUND must map to ScanRejected, got %A" other

            match parseReply "INSTREAM size limit exceeded. ERROR\000" with
            | ScanUnavailable _ -> ()
            | other -> failtestf "ERROR must map to ScanUnavailable — never to clean, got %A" other

            match parseReply "" with
            | ScanUnavailable _ -> ()
            | other -> failtestf "an empty reply must map to ScanUnavailable, got %A" other

            match parseReply "something entirely unexpected" with
            | ScanUnavailable _ -> ()
            | other -> failtestf "an unrecognised reply must map to ScanUnavailable, got %A" other
        }

        test "INSTREAM length prefixes are big-endian" {
            // BitConverter is little-endian on every RID this ships on, so
            // the reversal is load-bearing, not decorative.
            Expect.equal (lengthPrefix 1) [| 0uy; 0uy; 0uy; 1uy |] "1 encodes as 00 00 00 01"
            Expect.equal (lengthPrefix 0) [| 0uy; 0uy; 0uy; 0uy |] "the terminator is four zero bytes"
            Expect.equal (lengthPrefix 65536) [| 0uy; 1uy; 0uy; 0uy |] "65536 encodes as 00 01 00 00"
        }

        test "the ClamAV scanner refuses to be constructed without an explicit host" {
            Expect.throws
                (fun () -> ClamAvContentScanner(ClamAvOptions.create "") |> ignore)
                "an implicit localhost is how a deployment ends up scanning nothing while looking composed"
        }

        // ── Live arm (env-gated) ─────────────────────────────────────

        testCaseAsync "LIVE: a real clamd rejects the EICAR test string and passes a benign one"
        <| async {
            match clamAvHost with
            | None ->
                skiptestf "set %s (and optionally %s) to run the live ClamAV arm" ClamAvHostEnvVar ClamAvPortEnvVar
            | Some host ->
                let scanner =
                    ClamAvOptions.create host
                    |> ClamAvOptions.withPort clamAvPort
                    |> ClamAvContentScanner.createWith

                let! infected = scanner.Scan(eicarBytes (), "eicar.txt")

                match infected with
                | ScanRejected reason -> Expect.stringContains (reason.ToLowerInvariant()) "eicar" "clamd named EICAR"
                | other -> failtestf "a real clamd must reject the EICAR test string, got %A" other

                let! clean = scanner.Scan(benignCsv, "ok.csv")
                Expect.equal clean ScanClean "a benign CSV passes a real clamd"
        }
    ]