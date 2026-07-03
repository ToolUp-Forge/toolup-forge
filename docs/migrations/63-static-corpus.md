# Phase 63 — `ToolUp.RAG.StaticCorpus` companion

**Ships:** three new packages (`ToolUp.RAG.StaticCorpus.{Core,Build,Server}`) +
an additive `RAGServerApp.withRetrievalPipeline` seam in `ToolUp.RAG.Server`.
**Additive / opt-in — no consumer change required to stay on the new SDK
version.**

## What changes

A new build-time-precomputed doc-retrieval path. Consumers point the packer at a
`docs/` folder at build time; at runtime the deployment loads the resulting
`.scidx` index and serves retrieval with **no chunk-embedding provider
dependency** (only the per-query embedding uses the registered
`IEmbeddingProvider`) and **no live-ingestion overhead**.

The only change to an existing surface is additive:

- `RAGServerApp` gains a `RetrievalPipelineOverride` field + a
  `withRetrievalPipeline` setter. When set, `composeWithRAG` registers the
  supplied `IRetrievalPipeline` verbatim and skips the default pipeline; when set
  **and** no `VectorisationHandler` is registered, it also suppresses the
  ingestion + reembedding background services. A deployment that never calls
  `withRetrievalPipeline` is byte-for-byte unchanged (GP 11).

## How to adopt (opt-in)

Nothing is required to stay on the new SDK version. To serve a docs corpus with
static retrieval:

1. Add the packages:

   ```xml
   <PackageReference Include="ToolUp.RAG.StaticCorpus.Server" />
   <PackageReference Include="ToolUp.RAG.StaticCorpus.Build" />   <!-- build-time -->
   ```

2. Drop your Markdown under `docs/` and add a `staticcorpus.json`:

   ```json
   { "include": ["docs/**/*.md"], "embeddingProvider": "hashing",
     "dimensions": 512, "output": "out/docs.scidx" }
   ```

3. Pack at build via the `.targets` item (or run `toolup-rag pack-docs`):

   ```xml
   <ItemGroup>
     <ToolUpRagStaticCorpus Include="docs/**/*.md"
                            Config="staticcorpus.json"
                            Output="$(IntermediateOutputPath)docs.scidx" />
   </ItemGroup>
   ```

4. Change one line in your compose:

   ```fsharp
   open ToolUp.RAG.StaticCorpus
   let embedder = HashingEmbeddingProvider.createDefault ()

   RAGServerApp.create providerFactory providerProfile embedder
   |> RAGServerApp.withRetrievalPipeline (
       StaticCorpusRetrievalPipeline.loadFromFile embedder "docs.scidx")
   |> RAGServerApp.run
   ```

Use the **same** embedder at pack time and runtime (same model + dimensions) so
the query embeds into the same space as the precomputed chunks. `hashing` is the
offline default; `openai` gives semantic retrieval (see
[`docs/rag/static-corpus.md`](../rag/static-corpus.md)).

## Verification

- `dotnet build` — the packer runs before compile and writes `docs.scidx`; a
  second build is a no-op (incremental).
- `dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Phase 63"`
  — the StaticCorpus pack: serialisation round-trip + determinism, chunker
  boundaries, the retrieval pipeline (`IRetrievalPipeline`), and packer
  determinism / incrementality.

## Rollback

Drop the `withRetrievalPipeline` call — the composition reverts to the default
dense/hybrid `RetrievalPipeline` with live ingestion. The `RetrievalPipelineOverride`
field defaults to `None`, so an app that never calls the setter is unaffected.
Remove the `<ToolUpRagStaticCorpus>` item to stop packing.
