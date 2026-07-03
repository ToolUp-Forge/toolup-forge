# ToolUp.RAG.StaticCorpus.Core

Core types and binary index format for **static-corpus retrieval** — a
build-time-precomputed embedding index over a documentation corpus.

Consumers point the packer at a `docs/` folder at build time; at runtime the
retrieval pipeline loads the resulting `.scidx` index and serves cosine-similarity
retrieval with **no chunk-embedding provider dependency** (only the per-query
embedding uses the registered `IEmbeddingProvider`) and no live-ingestion overhead.

This package carries only:

- **`DocChunk` / `StaticCorpus`** (`Types.fs`) — pure, Fable-compatible records.
  A chunk holds its `HeadingPath` (full ancestor heading chain), `Body`,
  precomputed `Embedding` (`float32[]`), and `Metadata`.
- **`Serialization`** (`Serialization.fs`, .NET-only) — the MessagePack binary
  (de)serialisation of a `StaticCorpus`. Written with the low-level
  `MessagePackWriter` / `MessagePackReader` for a fully deterministic byte layout
  (fixed field order; metadata written in sorted-key order), so packing the same
  corpus twice yields a byte-identical blob.

The build-time chunker + packer CLI live in `ToolUp.RAG.StaticCorpus.Build`; the
`IRetrievalPipeline` implementation lives in `ToolUp.RAG.StaticCorpus.Server`.

The types ship under `fable/` in the nupkg so a Fable-side consumer can model a
loaded corpus; the MessagePack layer is `#if !FABLE_COMPILER`-gated (MessagePack-CSharp
is not a Fable library), so Fable compiles only the pure types.

Licensed under Apache-2.0.
