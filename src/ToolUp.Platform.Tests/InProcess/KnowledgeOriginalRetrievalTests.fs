module ToolUp.Platform.Tests.InProcess.KnowledgeOriginalRetrievalTests

open System
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.TeamManagement
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.RAG.IngestionTypes
open ToolUp.Remoting.Json.SystemTextJson
open SharedTypes
open KnowledgeBase.ServerIndexStorage
open KnowledgeBase.ServerOriginalSourceResolver
open KnowledgeBase.ServerApiDeps
open KnowledgeBase.ServerApiDocuments
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Wave 15 — KB original-document retrieval ────────────────────────
//
// Phase 104 — `IOriginalSourceResolver` contract pack: one branch per
//   `KnowledgeSource` case (UploadedFile → bytes + content type, Note →
//   markdown, FromNarrative → None; missing blob → None, never a throw).
// Phase 102 — `getOriginalDocument` handler: scope-gated fetch with
//   typed `NotInScope` / `NoOriginalAvailable` refusals, structural
//   cross-container isolation, resolver swappability.
// Phase 107 — `KnowledgeOriginalRetrieved` + denial audit emission on
//   every fetch / refusal path (identifiers only, no content).
// Phase 103 / 106 — `_originalRef` chunk-metadata stamp through the
//   real upload path, `RAGPromptBuilder.toRetrievedSource` projection
//   onto `RetrievedSource.OriginalRef`, neutral `SourceLocator`
//   mapping, and the pre-`OriginalRef` wire backward-compat pin.

let private opts = FableConverters.shared

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Thread-safe capturing IAuditLog double for the Phase 107 assertions.
type private CapturingAuditLog() =
    let events = ResizeArray<string * AuditEvent>()
    let gate = obj ()

    member _.Events = lock gate (fun () -> events |> List.ofSeq)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { lock gate (fun () -> events.Add(scopeId, audit)) }
        member _.GetAuditTrail(_, _, _) = async { return [] }

let private mkDoc (docId: string) (fileName: string) (fileType: string) (source: KnowledgeSource) : KnowledgeDocument = {
    Id = docId
    FileName = fileName
    FileType = fileType
    UploadedAt = DateTimeOffset.UtcNow
    UploadedBy = "user-1"
    Status = Complete 1
    SizeBytes = 0L
    ChunkCount = 1
    Source = source
}

let private mkDeps
    (storage: IBlobStorage)
    (auditLog: IAuditLog option)
    (resolver: IOriginalSourceResolver)
    (queue: IngestionQueue)
    (container: string)
    : KnowledgeApiDeps =
    {
        Storage = storage
        Queue = queue
        OcrProvider = ToolUp.RAG.NoOpDocUnderstanding.createOcrProvider ()
        TableExtractor = ToolUp.RAG.NoOpDocUnderstanding.createTableExtractor ()
        Notifications = Unchecked.defaultof<INotificationChannel>
        Logger = noopLogger
        Scope = {
            ScopeId = container
            Container = container
            Persist = true
        }
        UserId = "user-1"
        VectorScope = Deployment
        VectorStore = None
        EventStore = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
        OriginalResolver = resolver
        AuditLog = auditLog
        RecordEnqueue = ignore
        PublishInventory = fun () -> async.Return()
        MarkIngestionFailed = fun _ _ _ -> async.Return()
        EnsureContextWriteAllowed = fun () -> async { return Ok() }
    }

let private narrativeSource: NarrativeDocSource = {
    ModuleId = "analysis"
    PageRoute = Some "/analysis/overview"
    SettingsKey = "default"
    SettingsDisplay = []
    GeneratedAt = DateTimeOffset.UtcNow
}

let private noteSource: NoteSource = {
    Title = "Conventions"
    Author = "user-1"
    CreatedAt = DateTimeOffset.UtcNow
    LastEditedAt = None
}

// ─── Phase 104 — resolver contract pack ──────────────────────────────

