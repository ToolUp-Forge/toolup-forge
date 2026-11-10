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
open ToolUp.Platform.Infra.IVectorStore
open ToolUp.Platform.Infra.IEmbeddingProvider
open ToolUp.Platform.Infra.IRetrievalPipeline
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.Platform.VectorisationTypes
open ProcessedDataTypes
open DataManagementTypes
open ToolUp.RAG.IngestionTypes
open ToolUp.Platform.Infra
open ToolUp.Platform.Config
open ToolUp.Platform.Audit
open ToolUp.Platform.Compose
open ToolUp.Platform.Files
open ToolUp.Platform.Testing

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

/// The post-save hook over `handlers`, a status store and the Data
/// Manager's attempts over `storage`.
let private hookOver
    (handlers: VectorisationHandler list)
    (queue: IngestionQueue)
    (status: IIngestionStatusStore)
    (storage: BlobStorage.IBlobStorage)
    =
    ToolUp.RAG.VectorisationHook.makeVectorisationHook {
        Handlers = handlers
        Queue = queue
        Telemetry = ToolUp.RAG.RagTelemetry.createNoOp ()
        EnableSummaries = false
        MaxChunkBytes = None
        MaxDocumentBytes = None
        EventStore = InMemoryEventStore.InMemoryEventStore()
        StatusStore = status
        Attempts = IngestionAttemptStore.dataManager status storage Logger.silent
        OverflowPolicy = DropWrite
        AuditLog = NoOpAuditLog()
        Notifications = NotificationChannel.NoOpNotificationChannel()
        Logger = Logger.silent
    }

/// Run the hook for one saved file and return the job it enqueued.
let private enqueueFor (scope: StorageScope) (createdBy: string) : Async<DocumentIngestionJob> = async {
    let queue = IngestionQueue(16)

    let hook =
        hookOver
            [ handler ]
            queue
            (IngestionStatusStore.createInMemory ())
            (ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage())

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
                    logger = Logger.silent,
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

            let! bobResults = pipeline.Retrieve request (AccessContexts.forSubject (AuthenticatedUser "bob"))
            Expect.isEmpty bobResults "bob must retrieve none of alice's chunks"

            let! aliceResults = pipeline.Retrieve request (AccessContexts.forSubject (AuthenticatedUser "alice"))
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

        testAsync "a Data Manager file reaches Indexed from its persisted attempt across a replica switch" {
            let storage =
                ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage() :> BlobStorage.IBlobStorage

            // One durable status store per "replica", over the same backend.
            let replicaStatus () =
                IngestionStatusStore.create
                    (DataObjectStore.DataObjectStore(storage, Logger.silent) :> IDataObjectStore)
                    None

            let threeChunks: VectorisationHandler = {
                handler with
                    Vectorise =
                        fun _ -> [
                            for i in 1..3 ->
                                {
                                    Content = sprintf "figures part %d" i
                                    Metadata = Map.empty
                                }
                        ]
            }

            let queue = IngestionQueue(16)
            let status = replicaStatus ()
            let hook = hookOver [ threeChunks ] queue status storage
            let processed: ProcessedData = { TypeName = "Notes"; Payload = "{}" }

            let entry =
                ProcessedFileEntry.summarised "figures.csv" dataType DateTime.UtcNow processed

            do! hook (processed, entry, userScope "alice", "alice")

            let! lease = (queue :> IIngestionQueue).TryDequeue()

            let job =
                match lease with
                | Some lease -> lease.Job
                | None -> failtest "the hook enqueued no job"

            Expect.isSome job.Attempt "the job carries the attempt the Data Manager store began"

            let chunkJobs = [
                for chunkId, chunk in job.Chunks ->
                    {
                        DocumentId = job.DocumentId
                        DocumentName = job.DocumentName
                        ChunkId = chunkId
                        Chunk = chunk
                        Scope = job.Scope
                        ScopeId = job.ScopeId
                        Container = job.Container
                        OriginatingUserId = job.OriginatingUserId
                        Attempt = job.Attempt
                    }
                    : IngestionJob
            ]

            Expect.hasLength chunkJobs 3 "precondition: three chunks"

            // Two replicas: nothing in process is shared between them.
            let replica () =
                let replicaStore = replicaStatus ()

                ToolUp.RAG.IngestionObserver.create
                    (IngestionAttemptStore.dataManager replicaStore storage Logger.silent)
                    None
                    Logger.silent

            let first = replica ()
            let second = replica ()

            do! first.OnChunkIndexed chunkJobs[0]
            do! second.OnChunkIndexed chunkJobs[1]
            // The first replica re-delivers a chunk the second counted.
            do! first.OnChunkIndexed chunkJobs[1]

            let! midway = (replicaStatus ()).Get(job.Container, job.DocumentId)
            Expect.equal midway (Some Pending) "a chunk counted on another replica is not counted twice"

            do! second.OnChunkIndexed chunkJobs[2]

            let! settled = (replicaStatus ()).Get(job.Container, job.DocumentId)
            Expect.equal settled (Some Indexed) "the file completes from the persisted attempt"
        }
    ]

