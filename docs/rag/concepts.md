# Concepts

How ToolUp.RAG works under the hood.

## Pipeline overview

End-to-end flow for retrieval:

```
user query
   │
   ▼
IEmbeddingProvider.GenerateEmbedding  ──┐
   │                                    │
   ▼                                    ▼
IVectorStore.Search                IEmbeddingCache (LRU; SHA256 keys)
   │
   ▼
scope-access filter (AccessContext.TeamId)
   │
   ▼
MergeStrategy (DenseOnly | SparseOnly | DenseSparseHybrid | DenseSparseRerank)
   │
   ▼
truncate to SnippetCharLimit
   │
   ▼
RAGPromptBuilder.withRetrieval  ──→  injected into AI system prompt
   │
   ▼
IRetrievalTracer.Trace  (records the retrieval to IEventStore)
```

End-to-end flow for ingestion:

```
document upload  ──→  IBlobStorage.Save
                          │
                          ▼  (post-save hook)
                       VectorisationHandler.Vectorise
                          │
                          ▼  IngestionQueue.Enqueue
                          ▼
                    IngestionBackgroundService dequeues
                          │
                          ▼  IEmbeddingProvider.GenerateEmbedding
                          ▼  IVectorStore.Index (stamps EmbeddingVersion)
                          ▼  emits KnowledgeChunkIndexed event
```

End-to-end flow for re-embedding (model swap):

```
operator swaps IEmbeddingProvider  ──→  ReembeddingQueue.Enqueue scope
                                            │
                                            ▼
                                      ReembeddingBackgroundService
                                            │
                                            ▼  IVectorStore.ListChunks scope
                                            ▼  filter where EmbeddingVersion ≠ current
                                            ▼  re-embed + Index
                                            ▼  emits KnowledgeChunkReembedded event
```

## Vector store

`IVectorStore` is the storage interface:

```fsharp
type IVectorStore =
    abstract Index: VectorScope -> ChunkVector -> Async<unit>
    abstract Search: VectorScope list -> queryVec: float32[] -> topK: int -> minScore: float -> Async<VectorMatch list>
    abstract DeleteChunk: VectorScope -> chunkId: Guid -> Async<unit>
    abstract DeleteByScope: VectorScope -> Async<unit>
    abstract Vacuum: VectorScope -> retainTombstones: TimeSpan -> Async<int>
    abstract ListChunks: VectorScope -> Async<ChunkVector list>
    abstract ListScopes: unit -> Async<VectorScope list>

and ChunkVector = {
    Id: Guid
    Text: string
    Vector: float32[]
    Metadata: Map<string, string>   // includes _embedProvider / _embedModel / _embedDim
    Origin: ChunkOrigin
}

and VectorMatch = {
    Chunk: ChunkVector
    Score: float
}

and VectorScope =
    | Platform               // universally readable for authenticated callers (when PlatformKnowledgeBase enabled)
    | Deployment             // universally readable across teams; rare
    | Team of teamId: string
    | User of userId: string

and ChunkOrigin = UserContent | Narrative | Note | Synthetic
```

### Default `InMemoryVectorStore`

