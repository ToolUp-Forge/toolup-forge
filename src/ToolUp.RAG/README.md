# ToolUp.RAG

Companion package providing the retrieval-augmented generation pipeline for applications built on [`ToolUp.Platform`](../ToolUp.Platform/). Ships the vector store, retrieval pipeline, background ingestion service, post-save hook wiring, and AI-prompt retrieval builder — everything except the embedding provider itself (a default TF-IDF provider and an OpenAI provider live in sub-companions at [`src/EmbeddingProviders/`](../EmbeddingProviders/), and other providers can be added the same way).

For deep technical detail, see [`TECHNICAL_GUIDE.md`](TECHNICAL_GUIDE.md). This README covers the shape of the package, how to enable RAG in a deployment, and the extension points.

## Why a separate companion

Two reasons it doesn't live in `ToolUp.Platform`:

1. **RAG is an optional platform capability.** Deployments that don't need semantic retrieval shouldn't pay for its types, the background ingestion service, or the embedding-provider wiring. Stripping the `ToolUp.RAG` reference removes all RAG surface from the app.
2. **The runtime surface is substantial.** In-memory vector store, channel-backed ingestion loop, scope-access enforcement, retrieval prompt builder, RAG-aware compose wrapper — keeping this in core would conflate platform infrastructure with feature code. Companion packages (the same pattern as [`ToolUp.AI`](../ToolUp.AI/) and [`AgGridEnterprise`](../AgGridEnterprise/)) keep the boundary clean.

What stays in core:

- **`VectorKnowledgeTypes`** (`src/ToolUp.Platform.Core/Shared/Types/VectorKnowledgeTypes.fs`) — `VectorScope`, `TextChunk`, `VectorMatch`, `MergeStrategy`, `RetrievalRequest`. Shared by server and Fable-compiled code.
- **`VectorisationHandler`** (`src/ToolUp.Platform.Core/Shared/Types/VectorisationTypes.fs`) — per-module declaration: "this is how to turn my processed data into chunks for indexing." Modules declare one without referencing `ToolUp.RAG`.
- **`IEmbeddingProvider`** (`src/ToolUp.Platform.Server/Server/Rag/IEmbeddingProvider.fs`) — extension-point interface (`GenerateEmbedding`, `Dimensions`, `ProviderId`, `ModelId`). Also defines `EmbeddingVersion` (the `(ProviderId, ModelId, Dimensions)` triple stamped onto chunk metadata at upsert time). Implementations live in `src/EmbeddingProviders/<Name>/`.
- **`IEmbeddingCache`** (`src/ToolUp.Platform.Server/Server/IEmbeddingCache.fs`) — extension-point interface for caching `text → float32 array` lookups. The in-memory LRU implementation is in this companion; a distributed companion (Redis, blob-backed) can replace it without touching call sites.
- **`IVectorStore`** (`src/ToolUp.Platform.Server/Server/Rag/IVectorStore.fs`) — extension-point interface. The in-memory implementation is in this companion; a distributed companion (Qdrant, pgvector, …) can replace it without touching module code. Soft-delete contract: `DeleteChunk` writes `_deletedAt` tombstones; `Vacuum` hard-removes past the retention window; `DeleteByScope` is a config-grade reset that bypasses tombstone semantics.
- **`IRetrievalPipeline`** (`src/ToolUp.Platform.Server/Server/Rag/IRetrievalPipeline.fs`) — high-level facade that `SystemPromptBuilder` implementations and modules call into. The *runtime* (pipeline, vector store, background ingestion, reembedding service) lives in this companion.
- **`IOcrProvider`** (`src/ToolUp.Platform.Server/Server/IOcrProvider.fs`) — optional document-understanding extension point. `IsScanned` decides whether a document needs OCR; `ExtractText` returns per-page text. The default no-op (in this companion) reports `IsScanned = false` and `ExtractText = []` so consumers route to native text extraction; a concrete provider ships as a companion — `ToolUp.OcrProviders.Tesseract` (`src/OcrProviders/Tesseract/`, Phase 500) is the first. There is no `with*` builder: register the provider in DI before the RAG composition runs and `composeWithRAG` picks it up (see `docs/companions/ocr-providers.md`).
- **`ITableExtractor`** (`src/ToolUp.Platform.Server/Server/ITableExtractor.fs`) — optional document-understanding extension point. `ExtractTables` returns `ExtractedTable list` whose shape (`Page`, `Headers`, `Rows`) is deliberately compatible with `Chunking.SheetData` so consumers can pipe through `chunkSpreadsheet` without any type translation. The default no-op returns `[]`.
- **`IImageEmbedder`** (`src/ToolUp.Platform.Server/Server/IImageEmbedder.fs`) — optional multimodal extension point. `EmbedImage` and `EmbedQuery` produce vectors in a shared modality space (CLIP-style). No default no-op is registered: there is no honest no-op for image vectors. Reserved constants `ImageRegionDataTypeId` and `ImageEmbeddingMetadata.{ProviderKey, ModelKey, DimensionsKey}` define the routing surface for future image-aware vector stores.
- **`IRetrievalTracer`** (`src/ToolUp.Platform.Server/Server/IRetrievalTracer.fs`) — observability extension point. `Trace : RetrievalTrace -> AccessContext -> Async<unit>` runs after every `Retrieve` call. The `RetrievalTrace` record carries `QueryHash` (SHA256, never plaintext), `QueryLength`, requested vs permitted scopes, top-K, candidate-pool size, top score, dense/sparse/rerank flags, latency, stage list, and result count. Two implementations ship in this companion: `NoOpRetrievalTracer` (zero-cost opt-out) and `EventStoreRetrievalTracer` (writes `KnowledgeRetrieved` events under `SourceModule = "_platform.retrieval"`). The reserved literals `KnowledgeRetrievedEventType` and `RetrievalTraceSourceModule` are wire-format contracts.

