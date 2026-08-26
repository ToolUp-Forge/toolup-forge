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

```fsharp skip=signature
type IIngestionStatusObserver =
    /// Fired after a chunk has been successfully indexed by `IRetrievalPipeline`.
    abstract OnChunkIndexed: IngestionJob -> Async<unit>
    /// Fired when a chunk's pipeline call threw. `error` is the exception message.
    abstract OnChunkFailed: IngestionJob * error: string -> Async<unit>
```

Optional observer for ingestion lifecycle. The job itself carries the identity an observer needs, so there are no bare job-id / chunk-id parameters, and no accepted / completed callbacks — completion is derived from the indexed count reaching the job's total. `ToolUp.KnowledgeBase` registers one to surface per-document status in the UI. Apps without KB skip it; several observers can coexist via `RAGServerApp.withIngestionObservers`.

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

The record is wide and flat — every knob is a field with a `with*` builder, rather than nested config records. Abbreviated to the shape-defining fields:

```fsharp skip=signature
type RAGServerApp = {
    AI: AIServerApp
    EmbeddingProvider: IEmbeddingProvider
    EmbeddingCache: IEmbeddingCache option
    IngestionObservers: IIngestionStatusObserver list
    Reranker: IReranker option
    VectorStore: IVectorStore option
    SparseAnalyzer: ISparseAnalyzer option
    RetrievalDefaults: RetrievalDefaults
    RetrievalPipelineOverride: IRetrievalPipeline option
    GroundingMode: GroundingMode
    Telemetry: IRagTelemetry option
    IngestionConcurrency: int
    IngestionQueueCapacity: int
    IngestionRetryPolicy: IngestionRetryPolicy
    OverflowPolicy: IngestionOverflowPolicy
    // ... plus the MMR, citation-policy, size-cap, tombstone-retention,
    // conversation-indexing and ingestion-recovery fields
}
```

Vectorisation handlers are **not** a `RAGServerApp` field — each module contributes its own via `ServerModule.withVectorisation`, and they aggregate on the inner `ServerApp`.

Constructors:

```fsharp skip=signature
module RAGServerApp =
    val create: IAIProviderFactory -> IProviderProfile -> IEmbeddingProvider -> RAGServerApp
    val createFrom: IAIProviderFactory -> IProviderProfile -> IEmbeddingProvider -> ServerApp -> RAGServerApp
    val empty: RAGServerApp                  // requires all three withFactory/withConfigStore/withEmbedder before run
```

Mirrored `AIServerApp` builders (all `withConfig`, `withAuth`, `withStorage`, ... `withAITools`, `withAIConfig`, `withModuleAIContexts`, etc.).

RAG-specific builders:
- `withTopK: int -> RAGServerApp -> RAGServerApp` (default 5)
- `withMinScore: float option -> RAGServerApp -> RAGServerApp` (default 0.3)
- `withMergeStrategy: MergeStrategy -> RAGServerApp -> RAGServerApp` (default `DenseOnly`)
- `withSnippetCharLimit: int -> RAGServerApp -> RAGServerApp` (default 1500)
- `withOriginFilter: Set<ChunkOrigin> option -> RAGServerApp -> RAGServerApp` (default `None`)
- `withGroundingMode: GroundingMode -> RAGServerApp -> RAGServerApp` (default `Permissive`)
- `withFactClausePlanning: bool -> RAGServerApp -> RAGServerApp` (default `true`)
- `withFactClausePlanOptions: FactClausePlanOptions -> RAGServerApp -> RAGServerApp` (default on, 3,000 ms budget)

> The two fact-clause builders are inert unless the deployment composed the fact tier, which is what
> registers the `IFactClausePlanner` they control. With it composed, each user turn is compiled into a
> `RetrievalRequest.FactClause` before retrieval, so a question naming registered vocabulary gets its
> facts resolved and pushed into the prompt ahead of the similarity chunks — no tool round-trip. The
> compile can cost a provider call, so it runs under the options' budget and degrades to no clause on
> overrun or fault; pass `withFactClausePlanning false` when the fact tier is composed for its tool
> surface alone and a second model call per chat turn is not wanted.
- `withIngestionConcurrency: int -> RAGServerApp -> RAGServerApp` (default 2)
- `withIngestionQueueCapacity: int -> RAGServerApp -> RAGServerApp` (default unbounded)
- `withTelemetry: IRagTelemetry -> RAGServerApp -> RAGServerApp`
- `withVectorStore: IVectorStore -> RAGServerApp -> RAGServerApp` (default `InMemoryVectorStore`)
- `withEmbeddingCache: IEmbeddingCache -> RAGServerApp -> RAGServerApp`
- `withReranker: IReranker -> RAGServerApp -> RAGServerApp`

