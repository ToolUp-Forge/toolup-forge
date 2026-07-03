# ToolUp.RAG.StaticCorpus.Build

Build-time tooling for **static-corpus retrieval**: the Markdown chunker and the
packer CLI that precompute an embedding index (`.scidx`) over a documentation
corpus.

- **`Chunker`** — parses Markdown with Markdig and splits it into retrievable
  chunks on H2/H3 heading boundaries. Each `RawChunk` carries its full ancestor
  `HeadingPath` (so a retrieved chunk keeps its section context), the raw section
  body, and an `anchor` slug for `source#anchor` jump-links. A section body over
  the character budget is split on block boundaries — fenced code blocks and
  paragraphs are never split mid-way. Chunk identity is by value:
  `{source}:{heading-anchor}:{ordinal}`. Pure function, no I/O.

- **`pack-docs` CLI** (a later slice) — walks a configured include set, chunks
  each document, embeds each chunk once via the configured `IEmbeddingProvider`
  (caching embedding responses on disk against input hashes), and writes the
  deterministic `.scidx` index consumed at runtime by
  `ToolUp.RAG.StaticCorpus.Server`.

Licensed under Apache-2.0.
