module ToolUp.Platform.Tests.InProcess.SparseIndexCompositionTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.ISparseIndex
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.RAG.InMemoryBM25Index
open ToolUp.RAG.InMemoryVectorStore
open ToolUp.RAG.RAGCompose

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

/// Phase 963 — a planted pass-through decorator (telemetry, caching or a
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

/// Phase 964 — a planted pass-through decorator over a keyword index, the
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

/// Phase 963 — a type that only BORROWS the HNSW companion's name. It holds
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
    RAGServerApp.create stubFactory stubProfile constantEmbedder
    |> RAGServerApp.withStorage (InMemoryBlobStorage() :> BlobStorage.IBlobStorage)

let private withReplicas (count: int) (app: RAGServerApp) : RAGServerApp = {
    app with
        AI.Base.Config.ReplicaCount = count
}

let private provider (app: RAGServerApp) : ServiceProvider =
    let composed = composeRAG app
    let services = ServiceCollection() :> IServiceCollection

    let services =
        match composed.Extensions.ServiceConfig with
        | Some f -> f services
        | None -> services

    services.BuildServiceProvider()

let private resolvedSparse (app: RAGServerApp) : ISparseIndex option =
    use sp = provider app

    match sp.GetService typeof<ISparseIndex> with
    | null -> None
    | o -> Some(o :?> ISparseIndex)

/// Run the replica validator exactly as `composeRAG` registered it.
let private replicaVerdict (app: RAGServerApp) : ConfigValidation.ValidationResult =
    let composed = composeRAG app

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
    RAGServerApp.create stubFactory stubProfile lexicalEmbedder
    |> RAGServerApp.withStorage (InMemoryBlobStorage() :> BlobStorage.IBlobStorage)

let private teamScope = Team "t1"

let private teamAccess = AccessContext.unrestricted (TeamMember("u1", "t1"))

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
    }

    let! prompt = ToolUp.RAG.RAGPromptBuilder.withRetrieval defaults None None pipeline ctx
    return prompt, sources.Value |> List.map _.Snippet
}

let private strongText = "acme quarterly revenue rose sharply in the third quarter"
let private weakText = "the office kitchen rota for next week"
let private otherText = "parking permits renew every january"

type private CapturingTracer() =
    let traces = ResizeArray<ToolUp.Platform.IRetrievalTracer.RetrievalTrace>()
    member _.Last = traces |> Seq.tryLast

    interface ToolUp.Platform.IRetrievalTracer.IRetrievalTracer with
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
let private scoreSpaceVerdict (app: RAGServerApp) : ConfigValidation.ValidationResult =
    let composed = composeRAG app

    let v =
        composed.ConfigValidators |> List.find (fun v -> v.Name = "rag-min-score-space")

    v.Validate() |> Async.RunSynchronously

