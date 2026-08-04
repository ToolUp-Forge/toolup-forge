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
// Binds the four `I*Contract` packs to the shipped single-node
// defaults: TenantFleet to `EntityStoreTenantFleet`, the build-
// orchestrator pack to `JobSchedulerBuildOrchestrator` (over the
// deterministic `DeployPlaneJobScheduler` double below), and the
// deploy-pipeline pack to `DefaultDeployPipeline` composed over the
// same defaults. The in-memory mocks survive for the seams the packs
// still need doubles for: `IContainerScheduler` (the Docker-backed
// leg is env-gated), and `InMemoryDeployPipeline`, which the
// Phase 185 fallback binding keeps deliberately — it is the
// unchanged pre-Phase-185 implementer that proves the `PlanDeploy`
// extension member's GP 11 claim.
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

// ─── Deterministic IJobScheduler double for the deploy-plane bindings ─

let private nullLogger =
    { new ILogger with
        member _.Debug(_) = ()
        member _.Info(_) = ()
        member _.Warn(_) = ()
        member _.Error(_, _) = ()
    }

/// Minimal `IJobScheduler` double for binding the shipped
/// `JobSchedulerBuildOrchestrator`. Registers handlers, persists
/// registrations, and — when `dispatchOnTrigger` — executes the job's
/// handler synchronously inside `TriggerOnce`, so a build has
/// terminated before `EnqueueBuild` returns and a test never races a
/// background dispatch.
///
/// With `dispatchOnTrigger = false`, `TriggerOnce` acknowledges
/// without executing — the "background dispatch has not run yet"
/// state, held indefinitely. The IBuildOrchestrator contract pack
/// binds this mode so its active-build / queue-depth / cancel cases
/// observe builds exactly as enqueued (the real
/// `InProcessJobScheduler` dispatches via `Async.Start`, which would
/// make those observations a race).
type DeployPlaneJobScheduler(dispatchOnTrigger: bool) =
    let handlers = ConcurrentDictionary<string, IJobHandler>()
    let jobs = ConcurrentDictionary<JobId, JobRegistration>()

    interface IJobScheduler with
        member _.RegisterHandler(name, handler) = handlers[name] <- handler

        member _.RegisterHandlerAsync(name, handler) = async {
            handlers[name] <- handler
            return Ok()
        }

        member _.Schedule(registration) = async {
            let id = Guid.NewGuid()
            jobs[id] <- registration
            return Ok id
        }

        member _.Cancel(_scopeId, jobId) = async { jobs.TryRemove jobId |> ignore }

        member _.Disable(_, _) = async { return () }
        member _.Enable(_, _) = async { return () }
        member _.Get(_, _) = async { return None }
        member _.ListJobs(_) = async { return [] }
        member _.GetRecentRuns(_, _, _) = async { return [] }

        member _.TriggerOnce(_scopeId, jobId, byUserId) = async {
            match jobs.TryGetValue jobId with
            | false, _ -> return Error(sprintf "job %O not scheduled" jobId)
            | true, registration ->
                if not dispatchOnTrigger then
                    return Ok()
                else
                    match handlers.TryGetValue registration.Handler with
                    | false, _ -> return Error(sprintf "handler %s not registered" registration.Handler)
                    | true, handler ->
                        let now = DateTime.UtcNow

                        let ctx: JobContext = {
                            JobId = jobId
                            ScopeId = registration.ScopeId
                            AccessContext = AccessContext.unrestricted (AuthenticatedUser byUserId)
                            Attempt = 1
                            Trigger = registration.Trigger
                            TriggerSource = ScheduledManually byUserId
                            ScheduledAt = now
                            RunningAt = now
                            Payload = registration.Payload
                            DeadLetterDestination = registration.RetryPolicy.DeadLetterDestination
                        }

                        let! _ = handler.Execute ctx
                        return Ok()
        }

        member _.NotifyEventWritten(_, _, _) = async { return () }

// ─── ITenantFleet binding (real EntityStoreTenantFleet) ──────────────