// ─── RAG's registrations are staged ──────────────────────────────────
//
// RAG reads the event store, audit log and notification channel compose
// built from the `Substrate` handed to its staged contribution, and builds
// what it needs from other services in the app's own container. Two
// observables: the post-save hook writes through the SUBSTRATE's event store
// (a decoy registered in DI under `IEventStore` stays empty), and applying
// the registrations resolves nothing, so no container is built to probe for
// a service RAG reads.

let private stubAiFactory =
    { new ToolUp.AI.IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error ToolUp.AI.NoProviderConfigured }
        member _.TryResolveByLabel(_, _) = async { return Error ToolUp.AI.NoProviderConfigured }
        member _.BuildPlatform(_, _, _) = None
    }

let private stubProviderProfile =
    { new ToolUp.Platform.Providers.IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }
    }

/// A deployment with RAG composed and one vectorisation handler, so the
/// post-save hook is registered.
let private ragApp () : ServerApp =
    {
        ServerApp.empty with
            VectorisationHandlers = [ handler ]
    }
    |> ServerApp.withStorage (
        ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage() :> BlobStorage.IBlobStorage
    )
    |> ToolUp.RAG.RAGCompose.withRAG (
        ToolUp.RAG.RAGCompose.RagOptions.create stubAiFactory stubProviderProfile (ConstantEmbedder())
    )

let stagedTests =
    testList "RAG registrations are staged" [
        testAsync "the post-save hook writes through the substrate's event store, not a DI registration" {
            let app = ragApp ()
            let substrateEvents = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let decoy = InMemoryEventStore.InMemoryEventStore() :> IEventStore

            let services =
                Microsoft.Extensions.DependencyInjection.ServiceCollection()
                :> Microsoft.Extensions.DependencyInjection.IServiceCollection

            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IEventStore>(
                services,
                decoy
            )
            |> ignore

            let substrate = {
                ToolUp.Platform.Tests.Support.TestSubstrate.ofServices (ServerApp.effectiveConfig app) services with
                    EventStore = substrateEvents
            }

            Substrate.register substrate services |> ignore

            let services =
                ToolUp.Platform.Tests.Support.TestSubstrate.applyServiceConfig app services

            use sp =
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider
                    services

            let hook =
                Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetServices<
                    FileManagement.PostSaveHook
                 >(
                    sp
                )
                |> Seq.exactlyOne

            let processed: ProcessedData = { TypeName = "Slides"; Payload = "{}" }

            // A data type no handler serves: the hook records the skip.
            let entry =
                ProcessedFileEntry.summarised "slides.pptx" "unhandled-type" DateTime.UtcNow processed

            do! hook.Run(processed, entry, userScope "alice", "alice")

            let! written = substrateEvents.ReadByType("alice", "DocumentVectorisationSkipped")
            Expect.hasLength written 1 "the skip is written to the event store compose built"

            let! leaked = decoy.ReadAll "alice"
            Expect.isEmpty leaked "nothing is written to an IEventStore merely registered in DI"
        }

        test "applying RAG's registrations resolves nothing; the app's container builds what RAG reads" {
            let app = ragApp ()
            let built = ref 0

            let services =
                Microsoft.Extensions.DependencyInjection.ServiceCollection()
                :> Microsoft.Extensions.DependencyInjection.IServiceCollection

            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IDataObjectStore>(
                services,
                Func<IServiceProvider, IDataObjectStore>(fun _ ->
                    built.Value <- built.Value + 1

                    DataObjectStore.DataObjectStore(
                        ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage.InMemoryBlobStorage(),
                        Logger.silent
                    )
                    :> IDataObjectStore)
            )
            |> ignore

            let services =
                ToolUp.Platform.Tests.Support.TestSubstrate.applyServiceConfig app services

            Expect.equal built.Value 0 "registering RAG built no container to probe for the data-object store"

            use sp =
                Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider
                    services

            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<
                IRetrievalPipeline
             >(
                sp
            )
            |> ignore

            Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<
                IIngestionStatusStore
             >(
                sp
            )
            |> ignore

            Expect.equal built.Value 1 "the app's own container built the data-object store, once, and RAG read it"
        }
    ]