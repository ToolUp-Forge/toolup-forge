// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ConversationRetentionTests

open System
open System.Text
open System.Text.Json
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.AIAssistantHandler
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 504 — conversation retention / TTL policy ─────────────
//
// 504.A — `ConversationRetentionPolicy.selectExpired` is the pure
//         selection the sweep runs on: age, count, both, neither.
// 504.B — `ConversationRetention.sweepContainer` purges the selected
//         conversations' EVERY sibling blob and nothing else, writes one
//         `ConversationsPurged` audit row (decodable through the
//         production codec registry), and purges nothing under an inert
//         policy. The `IJobHandler` resolves its substrate from DI.
// 504.C — a deleted Knowledge Base note leaves no residual `note.md`,
//         through `deleteDocument` AND the KB retention sweep. The
//         premise is pinned by a test that proves a note's `FileName`
//         does NOT resolve to the body blob — the defect was real.
// 504.D — erasure (`DeleteConversation`) and the purge share ONE
//         sibling set, which names the consent sidecar.

let private jsonOptions = FableConverters.create ()

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

let private auditLog () =
    let store = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    AuditLog.EventStoreAuditLog(store, silentLogger) :> IAuditLog

let private container = "team-retention"
let private scopeId = "team-retention"

let private message (conversationId: Guid) (at: DateTime) : ConversationMessage = {
    Id = Guid.NewGuid()
    ConversationId = conversationId
    Participant = User
    Content = "hello"
    Timestamp = at
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = "alice"
    BeaconId = ""
    Verification = None
}

/// Write every sibling blob a conversation owns, dating it by the
/// newest message.
let private seedConversation (storage: IBlobStorage) (id: Guid) (lastActivity: DateTime) = async {
    let messages = [ message id (lastActivity.AddMinutes -5.0); message id lastActivity ]

    let bytes =
        JsonSerializer.Serialize(messages, jsonOptions) |> Encoding.UTF8.GetBytes

    let! _ = storage.Upload(container, $"ai-conversations/{id}.json", bytes)
    let! _ = storage.Upload(container, $"ai-conversations/{id}.history.json", Encoding.UTF8.GetBytes "[]")
    let! _ = storage.Upload(container, $"ai-conversations/{id}.meta.json", Encoding.UTF8.GetBytes "{}")
    let! _ = storage.Upload(container, $"ai-conversations/{id}.consent.json", Encoding.UTF8.GetBytes "{}")
    ()
}

let private siblingsPresent (storage: IBlobStorage) (id: Guid) = async {
    let! present =
        ConversationRetention.siblingBlobNames id
        |> List.map (fun name -> storage.Exists(container, name))
        |> Async.Sequential

    return present |> Array.toList
}

let private now = DateTime(2026, 9, 26, 12, 0, 0, DateTimeKind.Utc)

let private agePolicy days = {
    ConversationRetentionPolicy.retainForever with
        MaxAge = Some(TimeSpan.FromDays(float days))
}

// ─── KB fixtures (504.C) ─────────────────────────────────────────

let private noopNotifications = Unchecked.defaultof<INotificationChannel>

let private kbDeps (storage: IBlobStorage) : KnowledgeApiDeps = {
    Storage = storage
    Queue = ToolUp.RAG.IngestionTypes.IngestionQueue()
    OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
    TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
    Notifications = noopNotifications
    Logger = silentLogger
    Scope = {
        ScopeId = container
        Container = container
        Persist = true
    }
    UserId = "user-1"
    VectorScope = ToolUp.Platform.VectorKnowledgeTypes.Deployment
    VectorStore = None
    IndexLifecycle = None
    EventStore = None
    EmbeddingProvider = None
    NarrativeStore = None
    AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
    OriginalResolver = KnowledgeBase.ServerOriginalSourceResolver.createDefault ()
    AuditLog = None
    RecordEnqueue = ignore
    PublishInventory = fun () -> async.Return()
    MarkIngestionFailed = fun _ _ _ -> async.Return()
    EnsureContextWriteAllowed = fun () -> async { return Ok() }
    ScopeResolvedFromRequest = true
    UploadPolicy = KnowledgeUploadPolicy.permissive
    DedupPolicy = KnowledgeDedupPolicy.enabled
    VersioningPolicy = KnowledgeVersioningPolicy.disabled
    DataObjectStore = None
    QuotaPolicy = KnowledgeQuotaPolicy.unlimited
    RetentionPolicy = KnowledgeRetentionPolicy.retainForever
    ContentScanner = None
    ScanPolicy = ContentScanPolicy.defaults
    DisclosureGate = None
    ArchiveImportPolicy = ArchiveImportPolicy.defaults
    UrlIngestionPolicy = UrlIngestionPolicy.disabled
    UrlFetcher = None
}