let tenantFleetTests =
    let factory () : ITenantFleet =
        // LocalFileStorage exercises the cross-platform substrate
        // path on disk — the Tenant entity declares a (Region, Slug)
        // compound index whose joined key (`"region|slug"`) flows
        // through `BlobIndex.pathSafeSegment` so the `|` is
        // percent-encoded before it reaches the filesystem,
        // surviving Windows NTFS path validation. Mirrors the
        // BlobEntityStoreTests binding shape.
        let tempDir =
            Path.Combine(Path.GetTempPath(), "toolup-fleet-test-" + Guid.NewGuid().ToString("N"))

        Directory.CreateDirectory tempDir |> ignore
        let blob = LocalFileStorage.LocalFileStorage(tempDir) :> IBlobStorage
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

// ─── IBuildOrchestrator binding (shipped JobSchedulerBuildOrchestrator) ─
//
// Deferred-dispatch scheduler mode: builds stay observable in the
// state they were enqueued in, which is what the pack's active-list /
// queue-depth / cancel cases assert against.

let buildOrchestratorTests =
    let factory () : IBuildOrchestrator =
        let eventStore =
            ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

        ToolUp.Platform.JobSchedulerBuildOrchestrator.create (DeployPlaneJobScheduler false) eventStore nullLogger
        :> IBuildOrchestrator

    IBuildOrchestratorContract.tests "JobSchedulerBuildOrchestrator" factory

// ─── IDeployPipeline binding (shipped DefaultDeployPipeline) ─────────
//
// The full single-node default composition: DefaultDeployPipeline
// over JobSchedulerBuildOrchestrator (synchronous-dispatch scheduler
// mode, so an enqueued build has terminated before BeginDeploy fires
// the driver) + the in-memory container scheduler + event store.

let deployPipelineTests =
    let factory () : IDeployPipeline =
        let eventStore =
            ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

        let orchestrator =
            ToolUp.Platform.JobSchedulerBuildOrchestrator.create (DeployPlaneJobScheduler true) eventStore nullLogger

        ToolUp.Platform.DefaultDeployPipeline.create orchestrator (InMemoryContainerScheduler()) eventStore nullLogger
        :> IDeployPipeline

    IDeployPipelineContract.tests "DefaultDeployPipeline" factory

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

// ─── DockerLocal wire DTOs (always-on, no Docker needed) ─────────────
//
// Pins the serialisability of the Docker API DTOs through the same
// plain-STJ options shape the scheduler builds (PropertyNamingPolicy =
// null — Docker's API is PascalCase — plus case-insensitive reads).
// The DTOs were originally `private`, and System.Text.Json's
// reflection serialiser only sees public property getters and
// constructors: every create call posted `{}` and every inspect /
// list deserialise threw `NotSupportedException`. The defect stayed
// latent because the Docker-backed contract leg above is env-gated on
// a reachable Docker socket; this pack runs everywhere.

