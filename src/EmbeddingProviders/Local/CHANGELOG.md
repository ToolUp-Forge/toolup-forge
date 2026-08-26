# Changelog — ToolUp.EmbeddingProviders.Local

All notable changes to the `ToolUp.EmbeddingProviders.Local` package are recorded here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versions track the coordinated `ToolUp.Sdk` meta-release; per the
SemVer-on-0.x policy (see the repository `CLAUDE.md` "Versioning"
section), during `0.x` a minor bump may carry breaking changes while a
patch bump stays non-breaking.

## [Unreleased]

### Fixed

- **Dimension assignment is now feature-hashed, ending the vector
  invalidation caused by vocabulary churn.** The provider ranked its
  vocabulary by document frequency and rebuilt that ranking on *every*
  embed, while previously-indexed chunks were never re-embedded — so a
  chunk indexed early kept coordinates in a vocabulary the provider had
  since re-sorted, and on a small corpus dimension `0` could denote a
  different term by the time a query was issued. The failure was silent
  and total rather than degrading: retrieval returned confidently ranked
  nonsense. A term is now hashed (FNV-1a 64 + `fmix64`, signed) to a
  fixed slot, so the assignment cannot depend on arrival order or on
  document-frequency re-sorting. IDF weighting stays adaptive.
- Every vector is now the full declared `Dimensions` width. It was
  previously sized by the vocabulary known at embed time, so an early
  embed returned a shorter vector than the provider advertised.

### Changed — action required

- **`ModelId` moved on both shapes: `local-tfidf-v1` → `local-tfidf-v3`
  (unscoped) and `local-tfidf-v2` → `local-tfidf-v4` (the scope-keyed
  family prefix).** Vectors written by the pre-hashing scheme are not
  readable by this version — their coordinates denote a vocabulary that
  no longer exists, and nothing can recover it. This is a dev-tier reset,
  and it is handled automatically: `ReembeddingService` sees the
  `EmbeddingVersion` mismatch and re-embeds each stored corpus once. A
  persisted IDF state blob is unaffected and is still hydrated.

## [0.1.2]

Coordinated SDK release. No package-specific source changes since 0.1.0;
the version moved in lockstep with the `ToolUp.Sdk` meta-manifest.

## [0.1.0] - 2026-05-11

- Initial public release.
