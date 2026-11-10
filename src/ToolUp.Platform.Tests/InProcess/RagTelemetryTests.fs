module ToolUp.Platform.Tests.InProcess.RagTelemetryTests

// Two pins over the retrieval read side.
//
// 1. What the `/health/rag` classification can see. Under the default
//    composition the pipeline fuses the dense and keyword rankings and
//    min-max normalises the pool, so the best returned candidate scores
//    exactly 1.0 whether the corpus answers the query or not. The pool
//    therefore carries an absolute signal beside the fused order, the
//    best returned candidate's dense similarity, and the classification
//    reads a fused pool's miss on it.
//
// 2. One team's RAG audit rows share one stream id: the retrieval trace,
//    the retrieval miss, the over-length-query refusal and the vacuum
//    sweep all land under the team's bare id.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.IEmbeddingProvider
open ToolUp.Platform.Infra.IRetrievalPipeline
open ToolUp.Platform.Infra.IRetrievalTracer
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.Platform.Infra
open ToolUp.Platform.Jobs
open ToolUp.Platform.Compose

let private noopLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// Each word hashes to one of 64 dimensions, L2-normalised, so cosine
/// similarity tracks word overlap.
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

let private team = Team "acme"
let private teamAccess = AccessContexts.forSubject (TeamMember("u1", "acme"))

/// The default composition's pipeline: dense store plus keyword index.
let private fusedPipeline (corpus: string list) = async {
    let storage = InMemoryBlobStorage() :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(storage, logger = noopLogger, flushIntervalMs = 60000)

    let index =
        new ToolUp.RAG.InMemoryBM25Index.InMemoryBM25Index(storage, flushIntervalMs = 60000)

    let pipeline =
        new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store, lexicalEmbedder, sparseIndex = index)
        :> IRetrievalPipeline

    for i, content in List.indexed corpus do
        do!
            pipeline.Index
                (sprintf "c%d" i)
                {
                    Content = content
                    Metadata = Map.empty
                }
                team

    return pipeline
}

let private topScore (corpus: string list) (query: string) = async {
    let! pipeline = fusedPipeline corpus
    let! matches = pipeline.Retrieve (RetrievalRequest.create query [ team ] 5 Interleaved) teamAccess

    return
        match matches with
        | top :: _ -> top.Score
        | [] -> failtest "the corpus returned nothing"
}

let private scoreSpaceTests =
    testList "what the telemetry classification can see" [
        testCaseAsync "under the fused composition the returned top score is 1.0 on a hit and on a miss alike"
        <| async {
            let query = "acme quarterly revenue"

            let! hit =
                topScore
                    [
                        "acme quarterly revenue rose sharply in the third quarter"
                        "the office kitchen rota for next week"
                    ]
                    query

            let! miss =
                topScore
                    [
                        "the office kitchen rota for next week"
                        "parking permits renew every january"
                    ]
                    query

            Expect.equal hit 1.0 "a hit corpus tops out at 1.0"

            Expect.equal
                miss
                1.0
                "a miss corpus tops out at 1.0 too, so a classification read off the returned pool cannot tell them apart"
        }
    ]