let dockerWireDtoTests =
    let jsonOptions =
        System.Text.Json.JsonSerializerOptions(PropertyNamingPolicy = null, PropertyNameCaseInsensitive = true)

    let serialize value =
        System.Text.Json.JsonSerializer.Serialize(value, jsonOptions)

    let deserialize (json: string) : 'T =
        System.Text.Json.JsonSerializer.Deserialize<'T>(json, jsonOptions)

    testList "DockerLocalContainerScheduler — wire DTOs" [
        testCase "DockerCreateRequest serialises its fields PascalCase (not `{}`)"
        <| fun _ ->
            let labels = Dictionary<string, string>()
            labels["tenantId"] <- "tenant-1"
            let exposed = Dictionary<string, obj>()
            exposed["8080/tcp"] <- box (obj ())

            let request: DockerCreateRequest = {
                Image = "alpine:latest"
                Env = [| "A=1" |]
                Labels = labels
                ExposedPorts = exposed
                Healthcheck = null
            }

            use doc = System.Text.Json.JsonDocument.Parse(serialize request)
            let root = doc.RootElement

            Expect.equal (root.GetProperty("Image").GetString()) "alpine:latest" "Image written PascalCase"
            let env0 = root.GetProperty("Env")[0]
            Expect.equal (env0.GetString()) "A=1" "Env written"

            Expect.equal (root.GetProperty("Labels").GetProperty("tenantId").GetString()) "tenant-1" "Labels written"

            let hasExposedPorts, exposedEl = root.TryGetProperty "ExposedPorts"
            Expect.isTrue hasExposedPorts "ExposedPorts written"

            Expect.equal
                (exposedEl.GetProperty("8080/tcp").ValueKind)
                System.Text.Json.JsonValueKind.Object
                "port key maps to an empty object, per the Docker create contract"

        testCase "DockerCreateResponse deserialises a containers/create body"
        <| fun _ ->
            let parsed: DockerCreateResponse = deserialize """{"Id":"abc123","Warnings":[]}"""

            Expect.equal parsed.Id "abc123" "Id read"

        testCase "DockerInspectResponse deserialises nested State + Config"
        <| fun _ ->
            let body =
                """{
  "Id": "abc123",
  "State": { "Status": "exited", "Running": false, "Restarting": false, "Dead": false, "ExitCode": 0,
             "StartedAt": "2024-01-01T00:00:00Z", "FinishedAt": "2024-01-01T00:01:00Z", "Error": "" },
  "Config": { "Image": "alpine:latest", "Labels": { "tenantId": "tenant-1" } }
}"""

            let parsed: DockerInspectResponse = deserialize body
            Expect.equal parsed.Id "abc123" "Id read"
            Expect.equal parsed.State.Status "exited" "State.Status read"
            Expect.equal parsed.Config.Image "alpine:latest" "Config.Image read"
            Expect.equal parsed.Config.Labels["tenantId"] "tenant-1" "Config.Labels read"

        testCase "DockerListItem array deserialises a containers/json body"
        <| fun _ ->
            let body =
                """[{"Id":"abc","Image":"alpine:latest","Labels":{"tenantId":"t1"},"State":"running","Status":"Up 2 minutes"}]"""

            let parsed: DockerListItem[] = deserialize body
            Expect.equal parsed.Length 1 "one item"
            Expect.equal parsed[0].Id "abc" "Id read"
            Expect.equal parsed[0].State "running" "State read"
            Expect.equal parsed[0].Labels["tenantId"] "t1" "Labels read"
    ]
// ─── Phase 185 — PlanDeploy (dry-run) bindings ───────────────────────
//
// Three bindings of the `planTests` pack, deliberately covering both
// dispatch routes of the `PlanDeploy` extension member plus a
// falsifying control:
//
//   1. `DefaultDeployPipeline` — declares `IDeployPlanner`, so the
//      extension routes to its native implementation.
//   2. `InMemoryDeployPipeline` — declares NOTHING new. It is the
//      GP 11 evidence: an implementer written before Phase 185, left
//      byte-for-byte untouched by it, that still answers `PlanDeploy`
//      correctly through the fallback. If adding the plan surface had
//      broken existing implementers, this file would not compile.
//   3. `MutatingDeployPipeline` — applies while planning. The
//      read-only assertion MUST go red for it; `deployPlanMutationCheck`
//      below pins that it does, so the zero-mutation assertions in the
//      two bindings above are known-falsifiable rather than merely
//      passing.

/// A pipeline that mutates during `PlanDeploy` — the control. Stops
/// the first container it observes, then returns the honest diff, so
/// the ONLY thing distinguishing it from a conformant planner is the
/// mutation the probe is supposed to catch.
type MutatingDeployPipeline(scheduler: IContainerScheduler) =
    let inner = InMemoryDeployPipeline() :> IDeployPipeline

    interface IDeployPipeline with
        member _.BeginDeploy(tenantId, buildId, manifest, byUserId) =
            inner.BeginDeploy(tenantId, buildId, manifest, byUserId)

        member _.GetDeployState(deployId) = inner.GetDeployState deployId
        member _.Rollback(tenantId, byUserId) = inner.Rollback(tenantId, byUserId)
        member _.GetDeployHistory(tenantId, count) = inner.GetDeployHistory(tenantId, count)

    interface IDeployPlanner with
        member _.PlanDeploy(tenantId, target) = async {
            let! observed = scheduler.ListContainers(Some tenantId)

            match observed with
            | first :: _ ->
                // The defect the contract pack exists to catch: an
                // "apply" smuggled into the dry-run.
                let! _ = scheduler.StopContainer first.ContainerId
                ()
            | [] -> ()

            return! DeployPlanner.plan scheduler tenantId target
        }

let deployPlanDefaultPipelineTests =
    let binding: IDeployPipelineContract.DeployPlanBinding = {
        Scheduler = fun () -> InMemoryContainerScheduler() :> IContainerScheduler
        Pipeline =
            fun scheduler ->
                let eventStore =
                    ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

                ToolUp.Platform.DefaultDeployPipeline.create
                    (InMemoryBuildOrchestrator())
                    scheduler
                    eventStore
                    nullLogger
                :> IDeployPipeline
    }

    IDeployPipelineContract.planTests "DefaultDeployPipeline (native IDeployPlanner)" binding

