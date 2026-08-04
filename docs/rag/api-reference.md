# API reference

Public surface of `ToolUp.RAG`. Types are listed by package.

## `ToolUp.RAG.Core`

### `IngestionTypes`

```fsharp skip=fragment
type IngestionJob = {
    DocumentId: Guid
    Scope: VectorScope
    Chunks: TextChunk list
}

type IngestionQueue =
    member Enqueue: IngestionJob -> Async<Result<unit, IngestionEnqueueError>>
    member Dequeue: CancellationToken -> Async<IngestionJob>
    member Count: int
```

Channel-backed unbounded queue (bounded with `withIngestionQueueCapacity`). Thread-safe enqueue; single-reader dequeue served by the background service.

### `IIngestionStatusObserver`

```fsharp
type IIngestionStatusObserver =
    abstract OnJobAccepted: IngestionJob -> Async<unit>
    abstract OnChunkIndexed: jobId: Guid -> chunkId: Guid -> Async<unit>
    abstract OnJobCompleted: jobId: Guid -> chunkCount: int -> Async<unit>
    abstract OnJobFailed: jobId: Guid -> reason: string -> Async<unit>
```

Optional observer for ingestion lifecycle. `ToolUp.KnowledgeBase` registers one to surface per-document status in the UI. Apps without KB skip it.

## Shared types in `ToolUp.Platform.Core`

(Re-shown here for reference; defined in core.)

### `VectorKnowledgeTypes`

```fsharp
type VectorScope =
    | Platform
    | Deployment
    | Team of teamId: string
    | User of userId: string

type ChunkOrigin = UserContent | Narrative | Note | Synthetic

type TextChunk = {
    Id: Guid
    Text: string
    Metadata: Map<string, string>
    Origin: ChunkOrigin
}

type ChunkVector = {
    Id: Guid
    Text: string
    Vector: float32[]
    Metadata: Map<string, string>  // includes _embedProvider / _embedModel / _embedDim
    Origin: ChunkOrigin
}

type VectorMatch = {
    Chunk: ChunkVector
    Score: float
}

type MergeStrategy =
    | DenseOnly
    | SparseOnly
    | DenseSparseHybrid
    | DenseSparseRerank

type RetrievalRequest = {
    Query: string
    RequestedScopes: VectorScope list
    TopK: int
    MinScore: float
    MergeStrategy: MergeStrategy
    OriginFilter: ChunkOrigin list option
}
```

### `VectorisationHandler`

```fsharp skip=fragment
type VectorisationHandler = {
    DataTypeId: string
    Vectorise: fileName: string -> dataObject: obj -> Async<TextChunk list>
}
```

Modules declare one in `Server.fs` for each `DataTypeId` they want indexed. The post-save hook invokes `Vectorise` after a successful save; returned chunks enqueue.

## `ToolUp.RAG.Server`

### `RAGServerApp`

Flat superset of `AIServerApp`. The fluent shape:

```fsharp
type RAGServerApp = {
    AI: AIServerApp
    EmbeddingProvider: IEmbeddingProvider
    VectorisationHandlers: VectorisationHandler list
    RetrievalConfig: RetrievalConfig
    IngestionConfig: IngestionConfig
    Telemetry: IRagTelemetry option
    RetrievalTracer: IRetrievalTracer option
}
```

Constructors:

```fsharp skip=signature
module RAGServerApp =
    val create: AIProviderFactory * IUserAIConfigStore * IEmbeddingProvider -> RAGServerApp
    val empty: RAGServerApp                  // requires all three withFactory/withConfigStore/withEmbedder before run
```

Mirrored `AIServerApp` builders (all `withConfig`, `withAuth`, `withStorage`, ... `withAITools`, `withAIConfig`, `withModuleAIContexts`, etc.).