let private auditScopeTests =
    testList "one team's RAG audit rows share one stream id" [
        testCaseAsync "trace, miss, query refusal and vacuum land under the team's bare id"
        <| async {
            let eventStore =
                ToolUp.Platform.Infra.InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let tracer =
                ToolUp.RAG.RetrievalTracers.EventStoreRetrievalTracer(eventStore, noopLogger) :> IRetrievalTracer

            let miss: RetrievalMiss = {
                QueryHash = "h"
                QueryLength = 1
                Scopes = [ team ]
                MatchesAboveMinScore = 0
                MinScoreThreshold = 0.0
                TopScore = 0.0
            }

            do! tracer.Miss miss teamAccess

            // An over-length query is refused with an audit row.
            let storage = InMemoryBlobStorage() :> IBlobStorage

            use store =
                new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
                    storage,
                    logger = noopLogger,
                    flushIntervalMs = 60000
                )

            let pipeline =
                new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
                    store,
                    lexicalEmbedder,
                    maxQueryChars = 8,
                    eventStore = eventStore
                )
                :> IRetrievalPipeline

            let! refused =
                pipeline.Retrieve (RetrievalRequest.create "a query far over the cap" [ team ] 5 Interleaved) teamAccess
                |> Async.Catch

            match refused with
            | Choice2Of2 _ -> ()
            | Choice1Of2 _ -> failtest "the over-length query must be refused"

            // A vacuum sweep over the team's scope.
            let vs = store :> IVectorStore
            do! vs.Upsert team "doc:chunk:0" (Array.create 64 0.1f) { Content = "x"; Metadata = Map.empty }
            do! vs.DeleteChunk team "doc:chunk:0"

            let handler =
                ToolUp.RAG.RAGVacuumJobHandler.create {
                    VectorStore = vs
                    EventStore = eventStore
                    Retention = TimeSpan.Zero
                    Logger = noopLogger
                }

            let! _ = handler.Execute(Unchecked.defaultof<JobContext>)

            let! rows = eventStore.ReadAll "acme"
            let types = rows |> List.map _.EventType |> Set.ofList

            for expected in
                [
                    "KnowledgeRetrievalMiss"
                    "KnowledgeQueryRejected"
                    "KnowledgeVacuumCompleted"
                ] do
                Expect.contains types expected (sprintf "%s lands in the team's stream" expected)

            let! prefixed = eventStore.ReadAll "team-acme"
            Expect.isEmpty prefixed "no row lands under the container spelling"
        }

        test "the four spellings are one" {
            let spell = VectorScope.auditScopeId
            Expect.equal (spell team) "acme" "a team row is keyed by the persisted stream key, the bare id"
            Expect.equal (spell Platform) "_platform" "the platform scope"
            Expect.equal (spell Deployment) "_deployment" "the deployment scope, the deployment container's name"
            Expect.equal (spell (VectorScope.User "u1")) "user:u1" "a user scope"
        }
    ]

/// A reranker that scores every candidate by how many query words it holds.
let private overlapReranker =
    { new ToolUp.Platform.Infra.IReranker.IReranker with
        member _.Name = "overlap"
        member _.MaxBatchSize = 100

        member _.Rerank query candidates = async {
            let words = Set.ofArray (query.ToLowerInvariant().Split ' ')

            return
                candidates
                |> List.map (fun m ->
                    let held =
                        m.Content.ToLowerInvariant().Split ' '
                        |> Array.filter words.Contains
                        |> Array.length

                    { m with Score = float held })
                |> List.sortByDescending _.Score
        }
    }

/// A pipeline over `corpus` in one of the three compositions.
let private composed (keywordIndex: bool) (reranker: bool) (corpus: string list) = async {
    let storage = InMemoryBlobStorage() :> IBlobStorage

    let store =
        new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(storage, logger = noopLogger, flushIntervalMs = 60000)

    let options = {
        ToolUp.RAG.RetrievalPipeline.RetrievalPipelineOptions.defaults with
            Reranker = if reranker then Some overlapReranker else None
    }

    let pipeline =
        if keywordIndex then
            let index =
                new ToolUp.RAG.InMemoryBM25Index.InMemoryBM25Index(storage, flushIntervalMs = 60000)

            new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(
                store,
                lexicalEmbedder,
                sparseIndex = index,
                options = options
            )
            :> IRetrievalPipeline
        else
            new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(store, lexicalEmbedder, options = options)
            :> IRetrievalPipeline

    for i, content in List.indexed corpus do
        do!
            pipeline.Index
                (sprintf "c%d" i)
                {
                    Content = content
                    Metadata = Map.empty
                }
                team

    return pipeline
}