let private resolverTests =
    testList "Phase 104 — IOriginalSourceResolver per-KnowledgeSource resolution" [
        testCaseAsync "UploadedFile resolves to raw bytes + extension-derived content type"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let bytes = [| 1uy; 2uy; 3uy; 4uy |]
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", bytes)
            let resolver = createDefault ()
            let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile

            let! resolved = resolver.Resolve(storage, "team-a", doc)

            match resolved with
            | Some original ->
                Expect.equal original.Content bytes "raw bytes round-trip"
                Expect.equal original.ContentType "application/pdf" "content type from extension"
                Expect.equal original.FileName "report.pdf" "file name preserved"
                Expect.equal original.SizeBytes 4L "size from blob length"
            | None -> failtest "UploadedFile with a present blob must resolve"
        }

        testCaseAsync "Note resolves note.md as text/markdown"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let body = Encoding.UTF8.GetBytes "# Conventions\n\nWe chose X over Y."
            let! _ = storage.Upload("team-a", "knowledge/doc-2/note.md", body)
            let resolver = createDefault ()
            let doc = mkDoc "doc-2" "Conventions.md" "note" (KnowledgeSource.Note noteSource)

            let! resolved = resolver.Resolve(storage, "team-a", doc)

            match resolved with
            | Some original ->
                Expect.equal original.ContentType "text/markdown" "notes are markdown"
                Expect.equal original.Content body "note body round-trip"
            | None -> failtest "Note with a present note.md must resolve"
        }

        testCaseAsync "FromNarrative resolves to None — synthetic, no original"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            // Even with the rendered-markdown convenience blob present,
            // narratives have no user-facing original (their home is the
            // producing module's page).
            let! _ = storage.Upload("team-a", "knowledge/doc-3/Overview.md", Encoding.UTF8.GetBytes "rendered")
            let resolver = createDefault ()
            let doc = mkDoc "doc-3" "Overview.md" "md" (FromNarrative narrativeSource)

            let! resolved = resolver.Resolve(storage, "team-a", doc)
            Expect.isNone resolved "narrative chunks are synthetic — None, not a guess"
        }

        testCaseAsync "UploadedFile whose blob is gone resolves to None, not a throw"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let resolver = createDefault ()
            let doc = mkDoc "doc-4" "ghost.pdf" "pdf" UploadedFile

            let! resolved = resolver.Resolve(storage, "team-a", doc)
            Expect.isNone resolved "missing blob → typed absence"
        }

        testCase "contentTypeFor covers the KB extraction formats + a safe default"
        <| fun _ ->
            Expect.equal (contentTypeFor "pdf") "application/pdf" "pdf"
            Expect.equal (contentTypeFor "csv") "text/csv" "csv"
            Expect.equal (contentTypeFor "txt") "text/plain" "txt"
            Expect.equal (contentTypeFor "md") "text/markdown" "md"

            Expect.equal
                (contentTypeFor "xlsx")
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                "xlsx"

            Expect.equal (contentTypeFor "weird") "application/octet-stream" "unknown degrades safely"
    ]

// ─── Phase 102 + 107 — handler scope gate + audit trail ──────────────

