module ToolUp.Platform.Tests.InProcess.HostedServiceGateTests

// ─── Phase 1051 — every hosted service meets the gate or states its reason ──
//
// A hosted-service registration is in one of two states: the
// `ProcessProfileGate` matrix admits it for the profile, or it is exempt and
// says why (`HostedServiceExemption`, registered beside it by
// `ProcessProfileGate.addExempt`). There is no third state. The guard
// composes the RAG companion (which composes the AI one over the base
// `ServerApp`) together with the platform's own compose-time sites, under
// every `ProcessProfile × ServerlessHost` cell the matrix admits nothing in
// — `WebOnly`, `DispatcherOnly` (none of these registrations is one of its
// two dispatchers) and `ServerlessHost` — and reads the descriptors off the
// service collection: each `IHostedService` there must have an exemption.
// Under `AllInOne` the matrix admits everything, and the same registrations
// must still carry the same exemptions.
//
// Before this phase the RAG ingestion and re-embedding services (and every
// other site below) registered on every profile with no exemption, so each
// cell here was red.

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

let private silentLogger =
    { new ILogger with
        member _.Debug _ = ()
        member _.Info _ = ()
        member _.Warn _ = ()
        member _.Error(_, _) = ()
    }

/// The configuration a cell composes under: every subsystem whose
/// hosted-service site this guard reaches switched on.
let private configFor (profile: ProcessProfile) (host: ServerlessHostMode) : ServerConfig = {
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
/// return the service collection they registered into.
let private composed (config: ServerConfig) : IServiceCollection =
    let storage = InMemoryBlobStorage() :> IBlobStorage
    let services = ServiceCollection() :> IServiceCollection

    let app =
        ToolUp.RAG.RAGCompose.RAGServerApp.create stubAiFactory stubProviderProfile embedder
        |> ToolUp.RAG.RAGCompose.RAGServerApp.withConfig config
        |> ToolUp.RAG.RAGCompose.RAGServerApp.withStorage storage
        |> ToolUp.RAG.RAGCompose.RAGServerApp.withIngestionRecoverySweep [ "_platform" ]
        |> ToolUp.RAG.RAGCompose.RAGServerApp.withVacuumSchedule
        |> ToolUp.RAG.RAGCompose.RAGServerApp.mapAI (fun ai -> {
            ai with
                Base =
                    ai.Base
                    |> ServerApp.withDataObjectOrphanSweep (
                        DataObjectOrphanSweep.DataObjectOrphanSweepPolicy.forScopes [ "_platform" ]
                    )
        })
        |> ToolUp.RAG.RAGCompose.composeRAG

    let services =
        match app.Extensions.ServiceConfig with
        | Some configure -> configure services
        | None -> services

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

    services

let private hostedCount (services: IServiceCollection) =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<Microsoft.Extensions.Hosting.IHostedService>)
    |> Seq.length

let private exemptions (services: IServiceCollection) : HostedServiceExemption list =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<HostedServiceExemption>)
    |> Seq.map (fun d -> d.ImplementationInstance :?> HostedServiceExemption)
    |> List.ofSeq


let tests =
    testList "Phase 1051 — hosted services meet the gate or state their reason" [
        for profile, host in
            [
                WebOnly, KestrelHost
                DispatcherOnly, KestrelHost
                AllInOne, ServerlessHost
                WorkerOnly, ServerlessHost
            ] do
            test (
                sprintf
                    "%A x %A: the matrix admits nothing, so every hosted service is exempt with a reason"
                    profile
                    host
            ) {
                let config = configFor profile host

                for subsystem in
                    [
                        JobSchedulerSubsystem
                        AuditReplicatorSubsystem
                        DataMigrationSweepSubsystem
                        HealthStateTrackerSubsystem
                    ] do
                    Expect.isFalse
                        (ProcessProfileGate.shouldRegisterBackgroundService config subsystem)
                        (sprintf "the cell admits no %A (premise)" subsystem)

                let services = composed config
                let exempt = exemptions services

                Expect.isGreaterThan (hostedCount services) 8 "the guard reaches the sites it claims to (probe)"

                Expect.equal
                    (hostedCount services)
                    exempt.Length
                    (sprintf
                        "every IHostedService has an exemption; the exemptions present: %s"
                        (exempt |> List.map _.Service |> String.concat "; "))

                for e in exempt do
                    Expect.isFalse (String.IsNullOrWhiteSpace e.Reason) (sprintf "%s states a reason" e.Service)

                for named in [ "RAG ingestion"; "RAG re-embedding" ] do
                    Expect.exists exempt (fun e -> e.Service = named) (sprintf "%s is named with its reason" named)
            }

        test "AllInOne x KestrelHost registers the same hosted services, with the same exemptions (GP 11)" {
            let everywhere = composed (configFor AllInOne KestrelHost)
            let web = composed (configFor WebOnly KestrelHost)

            Expect.equal (hostedCount everywhere) (hostedCount web) "an exempt service registers on every profile"

            Expect.equal
                (exemptions everywhere |> List.map _.Service)
                (exemptions web |> List.map _.Service)
                "and says why on every profile"
        }
    ]