All nine server-side interfaces satisfy the Phase 9c portability rules: identity by value, async at every boundary, no callback/supervision hooks, stateless per invocation, no cross-scope ordering promises.

## What this package ships

### Compiled types — [IngestionTypes.fs](IngestionTypes.fs)

Compiled into `ToolUp.RAG.dll`. Referenced by modules that want to enqueue custom ingestion jobs (rare — most modules rely on the post-save hook).

| Type | Purpose |
|---|---|
| `IngestionJob` | Record handed from upload handlers to the background indexer: document id, chunk, scope |
| `IngestionQueue` | Channel-backed unbounded queue — thread-safe enqueue, single-reader dequeue |

### Server-side runtime — [Server/](Server/)

Injected into the consuming server project via `ToolUp.RAG.Server.props`. Compiles alongside the Platform server files.

| File | Purpose |
|---|---|
| `NoOpDocUnderstanding.fs` | Default `IOcrProvider` and `ITableExtractor` implementations — `IsScanned = false`, `ExtractText = []`, `ExtractTables = []`. Registered automatically when no concrete companion is wired so consumers can resolve the interfaces unconditionally |
| `Chunking.fs` | Token-aware chunking helpers — `ITokenCounter` (heuristic ≈4 chars/token by default, abstracted so a `Microsoft.ML.Tokenizers` companion can drop in as a one-line wrapper), `ChunkingConfig` (`MaxTokens` / `OverlapTokens` / `MinTokens`), `splitBySentence`, `splitByTokens` (greedy sentence packing with overlap; word-boundary fallback for sentences > `MaxTokens`), `SheetData` + `chunkSpreadsheet` (preserves 1-based row indices, repeats column headers per chunk, emits `Sheet "X", rows N–M of T` header line), `formatRow`, `withContextualHeader` (no-op when no `ITextSummariser` registered) |
| `InMemoryEmbeddingCache.fs` | `IEmbeddingCache` implementation — coarse-locked LRU keyed by `(ProviderId, ModelId, Dimensions, SHA256(text))`. Default capacity 10000; tracks hits/misses for `HitRate` |
| `CachingEmbeddingProvider.fs` | Decorator that wraps any `IEmbeddingProvider` with an `IEmbeddingCache`. SHA256-hashes text so raw user content never lands in cache keys. Preserves the wrapped provider's `ProviderId` / `ModelId` / `Dimensions` so version stamping downstream still records the underlying identity |
| `InMemoryVectorStore.fs` | `IVectorStore` implementation — cosine similarity, pre-normalised vectors, debounced `IBlobStorage` persistence, `IDisposable` with final flush on shutdown. Tombstone soft-delete via `_deletedAt`; `Vacuum` hard-removes entries past the retention window; `ListChunks` and `ListScopes` for re-embed scans |
| `RetrievalPipeline.fs` | `IRetrievalPipeline` implementation — scope-access validation, embedding generation, merge strategies. `Index` stamps `EmbeddingVersion` (`_embedProvider` / `_embedModel` / `_embedDim`) onto every chunk so a model swap is detectable post-hoc. After every `Retrieve` call, builds a `RetrievalTrace` with hashed query + stage shape and emits via `IRetrievalTracer.Trace` (failures swallowed — tracing must never fail retrieval) |
| `RetrievalTracers.fs` | `NoOpRetrievalTracer` (zero-cost opt-out) and `EventStoreRetrievalTracer` (default — writes `KnowledgeRetrieved` events into the configured `IEventStore` under `_platform.retrieval`). `hashQuery` SHA256 helper. `composeWithRAG` resolves the registered tracer from DI; if none is present, falls back to `EventStoreRetrievalTracer` so retrieval observability ships on by default |
| `IngestionService.fs` | `IngestionBackgroundService` — dequeues jobs, indexes via pipeline, emits `KnowledgeChunkIndexed` / `KnowledgeChunkFailed` events. Concurrency-capped via `SemaphoreSlim` |
| `ReembeddingService.fs` | `ReembeddingQueue` (unbounded `Channel<VectorScope>`) + `ReembeddingBackgroundService` — drains the queue, scans each scope for chunks whose `EmbeddingVersion` doesn't match the current provider, re-runs `pipeline.Index` to replace the prior vector. Emits `KnowledgeChunkReembedded` / `KnowledgeChunkReembedFailed` events |
| `RAGPromptBuilder.fs` | `withRetrieval` — a `SystemPromptBuilder` that injects retrieved chunks as cited context into the AI system prompt |
| `RAGCompose.fs` | `composeWithRAG` + `RAGServerApp` record — drop-in replacement for `composeWithAI` / `AIServerApp`. `RAGServerApp` wraps an `AIServerApp.AI` and adds an `IEmbeddingProvider`; `RAGServerApp.run` calls `composeWithRAG` on top of the AI wiring. Internally wraps the supplied embedder with `CachingEmbeddingProvider` and registers the reembedding service |

## How to enable RAG in a deployment

### 1. Reference the companion

Server project (`ToolupApp-Server.fsproj`):

