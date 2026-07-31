# API reference

Public surface of `ToolUp.KnowledgeBase`.

## `ToolUp.KnowledgeBase.Core`

### `KnowledgeApi` (ToolUp.Remoting contract)

```fsharp
type IKnowledgeApi = {
    UploadDocument: UploadDocumentRequest -> Async<Result<KnowledgeDocument, KnowledgeError>>
    ListDocuments: unit -> Async<KnowledgeDocument list>
    GetDocument: DocumentId -> Async<KnowledgeDocument option>
    DeleteDocument: DocumentId -> Async<unit>
    ResetKnowledgeBase: unit -> Async<unit>
    IngestNarrative: IngestNarrativeRequest -> Async<Result<KnowledgeDocument, KnowledgeError>>
    AddNote: AddNoteRequest -> Async<Result<KnowledgeDocument, KnowledgeError>>
    UpdateNote: UpdateNoteRequest -> Async<Result<KnowledgeDocument, KnowledgeError>>
    GetAIContext: unit -> Async<AIContextEntry list>
    SetAIContext: AIContextEntry list -> Async<unit>
    GetIngestionStatus: DocumentId -> Async<IngestionStatus option>
}

and UploadDocumentRequest = {
    FileName: string
    ContentType: string
    Bytes: byte[]
}

and IngestNarrativeRequest = {
    Title: string
    Body: string
    SourceModule: string
}

and AddNoteRequest = {
    Title: string option
    Body: string
}

and UpdateNoteRequest = {
    DocumentId: DocumentId
    Title: string option
    Body: string
}

and AIContextEntry = {
    Id: Guid
    Text: string
    Order: int
}
```

### `KnowledgeDocument`

```fsharp
type KnowledgeDocument = {
    DocumentId: DocumentId
    FileName: string
    ContentType: string
    Source: KnowledgeSource
    UploadedBy: string
    UploadedAt: DateTime
    SizeBytes: int64
    Status: IngestionStatus
    ChunkCount: int option
    Reason: string option        // populated when Status = Failed
}

and KnowledgeSource =
    | UploadedFile
    | FromNarrative of FromNarrativeSource
    | Note of NoteSource

and FromNarrativeSource = {
    SourceModule: string
    OriginalTitle: string
}

and NoteSource = {
    NoteTitle: string option
}

and IngestionStatus =
    | Pending
    | Extracting
    | Chunking
    | Embedding
    | Indexed
    | Failed
```

### `IngestionStatusUpdate` (SSE wire format)

```fsharp
type IngestionStatusUpdate = {
    DocumentId: Guid
    Status: IngestionStatus
    Progress: float option       // 0.0 - 1.0
    ChunksIndexed: int
    ChunksTotal: int option
    Reason: string option
}
```

Notification kind: `[<Literal>] IngestionStatusNotificationKey = "KnowledgeBase.IngestionStatus"`.

### `KnowledgeError`

```fsharp
type KnowledgeError =
    | InvalidFileType of string
    | FileTooLarge of {| MaxBytes: int64; ActualBytes: int64 |}
    | ExtractionFailed of string
    | StorageError of string
    | NotFound
    | Forbidden
```

## `ToolUp.KnowledgeBase.Server`

### `KnowledgeBase.Server.knowledgeApi`

The ToolUp.Remoting handler. Pass directly to `ServerModule.withGuardedApi`:

```fsharp
let kbModule =
    ServerModule.create "KnowledgeBase"
    |> ServerModule.withGuardedApi KnowledgeBase.Server.knowledgeApi
    |> ServerModule.withDataTypes [ KnowledgeBase.Server.kbDataType ]
```

### `KnowledgeBase.Server.kbDataType`

`DataType` declaration for the KB module:

```fsharp skip=fragment
let kbDataType : DataType = {
    Info = { Id = "Knowledge"; DisplayName = "Knowledge base entries"; Schema = None }
    Id = "Knowledge"
    Detect = fun _ -> true   // accepts any registered content-type for the kb route
    Process = ...
}
```

### `KnowledgeBase.Server.kbVectorisationHandler`

```fsharp
let kbVectorisationHandler : VectorisationHandler = {
    DataTypeId = "Knowledge"
    Vectorise = fun (fileName, dataObject) -> async {
        // routes by content-type, extracts text, chunks, returns TextChunk list
    }
}
```

Register via `RAGServerApp.withVectorisationHandler`.

### `KnowledgeBase.Server.makeIngestionStatusObserver`

Factory for the `IIngestionStatusObserver` registered with `composeWithRAG`:

```fsharp skip=signature
val makeIngestionStatusObserver: unit -> IIngestionStatusObserver
```

Register via `RAGServerApp.withIngestionStatusObserver`.

### `KnowledgeBase.Server.standingContextBuilder`

Opt-in AI prompt builder that reads the team's standing context per outer turn:

```fsharp skip=signature
val standingContextBuilder:
    IBlobStorage -> ILogger option -> SystemPromptBuilder
```

Compose into `AIAssistantServerConfig.SystemPrompt`:

```fsharp
let combinedPrompt =
    SystemPromptBuilder.compose [
        SystemPromptBuilder.fromStatic "..."
        KnowledgeBase.Server.standingContextBuilder blobStorage (Some logger)
        SystemPromptBuilder.activeModuleContext
    ]
```

## `ToolUp.KnowledgeBase.Client`

### `KnowledgeBaseView.narrativeCommitHandler`