let private handlerTests =
    testList "Phase 102/107 — getOriginalDocument scope gate + audit" [
        testCaseAsync "in-scope UploadedFile fetch returns bytes and emits KnowledgeOriginalRetrieved"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let bytes = Encoding.UTF8.GetBytes "pdf-bytes"
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", bytes)
            do! saveIndex storage "team-a" [ mkDoc "doc-1" "report.pdf" "pdf" UploadedFile ]
            let audit = CapturingAuditLog()

            let deps =
                mkDeps storage (Some(audit :> IAuditLog)) (createDefault ()) (IngestionQueue()) "team-a"

            let! result = getOriginalDocument deps "doc-1"

            match result with
            | Ok original ->
                Expect.equal original.Content bytes "bytes"
                Expect.equal original.ContentType "application/pdf" "content type"
                Expect.equal original.FileName "report.pdf" "file name"
            | Error e -> failtest $"expected Ok, got {e}"

            match audit.Events with
            | [ (scopeId, KnowledgeOriginalRetrieved p) ] ->
                Expect.equal scopeId "team-a" "recorded under the caller's scope"
                Expect.equal p.UserId "user-1" "actor"
                Expect.equal p.DocumentId "doc-1" "doc id"
                Expect.equal p.ScopeId "team-a" "payload scope"
                Expect.equal p.SourceKind "UploadedFile" "source kind"
                Expect.equal p.FileName "report.pdf" "file name"
            | other -> failtest $"expected exactly one KnowledgeOriginalRetrieved, got %A{other}"
        }

        testCaseAsync "unknown docId refuses NotInScope with a denial audit and no bytes"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            do! saveIndex storage "team-a" []
            let audit = CapturingAuditLog()

            let deps =
                mkDeps storage (Some(audit :> IAuditLog)) (createDefault ()) (IngestionQueue()) "team-a"

            let! result = getOriginalDocument deps "doc-nope"
            Expect.equal result (Error NotInScope) "typed refusal"

            match audit.Events with
            | [ (_, KnowledgeOriginalRetrievalDenied p) ] ->
                Expect.equal p.Reason "NotInScope" "denial reason"
                Expect.equal p.DocumentId "doc-nope" "requested id recorded verbatim"
                Expect.equal p.UserId "user-1" "actor"
            | other -> failtest $"expected exactly one denial audit, got %A{other}"
        }

        testCaseAsync "a document in another team's scope is structurally invisible (GP 4)"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            // Document exists — but in team-b's index + container.
            let! _ = storage.Upload("team-b", "knowledge/doc-9/secret.pdf", Encoding.UTF8.GetBytes "secret")
            do! saveIndex storage "team-b" [ mkDoc "doc-9" "secret.pdf" "pdf" UploadedFile ]
            do! saveIndex storage "team-a" []
            let audit = CapturingAuditLog()

            let deps =
                mkDeps storage (Some(audit :> IAuditLog)) (createDefault ()) (IngestionQueue()) "team-a"

            let! result = getOriginalDocument deps "doc-9"

            Expect.equal result (Error NotInScope) "same refusal as a nonexistent id — no existence oracle"
        }

        testCaseAsync "narrative source refuses NoOriginalAvailable with a denial audit"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            do! saveIndex storage "team-a" [ mkDoc "doc-5" "Overview.md" "md" (FromNarrative narrativeSource) ]
            let audit = CapturingAuditLog()

            let deps =
                mkDeps storage (Some(audit :> IAuditLog)) (createDefault ()) (IngestionQueue()) "team-a"

            let! result = getOriginalDocument deps "doc-5"
            Expect.equal result (Error NoOriginalAvailable) "typed absence, not an exception"

            match audit.Events with
            | [ (_, KnowledgeOriginalRetrievalDenied p) ] -> Expect.equal p.Reason "NoOriginalAvailable" "reason"
            | other -> failtest $"expected exactly one denial audit, got %A{other}"
        }

        testCaseAsync "no audit substrate registered — fetch still succeeds silently"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", Encoding.UTF8.GetBytes "x")
            do! saveIndex storage "team-a" [ mkDoc "doc-1" "report.pdf" "pdf" UploadedFile ]
            let deps = mkDeps storage None (createDefault ()) (IngestionQueue()) "team-a"

            let! result = getOriginalDocument deps "doc-1"
            Expect.isOk result "absent IAuditLog never fails the fetch"
        }

        testCaseAsync "a custom resolver swapped into deps is honoured"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            do! saveIndex storage "team-a" [ mkDoc "doc-6" "Overview.md" "md" (FromNarrative narrativeSource) ]

            // Deployment-custom resolver that DOES serve narratives.
            let custom =
                { new IOriginalSourceResolver with
                    member _.Resolve(_, _, doc) = async {
                        return
                            Some {
                                FileName = doc.FileName
                                ContentType = "text/markdown"
                                SizeBytes = 8L
                                Content = Encoding.UTF8.GetBytes "rendered"
                            }
                    }
                }

            let deps = mkDeps storage None custom (IngestionQueue()) "team-a"
            let! result = getOriginalDocument deps "doc-6"

            match result with
            | Ok original -> Expect.equal original.ContentType "text/markdown" "custom resolver branch served"
            | Error e -> failtest $"custom resolver should have resolved, got {e}"
        }
    ]