let private stubReranker =
    { new ToolUp.Platform.IReranker.IReranker with
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
                let app = newApp () |> RAGServerApp.withSparseIndex supplied

                match resolvedSparse app with
                | Some index -> Expect.isTrue (obj.ReferenceEquals(index, supplied)) "the composed instance"
                | None -> failtest "the supplied index must be registered"
            }

            test "withSparseIndex beside withSparseAnalyzer is refused at composition, naming the analyzer" {
                let analyzer =
                    ToolUp.RAG.SparseAnalysis.create "custom-analyzer-7" ToolUp.RAG.SparseAnalysis.tokeniseWords

                let app =
                    newApp ()
                    |> RAGServerApp.withSparseAnalyzer analyzer
                    |> RAGServerApp.withSparseIndex (externalIndex ())

                let ex = Expect.throwsC (fun () -> composeRAG app |> ignore) id
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
                    |> RAGServerApp.withSparseAnalyzer analyzer
                    |> RAGServerApp.withAnalyzedSparseIndex (fun a ->
                        seen <- seen @ [ a.Id ]
                        supplied)

                composeRAG app |> ignore
                Expect.equal seen [ "custom-analyzer-9" ] "built once, at composition, for the composed analyzer"

                match resolvedSparse app with
                | Some index -> Expect.isTrue (obj.ReferenceEquals(index, supplied)) "the built index is registered"
                | None -> failtest "the built index must be registered"
            }

            test "withAnalyzedSparseIndex with no analyzer composed receives the identity analyzer" {
                let mutable seen = ""

                let app =
                    newApp ()
                    |> RAGServerApp.withAnalyzedSparseIndex (fun a ->
                        seen <- a.Id
                        externalIndex ())

                composeRAG app |> ignore
                Expect.equal seen ToolUp.RAG.SparseAnalysis.IdentityAnalyzerId "the identity analyzer"
            }

            test "a refusal inside the builder surfaces at composition" {
                let app =
                    newApp ()
                    |> RAGServerApp.withAnalyzedSparseIndex (fun a ->
                        invalidOp (sprintf "no configuration for analyzer '%s'" a.Id))

                let ex = Expect.throwsC (fun () -> composeRAG app |> ignore) id
                Expect.stringContains ex.Message "identity" "the builder's refusal, naming the analyzer"
            }

            test "withoutSparseIndex registers no keyword index" {
                let app = newApp () |> RAGServerApp.withoutSparseIndex
                Expect.isNone (resolvedSparse app) "dense only: nothing registered"
            }

            testAsync "withoutSparseIndex retrieves the dense list, unfused" {
                let app = newApp () |> RAGServerApp.withoutSparseIndex
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
                        (AccessContext.unrestricted (TeamMember("u1", "t1")))

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
                let message = newApp () |> withReplicas 2 |> replicaVerdict |> warningText
                Expect.stringContains message "InMemoryVectorStore" "names the vector store"
                Expect.stringContains message "ToolUp.VectorStores.Pgvector" "and the companion replacing it"
                Expect.stringContains message "InMemoryBM25Index" "names the keyword index"
                Expect.stringContains message "ToolUp.SparseIndices.Postgres" "and the companion replacing it"
            }

            test "is silent at one replica" {
                Expect.equal
                    (newApp () |> withReplicas 1 |> replicaVerdict)
                    ConfigValidation.ValidationResult.Ok
                    "one replica has nothing to diverge"
            }

            test "is lifted when both composed stores span replicas" {
                let verdict =
                    newApp ()
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> RAGServerApp.withSparseIndex (externalIndex ())
                    |> withReplicas 3
                    |> replicaVerdict

                Expect.equal verdict ConfigValidation.ValidationResult.Ok "the premise no longer holds"
            }

            test "names only the store that is still in-process" {
                let message =
                    newApp ()
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> withReplicas 2
                    |> replicaVerdict
                    |> warningText

                Expect.isFalse (message.Contains "InMemoryVectorStore") "the external vector store is not named"
                Expect.stringContains message "InMemoryBM25Index" "the in-process keyword index still is"
            }

            test "a hand-composed in-process index still warns — the lift keys on the instance, not the builder" {
                let handComposed =
                    new InMemoryBM25Index(InMemoryBlobStorage() :> BlobStorage.IBlobStorage, flushIntervalMs = 60000)

                let message =
                    newApp ()
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> RAGServerApp.withSparseIndex handComposed
                    |> withReplicas 2
                    |> replicaVerdict
                    |> warningText

                Expect.stringContains message "InMemoryBM25Index" "still per-process"
            }

            test "dense-only at two replicas warns about the vector store alone" {
                let message =
                    newApp ()
                    |> RAGServerApp.withoutSparseIndex
                    |> withReplicas 2
                    |> replicaVerdict
                    |> warningText

                Expect.stringContains message "InMemoryVectorStore" "the in-process vector store"
                Expect.isFalse (message.Contains "InMemoryBM25Index") "no keyword index is composed"
            }

            // Phase 963 — the vector half reads the store's DECLARED locality,
            // never its type name.
            test "a decorator over the HNSW store still warns, naming the composed store" {
                use hnsw = newHnswStore ()

                let message =
                    newApp ()
                    |> RAGServerApp.withVectorStore (TracingVectorStore(hnsw) :> IVectorStore)
                    |> RAGServerApp.withSparseIndex (externalIndex ())
                    |> withReplicas 2
                    |> replicaVerdict
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
                    |> RAGServerApp.withVectorStore impostor
                    |> RAGServerApp.withSparseIndex (externalIndex ())
                    |> withReplicas 2
                    |> replicaVerdict

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

        // Phase 964 — the keyword half reads the index's DECLARED locality
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
                    |> RAGServerApp.withVectorStore (externalVectorStore ())
                    |> RAGServerApp.withSparseIndex wrapped
                    |> withReplicas 2
                    |> replicaVerdict
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
                    (lexicalApp () |> RAGServerApp.withMinScore (Some 0.5) |> scoreSpaceVerdict)
                    ConfigValidation.ValidationResult.Ok
                    "fused scores reach 1.0; 0.5 can be met"
            }

            test "the validator is silent with no threshold, whatever is composed" {
                Expect.equal
                    (lexicalApp () |> RAGServerApp.withReranker stubReranker |> scoreSpaceVerdict)
                    ConfigValidation.ValidationResult.Ok
                    "no gate, nothing to reach"
            }

            test "the validator warns when a reranker owns the scale the threshold reads" {
                let message =
                    lexicalApp ()
                    |> RAGServerApp.withReranker stubReranker
                    |> RAGServerApp.withMinScore (Some 0.5)
                    |> scoreSpaceVerdict
                    |> warningText

                Expect.stringContains message "Reranked" "names the space"
                Expect.stringContains message "withReranker" "and the setter that put it there"
            }

            test "the validator warns when a supplied pipeline owns retrieval" {
                let message =
                    lexicalApp ()
                    |> RAGServerApp.withRetrievalPipeline (externalPipeline ())
                    |> RAGServerApp.withMinScore (Some 0.5)
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
    ]