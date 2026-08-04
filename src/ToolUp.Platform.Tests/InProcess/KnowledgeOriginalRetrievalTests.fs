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

// Phase 200 — abbreviated rather than `open`ed on purpose: the preview
// seam module also exports a `createDefault`, and opening it would
// silently shadow the Phase 104 resolver's `createDefault` that every
// case in the first three lists below calls.
module PreviewSeam = KnowledgeBase.ServerOriginalPreviewSeam

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
    ContentHash = None
    Version = 1
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
        IndexLifecycle = None
        EventStore = None
        NarrativeStore = None
        AccessContext = AccessContext.unrestricted (AnonymousSession "user-1")
        OriginalResolver = resolver
        AuditLog = auditLog
        RecordEnqueue = ignore
        PublishInventory = fun () -> async.Return()
        MarkIngestionFailed = fun _ _ _ -> async.Return()
        EnsureContextWriteAllowed = fun () -> async { return Ok() }
        ScopeResolvedFromRequest = true
        UploadPolicy = KnowledgeUploadPolicy.permissive
        DedupPolicy = KnowledgeDedupPolicy.enabled
        VersioningPolicy = KnowledgeVersioningPolicy.disabled
        // Phase 512 — these packs pin pre-512 paths; the unlimited /
        // retain-forever defaults keep them byte-identical.
        QuotaPolicy = KnowledgeQuotaPolicy.unlimited
        RetentionPolicy = KnowledgeRetentionPolicy.retainForever
        DisclosureGate = None
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

            // Lineage widening (2026-06-12 audit): the live
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
                FactId = None
                FactRendering = None
                FactFreshness = None
                FactSupersededBy = None
                Span = None
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
            // Lineage widening (2026-06-12 audit): same GP 11
            // guarantee for the Scope / ChunkId fields — RetrievedSources
            // ride persisted conversation history, so replayed
            // pre-widening payloads must absorb to None, never a null
            // scope that crashes a renderer's pattern match.
            Expect.isNone back.Scope "missing Scope absorbs to None"
            Expect.isNone back.ChunkId "missing ChunkId absorbs to None"
    ]

// ─── Phase 200 — anchor-highlighting preview seam ────────────────────
//
// The join Phases 102/104/106 left to the consumer: a document + a
// citation's `SourceLocator` → a `PreviewTarget` a viewer opens at and
// highlights. Covered here: the total anchor projection (including the
// deliberate `WholeDocument` absences, which are the cases a fabricated
// "page 1" default would quietly replace), the two delivery modes,
// TTL-boundedness of the signed-URL mode, the scope gate's
// indistinguishable refusal (GP 4), and the GP 13 pin that a
// deployment composing no seam registers nothing.

/// Capturing signer double — records whether it was reached, so the
/// "resolver said no" case can assert the signer was never asked to
/// mint a URL for a document that has no original.
type private CapturingSigner(result: Result<string, string>) =
    let calls = ResizeArray<string * string * TimeSpan>()

    member _.Calls = List.ofSeq calls

    interface PreviewSeam.IPreviewUrlSigner with
        member _.Sign(doc, container, ttl) = async {
            calls.Add(doc.Id, container, ttl)
            return result
        }

