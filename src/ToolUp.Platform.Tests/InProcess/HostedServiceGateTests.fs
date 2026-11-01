module ToolUp.Platform.Tests.InProcess.HostedServiceGateTests

// ─── every hosted service goes through the one placement gate ────────
//
// `Hosting.register` records every unit of background work it is handed
// (`BackgroundUnitRecord`) and registers the unit's hosted service only when
// the deployment's shape admits the unit's `Placement`. So a composition's
// hosted services and its ADMITTED records are the same set — there is no
// way to register one without the other. The guard composes the RAG
// companion (which composes the AI one over the base `ServerApp`) together
// with the platform's own compose-time sites, under every
// `ProcessProfile × ServerlessHost` cell, and reads the descriptors off the
// service collection: the hosted-service count must equal the admitted-unit
// count, and a serverless host must start only `OnceAtStart` units.

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.IEmbeddingProvider
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

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

let private embedder =
    { new IEmbeddingProvider with
        member _.GenerateEmbedding _ = async { return Array.zeroCreate 8 }

        member this.GenerateEmbeddings texts =
            batchedFallback (this :> IEmbeddingProvider).GenerateEmbedding texts

        member _.Dimensions = 8
        member _.ProviderId = "test"
        member _.ModelId = "gate-v1"
    }

/// A stand-in for a shared ingestion queue store (the Redis companion's
/// shape); nothing here drains it.
let private sharedQueue =
    { new ToolUp.RAG.IngestionTypes.IIngestionQueueStore with
        member _.Name = "shared-test-queue"
        member _.Enqueue(_, _) = async { return true }
        member _.Claim _ = async { return None }
        member _.Complete _ = async { return () }
        member _.Release _ = async { return () }
        member _.ReclaimExpired() = async { return 0 }
        member _.Depth() = async { return 0 }
    }

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// The configuration a cell composes under: every subsystem whose
/// hosted-service site this guard reaches switched on.
let configFor (profile: ProcessProfile) (host: ServerlessHostMode) : ServerConfig = {
    ServerConfig.defaults with
        ProcessProfile = profile
        ServerlessHost = host
        JobScheduler = InProcessJobScheduler
        TenantLifecycle = EnabledTenantLifecycle
        ModelFitting = EnabledModelFitting
        DataSubjectRequests =
            DataSubjectRequestMode.Enabled {
                DataSubjectRequestConfig.sync ErasurePolicy.Tombstone with
                    Async = true
            }
}

/// Compose the RAG companion and the platform sites under `config`, and
/// return the service collection they registered into. `durableQueue`
/// composes a shared ingestion queue store, which is what makes RAG
/// ingestion shared work.
let composedWith (durableQueue: bool) (config: ServerConfig) : IServiceCollection =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let services = ServiceCollection() :> IServiceCollection

    let rag =
        ToolUp.RAG.RAGCompose.RagOptions.create stubAiFactory stubProviderProfile embedder
        |> ToolUp.RAG.RAGCompose.RagOptions.withIngestionRecoverySweep [ "_platform" ]
        |> ToolUp.RAG.RAGCompose.RagOptions.withVacuumSchedule

    let rag =
        if durableQueue then
            ToolUp.RAG.RAGCompose.RagOptions.withDurableIngestionQueue sharedQueue rag
        else
            rag

    let app =
        ServerApp.empty
        |> ServerApp.withConfig config
        |> ServerApp.withStorage storage
        |> ToolUp.RAG.RAGCompose.withRAG rag
        |> ServerApp.withDataObjectOrphanSweep (
            DataObjectOrphanSweep.DataObjectOrphanSweepPolicy.forScopes [ "_platform" ]
        )

    let services =
        ToolUp.Platform.Tests.Support.TestSubstrate.applyServiceConfig app services

    // The platform's own compose-time sites. The scheduler instance is
    // captured by the start hooks and never invoked: nothing here starts.
    let scheduler = Some(Unchecked.defaultof<IJobScheduler>)
    let auditLog = Unchecked.defaultof<IAuditLog>
    let channel = Unchecked.defaultof<INotificationChannel>
    ComposeTenantLifecycle.registerTenantLifecycle services config
    ComposeJobs.registerModelFitting services config scheduler auditLog silentLogger
    ComposeJobs.registerDataSubjectRequestJobs services config storage scheduler auditLog channel silentLogger

    ComposeStores.registerBackupCoordinator
        services
        {
            config with
                Backup =
                    BackupEnabled {
                        BackupSettings.defaults with
                            SnapshotCron = Some "0 3 * * *"
                    }
        }
        storage
        None

    // Admission is decided once, against the final configuration — the
    // step `compose` runs after every registration.
    Hosting.admit services config