```xml
<Import Project="..\ToolUp.Platform\ToolUp.Platform.Server.props" />
<Import Project="..\ToolUp.AI\ToolUp.AI.Server.props" />
<Import Project="..\ToolUp.RAG\ToolUp.RAG.Server.props" />
<Import Project="..\EmbeddingProviders\Local\LocalEmbeddingProvider.Server.props" />

<ItemGroup>
  <ProjectReference Include="..\ToolUp.Platform\ToolUp.Platform.fsproj" />
  <ProjectReference Include="..\ToolUp.AI\ToolUp.AI.fsproj" />
  <ProjectReference Include="..\ToolUp.RAG\ToolUp.RAG.fsproj" />
  <ProjectReference Include="..\EmbeddingProviders\Local\LocalEmbeddingProvider.fsproj" />
</ItemGroup>
```

Deployments that use OpenAI embeddings swap `LocalEmbeddingProvider` for `OpenAIEmbeddingProvider`.

### 2. Wire an embedding provider and run via `RAGServerApp`

In the server entry point:

```fsharp
open ToolUp.Platform
open ToolUp.Platform.Server
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.RAG.RAGCompose

// Dev / CI — no API key, offline-capable
let embeddingProvider = LocalEmbeddingProvider.create ()

// Production — neural embeddings, key from ISecretStore
// let embeddingProvider = OpenAIEmbeddingProvider.create secretStore

// Modules that index their processed data register a VectorisationHandler
// via ServerModule.withVectorisation — the handlers flow through to
// ServerApp.addModules. There is no separate `vectorisationHandlers` arg.

RAGServerApp.empty
|> RAGServerApp.withAI (
    AIServerApp.empty
    |> AIServerApp.withBase (
        ServerApp.empty
        |> ServerApp.withConfig config
        |> ServerApp.withAuth authProvider
        |> ServerApp.withLogger (Some logger)
        |> ServerApp.withStorage (Some blobStorage)
        |> ServerApp.addModules modules)
    |> AIServerApp.withAIFactory aiProviderFactory
    |> AIServerApp.withAIConfigStore aiConfigStore
    |> AIServerApp.withAITools AITools.allTools
    |> AIServerApp.withModuleAIContexts moduleAIContexts)
|> RAGServerApp.withEmbeddingProvider embeddingProvider
|> RAGServerApp.run
```

Deployments that don't want RAG use `AIServerApp.run` directly (no `RAGServerApp` wrapper). The AI wiring is identical — `RAGServerApp` only adds the embedding provider, vector-store, pipeline, ingestion queue, background service, post-save hook, and retrieval `SystemPromptBuilder` on top.

### Tuning the retrieval surface (Phase 14m–14p)

`RAGServerApp` exposes per-deployment knobs that tune retrieval behaviour without touching code. Compose them onto the pipeline before `run`:

```fsharp
RAGServerApp.empty
|> RAGServerApp.withAI aiServerApp
|> RAGServerApp.withEmbeddingProvider embedder
// — Throughput (Phase 14n) —
|> RAGServerApp.withIngestionConcurrency 16          // documents in flight per slot
|> RAGServerApp.withIngestionQueueCapacity 10000     // bounded queue (`429 + Retry-After` on overflow)
// — Retrieval shape (Phase 14o) —
|> RAGServerApp.withTopK 8                           // matches per turn (default 5)
|> RAGServerApp.withMinScore (Some 0.4)              // drop weak matches (default None)
|> RAGServerApp.withSnippetCharLimit 320             // Sources panel preview (default 240)
|> RAGServerApp.withOriginFilter (Some (Set.ofList [ Document; Note ]))
                                                     // restrict to specific origins; default excludes AIContext
|> RAGServerApp.withMergeStrategy Interleaved        // or Separate
// — Grounding stance (Phase 14p) —
|> RAGServerApp.withGroundingMode Preferred          // or Permissive | StrictlyGrounded
// — Telemetry (Phase 14n) —
|> RAGServerApp.withTelemetry customTelemetry        // optional Prometheus / OTel sink
|> RAGServerApp.run
```

`/health/rag` returns a 60-second rolling-window snapshot from `IRagTelemetry` — embedding latency / batch size, queue depth + rejections, flush latency + dirty chunks, retrieval hit / low-score-miss / empty counts. Wire it as a dashboard scrape target. `IRagTelemetry` is companion-extensible: register a Prometheus / OTel exporter via `withTelemetry` and `/health/rag` plus your downstream sink see the same data.

When fewer than two matches survive the `MinScore` gate, `RAGPromptBuilder.withRetrieval` emits a `KnowledgeRetrievalMiss` event via `IRetrievalTracer.Miss` — admin UIs scan these to spot teams whose KB is too thin / off-topic for their queries.

#### Configuring RAG — the supported bounds (Phase 9m.B)

Each knob has a supported range, checked at startup by the `rag-config-bounds`
preflight validator. **An out-of-range value refuses boot** (`Error` ⇒
`ConfigPreflightFailedException`) rather than being silently accepted: these are
settings whose damage shows up later and somewhere else, so failing at the line that
caused it is the cheaper outcome.

| Knob | Setter | Range | Default | Notes |
|---|---|---|---|---|
| `TopK` | `withTopK` | `[1, 100]` | `5` | `> 50` warns rather than refuses — legal, but the retrieval block starts dominating the prompt budget. |
| `MinScore` | `withMinScore` | `[0.0, 1.0]` or `None` | `None` | Cosine-similarity gate. `None` disables it. |
| `MmrLambda` | `withMmrLambda` | `[0.0, 1.0]` | `0.5` | Only checked when MMR is enabled — an inert λ is not a misconfiguration. |
| `SnippetCharLimit` | `withSnippetCharLimit` | `[32, 8192]` | `240` | Below 32 a Sources-panel preview is unidentifiable; above 8192 it ships whole documents to every client. |
| `IngestionConcurrency` | `withIngestionConcurrency` | `[1, 64]` | `8` | Effective upstream embedding-call concurrency; above 64 hosted providers rate-limit you into the retry path. |
| `IngestionQueueCapacity` | `withIngestionQueueCapacity` | `[100, 1000000]` | `5000` | Below 100 a single bulk upload saturates the queue and documents are dropped — saved, but permanently unsearchable. |

