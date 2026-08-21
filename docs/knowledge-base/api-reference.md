# API reference

Public surface of `ToolUp.KnowledgeBase`.

## `ToolUp.KnowledgeBase.Core`

### `KnowledgeApi` (ToolUp.Remoting contract)

Document ids are plain `string`s throughout — there is no `DocumentId` wrapper.

```fsharp
open ToolUp.Platform.Narrative

type KnowledgeApi = {
    UploadDocument: byte[] -> string -> Async<KnowledgeDocument>
    GetDocuments: unit -> Async<KnowledgeDocument list>
    DeleteDocument: string -> Async<Result<unit, string>>
    GetStatus: string -> Async<IngestionStatus>
    IngestNarrative: IngestNarrativeRequest -> Async<Result<KnowledgeDocument, IngestNarrativeError>>
    AddNote: AddNoteRequest -> Async<Result<KnowledgeDocument, string>>
    UpdateNote: UpdateNoteRequest -> Async<Result<KnowledgeDocument, string>>
    GetAIContext: unit -> Async<AIContextEntry option>
    SetAIContext: string -> Async<Result<AIContextEntry, string>>
    ResetIndex: unit -> Async<Result<unit, string>>
    GetSuggestedQuestions: string option -> Async<string list>
    RefreshAIContext: unit -> Async<unit>
    GetOriginalDocument: string -> Async<Result<OriginalDocument, KnowledgeBaseError>>
    GetOriginalDelivery: string -> Async<Result<PreviewContent, KnowledgeBaseError>>
    GetScopeUsage: unit -> Async<KnowledgeScopeUsage>
    GetDocumentVersions: string -> Async<KnowledgeDocumentVersion list>
    ImportBatch: BulkImportRequest -> Async<BulkImportReport>
    SetDocumentTags: SetDocumentTagsRequest -> Async<Result<KnowledgeDocument, string>>
}

and IngestNarrativeRequest = {
    Document: NarrativeDocument
    Overwrite: bool
}

and IngestNarrativeError =
    | MissingProvenance
    /// Carries the stored document so the UI can offer an overwrite prompt.
    | DuplicateExists of existing: KnowledgeDocument
    | IngestFailed of reason: string

and AddNoteRequest = { Title: string; Body: string }

and UpdateNoteRequest = { DocId: string; Title: string; Body: string }

/// The standing AI context is ONE markdown body per scope, not a list of
/// entries. An empty body clears it.
and AIContextEntry = {
    Body: string
    UpdatedAt: DateTimeOffset
    UpdatedBy: string
}
```

Every method is `[<AllowAnonymous>]` except `SetAIContext`, which requires a `scope` claim; `UploadDocument` carries a `[<RateLimit>]`, and `DeleteDocument` / `ResetIndex` carry `[<Audit>]` annotations.

### `KnowledgeDocument`

The failure reason rides the `IngestionStatus` case rather than a separate field.

```fsharp
type KnowledgeDocument = {
    /// Stable lineage identity; also the prefix of every `{Id}:chunk:{n}`.
    Id: string
    FileName: string
    FileType: string
    UploadedAt: DateTimeOffset
    UploadedBy: string
    Status: IngestionStatus
    SizeBytes: int64
    ChunkCount: int
    Source: KnowledgeSource
    /// SHA-256 of the raw bytes, so a scope-local re-upload short-circuits.
    /// `None` for notes, narratives, and pre-dedup documents.
    ContentHash: string option
    /// 1-based version of this lineage. A versioned re-upload supersedes in
    /// place, so the index always describes exactly one live document per
    /// lineage and retrieval structurally targets the current version.
    Version: int
    /// Normalised free-form tags, also stamped onto every chunk as
    /// `_tag.{tag}` so `RetrievalRequest.Filters` can scope to them.
    Tags: string list
}

and KnowledgeSource =
    | UploadedFile
    | FromNarrative of NarrativeDocSource
    | Note of NoteSource

and IngestionStatus =
    | Queued
    | ExtractingText
    | Embedding of chunksProcessed: int * chunksTotal: int
    | Complete of chunkCount: int
    | Failed of reason: string
    /// Refused by the deployment's upload policy before anything was
    /// persisted, so it never appears in `GetDocuments` / `GetStatus`.
    | UploadRejected of reason: string
    /// Stored and downloadable, but no extractor recognised the type — so it
    /// is not searchable. Deliberately distinct from `Complete 0`.
    | UnsupportedFormat of detail: string
```

