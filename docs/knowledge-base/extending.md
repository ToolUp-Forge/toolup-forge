# Extending ToolUp.KnowledgeBase

How to replace the built-in KB module with a custom one, add new extractors, customise the upload-status UI.

## Replacing the built-in module

`ToolUp.KnowledgeBase` is one consumer of `ToolUp.RAG`. Apps with different requirements (custom upload UI, different file kinds, integrated workflow management, etc.) can replace it entirely.

The minimum contract for a KB replacement:
1. Implements a Fable.Remoting API for document upload + list.
2. Wires an `IIngestionStatusObserver` into `composeWithRAG`.
3. Either matches the `"KnowledgeBase.IngestionStatus"` notification key (so the AI side panel surfaces ingestion progress) or accepts the AI panel won't show progress for its uploads.
4. Optionally installs the `Toolup.NarrativeCommit` handler (so other modules' "Save to Knowledge Base" buttons resolve).

A replacement is just another module under `src/Modules/MyKnowledgeBase/`:

```fsharp
// SharedTypes.fs
module MyKnowledgeBase.SharedTypes

type IMyKnowledgeApi = {
    UploadDocument: UploadRequest -> Async<Result<MyDocument, string>>
    ListDocuments: unit -> Async<MyDocument list>
    DeleteDocument: Guid -> Async<unit>
    // ...
}

// Server.fs
module MyKnowledgeBase.Server

let knowledgeApi (ctx: HttpContext) : IMyKnowledgeApi = {
    UploadDocument = fun req -> async {
        // your logic
    }
    // ...
}

let myVectorisationHandler : VectorisationHandler = {
    DataTypeId = "MyKnowledge"
    Vectorise = fun (fileName, dataObject) -> async {
        // extract text from your data shape, chunk, return TextChunk list
    }
}

let myIngestionStatusObserver : IIngestionStatusObserver =
    {
        new IIngestionStatusObserver with
            member _.OnJobAccepted(job) = ...
            member _.OnChunkIndexed(jobId, chunkId) = ...
            member _.OnJobCompleted(jobId, chunkCount) = ...
            member _.OnJobFailed(jobId, reason) = ...
    }
```

In the composition root:

```fsharp
RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> ...
|> RAGServerApp.addModules [ myKnowledgeBaseModule ]    // your module
|> RAGServerApp.withIngestionStatusObserver myIngestionStatusObserver
|> RAGServerApp.withVectorisationHandler myVectorisationHandler
|> RAGServerApp.run
```

Drop the `ToolUp.KnowledgeBase` `<PackageReference>` entries from the consuming project's `.fsproj`. The built-in KB module disappears; your replacement provides equivalent (or different) functionality.

## A future `KnowledgeBaseMode` DU

The shipped pattern requires the consumer to drop the package + provide their own module. A future SDK addition (`KnowledgeBaseMode`) will allow first-class substitution via fluent API:

```fsharp
RAGServerApp.create (...)
|> RAGServerApp.withKnowledgeBase (ExternalKnowledgeBase myErasedModule)
|> ...
```

(Not yet shipped; the structural contracts above are what would land.) Status: deferred.

## Adding a new format extractor

The shipped extractor list is internal to `ToolUp.KnowledgeBase.Server`. Adding a new format means either:

**Option 1**: Replace the KB module entirely (see above). Your custom `VectorisationHandler` handles whichever formats you want.

**Option 2**: Add a parallel module for the new format. Multiple modules can declare `DataType`s and `VectorisationHandler`s; they coexist via ingestion-queue routing by `DataTypeId`. Useful when you want to add `.epub` support alongside KB's PDFs without touching KB.

Example: ePub module:

```fsharp
// Server.fs (in your new module)
let epubDataType : DataType = {
    Info = { Id = "Epub"; DisplayName = "EPUB books"; Schema = None }
    Id = "Epub"
    Detect = fun (fileName, _) -> fileName.EndsWith(".epub")
    Process = fun (fileName, contents) ->
        // Parse EPUB, return (boxed result, ProcessedFileEntry)
}

let epubVectorisationHandler : VectorisationHandler = {
    DataTypeId = "Epub"
    Vectorise = fun (fileName, dataObject) -> async {
        let parsed = unbox<EpubBook> dataObject
        let chunks =
            parsed.Chapters
            |> List.map (fun chapter -> {
                Id = Guid.NewGuid()
                Text = chapter.Content
                Metadata = Map.ofList [
                    "_source", "Epub"
                    "_fileName", fileName
                    "_chapterTitle", chapter.Title
                ]
                Origin = ChunkOrigin.UserContent
            })
        return chunks
    }
}
```

Wire alongside KB:

```fsharp
RAGServerApp.create (...)
|> RAGServerApp.addModules [ kbModule; epubModule ]
|> RAGServerApp.withVectorisationHandler kbVectorisationHandler
|> RAGServerApp.withVectorisationHandler epubVectorisationHandler
|> ...
```

The file manager UI accepts EPUBs; the post-save hook routes by `DataTypeId` to the right handler.

## Customising the upload-status UI

The built-in `KnowledgeBaseView` subscribes to `"KnowledgeBase.IngestionStatus"` notifications. Custom UI subscribes to the same notification key:

```fsharp
// In your custom module's Client.fs
let subscribeIngestionStatus dispatch =
    let unsub = NotificationClient.subscribe "KnowledgeBase.IngestionStatus" (fun env ->
        match env.Payload with
        | :? IngestionStatusUpdate as update -> dispatch (IngestionStatusReceived update)
        | _ -> ())
    [ unsub ]
```

For modules using a different notification key, publish your own and subscribe to it. The AI side panel won't surface progress for non-matching keys; users see ingestion happen in your module's UI but the side panel doesn't show indexing-in-progress.

If you want the AI side panel to surface your module's ingestion too, match the `"KnowledgeBase.IngestionStatus"` key and use the `IngestionStatusUpdate` shape. The wire-format contract isn't enforced by the SDK — it's a published interop string.

## Adding an OCR provider

The KB extractor for PDFs uses `UglyToad.PdfPig` for native text extraction. Scanned PDFs (image-only pages) get empty text. To OCR them, register an `IOcrProvider`:

```fsharp
let ocrProvider = AzureDocIntelligenceOcrProvider.create azureClient :> IOcrProvider

RAGServerApp.create (...)
|> ...
|> RAGServerApp.withOcrProvider ocrProvider
|> ...
```

The KB extractor checks `IsScanned` on the document bytes; if true, falls back to `ExtractText` from the OCR provider. The result chunks the same way as native-extracted text.

OCR is expensive (~$1.50 per 1000 pages with Azure Document Intelligence). Use sparingly; check `IsScanned` cheaply before invoking.

## Adding a table extractor

For documents where embedded tables are important (financial reports, scientific papers), an `ITableExtractor` companion can surface tables explicitly:

```fsharp
let tableExtractor = CamelotTableExtractor.create pythonSidecar :> ITableExtractor

RAGServerApp.create (...)
|> ...
|> RAGServerApp.withTableExtractor tableExtractor
|> ...
```

The KB extractor calls `ExtractTables` alongside text extraction; tables go through `Chunking.chunkSpreadsheet` (same as XLSX sheets) for header-aware indexed chunks. The output shape (`ExtractedTable`) is compatible with `Chunking.SheetData` so consumers pipe through without translation.

## Customising the ingestion-status observer

The built-in observer updates document metadata blobs + publishes `IngestionStatusUpdate` notifications. Custom observers can do more — write to Slack on failure, page on-call, increment dashboard metrics:

```fsharp
type SlackOnFailureObserver(slackWebhookUrl: string) =
    interface IIngestionStatusObserver with
        member _.OnJobAccepted(_) = async { return () }
        member _.OnChunkIndexed(_, _) = async { return () }
        member _.OnJobCompleted(_, _) = async { return () }
        member _.OnJobFailed(jobId, reason) = async {
            do! postToSlack slackWebhookUrl $"Ingestion job {jobId} failed: {reason}"
        }
```

Wire alongside (or replace) the built-in observer:

```fsharp
let composedObserver = ChainedObserver([
    KnowledgeBase.Server.makeIngestionStatusObserver()
    SlackOnFailureObserver(slackWebhookUrl)
])

RAGServerApp.create (...)
|> ...
|> RAGServerApp.withIngestionStatusObserver composedObserver
|> ...
```

(`ChainedObserver` isn't shipped — it's a 5-line composition you write yourself.)

## Replacing the AI Context page

The AI Context page is a UI for the standing-context entries persisted to `_platform/kb-ai-context/{teamId}/entries.json`. A custom AI-context UI replaces just the page, keeping the persistence and the standing-context builder:

```fsharp
// Custom AI-context page
let aiContextView model dispatch : PageContent = ...

// Custom multi-page module that omits the built-in /ai-context page
let myKnowledgeBaseModule () =
    ClientModule.create { ... }
    |> ClientModule.withPages [
        { Route = "/documents"; Label = "Documents"; View = documentsView }
        { Route = "/notes"; Label = "Notes"; View = notesView }
        { Route = "/my-ai-context"; Label = "AI Context"; View = aiContextView }
    ]
    |> ClientModule.register
```

The standing-context builder still reads from the same blob path; the difference is only in the page UI. Replace the entry-edit UX (drag-to-reorder, tag-per-entry, etc.) without changing the wire shape.

## Cross-module knowledge surfacing

A common pattern: surface knowledge from across modules via a single AI conversation. The narrative-commit mechanism gives modules a one-line path to push their content into KB:

```fsharp
// In SalesAnalysis ClientView.fs
let saveAnalysisToKB analysis =
    Toolup.NarrativeCommit.submit {
        Title = $"Sales analysis: {analysis.Title}"
        Body = formatAnalysisAsMarkdown analysis
        SourceModule = "SalesAnalysis"
    }
```

The narrative-commit handler indexes the markdown body; later chat queries retrieve the content. Multi-module insights aggregate naturally without any cross-module imports.

## What can't be extended

- **The built-in KB module's Documents page UI** — the Documents page UI itself isn't broken up into smaller components for partial replacement. To customise the upload UX, replace the whole module.
- **The PDF / PPTX / DOCX / XLSX extractors** — they're internal to the package. To use a different PDF extractor, replace the module.
- **The narrative-commit dispatcher** — `Toolup.NarrativeCommit.submit` is a single dispatch path. Multiple handlers can be registered (`install` is additive), but the dispatch routes to whichever handler matches first.

For deeper customisation than the extension points allow, the right shape is "fork the module structure into your own consumer code". Module source is fully visible (Fable companion ships `fable/` source in the nupkg) so the cost is reading existing code, not reverse-engineering.