The targeted setters also **clamp** lower bounds on the way in (`withTopK 0` → `1`),
and the separate `rag-retrieval-defaults-clamp` validator reports every clamp that
fired — so a typo is visible as *both* a clamp report and, where it lands outside the
table above, a bounds refusal.

Two `Accept*` escape hatches relax RAG preflight; both are documented in
[`docs/operations/env-vars.md`](../../docs/operations/env-vars.md):

- `ServerConfig.AcceptEphemeralRagIndex` (`TOOLUP_ACCEPT_EPHEMERAL_RAG_INDEX=1`) —
  degrades the no-durable-backing refusal to a `Warning` for a deployment that
  deliberately re-ingests its corpus on every boot.
- `ServerConfig.AcceptLocalEmbedderAtScale` (`TOOLUP_ACCEPT_LOCAL_EMBEDDER_AT_SCALE=1`)
  — accepts the dev-only, process-stateful `LocalEmbeddingProvider` in a
  production-shaped deployment, silencing both local-embedder validators and the
  `embedding_provider:local` probe's `Degraded` verdict together.

`/dev/inspect` carries two matching panels: **RAG durability** (does this deployment's
index survive a restart, and was that chosen?) and **Vectorisation handlers** (which
registered data types are actually indexed — including the partial-coverage case no
validator warns about).

### Benchmarks (Phase 14q)