let deployPlanFallbackTests =
    let binding: IDeployPipelineContract.DeployPlanBinding = {
        Scheduler = fun () -> InMemoryContainerScheduler() :> IContainerScheduler
        // Ignores the scheduler entirely — exactly what a pre-Phase-185
        // implementer does. The extension member supplies the plan.
        Pipeline = fun _ -> InMemoryDeployPipeline() :> IDeployPipeline
    }

    IDeployPipelineContract.planTests "InMemoryDeployPipeline (unchanged implementer, fallback route)" binding

/// Mutation check for the read-only assertion. A test that only ever
/// sees conformant planners cannot tell "no mutation happened" from
/// "nothing was observed at all"; this drives the same probe with a
/// planner that DOES mutate and asserts the probe reports it.
let deployPlanMutationCheck =
    testList "PlanDeploy read-only assertion — mutation check" [
        testCaseAsync "the recording probe DOES catch a planner that applies while planning"
        <| async {
            let tenant = "tenant-plan-control"

            let recorder =
                IDeployPipelineContract.RecordingContainerScheduler(InMemoryContainerScheduler())

            let scheduler = recorder :> IContainerScheduler

            let spec: ContainerSpec = {
                Image = "img/a:1"
                EnvVars = Map.empty
                ExposedPort = Some 8080
                HealthcheckPath = Some "/health"
                Labels = Map.empty
            }

            let! launched = scheduler.LaunchContainer(tenant, spec)
            Expect.isOk launched "seeding LaunchContainer"
            recorder.Reset()

            let pipeline = MutatingDeployPipeline(scheduler) :> IDeployPipeline
            let! result = pipeline.PlanDeploy(scheduler, tenant, [ spec ])

            // The plan still comes back — the point is that the probe
            // sees the mutation the plan should not have made.
            Expect.isOk result "the control planner still returns a plan"

            Expect.contains
                recorder.MutationCalls
                "StopContainer"
                "the probe reports the smuggled mutation — so the zero-mutation assertion can fail"

            // And a conformant planner over the very same probe leaves
            // it empty, which is the contrast that makes the assertion
            // meaningful.
            recorder.Reset()
            let! _ = DeployPlanner.plan scheduler tenant [ spec ]
            Expect.isEmpty recorder.MutationCalls "the conformant planner leaves the same probe clean"
        }
    ]

