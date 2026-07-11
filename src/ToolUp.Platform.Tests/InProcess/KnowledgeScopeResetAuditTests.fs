module ToolUp.Platform.Tests.InProcess.KnowledgeScopeResetAuditTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.RAG.IngestionTypes
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiNarrative
open KnowledgeBase.ServerOriginalSourceResolver
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 115 — KnowledgeScopeErased audit on scope-level reset ──────
//
// `resetIndex` wipes a whole KB scope, fanning the deletion across every
// retrieval index via `IIndexLifecycle`. The dispatcher already emits a
// generic `Custom:KnowledgeIndexReset` action row (who reset); this pack
// pins the *structured* `KnowledgeScopeErased` row this phase adds —
// carrying the erasure outcome (document count + surviving-chunk count)
// so a half-completed fan-out is loud in the audit trail (GP 6 + GP 9),
// and registered via the Phase 114 codec registry.

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

/// Thread-safe capturing IAuditLog double.
type private CapturingAuditLog() =
    let events = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Events = lock gate (fun () -> events |> List.ofSeq)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock gate (fun () -> events.Add(scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private mkDoc (docId: string) : KnowledgeDocument = {
    Id = docId
    FileName = docId + ".pdf"
    FileType = "pdf"
    UploadedAt = DateTimeOffset.UtcNow
    UploadedBy = "user-1"
    Status = Complete 1
    SizeBytes = 0L
    ChunkCount = 1
    Source = UploadedFile
    ContentHash = None
}

let private chunk content : TextChunk = {
    Content = content
    Metadata = Map.empty
}

let private mkDeps
    (storage: IBlobStorage)
    (auditLog: IAuditLog option)
    (lifecycle: ToolUp.Platform.IIndexLifecycle.IIndexLifecycle option)
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
        IndexLifecycle = lifecycle
        EventStore = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
        OriginalResolver = createDefault ()
        AuditLog = auditLog
        RecordEnqueue = ignore
        PublishInventory = fun () -> async.Return()
        MarkIngestionFailed = fun _ _ _ -> async.Return()
        EnsureContextWriteAllowed = fun () -> async { return Ok() }
        ScopeResolvedFromRequest = true
        UploadPolicy = KnowledgeUploadPolicy.permissive
        DedupPolicy = KnowledgeDedupPolicy.enabled
        DisclosureGate = None
    }

let tests =
    testList "Phase 115 — KnowledgeScopeErased reset audit" [

        testCaseAsync "resetIndex emits a structured KnowledgeScopeErased row with a clean fan-out outcome"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
            use bm25 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)
            let vs = vectorStore :> IVectorStore
            let sparse = bm25 :> ISparseIndex

            let lifecycle =
                ToolUp.Platform.IIndexLifecycle.DefaultIndexLifecycle(vs, Some sparse, None)
                :> ToolUp.Platform.IIndexLifecycle.IIndexLifecycle

            // Two documents in the scope, each with a chunk in both retrieval legs.
            do! saveIndex storage "team-a" [ mkDoc "doc-1"; mkDoc "doc-2" ]
            do! vs.Upsert Deployment "doc-1:chunk:0" [| 1.0f; 0.0f |] (chunk "alpha")
            do! vs.Upsert Deployment "doc-2:chunk:0" [| 0.0f; 1.0f |] (chunk "beta")
            do! sparse.Upsert Deployment "doc-1:chunk:0" (chunk "alpha")
            do! sparse.Upsert Deployment "doc-2:chunk:0" (chunk "beta")

            let audit = CapturingAuditLog()
            let deps = mkDeps storage (Some(audit :> IAuditLog)) (Some lifecycle) "team-a"

            let! result = resetIndex deps
            Expect.isOk result "reset must succeed"

            match audit.Events with
            | [ (scopeId, KnowledgeScopeErased p) ] ->
                Expect.equal scopeId "team-a" "recorded under the caller's scope"
                Expect.equal p.UserId "user-1" "actor"
                Expect.equal p.ScopeId "team-a" "payload scope"
                Expect.equal p.DocumentCount 2 "document count reflects the wiped scope"
                Expect.equal p.OrphanChunkCount 0 "a clean fan-out leaves no surviving chunks"
            | other -> failtest $"expected exactly one KnowledgeScopeErased, got %A{other}"
        }

        testCaseAsync "reset with no IAuditLog substrate still succeeds silently"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            use vectorStore = new InMemoryVectorStore(storage, flushIntervalMs = 60000)
            use bm25 = new InMemoryBM25Index(storage, flushIntervalMs = 60000)

            let lifecycle =
                ToolUp.Platform.IIndexLifecycle.DefaultIndexLifecycle(
                    vectorStore :> IVectorStore,
                    Some(bm25 :> ISparseIndex),
                    None
                )
                :> ToolUp.Platform.IIndexLifecycle.IIndexLifecycle

            do! saveIndex storage "team-a" [ mkDoc "doc-1" ]
            let deps = mkDeps storage None (Some lifecycle) "team-a"

            let! result = resetIndex deps
            Expect.isOk result "absent IAuditLog never fails the reset"
        }
    ]