- Pre-normalised vectors (cosine similarity = dot product).
- Debounced `IBlobStorage` persistence — `Index` writes to memory immediately, persists after a 5-second debounce.
- `IDisposable` lifecycle — final flush on shutdown so no chunks are lost.
- Soft-delete via `_deletedAt` tombstones — `DeleteChunk` writes a tombstone; subsequent `Search` filters them out.
- `Vacuum` hard-removes tombstones past the retention window.
- `DeleteByScope` is a config-grade reset — bypasses tombstone semantics for "delete everything in scope X" (e.g., crypto-shred companion's scope-key destruction).

Ceiling: ~50,000 chunks before query latency becomes noticeable (1M+ comparisons per search). For larger corpora, swap to `ToolUp.VectorStores.Hnsw`.

### `ToolUp.VectorStores.Hnsw`

Hierarchical Navigable Small World index. Approximate nearest-neighbour; trades ~5% recall accuracy for orders-of-magnitude speedup. Persists the index to `IBlobStorage` for warm restart. Suitable for low-millions of chunks per scope.

For multi-million-chunk or horizontal-scale deployments, a distributed vector store (Qdrant, Pinecone, Weaviate, pgvector) is the right target — future companion work. The `IVectorStore` contract is portable; the `IVectorStoreContract` test pack ensures any impl is drop-in.

## Retrieval pipeline

`IRetrievalPipeline` is the high-level facade:

```fsharp
type IRetrievalPipeline =
    abstract Retrieve: RetrievalRequest -> AccessContext -> Async<VectorMatch list>
    abstract Index: VectorScope -> TextChunk -> Async<unit>

and RetrievalRequest = {
    Query: string
    RequestedScopes: VectorScope list   // caller's intent
    TopK: int
    MinScore: float
    MergeStrategy: MergeStrategy
    OriginFilter: ChunkOrigin list option
}

and MergeStrategy =
    | DenseOnly                  // cosine similarity only
    | SparseOnly                 // BM25 only (term overlap)
    | DenseSparseHybrid          // weighted combination
    | DenseSparseRerank          // hybrid + cross-encoder reranker (when IReranker is registered)
```

### Stages per `Retrieve` call

1. **Scope-access validation** — `authorisedScopes` filters `RequestedScopes` against `AccessContext.TeamId`. A mismatched `Team teamId` is dropped (not errored). `Platform` and `Deployment` scopes survive when enabled.
2. **Embedding generation** — `IEmbeddingProvider.GenerateEmbedding` produces the query vector. Goes through `CachingEmbeddingProvider` decorator (LRU, keyed by SHA256 of text — raw query never lands in cache key).
3. **Dense search** — `IVectorStore.Search` against the authorised scopes, top-K with `MinScore` floor.
4. **Sparse search** (if `MergeStrategy` includes it) — BM25 against the same scopes, top-K.
5. **Merge** — weighted combination per strategy. Hybrid default: 70% dense + 30% sparse.
6. **Rerank** (if `IReranker` registered + strategy uses it) — cross-encoder rescore on the merged candidate pool, top-K from reranked.
7. **Origin filter** — drop chunks whose `Origin` isn't in `OriginFilter`.
8. **Snippet truncation** — chunk text truncated to `SnippetCharLimit` before return.

### Scope isolation guarantees

The scope-access filter at stage 1 is the only choke-point. There's no API path that bypasses it — `IRetrievalPipeline.Retrieve` is the single entry. A misuse pattern would be a custom retrieval impl that doesn't filter, but the SDK's default does, and the contract test pack enforces it.

Result: Team A's vector store entries are never readable by Team B's caller, even if Team B's caller knows the document IDs. The chunk-level isolation is structural.

## Chunking

`Chunking.fs` handles the text → chunk transformation:

```fsharp
type ChunkingConfig = {
    MaxTokens: int           // default 500
    OverlapTokens: int       // default 50
    MinTokens: int           // default 100
}

module Chunking =
    val splitBySentence: string -> string list
    val splitByTokens: ChunkingConfig -> ITokenCounter -> string -> string list
    val chunkSpreadsheet: SheetData -> ChunkingConfig -> ITokenCounter -> string list
    val formatRow: string list -> string -> string
    val withContextualHeader: ITextSummariser option -> string -> string -> string
```

### Token-aware splitting

`ITokenCounter` abstracts token counting:

```fsharp
type ITokenCounter =
    abstract CountTokens: string -> int
```

Default `HeuristicTokenCounter` uses ~4 chars/token (good enough for English; less accurate for code or non-Latin scripts).

For accurate counts, a `Microsoft.ML.Tokenizers` companion can drop in as a one-line wrapper. The contract test pack ensures any counter is drop-in.

### Spreadsheet chunking

Sheets need special treatment — preserving headers and row context across chunks. `chunkSpreadsheet` repeats column headers per chunk and emits a `Sheet "X", rows N–M of T` header line so the model knows which slice of the sheet it's reading. Maintains 1-based row indices in the chunked output.

### Contextual headers (deferred / optional)

`ITextSummariser` is an optional extension point — given the whole document, produce a one-sentence summary that prepends every chunk. Helps the model when individual chunks lose document-level context ("...as discussed above"). The default impl is a no-op; a real impl would invoke an LLM to summarise, which costs tokens. Wire only when retrieval quality justifies the cost.

## Embedding providers

`IEmbeddingProvider` is the boundary:

```fsharp
type IEmbeddingProvider =
    abstract GenerateEmbedding: text: string -> Async<float32[]>
    abstract ProviderId: string
    abstract ModelId: string
    abstract Dimensions: int
```

`EmbeddingVersion` is the `(ProviderId, ModelId, Dimensions)` triple. Stamped onto every chunk's metadata at upsert time so a future model swap is detectable post-hoc.

### Caching layer

`CachingEmbeddingProvider` decorator wraps any provider with an `IEmbeddingCache`:

```fsharp
type IEmbeddingCache =
    abstract TryGet: providerId: string -> modelId: string -> dimensions: int -> textHash: string -> float32[] option
    abstract Set: providerId: string -> modelId: string -> dimensions: int -> textHash: string -> float32[] -> unit
    abstract HitRate: float
```

Default `InMemoryEmbeddingCache` is a coarse-locked LRU (capacity 10000). Cache keys are `(ProviderId, ModelId, Dimensions, SHA256(text))` — raw text never lands in keys. Hit rate exposed for observability.

The cache helps a lot when the same query / chunk text recurs (e.g., re-embedding a document that previously embedded). Distributed Redis-backed cache is a possible companion; the `IEmbeddingCache` contract is portable.

## Background services

Two background services run when RAG is enabled, plus an opt-in scheduled tombstone vacuum:

### `IngestionBackgroundService`

Drains the `IngestionQueue`, indexes each job via the pipeline. Concurrency-capped via `SemaphoreSlim` (default 2; `withIngestionConcurrency` to tune). Emits:

- `KnowledgeChunkIndexed` — successful index.
- `KnowledgeChunkFailed` — embedding or storage failure; retried per policy.

Queue capacity: the `IngestionQueue` is **always bounded** (default capacity 5000; `withIngestionQueueCapacity N` to tune). The channel uses `FullMode = Wait`, so the non-blocking `Enqueue` returns `false` when the queue is full — it never silently drops the incoming job. Upload sites surface that rejection: KB's `UploadDocument` / `AddNote` / `IngestNarrative` mark the affected document `Failed`, `IRagTelemetry` records the rejection, and `/health/rag` exposes live queue depth — so a burst ingestion (e.g. a 10K-document bulk upload) applies visible back-pressure instead of OOMing or losing documents silently.

### `ReembeddingBackgroundService`

Drains the `ReembeddingQueue` (a `Channel<VectorScope>`). For each enqueued scope:

1. List all chunks via `IVectorStore.ListChunks scope`.
2. Filter chunks whose `EmbeddingVersion` (from chunk metadata) doesn't match the current provider's.
3. For each mismatched chunk: re-embed via the current provider, re-index (overwrites the old vector).

Emits `KnowledgeChunkReembedded` / `KnowledgeChunkReembedFailed`.

Trigger on operator action (model swap, scope reset, periodic refresh). The default isn't to re-embed on every chunk access — too expensive. The pattern is: operator decides "I'm changing embedding model"; calls `reembeddingQueue.Enqueue(Team teamId)` for each affected scope; background service drains over the next minutes/hours.

### Tombstone auto-vacuum (Phase 14w) — the steady-state-memory contract

`IVectorStore.DeleteChunk` is a **soft-delete**: it stamps `_deletedAt` and keeps the entry so `RestoreChunk` can un-delete within the retention window. `Vacuum(scope, olderThan)` hard-removes tombstones past that window. Left un-driven, tombstones accumulate for the whole process lifetime and a long-running replica's memory grows without bound — soft-delete on its own does **not** bound memory.

`RAGServerApp.withVacuumSchedule` closes that gap. It registers a `RAGVacuumJobHandler` on the `IJobScheduler` under `_platform.rag.tombstone-vacuum`, triggered by a cron (default **daily 03:00 UTC**; override with `withVacuumScheduleCron "<5-field cron>"`). On each tick the handler enumerates `IVectorStore.ListScopes()` and calls `Vacuum(scope, now - retention)` per scope. Retention defaults to **7 days** (`withTombstoneRetention`, floored at one minute). Every scope that purges anything emits a `KnowledgeVacuumCompleted` audit event with `(ScopeKey, ChunksRemoved, BytesReclaimed, DurationMs)`.

**Deployment contract — required for production:**

- Set `ServerConfig.JobScheduler = InProcessJobScheduler` (or a distributed scheduler companion) **and** call `RAGServerApp.withVacuumSchedule`. Without both, memory does not stabilise: with the schedule but no scheduler the sweep never fires; with neither, tombstones are reclaimed only by a manual `IVectorStore.Vacuum` call from your own admin path.
- The `VacuumScheduleValidator` warns at startup in both misconfigured cases (visible in the HealthMonitorUI admin tab / `/dev/inspect` Validators panel), so the gap is loud rather than silent.
- `ListScopes()` on the in-memory default returns only scopes loaded so far this process lifetime — a cold scope is vacuumed on the first tick after it loads, not before. This is a latency, not a correctness, caveat: nothing is ever purged before its retention window elapses.

```fsharp
RAGServerApp.create factory providerProfile embedder
|> RAGServerApp.withConfig { config with JobScheduler = InProcessJobScheduler }
|> RAGServerApp.withTombstoneRetention (TimeSpan.FromDays 14.0)   // optional — default 7 days
|> RAGServerApp.withVacuumSchedule                                // daily 03:00 UTC
|> RAGServerApp.run
```

## RAG prompt builder

`RAGPromptBuilder.withRetrieval` is a `SystemPromptBuilder` that injects retrieved chunks:

```fsharp
let ragBuilder = RAGPromptBuilder.withRetrieval pipeline {
    TopK = 5
    MinScore = 0.3
    GroundingMode = Preferred
    ScopeStrategy = ActiveTeamPlusPlatform
}

AIServerApp.create (aiProviderFactory, aiConfigStore)
|> ...
|> AIServerApp.withAIConfig {
    AIAssistantServerConfig.defaults with
        SystemPrompt = Some ragBuilder
}
|> AIServerApp.run
```

The builder runs per chat request. It:
1. Reads the user's latest message as the query.
2. Calls `pipeline.Retrieve` for the appropriate scopes.
3. Truncates each chunk to `SnippetCharLimit`.
4. Formats them into a "Retrieved context" section in the system prompt.
5. Cites each chunk with a short identifier (`[Doc abc, Chunk 1 of 5]`).
6. Returns the formatted string.

When `RAGCompose.composeWithRAG` is in the pipeline, this builder is automatically composed with the default system-prompt builders (platform + active module). Apps that want full control over composition can wire it via `withAIConfig` manually.

### Grounding modes

```fsharp
type GroundingMode =
    | Permissive         // retrieve, inject if found, assistant can answer with or without
    | Preferred          // retrieve, inject if found, system prompt nudges the model to prefer retrieved context
    | StrictlyGrounded   // retrieve, refuse to answer if nothing retrieved
```

`StrictlyGrounded` is the strongest signal — useful when hallucinations are unacceptable. The system prompt explicitly tells the model "answer only from retrieved context; refuse otherwise". `Permissive` is the loosest; useful when retrieval is one of many input sources.

### Retrieval miss

When fewer than 2 matches survive the `MinScore` gate, the prompt builder emits a `KnowledgeRetrievalMiss` event via `IRetrievalTracer.Miss`. Operators surface this in the `HealthMonitor` admin module to detect deployments where the knowledge base is empty or the embedding model is mismatched.

## Observability

The `IRetrievalTracer` interface:

```fsharp
type IRetrievalTracer =
    abstract Trace: RetrievalTrace -> AccessContext -> Async<unit>
    abstract Miss: scope: VectorScope -> queryHash: string -> Async<unit>

and RetrievalTrace = {
    QueryHash: string            // SHA256; never plaintext
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

Two shipped tracers:
- `NoOpRetrievalTracer` — zero-cost opt-out.
- `EventStoreRetrievalTracer` (default) — writes `KnowledgeRetrieved` events to `IEventStore` under `_platform.retrieval`.

The `KnowledgeRetrieved` event type is a wire-format contract. Use it for retrieval-quality monitoring, replay debugging (with the QueryHash as the dedup key), and slow-query investigation.

`/health/rag` exposes a 60-second rolling-window snapshot from `IRagTelemetry`: embedding latency, ingestion queue depth, flush latency, retrieval hit / miss / empty counts. The `HealthMonitorUI` admin module surfaces it for operators.

## Multi-modal extensions (deferred)

The `IOcrProvider`, `ITableExtractor`, and `IImageEmbedder` extension points reserve the surface for future multimodal work:

- `IOcrProvider` — scanned-document OCR. Default no-op (assumes documents are text-extractable).
- `ITableExtractor` — table extraction with structure preserved. Default no-op.
- `IImageEmbedder` — CLIP-style image vectors in a shared modality space. No default registered (no honest no-op).

When `IAIProvider.AIProviderMessage.Content` is widened to support multimodal content blocks (future SDK version), the image-embedding path will plug in alongside text embedding for cross-modal retrieval.