/// Unit tests over `DeployPlanner.diff` — the pure classification,
/// exercised without any `IContainerScheduler` at all. Pins the
/// status-to-kind mapping (including the transitional cases a
/// scheduler-backed test cannot easily stage) and the deterministic
/// change ordering.
let deployPlanDiffTests =
    let spec image : ContainerSpec = {
        Image = image
        EnvVars = Map.empty
        ExposedPort = None
        HealthcheckPath = None
        Labels = Map.empty
    }

    let info id image status : ContainerInfo = {
        ContainerId = id
        TenantId = "t"
        ImageRef = image
        Status = status
        Labels = Map.empty
    }

    let at = DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)

    let kindFor (status: ContainerStatus) =
        let plan = DeployPlanner.diff "t" [ spec "img:1" ] [ info "c-1" "img:1" status ] at

        Expect.equal plan.Changes.Length 1 "one line"
        (List.head plan.Changes).Kind

    testList "DeployPlanner.diff — pure classification" [
        testCase "a running container is NoChange"
        <| fun _ -> Expect.equal (kindFor (ContainerRunning at)) PlannedChangeKind.NoChange "running -> NoChange"

        testCase "transitional statuses are NoChange — they converge without intervention"
        <| fun _ ->
            Expect.equal (kindFor ContainerCreated) PlannedChangeKind.NoChange "created -> NoChange"

            Expect.equal (kindFor ContainerRestarting) PlannedChangeKind.NoChange "restarting -> NoChange"

        testCase "not-serving statuses are Restart"
        <| fun _ ->
            Expect.equal (kindFor (ContainerExited(0, at))) PlannedChangeKind.Restart "exited -> Restart"

            Expect.equal (kindFor (ContainerCrashed("oom", at))) PlannedChangeKind.Restart "crashed -> Restart"

            Expect.equal (kindFor ContainerNotFound) PlannedChangeKind.Restart "lost by the scheduler -> Restart"

        testCase "changes are ordered Launch, Restart, Stop, NoChange"
        <| fun _ ->
            let plan =
                DeployPlanner.diff
                    "t"
                    [ spec "img/new:1"; spec "img/dead:1"; spec "img/live:1" ]
                    [
                        info "c-live" "img/live:1" (ContainerRunning at)
                        info "c-dead" "img/dead:1" (ContainerExited(1, at))
                        info "c-surplus" "img/surplus:1" (ContainerRunning at)
                    ]
                    at

            Expect.equal
                (plan.Changes |> List.map _.Kind)
                [
                    PlannedChangeKind.Launch
                    PlannedChangeKind.Restart
                    PlannedChangeKind.Stop
                    PlannedChangeKind.NoChange
                ]
                "mutations first, most-consequential first, unchanged last"

            Expect.equal
                (DeployPlan.summarise plan)
                "1 to launch, 1 to restart, 1 to stop, 1 unchanged"
                "the operator summary counts each kind"

        testCase "duplicate requested images pair against duplicate observed containers"
        <| fun _ ->
            // Two requested, one running: one NoChange + one Launch.
            let plan =
                DeployPlanner.diff "t" [ spec "img:1"; spec "img:1" ] [ info "c-1" "img:1" (ContainerRunning at) ] at

            Expect.equal (DeployPlan.countOf PlannedChangeKind.NoChange plan) 1 "the running one pairs off"

            Expect.equal (DeployPlan.countOf PlannedChangeKind.Launch plan) 1 "the unpaired request launches"

            // One requested, two running: one NoChange + one Stop.
            let plan2 =
                DeployPlanner.diff
                    "t"
                    [ spec "img:1" ]
                    [
                        info "c-1" "img:1" (ContainerRunning at)
                        info "c-2" "img:1" (ContainerRunning at)
                    ]
                    at

            Expect.equal (DeployPlan.countOf PlannedChangeKind.NoChange plan2) 1 "one pairs off"
            Expect.equal (DeployPlan.countOf PlannedChangeKind.Stop plan2) 1 "the surplus container stops"

        testCase "the diff is independent of the order the scheduler enumerated containers in"
        <| fun _ ->
            // `ListContainers` promises only "scheduler-defined"
            // ordering and shipped backends enumerate hash maps, so the
            // same state can arrive in any order. With duplicates on one
            // image, that order must not decide WHICH container is
            // paired and which is surplus — otherwise two plans over
            // identical state disagree about what to stop.
            let observed = [
                info "c-3" "img:1" (ContainerRunning at)
                info "c-1" "img:1" (ContainerRunning at)
                info "c-2" "img:1" (ContainerExited(0, at))
            ]

            let expected = DeployPlanner.diff "t" [ spec "img:1"; spec "img:1" ] observed at

            for permutation in
                [
                    [ observed[2]; observed[0]; observed[1] ]
                    [ observed[1]; observed[2]; observed[0] ]
                    [ observed[2]; observed[1]; observed[0] ]
                ] do
                Expect.equal
                    (DeployPlanner.diff "t" [ spec "img:1"; spec "img:1" ] permutation at)
                    expected
                    "a permuted enumeration yields the identical plan"

            // And the pairing is the id-sorted one: c-1 + c-2 pair off
            // (c-2 exited, so Restart), c-3 is the surplus that stops.
            let stop = expected.Changes |> List.find (fun c -> c.Kind = PlannedChangeKind.Stop)

            Expect.equal stop.ContainerId (Some "c-3") "the highest id is the surplus, not whichever came last"

            Expect.equal
                (DeployPlan.countOf PlannedChangeKind.Restart expected)
                1
                "the exited paired container restarts"

        testCase "ObservedAt is carried verbatim and DeployPlan.empty is a no-op"
        <| fun _ ->
            let plan = DeployPlanner.diff "t" [] [] at
            Expect.equal plan.ObservedAt at "ObservedAt carried through"
            Expect.equal plan (DeployPlan.empty "t" at) "an empty diff equals the empty plan"
            Expect.isTrue (DeployPlan.isNoOp plan) "empty is a no-op"
    ]

