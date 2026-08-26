# ToolUp.EmbeddingProviders.Local

In-process TF-IDF `IEmbeddingProvider` for the `ToolUp.RAG` companion. Produces 512-dimensional vectors over a **fixed hashed feature space** — a term's dimension is a deterministic, culture-invariant hash of the term itself, so it never depends on what the provider has already seen. No external API key, no network. Suitable for local dev and CI.

Retains mutable IDF state across calls — explicitly dev-only; not intended for distributed deployments (violates Phase 9c rule 4 for portable interfaces). IDF weighting still adapts as documents arrive; that rescales coordinates and no longer permutes them.

**Vectors written before the hashing change are not readable by this version.** The earlier scheme ranked its vocabulary by document frequency and rebuilt that ranking on every embed, so dimension `i` denoted whichever term happened to be ranked `i`-th at index time. Both `ModelId` literals moved with the change (`local-tfidf-v3` unscoped, `local-tfidf-v4` scope-keyed), so `ReembeddingService` re-embeds a stored corpus once, automatically, exactly as for any other algorithm change. A persisted IDF state blob stays readable.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