```fsharp skip=signature
val narrativeCommitHandler: NarrativeCommitHandler
```

The companion's "Save to Knowledge Base" broker. Set it on `ClientConfig.Handlers.NarrativeCommitHandler` (or let `KnowledgeBaseClientConfig.withKnowledgeBase` do it) so other modules' Save buttons resolve. Phase 13a replaced the legacy `installNarrativeCommit ()` module-load side effect with this value.

### `KnowledgeBaseView.register` / `KnowledgeBaseView.create`

```fsharp skip=signature
val register: unit -> ErasedModule
val create: KnowledgeBaseConfig option -> ErasedModule
```

Returns the KB `ErasedModule` for SDK registration. Multi-page module with `/documents`, `/notes`, `/platform-library`, `/ai-context` pages. `create` is the re-brandable form behind `ConfiguredKnowledgeBase`; `create None` is `register ()`.

### `KnowledgeBaseMode` / `KnowledgeBaseClientConfig.withKnowledgeBase`

```fsharp skip=signature
// namespace ToolUp.KnowledgeBase
type KnowledgeBaseConfig = { Name: string; Icon: ReactElement; Group: string option }

type KnowledgeBaseMode =
    | NoKnowledgeBase
    | DefaultKnowledgeBase
    | ConfiguredKnowledgeBase of KnowledgeBaseConfig
    | ExternalKnowledgeBase of ErasedModule

// module ToolUp.KnowledgeBase.Client.KnowledgeBaseClientConfig
val appendKnowledgeBaseModule: KnowledgeBaseMode -> ErasedModule list -> ErasedModule list
val withNarrativeCommitHandler: KnowledgeBaseMode -> ClientConfig -> ClientConfig
val withKnowledgeBase: KnowledgeBaseMode -> ClientConfig -> ErasedModule list -> ClientConfig * ErasedModule list
```

The four-case override mode parallel to `DataManagerMode`. Applies client-side only; server wiring stays a composition-root concern. See [extending.md](extending.md#knowledgebasemode--first-class-substitution) for the per-case table and the three integration contracts an `ExternalKnowledgeBase` must honour.

### `Toolup.NarrativeCommit` (global submitter)

```fsharp skip=signature
module Toolup.NarrativeCommit =
    val install: handler: (NarrativeCommitRequest -> Async<unit>) -> unit
    val submit: NarrativeCommitRequest -> unit       // fire-and-forget; logs on failure

and NarrativeCommitRequest = {
    Title: string
    Body: string
    SourceModule: string
}
```

Other modules call `Toolup.NarrativeCommit.submit` to push content into KB. No compile-time dependency on KB.

### `KnowledgeBaseIcons`

```fsharp skip=signature
module KnowledgeBaseIcons =
    val documentIcon: ReactElement
    val noteIcon: ReactElement
    val aiContextIcon: ReactElement
    val resetIcon: ReactElement
```

Exposed for apps replacing the built-in KB module — re-use the same icon set.

## Events emitted to `IEventStore`

Under `SourceModule = "_platform.kb"`:
- `DocumentUploaded`
- `DocumentDeleted`
- `DocumentExtractionFailed`
- `KnowledgeBaseReset`
- `AIContextUpdated`

Under `SourceModule = "_platform.ingestion"` (forwarded from RAG):
- `KnowledgeChunkIndexed`
- `KnowledgeChunkFailed`

## Notifications published

Notification kind: `"KnowledgeBase.IngestionStatus"`. Payload: `IngestionStatusUpdate`. Subscribers:
- KB Documents page (per-document status pill).
- AI assistant side panel (indexing-in-progress indicator).

## HTTP endpoints

Auto-injected by `ServerModule.withGuardedApi`:

- `POST /api/IKnowledgeApi/UploadDocument`
- `POST /api/IKnowledgeApi/ListDocuments`
- `POST /api/IKnowledgeApi/GetDocument`
- `POST /api/IKnowledgeApi/DeleteDocument`
- `POST /api/IKnowledgeApi/ResetKnowledgeBase`
- `POST /api/IKnowledgeApi/IngestNarrative`
- `POST /api/IKnowledgeApi/AddNote`
- `POST /api/IKnowledgeApi/UpdateNote`
- `POST /api/IKnowledgeApi/GetAIContext`
- `POST /api/IKnowledgeApi/SetAIContext`
- `POST /api/IKnowledgeApi/GetIngestionStatus`

All gated by `makePermissionGuardedApi` against the caller's `ModulePermissions`.

## Blob layout

Per team scope (`team-{teamId}/`):
- `kb-documents/{documentId}.{ext}` — original file bytes
- `kb-documents/{documentId}.meta.json` — `KnowledgeDocument` metadata
- `kb-ai-context/entries.json` — `AIContextEntry list`

`Reset` deletes everything under `kb-documents/` and `kb-ai-context/` for the scope.

## Configuration knobs

Server-side (in `KnowledgeBase.Server`):
- `MaxFileSizeBytes` (default `100 * 1024 * 1024` = 100 MB)
- `AllowedContentTypes` (default: `application/pdf`, `application/vnd.openxmlformats-officedocument.*`, `text/plain`, `text/csv`, `text/markdown`, `application/json`)
- `ChunkingConfig` — passes through to RAG; overrides the RAG default for KB documents specifically.

These aren't currently first-class fluent builders on `RAGServerApp`. Customise by replacing the shipped vectorisation handler with a custom one (see [extending.md](extending.md)).
