module ToolUp.Platform.Tests.InProcess.DeployPlaneTests

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.IO
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.Platform.EntityStore
open ToolUp.Platform.IEntityStore
open ToolUp.Platform.TenantEntity
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage
open ToolUp.ContainerSchedulers.DockerLocal.Scheduler

// ─── Phase 26 substrate — in-process bindings for the contract packs ─
//
// Binds the four `I*Contract` packs to single-node default impls + a
// minimal in-memory mock surface. The TenantFleet pack binds to the
// shipped `EntityStoreTenantFleet`; the other three bind to small
// in-memory mocks until lane 1's `JobSchedulerBuildOrchestrator` and
// `DefaultDeployPipeline` land — once they do, those bindings swap to
// the real impls without touching the contract packs.
//
// The IContainerScheduler pack runs against the in-memory mock
// always. A second binding targets `DockerLocalContainerScheduler`
// against the local Docker socket; the binding's test list is
// returned wrapped in a skip when no socket is reachable so a fresh
// checkout without Docker still ships green.

// ─── In-memory mocks ────────────────────────────────────────────────

type InMemoryContainerScheduler() =
    let containers =
        ConcurrentDictionary<ContainerId, TenantId * ContainerSpec * ContainerStatus>()

    let nextId () = Guid.NewGuid().ToString("N")

    interface IContainerScheduler with
        member _.LaunchContainer(tenantId, spec) = async {
            if String.IsNullOrWhiteSpace spec.Image then
                return Error(InvalidContainerSpec "Image is required")
            else
                match spec.ExposedPort with
                | Some p when p < 1 || p > 65535 ->
                    return Error(InvalidContainerSpec(sprintf "ExposedPort %d outside 1..65535" p))
                | _ ->
                    let id = nextId ()
                    containers[id] <- (tenantId, spec, ContainerRunning DateTime.UtcNow)
                    return Ok id
        }

        member _.StopContainer(containerId) = async {
            match containers.TryGetValue containerId with
            | true, (tid, spec, _) ->
                containers[containerId] <- (tid, spec, ContainerExited(0, DateTime.UtcNow))
                return Ok()
            | _ -> return Ok() // idempotent on missing
        }

        member _.RestartContainer(containerId) = async {
            match containers.TryGetValue containerId with
            | true, (tid, spec, _) ->
                containers[containerId] <- (tid, spec, ContainerRunning DateTime.UtcNow)
                return Ok()
            | _ -> return Error(UnknownContainer containerId)
        }

        member _.GetContainerStatus(containerId) = async {
            match containers.TryGetValue containerId with
            | true, (_, _, status) -> return Ok status
            | _ -> return Ok ContainerNotFound
        }

        member _.ListContainers(tenantId) = async {
            return
                containers
                |> Seq.choose (fun kv ->
                    let id = kv.Key
                    let (tid, spec, status) = kv.Value

                    let labels = spec.Labels |> Map.add "tenantId" tid

                    let info = {
                        ContainerId = id
                        TenantId = tid
                        ImageRef = spec.Image
                        Status = status
                        Labels = labels
                    }

                    match tenantId with
                    | Some requested when requested <> tid -> None
                    | _ -> Some info)
                |> Seq.toList
        }

        member _.StreamLogs(_, _) =
            { new IAsyncEnumerable<LogEntry> with
                member _.GetAsyncEnumerator(_) =
                    { new IAsyncEnumerator<LogEntry> with
                        member _.Current = Unchecked.defaultof<_>

                        member _.MoveNextAsync() =
                            System.Threading.Tasks.ValueTask<bool>(false)

                        member _.DisposeAsync() = System.Threading.Tasks.ValueTask()
                    }
            }