let private previewAnchorTests =
    testList "Phase 200 — SourceLocator → PreviewAnchor projection" [
        testCase "every SourceLocator case projects onto its anchor"
        <| fun _ ->
            Expect.equal (PreviewAnchor.ofLocator (Some(SourceLocator.Page 4))) (PreviewAnchor.Page 4) "page → Page"

            Expect.equal
                (PreviewAnchor.ofLocator (Some(SourceLocator.Slide 12)))
                (PreviewAnchor.Slide 12)
                "slide → Slide"

            Expect.equal
                (PreviewAnchor.ofLocator (Some(SourceLocator.Sheet "Q3")))
                (PreviewAnchor.Sheet "Q3")
                "sheet → Sheet"

            Expect.equal
                (PreviewAnchor.ofLocator (Some(SourceLocator.Section "Pricing")))
                (PreviewAnchor.Section "Pricing")
                "section → Section"

            Expect.equal
                (PreviewAnchor.ofLocator (Some(SourceLocator.RowGroup(2, 50))))
                (PreviewAnchor.Rows(2, 50))
                "row group → Rows"

        testCase "a citation with no locator anchors WholeDocument, never a fabricated page 1"
        <| fun _ -> Expect.equal (PreviewAnchor.ofLocator None) PreviewAnchor.WholeDocument "absence is a case (GP 9)"

        testCase "a character span projects onto CharRange"
        <| fun _ ->
            // The mapping point for a citation carrying document-relative
            // character offsets: the caller passes the raw offset pair, so
            // the preview seam never takes a dependency on a retrieval-side
            // span record.
            Expect.equal (PreviewAnchor.ofCharSpan 120 180) (PreviewAnchor.CharRange(120, 180)) "half-open span"
            Expect.equal (PreviewAnchor.ofCharSpan 0 1) (PreviewAnchor.CharRange(0, 1)) "single character at offset 0"

        testCase "a degenerate character span degrades to WholeDocument"
        <| fun _ ->
            Expect.equal (PreviewAnchor.ofCharSpan 10 10) PreviewAnchor.WholeDocument "empty span highlights nothing"
            Expect.equal (PreviewAnchor.ofCharSpan 50 10) PreviewAnchor.WholeDocument "inverted span"
            Expect.equal (PreviewAnchor.ofCharSpan -1 40) PreviewAnchor.WholeDocument "negative start"
    ]

let private previewSeamTests =
    testList "Phase 200 — IOriginalPreviewSeam delivery modes" [
        testCaseAsync "inline seam pairs the resolved original with the projected anchor"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let bytes = Encoding.UTF8.GetBytes "pdf-bytes"
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", bytes)
            let seam = PreviewSeam.createDefault (createDefault ())
            let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile

            let! result = seam.Preview(storage, "team-a", doc, Some(SourceLocator.Page 4))

            match result with
            | Ok target ->
                Expect.equal target.DocumentId "doc-1" "target names the document"
                Expect.equal target.Anchor (PreviewAnchor.Page 4) "anchor matches the citation"
                Expect.equal target.ContentType "application/pdf" "content type surfaces without opening the bytes"
                Expect.equal target.FileName "report.pdf" "file name"
                Expect.equal target.SizeBytes (int64 bytes.Length) "size"

                match target.Content with
                | PreviewContent.Inline original ->
                    Expect.equal original.Content bytes "inline delivery carries the original bytes"
                    // The top-level metadata must mirror the embedded
                    // record — they are constructed together precisely so
                    // they cannot drift.
                    Expect.equal original.ContentType target.ContentType "content type mirrors"
                    Expect.equal original.FileName target.FileName "file name mirrors"
                    Expect.equal original.SizeBytes target.SizeBytes "size mirrors"
                | other -> failtest $"expected inline delivery, got %A{other}"
            | Error e -> failtest $"expected Ok, got {e}"
        }

        testCaseAsync "a citation without a locator still previews, anchored WholeDocument"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", Encoding.UTF8.GetBytes "x")
            let seam = PreviewSeam.createDefault (createDefault ())
            let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile

            let! result = seam.Preview(storage, "team-a", doc, None)

            match result with
            | Ok target -> Expect.equal target.Anchor PreviewAnchor.WholeDocument "no locator → open at the top"
            | Error e -> failtest $"expected Ok, got {e}"
        }

        testCaseAsync "a source kind with no original refuses NoOriginalAvailable"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let seam = PreviewSeam.createDefault (createDefault ())
            let doc = mkDoc "doc-3" "Overview.md" "md" (FromNarrative narrativeSource)

            let! result = seam.Preview(storage, "team-a", doc, Some(SourceLocator.Section "Summary"))
            Expect.equal result (Error NoOriginalAvailable) "typed absence — the seam never fabricates a target"
        }

        testCaseAsync "a document whose blob is gone refuses NoOriginalAvailable"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let seam = PreviewSeam.createDefault (createDefault ())
            let doc = mkDoc "doc-4" "ghost.pdf" "pdf" UploadedFile

            let! result = seam.Preview(storage, "team-a", doc, Some(SourceLocator.Page 1))
            Expect.equal result (Error NoOriginalAvailable) "missing blob → typed absence, not a throw"
        }

        testCaseAsync "a throwing resolver surfaces as OriginalRetrievalFailed, never an exception (GP 9)"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            let exploding =
                { new IOriginalSourceResolver with
                    member _.Resolve(_, _, _) = async { return failwith "storage exploded" }
                }

            let seam = PreviewSeam.createDefault exploding
            let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile

            let! result = seam.Preview(storage, "team-a", doc, None)

            match result with
            | Error(OriginalRetrievalFailed reason) -> Expect.stringContains reason "exploded" "diagnostic preserved"
            | other -> failtest $"expected OriginalRetrievalFailed, got %A{other}"
        }

        testCaseAsync "a deployment-custom resolver is honoured by the seam — one resolution rule, not two"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage

            // The same "deployment serves narratives after all" resolver the
            // Phase 104 pack composes: swapping it must change what the
            // preview seam resolves, without the seam knowing anything about it.
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

            let seam = PreviewSeam.createDefault custom
            let doc = mkDoc "doc-6" "Overview.md" "md" (FromNarrative narrativeSource)

            let! result = seam.Preview(storage, "team-a", doc, Some(SourceLocator.Section "Summary"))

            match result with
            | Ok target ->
                Expect.equal target.ContentType "text/markdown" "custom branch served"
                Expect.equal target.Anchor (PreviewAnchor.Section "Summary") "anchor unaffected by the swap"
            | Error e -> failtest $"custom resolver should have resolved, got {e}"
        }
    ]

