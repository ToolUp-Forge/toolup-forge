module ToolUp.RAG.Benchmarks.PipelineFactory

open System
open System.IO
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IEmbeddingCache
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryEmbeddingCache
open ToolUp.RAG.CachingEmbeddingProvider
open ToolUp.RAG.RetrievalPipeline
open ToolUp.RAG.RetrievalTracers
open ToolUp.RAG.VectorStores.Hnsw

// ─── CLI choice DUs ──────────────────────────────────────────────

type EmbedderChoice =
    | Local
    | OpenAI

type VectorStoreChoice =
    | Flat
    | Hnsw

/// Knobs that vary per run. Reranker is `None` in v1 — no concrete reranker
/// companion ships yet, so the CLI flag warns and ignores. MMR is wired
/// through `RetrievalPipelineOptions` and works against any embedder /
/// store combination.
type BuildOptions = {
    Embedder: EmbedderChoice
    VectorStore: VectorStoreChoice
    EnableMmr: bool
    MmrLambda: float
}

// ─── OpenAI key resolution ───────────────────────────────────────

/// The OpenAI embedder reads `_platform / "openai-api-key"` from `ISecretStore`.
/// Backed by an env-var lookup that accepts either the conventional
/// `OPENAI_API_KEY` (what most operators have set) or the literal
/// `openai-api-key` (what `EnvironmentSecretStore` would read for the
/// `_platform` scope). Keeps the benchmark friction-free without forcing a
/// non-standard env var name.
let private openAiSecretStore: ISecretStore =
    { new ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            if scopeId = "_platform" && key = "openai-api-key" then
                let value =
                    [ "OPENAI_API_KEY"; "openai-api-key" ]
                    |> List.tryPick (fun name ->
                        match Environment.GetEnvironmentVariable name with
                        | null
                        | "" -> None
                        | v -> Some v)

                return value
            else
                return None
        }

        member _.SetSecret(_, _, _) = async { return Error "benchmark secret store is read-only" }
        member _.DeleteSecret(_, _) = async { return Error "benchmark secret store is read-only" }
        member _.ListKeys(_) = async { return [] }
    }

// ─── Pipeline construction ──────────────────────────────────────

/// Build a fresh `IRetrievalPipeline` for one benchmark run. `tempDir` is
/// the per-run isolated `LocalFileStorage` root — the caller is responsible
/// for creating and cleaning it. The returned pipeline wires the production
/// `CachingEmbeddingProvider` over the chosen embedder; in replicate-N
/// mode this collapses repeated content to one upstream call.
let build (tempDir: string) (opts: BuildOptions) : IRetrievalPipeline =
    Directory.CreateDirectory tempDir |> ignore

    let storage = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
    let logger = ConsoleLogger.ConsoleLogger() :> ILogger

    // 1. Choose embedder. Local needs no secrets; OpenAI reads
    //    `OPENAI_API_KEY` (or `openai-api-key`) from env.
    let baseEmbedder: IEmbeddingProvider =
        match opts.Embedder with
        | Local -> LocalEmbeddingProvider.create ()
        | OpenAI -> OpenAIEmbeddingProvider.create openAiSecretStore

    // 2. Wrap with the production cache. Critical for replicate-N: same
    //    content → one underlying call. The module-level `create` is in
    //    scope from `open ToolUp.RAG.CachingEmbeddingProvider`; resolving
    //    unqualified avoids a class-vs-module name collision F# would
    //    otherwise resolve to the type.
    let embeddingCache: IEmbeddingCache =
        new InMemoryEmbeddingCache() :> IEmbeddingCache

    let cachedEmbedder = create baseEmbedder embeddingCache

    // 3. Choose vector store. flushIntervalMs = 50 mirrors the eval
    //    project's pattern — every Index call should be visible to the
    //    next Retrieve without waiting on the production 2 s timer.
    let vectorStore: IVectorStore =
        match opts.VectorStore with
        | Flat -> new InMemoryVectorStore(storage, logger, flushIntervalMs = 50) :> IVectorStore
        | Hnsw -> HnswVectorStore.create storage (Some logger)

    // 4. BM25 sparse index — always wired (matches production's hybrid
    //    retrieval). The benchmark scores the production pipeline shape,
    //    not a cosine-only stripped variant.
    let sparseIndex: ISparseIndex =
        new InMemoryBM25Index(storage, logger, flushIntervalMs = 50) :> ISparseIndex

    let pipelineOptions: RetrievalPipelineOptions = {
        Reranker = None
        EnableMmr = opts.EnableMmr
        MmrLambda = opts.MmrLambda
        ActiveModuleBoost = RetrievalPipelineOptions.defaults.ActiveModuleBoost
        SummaryBoost = RetrievalPipelineOptions.defaults.SummaryBoost
        FactNarrativeJoinBoost = RetrievalPipelineOptions.defaults.FactNarrativeJoinBoost
        // Phase 506 — the benchmark wires no IQueryRewriter, so this bound
        // is never consulted; carried at the default so the record matches
        // the production shape it is meant to score.
        QueryRewriteTimeoutMs = RetrievalPipelineOptions.defaults.QueryRewriteTimeoutMs
    }

    let tracer: IRetrievalTracer = createNoOp ()

    RetrievalPipeline(
        store = vectorStore,
        embedder = cachedEmbedder,
        sparseIndex = sparseIndex,
        options = pipelineOptions,
        tracer = tracer
    )
    :> IRetrievalPipeline