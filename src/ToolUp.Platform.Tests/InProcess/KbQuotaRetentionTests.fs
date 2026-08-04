module ToolUp.Platform.Tests.InProcess.KbQuotaRetentionTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerRetentionSweep
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 512 — per-scope KB quota + retention ──────────────────────
//
// Two independent levers over one derived surface:
//
//   * **Quota** caps the *corpus* (`MaxDocuments` / `MaxBytes`) at the
//     upload boundary, before anything is persisted, and reports headroom
//     through `GetScopeUsage`.
//   * **Retention** expires documents past `MaxAge` on a scheduled sweep,
//     through the Phase 115 deletion fan-out, with a
//     `KnowledgeDocumentsPurged` audit row.
//
// Both are derived entirely from fields `KnowledgeDocument` has always
// carried (`SizeBytes`, `UploadedAt`) — no persisted record was widened,
// so there is no legacy-blob deserialisation hazard to cover here.
//
// The GP 11 arm is the one that matters most and is asserted on both
// levers: a deployment that composes neither is byte-for-byte its pre-512
// self — nothing capped, nothing purged.

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

/// Thread-safe capturing `IAuditLog` double.
type private CapturingAuditLog() =
    let events = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Events = lock gate (fun () -> events |> List.ofSeq)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock gate (fun () -> events.Add(scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private mkDeps (storage: IBlobStorage) (quota: KnowledgeQuotaPolicy) (container: string) : KnowledgeApiDeps = {
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
    AuditLog = None
    RecordEnqueue = ignore
    PublishInventory = fun () -> async.Return()
    MarkIngestionFailed = fun _ _ _ -> async.Return()
    EnsureContextWriteAllowed = fun () -> async { return Ok() }
    ScopeResolvedFromRequest = true
    UploadPolicy = KnowledgeUploadPolicy.permissive
    DedupPolicy = KnowledgeDedupPolicy.enabled
    // Phase 510 — quota/retention are asserted on the unversioned path.
    VersioningPolicy = KnowledgeVersioningPolicy.disabled
    QuotaPolicy = quota
    RetentionPolicy = KnowledgeRetentionPolicy.retainForever
    DisclosureGate = None
}

let private csv (name: string) =
    Text.Encoding.UTF8.GetBytes(sprintf "name,score\n%s,1" name)

let private rejectionReason (status: IngestionStatus) =
    match status with
    | UploadRejected reason -> Some reason
    | _ -> None

/// A document as it sits in a persisted index — the shape the retention
/// sweep selects over. `age` back-dates `UploadedAt`.
let private storedDoc (docId: string) (age: TimeSpan) (size: int64) : KnowledgeDocument = {
    Id = docId
    FileName = docId + ".pdf"
    FileType = "pdf"
    UploadedAt = DateTimeOffset.UtcNow - age
    UploadedBy = "user-1"
    Status = Complete 1
    SizeBytes = size
    ChunkCount = 1
    Source = UploadedFile
    ContentHash = None
    Version = 1
}

let private withSource (source: KnowledgeSource) (doc: KnowledgeDocument) = { doc with Source = source }

let private noteSource: KnowledgeSource =
    Note {
        Title = "a note"
        Author = "user-1"
        CreatedAt = DateTimeOffset.UtcNow
        LastEditedAt = None
    }

let private narrativeSource: KnowledgeSource =
    FromNarrative {
        ModuleId = "sales"
        PageRoute = None
        SettingsKey = "k"
        SettingsDisplay = []
        GeneratedAt = DateTimeOffset.UtcNow
    }

let private days (n: float) = TimeSpan.FromDays n

let tests =
    testList "Phase 512 — KB per-scope quota + retention" [

        // ─── 512.A — quota policy, pure ───────────────────────────────

        test "unlimited quota never refuses, whatever the corpus holds" {
            Expect.isNone
                (KnowledgeQuotaPolicy.exceeds 10_000 999_999_999L 1_000L KnowledgeQuotaPolicy.unlimited)
                "an unlimited policy has no refusal to give"

            Expect.isTrue (KnowledgeQuotaPolicy.isUnlimited KnowledgeQuotaPolicy.unlimited) "the default is unlimited"
        }

        test "document cap refuses at the cap, not one past it" {
            let policy = {
                KnowledgeQuotaPolicy.unlimited with
                    MaxDocuments = Some 3
            }

            Expect.isNone (KnowledgeQuotaPolicy.exceeds 2 0L 1L policy) "two of three permitted still admits one more"

            let refusal = KnowledgeQuotaPolicy.exceeds 3 0L 1L policy

            Expect.isSome refusal "a scope already holding the cap admits nothing further"

            Expect.stringContains
                refusal.Value
                "3 permitted documents"
                "the reason names the cap so the uploader knows what to delete against"
        }

        test "byte cap counts the incoming upload, not just what is stored" {
            let policy = {
                KnowledgeQuotaPolicy.unlimited with
                    MaxBytes = Some 1_000L
            }

            Expect.isNone (KnowledgeQuotaPolicy.exceeds 1 900L 100L policy) "landing exactly on the cap is admitted"

            Expect.isSome
                (KnowledgeQuotaPolicy.exceeds 1 900L 101L policy)
                "one byte past the cap is refused — the incoming size is part of the sum"
        }

        test "a zero cap refuses the first upload rather than admitting a freebie" {
            let byCount = {
                KnowledgeQuotaPolicy.unlimited with
                    MaxDocuments = Some 0
            }

            let byBytes = {
                KnowledgeQuotaPolicy.unlimited with
                    MaxBytes = Some 0L
            }

            Expect.isSome (KnowledgeQuotaPolicy.exceeds 0 0L 1L byCount) "MaxDocuments = 0 admits nothing"
            Expect.isSome (KnowledgeQuotaPolicy.exceeds 0 0L 1L byBytes) "MaxBytes = 0 admits nothing"
        }

        // ─── 512.A — quota at the upload boundary ────────────────────

        testCaseAsync "an upload past MaxDocuments is refused with a legible reason and persists nothing"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let deps =
                mkDeps
                    storage
                    {
                        KnowledgeQuotaPolicy.unlimited with
                            MaxDocuments = Some 2
                    }
                    "team-quota-docs"

            let! first = uploadDocument deps (csv "a") "a.csv"
            let! second = uploadDocument deps (csv "b") "b.csv"
            Expect.isNone (rejectionReason first.Status) "first upload is under the cap"
            Expect.isNone (rejectionReason second.Status) "second upload fills the cap"

            let! third = uploadDocument deps (csv "c") "c.csv"

            match rejectionReason third.Status with
            | None -> failtest $"expected the third upload to be refused, got %A{third.Status}"
            | Some reason ->
                Expect.stringContains reason "document quota" "the refusal names the quota, not a generic error"

                Expect.stringContains
                    reason
                    "2 permitted documents"
                    "the refusal states the cap so the user knows the shape of the problem"

            // The refusal stored NOTHING — the index still holds exactly the
            // two admitted documents, and the third's raw blob was never written.
            let! index = loadIndex storage "team-quota-docs"
            Expect.hasLength index 2 "a refused upload leaves the index untouched"

            Expect.isFalse
                (index |> List.exists (fun d -> d.Id = third.Id))
                "the refused document never reaches the index"

            let! blobs = storage.List("team-quota-docs", sprintf "knowledge/%s" third.Id)
            Expect.isEmpty blobs "a refused upload writes no raw blob"
        }

        testCaseAsync "an upload past MaxBytes is refused"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let payload = csv "a"

            // Room for exactly one payload.
            let deps =
                mkDeps
                    storage
                    {
                        KnowledgeQuotaPolicy.unlimited with
                            MaxBytes = Some(int64 payload.Length)
                    }
                    "team-quota-bytes"

            let! first = uploadDocument deps payload "a.csv"
            Expect.isNone (rejectionReason first.Status) "the first upload fits exactly"

            let! second = uploadDocument deps (csv "bbbb") "b.csv"

            match rejectionReason second.Status with
            | None -> failtest $"expected the second upload to be refused, got %A{second.Status}"
            | Some reason -> Expect.stringContains reason "storage quota" "the refusal names the byte quota"

            let! index = loadIndex storage "team-quota-bytes"
            Expect.hasLength index 1 "the over-quota upload persisted nothing"
        }

        testCaseAsync "GP 11 — no quota composed means nothing is ever capped"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let deps = mkDeps storage KnowledgeQuotaPolicy.unlimited "team-uncapped"

            for i in 1..6 do
                let! doc = uploadDocument deps (csv (string i)) (sprintf "%d.csv" i)

                Expect.isNone
                    (rejectionReason doc.Status)
                    "an uncomposed deployment refuses nothing, however large the corpus grows"

            let! index = loadIndex storage "team-uncapped"
            Expect.hasLength index 6 "every upload landed"
        }

        testCaseAsync "a deduplicated re-upload is not refused by a full quota"
        <| async {
            // Ordering guard: dedup runs BEFORE the quota check, because a
            // duplicate stores nothing new. Refusing it would reject a
            // request that costs the scope no additional storage — and the
            // caller would lose the docId they already own.
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let deps =
                mkDeps
                    storage
                    {
                        KnowledgeQuotaPolicy.unlimited with
                            MaxDocuments = Some 1
                    }
                    "team-dedup-quota"

            let bytes = csv "a"
            let! first = uploadDocument deps bytes "a.csv"
            Expect.isNone (rejectionReason first.Status) "the first upload fills the one-document cap"

            let! again = uploadDocument deps bytes "a-copy.csv"

            Expect.isNone (rejectionReason again.Status) "a byte-identical re-upload is deduped, not refused"
            Expect.equal again.Id first.Id "the existing document is returned verbatim"

            let! index = loadIndex storage "team-dedup-quota"
            Expect.hasLength index 1 "no second document was created"
        }

        // ─── 512.C — usage surfacing ─────────────────────────────────

        testCaseAsync "GetScopeUsage reports counts, caps and headroom against the composed quota"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let deps =
                mkDeps
                    storage
                    {
                        MaxDocuments = Some 5
                        MaxBytes = Some 10_000L
                    }
                    "team-usage"

            let! a = uploadDocument deps (csv "a") "a.csv"
            let! b = uploadDocument deps (csv "b") "b.csv"
            let expectedBytes = a.SizeBytes + b.SizeBytes

            let! usage = getScopeUsage deps

            Expect.equal usage.DocumentCount 2 "counts the scope's documents"
            Expect.equal usage.TotalBytes expectedBytes "sums SizeBytes across the corpus"
            Expect.equal usage.MaxDocuments (Some 5) "echoes the composed document cap"
            Expect.equal usage.MaxBytes (Some 10_000L) "echoes the composed byte cap"
            Expect.equal usage.DocumentsRemaining (Some 3) "headroom is cap minus current"
            Expect.equal usage.BytesRemaining (Some(10_000L - expectedBytes)) "byte headroom likewise"
        }

        testCaseAsync "GetScopeUsage distinguishes 'unlimited' from 'zero remaining'"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let deps = mkDeps storage KnowledgeQuotaPolicy.unlimited "team-usage-unlimited"
            let! _ = uploadDocument deps (csv "a") "a.csv"

            let! usage = getScopeUsage deps

            Expect.equal usage.DocumentCount 1 "counts are always honest"
            Expect.isNone usage.MaxDocuments "no cap composed"
            Expect.isNone usage.DocumentsRemaining "an unlimited quota has no headroom figure — not a zero one"
            Expect.isNone usage.BytesRemaining "likewise for bytes"
        }

        test "headroom floors at zero rather than going negative" {
            let policy = {
                MaxDocuments = Some 1
                MaxBytes = Some 10L
            }

            // A corpus that grew past a cap tightened afterwards.
            let docs = [ storedDoc "d1" (days 1.0) 50L; storedDoc "d2" (days 1.0) 50L ]

            let usage = KnowledgeScopeUsage.ofDocuments policy docs

            Expect.equal usage.DocumentsRemaining (Some 0) "over-cap reports no headroom, never a negative one"
            Expect.equal usage.BytesRemaining (Some 0L) "likewise for bytes"
        }

        // ─── 512.B — retention selection, pure ───────────────────────

        test "GP 11 — retainForever selects nothing, whatever the corpus age" {
            let docs = [ storedDoc "old" (days 3650.0) 1L; storedDoc "older" (days 36500.0) 1L ]

            Expect.isEmpty
                (KnowledgeRetentionPolicy.selectExpired
                    DateTimeOffset.UtcNow
                    KnowledgeRetentionPolicy.retainForever
                    docs)
                "no MaxAge means nothing is ever expired"

            Expect.isTrue
                (KnowledgeRetentionPolicy.isInert KnowledgeRetentionPolicy.retainForever)
                "the default is inert"
        }

        test "MaxAge selects only documents older than it" {
            let policy = {
                KnowledgeRetentionPolicy.retainForever with
                    MaxAge = Some(days 30.0)
            }

            let docs = [
                storedDoc "fresh" (days 5.0) 1L
                storedDoc "stale" (days 45.0) 1L
                storedDoc "edge" (days 29.9) 1L
            ]

            let expired =
                KnowledgeRetentionPolicy.selectExpired DateTimeOffset.UtcNow policy docs
                |> List.map _.Id

            Expect.equal expired [ "stale" ] "only the document past MaxAge is selected"
        }

        test "notes and narratives are spared unless explicitly opted in" {
            let baseline = {
                KnowledgeRetentionPolicy.retainForever with
                    MaxAge = Some(days 1.0)
            }

            let docs = [
                storedDoc "upload" (days 10.0) 1L
                storedDoc "note" (days 10.0) 1L |> withSource noteSource
                storedDoc "narrative" (days 10.0) 1L |> withSource narrativeSource
            ]

            let selected (p: KnowledgeRetentionPolicy) =
                KnowledgeRetentionPolicy.selectExpired DateTimeOffset.UtcNow p docs
                |> List.map _.Id

            Expect.equal
                (selected baseline)
                [ "upload" ]
                "hand-authored content does not vanish on a timer by default — only uploads age out"

            Expect.equal
                (selected { baseline with ExpireNotes = true })
                [ "upload"; "note" ]
                "ExpireNotes brings notes into scope"

            Expect.equal
                (selected {
                    baseline with
                        ExpireNotes = true
                        ExpireNarratives = true
                })
                [ "upload"; "note"; "narrative" ]
                "ExpireNarratives brings narratives into scope"
        }

        // ─── 512.B — the sweep ───────────────────────────────────────

        testCaseAsync "the sweep purges only expired documents and audits what it took"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let container = "team-sweep"
            let audit = CapturingAuditLog()

            let stale = storedDoc "stale" (days 90.0) 400L
            let fresh = storedDoc "fresh" (days 1.0) 100L
            do! saveIndex storage container [ stale; fresh ]

            // Raw blobs, so the purge's blob deletion is observable.
            let! _ = storage.Upload(container, sprintf "knowledge/%s/%s" stale.Id stale.FileName, csv "stale")
            let! _ = storage.Upload(container, sprintf "knowledge/%s/%s" fresh.Id fresh.FileName, csv "fresh")

            let policy = {
                KnowledgeRetentionPolicy.retainForever with
                    MaxAge = Some(days 30.0)
            }

            let! report =
                sweepScope storage None (Some(audit :> IAuditLog)) noopLogger DateTimeOffset.UtcNow policy "team-sweep"

            Expect.equal report.Purged [ "stale" ] "only the expired document was purged"
            Expect.equal report.ReclaimedBytes 400L "reclaimed bytes come from the purged document's SizeBytes"
            Expect.equal report.Examined 2 "the report names how much was considered"
            Expect.isTrue (RetentionSweepReport.isClean report) "a purge with no surviving chunks is clean"

            let! index = loadIndex storage container
            Expect.equal (index |> List.map _.Id) [ "fresh" ] "the unexpired document is untouched"

            let! staleBlobs = storage.List(container, sprintf "knowledge/%s" stale.Id)
            Expect.isEmpty staleBlobs "the purged document's raw blob is gone"

            let! freshBlobs = storage.List(container, sprintf "knowledge/%s" fresh.Id)
            Expect.isNonEmpty freshBlobs "the retained document's raw blob survives"

            match audit.Events with
            | [ (scopeId, KnowledgeDocumentsPurged payload) ] ->
                Expect.equal scopeId "team-sweep" "the row is recorded under the swept scope (GP 4)"
                Expect.equal payload.DocumentIds [ "stale" ] "the row carries the evidence, not just a count"
                Expect.equal payload.PurgedCount 1 "count is denormalised for sinks"
                Expect.equal payload.ReclaimedBytes 400L "reclaimed bytes are on the row"
                Expect.equal payload.OrphanChunkCount 0 "a clean fan-out reports no orphans"

                Expect.equal
                    payload.MaxAgeSeconds
                    (int64 (days 30.0).TotalSeconds)
                    "the row carries the policy that produced it"
            | other -> failtest $"expected exactly one KnowledgeDocumentsPurged row, got %A{other}"
        }

        testCaseAsync "GP 11 — an inert policy purges nothing and writes no audit row"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let container = "team-no-retention"
            let audit = CapturingAuditLog()

            let ancient = storedDoc "ancient" (days 3650.0) 999L
            do! saveIndex storage container [ ancient ]

            let! report =
                sweepScope
                    storage
                    None
                    (Some(audit :> IAuditLog))
                    noopLogger
                    DateTimeOffset.UtcNow
                    KnowledgeRetentionPolicy.retainForever
                    "team-no-retention"

            Expect.isEmpty report.Purged "retainForever purges nothing"
            Expect.equal report.Examined 0 "an inert policy short-circuits before it even reads the index"

            let! index = loadIndex storage container
            Expect.hasLength index 1 "the corpus is untouched"
            Expect.isEmpty audit.Events "a run that removed nothing writes no purge row"
        }

        testCaseAsync "a sweep that expires nothing writes no audit row"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let container = "team-nothing-due"
            let audit = CapturingAuditLog()
            do! saveIndex storage container [ storedDoc "fresh" (days 1.0) 10L ]

            let policy = {
                KnowledgeRetentionPolicy.retainForever with
                    MaxAge = Some(days 30.0)
            }

            let! report =
                sweepScope
                    storage
                    None
                    (Some(audit :> IAuditLog))
                    noopLogger
                    DateTimeOffset.UtcNow
                    policy
                    "team-nothing-due"

            Expect.isEmpty report.Purged "nothing was due"
            Expect.equal report.Examined 1 "but the corpus WAS examined — distinct from the inert short-circuit"
            Expect.isEmpty audit.Events "a purge trail records deletions, not the absence of them"
        }

        testCaseAsync "the sweep is idempotent — a second run finds nothing left to purge"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let container = "team-idempotent"
            let audit = CapturingAuditLog()
            do! saveIndex storage container [ storedDoc "stale" (days 90.0) 5L ]

            let policy = {
                KnowledgeRetentionPolicy.retainForever with
                    MaxAge = Some(days 30.0)
            }

            let sweep () =
                sweepScope
                    storage
                    None
                    (Some(audit :> IAuditLog))
                    noopLogger
                    DateTimeOffset.UtcNow
                    policy
                    "team-idempotent"

            let! first = sweep ()
            let! second = sweep ()

            Expect.equal first.Purged [ "stale" ] "the first run purges"
            Expect.isEmpty second.Purged "the second finds nothing"
            Expect.hasLength audit.Events 1 "and writes no second audit row"
        }

        test "scope ids map onto the KB container and vector scope conventions" {
            Expect.equal (containerOf "team-a") "team-a" "an already-prefixed team id passes through"
            Expect.equal (containerOf "user-b") "user-b" "as does a user id"
            Expect.equal (containerOf "bare") "team-bare" "a bare id is a team id — the tenant unit"

            Expect.equal (vectorScopeOf "team-a") (Team "a") "a team container maps to the team-shared vector scope"

            Expect.equal
                (vectorScopeOf "user-b")
                (User "b")
                "a user container maps to that user's OWN vector scope — never Deployment, which would reach another caller's chunks"
        }
    ]