let private kbDoc
    (docId: string)
    (fileName: string)
    (source: KnowledgeSource)
    (uploadedAt: DateTimeOffset)
    : KnowledgeDocument =
    {
        Id = docId
        FileName = fileName
        FileType = "md"
        UploadedAt = uploadedAt
        UploadedBy = "user-1"
        Status = Complete 1
        SizeBytes = 0L
        ChunkCount = 1
        Source = source
        ContentHash = None
        Version = 1
        Tags = []
    }

let private noteSource: NoteSource = {
    Title = "Quarterly plan"
    Author = "user-1"
    CreatedAt = DateTimeOffset.UtcNow
    LastEditedAt = None
}

/// A note exactly as `addNote` leaves it: `FileName = "{title}.md"`, an
/// index entry, and the body at `knowledge/{id}/note.md`.
let private seedNote (storage: IBlobStorage) (docId: string) (uploadedAt: DateTimeOffset) = async {
    let doc = kbDoc docId "Quarterly plan.md" (Note noteSource) uploadedAt
    do! upsertIndexEntry storage container doc
    let! _ = storage.Upload(container, noteBodyBlobName docId, Encoding.UTF8.GetBytes "# plan\n\nbody")
    return doc
}

let tests =
    testList "Phase 504 — conversation retention / TTL policy" [
        testList "504.A selectExpired" [
            testCase "no policy ⇒ nothing selected, whatever the data"
            <| fun () ->
                let a, b = Guid.NewGuid(), Guid.NewGuid()

                let expired =
                    ConversationRetentionPolicy.selectExpired now ConversationRetentionPolicy.retainForever [
                        a, now.AddYears -10
                        b, now.AddDays -400.0
                    ]

                Expect.isEmpty expired "retainForever selects nothing"
                Expect.isTrue (ConversationRetentionPolicy.isInert ConversationRetentionPolicy.retainForever) "inert"

            testCase "age: strictly older than MaxAge, at-the-limit kept, oldest first"
            <| fun () ->
                let old, atLimit, fresh = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()
                let older = Guid.NewGuid()

                let expired =
                    ConversationRetentionPolicy.selectExpired now (agePolicy 30) [
                        fresh, now.AddDays -1.0
                        old, now.AddDays -31.0
                        atLimit, now.AddDays -30.0
                        older, now.AddDays -90.0
                    ]

                Expect.equal expired [ older; old ] "the two past the limit, oldest first; at-the-limit kept"

            testCase "count: keeps the MaxCount newest by activity, ties broken by id"
            <| fun () ->
                let ids = List.init 5 (fun _ -> Guid.NewGuid())
                let dated = ids |> List.mapi (fun i id -> id, now.AddDays(float -i))

                let expired =
                    ConversationRetentionPolicy.selectExpired
                        now
                        {
                            ConversationRetentionPolicy.retainForever with
                                MaxCount = Some 2
                        }
                        dated

                Expect.equal expired (ids |> List.skip 2 |> List.rev) "the three least-recent, oldest first"

                // Ties: same instant, the id ordering decides, and both
                // orderings of the input agree.
                let t1, t2 =
                    Guid.Parse "00000000-0000-0000-0000-000000000001", Guid.Parse "00000000-0000-0000-0000-000000000002"

                let policy = {
                    ConversationRetentionPolicy.retainForever with
                        MaxCount = Some 1
                }

                let one = ConversationRetentionPolicy.selectExpired now policy [ t1, now; t2, now ]
                let two = ConversationRetentionPolicy.selectExpired now policy [ t2, now; t1, now ]
                Expect.equal one two "input order does not change the selection"
                Expect.equal one [ t2 ] "the higher id is the one past the count"

            testCase "count: MaxCount at or below zero keeps none; a count above the size keeps all"
            <| fun () ->
                let a, b = Guid.NewGuid(), Guid.NewGuid()
                let dated = [ a, now; b, now.AddDays -1.0 ]

                let none =
                    ConversationRetentionPolicy.selectExpired
                        now
                        {
                            ConversationRetentionPolicy.retainForever with
                                MaxCount = Some 0
                        }
                        dated

                Expect.equal none [ b; a ] "zero keeps nothing"

                let all =
                    ConversationRetentionPolicy.selectExpired
                        now
                        {
                            ConversationRetentionPolicy.retainForever with
                                MaxCount = Some 10
                        }
                        dated

                Expect.isEmpty all "a count above the size selects nothing (no List.skip overrun)"

            testCase "both: the union, each id once"
            <| fun () ->
                let a, b, c = Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()

                let expired =
                    ConversationRetentionPolicy.selectExpired
                        now
                        {
                            ConversationRetentionPolicy.retainForever with
                                MaxAge = Some(TimeSpan.FromDays 10.0)
                                MaxCount = Some 1
                        }
                        [ a, now; b, now.AddDays -5.0; c, now.AddDays -50.0 ]

                Expect.equal expired [ c; b ] "c by age (and count), b by count only; a kept"
        ]

        testList "504.B sweepContainer" [
            testCaseAsync "age expiry purges every sibling of the expired conversation and nothing else"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let audit = auditLog ()
                let stale, fresh = Guid.NewGuid(), Guid.NewGuid()
                do! seedConversation storage stale (now.AddDays -40.0)
                do! seedConversation storage fresh (now.AddDays -2.0)

                let! report =
                    ConversationRetention.sweepContainer
                        storage
                        (Some audit)
                        silentLogger
                        now
                        (agePolicy 30)
                        scopeId
                        container

                Expect.equal report.Examined 2 "both conversations enumerated"
                Expect.equal report.Purged [ stale ] "only the stale one purged"
                Expect.isEmpty report.Failures "clean"

                let! stalePresent = siblingsPresent storage stale

                Expect.equal
                    stalePresent
                    [ false; false; false; false ]
                    "all four siblings of the stale conversation are gone"

                let! freshPresent = siblingsPresent storage fresh
                Expect.equal freshPresent [ true; true; true; true ] "the fresh conversation is untouched"

                let! trail = audit.GetAuditTrail(scopeId, None, Some "ConversationsPurged")

                match trail with
                | [ ConversationsPurged p ] ->
                    Expect.equal p.ScopeId scopeId "scope"
                    Expect.equal p.ConversationIds [ string stale ] "the purged ids are the evidence"
                    Expect.equal p.PurgedCount 1 "count"
                    Expect.equal p.MaxAgeSeconds (Some(30L * 86400L)) "the policy rides the row"
                    Expect.equal p.MaxCount None "no count limit"
                    Expect.equal p.FailedCount 0 "nothing failed"
                | other -> failtestf "expected exactly one ConversationsPurged row; got %A" other
            }

            testCaseAsync "count expiry keeps the newest and purges the rest"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let ids = List.init 4 (fun _ -> Guid.NewGuid())

                for i, id in List.indexed ids do
                    do! seedConversation storage id (now.AddDays(float -i))

                let policy = {
                    ConversationRetentionPolicy.retainForever with
                        MaxCount = Some 2
                }

                let! report =
                    ConversationRetention.sweepContainer storage None silentLogger now policy scopeId container

                Expect.equal (Set.ofList report.Purged) (Set.ofList (List.skip 2 ids)) "the two least-recent purged"

                let! kept = ConversationRetention.listConversationIds storage container
                Expect.equal (Set.ofList kept) (Set.ofList (List.take 2 ids)) "the two newest remain"
            }

            testCaseAsync "no policy ⇒ nothing purged, nothing read, no audit row"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let audit = auditLog ()
                let ancient = Guid.NewGuid()
                do! seedConversation storage ancient (now.AddYears -5)

                let! report =
                    ConversationRetention.sweepContainer
                        storage
                        (Some audit)
                        silentLogger
                        now
                        ConversationRetentionPolicy.retainForever
                        scopeId
                        container

                Expect.equal report.Examined 0 "an inert policy enumerates nothing"
                Expect.isEmpty report.Purged "nothing purged"

                let! present = siblingsPresent storage ancient
                Expect.equal present [ true; true; true; true ] "every blob still there"

                let! trail = audit.GetAuditTrail(scopeId, None, None)
                Expect.isEmpty trail "no audit row for a run that removed nothing"
            }

            testCaseAsync "a run that expires nothing writes no audit row"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let audit = auditLog ()
                do! seedConversation storage (Guid.NewGuid()) (now.AddDays -1.0)

                let! report =
                    ConversationRetention.sweepContainer
                        storage
                        (Some audit)
                        silentLogger
                        now
                        (agePolicy 30)
                        scopeId
                        container

                Expect.equal report.Examined 1 "enumerated"
                Expect.isEmpty report.Purged "nothing expired"
                let! trail = audit.GetAuditTrail(scopeId, None, None)
                Expect.isEmpty trail "no row"
            }

            testCaseAsync "an empty conversation is dated by its blob write time, not skipped"
            <| async {
                // The in-memory store stamps `LastModified = UtcNow`, so an
                // empty conversation reads as fresh: it must be examined
                // and kept, never purged by an undated-defaults-to-zero
                // path.
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let empty = Guid.NewGuid()
                let! _ = storage.Upload(container, $"ai-conversations/{empty}.json", Encoding.UTF8.GetBytes "[]")

                let! dated = ConversationRetention.lastActivity silentLogger storage container empty
                Expect.isSome dated "dated by metadata"

                let! report =
                    ConversationRetention.sweepContainer
                        storage
                        None
                        silentLogger
                        DateTime.UtcNow
                        (agePolicy 30)
                        scopeId
                        container

                Expect.equal report.Examined 1 "examined"
                Expect.isEmpty report.Purged "a just-written empty conversation is not expired"
            }

            testCaseAsync "the job handler resolves storage + audit from DI and sweeps its scope"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let audit = auditLog ()
                let stale = Guid.NewGuid()
                do! seedConversation storage stale (DateTime.UtcNow.AddDays -40.0)

                let services = ServiceCollection()
                services.AddSingleton<IBlobStorage>(storage) |> ignore
                services.AddSingleton<IAuditLog>(audit) |> ignore
                services.AddSingleton<ILogger>(silentLogger) |> ignore
                let provider = services.BuildServiceProvider()

                let handler =
                    ConversationRetentionSweepJobHandler(provider, agePolicy 30) :> IJobHandler

                let ctx: JobContext = {
                    JobId = Guid.NewGuid()
                    ScopeId = scopeId
                    AccessContext = AccessContext.unrestricted (AuthenticatedUser "_system")
                    Attempt = 1
                    Trigger = Manual
                    TriggerSource = TriggerSource.ScheduledManually "_system"
                    ScheduledAt = DateTime.UtcNow
                    RunningAt = DateTime.UtcNow
                    Payload = ""
                    DeadLetterDestination = None
                }

                let! result = handler.Execute ctx
                Expect.equal result Success "clean sweep"

                let! present = siblingsPresent storage stale
                Expect.equal present [ false; false; false; false ] "purged through the handler"

                let! trail = audit.GetAuditTrail(scopeId, None, Some "ConversationsPurged")
                Expect.equal trail.Length 1 "audited"

                // No storage composed ⇒ permanent, not transient.
                let bare = ServiceCollection().BuildServiceProvider()
                let! noStore = (ConversationRetentionSweepJobHandler(bare, agePolicy 30) :> IJobHandler).Execute ctx

                match noStore with
                | PermanentFailure _ -> ()
                | other -> failtestf "expected PermanentFailure without IBlobStorage; got %A" other
            }

            testCase "containerOf mirrors the team-/user- convention"
            <| fun () ->
                Expect.equal (ConversationRetention.containerOf "team-a") "team-a" "prefixed team"
                Expect.equal (ConversationRetention.containerOf "user-bob") "user-bob" "prefixed user"
                Expect.equal (ConversationRetention.containerOf "acme") "team-acme" "bare id is a team"
        ]

        testList "504.D erasure and purge share one sibling set" [
            testCaseAsync "deleteSiblings removes the UI blob, provider history, meta AND the consent sidecar"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let id = Guid.NewGuid()
                do! seedConversation storage id now

                Expect.contains
                    (ConversationRetention.siblingBlobNames id)
                    (AIConsentDispatch.consentBlobName id)
                    "the consent sidecar is in the shared set"

                let! r = ConversationRetention.deleteSiblings storage container id
                Expect.isOk r "clean"

                let! remaining = storage.List(container, "ai-conversations/")
                Expect.isEmpty remaining "nothing retrievable remains"

                // Idempotent — a second erasure of a gone conversation is Ok.
                let! again = ConversationRetention.deleteSiblings storage container id
                Expect.isOk again "idempotent"
            }
        ]

        testList "504.C KB note erasure" [
            testCase "premise: a note's FileName does NOT resolve to its body blob"
            <| fun () ->
                // The convention original path is `knowledge/{id}/{FileName}`
                // and `addNote` sets `FileName = "{title}.md"`, while the body
                // lives at `noteBodyBlobName`. Pinned so the fix cannot be
                // "simplified" back to the unconditional convention delete.
                let docId = "doc-1"

                Expect.notEqual
                    (sprintf "knowledge/%s/%s" docId "Quarterly plan.md")
                    (noteBodyBlobName docId)
                    "different blobs"

                Expect.equal (noteBodyBlobName docId) "knowledge/doc-1/note.md" "the body path"

            testCaseAsync "deleteDocument removes a note's body blob, not just its index entry"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let docId = Guid.NewGuid().ToString()
                let! _ = seedNote storage docId DateTimeOffset.UtcNow

                let! before = storage.Exists(container, noteBodyBlobName docId)
                Expect.isTrue before "seeded"

                let! result = KnowledgeBase.ServerApiDocuments.deleteDocument (kbDeps storage) docId
                Expect.isOk result "deleted"

                let! after = storage.Exists(container, noteBodyBlobName docId)
                Expect.isFalse after "the note body is gone"

                let! index = loadIndex storage container
                Expect.isEmpty index "the index entry is gone too"

                let! residue = storage.List(container, sprintf "knowledge/%s/" docId)
                Expect.isEmpty residue "no residual blob under the document"
            }

            testCaseAsync "deleteDocument still removes an uploaded file's original at the convention path"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let docId = Guid.NewGuid().ToString()
                let doc = kbDoc docId "report.pdf" UploadedFile DateTimeOffset.UtcNow
                do! upsertIndexEntry storage container doc
                let! _ = storage.Upload(container, sprintf "knowledge/%s/report.pdf" docId, [| 1uy; 2uy |])

                let! result = KnowledgeBase.ServerApiDocuments.deleteDocument (kbDeps storage) docId
                Expect.isOk result "deleted"

                let! residue = storage.List(container, sprintf "knowledge/%s/" docId)
                Expect.isEmpty residue "the original is gone"
            }

            testCaseAsync "the KB retention sweep removes an expired note's body too"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let docId = Guid.NewGuid().ToString()
                let sweepNow = DateTimeOffset.UtcNow
                let! _ = seedNote storage docId (sweepNow.AddDays -100.0)

                let policy = {
                    KnowledgeRetentionPolicy.retainForever with
                        MaxAge = Some(TimeSpan.FromDays 30.0)
                        ExpireNotes = true
                }

                let! report =
                    KnowledgeBase.ServerRetentionSweep.sweepScope storage None None silentLogger sweepNow policy scopeId

                Expect.equal report.Purged [ docId ] "the note was selected and purged"

                let! after = storage.Exists(container, noteBodyBlobName docId)
                Expect.isFalse after "the note body is gone after the sweep"
            }
        ]
    ]