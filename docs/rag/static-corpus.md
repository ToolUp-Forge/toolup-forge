# Static-corpus retrieval

Serve retrieval over a **build-time-precomputed** embedding index of a Markdown
documentation corpus. The chunk embeddings are computed once at build time and
shipped as a compact `.scidx` file; at runtime only the *query* is embedded, so
the deployment carries no ingestion queue, no reembedding service, and no
per-chunk embedding calls.

Use it for a docs-aware assistant over content you own and rebuild at deploy
time: your project's `docs/` folder, a product help corpus, a third-party Q&A
bot. Do **not** use it for user-uploaded / live-changing knowledge — that is
what the default `ToolUp.RAG` pipeline (with ingestion + a vector store) is for.

## Packages

| Package | Role |
|---|---|
| `ToolUp.RAG.StaticCorpus.Core` | `DocChunk` / `StaticCorpus` types + the MessagePack `.scidx` (de)serialisation. |
| `ToolUp.RAG.StaticCorpus.Build` | The Markdown chunker + the `toolup-rag pack-docs` CLI + the MSBuild target. |
| `ToolUp.RAG.StaticCorpus.Server` | `StaticCorpusRetrievalPipeline` (`IRetrievalPipeline`) + an offline `HashingEmbeddingProvider`. |

## The `withRetrievalPipeline` seam

`RAGServerApp` gained a `withRetrievalPipeline` setter (`ToolUp.RAG.Server`) that
substitutes the whole `IRetrievalPipeline`. When it is set **and** no module
contributes a `VectorisationHandler`, `composeWithRAG` also suppresses the
ingestion + reembedding background services — so a static-doc deployment pays
nothing for the live-ingestion machinery it doesn't use.

```fsharp
open ToolUp.RAG.StaticCorpus

let embedder = HashingEmbeddingProvider.createDefault ()   // or a real model

RAGServerApp.create providerFactory providerProfile embedder
|> RAGServerApp.withRetrievalPipeline (
    StaticCorpusRetrievalPipeline.loadFromFile embedder "docs.scidx")
|> RAGServerApp.run
```

The embedder passed to `StaticCorpusRetrievalPipeline` must be the **same
provider** the index was packed with (same model + dimensions), because the
query is embedded into the same space as the precomputed chunk vectors. A
dimension mismatch fails loudly at the first retrieval.

## Packing the index

Point the packer at a `docs/` folder with a `staticcorpus.json` config:

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

Then either run the CLI directly:

```bash
dotnet tool install --global ToolUp.RAG.StaticCorpus.Build
toolup-rag pack-docs --config staticcorpus.json
```

or wire it into a project build via the packaged `.targets`:

```xml
<ItemGroup>
  <ToolUpRagStaticCorpus Include="docs/**/*.md"
                         Config="staticcorpus.json"
                         Output="$(IntermediateOutputPath)docs.scidx" />
</ItemGroup>
```

The target packs before compile and ships the `.scidx` as content.

### Embedding providers

- **`hashing`** (default) — an offline, deterministic feature-hashing
  bag-of-words embedder (`HashingEmbeddingProvider`). No API key, no network,
  reproducible across machines. It is *lexical* — retrieval matches shared
  vocabulary, not deep semantics — but it needs no credentials and is ideal for
  small corpora, samples, and CI. Compose the **same** `HashingEmbeddingProvider`
  at runtime.
- **`openai`** — the OpenAI embeddings API for semantic retrieval (API key read
  from `ISecretStore` at `_platform / "openai-api-key"`). Compose the matching
  OpenAI embedding provider at runtime.

## Chunking guarantees

The chunker parses Markdown and splits on **H2/H3** heading boundaries. Each
chunk carries:

- its full ancestor **heading path** (`["# Title"; "## Section"; "### Sub"]`), so
  a retrieved chunk always carries its section context;
- an **anchor** slug (`Metadata["anchor"]`) for a `source#anchor` jump-link;
- a stable **id** — `{source}:{heading-anchor}:{ordinal}`.

Fenced code blocks are never split mid-fence. A section body over
`maxChunkChars` is split on whole-block boundaries (a paragraph or a code fence
is atomic), and every split replays the same heading path.

## Determinism

Packing is byte-reproducible: files are enumerated in a stable order, chunk ids
and heading paths are deterministic, and the corpus `BuiltUtc` is an injected
value (`SOURCE_DATE_EPOCH`, or the Unix epoch by default) rather than the
wall-clock. Packing the same corpus twice yields a byte-identical `.scidx`.

The pack is incremental two ways: the MSBuild target's timestamp check, and the
packer's own content hash of `(packer version + config + file bytes)` written to
a `<output>.inputs` sidecar — so an unchanged re-pack is a no-op even on a fresh
checkout where file timestamps are unreliable. This determinism lets an
evaluation suite pin retrieval behaviour to a specific index build.

## Observability

Retrievals flow through the same prompt-builder path as the default pipeline, so
`/health/rag` shows the static corpus's retrieval counters with the same shape —
an operator switching a deployment from live KB to a static corpus does not have
to re-learn the observability surface.

## Read-only

The corpus is immutable at runtime: `IRetrievalPipeline.Index` and
`DeleteByScope` raise `NotSupportedException`. To change the corpus, edit the
Markdown and re-pack.
