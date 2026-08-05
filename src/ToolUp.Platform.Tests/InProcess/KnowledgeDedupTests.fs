module ToolUp.Platform.Tests.InProcess.KnowledgeDedupTests

open System
open System.Text
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 14x — KB document content-hash dedup ──────────────────────
//
// Pins the upload idempotency contract: re-uploading byte-identical
// content into the same scope returns the EXISTING document (same
// docId), persists nothing, skips ingestion, emits exactly one
// `KnowledgeDocumentDeduplicated` audit row, and surfaces an Info
// `SystemMessage`. Also pins:
//   * the O(1) hash ref under `_platform/kb-content-hash/{hash}/{id}.ref`
//     (Phase 9f `BlobIndex`), written on ingest and removed on delete;
//   * `withDocumentDedup false` → the pre-14x path byte-for-byte (two
//     documents, no hash stamped, no ref, no audit);
//   * a `Failed` document never dedups (re-upload is the retry path);
//   * a pre-14x `index.json` record (no `ContentHash` property)
//     deserialises with `ContentHash = None` — the upgrade never blanks
//     an existing KB index.

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Thread-safe capturing IAuditLog double (same shape as the Phase 107
/// pack's).
type private CapturingAuditLog() =
    let events = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Events = lock gate (fun () -> events |> List.ofSeq)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock gate (fun () -> events.Add(scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

/// Capturing INotificationChannel double for the Info-toast assertion.
type private CapturingNotifications() =
    let published = ResizeArray<string * Notification>()
    let gate = obj ()

    member _.Published = lock gate (fun () -> published |> List.ofSeq)

    interface INotificationChannel with
        member _.Publish(userId, notification) = async { lock gate (fun () -> published.Add(userId, notification)) }

        member _.Subscribe(_, _) =
            async.Return Unchecked.defaultof<NotificationSubscriptionId>

        member _.Unsubscribe _ = async.Return()

let private mkDeps
    (storage: IBlobStorage)
    (auditLog: CapturingAuditLog)
    (notifications: CapturingNotifications)
    (dedupPolicy: KnowledgeDedupPolicy)
    (container: string)
    : KnowledgeApiDeps =
    {
        Storage = storage
        Queue = IngestionQueue()
        OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
        TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
        Notifications = notifications
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
        EmbeddingProvider = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
        OriginalResolver = createDefault ()
        AuditLog = Some(auditLog :> IAuditLog)
        RecordEnqueue = ignore
        PublishInventory = fun () -> async.Return()
        MarkIngestionFailed = fun _ _ _ -> async.Return()
        EnsureContextWriteAllowed = fun () -> async { return Ok() }
        ScopeResolvedFromRequest = true
        UploadPolicy = KnowledgeUploadPolicy.permissive
        DedupPolicy = dedupPolicy
        // Phase 510 — dedup is asserted independently of versioning, so
        // this pack keeps the pre-510 upload path.
        VersioningPolicy = KnowledgeVersioningPolicy.disabled
        // Phase 105 — retention not composed: the pre-105 convention-blob path.
        DataObjectStore = None
        // Phase 512 — these packs pin pre-512 paths; the unlimited /
        // retain-forever defaults keep them byte-identical.
        QuotaPolicy = KnowledgeQuotaPolicy.unlimited
        RetentionPolicy = KnowledgeRetentionPolicy.retainForever
        // Phase 515 — no scanner composed: the pre-515 upload path.
        ContentScanner = None
        ScanPolicy = ContentScanPolicy.defaults
        DisclosureGate = None
        // Phase 511 — bulk import is not exercised here: archive guards at
        // their shipped defaults, URL ingestion inert, no transport.
        ArchiveImportPolicy = ArchiveImportPolicy.defaults
        UrlIngestionPolicy = UrlIngestionPolicy.disabled
        UrlFetcher = None
    }

let private csvBytes = Encoding.UTF8.GetBytes "name,score\nalpha,1\nbeta,2"

/// Same identity the implementation computes — lowercase SHA-256 hex of
/// the raw bytes.
let private hashOf (bytes: byte[]) =
    SHA256.HashData bytes |> Convert.ToHexStringLower

let private dedupEvents (audit: CapturingAuditLog) =
    audit.Events
    |> List.choose (fun (_, e) ->
        match e with
        | KnowledgeDocumentDeduplicated p -> Some p
        | _ -> None)

let tests =
    testList "Phase 14x — KB content-hash dedup" [

        testCaseAsync "re-uploading identical bytes returns the existing docId, persists nothing new, audits once"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let notifications = CapturingNotifications()

            let deps =
                mkDeps storage audit notifications KnowledgeDedupPolicy.enabled "team-dedup-a"

            let! first = uploadDocument deps csvBytes "scores.csv"
            let! second = uploadDocument deps csvBytes "scores-copy.csv"

            Expect.equal second.Id first.Id "the second upload returns the existing document (same docId)"

            let! idx = loadIndex storage "team-dedup-a"
            Expect.equal (idx |> List.map _.Id) [ first.Id ] "exactly one KnowledgeDocument in the scope"

            Expect.equal
                (idx |> List.head |> _.ContentHash)
                (Some(hashOf csvBytes))
                "the stored document carries the content hash"

            match dedupEvents audit with
            | [ p ] ->
                Expect.equal p.ExistingDocumentId first.Id "audit points at the existing document"
                Expect.equal p.FileName "scores-copy.csv" "audit records the attempted upload's name"
                Expect.equal p.ContentHash (hashOf csvBytes) "audit carries the hash identifier"
            | other -> failtest $"expected exactly one KnowledgeDocumentDeduplicated, got %A{other}"

            let toasts =
                notifications.Published
                |> List.choose (fun (_, n) ->
                    match n with
                    | SystemMessage(SystemMessageLevel.Info, text) -> Some text
                    | _ -> None)

            Expect.hasLength toasts 1 "the dedup decision surfaces exactly one Info SystemMessage"
            Expect.stringContains (List.head toasts) "already exists" "the toast says the document already exists"
        }

        testCaseAsync "the content hash is registered under _platform/kb-content-hash for O(1) lookup"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let notifications = CapturingNotifications()

            let deps =
                mkDeps storage audit notifications KnowledgeDedupPolicy.enabled "team-dedup-b"

            let! doc = uploadDocument deps csvBytes "scores.csv"

            let expectedPrefix = sprintf "_platform/kb-content-hash/%s/" (hashOf csvBytes)
            let! refs = storage.List("team-dedup-b", expectedPrefix)

            Expect.hasLength refs 1 "exactly one hash ref for the document"

            Expect.stringContains
                (List.head refs)
                doc.Id
                "the ref leaf is the docId (hash → docId, one List call to resolve)"
        }

        testCaseAsync "withDocumentDedup false restores the pre-14x path: two documents, no hash, no ref, no audit"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let notifications = CapturingNotifications()

            let deps =
                mkDeps storage audit notifications KnowledgeDedupPolicy.disabled "team-dedup-c"

            let! first = uploadDocument deps csvBytes "scores.csv"
            let! second = uploadDocument deps csvBytes "scores.csv"

            Expect.notEqual second.Id first.Id "opt-out: every upload ingests as its own document"

            let! idx = loadIndex storage "team-dedup-c"
            Expect.hasLength idx 2 "both uploads are in the index"
            Expect.all idx (fun d -> d.ContentHash = None) "opt-out stamps no hash (byte-for-byte legacy shape)"

            let! refs = storage.List("team-dedup-c", "_platform/kb-content-hash/")
            Expect.isEmpty refs "opt-out writes no hash refs"
            Expect.isEmpty (dedupEvents audit) "opt-out emits no dedup audit"
        }

        testCaseAsync "deleting a document removes its hash ref, so re-uploading the same bytes re-ingests fresh"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let notifications = CapturingNotifications()

            let deps =
                mkDeps storage audit notifications KnowledgeDedupPolicy.enabled "team-dedup-d"

            let! first = uploadDocument deps csvBytes "scores.csv"

            let! deleted = deleteDocument deps first.Id
            Expect.isOk deleted "delete succeeds"

            let hashPrefix = sprintf "_platform/kb-content-hash/%s/" (hashOf csvBytes)
            let! refsAfterDelete = storage.List("team-dedup-d", hashPrefix)
            Expect.isEmpty refsAfterDelete "the hash ref is removed with the document"

            let! reupload = uploadDocument deps csvBytes "scores.csv"
            Expect.notEqual reupload.Id first.Id "the re-upload is a fresh ingest, not the deleted docId"

            Expect.isEmpty (dedupEvents audit) "no dedup fired across the delete boundary"
        }

        testCaseAsync "a Failed document never dedups — re-uploading the same bytes is the retry path"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let audit = CapturingAuditLog()
            let notifications = CapturingNotifications()

            let deps =
                mkDeps storage audit notifications KnowledgeDedupPolicy.enabled "team-dedup-e"

            // Seed a Failed document carrying the hash + its index ref —
            // the state a crashed / queue-full ingestion leaves behind.
            let hash = hashOf csvBytes

            let failedDoc: KnowledgeDocument = {
                Id = "doc-failed"
                FileName = "scores.csv"
                FileType = "csv"
                UploadedAt = DateTimeOffset.UtcNow
                UploadedBy = "user-1"
                Status = Failed "ingestion queue full"
                SizeBytes = int64 csvBytes.Length
                ChunkCount = 0
                Source = UploadedFile
                ContentHash = Some hash
                Version = 1
                // Phase 502.C — untagged fixture.
                Tags = []
            }

            do! upsertIndexEntry storage "team-dedup-e" failedDoc

            let refName = sprintf "_platform/kb-content-hash/%s/%s.ref" hash failedDoc.Id
            let! _ = storage.Upload("team-dedup-e", refName, Array.empty)

            let! retried = uploadDocument deps csvBytes "scores.csv"

            Expect.notEqual retried.Id failedDoc.Id "the retry ingests a fresh document"
            Expect.isEmpty (dedupEvents audit) "a Failed match never counts as a duplicate"
        }

        testCaseAsync "a pre-14x index record (no ContentHash property) loads with ContentHash = None"
        <| async {
            // `loadIndex` swallows deserialisation failures into an empty
            // list, so a non-lenient reader would silently blank existing
            // KBs on upgrade — this pin is the guard against that.
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let legacyJson =
                """[{"Id":"doc-legacy","FileName":"old.pdf","FileType":"pdf","UploadedAt":"2026-01-01T00:00:00+00:00","UploadedBy":"user-1","Status":{"Complete":3},"SizeBytes":10,"ChunkCount":3,"Source":"UploadedFile"}]"""

            let! _ = storage.Upload("team-dedup-f", "knowledge/index.json", Encoding.UTF8.GetBytes legacyJson)

            let! idx = loadIndex storage "team-dedup-f"

            match idx with
            | [ doc ] ->
                Expect.equal doc.Id "doc-legacy" "the legacy record loads"
                Expect.equal doc.ContentHash None "the missing property absorbs leniently to None"
            | other -> failtest $"expected the single legacy document, got %A{other}"
        }
    ]