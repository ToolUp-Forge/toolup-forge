# Adding static-corpus retrieval

This page shows how to give HelloWorld a docs-aware assistant that answers
questions from *this very `docs/` folder* using a build-time-precomputed
retrieval index — with no live-ingestion machinery.

## 1. Pack the docs at build

Add a `staticcorpus.json` next to the project (see the sample's own file) and
pack it into a `.scidx` index:

```bash
dotnet toolup-rag pack-docs --config staticcorpus.json
```

The packer chunks each Markdown file on heading boundaries, embeds each chunk
once (the offline `hashing` provider needs no API key), and writes a
deterministic `docs.scidx`.

## 2. Wire the pipeline into the composition root

Compose with `RAGServerApp` and swap in the static-corpus pipeline. Because the
override is set and no module contributes a `VectorisationHandler`, the ingestion
and reembedding background services are suppressed — the deployment carries no
live-ingestion overhead.

```fsharp
open ToolUp.RAG.StaticCorpus

let embedder = HashingEmbeddingProvider.createDefault ()

RAGServerApp.create providerFactory providerProfile embedder
|> RAGServerApp.withConfig config
|> RAGServerApp.addModule helloWorldModule
|> RAGServerApp.withRetrievalPipeline (
    StaticCorpusRetrievalPipeline.loadFromFile embedder "docs.scidx")
|> RAGServerApp.run
```

Use the **same** embedder at pack time and runtime so the query embeds into the
same space as the precomputed chunks.

## 3. Ask a question

With an AI provider configured, the assistant answers "what does HelloWorld do?"
from the retrieved chunks of `overview.md` / `module-shape.md`, with citation
markers pointing back at the source sections — and never makes a chunk-embedding
call at runtime beyond the single per-query embedding.

See `docs/rag/static-corpus.md` in the SDK for the full reference.