### `IngestionStatusUpdate` (SSE wire format)

Only terminal outcomes are published, and the payload is deliberately DU-free so another companion can parse it without mirroring `IngestionStatus` across the module boundary.

```fsharp
type IngestionStatusUpdate = {
    DocumentId: string
    FileName: string
    Outcome: string       // "Complete" or "Failed"
    ChunkCount: int       // chunk count when Complete, 0 otherwise
    ErrorReason: string   // reason when Failed, "" otherwise
    UploadedBy: string
}
```

Notification kind: `[<Literal>] IngestionStatusNotificationKey = "KnowledgeBase.IngestionStatus"`.

### `KnowledgeBaseError`

Typed refusals for the original-document reads. Absence and denial are results, never exceptions.

```fsharp
type KnowledgeBaseError =
    /// Not visible in the caller's scope — deliberately indistinguishable from
    /// "does not exist", so an out-of-scope caller cannot probe for existence.
    /// A denial audit is emitted.
    | NotInScope
    /// In scope, but this source kind has no retrievable original (a
    /// module-generated narrative, an AI-context entry) or the blob is gone.
    | NoOriginalAvailable
    | OriginalRetrievalFailed of reason: string
```

Ingestion refusals are not part of this DU: an oversize or disallowed upload comes back as a `KnowledgeDocument` whose `Status` is `UploadRejected reason`.

## `ToolUp.KnowledgeBase.Server`

### `KnowledgeBase.Server.knowledgeApi`

The ToolUp.Remoting handler. Pass directly to `ServerModule.withGuardedApi`:

```fsharp skip=fragment
let kbModule =
    ServerModule.create "KnowledgeBase"
    |> ServerModule.withGuardedApi KnowledgeBase.Server.knowledgeApi
```

`knowledgeApi` takes the `HttpContext` and resolves its dependencies from DI, so it is an ordinary guarded-API handler — there is no `kbDataType` value to register alongside it. The companion owns its own ingestion path rather than routing uploads through a `DataType`.

### Vectorisation

A `VectorisationHandler` is a plain record keyed by data-type id; `Vectorise` is a pure function over the processed payload, not an async callback over a file name:

```fsharp skip=signature
type VectorisationHandler = {
    /// Must match the `DataType.Id` of the module's registered data type.
    DataTypeId: DataTypeId
    /// Return `[]` to skip indexing a particular record.
    Vectorise: ProcessedData -> TextChunk list
    /// Optional whole-document summary chunk, indexed under `_isSummary`
    /// with a retrieval score boost.
    Summarise: (ProcessedData -> TextChunk) option
}
```

Register handlers on the module with `ServerModule.withVectorisation`.

### `KnowledgeBase.Server.makeIngestionStatusObserver`

Builds the `IIngestionStatusObserver` that reflects RAG ingestion progress back into the Knowledge Base's status cache:

```fsharp skip=signature
type IIngestionStatusObserver =
    abstract OnChunkIndexed: IngestionJob -> Async<unit>
    abstract OnChunkFailed: IngestionJob * error: string -> Async<unit>
```

Register it with `RAGServerApp.withIngestionObserver` (or `withIngestionObservers` for several).

### `KnowledgeBase.Server.standingContextBuilder`

Opt-in AI prompt builder that reads the team's standing context per outer turn:

```fsharp skip=signature
val standingContextBuilder: IBlobStorage -> ILogger option -> SystemPromptBuilder
/// Emits a one-paragraph summary of the index for the system prompt.
val kbInventoryBuilder: SystemPromptBuilder
/// The canonical pair of KB system-prompt builders.
val knowledgeBasePromptBuilders: IBlobStorage -> ILogger option -> SystemPromptBuilder list
```

Compose into `AIAssistantServerConfig.SystemPrompt`. Both the builders and `compose` live in the `ToolUp.AI.SystemPromptBuilder` module, so that open is needed alongside `open ToolUp.AI`:

```fsharp skip=fragment
open ToolUp.AI.SystemPromptBuilder

let combinedPrompt =
    SystemPromptBuilder.compose [
        SystemPromptBuilder.fromStatic "..."
        yield! KnowledgeBase.Server.knowledgeBasePromptBuilders blobStorage (Some logger)
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
