module ToolUp.Platform.Tests.InProcess.SparseIndexCompositionTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.ISparseIndex
open ToolUp.Platform.Infra.IEmbeddingProvider
open ToolUp.Platform.Infra.IRetrievalPipeline
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.RAGCompose
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Compose

// ─── Phase 893 — the keyword index is a composition choice ───────────
//
// `composeRAG` used to construct `InMemoryBM25Index` unconditionally, so a
// deployment that had moved its dense store to the database still kept its
// keyword index in one process. What is pinned here:
//
//  • unset is the pre-893 composition — the in-process index, resolved from
//    DI exactly as before (the byte-identical pin, GP 11);
//  • `withSparseIndex` registers the supplied index verbatim, and refuses
//    at composition when an analyzer is composed beside it (the analyzer
//    would be silently ignored);
//  • `withAnalyzedSparseIndex` builds the index for the COMPOSED analyzer —
//    the seam an index that tokenises in its own engine maps (or refuses)
//    the analyzer through;
//  • `withoutSparseIndex` registers no keyword index and retrieval is the
//    dense list, unfused;
//  • the replica validator fires for an in-process store at two replicas,
//    is silent at one, and is lifted by the store that removes its premise.

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

let private constantEmbedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts = async { return texts |> Seq.map (fun _ -> unitVec) |> Seq.toArray }

        member _.ProviderId = "stub"
        member _.ModelId = "constant"
        member _.Dimensions = 8
    }

let private stubFactory =
    { new ToolUp.AI.IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None

        member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }

        member _.BuildPlatform(_, _, _) = None
    }

let private stubProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

/// A keyword index that is NOT the in-process one — structurally what the
/// database companion looks like to the composition.
let private externalIndex () =
    { new ISparseIndex with
        member _.Upsert _ _ _ = async { return () }
        member _.Search _ _ _ = async { return [] }
        member _.DeleteByScope _ = async { return () }
        member _.DeleteChunk _ _ = async { return () }

        member _.Erase(_, _, _, _) = async {
            return
                Ok {
                    HandlerName = "sparse-index"
                    RecordsAffected = 0
                    Note = None
                }
        }
    }

/// A vector store that is NOT one of the in-process ones.
let private externalVectorStore () =
    let inner =
        new InMemoryVectorStore(InMemoryBlobStorage() :> BlobStorage.IBlobStorage, flushIntervalMs = 60000)
        :> IVectorStore

    { new IVectorStore with
        member _.Upsert s c v t = inner.Upsert s c v t
        member _.Search s q k = inner.Search s q k
        member _.ListChunks s d = inner.ListChunks s d
        member _.DeleteChunk s c = inner.DeleteChunk s c
        member _.RestoreChunk s c = inner.RestoreChunk s c
        member _.Vacuum s r = inner.Vacuum s r
        member _.DeleteByScope s = inner.DeleteByScope s
        member _.ListScopes() = inner.ListScopes()
        member _.Erase(s, u, p, d) = inner.Erase(s, u, p, d)
    }

/// A planted pass-through decorator (telemetry, caching or a
/// disclosure gate would have this shape). No vector-store decorator exists
/// in `src/`, so the blind spot a name match has for one is shown here. It
/// forwards its inner store's locality declaration, as the
/// `IVectorStoreLocality` contract asks of every decorator.
type private TracingVectorStore(inner: IVectorStore) =
    interface IVectorStoreLocality with
        member _.IndexLocality = VectorIndexLocality.declared inner

    interface IVectorStore with
        member _.Upsert s c v t = inner.Upsert s c v t
        member _.Search s q k = inner.Search s q k
        member _.ListChunks s d = inner.ListChunks s d
        member _.DeleteChunk s c = inner.DeleteChunk s c
        member _.RestoreChunk s c = inner.RestoreChunk s c
        member _.Vacuum s r = inner.Vacuum s r
        member _.DeleteByScope s = inner.DeleteByScope s
        member _.ListScopes() = inner.ListScopes()
        member _.Erase(s, u, p, d) = inner.Erase(s, u, p, d)

/// A planted pass-through decorator over a keyword index, the
/// sparse twin of `TracingVectorStore`. No keyword-index decorator exists
/// in `src/`, so the blind spot a type test has for one is shown here. It
/// forwards its inner index's locality declaration, as the
/// `ISparseIndexLocality` contract asks of every decorator.
type private TracingSparseIndex(inner: ISparseIndex) =
    interface ISparseIndexLocality with
        member _.IndexLocality = SparseIndexLocality.declared inner

    interface ISparseIndex with
        member _.Upsert s c t = inner.Upsert s c t
        member _.Search s q k = inner.Search s q k
        member _.DeleteByScope s = inner.DeleteByScope s
        member _.DeleteChunk s c = inner.DeleteChunk s c
        member _.Erase(s, u, p, d) = inner.Erase(s, u, p, d)

/// A type that only BORROWS the HNSW companion's name. It holds
/// no index of its own: every call goes to the external (cross-replica)
/// store it was handed.
module private Impostor =
    type HnswVectorStore(shared: IVectorStore) =
        interface IVectorStore with
            member _.Upsert s c v t = shared.Upsert s c v t
            member _.Search s q k = shared.Search s q k
            member _.ListChunks s d = shared.ListChunks s d
            member _.DeleteChunk s c = shared.DeleteChunk s c
            member _.RestoreChunk s c = shared.RestoreChunk s c
            member _.Vacuum s r = shared.Vacuum s r
            member _.DeleteByScope s = shared.DeleteByScope s
            member _.ListScopes() = shared.ListScopes()
            member _.Erase(s, u, p, d) = shared.Erase(s, u, p, d)

let private newHnswStore () =
    new ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.HnswVectorStore(
        InMemoryBlobStorage() :> BlobStorage.IBlobStorage,
        flushIntervalMs = 60000
    )

let private newApp () =
    RagOptions.create stubFactory stubProfile constantEmbedder

/// The deployment `rag` composes onto: in-memory storage, `replicas`
/// replicas, resolved as `run` resolves it.
let private composedAt (replicas: int) (rag: RagOptions) : ServerApp =
    ServerApp.empty
    |> ServerApp.withConfig {
        ServerConfig.defaults with
            MultiInstance = {
                ServerConfig.defaults.MultiInstance with
                    ReplicaCount = replicas
            }
    }
    |> ServerApp.withStorage (InMemoryBlobStorage() :> BlobStorage.IBlobStorage)
    |> withRAG rag
    |> ServerApp.resolve

let private composed (rag: RagOptions) : ServerApp = composedAt 1 rag

let private provider (app: RagOptions) : ServiceProvider =
    let composed = composed app
    let services = ServiceCollection() :> IServiceCollection

    let services =
        ToolUp.Platform.Tests.Support.TestSubstrate.applyServiceConfig composed services

    services.BuildServiceProvider()

let private resolvedSparse (app: RagOptions) : ISparseIndex option =
    use sp = provider app

    match sp.GetService typeof<ISparseIndex> with
    | null -> None
    | o -> Some(o :?> ISparseIndex)

/// Run the replica validator exactly as `composeRAG` registered it.
let private replicaVerdict (replicas: int) (app: RagOptions) : ConfigValidation.ValidationResult =
    let composed = composedAt replicas app

    let v =
        composed.ConfigValidators
        |> List.find (fun v -> v.Name = "rag-in-process-index-replicas")

    v.Validate() |> Async.RunSynchronously

let private warningText (result: ConfigValidation.ValidationResult) =
    match result with
    | ConfigValidation.ValidationResult.Warning message -> message
    | other -> failtestf "expected a Warning, got %A" other