// ─── Phase 103 + 106 — ingestion stamp + retrieval projection ────────

let private lineageTests =
    testList "Phase 103/106 — OriginalRef lineage + SourceLocator deep-link" [
        testCaseAsync "upload path stamps _originalRef (+ locator) onto every enqueued chunk"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let queue = IngestionQueue()
            let deps = mkDeps storage None (createDefault ()) queue "team-a"
            let csv = "name,score\nalpha,1\nbeta,2\ngamma,3"
            let bytes = Encoding.UTF8.GetBytes csv

            let! doc = uploadDocument deps bytes "scores.csv"

            // Extraction runs off the request path (Async.Start); the
            // enqueued job is the observable hand-off point.
            let readTask = queue.Reader.ReadAsync().AsTask()
            let! winner = Task.WhenAny(readTask, Task.Delay 15000) |> Async.AwaitTask

            if not (obj.ReferenceEquals(winner, readTask)) then
                failtest "timed out waiting for the upload's ingestion job"

            let job = readTask.Result
            Expect.isNonEmpty job.Chunks "csv upload produces chunks"

            for _, chunk in job.Chunks do
                match chunk.Metadata.TryFind ChunkMetadata.OriginalRefKey with
                | None -> failtest "every uploaded-file chunk carries _originalRef"
                | Some json ->
                    let originalRef = JsonSerializer.Deserialize<OriginalDocumentRef>(json, opts)
                    Expect.equal originalRef.DocumentId doc.Id "ref points at the uploaded doc"
                    Expect.equal originalRef.FileName "scores.csv" "file name"
                    Expect.equal originalRef.FileType "csv" "file type"
                    Expect.equal originalRef.SizeBytes (int64 bytes.Length) "original size"
                    Expect.isSome originalRef.Location "csv chunks carry a locator"

            // The schema+sample chunk is first and anchors to the sheet.
            let _, schemaChunk = job.Chunks.Head

            let schemaRef =
                JsonSerializer.Deserialize<OriginalDocumentRef>(
                    schemaChunk.Metadata[ChunkMetadata.OriginalRefKey],
                    opts
                )

            Expect.equal schemaRef.Location (Some(SourceLocator.Sheet "data")) "schema chunk → Sheet locator"
        }

        testCase "toRetrievedSource projects a stamped _originalRef onto OriginalRef"
        <| fun _ ->
            let originalRef: OriginalDocumentRef = {
                DocumentId = "doc-1"
                FileName = "report.pdf"
                FileType = "pdf"
                SizeBytes = 1024L
                Location = Some(SourceLocator.Page 4)
            }

            let m: VectorMatch = {
                ChunkId = "doc-1:chunk:0"
                Content = "Q3 revenue grew 14%."
                Score = 0.91
                Scope = Deployment
                Metadata =
                    Map.ofList [
                        ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue ChunkOrigin.Document
                        ChunkMetadata.OriginalRefKey, JsonSerializer.Serialize(originalRef, opts)
                    ]
            }

            let source = ToolUp.RAG.RAGPromptBuilder.toRetrievedSource 240 m
            Expect.equal source.OriginalRef (Some originalRef) "ref round-trips through the projection"
            Expect.equal (source.OriginalRef |> Option.bind _.Location) (Some(SourceLocator.Page 4)) "page anchor"

            // Lineage widening (Investigate gaps 2026-06-12): the live
            // projection always carries the match's scope (authority
            // badge) and chunk id (citation join key).
            Expect.equal source.Scope (Some Deployment) "scope projects from the VectorMatch"
            Expect.equal source.ChunkId (Some "doc-1:chunk:0") "chunk id projects from the VectorMatch"

        testCase "chunks without _originalRef (notes, pre-Phase-103 ingests) project None"
        <| fun _ ->
            let m: VectorMatch = {
                ChunkId = "doc-2:chunk:0"
                Content = "We chose X over Y."
                Score = 0.8
                Scope = Deployment
                Metadata = Map.ofList [ ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue ChunkOrigin.Note ]
            }

            let source = ToolUp.RAG.RAGPromptBuilder.toRetrievedSource 240 m
            Expect.isNone source.OriginalRef "absence is explicit — never a rebuilt blob-name guess"

        testCase "malformed _originalRef metadata degrades to None, not a throw"
        <| fun _ ->
            let m: VectorMatch = {
                ChunkId = "doc-3:chunk:0"
                Content = "x"
                Score = 0.5
                Scope = Deployment
                Metadata = Map.ofList [ ChunkMetadata.OriginalRefKey, "{not-json" ]
            }

            let source = ToolUp.RAG.RAGPromptBuilder.toRetrievedSource 240 m
            Expect.isNone source.OriginalRef "defensive deserialisation"

        testCase "KB SourceLocation maps onto the neutral SourceLocator case-for-case"
        <| fun _ ->
            Expect.equal (SourceLocation.toLocator (Page 4)) (SourceLocator.Page 4) "page"
            Expect.equal (SourceLocation.toLocator (Slide(12, Some "Methodology"))) (SourceLocator.Slide 12) "slide"

            Expect.equal (SourceLocation.toLocator (Sheet("Q3", Some "rows 2–1350"))) (SourceLocator.Sheet "Q3") "sheet"

            Expect.equal (SourceLocation.toLocator (Section "Pricing")) (SourceLocator.Section "Pricing") "section"
            Expect.equal (SourceLocation.toLocator (RowGroup(2, 50))) (SourceLocator.RowGroup(2, 50)) "row group"

        testCase "RetrievedSource with OriginalRef round-trips through the STJ wire"
        <| fun _ ->
            let source: RetrievedSource = {
                DocumentId = "doc-1"
                DocumentName = "report.pdf"
                Snippet = "Q3 revenue grew 14%."
                Score = 0.91
                Origin = ChunkOrigin.Document
                LocationHint = Some "Page 4"
                OriginalRef =
                    Some {
                        DocumentId = "doc-1"
                        FileName = "report.pdf"
                        FileType = "pdf"
                        SizeBytes = 1024L
                        Location = Some(SourceLocator.RowGroup(2, 50))
                    }
                Scope = Some(Team "T")
                ChunkId = Some "doc-1:chunk:0"
            }

            let json = JsonSerializer.Serialize(source, opts)
            let back = JsonSerializer.Deserialize<RetrievedSource>(json, opts)
            Expect.equal back source "wire round-trip"

        testCase "pre-OriginalRef wire payloads deserialise with OriginalRef = None (GP 11)"
        <| fun _ ->
            // The exact field set RetrievedSource carried before Phase 103.
            let legacy = {|
                DocumentId = "doc-1"
                DocumentName = "report.pdf"
                Snippet = "s"
                Score = 0.9
                Origin = ChunkOrigin.Document
                LocationHint = (None: string option)
            |}

            let json = JsonSerializer.Serialize(legacy, opts)
            let back = JsonSerializer.Deserialize<RetrievedSource>(json, opts)
            Expect.isNone back.OriginalRef "missing field absorbs to None"
            Expect.equal back.DocumentId "doc-1" "legacy fields intact"
            // Lineage widening (Investigate gaps 2026-06-12): same GP 11
            // guarantee for the Scope / ChunkId fields — RetrievedSources
            // ride persisted conversation history, so replayed
            // pre-widening payloads must absorb to None, never a null
            // scope that crashes a renderer's pattern match.
            Expect.isNone back.Scope "missing Scope absorbs to None"
            Expect.isNone back.ChunkId "missing ChunkId absorbs to None"
    ]

let tests =
    testList "KnowledgeOriginalRetrieval (Wave 15 — Phases 102/103/104/106/107)" [
        resolverTests
        handlerTests
        lineageTests
    ]