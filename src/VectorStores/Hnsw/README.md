# ToolUp.VectorStores.Hnsw

HNSW approximate-nearest-neighbour `IVectorStore` for the `ToolUp.RAG` companion. Lifts the ~50,000-chunk ceiling of the default `InMemoryVectorStore`; suitable for single-instance deployments with hundreds of thousands to low millions of chunks.

Persists the index to `IBlobStorage` for warm restart. For multi-instance / horizontal scale, a distributed vector store (e.g. Qdrant, Weaviate, Pinecone) is the right target — out of scope for this companion.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
