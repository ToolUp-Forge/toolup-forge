# ToolUp.RAG.StaticCorpus.Server

The runtime half of static-corpus retrieval: an `IRetrievalPipeline` over a
build-time-precomputed `.scidx` embedding index.

```fsharp skip=fragment
RAGServerApp.create factory providerProfile embedder
|> RAGServerApp.withRetrievalPipeline (
    StaticCorpusRetrievalPipeline.loadFromFile embedder "docs.scidx")
|> RAGServerApp.run
```

- Only the **query** is embedded at runtime, via the registered
  `IEmbeddingProvider`; chunk embeddings are precomputed by the packer. So the
  deployment needs no ingestion queue, no reembedding service, and no
  chunk-embedding calls — `withRetrievalPipeline` suppresses that machinery.
- `Retrieve` is a flat cosine scan over the in-memory embedding matrix
  (embeddings L2-normalised once at load), honouring the request's `TopK`,
  `Filters` (metadata equality) and `OriginFilter`. Matches are deployment-scoped
  and carry a `_source` citation header + `_origin = Document`.
- The corpus is **read-only**: `Index` and `DeleteByScope` raise
  `NotSupportedException` — rebuild the index with `dotnet toolup-rag pack-docs`.
- Retrieval-outcome telemetry + miss diagnostics are recorded by the shared RAG
  prompt-builder (the same path the default pipeline uses), so `/health/rag`
  shows static-corpus retrievals with the same counters.

Load the index from a file (`loadFromFile`), a stream (`loadFromStream`), or an
already-deserialised `StaticCorpus` (`create`).

Licensed under Apache-2.0.