// ─── Phase 866 — retrieval scores carry their scale ──────────────────
//
// The default composition always builds a keyword index, so every default
// retrieval is fused by reciprocal rank fusion (k = 60), whose raw scores
// top out at 2/61 ≈ 0.033. A `MinScore` threshold documented as a cosine
// gate therefore dropped EVERY chunk above ~0.033, and the additive boosts
// (0.05 / 0.10 / 0.15) each exceeded the whole fused range. Pinned here, in
// the composition a deployment actually gets:
//
//  • MinScore 0.5 keeps a strong match and drops a weak one;
//  • a boost is a nudge within the space, never a tier above it;
//  • with no threshold and no boosts, result ORDER is unchanged;
//  • the validator reports a threshold whose space cannot be confirmed.

/// A lexical embedder: each word hashes to one of 64 dimensions and the
/// vector is L2-normalised, so cosine similarity tracks word overlap —
/// enough to rank a small corpus without a model.
let private lexicalEmbedder =
    let dims = 64

    let embed (text: string) =
        let v = Array.zeroCreate<float32> dims

        for word in text.ToLowerInvariant().Split([| ' '; ','; '.'; '?' |], StringSplitOptions.RemoveEmptyEntries) do
            let h = word |> Seq.fold (fun acc c -> (acc * 31 + int c) % 1_000_003) 7
            v[h % dims] <- v[h % dims] + 1.0f

        let norm = v |> Array.sumBy (fun x -> x * x) |> sqrt

        if norm = 0.0f then
            v
        else
            v |> Array.map (fun x -> x / norm)

    { new IEmbeddingProvider with
        member _.GenerateEmbedding text = async { return embed text }

        member _.GenerateEmbeddings texts = async { return texts |> Seq.map embed |> Seq.toArray }

        member _.ProviderId = "stub"
        member _.ModelId = "lexical"
        member _.Dimensions = dims
    }

let private lexicalApp () =
    RagOptions.create stubFactory stubProfile lexicalEmbedder

let private teamScope = Team "t1"

let private teamAccess = AccessContexts.forSubject (TeamMember("u1", "t1"))

let private indexAll (pipeline: IRetrievalPipeline) (chunks: (string * string * (string * string) list) list) = async {
    for chunkId, content, meta in chunks do
        do!
            pipeline.Index
                chunkId
                {
                    Content = content
                    Metadata = Map.ofList meta
                }
                teamScope
}

/// Run the retrieval prompt builder over `pipeline` and return the prompt
/// and the chunk ids surfaced as sources — what the model and the user see.
let private promptFor (defaults: RetrievalDefaults) (pipeline: IRetrievalPipeline) (question: string) = async {
    let sources = ref []

    let ctx: ToolUp.AI.SystemPromptBuilder.PromptContext = {
        Access = teamAccess
        ActiveModule = None
        ActivePage = None
        ActivePageNarrative = None
        ModuleContexts = Map.empty
        CurrentMessage = Some question
        ConversationHistory = []
        RetrievalFilters = None
        RetrievedSources = sources
        ShortCircuit = ref None
        PlannedAnswerId = ref None
        Scope = ResolvedScope.anonymous
        Services = ToolUp.AI.SystemPromptBuilder.PromptContext.noServices
    }

    let! prompt = ToolUp.RAG.RAGPromptBuilder.withRetrieval defaults None None pipeline ctx
    return prompt, sources.Value |> List.map _.Snippet
}

let private strongText = "acme quarterly revenue rose sharply in the third quarter"
let private weakText = "the office kitchen rota for next week"
let private otherText = "parking permits renew every january"

type private CapturingTracer() =
    let traces = ResizeArray<ToolUp.Platform.Infra.IRetrievalTracer.RetrievalTrace>()
    member _.Last = traces |> Seq.tryLast

    interface ToolUp.Platform.Infra.IRetrievalTracer.IRetrievalTracer with
        member _.Trace trace _ = async { traces.Add trace }
        member _.Miss _ _ = async { return () }

let private rankedMatch (chunkId: string) (meta: (string * string) list) : VectorMatch = {
    ChunkId = chunkId
    Content = "content " + chunkId
    Score = 0.5
    Scope = teamScope
    Metadata = Map.ofList meta
}

/// A pipeline whose two retrievers return FIXED rankings, so the fused
/// score of every candidate is known in advance: `1/(60+rank)` summed
/// over the lists it appears in.
let private fixedRankPipeline
    (dense: VectorMatch list)
    (sparse: VectorMatch list option)
    (options: ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions)
    (tracer: CapturingTracer)
    : IRetrievalPipeline =
    let inner = externalVectorStore ()

    let store =
        { new IVectorStore with
            member _.Upsert s c v t = inner.Upsert s c v t
            member _.Search _ _ k = async { return dense |> List.truncate k }
            member _.ListChunks s d = inner.ListChunks s d
            member _.DeleteChunk s c = inner.DeleteChunk s c
            member _.RestoreChunk s c = inner.RestoreChunk s c
            member _.Vacuum s r = inner.Vacuum s r
            member _.DeleteByScope s = inner.DeleteByScope s
            member _.ListScopes() = inner.ListScopes()
            member _.Erase(s, u, p, d) = inner.Erase(s, u, p, d)
        }

    let keyword =
        sparse
        |> Option.map (fun ranked ->
            let stub = externalIndex ()

            { new ISparseIndex with
                member _.Upsert s c t = stub.Upsert s c t
                member _.Search _ _ k = async { return ranked |> List.truncate k }
                member _.DeleteByScope s = stub.DeleteByScope s
                member _.DeleteChunk s c = stub.DeleteChunk s c
                member _.Erase(s, u, p, d) = stub.Erase(s, u, p, d)
            })

    match keyword with
    | Some index ->
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
            store,
            lexicalEmbedder,
            sparseIndex = index,
            options = options,
            tracer = tracer
        )
        :> IRetrievalPipeline
    | None ->
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store, lexicalEmbedder, options = options, tracer = tracer)
        :> IRetrievalPipeline

let private noBoosts: ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions = {
    ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults with
        ActiveModuleBoost = 0.0
        SummaryBoost = 0.0
        FactNarrativeJoinBoost = 0.0
}

let private summaryMeta = [ ChunkMetadata.IsSummaryKey, "true" ]

let private retrieveIds (pipeline: IRetrievalPipeline) = async {
    let! matches = pipeline.Retrieve (RetrievalRequest.create "query" [ teamScope ] 20 Interleaved) teamAccess
    return matches
}

/// Run the MinScore-space validator exactly as `composeRAG` registered it.
let private scoreSpaceVerdict (app: RagOptions) : ConfigValidation.ValidationResult =
    let composed = composed app

    let v =
        composed.ConfigValidators |> List.find (fun v -> v.Name = "rag-min-score-space")

    v.Validate() |> Async.RunSynchronously

let private stubReranker =
    { new ToolUp.Platform.Infra.IReranker.IReranker with
        member _.Name = "stub-reranker"
        member _.MaxBatchSize = 32
        member _.Rerank _ candidates = async { return candidates }
    }

let private externalPipeline () =
    { new IRetrievalPipeline with
        member _.Retrieve _ _ = async { return [] }
        member _.Index _ _ _ = async { return () }
        member _.DeleteByScope _ = async { return () }
    }

