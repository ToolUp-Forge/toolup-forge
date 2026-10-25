module ToolUp.Platform.Tests.InProcess.VectorisationHookTests

// The post-save vectorisation hook routes a Data Manager file into the
// vector scope its storage container names. A `user-{id}` container is one
// caller's private storage, so its chunks belong in that caller's `User`
// scope: routing them to the shared `Deployment` scope makes them
// retrievable by every authenticated caller on the deployment, which the
// `VectorScope.Deployment` hard rule forbids.

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.IVectorStore
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.VectorisationTypes
open ProcessedDataTypes
open ToolUp.RAG.IngestionTypes

type private SilentLogger() =
    interface ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()

let private unitVec: float32 array =
    Array.init 8 (fun i -> if i = 0 then 1.0f else 0.0f)

type private ConstantEmbedder() =
    interface IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return unitVec }

        member _.GenerateEmbeddings texts =
            batchedFallback (fun _ -> async { return unitVec }) texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "constant-v1"

let private dataType = "notes-csv"

let private handler: VectorisationHandler = {
    DataTypeId = dataType
    Vectorise =
        fun _ -> [
            {
                Content = "alice's private figures"
                Metadata = Map.empty
            }
        ]
    Summarise = None
}

/// Run the hook for one saved file and return the job it enqueued.
let private enqueueFor (scope: StorageScope) (createdBy: string) : Async<DocumentIngestionJob> = async {
    let queue = IngestionQueue(16)

    let hook =
        ToolUp.RAG.RAGCompose.makeVectorisationHook
            [ handler ]
            queue
            (ToolUp.RAG.RagTelemetry.createNoOp ())
            false
            None
            None
            (ref None)
            (ref None)
            DropWrite
            (ref None)
            (ref None)
            (SilentLogger())

    let processed: ProcessedData = { TypeName = "Notes"; Payload = "{}" }

    let entry =
        ProcessedFileEntry.summarised "figures.csv" dataType DateTime.UtcNow processed

    do! hook (processed, entry, scope, createdBy)

    match! (queue :> IIngestionQueue).TryDequeue() with
    | Some lease -> return lease.Job
    | None -> return failtest "the hook enqueued no job"
}

let private userScope (userId: string) : StorageScope = {
    ScopeId = userId
    Container = $"user-{userId}"
    Persist = true
}

let tests =
    testList "Vectorisation hook scope" [
        testAsync "a user-{id} container is vectorised into that user's User scope" {
            let! job = enqueueFor (userScope "alice") "alice"
            Expect.equal job.Scope (VectorScope.User "alice") "a per-user container lands in the user's own scope"
        }

        testAsync "a second user's retrieval over the same deployment returns none of the chunks" {
            let! job = enqueueFor (userScope "alice") "alice"

            let storage =
                ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage() :> BlobStorage.IBlobStorage

            use store =
                new ToolUp.RAG.InMemoryVectorStore.InMemoryVectorStore(
                    storage,
                    logger = SilentLogger(),
                    flushIntervalMs = 60000
                )

            let vs = store :> IVectorStore

            for chunkId, chunk in job.Chunks do
                do! vs.Upsert job.Scope chunkId unitVec chunk

            let pipeline =
                new ToolUp.RAG.RetrievalPipeline.RetrievalPipeline(vs, ConstantEmbedder()) :> IRetrievalPipeline

            // Every scope a non-team caller can name: the deployment's shared
            // scope, its own, and the uploader's.
            let request =
                RetrievalRequest.create
                    "figures"
                    [ Deployment; VectorScope.User "bob"; VectorScope.User "alice" ]
                    10
                    Interleaved

            let! bobResults = pipeline.Retrieve request (AccessContext.unrestricted (AuthenticatedUser "bob"))
            Expect.isEmpty bobResults "bob must retrieve none of alice's chunks"

            let! aliceResults = pipeline.Retrieve request (AccessContext.unrestricted (AuthenticatedUser "alice"))
            Expect.isNonEmpty aliceResults "alice retrieves her own chunks (positive control)"
        }

        testAsync "a team container still lands in its team scope" {
            let! job =
                enqueueFor
                    {
                        ScopeId = "acme"
                        Container = "team-acme"
                        Persist = true
                    }
                    "alice"

            Expect.equal job.Scope (Team "acme") "team containers are unchanged"
        }

        testAsync "only the deployment container lands in the Deployment scope" {
            let! job =
                enqueueFor
                    {
                        ScopeId = ScopeEnumeration.DeploymentContainer
                        Container = ScopeEnumeration.DeploymentContainer
                        Persist = true
                    }
                    ""

            Expect.equal job.Scope Deployment "the deployment container is deployment-wide shared content"
        }
    ]