RAG-specific builders:
- `withVectorisationHandler: VectorisationHandler -> RAGServerApp -> RAGServerApp`
- `withTopK: int -> RAGServerApp -> RAGServerApp` (default 5)
- `withMinScore: float -> RAGServerApp -> RAGServerApp` (default 0.3)
- `withMergeStrategy: MergeStrategy -> RAGServerApp -> RAGServerApp` (default `DenseOnly`)
- `withSnippetCharLimit: int -> RAGServerApp -> RAGServerApp` (default 1500)
- `withOriginFilter: ChunkOrigin list option -> RAGServerApp -> RAGServerApp` (default `None`)
- `withGroundingMode: GroundingMode -> RAGServerApp -> RAGServerApp` (default `Permissive`)
- `withIngestionConcurrency: int -> RAGServerApp -> RAGServerApp` (default 2)
- `withIngestionQueueCapacity: int option -> RAGServerApp -> RAGServerApp` (default `None` = unbounded)
- `withTelemetry: IRagTelemetry -> RAGServerApp -> RAGServerApp`
- `withRetrievalTracer: IRetrievalTracer -> RAGServerApp -> RAGServerApp`
- `withVectorStore: IVectorStore -> RAGServerApp -> RAGServerApp` (default `InMemoryVectorStore`)
- `withEmbeddingCache: IEmbeddingCache -> RAGServerApp -> RAGServerApp`
- `withTableExtractor: ITableExtractor -> RAGServerApp -> RAGServerApp`
- `withReranker: IReranker -> RAGServerApp -> RAGServerApp`

> `IOcrProvider` has no `with*` builder: `composeWithRAG` probes DI for an already-registered provider and falls back to the no-op only when it finds none, so an OCR companion is composed by `services.AddSingleton<IOcrProvider>(...)` before the RAG composition runs. See [`companions/ocr-providers.md`](../companions/ocr-providers.md).
- `withTextSummariser: ITextSummariser -> RAGServerApp -> RAGServerApp`

Terminal:
- `run: RAGServerApp -> int`

## Server-side interfaces (in `ToolUp.Platform.Core` / `Server`)

### `IEmbeddingProvider`

```fsharp
type IEmbeddingProvider =
    abstract GenerateEmbedding: text: string -> Async<float32[]>
    abstract ProviderId: string
    abstract ModelId: string
    abstract Dimensions: int
```

`EmbeddingVersion` is the `(ProviderId, ModelId, Dimensions)` triple. Stamped onto every chunk's metadata at index time.

### `IVectorStore`

```fsharp
type IVectorStore =
    abstract Index: VectorScope -> ChunkVector -> Async<unit>
    abstract Search: scopes: VectorScope list -> queryVec: float32[] -> topK: int -> minScore: float -> Async<VectorMatch list>
    abstract DeleteChunk: VectorScope -> chunkId: Guid -> Async<unit>
    abstract DeleteByScope: VectorScope -> Async<unit>
    abstract Vacuum: VectorScope -> retainTombstones: TimeSpan -> Async<int>
    abstract ListChunks: VectorScope -> Async<ChunkVector list>
    abstract ListScopes: unit -> Async<VectorScope list>
```

### `IRetrievalPipeline`

```fsharp
type IRetrievalPipeline =
    abstract Retrieve: RetrievalRequest -> AccessContext -> Async<VectorMatch list>
    abstract Index: VectorScope -> TextChunk -> Async<unit>
```

### `IEmbeddingCache`

```fsharp
type IEmbeddingCache =
    abstract TryGet: providerId: string -> modelId: string -> dimensions: int -> textHash: string -> float32[] option
    abstract Set: providerId: string -> modelId: string -> dimensions: int -> textHash: string -> float32[] -> unit
    abstract HitRate: float
```

### `IOcrProvider`

```fsharp
type IOcrProvider =
    abstract IsScanned: documentBytes: byte[] -> Async<bool>
    abstract ExtractText: documentBytes: byte[] -> Async<PageText list>

and PageText = { Page: int; Text: string }
```

### `ITableExtractor`

```fsharp
type ITableExtractor =
    abstract ExtractTables: documentBytes: byte[] -> Async<ExtractedTable list>

and ExtractedTable = {
    Page: int
    Headers: string list
    Rows: string list list
}
```

### `IImageEmbedder`

```fsharp
type IImageEmbedder =
    abstract EmbedImage: imageBytes: byte[] -> Async<float32[]>
    abstract EmbedQuery: text: string -> Async<float32[]>
    abstract Dimensions: int
    abstract ProviderId: string
    abstract ModelId: string
```

### `IRetrievalTracer`

```fsharp
type IRetrievalTracer =
    abstract Trace: RetrievalTrace -> AccessContext -> Async<unit>
    abstract Miss: scope: VectorScope -> queryHash: string -> Async<unit>

and RetrievalTrace = {
    QueryHash: string
    QueryLength: int
    RequestedScopes: VectorScope list
    PermittedScopes: VectorScope list
    TopK: int
    CandidatePoolSize: int
    TopScore: float
    Dense: bool
    Sparse: bool
    Reranked: bool
    LatencyMs: int
    Stages: string list
    ResultCount: int
}
```

### `ITextSummariser`

