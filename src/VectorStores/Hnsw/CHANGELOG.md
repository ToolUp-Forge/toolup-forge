# Changelog — ToolUp.VectorStores.Hnsw

All notable changes to the `ToolUp.VectorStores.Hnsw` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

Lands in the `0.27.0` draft, which the lockstep version advanced to for
this change (the `0.26.0` slot is released).

### Changed — breaking (Phase 1007)

- **`HnswParameters` gains two fields**, `ConstructionPruning` (the
  build-time candidate list, efConstruction; default `200`, the value
  HNSW.Net applied implicitly before) and `TombstoneRebuildFraction`
  (default `0.25`). A full record literal no longer compiles; a
  `{ HnswParameters.defaults with … }` copy-and-update is unaffected.
  `HnswParameters.validate` bounds both (`[1, 2000]` and `[0, 1]`).
- **A write no longer rebuilds the scope's graph.** An upsert into a
  scope whose graph is built is added to the graph at the next search;
  a delete, tombstone or overwrite retires the chunk's old node, which
  the search skips inside the graph traversal (so `topK` stays full);
  a restore revives the node; a same-vector rewrite keeps it. The scope
  rebuilds only on a cold load, or when retired nodes exceed
  `TombstoneRebuildFraction` of the graph. Before, every write marked
  the whole scope stale and the next search rebuilt it from scratch
  under the scope lock. The per-scope lock discipline and the on-disk
  shape are unchanged. Measured on BEIR SciFact (5,183 chunks): 100
  writes into the built scope cost 1.5 s at `fastBuild` and 14–46 s at
  the defaults, against a 48 s / 773 s rebuild of the same scope.

### Added (Phase 1007)

- **`HnswParameters.fastBuild`** — `defaults` with `ExpandBestSelection`
  off. Measured at the shipped `efSearch` (800): graph build 9.3 vs
  149 ms per chunk on SciFact and 7.9 vs 69 ms on a 3,000-chunk
  real-text corpus, recall@10 against the flat scan 1.000 on both (the
  defaults: 0.9997 and 1.000). `defaults` keeps `ExpandBestSelection`
  deliberately: its recall is the one the mixed-dim fidelity fixture
  and the opt-in corpus check pin. Figures and method: the RAG
  technical guide's "Measured scale (Phase 1007)".

### Fixed

- **Graph builds no longer allocate ~4 GB regardless of corpus size.**
  HNSW.Net's construction-time distance cache defaults to
  `InitialDistanceCacheSize = 1_048_576`, which allocates ~4 GB per graph
  build even for a three-vector corpus (measured on HNSW 26.4.177,
  linux-x64). Per-scope builds stack, so multi-scope ingestion or test
  runs could exhaust a 16 GB host. The store now pins the cache at 4096
  entries — measured ~10× lower allocation at every corpus scale with
  equal-or-better build times (disabling the cache entirely is worse:
  neighbour selection then recomputes distances combinatorially).
  Deterministic-build guarantees are unaffected. No public-surface
  change.

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.