// ─── Phase 978 — the retrieval pipeline orders once ──────────────────
//
// Phase 866 found three ordering defects that pre-dated it, each pinned red
// here before the fix: (1) a pool larger than the reranker's batch kept an
// unreranked tail on the fused scale, and the Interleaved merge sorted
// across the two; (2) the boosts ran BEFORE the reranker, which replaced
// every score and so discarded them; (3) the Interleaved merge re-sorted
// AFTER MMR, undoing the diversity order MMR produced.

/// A reranker scoring each candidate from a fixed table, recording every
/// candidate it was shown — the contract shape (same set, new scores,
/// sorted descending, total-order tie-break).
type private TableReranker(maxBatch: int, scores: Map<string, float>) =
    let seen = ResizeArray<string>()
    member _.Seen = List.ofSeq seen

    interface ToolUp.Platform.Infra.IReranker.IReranker with
        member _.Name = "table-reranker"
        member _.MaxBatchSize = maxBatch

        member _.Rerank _ candidates = async {
            seen.AddRange(candidates |> List.map _.ChunkId)

            return
                candidates
                |> List.map (fun m -> {
                    m with
                        Score = scores |> Map.tryFind m.ChunkId |> Option.defaultValue 0.0
                })
                |> List.sortBy (fun m -> -m.Score, m.Scope, m.ChunkId)
        }

let private withContent (content: string) (m: VectorMatch) = { m with Content = content }

// ─── The stage marks: one fixture for every composition ─────────────

let private markFixturePath =
    IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "fixtures", "retrieval", "mark-sequences.txt")

/// The fixture's `<composition>: <marks>` lines, keyed by composition.
let private markFixture () =
    IO.File.ReadAllLines markFixturePath
    |> Array.filter (fun line -> not (line.StartsWith "#") && not (line.StartsWith "mark ") && line.Contains ": ")
    |> Array.map (fun line ->
        let colon = line.IndexOf ": "
        line.Substring(0, colon), line.Substring(colon + 2))
    |> Map.ofArray

/// The fixture's `mark <template>` lines: every mark the pipeline can emit.
let private markVocabulary () =
    IO.File.ReadAllLines markFixturePath
    |> Array.filter _.StartsWith("mark ")
    |> Array.map _.Substring(5)
    |> List.ofArray

let private stagesOf (request: RetrievalRequest) (build: CapturingTracer -> IRetrievalPipeline) = async {
    let tracer = CapturingTracer()
    let! _ = (build tracer).Retrieve request teamAccess

    return
        match tracer.Last with
        | Some trace -> trace.Stages
        | None -> failtest "a trace is emitted"
}

/// A composition with every optional collaborator wired: a fact resolver,
/// a disclosure gate, a query rewriter and the per-turn budgets. `faulty`
/// makes each of them fail the way its bound degrades: the embed raises,
/// the resolver overruns, the gate raises and the rewriter raises.
let private fullyWiredStages (faulty: bool) = async {
    let tracer = CapturingTracer()

    let ranked = [
        rankedMatch "plain" []
        rankedMatch "cites-f1" [ ChunkMetadata.FactRefsKey, "f1" ]
        rankedMatch "cites-f2" [ ChunkMetadata.FactRefsKey, "f2" ]
    ]

    let inner = externalVectorStore ()

    let store =
        { new IVectorStore with
            member _.Upsert s c v t = inner.Upsert s c v t
            member _.Search _ _ k = async { return ranked |> List.truncate k }
            member _.ListChunks s d = inner.ListChunks s d
            member _.DeleteChunk s c = inner.DeleteChunk s c
            member _.RestoreChunk s c = inner.RestoreChunk s c
            member _.Vacuum s r = inner.Vacuum s r
            member _.DeleteByScope s = inner.DeleteByScope s
            member _.ListScopes() = inner.ListScopes()
            member _.Erase(s, u, p, d) = inner.Erase(s, u, p, d)
        }

    let stub = externalIndex ()

    let keyword =
        { new ISparseIndex with
            member _.Upsert s c t = stub.Upsert s c t
            member _.Search _ _ k = async { return List.rev ranked |> List.truncate k }
            member _.DeleteByScope s = stub.DeleteByScope s
            member _.DeleteChunk s c = stub.DeleteChunk s c
            member _.Erase(s, u, p, d) = stub.Erase(s, u, p, d)
        }

    let embedder =
        if faulty then
            { new IEmbeddingProvider with
                member _.GenerateEmbedding _ = async { return failwith "the embedder is down" }
                member _.GenerateEmbeddings _ = async { return failwith "the embedder is down" }
                member _.ProviderId = "stub"
                member _.ModelId = "down"
                member _.Dimensions = 64
            }
        else
            lexicalEmbedder

    let fact (id: string) : ResolvedFact = {
        FactId = id
        Rendering = "21,800"
        Freshness = FactFresh
        SupersededBy = None
        Metric = "revenue"
    }

    let resolver =
        { new IFactResolver with
            member _.Resolve(_, _) = async {
                if faulty then
                    do! Async.Sleep 2_000

                return [ fact "f1"; fact "f2" ]
            }
        }

    let gate =
        { new IFactDisclosureGate with
            member _.Check(_, _, _, ids) = async {
                if faulty then
                    failwith "the gate is down"

                return
                    ids
                    |> List.map (fun id ->
                        id,
                        (if id = "f2" then
                             FactNotDisclosable "restricted"
                         else
                             FactDisclosable))
                    |> Map.ofList
            }
        }

    let rewriter =
        { new ToolUp.Platform.Infra.IQueryRewriter.IQueryRewriter with
            member _.Name = "stub-rewriter"

            member _.Rewrite query _ = async {
                if faulty then
                    failwith "the rewriter is down"

                return ToolUp.Platform.Infra.IQueryRewriter.QueryRewritten(query + " acme")
            }
        }

    let budgets = {
        ToolUp.RAG.RetrievalPipeline.RetrievalBudgets.defaults with
            FactStage = TimeSpan.FromMilliseconds 100.0
    }

    let pipeline =
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
            store,
            embedder,
            sparseIndex = keyword,
            options = {
                noBoosts with
                    FactNarrativeJoinBoost = 0.15
            },
            tracer = tracer,
            factResolver = resolver,
            disclosureGate = gate,
            queryRewriter = rewriter,
            budgets = budgets
        )
        :> IRetrievalPipeline

    let clause: FactClause = {
        SubjectHierarchy = "brand"
        SubjectPath = [ "acme" ]
        Metric = "revenue"
        PeriodFrom = None
        PeriodTo = None
        AsOf = None
    }

    let request = {
        RetrievalRequest.create "what about revenue" [ teamScope ] 20 Interleaved with
            FactClause = Some clause
            History = Some [ "tell me about acme" ]
    }

    let! _ = pipeline.Retrieve request teamAccess

    return
        match tracer.Last with
        | Some trace -> trace.Stages
        | None -> failtest "a trace is emitted"
}

