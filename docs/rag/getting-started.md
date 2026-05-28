# Getting started with ToolUp.RAG

End-to-end walkthrough: enable RAG in your app, ingest some documents, watch retrieval ground the assistant's answers.

## Prerequisites

- A working ToolUp Platform app with `ToolUp.AI` enabled — see the [AI getting-started](../ai/getting-started.md).
- An embedding provider — for this walkthrough, use `ToolUp.EmbeddingProviders.OpenAI` (sub-cent per call, fast).

For fully-offline / no-key dev, swap `ToolUp.EmbeddingProviders.OpenAI` for `ToolUp.EmbeddingProviders.Local` (TF-IDF in-process; no API key needed, but retrieval quality is meaningfully lower than real embeddings).

## 1. Add the packages

In your server project's `.fsproj`:

```xml
<ItemGroup>
  <PackageReference Include="ToolUp.RAG.Server" />
  <PackageReference Include="ToolUp.EmbeddingProviders.OpenAI" />
</ItemGroup>
```

## 2. Wire an embedding provider

```fsharp
open ToolUp.EmbeddingProviders.OpenAI

let embedder = OpenAIEmbeddingProvider.create secretStore :> IEmbeddingProvider
```

The provider reads the OpenAI API key from `ISecretStore` under the `_platform` scope, key name `OPENAI_API_KEY`. Store it once at setup (the same way you stored the AI provider key in the AI walkthrough).

## 3. Switch from `AIServerApp.create` to `RAGServerApp.create`

```fsharp
open ToolUp.RAG

RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> RAGServerApp.withConfig serverConfig
|> RAGServerApp.withAuth authProvider
|> RAGServerApp.withStorage blobStorage
|> RAGServerApp.addModules modules
|> RAGServerApp.withAITools AITools.allTools
|> RAGServerApp.run
```

`RAGServerApp` is a flat superset of `AIServerApp`. Every `AIServerApp.with*` helper is mirrored on `RAGServerApp`. Plus RAG-specific tuning:

- `withTopK 10` — how many chunks to retrieve per query (default 5).
- `withMinScore 0.4` — minimum cosine similarity to include a chunk (default 0.3).
- `withMergeStrategy DenseSparseHybrid` — combine dense + BM25 sparse signals (default `DenseOnly`).
- `withSnippetCharLimit 500` — truncate long chunks before prompt injection (default 1500).
- `withOriginFilter (Some [Team; Platform])` — restrict retrievable origins (default `None` = all readable origins).
- `withGroundingMode StrictlyGrounded` — assistant refuses to answer when retrieval yields nothing (default `Permissive`).
- `withIngestionConcurrency 4` — max concurrent ingestion jobs (default 2).
- `withIngestionQueueCapacity 1000` — bounded queue size (default `None` = unbounded).
- `withTelemetry myRagTelemetry` — register a custom `IRagTelemetry` impl (default `NoOpRagTelemetry`).

The defaults are sensible for most deployments. Tune when you have evidence.

## 4. Verify the wiring

Start the server. Visit `/health/rag`:

```bash
curl http://localhost:5000/health/rag
```

Returns JSON with the embedding provider's `ProviderId` / `ModelId` / `Dimensions`, vector-store status, ingestion-queue depth, and rolling-window stats (embedding latency, retrieval hit/miss/empty counts).

## 5. Ingest a document

The simplest path: use `ToolUp.KnowledgeBase` — adds a multi-page module with document upload + multi-format extraction (PDF / PPTX / DOCX / XLSX / CSV / TXT / MD). Add the package and the matching client wrapper; documents land in `IBlobStorage`, the post-save hook indexes them.

```xml
<PackageReference Include="ToolUp.KnowledgeBase.Server" />
<PackageReference Include="ToolUp.KnowledgeBase.Client" />
```

Upload a document via the Knowledge Base sidebar entry. Watch the ingestion-status panel in the UI; ingestion-status updates flow over SSE.

For programmatic ingestion (no UI), enqueue an `IngestionJob` directly:

```fsharp
open ToolUp.RAG

let queue = serviceProvider.GetRequiredService<IngestionQueue>()
do! queue.Enqueue {
    DocumentId = Guid.NewGuid()
    Scope = Team teamId
    Chunks = [
        { Id = Guid.NewGuid(); Text = "..."; Metadata = Map.empty; ... }
    ]
}
```

The background `IngestionBackgroundService` dequeues and indexes. Watch `KnowledgeChunkIndexed` events under `_platform.ingestion` in the audit log.

## 6. Chat with the assistant — watch retrieval ground answers

Open the AI assistant. Ask a question about the document you uploaded:

```
What does our marketing strategy say about Q3 priorities?
```

The retrieval pipeline embeds the query, fetches the top-K chunks from the team scope, injects them into the system prompt as `## Retrieved context\n\n[Doc abc, Chunk 1 of 5]: ...`, and the agent answers grounded in those chunks.

Inspect the retrieval trail via `/dev/inspect` (when `EnableDevEndpoints` is on) or query the audit log:

```fsharp
let! events = eventStore.ReadByType("_platform.retrieval", "KnowledgeRetrieved")
```

Each `KnowledgeRetrieved` event carries the hashed query (`SHA256`, never plaintext), top-K, scope filter, latency, top score, and result count. Use it for retrieval-quality monitoring.

## 7. Author a custom `VectorisationHandler`

Modules that emit non-document content can plug into the ingestion pipeline. Declare a handler in `Server.fs`:

```fsharp
let myDataVectorisationHandler : VectorisationHandler = {
    DataTypeId = "MyDataType"
    Vectorise = fun (fileName, dataObject) -> async {
        // Translate processed data into chunks
        let chunks =
            processData dataObject
            |> List.map (fun entry -> {
                Id = Guid.NewGuid()
                Text = $"Entry: {entry.Description}\nValue: {entry.Value}"
                Metadata = Map.ofList [
                    "_source", "MyDataType"
                    "_fileName", fileName
                ]
                Origin = ChunkOrigin.UserContent
            })
        return chunks
    }
}
```

Register via `composeWithRAG`:

```fsharp
RAGServerApp.create (aiProviderFactory, aiConfigStore, embedder)
|> ...
|> RAGServerApp.withVectorisationHandler myDataVectorisationHandler
|> RAGServerApp.run
```

Now every save of `MyDataType` data triggers the handler post-save; the returned chunks queue for ingestion.

## 8. Tune retrieval quality

Default settings work well for most deployments, but a few patterns help:

- **Long documents → smaller chunks**: lower `ChunkingConfig.MaxTokens` (default 500) for legal / technical content where context windows are tight.
- **Code or tabular data → wider chunks**: raise `MaxTokens` to keep related rows together.
- **Multi-language deployments**: TF-IDF (`Local` provider) degrades badly on non-English; switch to a real embedding provider.
- **Drift over time**: when you upgrade the embedding model, run a one-shot scope reindex via `ReembeddingQueue.Enqueue`; the background service detects mismatched `EmbeddingVersion` stamps and replaces them.

## Next steps

- [concepts.md](concepts.md) — vector store internals, retrieval pipeline stages, ingestion + reembedding lifecycle, prompt-builder composition.
- [api-reference.md](api-reference.md) — the full public surface.
- [extending.md](extending.md) — write a new embedding provider, vector store, retrieval tracer.