let private previewSignedUrlTests =
    testList "Phase 200 — signed-URL delivery is TTL-bounded" [
        testCaseAsync "signed-URL mode returns a URL + expiry and keeps the bytes out of the response"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let bytes = Encoding.UTF8.GetBytes "pdf-bytes"
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", bytes)

            let now = DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)
            let signer = CapturingSigner(Ok "https://blobs.example/doc-1?sig=abc")

            let options = {
                PreviewSeam.PreviewSignedUrlOptions.defaults with
                    Ttl = TimeSpan.FromMinutes 15.0
                    Now = fun () -> now
            }

            let seam =
                PreviewSeam.createSignedUrl (createDefault ()) (signer :> PreviewSeam.IPreviewUrlSigner) options

            let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile

            let! result = seam.Preview(storage, "team-a", doc, Some(SourceLocator.Page 7))

            match result with
            | Ok target ->
                Expect.equal target.Anchor (PreviewAnchor.Page 7) "anchor is delivery-mode independent"
                Expect.equal target.ContentType "application/pdf" "viewer choice without downloading"
                Expect.equal target.SizeBytes (int64 bytes.Length) "size still surfaced"

                match target.Content with
                | PreviewContent.SignedUrl(url, expiresAt) ->
                    Expect.equal url "https://blobs.example/doc-1?sig=abc" "signer's URL"
                    Expect.equal expiresAt (now.AddMinutes 15.0) "expiry is exactly now + TTL"
                    Expect.isTrue (expiresAt > now) "TTL-bounded, and bounded forward"
                | other -> failtest $"expected a signed URL, got %A{other}"

                Expect.equal
                    signer.Calls
                    [ "doc-1", "team-a", TimeSpan.FromMinutes 15.0 ]
                    "signer saw the TTL it must honour"
            | Error e -> failtest $"expected Ok, got {e}"
        }

        testCaseAsync "a signer failure refuses — it never falls back to inlining the bytes"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", Encoding.UTF8.GetBytes "x")
            let signer = CapturingSigner(Error "signing key unavailable")

            let seam =
                PreviewSeam.createSignedUrl
                    (createDefault ())
                    (signer :> PreviewSeam.IPreviewUrlSigner)
                    PreviewSeam.PreviewSignedUrlOptions.defaults

            let doc = mkDoc "doc-1" "report.pdf" "pdf" UploadedFile

            let! result = seam.Preview(storage, "team-a", doc, None)

            match result with
            | Error(OriginalRetrievalFailed reason) ->
                Expect.stringContains reason "signing key unavailable" "signer diagnostic surfaces"
            | other -> failtest $"expected OriginalRetrievalFailed, got %A{other}"
        }

        testCaseAsync "a document with no original never reaches the signer"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let signer = CapturingSigner(Ok "https://blobs.example/should-not-be-minted")

            let seam =
                PreviewSeam.createSignedUrl
                    (createDefault ())
                    (signer :> PreviewSeam.IPreviewUrlSigner)
                    PreviewSeam.PreviewSignedUrlOptions.defaults

            let doc = mkDoc "doc-3" "Overview.md" "md" (FromNarrative narrativeSource)

            let! result = seam.Preview(storage, "team-a", doc, None)
            Expect.equal result (Error NoOriginalAvailable) "absence short-circuits"
            Expect.isEmpty signer.Calls "no URL minted for a document that has no original"
        }

        testCase "a non-positive TTL is refused at construction, not on a request path"
        <| fun _ ->
            let signer =
                CapturingSigner(Ok "https://blobs.example/x") :> PreviewSeam.IPreviewUrlSigner

            let bad = {
                PreviewSeam.PreviewSignedUrlOptions.defaults with
                    Ttl = TimeSpan.Zero
            }

            Expect.throwsT<ArgumentException>
                (fun () -> PreviewSeam.createSignedUrl (createDefault ()) signer bad |> ignore)
                "a zero TTL mints already-expired links — fail the compose, not the request"

        testCase "the default TTL matches the media-tier signed-URL default"
        <| fun _ ->
            Expect.equal
                PreviewSeam.PreviewSignedUrlOptions.defaults.Ttl
                (TimeSpan.FromHours 1.0)
                "one hour, as IMediaLibrary's SignedUrlDefaultTtl"
    ]