/// The composition over the historical in-process ingestion queue.
let composed (config: ServerConfig) : IServiceCollection = composedWith false config

/// Every `ProcessProfile × ServerlessHost` cell.
let cells: (ProcessProfile * ServerlessHostMode) list = [
    for host in [ KestrelHost; ServerlessHost ] do
        for profile in [ AllInOne; WebOnly; WorkerOnly; DispatcherOnly ] do
            profile, host
]

let private hostedCount (services: IServiceCollection) =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<Microsoft.Extensions.Hosting.IHostedService>)
    |> Seq.length

let private admittedIds (config: ServerConfig) (services: IServiceCollection) : string list =
    Hosting.describe config (Hosting.records services)
    |> List.filter _.Admitted
    |> List.map (fun row -> ComponentId.value row.Unit.Id)

let tests =
    testList "Hosting — every hosted service goes through the one placement gate" [
        for profile, host in cells do
            test (sprintf "%A x %A: the hosted services are exactly the admitted units" profile host) {
                let config = configFor profile host
                let services = composed config
                let admitted = admittedIds config services

                Expect.isGreaterThan
                    (Hosting.records services).Length
                    8
                    "the guard reaches the sites it claims to (probe)"

                Expect.equal
                    (hostedCount services)
                    admitted.Length
                    (sprintf "one hosted service per admitted unit; admitted: %s" (String.concat "; " admitted))
            }

        test "a serverless host starts only OnceAtStart units" {
            for profile in [ AllInOne; WebOnly; WorkerOnly; DispatcherOnly ] do
                let config = configFor profile ServerlessHost

                let admitted =
                    Hosting.describe config (Hosting.records (composed config))
                    |> List.filter _.Admitted

                Expect.isNonEmpty admitted "the one-shot units still register (probe)"

                for row in admitted do
                    Expect.equal
                        row.Unit.Placement
                        OnceAtStart
                        (sprintf "%s is admitted on a serverless host" (ComponentId.value row.Unit.Id))
        }

        test "a WebOnly composition over a shared ingestion queue registers no RAG ingestion or re-embedding" {
            let web = configFor WebOnly KestrelHost
            let services = composedWith true web
            let onWeb = admittedIds web services
            let onWorker = admittedIds (configFor WorkerOnly KestrelHost) services

            for unit in [ "hosted:rag-ingestion"; "hosted:rag-reembedding" ] do
                Expect.isFalse (List.contains unit onWeb) (sprintf "%s is not admitted on WebOnly" unit)
                Expect.isTrue (List.contains unit onWorker) (sprintf "%s runs on the worker silo" unit)
        }

        test "over the in-process ingestion queue, ingestion stays with the process that accepted the upload" {
            let config = configFor WebOnly KestrelHost

            let ingestion =
                Hosting.describe config (Hosting.records (composed config))
                |> List.find (fun row -> ComponentId.value row.Unit.Id = "hosted:rag-ingestion")

            Expect.equal ingestion.Unit.Placement ProcessLocal "process-local over the in-process channel"
            Expect.isTrue ingestion.Admitted "a WebOnly silo drains what its own HTTP tier enqueued"
        }
    ]