// ─── DefaultDeployPipeline.Rollback — build-sourced image recovery ───
//
// Regression pack for the latent Phase 26 defect found during the
// Phase 185 ship: `Rollback` never recovered the pushed artefact ref
// from the target deploy's `DeployPushing` transition (both branches
// of the recovery match returned `None`), so rolling back a deploy
// whose image came from a build — `manifest.Runtime.Image = None` —
// synthesised `local-build:<slug>:<buildId>`, an image no registry
// serves, and the launch failure was logged at Warn and swallowed:
// the caller saw a successful rollback that launched nothing.
//
// The manifests here deliberately carry NO `Runtime.Image` and the
// build's artefact ref differs from the synthetic `local-build:`
// shape, so a passing test can only mean the ref was recovered from
// the event history.

/// `IContainerScheduler` decorator whose `LaunchContainer` can be
/// switched to fail — succeeds while the test seeds deploy history,
/// then fails the rollback's relaunch.
type private FlakyLaunchScheduler(inner: IContainerScheduler) =
    member val FailLaunches = false with get, set

    interface IContainerScheduler with
        member this.LaunchContainer(tenantId, spec) = async {
            if this.FailLaunches then
                return Error(SchedulerUnavailable "registry offline")
            else
                return! inner.LaunchContainer(tenantId, spec)
        }

        member _.StopContainer(containerId) = inner.StopContainer containerId
        member _.RestartContainer(containerId) = inner.RestartContainer containerId
        member _.GetContainerStatus(containerId) = inner.GetContainerStatus containerId
        member _.ListContainers(tenantId) = inner.ListContainers tenantId
        member _.StreamLogs(containerId, fromTime) = inner.StreamLogs(containerId, fromTime)

