module ToolUp.Platform.Tests.InProcess.RagTelemetryTests

// Two pins over the retrieval read side.
//
// 1. What the `/health/rag` classification can see. Under the default
//    composition the pipeline fuses the dense and keyword rankings and
//    min-max normalises the pool, so the best returned candidate scores
//    exactly 1.0 whether the corpus answers the query or not. Every
//    number the prompt builder hands the telemetry is read off that
//    returned pool, so no threshold "in the fused space" can tell a hit
//    from a miss: the information is gone before the reader sees it.
//    The pin records that, so the change that carries an absolute
//    relevance signal out of the pipeline turns it red on purpose.
//
// 2. One team's RAG audit rows share one stream id: the retrieval trace,
//    the retrieval miss, the over-length-query refusal and the vacuum
//    sweep all land under the team's bare id.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.IRetrievalTracer
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

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
let private teamAccess = AccessContext.unrestricted (TeamMember("u1", "acme"))

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
                ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

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
            let spell = ToolUp.RAG.RetrievalTracers.auditScopeId
            Expect.equal (spell team) "acme" "a team row is keyed by the persisted stream key, the bare id"
            Expect.equal (spell Platform) "_platform" "the platform scope"
            Expect.equal (spell Deployment) "_deployment" "the deployment scope, the deployment container's name"
            Expect.equal (spell (VectorScope.User "u1")) "user:u1" "a user scope"
        }
    ]

let tests =
    testList "RAG telemetry and audit scope" [ scoreSpaceTests; auditScopeTests ]