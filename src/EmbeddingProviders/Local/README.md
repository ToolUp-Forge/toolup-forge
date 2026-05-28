# ToolUp.EmbeddingProviders.Local

In-process TF-IDF `IEmbeddingProvider` for the `ToolUp.RAG` companion. Produces 512-dimensional vectors over an evolving vocabulary; no external API key, no network. Suitable for local dev and CI.

Retains mutable IDF state across calls — explicitly dev-only; not intended for distributed deployments (violates Phase 9c rule 4 for portable interfaces).

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