let defaultPipelineRollbackTests =
    /// Manifest with NO `Runtime.Image` — the deploy's image comes
    /// from the build outcome, so rollback has no manifest fallback.
    let mkBuildManifest slug : DeployManifest = {
        DeployManifest.empty with
            App = {
                Name = slug
                Slug = slug
                Region = "eu-west"
            }
            Runtime = {
                DeployManifest.empty.Runtime with
                    Framework = "dotnet:10"
                    Image = None
            }
    }

    /// Build whose artefact ref is `image` (a `PrebuiltImage` build
    /// source succeeds with the ref verbatim under the substrate
    /// default). Synchronous-dispatch scheduler mode means the build
    /// has terminated when this returns.
    let enqueueBuild (orchestrator: IBuildOrchestrator) (slug: string) (image: string) = async {
        let request: BuildRequest = {
            AppSlug = slug
            Source = PrebuiltImage image
            Manifest = mkBuildManifest slug
            RetryPolicy = BuildRetryPolicy.noRetry
            SubmittedBy = "alice"
            Idempotency = None
        }

        match! orchestrator.EnqueueBuild request with
        | Ok buildId -> return buildId
        | Error e -> return failtestf "EnqueueBuild: %A" e
    }

    /// Poll `GetDeployState` until the deploy reaches a terminal
    /// state (the pipeline driver runs on a background async).
    let awaitTerminal (pipeline: IDeployPipeline) (deployId: DeployId) = async {
        let deadline = DateTime.UtcNow.AddSeconds 30.0
        let mutable terminal: DeploySummary option = None

        while terminal.IsNone && DateTime.UtcNow < deadline do
            match! pipeline.GetDeployState deployId with
            | Ok summary ->
                match summary.State with
                | DeploySucceeded _
                | DeployFailed _
                | DeployRolledBack _ -> terminal <- Some summary
                | _ -> do! Async.Sleep 25
            | Error _ -> do! Async.Sleep 25

        match terminal with
        | Some summary -> return summary
        | None -> return failtestf "deploy %s did not reach a terminal state" deployId
    }

    /// Two succeeded build-sourced deploys for `tenant` (images v1
    /// then v2), so a rollback targets the v1 deploy.
    let seedTwoDeploys
        (pipeline: IDeployPipeline)
        (orchestrator: IBuildOrchestrator)
        (tenant: TenantId)
        (slug: string)
        =
        async {
            let! buildId1 = enqueueBuild orchestrator slug (sprintf "registry/%s:v1" slug)
            let! begun1 = pipeline.BeginDeploy(tenant, buildId1, mkBuildManifest slug, "alice")

            match begun1 with
            | Error e -> return failtestf "BeginDeploy v1: %A" e
            | Ok deployId1 ->
                let! settled1 = awaitTerminal pipeline deployId1

                match settled1.State with
                | DeploySucceeded _ -> ()
                | other -> failtestf "v1 deploy did not succeed: %A" other

                // Distinct StartedAt so the rollback's newest-first scan
                // is deterministic.
                do! Async.Sleep 15

                let! buildId2 = enqueueBuild orchestrator slug (sprintf "registry/%s:v2" slug)
                let! begun2 = pipeline.BeginDeploy(tenant, buildId2, mkBuildManifest slug, "alice")

                match begun2 with
                | Error e -> return failtestf "BeginDeploy v2: %A" e
                | Ok deployId2 ->
                    let! settled2 = awaitTerminal pipeline deployId2

                    match settled2.State with
                    | DeploySucceeded _ -> return ()
                    | other -> return failtestf "v2 deploy did not succeed: %A" other
        }

    let composed (containerScheduler: IContainerScheduler) =
        let eventStore =
            ToolUp.Platform.InMemoryEventStore.InMemoryEventStore() :> IEventStore

        let orchestrator =
            ToolUp.Platform.JobSchedulerBuildOrchestrator.create (DeployPlaneJobScheduler true) eventStore nullLogger
            :> IBuildOrchestrator

        let pipeline =
            ToolUp.Platform.DefaultDeployPipeline.create orchestrator containerScheduler eventStore nullLogger
            :> IDeployPipeline

        orchestrator, pipeline

    testList "DefaultDeployPipeline.Rollback — build-sourced deploys" [

        testCaseAsync "rollback relaunches the artefact ref the target deploy pushed, not a synthetic local-build ref"
        <| async {
            let tenant = "tenant-rollback-recovery"
            let slug = "rollapp"
            let scheduler = InMemoryContainerScheduler() :> IContainerScheduler
            let orchestrator, pipeline = composed scheduler

            do! seedTwoDeploys pipeline orchestrator tenant slug

            let! rolled = pipeline.Rollback(tenant, "operator-1")

            match rolled with
            | Error e -> failtestf "Rollback: expected Ok, got %A" e
            | Ok rollbackDeployId -> Expect.isFalse (String.IsNullOrEmpty rollbackDeployId) "rollback DeployId assigned"

            let! containers = scheduler.ListContainers(Some tenant)

            let imagesOf ref =
                containers |> List.filter (fun c -> c.ImageRef = ref) |> List.length

            Expect.equal
                (imagesOf (sprintf "registry/%s:v1" slug))
                2
                "the v1 image launched twice: the original deploy plus the rollback's recovered relaunch"

            Expect.equal (imagesOf (sprintf "registry/%s:v2" slug)) 1 "the v2 head deploy's container is untouched"

            Expect.isEmpty
                (containers |> List.filter (fun c -> c.ImageRef.StartsWith "local-build:"))
                "no synthetic local-build image was ever launched — the defect this pack regresses"
        }

        testCaseAsync "a rollback whose relaunch fails surfaces Error instead of a healthy-looking Ok"
        <| async {
            let tenant = "tenant-rollback-launchfail"
            let slug = "failapp"
            let flaky = FlakyLaunchScheduler(InMemoryContainerScheduler())
            let orchestrator, pipeline = composed flaky

            do! seedTwoDeploys pipeline orchestrator tenant slug

            let! before = (flaky :> IContainerScheduler).ListContainers(Some tenant)

            flaky.FailLaunches <- true
            let! rolled = pipeline.Rollback(tenant, "operator-1")

            match rolled with
            | Ok id -> failtestf "expected the failed relaunch to surface as Error, got Ok %s" id
            | Error(DeployStorageFailure msg) ->
                Expect.stringContains msg "rollback" "the failure names the rollback step"

                Expect.stringContains
                    msg
                    (sprintf "registry/%s:v1" slug)
                    "the failure names the RECOVERED image — recovery ran even on the failure path"
            | Error other -> failtestf "expected DeployStorageFailure, got %A" other

            let! after = (flaky :> IContainerScheduler).ListContainers(Some tenant)

            Expect.equal after.Length before.Length "the failed rollback launched nothing"
        }
    ]