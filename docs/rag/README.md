# ToolUp.RAG

Retrieval-augmented generation runtime for apps built on ToolUp Platform. Vector store, retrieval pipeline, background ingestion + reembedding services, prompt builder for injecting retrieved chunks into AI system prompts.

Wraps `ToolUp.AI` — `RAGServerApp` is a flat superset of `AIServerApp`, which is itself a flat superset of `ServerApp`. Pick the tier that matches your needs.

## When to use this companion

- AI assistant should ground its answers in user-uploaded documents, notes, narrative-committed content, or other text data.
- "What does our team's data say about X?" — answering questions with citations from the team's corpus.
- Semantic search over user-tenant data without operator access to the content.
- Continuous re-indexing as new documents arrive.

## When NOT to use this companion

- **Exact-match keyword search** — use SQL `LIKE`, full-text search (Postgres tsvector, etc.), or a dedicated search engine. RAG is for semantic relevance, not exact match.
- **Cross-tenant search** — the SDK enforces per-scope isolation. Building a global search across all tenants requires an `IRetrievalPipeline` impl that intentionally widens scope access, defeating the security property.
- **High-throughput log analytics** — vector stores aren't optimised for billion-row time-series. Use a proper log store (Loki, ClickHouse, Splunk).
- **Single-shot embedding without retrieval** — depend on `IEmbeddingProvider` from `ToolUp.Platform.Server` directly.

## What's in the box

Two packages:

| Package | What it is |
|---|---|
| `ToolUp.RAG.Core` | Shared types: `IngestionTypes` (`IngestionQueue`, `IIngestionStatusObserver`). The minimum surface for a downstream companion (`ToolUp.KnowledgeBase`) to plug into the pipeline. |
| `ToolUp.RAG.Server` | Chunking, default in-memory vector store + BM25 index, retrieval pipeline, ingestion + reembedding services, RAG prompt builder, `RAGCompose`. |

Plus embedding-provider sub-companions:
- `ToolUp.EmbeddingProviders.Local` — in-process TF-IDF; dev / CI / offline.
- `ToolUp.EmbeddingProviders.OpenAI` — `text-embedding-3-small` against OpenAI API.

And vector-store sub-companions:
- `ToolUp.VectorStores.Hnsw` — HNSW approximate-nearest-neighbour; lifts the ~50K-chunk ceiling of `InMemoryVectorStore`.

## Quick start

Add the packages:

```xml
<PackageReference Include="ToolUp.RAG.Server" />
<PackageReference Include="ToolUp.EmbeddingProviders.OpenAI" />
<PackageReference Include="ToolUp.AIProviders.Claude" />
```

Wire the server composition root:

```fsharp skip=fragment
open ToolUp.RAG

// Each embedding-provider package exposes a top-level module; there is no
// ToolUp.EmbeddingProviders.* namespace to open.
//
// `EmbeddingProviderEnv.fromEnv` picks between the companions this
// deployment ships, keyed on TOOLUP_EMBEDDING_PROVIDER. The last
// argument is what it builds when the variable is unset — so this reads
// exactly as `OpenAIEmbeddingProvider.create secretStore` did until an
// operator sets the variable.
let embedder =
    EmbeddingProviderEnv.fromEnv logger [
        { Name = "local"; Resolve = fun () -> LocalEmbeddingProvider.fromEnv (Some blobStorage) }
        { Name = "openai"; Resolve = fun () -> OpenAIEmbeddingProvider.fromEnv secretStore }
    ] (fun () -> OpenAIEmbeddingProvider.create secretStore)

RAGServerApp.create aiProviderFactory providerProfile embedder
|> RAGServerApp.withConfig serverConfig
|> RAGServerApp.withAuth authProvider
|> RAGServerApp.addModules modules
|> RAGServerApp.run
```

The agent loop now augments its system prompt with retrieved chunks from any registered scope. Documents uploaded via `KnowledgeBase` (or any `VectorisationHandler`-registered DataType) get auto-indexed on the post-save hook.

See [getting-started.md](getting-started.md) for the full walkthrough.

## Concepts

See [concepts.md](concepts.md) for the vector store, retrieval pipeline, chunking, ingestion + reembedding services, scope isolation, prompt-builder composition.

## API reference

See [api-reference.md](api-reference.md) for `RAGServerApp`, `IRetrievalPipeline`, `IVectorStore`, `IEmbeddingProvider`, `VectorisationHandler`, `RAGPromptBuilder`, and the per-deployment tuning knobs (`withTopK`, `withMinScore`, `withMergeStrategy`, etc.).

## Extending

See [extending.md](extending.md) for writing a new embedding provider, vector store, or retrieval tracer.

## Scope isolation

The retrieval pipeline filters requested scopes against the caller's `AccessContext.TeamId` before any call into `IVectorStore`. A mismatched `Team teamId` returns an empty result rather than an error — prompt builders compose without fault handling. `Platform` and `Deployment` scopes are universally readable for authenticated callers (when `PlatformKnowledgeBase` is enabled).

There's no API path to retrieve across team boundaries. Custom `IRetrievalPipeline` impls that bypass scope filtering should not exist — the safe default is structural.
