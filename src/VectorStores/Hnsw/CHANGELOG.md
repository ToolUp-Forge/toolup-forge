# Changelog — ToolUp.VectorStores.Hnsw

All notable changes to the `ToolUp.VectorStores.Hnsw` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

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