type InMemoryBuildOrchestrator() =
    let builds = ConcurrentDictionary<BuildId, BuildSummary>()
    let idempotencyMap = ConcurrentDictionary<string, BuildId>()
    let mutable depth = 0

    let nextId () = Guid.NewGuid().ToString("N")

    let validate (req: BuildRequest) : Result<unit, BuildOrchestratorError> =
        if String.IsNullOrEmpty req.AppSlug then
            Error(InvalidRequest "AppSlug is required")
        else
            match req.Source with
            | GitHubRef(_, sha) when String.IsNullOrEmpty sha -> Error(InvalidRequest "GitHubRef.sha is required")
            | PrebuiltImage img when String.IsNullOrEmpty img -> Error(InvalidRequest "PrebuiltImage.image is required")
            | _ ->
                if req.RetryPolicy.MaxAttempts < 1 then
                    Error(InvalidRequest "RetryPolicy.MaxAttempts must be >= 1")
                else
                    Ok()

    interface IBuildOrchestrator with
        member _.EnqueueBuild(request) = async {
            match validate request with
            | Error e -> return Error e
            | Ok() ->
                match request.Idempotency with
                | Some token when idempotencyMap.ContainsKey token -> return Ok idempotencyMap[token]
                | _ ->
                    let id = nextId ()
                    Interlocked.Increment &depth |> ignore

                    let summary = {
                        BuildId = id
                        AppSlug = request.AppSlug
                        Status = Queued
                        SubmittedAt = DateTime.UtcNow
                        SubmittedBy = request.SubmittedBy
                        AttemptCount = 0
                    }

                    builds[id] <- summary

                    match request.Idempotency with
                    | Some token -> idempotencyMap[token] <- id
                    | None -> ()

                    return Ok id
        }

        member _.GetBuild(buildId) = async {
            match builds.TryGetValue buildId with
            | true, s -> return Ok s
            | _ -> return Error(UnknownBuild buildId)
        }

        member _.ListActiveBuilds(appSlug) = async {
            return
                builds.Values
                |> Seq.filter (fun s ->
                    match s.Status with
                    | Queued
                    | Building _ -> true
                    | _ -> false)
                |> Seq.filter (fun s ->
                    match appSlug with
                    | Some slug -> s.AppSlug = slug
                    | None -> true)
                |> Seq.sortBy _.SubmittedAt
                |> Seq.toList
        }

        member _.GetQueueDepth() = async { return depth }

        member _.CancelBuild(buildId, byUserId) = async {
            match builds.TryGetValue buildId with
            | true, s ->
                let cancelled = {
                    s with
                        Status = BuildStatus.Cancelled(DateTime.UtcNow, byUserId)
                }

                builds[buildId] <- cancelled
                return Ok()
            | _ -> return Error(UnknownBuild buildId)
        }

        member _.GetBuildHistory(appSlug, count) = async {
            return
                builds.Values
                |> Seq.filter (fun s -> s.AppSlug = appSlug)
                |> Seq.sortByDescending _.SubmittedAt
                |> Seq.truncate count
                |> Seq.toList
        }

type InMemoryDeployPipeline() =
    let deploys = ConcurrentDictionary<DeployId, DeploySummary>()
    let perTenant = ConcurrentDictionary<TenantId, ResizeArray<DeployId>>()
    let nextId () = Guid.NewGuid().ToString("N")

    interface IDeployPipeline with
        member _.BeginDeploy(tenantId, buildId, manifest, byUserId) = async {
            let id = nextId ()

            let summary = {
                DeployId = id
                TenantId = tenantId
                BuildId = buildId
                Manifest = manifest
                State = DeployQueued
                StartedAt = DateTime.UtcNow
                StateChangedAt = DateTime.UtcNow
                SubmittedBy = byUserId
            }

            deploys[id] <- summary
            let bucket = perTenant.GetOrAdd(tenantId, fun _ -> ResizeArray<_>())
            lock bucket (fun () -> bucket.Add id)
            return Ok id
        }

        member _.GetDeployState(deployId) = async {
            match deploys.TryGetValue deployId with
            | true, s -> return Ok s
            | _ -> return Error(UnknownDeploy deployId)
        }

        member _.Rollback(tenantId, _byUserId) = async {
            match perTenant.TryGetValue tenantId with
            | true, bucket when bucket.Count > 0 ->
                let succeeded =
                    bucket
                    |> Seq.choose (fun id ->
                        match deploys.TryGetValue id with
                        | true, s ->
                            match s.State with
                            | DeploySucceeded _ -> Some s
                            | _ -> None
                        | _ -> None)
                    |> Seq.tryHead

                match succeeded with
                | Some _ -> return Ok(nextId ())
                | None -> return Error(NothingToRollbackTo tenantId)
            | _ -> return Error(NothingToRollbackTo tenantId)
        }

        member _.GetDeployHistory(tenantId, count) = async {
            match perTenant.TryGetValue tenantId with
            | true, bucket ->
                return
                    bucket
                    |> Seq.choose (fun id ->
                        match deploys.TryGetValue id with
                        | true, s -> Some s
                        | _ -> None)
                    |> Seq.sortByDescending _.StateChangedAt
                    |> Seq.truncate count
                    |> Seq.toList
            | _ -> return []
        }

