// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.RAG.StaticCorpus

open System
open System.IO
open System.Text.Json
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline

// ─── Static-corpus retrieval pipeline ────────────────────────────
//
// An `IRetrievalPipeline` backed by a build-time-precomputed `StaticCorpus`
// (chunk embeddings computed by the packer). Only the *query* is embedded at
// runtime — via the registered `IEmbeddingProvider` — so a static-doc
// deployment carries no live-ingestion machinery. `Retrieve` is a flat cosine
// scan over the in-memory embedding matrix, honouring `RetrievalRequest`'s
// `TopK` / `Filters` / `OriginFilter`. The corpus is read-only: `Index` and
// `DeleteByScope` raise `NotSupportedException` (rebuild via the packer).
//
// GP 12 audit: identity by value (`ChunkId` / string metadata), async at the
// retrieval boundary, no retry semantics (read-only), stateless between
// `Retrieve` calls (chunk embeddings pre-normalised once at construction, then
// never mutated), no cross-chunk ordering promise beyond the deterministic
// score sort, precision = deterministic top-k.

module StaticCorpusRetrievalPipeline =

    /// L2-normalise a vector so cosine similarity reduces to a dot product.
    /// A zero vector is returned unchanged (its similarity is 0 to everything).
    let private normalise (v: float32[]) : float32[] =
        let mutable mag = 0.0

        for x in v do
            mag <- mag + float x * float x

        let mag = sqrt mag

        if mag <= 0.0 then
            v
        else
            v |> Array.map (fun x -> float32 (float x / mag))

    /// Dot product over the shared prefix of two vectors (both are
    /// `EmbeddingDimensions` long in practice).
    let private dot (a: float32[]) (b: float32[]) : float =
        let n = min a.Length b.Length
        let mutable s = 0.0

        for i in 0 .. n - 1 do
            s <- s + float a[i] * float b[i]

        s

    /// `_source` metadata JSON the AI Sources panel renders as the citation
    /// header — shape matches the fields `RAGPromptBuilder` reads
    /// (`documentId` / `documentName` / `fileType` / `location`).
    let private sourceJson (source: string) =
        JsonSerializer.Serialize(
            {|
                documentId = source
                documentName = source
                fileType = "md"
                location = (null: obj)
            |}
        )

    type private StaticPipeline(corpus: StaticCorpus, embedder: IEmbeddingProvider) =
        // Pre-normalise chunk embeddings once — the only construction-time
        // state; never mutated per `Retrieve` (rule 4).
        let normChunks = corpus.Chunks |> Array.map (fun c -> c, normalise c.Embedding)

        let notSupported (op: string) =
            NotSupportedException(
                sprintf
                    "StaticCorpusRetrievalPipeline is read-only; %s is not supported. Rebuild the index via `dotnet toolup-rag pack-docs`."
                    op
            )

        interface IRetrievalPipeline with
            member _.Retrieve (request: RetrievalRequest) (_access: AccessContext) : Async<VectorMatch list> = async {
                if String.IsNullOrWhiteSpace request.Query || normChunks.Length = 0 then
                    return []
                else
                    let! queryRaw = embedder.GenerateEmbedding request.Query

                    if queryRaw.Length <> corpus.EmbeddingDimensions then
                        return
                            raise (
                                InvalidOperationException(
                                    sprintf
                                        "StaticCorpus embedding-dimension mismatch: the query embedder produced %d dims but the index was built at %d (model '%s'). Rebuild the index with the same embedding model the runtime uses."
                                        queryRaw.Length
                                        corpus.EmbeddingDimensions
                                        corpus.EmbeddingModel
                                )
                            )

                    let query = normalise queryRaw

                    // Metadata-equality filter (request.Filters) + origin filter.
                    // Static chunks are deployment documents, so they carry
                    // origin `Document` for `OriginFilter` purposes.
                    let passesFilters (c: DocChunk) =
                        match request.Filters with
                        | None -> true
                        | Some f -> f |> Map.forall (fun k v -> c.Metadata.TryFind k = Some v)

                    let passesOrigin =
                        match request.OriginFilter with
                        | None -> true
                        | Some origins -> origins.Contains ChunkOrigin.Document

                    let matches =
                        if not passesOrigin then
                            []
                        else
                            normChunks
                            |> Array.filter (fun (c, _) -> passesFilters c)
                            |> Array.map (fun (c, e) -> c, dot query e)
                            |> Array.sortByDescending snd
                            |> Array.truncate (max 0 request.TopK)
                            |> Array.toList
                            |> List.map (fun (c, score) ->
                                let metadata =
                                    c.Metadata
                                    |> Map.add
                                        ChunkMetadata.OriginKey
                                        (ChunkOrigin.toMetadataValue ChunkOrigin.Document)
                                    |> Map.add "_source" (sourceJson c.Source)

                                {
                                    ChunkId = c.Id
                                    Content = c.Body
                                    Score = score
                                    Scope = Deployment
                                    Metadata = metadata
                                })

                    return matches
            }

            member _.Index _ _ _ = raise (notSupported "Index")
            member _.DeleteByScope _ = raise (notSupported "DeleteByScope")

    /// Build a pipeline over an already-deserialised corpus + the query
    /// embedder (the same `IEmbeddingProvider` the deployment composes via
    /// `RAGServerApp.create`).
    let create (embedder: IEmbeddingProvider) (corpus: StaticCorpus) : IRetrievalPipeline =
        StaticPipeline(corpus, embedder) :> IRetrievalPipeline

    /// Load a `.scidx` index from a stream and build the pipeline.
    let loadFromStream (embedder: IEmbeddingProvider) (stream: Stream) : IRetrievalPipeline =
        use ms = new MemoryStream()
        stream.CopyTo ms
        create embedder (Serialization.deserialize (ms.ToArray()))

    /// Load a `.scidx` index from a file path and build the pipeline. The
    /// typical wiring: `withRetrievalPipeline (StaticCorpusRetrievalPipeline.loadFromFile embedder "docs.scidx")`.
    let loadFromFile (embedder: IEmbeddingProvider) (path: string) : IRetrievalPipeline =
        create embedder (Serialization.deserialize (File.ReadAllBytes path))