/// One value of every `RetrievalStage` case, every payload alternative the
/// fixture's templates name.
let private everyStage: ToolUp.RAG.RetrievalPipeline.RetrievalStage list = [
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.AuthoriseScopes
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.FactResolve
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DisclosureFilter
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.FactsDegraded ToolUp.RAG.RetrievalPipeline.BoundedFault.TimedOut
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.FactsDegraded ToolUp.RAG.RetrievalPipeline.BoundedFault.Failed
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.QueryRewrite
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Dense
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DenseDegraded(QueryEmbedOutcome.TimedOut(TimeSpan.FromSeconds 5.0))
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DenseDegraded(QueryEmbedOutcome.Refused 4)
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DenseDegraded(QueryEmbedOutcome.Failed "down")
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Sparse
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.RRF
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.MetadataFilter
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.OriginFilter
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DerivedDisclosureDegraded
        ToolUp.RAG.RetrievalPipeline.BoundedFault.TimedOut
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DerivedDisclosureDegraded
        ToolUp.RAG.RetrievalPipeline.BoundedFault.Failed
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DerivedDisclosure
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.DerivedDisclosureDropped(1, 3)
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Normalise
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Rerank
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.RerankCapped(2, 6)
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Boost ToolUp.RAG.RetrievalPipeline.BoostKind.ActiveModule
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Boost ToolUp.RAG.RetrievalPipeline.BoostKind.Summary
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Boost ToolUp.RAG.RetrievalPipeline.BoostKind.FactNarrativeJoin
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.MMR
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.AdaptiveK
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.TopK
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.Merge None
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.FinalOrder ToolUp.RAG.RetrievalPipeline.OrderingStep.Interleaved
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.ScoreSpace RetrievalScoreSpace.Cosine
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.ScoreSpace RetrievalScoreSpace.Fused
    ToolUp.RAG.RetrievalPipeline.RetrievalStage.ScoreSpace RetrievalScoreSpace.Reranked
]

/// A fixture template as a pattern: `<a|b>` is one of the alternatives,
/// `<name>` any value.
let private templatePattern (template: string) =
    let body =
        System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Escape(template).Replace("\\|", "|"),
            "<([^>]*)>",
            fun m ->
                let inner = m.Groups[1].Value

                if inner.Contains "|" then "(" + inner + ")" else "[^ ]+"
        )

    System.Text.RegularExpressions.Regex("^" + body + "$")

let private apiReferencePath =
    IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "..", "..", "docs", "rag", "api-reference.md")

/// One request per composition the fixture names, each over fixed rankings.
let private markScenarios: (string * Async<string list>) list =
    let ranked = [ for i in 1..6 -> rankedMatch (sprintf "c%d" i) [] ]

    let tagged =
        ranked
        |> List.mapi (fun i m -> {
            m with
                Metadata =
                    Map.ofList [
                        ChunkMetadata.OriginKey, ChunkOrigin.toMetadataValue ChunkOrigin.Document
                        "dept", "ops"
                        if i = 1 then
                            ChunkMetadata.OriginModuleKey, "kb"
                        if i = 2 then
                            ChunkMetadata.IsSummaryKey, "true"
                    ]
        })

    let plain = RetrievalRequest.create "query" [ teamScope ] 20 Interleaved
    let scores = Map.ofList [ for i in 1..6 -> sprintf "c%d" i, 1.0 / float i ]

    let reranking (batch: int) = {
        noBoosts with
            Reranker = Some(TableReranker(batch, scores) :> ToolUp.Platform.Infra.IReranker.IReranker)
    }

    let everything = {
        plain with
            ActiveModule = Some "kb"
            Filters = Some(Map.ofList [ "dept", "ops" ])
            OriginFilter = Some(Set.ofList [ ChunkOrigin.Document ])
            AdaptiveK = Some { MinK = 1; MaxK = 4; ScoreFloor = 0.5 }
    }

    let mmr = {
        ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults with
            EnableMmr = true
    }

    [
        "dense-only", stagesOf plain (fixedRankPipeline ranked None noBoosts)
        "fused", stagesOf plain (fixedRankPipeline ranked (Some(List.rev ranked)) noBoosts)
        "reranked", stagesOf plain (fixedRankPipeline ranked (Some ranked) (reranking 32))
        "reranked-capped", stagesOf plain (fixedRankPipeline ranked (Some ranked) (reranking 2))
        "fused-boosted-filtered-mmr-adaptive",
        stagesOf everything (fixedRankPipeline tagged (Some(List.rev tagged)) mmr)
        "dense-only-boosted-filtered-adaptive",
        stagesOf
            everything
            (fixedRankPipeline tagged None ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults)
        "fused-separate", stagesOf { plain with Merge = Separate } (fixedRankPipeline ranked (Some ranked) noBoosts)
        "fused-facts-rewrite-disclosure", fullyWiredStages false
        "fused-every-bound-degraded", fullyWiredStages true
    ]