> **Seams with no `with*` builder — register them in DI instead.** `composeWithRAG` probes DI for
> `IOcrProvider`, `ITableExtractor` and `IRetrievalTracer`, falling back to its own default only when
> it finds none. So `services.AddSingleton<IOcrProvider>(…)` (etc.) *before* the RAG composition runs
> **is** the opt-in, and the compose surface grows no knob for it. See
> [`companions/ocr-providers.md`](../companions/ocr-providers.md) for the OCR case.
>
> `IQueryRewriter` (conversation-aware query rewrite) is the same shape. Register before the RAG
> composition runs — `services.AddSingleton<IQueryRewriter>(ProviderQueryRewriter.create aiProvider)`
> for the shipped provider-backed rewriter, or any implementation of your own. No registration ⇒ the
> pipeline is byte-for-byte its pre-rewrite self (GP 11 / GP 13); the stage only ever fires for a
> request that carries `RetrievalRequest.History`. Bound it with
> `RetrievalPipelineOptions.QueryRewriteTimeoutMs` (default 2000ms) — on overrun or fault the pipeline
> searches the raw query and records `Failed` on the retrieval trace.
>
> `ITextSummariser` is **not composed at all**. `Chunking.withContextualHeader` takes one as an
> `option` and no forge path calls it, so contextual-header summarisation is a helper a consumer wires
> into its own ingestion, not a composition knob.
>
> `VectorisationHandler` is a per-module DECLARATION, not a builder: a module lists its handlers on
> `ServerModule.VectorisationHandlers` and the composition reads them from the module registry.

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

Chunk ids are `string`, not `Guid`, and there is no `ChunkVector` type — the vector and its `TextChunk` are passed and returned separately. Deletion is a two-stage tombstone: `DeleteChunk` stamps `_deletedAt` and hides the chunk from `Search`, `RestoreChunk` un-hides it within the retention window, and `Vacuum` hard-removes anything older than the instant given.

```fsharp skip=signature
type IVectorStore =
    /// Idempotent on `(scope, chunkId)`; clears any existing tombstone.
    abstract Upsert: scope: VectorScope -> chunkId: string -> vector: float32 array -> chunk: TextChunk -> Async<unit>
    /// Descending score order, merged across scopes, tombstones filtered.
    abstract Search: scopes: VectorScope list -> query: float32 array -> topK: int -> Async<VectorMatch list>
    abstract ListChunks: scope: VectorScope -> includeDeleted: bool -> Async<(string * TextChunk) list>
    abstract DeleteChunk: scope: VectorScope -> chunkId: string -> Async<unit>
    abstract RestoreChunk: scope: VectorScope -> chunkId: string -> Async<unit>
    abstract Vacuum: scope: VectorScope -> olderThan: DateTimeOffset -> Async<int>
    /// Bypasses tombstone semantics — there is no recovery from this.
    abstract DeleteByScope: scope: VectorScope -> Async<unit>
    abstract ListScopes: unit -> Async<VectorScope list>
```

`minScore` is not a store-level parameter: score thresholding is applied by the retrieval pipeline, so a store implementation never has to reason about it.

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

The recorders are deliberately synchronous — they sit on hot paths and are fire-and-forget, the same documented escape from the async-at-every-boundary rule that `ILogger` and `IMetricsSink` take. `Snapshot` is the only async member.

```fsharp skip=signature
type IRagTelemetry =
    abstract RecordEmbedding: texts: int * latencyMs: int64 -> unit
    abstract RecordEnqueue: depth: int * capacity: int * accepted: bool -> unit
    abstract RecordFlush: dirtyChunks: int * latencyMs: int64 -> unit
    abstract RecordIndexLoadError: scopeKey: string -> unit
    abstract RecordRetrievalStages: stageTimings: (string * float) list -> unit
    abstract RecordRetrieval: topScore: float * resultCount: int * minScoreThreshold: float -> unit
    abstract RecordObserverFailure: observerName: string -> unit
    abstract Snapshot: unit -> Async<RagTelemetrySnapshot>
```

Retrieval outcomes are recorded as a score plus a result count rather than as hit / miss / empty counters per scope, so a caller can set its own thresholds after the fact; per-scope detail for index-load failures lives in the `KnowledgeIndexLoadFailed` audit trail rather than the snapshot.

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