`src/ToolUp.RAG.Benchmarks/` is a sibling executable that scores the production retrieval pipeline against [BEIR](https://github.com/beir-cellar/beir) datasets — the standard IR benchmark suite. It composes the same `RetrievalPipeline`, `InMemoryVectorStore` / `HnswVectorStore`, `CachingEmbeddingProvider`, and `LocalEmbeddingProvider` / `OpenAIEmbeddingProvider` that ship in production; what it adds is a BEIR loader, a percentile-aware latency aggregator, and a CSV writer.

Run it:

```bash
# Local TF-IDF embedder + flat-scan vector store, default smoke dataset:
dotnet run --project src/ToolUp.RAG.Benchmarks -- --dataset scifact

# OpenAI embedder + HNSW (requires OPENAI_API_KEY env var):
dotnet run --project src/ToolUp.RAG.Benchmarks -- \
  --dataset fiqa --embedder openai --vector-store hnsw

# Stress test HNSW at 10× corpus replication (quality metrics suppressed):
dotnet run --project src/ToolUp.RAG.Benchmarks -- \
  --dataset fiqa --vector-store hnsw --replicate 10
```

Each run appends one row to `bench-results.csv` (configurable via `--out`). Columns: dataset, embedder, vector store, reranker, MMR, topK, replicate, queries-evaluated, nDCG@10, Recall@100, MRR@10, ingest seconds, query p50 / p95 / p99 ms, wall-clock seconds. Use the matrix to pick `RetrievalDefaults` values, validate vector-store substitutions, and regression-test future retrieval changes.

BEIR datasets are downloaded on first use to `data/beir/` (override via `TOOLUP_BEIR_CACHE`). SciFact is ~10 MB; FiQA is ~80 MB. MS MARCO is supported by name (`--dataset msmarco`) but stays out of the in-tree validation set due to OpenAI cost — operator-driven only.

### 3. Ingest content

There are two paths by which chunks enter the index:

**Path A — post-save hook (automatic).** When a module registers a `VectorisationHandler` for its `DataTypeId`, every successful `SessionFileStore.AddFile` fires the hook. The hook calls `handler.Vectorise processedData`, wraps each returned `TextChunk` in an `IngestionJob`, and enqueues it. Modules need no extra wiring — uploading a file through the standard Data Manager is enough.

**Path B — direct enqueue (module-driven).** A module that produces chunks outside the file-upload flow (e.g. a web-scraper module, a Slack-ingestion module) depends on `ToolUp.RAG.IngestionTypes` for `IngestionJob` / `IngestionQueue`, resolves the queue from DI, and calls `queue.Enqueue`.

Both paths converge on `IngestionBackgroundService`, which dequeues jobs, calls `IRetrievalPipeline.Index`, and emits audit events.

### 4. Consume retrieved context

With `AIConfig = None` (the default on `AIServerApp.empty`), `RAGServerApp.run` installs the `withRetrieval` builder automatically — every AI message goes through retrieval and relevant chunks are prepended to the system prompt as cited context.

Deployments that want custom prompt composition supply their own `aiConfig` and compose `withRetrieval` explicitly:

```fsharp
open ToolUp.AI.SystemPromptBuilder
open ToolUp.RAG.RAGPromptBuilder

let ragAwarePrompt pipeline =
    SystemPromptBuilder.compose [
        SystemPromptBuilder.fromStatic "You are ToolUp, an analytics assistant..."
        SystemPromptBuilder.activeModuleContext
        withRetrieval pipeline    // from ToolUp.RAG
    ]
```

Retrieval runs per request and reads `PromptContext.CurrentMessage` as the query. Access is scope-validated: a `Team teamId` request from a user whose `AccessContext.TeamId ≠ Some teamId` returns an empty result, not an error.

## Chunker contract — when and how to use `Chunking`

The `ToolUp.RAG.Chunking` module is the canonical way to slice content before handing it to a `VectorisationHandler` or directly to `IngestionQueue`. Use it whenever you produce text that may exceed an embedding model's input budget — page contents, narrative bodies, sheet rows, parsed sections of structured documents.

**Two entry points:**

```fsharp
open ToolUp.RAG.Chunking

// Prose: token-budgeted, sentence-aware, overlap-preserving.
let chunks : string list = splitByTokens ChunkingConfig.defaults longText
// ChunkingConfig.defaults: MaxTokens = 512, OverlapTokens = 64, MinTokens = 16

// Tabular: row-preserving, header-repeating, token-budgeted.
let sheetData : SheetData = {
    SheetName = "Sales"
    Headers = [| "SKU"; "Region"; "Revenue" |]
    Rows = [ (2, [| "A1"; "EMEA"; "1234" |]); (3, [| "A2"; "EMEA"; "5678" |]) ]
}
let rowChunks : string list = chunkSpreadsheet ChunkingConfig.tabular sheetData
// ChunkingConfig.tabular: MaxTokens = 512, OverlapTokens = 0, MinTokens = 1
// Each chunk starts with `Sheet "Sales", rows N–M of T` and repeats column headers.
```

**Contracts the chunker honours:**

- A row that overflows the per-chunk token budget on its own is still emitted as a single chunk. Citable identity beats truncation guarantees — the embedder will truncate at its own max input, but the row stays a citable unit.
- The header line `Sheet "X", rows N–M of T` is regex-parseable: callers (including `ToolUp.KnowledgeBase.Server.parseRowRange`) round-trip the row range back into their own `SourceReference.Location` / equivalent for click-through citations.
- `splitByTokens` preserves sentence boundaries when it can; sentences longer than `MaxTokens` fall back to word-boundary splitting (no mid-word breaks).
- Overlap is applied at the sentence level, not the token level — successive chunks share the last few sentences of the previous chunk, not arbitrary mid-sentence text.
- The default `HeuristicTokenCounter` approximates ≈4 chars/token. It's intentionally conservative (over-counts slightly) to stay within real-tokeniser budgets without depending on `Microsoft.ML.Tokenizers`. A future companion can plug in a real BPE counter via `ITokenCounter` as a one-line wrapper.

**Optional contextual-retrieval preamble (`ITextSummariser`):**

`withContextualHeader : ITextSummariser option -> string -> string -> Async<string>` prepends an Anthropic-style "this chunk is from document X about Y" summary before the chunk text, when a summariser companion is registered. With `None` (the default), it's a no-op and ingestion behaviour is unchanged. Concrete `IAIProvider`-backed summariser companions are deferred — cost-conscious by design (one `IAIProvider` call per chunk).

## Document-understanding interfaces (OCR, table extraction, image embedding)

Three optional extension points let document-aware companions enrich extraction without leaking heavyweight dependencies (`Tesseract`, `Camelot`, `CLIP`) into core or this RAG companion. All three are no-op by default — with nothing wired, behaviour is byte-equivalent to a deployment that doesn't know they exist. One concrete companion ships today: `ToolUp.OcrProviders.Tesseract` (Phase 500); the other two seams still have no in-repo implementation beyond their no-ops.

**`IOcrProvider`.** Two methods: `IsScanned : byte[] -> mimeType -> Async<bool>` and `ExtractText : byte[] -> mimeType -> Async<OcrPage list>`. Used by `ToolUp.KnowledgeBase`'s PDF extractor: if `IsScanned` returns `true`, the path skips `PdfPig` text extraction and uses `ExtractText` instead. The default `NoOpOcrProvider` reports `false` for everything, so the existing native-text path runs unchanged.

**`ITableExtractor`.** One method: `ExtractTables : byte[] -> mimeType -> Async<ExtractedTable list>`. The returned `ExtractedTable` record carries `Page : int option`, `Headers : string array`, `Rows : (int * string array) list` — deliberately shape-compatible with `Chunking.SheetData` so the KB extractor adapts the record without leaking the chunker type into core. KB pipes each extracted table through `chunkSpreadsheet`, so a future `Camelot` companion produces the same row-preserving, header-repeating chunks that XLSX/CSV ingestion already produces. The default `NoOpTableExtractor` returns `[]`.

**`IImageEmbedder`.** Three methods: `EmbedImage : byte[] -> mimeType -> Async<float[]>`, `EmbedQuery : string -> Async<float[]>`, plus the standard `ProviderId` / `ModelId` / `Dimensions` triple. Image embeddings live in a different vector space than text embeddings; the reserved constants in `ImageEmbeddingMetadata` (`_imageProvider`, `_imageModel`, `_imageDim`) document that future image-aware ingestion paths must stamp these *separately* from `EmbeddingVersion` and route image chunks (`dataTypeId = ImageRegionDataTypeId = "ImageRegion"`) through a vector store that isolates dimensions per modality. No default is registered — consumers null-check the DI lookup.

**Companion pattern (deferred).** Concrete implementations land at `src/Ocr/<Name>/`, `src/TableExtractors/<Name>/`, `src/ImageEmbedders/<Name>/` — same `.fsproj` + `.Server.props` + `create` factory shape as `src/EmbeddingProviders/Local/`. A deployment wires a companion by importing the props and substituting the DI registration in its server entry point; nothing else changes. The interfaces ship now; the concrete companions (`Tesseract`, `Camelot`, `CLIP`) are deferred follow-ups so deployments that don't need them carry no extra weight.

## Embedding cache, version stamping, soft delete

Three operational features ride together because they share one substrate (chunk metadata) and one lifecycle (the retrieval / ingestion runtime).

**Embedding cache (`IEmbeddingCache`).** The supplied `IEmbeddingProvider` is wrapped transparently inside `composeWithRAG` with `CachingEmbeddingProvider`. Repeated query strings (and re-uploaded chunks with identical text) hit an in-memory LRU keyed by `(ProviderId, ModelId, Dimensions, SHA256(text))` and skip the underlying provider — no API call for OpenAI, no TF-IDF recompute for `LocalEmbeddingProvider`. SHA256 is used so raw user text never sits in cache keys. The cache is registered as `IEmbeddingCache` in DI; admin endpoints can read `cache.HitRate()` to monitor steady-state behaviour. Default capacity 10000; eviction is strict-LRU.

A model swap automatically invalidates the cache without any explicit flush — the cache key includes the provider identity, so old keys are simply unreachable under the new identity and age out via LRU.

**Version stamping (`EmbeddingVersion`).** `RetrievalPipeline.Index` stamps `_embedProvider` / `_embedModel` / `_embedDim` onto every chunk's metadata as it indexes. The stamp travels with the chunk through `IBlobStorage` persistence and back through `ListChunks`. When a deployment swaps the embedding provider (e.g. `text-embedding-3-small` → `text-embedding-3-large`), the `ReembeddingBackgroundService` detects the per-chunk mismatch on its next scope scan and re-runs `pipeline.Index` to replace the prior vector. The new index entry carries the new stamp; subsequent scans find no work.

The reembedding service drains a `ReembeddingQueue` (unbounded `Channel<VectorScope>`). On startup it pushes whatever scopes the store currently knows about; thereafter it loops on the queue. Composing apps push specific scopes onto the queue from admin endpoints — typically after a deployment-level model swap — to force a rescan. `IngestionBackgroundService` and `ReembeddingBackgroundService` both register as `IHostedService` and run in parallel.

**Soft delete (`_deletedAt` tombstones).** `IVectorStore.DeleteChunk` no longer hard-deletes — it stamps `_deletedAt = <ISO 8601 UTC>` on the chunk's metadata and leaves the entry in place. `Search` filters tombstoned chunks out (so deleted content is invisible to AI prompts and module retrieval); `ListChunks scope false` agrees. `RestoreChunk` removes the tombstone within the retention window, making the chunk visible again; past retention, `Vacuum scope (UtcNow - retention)` hard-removes the entry and `RestoreChunk` becomes a no-op. `DeleteByScope` continues to hard-delete unconditionally — it's a config-grade reset, not a recoverable action.

**Steady-state memory — the auto-vacuum contract (Phase 14w).** Soft-delete alone does not bound memory: without a vacuum, `_deletedAt` tombstones accumulate for the process lifetime and a long-running replica grows toward OOM. `RAGServerApp.withVacuumSchedule` closes that gap — it registers a `RAGVacuumJobHandler` on the `IJobScheduler` that, on a cron (default **daily 03:00 UTC**), enumerates every scope via `IVectorStore.ListScopes()` and calls `Vacuum(scope, now - retention)` per scope. The retention window defaults to **7 days** (tune with `withTombstoneRetention`; floored at one minute). Each scope that purges anything emits a `KnowledgeVacuumCompleted` audit event carrying `(ScopeKey, ChunksRemoved, BytesReclaimed, DurationMs)`.

> **Production deployments must enable `JobScheduler = InProcessJobScheduler` (or a distributed scheduler companion) *and* `RAGServerApp.withVacuumSchedule` for memory to stabilise.** With the schedule set but no scheduler, the sweep can never fire; with neither, tombstones are reclaimed only by a manual `IVectorStore.Vacuum` call. The `VacuumScheduleValidator` warns at startup in both cases (visible in the HealthMonitorUI admin tab / `/dev/inspect` Validators panel).

```fsharp
RAGServerApp.create factory providerProfile embedder
|> RAGServerApp.withConfig { config with JobScheduler = InProcessJobScheduler }
|> RAGServerApp.withTombstoneRetention (TimeSpan.FromDays 14.0)   // optional — default 7 days
|> RAGServerApp.withVacuumSchedule                                // daily 03:00 UTC; or withVacuumScheduleCron "0 */6 * * *"
|> RAGServerApp.run
```

Deployments that want a bespoke cadence use `withVacuumScheduleCron "<5-field cron>"`; those that prefer to drive `Vacuum` from their own admin path can omit the schedule entirely (accepting the manual-reclaim contract above).

## Writing a `VectorisationHandler` (for module authors)

Modules declare handlers — nothing in `ToolUp.RAG` names a module.

```fsharp
// In SkuAnalysis/Server.fs
open ToolUp.Platform.VectorisationTypes
open ToolUp.Platform.VectorKnowledgeTypes

let salesVectorisation: VectorisationHandler = {
    DataTypeId = SkuAnalysis.SharedTypes.DataTypeConstants.SalesData
    Vectorise = fun processed ->
        match processed with
        | :? SalesSummary as s ->
            s.RowGroups
            |> List.map (fun group -> {
                Content = $"{group.Brand} {group.Category}: {group.Insights}"
                Metadata =
                    Map.ofList [
                        "dataTypeId", "SalesData"
                        "brand", group.Brand
                    ]
            })
        | _ -> []
}
```

The app attaches each module's vectorisation handler(s) via `ServerModule.withVectorisation` when assembling the module list. Modules that don't vectorise simply don't call the helper:

```fsharp
let skuAnalysisModule =
    ServerModule.create "SkuAnalysis"
    |> ServerModule.withGuardedApi skuAnalysisApi
    |> ServerModule.withDataTypes [ salesDataType ]
    |> ServerModule.withVectorisation [ SkuAnalysis.Server.salesVectorisation ]

// ... addModules flows handlers into the RAGServerApp pipeline above.
```

Principles:

- The module owns the extraction — only the author knows which fields are semantically meaningful.
- The SDK owns embedding, storage, scoping, access enforcement, and audit events.
- Modules never reference `ToolUp.RAG` for the handler — the handler type lives in `ToolUp.Platform.Shared`.
- Return `[]` from `Vectorise` to opt out on a per-record basis (e.g. records with no meaningful text content).

## Writing a new embedding provider

Follow the pattern in [`src/EmbeddingProviders/Local/`](../EmbeddingProviders/Local/) and [`src/EmbeddingProviders/OpenAI/`](../EmbeddingProviders/OpenAI/). Minimum:

1. Implement `IEmbeddingProvider` (in `src/ToolUp.Platform.Server/Server/Rag/IEmbeddingProvider.fs`):
   - `GenerateEmbedding: string -> Async<float32 array>` — pure-per-call for distributed implementations; the `LocalEmbeddingProvider` is the documented exception (see its file-level comment about Rule 4).
   - `Dimensions: int` — constant for the provider's lifetime.
2. Expose a factory function, typically `create (secretStore: ISecretStore) : IEmbeddingProvider` for API-backed providers or `create () : IEmbeddingProvider` for local / offline ones. API-backed providers read the key from the injected `ISecretStore` on each call, never hardcoded.
3. Create a `.fsproj` and `.Server.props` in `src/EmbeddingProviders/<Name>/`. Deployments swap providers by changing the props import and the `embeddingProvider` binding — no other wiring changes.

The retrieval pipeline, ingestion service, and vector store are provider-agnostic. A new provider only needs to translate text → `float32 array`.

## Writing a new vector store

The SDK's documented scale story is **in-memory < 50k → HNSW < 1M → external (Pgvector / Qdrant)**.

- **`InMemoryVectorStore`** (this companion) — flat-scan cosine over a `ConcurrentDictionary`. Suitable for deployments up to ~50,000 chunks per scope; per-query CPU dominates above that.
- **`HnswVectorStore`** ([`src/VectorStores/Hnsw/`](../VectorStores/Hnsw/)) — `HNSW.Net`-backed `SmallWorld<float[], float>` graph per scope. Scope isolation is structural (each scope owns an independent graph; cross-scope queries are impossible by construction). Wire it in via `RAGServerApp.withVectorStore (HnswVectorStore.create blobStorage logger)` before `RAGServerApp.run` — the in-memory default is silently skipped.
- **External (Pgvector, Qdrant, …)** — the `IVectorStore` contract supports them; concrete companions ship as separate `src/VectorStores/<Name>/` packages and substitute the same way.

To add a new vector store:

1. Place it at `src/VectorStores/<Name>/` (parallel to `src/Storage/` and `src/EmbeddingProviders/`).
2. Implement all `IVectorStore` methods — `Upsert`, `Search`, `ListChunks` (honouring `includeDeleted`), `DeleteChunk` (tombstone-write, not hard-delete), `RestoreChunk`, `Vacuum`, `DeleteByScope`, `ListScopes`. Honour scope isolation (never return cross-scope results), keep the async contract, write `_deletedAt` tombstones rather than hard-removing on `DeleteChunk`, and make the type `IDisposable` if it owns background resources.
3. Substitute it via `RAGServerApp.withVectorStore yourStore` before `RAGServerApp.run`. The existing `RetrievalPipeline`, `IngestionBackgroundService`, `ReembeddingBackgroundService`, and `RAGPromptBuilder` pick up the replacement via DI.

The `InMemoryVectorStore` source is the reference implementation for the contract — especially its scope-key encoding (`"platform"`, `"deployment"`, `"team:{id}"`), its pre-normalisation approach to cosine similarity, and its tombstone semantics (strip `_deletedAt` on `Upsert`, filter on `Search`, parse on `Vacuum`). `HnswVectorStore` reuses the same scope-key encoding and tombstone semantics, differing only in the search algorithm and graph caching.

## Retrieval observability + evaluation harness (Phase 14j)

`IRetrievalTracer` ships with two implementations: `NoOpRetrievalTracer` (zero-cost opt-out) and `EventStoreRetrievalTracer` (default). The default writes a `KnowledgeRetrieved` event under `SourceModule = "_platform.retrieval"` after every retrieval call. The payload schema is fixed and wire-format-stable:

| Field | Type | Notes |
|---|---|---|
| `QueryHash` | `string` | SHA256 hex digest. Plaintext query never persisted |
| `QueryLength` | `int` | Character count (degenerate-input visibility) |
| `RequestedScopes` / `PermittedScopes` | `string list` | Flattened to `"platform"` / `"deployment"` / `"team:<id>"` |
| `TopK`, `AdaptiveK`, `CandidatePoolSize`, `TopScore` | retrieval shape | |
| `DenseUsed`, `SparseUsed`, `RerankerName` | stage telemetry | |
| `LatencyMs`, `Stages`, `ResultCount` | outcome | `Stages` is an ordered list (`["AuthoriseScopes"; "Dense"; "TopK"]` for the cosine-only path) |

The plaintext query never leaves request memory. Admin UIs and replay tooling read traces by `QueryHash`. To opt out, register a `NoOpRetrievalTracer` ahead of `RAGServerApp.run`:

```fsharp
services.AddSingleton<IRetrievalTracer>(ToolUp.RAG.RetrievalTracers.createNoOp ())
```

`composeWithRAG` resolves `IRetrievalTracer` from DI; only when nothing is registered does it fall back to `EventStoreRetrievalTracer`. A custom tracer (e.g., one that mirrors traces to Splunk via `IAuditSink` once that ships) just needs to implement `IRetrievalTracer.Trace` and register ahead of `composeWithRAG`.

### Eval harness — `src/ToolUp.RAG.Evaluation/`

A standalone runnable project that loads a labelled fixture, builds an in-memory pipeline, runs every query, and reports retrieval metrics:

```bash
# Run smoke fixture (defaults to fixtures/platform-readme.json)
dotnet run --project src/ToolUp.RAG.Evaluation

# Multiple fixtures
dotnet run --project src/ToolUp.RAG.Evaluation -- path/to/a.json path/to/b.json

# With baseline regression check (5% tolerance on Recall@5 / nDCG@10)
dotnet run --project src/ToolUp.RAG.Evaluation -- --baseline baseline.json --out latest.json
```

Public surface (`ToolUp.RAG.Evaluation`):

| Module | API |
|---|---|
| `EvalTypes` | `Fixture`, `LabelledQuery`, `CorpusEntry`, `QueryResult`, `EvalReport` |
| `FixtureLoader` | `load : string -> Fixture` (System.Text.Json via the `FableConverters` options; accepts the JSON shape under `fixtures/`) |
| `Metrics` | `recallAt`, `ndcgAt`, `mrr`, `buildReport` (binary-relevance nDCG with ideal-ordering normalisation) |
| `RetrievalEval` | `evaluate : IRetrievalPipeline -> Fixture -> Async<EvalReport>`; `seedCorpus`; `detectRegression : tolerance -> baseline -> candidate -> Result<unit, string>` |

Fixture format — `fixtures/platform-readme.json` is the reference. Each entry carries a `chunkId`, `content`, `scope` (string-flat: `"platform"` / `"deployment"` / `"team:<id>"`), and metadata; each query carries an `id`, `query`, target `scopes`, and a list of `relevantChunkIds`. The `LocalEmbeddingProvider` (TF-IDF) is the default for CI smoke runs — no API key required, deterministic across machines. Production deployments swap in OpenAI embeddings for the offline eval batch; the harness contract is provider-agnostic.

The smoke fixture's baseline (Recall@5 ≈ 0.9, MRR ≈ 0.67 with TF-IDF) is bounded by the embedding provider, not by the pipeline. CI integration (gate PRs on >5% recall regression) is a deferred follow-up — the regression-check API is ready; only the workflow file is missing.

## Observability and metrics (Phase 9 / 9e delegations)

`RAGServerApp` mirrors the `AIServerApp` (and through it, `ServerApp`) observability surface:

- `RAGServerApp.withHealthCheck` (Phase 9k) — companion `IHealthCheck` (e.g. `HnswVectorStoreHealth`).
- `RAGServerApp.withConfigValidator` (Phase 9m) — companion `IConfigValidator` for startup preflight.
- `RAGServerApp.withMetricsSink` (Phase 9e) — companion `IMetricsSink` (e.g. `OtelMetricsSink.create regs logger`) alongside the in-process Prometheus default. Wire `MetricsEndpoint = EnabledMetricsEndpoint` on `ServerConfig` to mount `/metrics`. RAG-specific metrics (`toolup.rag.embedding_latency_ms`, `toolup.rag.retrieval.empty_count`, etc.) are not yet emitted at SDK level — the `IRagTelemetry` interface (Phase 14j) carries them today; promoting RAG telemetry into the `IMetricsSink` substrate is a follow-up.

Each helper delegates through `AIServerApp` to `ServerApp` — see [`src/ToolUp.Platform/README.md`](../ToolUp.Platform/README.md) and `TECHNICAL_GUIDE.md` for the full contract.

## Deferred follow-ups

- **Configurable `withRetrieval` parameters.** `TopK = 5` and `Merge = Interleaved` are hard-coded. A builder-factory variant `withRetrievalOptions { TopK; Merge; MinScore }` would let deployments tune this without rewriting the builder.
- **Distributed vector store companion.** The HNSW companion ([`src/VectorStores/Hnsw/`](../VectorStores/Hnsw/)) is the first concrete in-process alternative to `InMemoryVectorStore`. The next rung is an external implementation (Pgvector / Qdrant) — the `IVectorStore` contract supports it; deferred follow-ups beyond Phase 14k.
- **Auto-vacuum scheduler.** ✅ Shipped (Phase 14w) — `RAGServerApp.withVacuumSchedule` + `withTombstoneRetention` register a `RAGVacuumJobHandler` on the `IJobScheduler` that sweeps every scope on a cron (default daily 03:00 UTC). See the "Steady-state memory" contract above.
- **CI integration for the eval harness.** The repo has no CI pipeline yet; once one exists, a step running `dotnet run --project src/ToolUp.RAG.Evaluation -- --baseline baseline.json --out latest.json` against a tracked baseline would fail PRs that regress recall by more than 5%.
- **Trace export sinks.** `IRetrievalTracer` only emits to `IEventStore` today. A Splunk / Datadog companion mirroring `KnowledgeRetrieved` payloads is a natural follow-up alongside `IAuditSink`.