let private previewScopeTests =
    testList "Phase 200 — previewOriginal scope gate (GP 4)" [
        testCaseAsync "an in-scope document previews from its id alone"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", Encoding.UTF8.GetBytes "pdf")
            do! saveIndex storage "team-a" [ mkDoc "doc-1" "report.pdf" "pdf" UploadedFile ]
            let seam = PreviewSeam.createDefault (createDefault ())

            let! result = PreviewSeam.previewOriginal seam storage "team-a" "doc-1" (Some(SourceLocator.Page 2))

            match result with
            | Ok target ->
                Expect.equal target.DocumentId "doc-1" "resolved from the scope's index"
                Expect.equal target.Anchor (PreviewAnchor.Page 2) "anchor"
            | Error e -> failtest $"expected Ok, got {e}"
        }

        testCaseAsync "an unknown id and another team's document refuse identically — no existence oracle"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            // doc-9 exists — in team-b's index and container.
            let! _ = storage.Upload("team-b", "knowledge/doc-9/secret.pdf", Encoding.UTF8.GetBytes "secret")
            do! saveIndex storage "team-b" [ mkDoc "doc-9" "secret.pdf" "pdf" UploadedFile ]
            do! saveIndex storage "team-a" []
            let seam = PreviewSeam.createDefault (createDefault ())

            let! unknown = PreviewSeam.previewOriginal seam storage "team-a" "doc-nope" None
            let! foreign = PreviewSeam.previewOriginal seam storage "team-a" "doc-9" None

            Expect.equal unknown (Error NotInScope) "unknown id"
            Expect.equal foreign (Error NotInScope) "another team's document"
            Expect.equal unknown foreign "byte-identical refusals — absence and denial are indistinguishable (GP 4)"
        }

        testCaseAsync "an in-scope document with no original refuses NoOriginalAvailable, not NotInScope"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            do! saveIndex storage "team-a" [ mkDoc "doc-5" "Overview.md" "md" (FromNarrative narrativeSource) ]
            let seam = PreviewSeam.createDefault (createDefault ())

            let! result = PreviewSeam.previewOriginal seam storage "team-a" "doc-5" None

            Expect.equal
                result
                (Error NoOriginalAvailable)
                "in-scope-but-unavailable is a different answer from out-of-scope"
        }
    ]