let private hitCorpus = [
    "acme quarterly revenue rose sharply in the third quarter"
    "the office kitchen rota for next week"
]

let private missCorpus = [
    "the office kitchen rota for next week"
    "parking permits renew every january"
]

let private query = "acme quarterly revenue"

let private request () =
    RetrievalRequest.create query [ team ] 5 Interleaved

/// Retrieve through the scored sibling and classify the pool in its space.
let private classify (pipeline: IRetrievalPipeline) (minScore: float option) = async {
    let! pool = ScoredPool.retrieve RetrievalScoreSpace.Fused pipeline (request ()) teamAccess
    let telemetry = ToolUp.RAG.RagTelemetry.RollingRagTelemetry()
    (telemetry :> ToolUp.RAG.RagTelemetry.IScoredRagTelemetry).RecordScoredRetrieval(pool, minScore)
    let! snapshot = (telemetry :> ToolUp.Platform.Infra.IRagTelemetry.IRagTelemetry).Snapshot()
    return pool, snapshot
}

let private scoredPoolTests =
    testList "Phase 1083 — the pool carries its space and the telemetry classifies by admits" [
        testCaseAsync "each composition returns the space its scores are in, and Retrieve returns the pool's matches"
        <| async {
            for keywordIndex, reranker, expected in
                [
                    false, false, RetrievalScoreSpace.Cosine
                    true, false, RetrievalScoreSpace.Fused
                    true, true, RetrievalScoreSpace.Reranked
                ] do
                let! pipeline = composed keywordIndex reranker hitCorpus
                let! pool = (pipeline :?> IScoredRetrievalPipeline).RetrieveScored (request ()) teamAccess
                let! matches = pipeline.Retrieve (request ()) teamAccess
                Expect.equal pool.Space expected (sprintf "%A" expected)

                Expect.equal
                    (pool.Matches |> List.map (fun m -> m.ChunkId, m.Score))
                    (matches |> List.map (fun m -> m.ChunkId, m.Score))
                    "Retrieve returns the pool's matches, scores unchanged"
        }

        testCaseAsync "dense-only: a hit corpus is a hit and a miss corpus a low-score miss, in cosine space"
        <| async {
            let! hitPipeline = composed false false hitCorpus
            let! missPipeline = composed false false missCorpus
            let! _, hit = classify hitPipeline (Some 0.3)
            let! missPool, miss = classify missPipeline (Some 0.3)

            Expect.equal hit.RetrievalHits 1 "the answering corpus clears a cosine floor"
            Expect.equal miss.RetrievalLowScoreMisses 1 "the off-topic corpus returns chunks the floor drops"
            Expect.isNonEmpty missPool.Matches "a low-score miss is not an empty pool"
        }

        testCaseAsync "fused: a miss corpus is a low-score miss, read on the best candidate's dense similarity"
        <| async {
            let! hitPipeline = composed true false hitCorpus
            let! missPipeline = composed true false missCorpus
            let! hitPool, hit = classify hitPipeline None
            let! missPool, miss = classify missPipeline None

            Expect.equal
                (hitPool.Matches |> List.map _.Score |> List.max, missPool.Matches |> List.map _.Score |> List.max)
                (1.0, 1.0)
                "both fused pools lead at 1.0, so the fused order alone cannot tell them apart"

            Expect.equal hit.RetrievalHits 1 "the answering corpus is a hit"
            Expect.equal miss.RetrievalLowScoreMisses 1 "the off-topic corpus is a low-score miss"
            Expect.isLessThan hit.RetrievalAvgTopScore 1.0 "the recorded top score is the absolute reading"

            Expect.notEqual
                hit.RetrievalAvgTopScore
                miss.RetrievalAvgTopScore
                "the average top score moves between a hit and a miss corpus"
        }

        testCaseAsync "reranked: the threshold is read on the reranker's own scale"
        <| async {
            let! hitPipeline = composed true true hitCorpus
            let! missPipeline = composed true true missCorpus
            let! _, hit = classify hitPipeline (Some 2.0)
            let! _, miss = classify missPipeline (Some 2.0)
            Expect.equal hit.RetrievalHits 1 "three query words held clear a threshold of two"
            Expect.equal miss.RetrievalLowScoreMisses 1 "no candidate holds more than none"
        }

        testCaseAsync "an empty pool is empty, whatever the threshold"
        <| async {
            let telemetry = ToolUp.RAG.RagTelemetry.RollingRagTelemetry()

            (telemetry :> ToolUp.RAG.RagTelemetry.IScoredRagTelemetry)
                .RecordScoredRetrieval(ScoredPool.ofMatches RetrievalScoreSpace.Cosine [], Some 0.5)

            let! snapshot = (telemetry :> ToolUp.Platform.Infra.IRagTelemetry.IRagTelemetry).Snapshot()
            Expect.equal snapshot.RetrievalEmpties 1 "empty"
        }

        testCaseAsync "a pipeline without the scored sibling still composes and reads in the composed space"
        <| async {
            let! inner = composed false false hitCorpus

            let plain =
                { new IRetrievalPipeline with
                    member _.Retrieve request context = inner.Retrieve request context
                    member _.Index chunkId chunk scope = inner.Index chunkId chunk scope
                    member _.DeleteByScope scope = inner.DeleteByScope scope
                }

            let! direct = inner.Retrieve (request ()) teamAccess
            let! pool = ScoredPool.retrieve RetrievalScoreSpace.Cosine plain (request ()) teamAccess
            Expect.equal pool.Space RetrievalScoreSpace.Cosine "the composed space"

            Expect.equal (pool.Matches |> List.map _.ChunkId) (direct |> List.map _.ChunkId) "its matches unchanged"

            Expect.isTrue
                (ScoredPool.declaring RetrievalScoreSpace.Cosine plain :? IScoredRetrievalPipeline)
                "declaring hands readers a scored pipeline"

            Expect.isTrue
                (obj.ReferenceEquals(ScoredPool.declaring RetrievalScoreSpace.Fused inner, inner))
                "a pipeline that declares its own space keeps it"
        }
    ]

