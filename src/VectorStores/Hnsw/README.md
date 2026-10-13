# ToolUp.VectorStores.Hnsw

HNSW approximate-nearest-neighbour `IVectorStore` for the `ToolUp.RAG` companion: one in-process graph per scope, built on the scope's first search after a cold load and kept current incrementally after that — a write is added to the graph, a delete or overwrite retires the old node, and the scope rebuilds only when retired nodes pass `HnswParameters.TombstoneRebuildFraction`.

Its scale is measured, not assumed: read the RAG technical guide's "Measured scale" sections (Phases 970 and 1007) before choosing it over the default `InMemoryVectorStore`. On the measured machine the graph build grows faster than linearly in the scope's size (`HnswParameters.fastBuild` is the cheaper build), and at the shipped `efSearch` the query was not faster than the flat scan at any size measured.

Persists the index to `IBlobStorage` for warm restart. For multi-instance / horizontal scale, a distributed vector store (e.g. Qdrant, Weaviate, Pinecone) is the right target — out of scope for this companion.

## Decision record — an alternative ANN implementation (Phase 1007)

**Budget.** A 500k-chunk scope must build in minutes, not hours. The cheaper `fastBuild` profile measured 12.2 ms per chunk at 50k and 100k chunks (1,221 s for 100k), so a 500k scope extrapolates to about 1.7 hours of single-threaded build: over budget, so one alternative ANN implementation was probed on the same benchmark.

**Probed: IVF-flat** — a spherical k-means coarse quantiser (`2·√n` lists, eight iterations) with exact rescoring inside the probed lists, the inverted-file index most vector databases offer beside HNSW. It needs no package: the probe was an in-process script over the benchmark's own corpus, padding and exact ground truth.

| BEIR SciFact, 5,183 chunks, local embedder | build | lists probed | recall@10 vs flat | p95 |
|---|---|---|---|---|
| IVF-flat | 3.3 s (0.63 ms/chunk) | 16 of 143 | 0.663 | 4 ms |
| | | 64 of 143 | 0.864 | 3 ms |
| | | 96 of 143 | 0.936 | 13 ms |
| | | 128 of 143 | 0.980 | 6 ms |
| HNSW `fastBuild`, `efSearch` 800 | 48 s (9.3 ms/chunk) | — | 1.000 | 164 ms |

**Decision: reject.** IVF builds about fifteen times faster, but on the local embedder's sparse vectors (88 non-zero slots of 512) the clusters do not separate the neighbours: recall within 2 % of the flat scan needs 90 % of the lists probed, which is a flat scan with extra steps, so at 500k its query costs what the flat scan's does (p95 429 ms, Phase 970). It buys nothing the default `InMemoryVectorStore` does not already give. The probe says nothing about dense embeddings (an API embedder's 1,536 dense dimensions cluster far better); a deployment on those that needs the 500k rung is better served by the Pgvector companion's indexes than by a second in-process ANN here. No port phase follows from this decision.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.