```fsharp
type ITextSummariser =
    abstract Summarise: text: string -> Async<string>
```

Optional. Default unregistered = no contextual headers prepended.

### `IReranker`

```fsharp
type IReranker =
    abstract Rerank: query: string -> candidates: VectorMatch list -> topK: int -> Async<VectorMatch list>
```

Optional. Required only when `MergeStrategy = DenseSparseRerank`.

### `IRagTelemetry`

```fsharp
type IRagTelemetry =
    abstract RecordEmbeddingLatency: ms: int -> unit
    abstract RecordIngestionFlush: ms: int -> chunkCount: int -> unit
    abstract RecordRetrievalHit: scope: VectorScope -> unit
    abstract RecordRetrievalMiss: scope: VectorScope -> unit
    abstract RecordRetrievalEmpty: scope: VectorScope -> unit
    abstract Snapshot: unit -> RagTelemetrySnapshot
```

## Chunking

```fsharp skip=signature
type ChunkingConfig = {
    MaxTokens: int           // default 500
    OverlapTokens: int       // default 50
    MinTokens: int           // default 100
}

type ITokenCounter =
    abstract CountTokens: string -> int

module Chunking =
    val splitBySentence: string -> string list
    val splitByTokens: ChunkingConfig -> ITokenCounter -> string -> string list
    val chunkSpreadsheet: SheetData -> ChunkingConfig -> ITokenCounter -> string list
    val formatRow: string list -> string -> string
    val withContextualHeader: ITextSummariser option -> string -> string -> string

and SheetData = {
    SheetName: string
    Headers: string list
    Rows: string list list   // each row is a list of cell values
}
```

## `RAGPromptBuilder`

```fsharp skip=signature
module RAGPromptBuilder =
    val withRetrieval: pipeline: IRetrievalPipeline -> config: RAGPromptConfig -> SystemPromptBuilder

and RAGPromptConfig = {
    TopK: int
    MinScore: float
    GroundingMode: GroundingMode
    ScopeStrategy: ScopeStrategy
    SnippetCharLimit: int
}

and GroundingMode =
    | Permissive
    | Preferred
    | StrictlyGrounded

and ScopeStrategy =
    | ActiveTeamOnly
    | ActiveTeamPlusUser
    | ActiveTeamPlusPlatform
    | All                          // every readable scope; rare
    | Custom of (AccessContext -> Async<VectorScope list>)
```

## `composeWithRAG`

```fsharp skip=signature
module RAGCompose =
    val composeWithRAG:
        ai: AIServerApp ->
        embedder: IEmbeddingProvider ->
        vectorisationHandlers: VectorisationHandler list ->
        retrievalConfig: RetrievalConfig ->
        ingestionConfig: IngestionConfig ->
        int
```

Called internally by `RAGServerApp.run`. Wraps the embedder with `CachingEmbeddingProvider`, registers the ingestion + reembedding background services, wires the retrieval prompt builder into the AI compose, and runs.

## Events emitted to `IEventStore`

Under `SourceModule = "_platform.ingestion"`:
- `KnowledgeChunkIndexed`
- `KnowledgeChunkFailed`
- `KnowledgeChunkReembedded`
- `KnowledgeChunkReembedFailed`

Under `SourceModule = "_platform.retrieval"`:
- `KnowledgeRetrieved` (carries `RetrievalTrace`)
- `KnowledgeRetrievalMiss`

## HTTP endpoints

Auto-injected by `RAGServerApp.run`:

- `GET /health/rag` — JSON snapshot (embedding provider id/model/dimensions, vector-store status, ingestion-queue depth, rolling-window stats from `IRagTelemetry`)

When `EnableDevEndpoints` is true and the RAG layer is active:
- `GET /dev/rag` — broader diagnostic snapshot
- `GET /dev/rag/scopes` — list of scopes with chunk counts (admin-gated)

## Configuration knobs

All set via the `RAGServerApp.with*` builders documented above.

Environment variables (read by embedding providers via `ISecretStore`, never directly):
- `OPENAI_API_KEY` (for `OpenAIEmbeddingProvider`) — store in `ISecretStore` under `_platform` scope.

## Conformance test pack

`ToolUp.Platform.Tests` ships:
- `IVectorStoreContract` — N tests; any vector-store impl passes the same set.
- `IRetrievalPipelineContract` — covers scope-isolation, top-K, min-score, merge strategies.
- `IEmbeddingProviderContract` — minimal interface check.

External impls consume the test pack as `<PackageReference>` and run it against their impl in their own test suite.