// ─── ITenantFleet binding (real EntityStoreTenantFleet) ──────────────

let tenantFleetTests =
    let factory () : ITenantFleet =
        // In-memory blob storage rather than LocalFileStorage. The Tenant
        // entity declares a (Region, Slug) compound index whose key
        // segments are pipe-delimited via `EntityRegistration.withCompoundIndex`'s
        // documented join character; on Windows NTFS rejects `|` in path
        // segments, so LocalFileStorage would fail on every test that
        // touches the compound-index materialisation path. The
        // dictionary-backed `InMemoryBlobStorage` sidesteps the path
        // validation entirely and exercises the substrate's documented
        // contract end-to-end on every OS. See TIDY-UP entry "Compound
        // index pipe-encoding on Windows" for the substrate fix.
        let blob = InMemoryBlobStorage.InMemoryBlobStorage() :> IBlobStorage
        let dos = DataObjectStore(blob) :> IDataObjectStore
        let registry = EntityRegistry()
        registry.Register<Tenant>(Tenant.registration)
        let entityStore = BlobEntityStore(dos, blob, registry, None) :> IEntityStore

        let containerScheduler = InMemoryContainerScheduler() :> IContainerScheduler

        let deployPipeline = InMemoryDeployPipeline() :> IDeployPipeline

        let logger =
            { new ILogger with
                member _.Debug(_) = ()
                member _.Info(_) = ()
                member _.Warn(_) = ()
                member _.Error(_, _) = ()
            }

        EntityStoreTenantFleet(entityStore, containerScheduler, deployPipeline, logger) :> ITenantFleet

    ITenantFleetContract.tests "EntityStoreTenantFleet" factory

// ─── IBuildOrchestrator binding (in-memory mock) ─────────────────────

let buildOrchestratorTests =
    let factory () : IBuildOrchestrator =
        InMemoryBuildOrchestrator() :> IBuildOrchestrator

    IBuildOrchestratorContract.tests "InMemoryBuildOrchestrator" factory

// ─── IDeployPipeline binding (in-memory mock) ────────────────────────

let deployPipelineTests =
    let factory () : IDeployPipeline =
        InMemoryDeployPipeline() :> IDeployPipeline

    IDeployPipelineContract.tests "InMemoryDeployPipeline" factory

// ─── IContainerScheduler binding (in-memory mock) ────────────────────

let containerSchedulerInMemoryTests =
    let binding: IContainerSchedulerContract.ContainerSchedulerBinding = {
        Factory = fun () -> InMemoryContainerScheduler() :> IContainerScheduler
        BackendReal = false
        SampleImage = "mock:latest"
        SampleTenantId = "tenant-mock"
    }

    IContainerSchedulerContract.tests "InMemoryContainerScheduler" binding

// ─── IContainerScheduler binding (DockerLocal — env-gated) ───────────
//
// The Docker-backed leg runs only when the local Docker socket /
// named pipe is reachable. CI without Docker reports the pack
// `Pending` rather than failing — same convention as the
// AIProvider live-API packs.

let private dockerSocketReachable () =
    try
        let config = DockerLocalContainerSchedulerConfig.defaults

        if
            System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform
                System.Runtime.InteropServices.OSPlatform.Windows
        then
            // On Windows, check the named pipe exists by attempting a
            // 250ms connect.
            use pipe =
                new System.IO.Pipes.NamedPipeClientStream(
                    ".",
                    config.WindowsPipeName,
                    System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous
                )

            try
                pipe.Connect(250)
                true
            with _ ->
                false
        else
            File.Exists config.UnixSocketPath
    with _ ->
        false

let containerSchedulerDockerLocalTests =
    if dockerSocketReachable () then
        let binding: IContainerSchedulerContract.ContainerSchedulerBinding = {
            Factory = fun () -> DockerLocalContainerScheduler.create DockerLocalContainerSchedulerConfig.defaults
            BackendReal = true
            SampleImage = "alpine:latest"
            SampleTenantId = "tenant-docker-local"
        }

        IContainerSchedulerContract.tests "DockerLocalContainerScheduler" binding
    else
        testList "DockerLocalContainerScheduler — IContainerScheduler contract (skipped)" [
            testCase "Docker socket not reachable — pack skipped on this machine"
            <| fun _ -> skiptest "no Docker socket available; set up Docker Desktop / dockerd to exercise"
        ]