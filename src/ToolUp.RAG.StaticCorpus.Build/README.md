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

- **`toolup-rag pack-docs` CLI** (`Packer` + `Program`) — walks a configured
  include set (`staticcorpus.json`), chunks each document, embeds each chunk once
  via the configured provider (`hashing` offline default, or `openai`), caches
  embedding responses on disk against `(model, text)` hashes, and writes the
  deterministic `.scidx` index consumed at runtime by
  `ToolUp.RAG.StaticCorpus.Server`. Exit codes: `0` clean, `1` config invalid,
  `2` embedding-provider error, `3` output write failure.

## `staticcorpus.json`

```json
{
  "include": ["docs/**/*.md"],
  "exclude": ["**/drafts/**"],
  "embeddingProvider": "hashing",
  "dimensions": 512,
  "maxChunkChars": 1500,
  "output": "out/docs.scidx"
}
```

Run `dotnet toolup-rag pack-docs --config staticcorpus.json`, or wire the packer
into a project build with the shipped `ToolUp.RAG.StaticCorpus.Build.targets`:

```xml
<ItemGroup>
  <ToolUpRagStaticCorpus Include="docs/**/*.md"
                         Config="staticcorpus.json"
                         Output="$(IntermediateOutputPath)docs.scidx" />
</ItemGroup>
```

The pack is incremental: it content-hashes the inputs (config + docs + packer
version) and writes a `<output>.inputs` sidecar, so an unchanged re-pack is a
no-op even on a fresh checkout where timestamps are unreliable. Setting
`SOURCE_DATE_EPOCH` fixes the corpus `BuiltUtc` for reproducible builds (it
defaults to the Unix epoch, so the `.scidx` is byte-reproducible by default).

Licensed under Apache-2.0.