/// The default pipeline in each composition, and a plain pipeline a
/// composition declares a space for, held to the scored-sibling contract.
let private scoredContractTests =
    let subject keywordIndex reranker () = async {
        let! pipeline = composed keywordIndex reranker hitCorpus

        let subject: IScoredRetrievalPipelineContract.Subject = {
            Pipeline = pipeline
            Request = request ()
            Access = teamAccess
        }

        return subject
    }

    let declared () = async {
        let! inner = composed false false hitCorpus

        let plain =
            { new IRetrievalPipeline with
                member _.Retrieve request context = inner.Retrieve request context
                member _.Index chunkId chunk scope = inner.Index chunkId chunk scope
                member _.DeleteByScope scope = inner.DeleteByScope scope
            }

        let subject: IScoredRetrievalPipelineContract.Subject = {
            Pipeline = ScoredPool.declaring RetrievalScoreSpace.Cosine plain
            Request = request ()
            Access = teamAccess
        }

        return subject
    }

    testList "the scored sibling's contract" [
        IScoredRetrievalPipelineContract.tests
            "RetrievalPipeline (dense-only)"
            RetrievalScoreSpace.Cosine
            (subject false false)
        IScoredRetrievalPipelineContract.tests
            "RetrievalPipeline (fused)"
            RetrievalScoreSpace.Fused
            (subject true false)
        IScoredRetrievalPipelineContract.tests
            "RetrievalPipeline (reranked)"
            RetrievalScoreSpace.Reranked
            (subject true true)
        IScoredRetrievalPipelineContract.tests "ScoredPool.declaring" RetrievalScoreSpace.Cosine declared
    ]

let tests =
    testList "RAG telemetry and audit scope" [ scoreSpaceTests; auditScopeTests; scoredPoolTests; scoredContractTests ]