let tests =
    testList "Phase 893 — the keyword index is a composition choice" [

        testList "withSparseIndex / withAnalyzedSparseIndex / withoutSparseIndex" [
            test "unset ⇒ the composition still builds InMemoryBM25Index (GP 11)" {
                let app = newApp ()

                match app.SparseIndex with
                | SparseIndexComposition.InProcessSparseIndex -> ()
                | other -> failtestf "the default must be in-process, got %A" other

                match resolvedSparse app with
                | Some index -> Expect.isTrue (index :? InMemoryBM25Index) "the pre-893 default, unchanged"
                | None -> failtest "the default composition must register an ISparseIndex"
            }

            test "withSparseIndex registers the supplied index verbatim" {
                let supplied = externalIndex ()
                let app = newApp () |> RagOptions.withSparseIndex supplied

                match resolvedSparse app with
                | Some index -> Expect.isTrue (obj.ReferenceEquals(index, supplied)) "the composed instance"
                | None -> failtest "the supplied index must be registered"
            }

            test "withSparseIndex beside withSparseAnalyzer is refused at composition, naming the analyzer" {
                let analyzer =
                    ToolUp.RAG.SparseAnalysis.create "custom-analyzer-7" ToolUp.RAG.SparseAnalysis.tokeniseWords

                let app =
                    newApp ()
                    |> RagOptions.withSparseAnalyzer analyzer
                    |> RagOptions.withSparseIndex (externalIndex ())

                let ex = Expect.throwsC (fun () -> composed app |> ignore) id
                Expect.stringContains ex.Message "custom-analyzer-7" "the refusal names the analyzer"
                Expect.stringContains ex.Message "withAnalyzedSparseIndex" "and the builder that serves the case"
            }

            test "withAnalyzedSparseIndex builds the index for the composed analyzer, at composition" {
                let analyzer =
                    ToolUp.RAG.SparseAnalysis.create "custom-analyzer-9" ToolUp.RAG.SparseAnalysis.tokeniseWords

                let mutable seen: string list = []
                let supplied = externalIndex ()

                let app =
                    newApp ()
                    |> RagOptions.withSparseAnalyzer analyzer
                    |> RagOptions.withAnalyzedSparseIndex (fun a ->
                        seen <- seen @ [ a.Id ]
                        supplied)

                composed app |> ignore
                Expect.equal seen [ "custom-analyzer-9" ] "built once, at composition, for the composed analyzer"

                match resolvedSparse app with
                | Some index -> Expect.isTrue (obj.ReferenceEquals(index, supplied)) "the built index is registered"
                | None -> failtest "the built index must be registered"
            }

            test "withAnalyzedSparseIndex with no analyzer composed receives the identity analyzer" {
                let mutable seen = ""

                let app =
                    newApp ()
                    |> RagOptions.withAnalyzedSparseIndex (fun a ->
                        seen <- a.Id
                        externalIndex ())

                composed app |> ignore
                Expect.equal seen ToolUp.RAG.SparseAnalysis.IdentityAnalyzerId "the identity analyzer"
            }

            test "a refusal inside the builder surfaces at composition" {
                let app =
                    newApp ()
                    |> RagOptions.withAnalyzedSparseIndex (fun a ->
                        invalidOp (sprintf "no configuration for analyzer '%s'" a.Id))

                let ex = Expect.throwsC (fun () -> composed app |> ignore) id
                Expect.stringContains ex.Message "identity" "the builder's refusal, naming the analyzer"
            }

            test "withoutSparseIndex registers no keyword index" {
                let app = newApp () |> RagOptions.withoutSparseIndex
                Expect.isNone (resolvedSparse app) "dense only: nothing registered"
            }

            testAsync "withoutSparseIndex retrieves the dense list, unfused" {
                let app = newApp () |> RagOptions.withoutSparseIndex
                use sp = provider app
                let store = sp.GetRequiredService<IVectorStore>()
                let pipeline = sp.GetRequiredService<IRetrievalPipeline>()
                let scope = Team "t1"

                for chunkId in [ "c3"; "c1"; "c2" ] do
                    do!
                        store.Upsert scope chunkId unitVec {
                            Content = "note " + chunkId
                            Metadata = Map.empty
                        }

                let! dense = store.Search [ scope ] unitVec 10

                let! retrieved =
                    pipeline.Retrieve
                        (RetrievalRequest.create "note" [ scope ] 10 Interleaved)
                        (AccessContexts.forSubject (TeamMember("u1", "t1")))

                Expect.equal
                    (retrieved |> List.map _.ChunkId)
                    (dense |> List.map _.ChunkId)
                    "fusion over one list is that list"

                Expect.equal
                    (retrieved |> List.map _.Score)
                    (dense |> List.map _.Score)
                    "with the dense scores, not rank-fusion scores"
            }
        ]

        testList "InProcessIndexReplicaValidator" [
            test "fires at two replicas over the in-process stores, naming both and their replacements" {
                let message = newApp () |> replicaVerdict 2 |> warningText
                Expect.stringContains message "InMemoryVectorStore" "names the vector store"
                Expect.stringContains message "ToolUp.VectorStores.Pgvector" "and the companion replacing it"
                Expect.stringContains message "InMemoryBM25Index" "names the keyword index"
                Expect.stringContains message "ToolUp.SparseIndices.Postgres" "and the companion replacing it"
            }

            test "is silent at one replica" {
                Expect.equal
                    (newApp () |> replicaVerdict 1)
                    ConfigValidation.ValidationResult.Ok
                    "one replica has nothing to diverge"
            }

            test "is lifted when both composed stores span replicas" {
                let verdict =
                    newApp ()
                    |> RagOptions.withVectorStore (externalVectorStore ())
                    |> RagOptions.withSparseIndex (externalIndex ())
                    |> replicaVerdict 3

                Expect.equal verdict ConfigValidation.ValidationResult.Ok "the premise no longer holds"
            }

            test "names only the store that is still in-process" {
                let message =
                    newApp ()
                    |> RagOptions.withVectorStore (externalVectorStore ())
                    |> replicaVerdict 2
                    |> warningText

                Expect.isFalse (message.Contains "InMemoryVectorStore") "the external vector store is not named"
                Expect.stringContains message "InMemoryBM25Index" "the in-process keyword index still is"
            }

            test "a hand-composed in-process index still warns — the lift keys on the instance, not the builder" {
                let handComposed =
                    new InMemoryBM25Index(InMemoryBlobStorage() :> BlobStorage.IBlobStorage, flushIntervalMs = 60000)

                let message =
                    newApp ()
                    |> RagOptions.withVectorStore (externalVectorStore ())
                    |> RagOptions.withSparseIndex handComposed
                    |> replicaVerdict 2
                    |> warningText

                Expect.stringContains message "InMemoryBM25Index" "still per-process"
            }

            test "dense-only at two replicas warns about the vector store alone" {
                let message =
                    newApp () |> RagOptions.withoutSparseIndex |> replicaVerdict 2 |> warningText

                Expect.stringContains message "InMemoryVectorStore" "the in-process vector store"
                Expect.isFalse (message.Contains "InMemoryBM25Index") "no keyword index is composed"
            }

            // The vector half reads the store's DECLARED locality,
            // never its type name.
            test "a decorator over the HNSW store still warns, naming the composed store" {
                use hnsw = newHnswStore ()

                let message =
                    newApp ()
                    |> RagOptions.withVectorStore (TracingVectorStore(hnsw) :> IVectorStore)
                    |> RagOptions.withSparseIndex (externalIndex ())
                    |> replicaVerdict 2
                    |> warningText

                Expect.stringContains
                    message
                    "the vector store is the in-process"
                    "the wrapped index is still per-process"

                Expect.stringContains message "TracingVectorStore" "named as the type the deployment composed"
            }

            test "a type that only borrows the HNSW name is not classed as in-process" {
                let impostor = Impostor.HnswVectorStore(externalVectorStore ()) :> IVectorStore

                Expect.isFalse (isInProcessVectorStore impostor) "the name is not the locality"

                let verdict =
                    newApp ()
                    |> RagOptions.withVectorStore impostor
                    |> RagOptions.withSparseIndex (externalIndex ())
                    |> replicaVerdict 2

                Expect.equal verdict ConfigValidation.ValidationResult.Ok "no in-process index is composed"
            }

            test "the shipped stores declare their locality" {
                use hnsw = newHnswStore ()

                use inMemory =
                    new InMemoryVectorStore(InMemoryBlobStorage() :> BlobStorage.IBlobStorage, flushIntervalMs = 60000)

                Expect.isTrue (isInProcessVectorStore (hnsw :> IVectorStore)) "HNSW keeps its graph in the process"
                Expect.isTrue (isInProcessVectorStore (inMemory :> IVectorStore)) "so does the in-memory store"

                // The I/O-free constructor: nothing here touches a database.
                use dataSource = Npgsql.NpgsqlDataSource.Create "Host=localhost;Database=unused"

                use pgvector =
                    new ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore.PgvectorVectorStore(
                        dataSource,
                        ToolUp.RAG.VectorStores.Pgvector.PgvectorVectorStore.PgvectorOptions.forDimensions 4,
                        false
                    )

                Expect.equal
                    (VectorIndexLocality.declared (pgvector :> IVectorStore))
                    (Some VectorIndexLocality.Shared)
                    "every replica searches the one pgvector table"

                Expect.equal
                    (VectorIndexLocality.declared (TracingVectorStore(externalVectorStore ()) :> IVectorStore))
                    None
                    "a decorator over an undeclared store passes the absence through"
            }
        ]

        // The keyword half reads the index's DECLARED locality
        // (`ISparseIndexLocality`), never a type test, as 963 made the
        // vector half do.
        testList "InProcessIndexReplicaValidator — the keyword index declares its locality (Phase 964)" [
            test "a decorator over the in-process keyword index still warns" {
                let wrapped =
                    TracingSparseIndex(
                        new InMemoryBM25Index(
                            InMemoryBlobStorage() :> BlobStorage.IBlobStorage,
                            flushIntervalMs = 60000
                        )
                    )
                    :> ISparseIndex

                Expect.isTrue (isInProcessSparseIndex wrapped) "the wrapped postings are still per-process"

                let message =
                    newApp ()
                    |> RagOptions.withVectorStore (externalVectorStore ())
                    |> RagOptions.withSparseIndex wrapped
                    |> replicaVerdict 2
                    |> warningText

                Expect.stringContains message "InMemoryBM25Index" "the keyword half of the warning fires"
            }

            test "the shipped keyword indexes declare their locality" {
                use bm25 =
                    new InMemoryBM25Index(InMemoryBlobStorage() :> BlobStorage.IBlobStorage, flushIntervalMs = 60000)

                Expect.equal
                    (SparseIndexLocality.declared (bm25 :> ISparseIndex))
                    (Some VectorIndexLocality.InProcess)
                    "BM25 keeps its postings in the process"

                // The I/O-free constructor: nothing here touches a database.
                use dataSource = Npgsql.NpgsqlDataSource.Create "Host=localhost;Database=unused"

                use postgresIndex =
                    new ToolUp.SparseIndices.Postgres.PostgresFullTextIndex.PostgresFullTextIndex(
                        dataSource,
                        ToolUp.SparseIndices.Postgres.PostgresFullTextIndex.PostgresFullTextOptions.defaults,
                        "simple",
                        false,
                        None
                    )

                let postgres = postgresIndex :> ISparseIndex

                Expect.equal
                    (SparseIndexLocality.declared postgres)
                    (Some VectorIndexLocality.Shared)
                    "every replica searches the one full-text table"

                Expect.isFalse (isInProcessSparseIndex postgres) "so it never trips the warning"

                Expect.equal
                    (SparseIndexLocality.declared (TracingSparseIndex(externalIndex ()) :> ISparseIndex))
                    None
                    "a decorator over an undeclared index passes the absence through"

                Expect.isFalse (isInProcessSparseIndex (externalIndex ())) "an undeclared index is not in-process"
            }
        ]

        testList "Phase 866 — retrieval scores carry their scale" [
            testAsync "default composition: MinScore 0.5 keeps the strong match and drops the weak one" {
                use sp = provider (lexicalApp ())
                let pipeline = sp.GetRequiredService<IRetrievalPipeline>()

                do! indexAll pipeline [ "strong", strongText, []; "weak", weakText, []; "other", otherText, [] ]

                let defaults = {
                    RetrievalDefaults.defaults with
                        MinScore = Some 0.5
                }

                let! prompt, snippets = promptFor defaults pipeline "what was acme quarterly revenue"

                Expect.contains snippets strongText "the strong match survives a 0.5 threshold"
                Expect.isFalse (List.contains weakText snippets) "the weak match is dropped"
                Expect.stringContains prompt "acme quarterly revenue" "and reaches the prompt"
            }

            testAsync "default composition: fused scores reach the caller normalised onto [0, 1]" {
                use sp = provider (lexicalApp ())
                let pipeline = sp.GetRequiredService<IRetrievalPipeline>()

                do! indexAll pipeline [ "strong", strongText, []; "weak", weakText, []; "other", otherText, [] ]

                let! matches =
                    pipeline.Retrieve
                        (RetrievalRequest.create "what was acme quarterly revenue" [ teamScope ] 5 Interleaved)
                        teamAccess

                let scores = matches |> List.map _.Score
                Expect.equal (List.head matches).ChunkId "strong" "the strong match leads"
                Expect.floatClose Accuracy.veryHigh (List.head scores) 1.0 "the pool's best scores 1.0"
                Expect.floatClose Accuracy.veryHigh (List.last scores) 0.0 "and its weakest 0.0"
                Expect.all scores (fun s -> s >= 0.0 && s <= 1.0) "every score is in the unit interval"
            }

            testAsync "the trace states the normalisation and the space it returned" {
                let tracer = CapturingTracer()
                let dense = [ rankedMatch "a" []; rankedMatch "b" [] ]
                let pipeline = fixedRankPipeline dense (Some(List.rev dense)) noBoosts tracer
                let! _ = retrieveIds pipeline

                match tracer.Last with
                | None -> failtest "a trace is emitted"
                | Some trace ->
                    Expect.contains trace.Stages "Normalise:MinMax" "the normalisation is stated"
                    Expect.equal (List.last trace.Stages) "ScoreSpace:Fused" "and the returned space"
            }

            testAsync "dense-only: cosine scores are left as scored, and the trace says Cosine" {
                let tracer = CapturingTracer()

                let dense = [
                    { rankedMatch "a" [] with Score = 0.9 }
                    { rankedMatch "b" [] with Score = 0.4 }
                ]

                let pipeline = fixedRankPipeline dense None noBoosts tracer
                let! matches = retrieveIds pipeline

                Expect.equal (matches |> List.map _.Score) [ 0.9; 0.4 ] "no normalisation on the cosine path"

                match tracer.Last with
                | None -> failtest "a trace is emitted"
                | Some trace ->
                    Expect.isFalse (List.contains "Normalise:MinMax" trace.Stages) "nothing to normalise"
                    Expect.equal (List.last trace.Stages) "ScoreSpace:Cosine" "the returned space"
            }

            testAsync "no threshold, no boosts: the fused ORDER is unchanged, and the scores are min-max RRF" {
                // Ten candidates in two partially overlapping rankings.
                let ids = [ for i in 1..10 -> sprintf "c%02d" i ]
                let dense = ids |> List.map (fun id -> rankedMatch id [])

                let sparse =
                    [ "c07"; "c02"; "c10"; "c01"; "c05"; "c09" ]
                    |> List.map (fun id -> rankedMatch id [])

                let rrf =
                    let contribution (ranked: VectorMatch list) (id: string) =
                        match ranked |> List.tryFindIndex (fun m -> m.ChunkId = id) with
                        | Some i -> 1.0 / (60.0 + float (i + 1))
                        | None -> 0.0

                    ids |> List.map (fun id -> id, contribution dense id + contribution sparse id)

                let expectedOrder = rrf |> List.sortBy (fun (id, s) -> -s, id) |> List.map fst
                let lo = rrf |> List.map snd |> List.min
                let hi = rrf |> List.map snd |> List.max

                let! matches = retrieveIds (fixedRankPipeline dense (Some sparse) noBoosts (CapturingTracer()))

                Expect.equal (matches |> List.map _.ChunkId) expectedOrder "rank fusion order, unchanged"

                for m in matches do
                    let raw = rrf |> List.find (fun (id, _) -> id = m.ChunkId) |> snd
                    Expect.floatClose Accuracy.veryHigh m.Score ((raw - lo) / (hi - lo)) m.ChunkId
            }

            testAsync "a summary outranks an equal-relevance chunk" {
                // `a` and `s` swap ranks across the two lists: equal fused relevance.
                let a = rankedMatch "a" []
                let s = rankedMatch "s" summaryMeta
                let x = rankedMatch "x" []
                let options = ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults

                let! unboosted =
                    retrieveIds (fixedRankPipeline [ a; s; x ] (Some [ s; a; x ]) noBoosts (CapturingTracer()))

                let! boosted =
                    retrieveIds (fixedRankPipeline [ a; s; x ] (Some [ s; a; x ]) options (CapturingTracer()))

                Expect.equal (unboosted |> List.map _.ChunkId) [ "a"; "s"; "x" ] "a tie, broken by id"
                Expect.equal (boosted |> List.map _.ChunkId) [ "s"; "a"; "x" ] "the summary boost breaks the tie"
            }

            testAsync "a summary does not outrank a far better chunk" {
                // `a` leads both lists; the summary trails both. Raw fused
                // scores top out at 2/61, so before Phase 866 the +0.10
                // summary boost alone lifted `s` over everything.
                let a = rankedMatch "a" []
                let fillers = [ for i in 1..8 -> rankedMatch (sprintf "f%d" i) [] ]
                let s = rankedMatch "s" summaryMeta
                let ranked = a :: fillers @ [ s ]
                let options = ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults
                let! matches = retrieveIds (fixedRankPipeline ranked (Some ranked) options (CapturingTracer()))
                let ids = matches |> List.map _.ChunkId

                Expect.equal (List.head ids) "a" "the far better chunk keeps the lead"

                let summaryRank = ids |> List.findIndex ((=) "s")
                let summaryScore = (matches |> List.find (fun m -> m.ChunkId = "s")).Score

                Expect.isGreaterThan summaryRank 1 "the summary is nudged, not promoted to a tier"

                Expect.floatClose
                    Accuracy.veryHigh
                    summaryScore
                    ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults.SummaryBoost
                    "the weakest candidate (0.0) plus exactly the boost"
            }

            test "the validator is silent for the default composition with a reachable threshold" {
                Expect.equal
                    (lexicalApp () |> RagOptions.withMinScore (Some 0.5) |> scoreSpaceVerdict)
                    ConfigValidation.ValidationResult.Ok
                    "fused scores reach 1.0; 0.5 can be met"
            }

            test "the validator is silent with no threshold, whatever is composed" {
                Expect.equal
                    (lexicalApp () |> RagOptions.withReranker stubReranker |> scoreSpaceVerdict)
                    ConfigValidation.ValidationResult.Ok
                    "no gate, nothing to reach"
            }

            test "the validator warns when a reranker owns the scale the threshold reads" {
                let message =
                    lexicalApp ()
                    |> RagOptions.withReranker stubReranker
                    |> RagOptions.withMinScore (Some 0.5)
                    |> scoreSpaceVerdict
                    |> warningText

                Expect.stringContains message "Reranked" "names the space"
                Expect.stringContains message "withReranker" "and the setter that put it there"
            }

            test "the validator warns when a supplied pipeline owns retrieval" {
                let message =
                    lexicalApp ()
                    |> RagOptions.withRetrievalPipeline (externalPipeline ())
                    |> RagOptions.withMinScore (Some 0.5)
                    |> scoreSpaceVerdict
                    |> warningText

                Expect.stringContains message "withRetrievalPipeline" "names the supplied pipeline"
            }

            test "a threshold at the top of a bounded space cannot be met" {
                let finding =
                    ToolUp.RAG.RetrievalPipeline.RetrievalScoreSpace.thresholdFinding
                        (Some ToolUp.RAG.RetrievalPipeline.RetrievalScoreSpace.Fused)
                        (Some 1.0)

                match finding with
                | Some message -> Expect.stringContains message "cannot be met" "the gate keeps only scores above it"
                | None -> failtest "a 1.0 threshold drops every unboosted fused match"

                Expect.isNone
                    (ToolUp.RAG.RetrievalPipeline.RetrievalScoreSpace.thresholdFinding
                        (Some ToolUp.RAG.RetrievalPipeline.RetrievalScoreSpace.Cosine)
                        (Some 0.99))
                    "0.99 is reachable in cosine"
            }
        ]

        testList "Phase 1083 — one threshold rule, admits, in every space" [
            test "no threshold admits every score, in every space" {
                for space in
                    [
                        RetrievalScoreSpace.Cosine
                        RetrievalScoreSpace.Fused
                        RetrievalScoreSpace.Reranked
                    ] do
                    Expect.isTrue (RetrievalScoreSpace.admits space None -5.0) (sprintf "%A" space)
            }

            test "cosine: a similarity floor admits only scores strictly above it" {
                Expect.isTrue (RetrievalScoreSpace.admits RetrievalScoreSpace.Cosine (Some 0.5) 0.51) "above"
                Expect.isFalse (RetrievalScoreSpace.admits RetrievalScoreSpace.Cosine (Some 0.5) 0.5) "at"
                Expect.isFalse (RetrievalScoreSpace.admits RetrievalScoreSpace.Cosine (Some 0.5) 0.2) "below"
            }

            test "fused: the pool's best candidate (1.0) clears any threshold the validator accepts" {
                Expect.isTrue (RetrievalScoreSpace.admits RetrievalScoreSpace.Fused (Some 0.99) 1.0) "the best match"
                Expect.isFalse (RetrievalScoreSpace.admits RetrievalScoreSpace.Fused (Some 0.3) 0.0) "the weakest"
                Expect.isFalse (RetrievalScoreSpace.admits RetrievalScoreSpace.Fused (Some 1.0) 1.0) "a 1.0 threshold"
            }

            test "reranked: the threshold is read on the reranker's own scale" {
                Expect.isTrue (RetrievalScoreSpace.admits RetrievalScoreSpace.Reranked (Some 2.0) 3.0) "above"
                Expect.isFalse (RetrievalScoreSpace.admits RetrievalScoreSpace.Reranked (Some 2.0) 2.0) "at"
            }

            test "the validator reads the same rule: a threshold the space's bound does not clear is a finding" {
                Expect.isSome
                    (ToolUp.RAG.RetrievalPipeline.RetrievalScoreSpace.thresholdFinding
                        (Some RetrievalScoreSpace.Cosine)
                        (Some 1.0))
                    "1.0 is not admitted at cosine's bound"

                Expect.isNone
                    (ToolUp.RAG.RetrievalPipeline.RetrievalScoreSpace.thresholdFinding
                        (Some RetrievalScoreSpace.Fused)
                        (Some 0.5))
                    "0.5 is admitted at the fused bound"
            }
        ]

        testList "Phase 978 — the retrieval pipeline orders once" [
            testAsync "a pool larger than the reranker's batch returns no candidate the reranker did not score" {
                // Six candidates, a batch of two. The reranker's scale sits far
                // below the fused [0, 1] one, so a tail left on the fused scale
                // outranks the reranked head once the Interleaved merge sorts.
                let tracer = CapturingTracer()
                let ranked = [ for i in 1..6 -> rankedMatch (sprintf "c%d" i) [] ]
                let reranker = TableReranker(2, Map.ofList [ "c1", 0.002; "c2", 0.001 ])

                let options = {
                    noBoosts with
                        Reranker = Some(reranker :> ToolUp.Platform.Infra.IReranker.IReranker)
                }

                let! matches = retrieveIds (fixedRankPipeline ranked (Some ranked) options tracer)
                let ids = matches |> List.map _.ChunkId

                Expect.all ids (fun id -> List.contains id reranker.Seen) "every returned chunk was reranked"
                Expect.equal ids [ "c1"; "c2" ] "the reranked head, in the reranker's order"

                match tracer.Last with
                | None -> failtest "a trace is emitted"
                | Some trace -> Expect.contains trace.Stages "Rerank:2/6" "the trace states the cap"
            }

            testAsync "a boost survives a composed reranker: it breaks the reranker's tie" {
                let a = rankedMatch "a" []
                let s = rankedMatch "s" summaryMeta
                let x = rankedMatch "x" []
                let ranked = [ a; s; x ]
                let scores = Map.ofList [ "a", 0.9; "s", 0.9; "x", 0.1 ]

                let rerankedWith (options: ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions) =
                    let options = {
                        options with
                            Reranker = Some(TableReranker(32, scores) :> ToolUp.Platform.Infra.IReranker.IReranker)
                    }

                    retrieveIds (fixedRankPipeline ranked (Some ranked) options (CapturingTracer()))

                let! unboosted = rerankedWith noBoosts
                let! boosted = rerankedWith ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults

                Expect.equal (unboosted |> List.map _.ChunkId) [ "a"; "s"; "x" ] "a reranker tie, broken by id"
                Expect.equal (boosted |> List.map _.ChunkId) [ "s"; "a"; "x" ] "the summary boost breaks it"
            }

            testAsync "on the reranker's scale a boost is a nudge of the pool's range, never a tier" {
                // Logit-like scores spanning 20. The summary scores lowest; a
                // 0.10 boost is 10 % of the pool's range — 2.0 here — exactly as
                // it is 10 % of the normalised fused range.
                let a = rankedMatch "a" []
                let fillers = [ for i in 1..8 -> rankedMatch (sprintf "f%d" i) [] ]
                let s = rankedMatch "s" summaryMeta
                let ranked = a :: fillers @ [ s ]

                let scores =
                    Map.ofList (
                        ("a", 10.0)
                        :: ("s", -10.0)
                        :: [ for i in 1..8 -> sprintf "f%d" i, 10.0 - float i ]
                    )

                let options = {
                    ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults with
                        Reranker = Some(TableReranker(32, scores) :> ToolUp.Platform.Infra.IReranker.IReranker)
                }

                let! matches = retrieveIds (fixedRankPipeline ranked (Some ranked) options (CapturingTracer()))
                let ids = matches |> List.map _.ChunkId
                let summaryScore = (matches |> List.find (fun m -> m.ChunkId = "s")).Score

                Expect.equal (List.head ids) "a" "the reranker's best keeps the lead"
                Expect.equal (List.last ids) "s" "the summary is nudged, not promoted over far better chunks"

                Expect.floatClose
                    Accuracy.veryHigh
                    summaryScore
                    (-10.0
                     + ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults.SummaryBoost
                       * 20.0)
                    "the reranker's score plus the boost, in units of the pool's range"
            }

            testAsync "MMR's diversity order is the order the caller receives" {
                // `a` and `d` are near-duplicates and lead the fused ranking;
                // `b` is distinct and trails. MMR (λ 0.5) picks `a`, then the
                // distinct `b` over the duplicate `d`. The Interleaved merge
                // used to re-sort by score afterwards and put `d` back second.
                let tracer = CapturingTracer()

                let ranked = [
                    rankedMatch "a" [] |> withContent "alpha beta gamma"
                    rankedMatch "d" [] |> withContent "alpha beta gamma"
                    rankedMatch "b" [] |> withContent "delta epsilon zeta"
                ]

                let options = {
                    noBoosts with
                        EnableMmr = true
                        MmrLambda = 0.5
                }

                let! matches = retrieveIds (fixedRankPipeline ranked (Some ranked) options tracer)

                Expect.equal (matches |> List.map _.ChunkId) [ "a"; "b"; "d" ] "MMR's order survives the merge"

                match tracer.Last with
                | None -> failtest "a trace is emitted"
                | Some trace -> Expect.contains trace.Stages "FinalOrder:MMR" "the trace names the final ordering step"
            }

            testAsync "without MMR the trace names the Interleaved merge as the final ordering step" {
                let tracer = CapturingTracer()
                let dense = [ rankedMatch "a" []; rankedMatch "b" [] ]
                let! _ = retrieveIds (fixedRankPipeline dense (Some(List.rev dense)) noBoosts tracer)

                match tracer.Last with
                | None -> failtest "a trace is emitted"
                | Some trace ->
                    Expect.contains trace.Stages "FinalOrder:Interleaved" "the merge orders last"
                    Expect.equal (List.last trace.Stages) "ScoreSpace:Fused" "the space mark stays last"
            }
        ]
        testList "Phase 1086 — the stage marks, Separate and adaptive-K over relevance" [
            testAsync "every composition's mark sequence matches the fixture" {
                let expected = markFixture ()
                let actual = ResizeArray<string * string>()

                for name, run in markScenarios do
                    let! stages = run
                    actual.Add((name, String.Join(" ", stages)))

                let rendered =
                    actual
                    |> Seq.map (fun (name, marks) -> name + ": " + marks)
                    |> String.concat
                        "
"

                Expect.equal
                    (Map.ofSeq actual)
                    expected
                    (sprintf
                        "every composition's marks, byte for byte; the tree emits:
%s"
                        rendered)
            }

            test "RetrievalStage.mark renders exactly the fixture's vocabulary, and the reference lists it" {
                let vocabulary = markVocabulary ()

                let patterns =
                    vocabulary |> List.map (fun template -> template, templatePattern template)

                let rendered =
                    everyStage |> List.map ToolUp.RAG.RetrievalPipeline.RetrievalStage.mark

                for mark in
                    rendered
                    @ (markFixture ()
                       |> Map.toList
                       |> List.collect (fun (_, line) -> List.ofArray (line.Split ' '))) do
                    Expect.equal
                        (patterns
                         |> List.filter (fun (_, pattern) -> pattern.IsMatch mark)
                         |> List.length)
                        1
                        (sprintf "'%s' is exactly one template" mark)

                for template, pattern in patterns do
                    Expect.isTrue
                        (List.exists (fun (mark: string) -> pattern.IsMatch mark) rendered)
                        (sprintf "a case renders '%s'" template)

                let caseOf (stage: ToolUp.RAG.RetrievalPipeline.RetrievalStage) =
                    let case, _ =
                        Reflection.FSharpValue.GetUnionFields(
                            stage,
                            typeof<ToolUp.RAG.RetrievalPipeline.RetrievalStage>
                        )

                    case.Name

                Expect.equal
                    (everyStage |> List.map caseOf |> Set.ofList)
                    (Reflection.FSharpType.GetUnionCases typeof<ToolUp.RAG.RetrievalPipeline.RetrievalStage>
                     |> Array.map _.Name
                     |> Set.ofArray)
                    "every case is enumerated"

                let reference = IO.File.ReadAllText apiReferencePath

                for template in vocabulary do
                    Expect.stringContains reference ("`" + template + "`") (sprintf "the reference lists %s" template)
            }

            testAsync "Separate over two scopes returns the scopes grouped in the order the request listed them" {
                let userScope = User "u1"

                let dense = [
                    { rankedMatch "a" [] with Score = 0.9 }
                    {
                        rankedMatch "b" [] with
                            Score = 0.8
                            Scope = userScope
                    }
                    { rankedMatch "c" [] with Score = 0.7 }
                    {
                        rankedMatch "d" [] with
                            Score = 0.6
                            Scope = userScope
                    }
                ]

                let pipeline = fixedRankPipeline dense None noBoosts (CapturingTracer())

                let! matches =
                    pipeline.Retrieve (RetrievalRequest.create "query" [ userScope; teamScope ] 3 Separate) teamAccess

                Expect.equal
                    (matches |> List.map _.ChunkId)
                    [ "b"; "a"; "c" ]
                    "the TopK best, the user scope's group first (it was listed first), each group in rank order"
            }

            testAsync "with MMR on, adaptive-K cuts on the relevance order's gaps, not MMR's diversity order" {
                // Relevance: a 0.9, d 0.85, c 0.8, b 0.7. No gap reaches the
                // 0.15 floor, so the hint keeps MaxK. MMR (lambda 0.5) puts the
                // distinct `b` second, and in that order a to b is a 0.2 gap.
                let dense = [
                    { rankedMatch "a" [] with Score = 0.9 } |> withContent "alpha beta gamma"
                    { rankedMatch "d" [] with Score = 0.85 } |> withContent "alpha beta gamma"
                    { rankedMatch "c" [] with Score = 0.8 } |> withContent "alpha beta gamma delta"
                    { rankedMatch "b" [] with Score = 0.7 } |> withContent "epsilon zeta eta"
                ]

                let options = {
                    noBoosts with
                        EnableMmr = true
                        MmrLambda = 0.5
                }

                let request = {
                    RetrievalRequest.create "query" [ teamScope ] 4 Interleaved with
                        AdaptiveK =
                            Some {
                                MinK = 1
                                MaxK = 4
                                ScoreFloor = 0.15
                            }
                }

                let pipeline = fixedRankPipeline dense None options (CapturingTracer())
                let! matches = pipeline.Retrieve request teamAccess

                Expect.equal
                    (matches |> List.map _.ChunkId)
                    [ "a"; "b"; "c"; "d" ]
                    "no relevance cliff, so all four, in MMR's order (the duplicate of a last)"
            }
        ]
    ]