/// The DI namespace is opened HERE and not at file scope on purpose:
/// `Microsoft.Extensions.DependencyInjection` puts `Add` extension
/// methods in scope, which suppresses F#'s implicit-tuple resolution on
/// the plain `ResizeArray.Add(a, b)` calls the Phase 104/107 doubles
/// above make. A file-scope open compiles them into errors — a
/// pointless churn of lines this phase has no business touching.
module private PreviewCompose =
    open Microsoft.Extensions.DependencyInjection

    let tests =
        testList "Phase 200 — opt-in compose (GP 11 / GP 13)" [
            test "an app that never composes a preview seam registers nothing" {
                let app = ServerApp.empty

                Expect.isNone
                    app.Extensions.ServiceConfig
                    "no preview seam composed → no service config → the pre-200 composition, byte-for-byte"

                let services = ServiceCollection()
                let sp = services.BuildServiceProvider()

                Expect.isTrue
                    (isNull (box (sp.GetService<PreviewSeam.IOriginalPreviewSeam>())))
                    "there is no default seam in DI — the SDK ships no viewer path a deployment did not ask for"
            }

            test "withOriginalPreviewSeam registers exactly one singleton, and it is the composed instance" {
                let seam = PreviewSeam.createDefault (createDefault ())
                let app = ServerApp.empty |> PreviewSeam.withOriginalPreviewSeam seam

                let services = ServiceCollection()
                let before = services.Count

                match app.Extensions.ServiceConfig with
                | Some cfg -> cfg services |> ignore
                | None -> failtest "composing the seam must populate ServiceConfig"

                Expect.equal (services.Count - before) 1 "one knob, one registration — no hidden hosted service"

                let sp = services.BuildServiceProvider()
                let resolved = sp.GetService<PreviewSeam.IOriginalPreviewSeam>()
                Expect.isTrue (obj.ReferenceEquals(resolved, seam)) "the composed instance is what resolves"
            }

            test "an existing ServiceConfig is chained, never replaced" {
                let marker =
                    { new ILogger with
                        member _.Debug _ = ()
                        member _.Info _ = ()
                        member _.Warn _ = ()
                        member _.Error(_, _) = ()
                    }

                let app =
                    {
                        ServerApp.empty with
                            Extensions = {
                                ServerApp.empty.Extensions with
                                    ServiceConfig = Some(fun s -> s.AddSingleton<ILogger>(marker))
                            }
                    }
                    |> PreviewSeam.withOriginalPreviewSeam (PreviewSeam.createDefault (createDefault ()))

                let services = ServiceCollection()

                match app.Extensions.ServiceConfig with
                | Some cfg -> cfg services |> ignore
                | None -> failtest "ServiceConfig must survive composition"

                let sp = services.BuildServiceProvider()

                Expect.isTrue
                    (obj.ReferenceEquals(sp.GetService<ILogger>(), marker))
                    "a previously-composed registration is preserved"

                Expect.isFalse
                    (isNull (box (sp.GetService<PreviewSeam.IOriginalPreviewSeam>())))
                    "and the seam is added alongside it"
            }

            testCaseAsync "the Phase 102 retrieval path is unchanged by the seam's presence"
            <| async {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let bytes = Encoding.UTF8.GetBytes "pdf-bytes"
                let! _ = storage.Upload("team-a", "knowledge/doc-1/report.pdf", bytes)
                do! saveIndex storage "team-a" [ mkDoc "doc-1" "report.pdf" "pdf" UploadedFile ]

                let deps = mkDeps storage None (createDefault ()) (IngestionQueue()) "team-a"

                let! before = getOriginalDocument deps "doc-1"

                // Compose a preview seam — it is registered in DI and is not
                // on `getOriginalDocument`'s path at all.
                ServerApp.empty
                |> PreviewSeam.withOriginalPreviewSeam (PreviewSeam.createDefault (createDefault ()))
                |> ignore

                let! after = getOriginalDocument deps "doc-1"

                Expect.equal after before "the original-retrieval result is identical with a seam composed (GP 13)"
                Expect.isOk before "and it is the same successful fetch it was pre-200"
            }
        ]

let tests =
    testList "KnowledgeOriginalRetrieval (Wave 15 — Phases 102/103/104/106/107; Phase 200 preview seam)" [
        resolverTests
        handlerTests
        lineageTests
        previewAnchorTests
        previewSeamTests
        previewSignedUrlTests
        previewScopeTests
        PreviewCompose.tests
    ]