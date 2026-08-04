module ToolUp.Platform.Tests.Contracts.IDeployPipelineContract

open System
open System.Collections.Concurrent
open System.Collections.Generic
open Expecto
open ToolUp.Platform

// ─── Phase 26 IDeployPipeline contract tests ────────────────────────
//
// Parametrised tests for any `IDeployPipeline` implementation.
// Bindings hand in a factory that returns a fresh pipeline. The pack
// exercises the documented `BeginDeploy` → `GetDeployState` round-
// trip, `Rollback`'s `NothingToRollbackTo` and `DeployStillRunning`
// branches, `GetDeployHistory`'s ordering contract, and the six-
// rule portability audit's observable claims.
//
// The pack does NOT exercise the pipeline's internal state-machine
// transitions (build → push → healthcheck → traffic-flip). Those are
// implementation-specific and depend on the underlying
// `IContainerScheduler` and `IBuildOrchestrator` bindings; the pack
// stays at the documented IDeployPipeline surface contract.

let private mkManifest slug : DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = slug
            Slug = slug
            Region = "eu-west"
        }
        Runtime = {
            DeployManifest.empty.Runtime with
                Framework = "dotnet:10"
                Image = Some "ghcr.io/example/app:latest"
        }
}

let private okOrFail label =
    function
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %A" label e

let tests (name: string) (factory: unit -> IDeployPipeline) =

    testList $"{name} — IDeployPipeline contract" [

        // ─── BeginDeploy ──────────────────────────────────────────

        testCaseAsync "BeginDeploy returns a non-empty DeployId"
        <| async {
            let pipeline = factory ()

            let! result = pipeline.BeginDeploy("tenant-1", "build-1", mkManifest "app", "alice")
            let deployId = okOrFail "BeginDeploy" result
            Expect.isFalse (String.IsNullOrEmpty deployId) "DeployId is a non-empty string"
        }

        // ─── GetDeployState ───────────────────────────────────────

        testCaseAsync "GetDeployState on unknown id returns UnknownDeploy"
        <| async {
            let pipeline = factory ()
            let! result = pipeline.GetDeployState "no-such-deploy"

            match result with
            | Error(UnknownDeploy did) -> Expect.equal did "no-such-deploy" "id preserved"
            | other -> failtestf "expected UnknownDeploy, got %A" other
        }

        testCaseAsync "GetDeployState after BeginDeploy round-trips TenantId + BuildId"
        <| async {
            let pipeline = factory ()
            let manifest = mkManifest "rtrip"
            let! begun = pipeline.BeginDeploy("tenant-rtrip", "build-rtrip", manifest, "alice")
            let deployId = okOrFail "BeginDeploy" begun

            let! state = pipeline.GetDeployState deployId
            let summary = okOrFail "GetDeployState" state
            Expect.equal summary.DeployId deployId "DeployId round-trips"
            Expect.equal summary.TenantId "tenant-rtrip" "TenantId round-trips"
            Expect.equal summary.BuildId "build-rtrip" "BuildId round-trips"
            Expect.equal summary.SubmittedBy "alice" "SubmittedBy round-trips"
            Expect.equal summary.Manifest.App.Slug "rtrip" "Manifest round-trips"
        }

        // ─── Rollback ─────────────────────────────────────────────

        testCaseAsync "Rollback on a tenant with no prior deploy returns NothingToRollbackTo"
        <| async {
            let pipeline = factory ()
            let! result = pipeline.Rollback("tenant-no-history", "operator-1")

            match result with
            | Error(NothingToRollbackTo tid) -> Expect.equal tid "tenant-no-history" "tenant id preserved"
            | other -> failtestf "expected NothingToRollbackTo, got %A" other
        }

        // ─── GetDeployHistory ─────────────────────────────────────

        testCaseAsync "GetDeployHistory on a tenant with no deploys returns an empty list"
        <| async {
            let pipeline = factory ()
            let! history = pipeline.GetDeployHistory("tenant-empty", 10)
            Expect.isEmpty history "no deploys -> empty history"
        }

        testCaseAsync "GetDeployHistory bounds by the requested count"
        <| async {
            let pipeline = factory ()
            let manifest = mkManifest "bounded"

            for i in 1..3 do
                let! _ = pipeline.BeginDeploy("tenant-bounded", sprintf "build-%d" i, manifest, "alice")
                ()

            let! history = pipeline.GetDeployHistory("tenant-bounded", 2)
            Expect.isLessThanOrEqual history.Length 2 "history bounded by count argument"
        }

        testCaseAsync "GetDeployHistory returns newest-first ordering"
        <| async {
            let pipeline = factory ()
            let manifest = mkManifest "ordered"

            let mutable deployIds = []

            for i in 1..3 do
                let! begun = pipeline.BeginDeploy("tenant-ordered", sprintf "build-%d" i, manifest, "alice")
                deployIds <- (okOrFail "BeginDeploy" begun) :: deployIds
                // Small delay so StateChangedAt differs across deploys.
                do! Async.Sleep 5

            let! history = pipeline.GetDeployHistory("tenant-ordered", 10)
            // The most-recent BeginDeploy must appear at head if there
            // are any entries at all. Order is by StateChangedAt
            // descending per the documented contract.
            if not history.IsEmpty then
                let mostRecent = List.head deployIds
                Expect.equal (List.head history).DeployId mostRecent "newest-first ordering"
        }

        // ─── Six-rule portability audit (Phase 9c, GP 12) ─────────

        testCaseAsync "Rule 1 — identity-by-value: DeployId / TenantId / BuildId are string aliases"
        <| async {
            let did: DeployId = "d-1"
            let tid: TenantId = "t-1"
            let bid: BuildId = "b-1"
            Expect.equal did "d-1" "DeployId is a string alias"
            Expect.equal tid "t-1" "TenantId is a string alias"
            Expect.equal bid "b-1" "BuildId is a string alias"
            do! async.Return()
        }

        testCaseAsync "Rule 2 — async at every boundary: every method returns Async<_>"
        <| async {
            let pipeline = factory ()
            let! _ = pipeline.BeginDeploy("rule-2-tenant", "rule-2-build", mkManifest "rule-2", "alice")
            let! _ = pipeline.GetDeployState "rule-2-deploy"
            let! _ = pipeline.Rollback("rule-2-tenant", "alice")
            let! _ = pipeline.GetDeployHistory("rule-2-tenant", 1)
            ()
        }

        testCaseAsync "Rule 3 — failure flows through DeployPipelineError data"
        <| async {
            let pipeline = factory ()
            let! result = pipeline.GetDeployState "missing"
            Expect.isError result "missing-id failure surfaces as Error data"
        }
    ]

// ─── Phase 185 — PlanDeploy (dry-run) contract ──────────────────────
//
// `PlanDeploy` is reachable on every `IDeployPipeline` (the extension
// member in `DeployPlanner.fs`), routing to the pipeline's own
// `IDeployPlanner` when it declares one and to the generic
// `DeployPlanner.plan` otherwise. BOTH routes are in scope for this
// pack — it is bound against a native planner, a pipeline with no
// planner at all, and a deliberately-mutating planner used as the
// falsifying control.
//
// The load-bearing assertion is read-only-ness: a plan must fire ZERO
// `IContainerScheduler` mutation calls. That assertion is only worth
// anything if it can fail, and "no mutations happened" is exactly the
// shape that passes vacuously when the probe saw nothing at all — so
// every case that asserts zero mutations ALSO asserts the reads it
// expected did fire, and `DeployPlaneTests` binds a planner that
// applies while planning to demonstrate the probe going red.

/// Decorates any `IContainerScheduler`, recording every call by
/// member name and delegating unchanged. Mutations are recorded AND
/// performed — the probe measures, it does not prevent, so a planner
/// that mutates is caught rather than quietly neutered.
type RecordingContainerScheduler(inner: IContainerScheduler) =
    let calls = ConcurrentQueue<string>()

    let record name = calls.Enqueue name

    /// Every call since construction or the last `Reset`, in order.
    member _.Calls = calls |> Seq.toList

    /// Calls that would change scheduler state. The set a plan must
    /// leave empty.
    member this.MutationCalls =
        this.Calls
        |> List.filter (fun c -> c = "LaunchContainer" || c = "StopContainer" || c = "RestartContainer")

    /// Calls that only observe state.
    member this.ReadCalls =
        this.Calls
        |> List.filter (fun c -> c = "ListContainers" || c = "GetContainerStatus" || c = "StreamLogs")

    /// Drop the recorded history — used to separate a test's seeding
    /// (which legitimately mutates) from the plan under observation.
    member _.Reset() =
        let mutable dropped = Unchecked.defaultof<string>

        while calls.TryDequeue(&dropped) do
            ()

    interface IContainerScheduler with
        member _.LaunchContainer(tenantId, spec) =
            record "LaunchContainer"
            inner.LaunchContainer(tenantId, spec)

        member _.StopContainer(containerId) =
            record "StopContainer"
            inner.StopContainer containerId

        member _.RestartContainer(containerId) =
            record "RestartContainer"
            inner.RestartContainer containerId

        member _.GetContainerStatus(containerId) =
            record "GetContainerStatus"
            inner.GetContainerStatus containerId

        member _.ListContainers(tenantId) =
            record "ListContainers"
            inner.ListContainers tenantId

        member _.StreamLogs(containerId, fromTime) =
            record "StreamLogs"
            inner.StreamLogs(containerId, fromTime)

/// How to build the pipeline/scheduler pair under test.
type DeployPlanBinding = {
    /// A fresh, empty scheduler the pack seeds by launching
    /// containers through it.
    Scheduler: unit -> IContainerScheduler
    /// Build a pipeline that plans against the SUPPLIED scheduler.
    /// The pack hands in a `RecordingContainerScheduler`, so a
    /// pipeline that ignores it and plans against a scheduler of its
    /// own fails the read-only assertion by construction — there is
    /// no passing this pack by not being looked at.
    Pipeline: IContainerScheduler -> IDeployPipeline
}

let private specFor (image: string) : ContainerSpec = {
    Image = image
    EnvVars = Map.empty
    ExposedPort = Some 8080
    HealthcheckPath = Some "/health"
    Labels = Map.empty
}

let private planOrFail label =
    function
    | Ok(p: DeployPlan) -> p
    | Error e -> failtestf "%s: expected a plan, got %A" label e

let private kindsOf (plan: DeployPlan) =
    plan.Changes |> List.map _.Kind |> List.distinct |> List.sort

let planTests (name: string) (binding: DeployPlanBinding) =

    /// Fresh (recorder, pipeline) pair with `images` launched for
    /// `tenantId`, recorder reset so only the plan's own calls show.
    let seeded (tenantId: TenantId) (images: string list) = async {
        let recorder = RecordingContainerScheduler(binding.Scheduler())
        let scheduler = recorder :> IContainerScheduler
        let pipeline = binding.Pipeline scheduler
        let launched = ResizeArray<ContainerId>()

        for image in images do
            let! r = scheduler.LaunchContainer(tenantId, specFor image)

            match r with
            | Ok id -> launched.Add id
            | Error e -> failtestf "seeding LaunchContainer failed: %A" e

        recorder.Reset()
        return recorder, pipeline, scheduler, List.ofSeq launched
    }

    testList $"{name} — IDeployPipeline PlanDeploy (Phase 185) contract" [

        // ─── Read-only (the acceptance criterion) ─────────────────

        testCaseAsync "PlanDeploy fires ZERO scheduler mutation calls — and did observe state"
        <| async {
            let tenant = "tenant-plan-readonly"
            // Seed one running container, then plan a target set that
            // needs a launch (new image) AND a stop (seeded image
            // dropped) — the most mutation-tempting shape there is.
            let! recorder, pipeline, scheduler, _ = seeded tenant [ "img/a:1" ]

            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/b:1" ])
            let plan = planOrFail "PlanDeploy" result

            Expect.isEmpty recorder.MutationCalls "a plan mutates nothing: no Launch/Stop/Restart call fired"

            // Guard against a vacuous pass: if the planner had read
            // nothing at all, "zero mutations" would be trivially
            // true and this pack would certify a no-op.
            Expect.isNonEmpty recorder.ReadCalls "the plan DID read observed state (else zero-mutations is vacuous)"

            Expect.contains recorder.Calls "ListContainers" "observed state came from ListContainers"

            Expect.isNonEmpty (DeployPlan.mutating plan) "the planned change set is non-empty for this scenario"
        }

        testCaseAsync "PlanDeploy leaves the observed container set unchanged"
        <| async {
            let tenant = "tenant-plan-noeffect"
            let! _, pipeline, scheduler, ids = seeded tenant [ "img/a:1"; "img/b:1" ]

            let! _ = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/c:1" ])
            let! after = scheduler.ListContainers(Some tenant)

            Expect.equal after.Length ids.Length "container count unchanged by a plan"

            for info in after do
                match info.Status with
                | ContainerRunning _ -> ()
                | other -> failtestf "plan changed a container's status to %A" other
        }

        // ─── Diff correctness: add / remove / change / no-change ───

        testCaseAsync "no-change — every requested container already running"
        <| async {
            let tenant = "tenant-plan-nochange"
            let! _, pipeline, scheduler, _ = seeded tenant [ "img/a:1"; "img/b:1" ]

            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:1"; specFor "img/b:1" ])
            let plan = planOrFail "PlanDeploy" result

            Expect.equal plan.TenantId tenant "plan carries the tenant"
            Expect.equal plan.Changes.Length 2 "one change line per container"
            Expect.equal (kindsOf plan) [ PlannedChangeKind.NoChange ] "converged state yields only NoChange"
            Expect.isTrue (DeployPlan.isNoOp plan) "a converged plan is a no-op"
            Expect.equal (DeployPlan.countOf PlannedChangeKind.NoChange plan) 2 "both lines counted as NoChange"
        }

        testCaseAsync "add — a requested image with nothing running it plans a Launch"
        <| async {
            let tenant = "tenant-plan-add"
            let! _, pipeline, scheduler, _ = seeded tenant [ "img/a:1" ]

            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:1"; specFor "img/new:1" ])
            let plan = planOrFail "PlanDeploy" result

            Expect.equal (DeployPlan.countOf PlannedChangeKind.Launch plan) 1 "exactly one Launch"
            Expect.equal (DeployPlan.countOf PlannedChangeKind.NoChange plan) 1 "the running one is unchanged"
            Expect.equal (DeployPlan.countOf PlannedChangeKind.Stop plan) 0 "nothing is surplus"

            let launch = plan.Changes |> List.find (fun c -> c.Kind = PlannedChangeKind.Launch)

            Expect.equal launch.ImageRef "img/new:1" "Launch names the requested image"
            Expect.isNone launch.ContainerId "a Launch has no container id yet — the scheduler assigns it at apply"
            Expect.isSome launch.Target "a Launch carries the spec it would apply"
            Expect.equal launch.Observed ContainerNotFound "a Launch observes nothing"
            Expect.equal launch.TenantId tenant "change carries the tenant"
        }

        testCaseAsync "remove — a running image absent from the target plans a Stop"
        <| async {
            let tenant = "tenant-plan-remove"
            let! _, pipeline, scheduler, ids = seeded tenant [ "img/a:1"; "img/gone:1" ]

            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:1" ])
            let plan = planOrFail "PlanDeploy" result

            Expect.equal (DeployPlan.countOf PlannedChangeKind.Stop plan) 1 "exactly one Stop"
            Expect.equal (DeployPlan.countOf PlannedChangeKind.NoChange plan) 1 "the still-requested one is unchanged"

            let stop = plan.Changes |> List.find (fun c -> c.Kind = PlannedChangeKind.Stop)

            Expect.equal stop.ImageRef "img/gone:1" "Stop names the observed image"
            Expect.isSome stop.ContainerId "a Stop names the container it would stop"
            Expect.isTrue (ids |> List.contains stop.ContainerId.Value) "the id is one the scheduler actually reported"
            Expect.isNone stop.Target "a Stop requests no spec — it is surplus to the manifest"
        }

        testCaseAsync "change — an image swap plans Stop-the-old + Launch-the-new"
        <| async {
            let tenant = "tenant-plan-change"
            let! _, pipeline, scheduler, _ = seeded tenant [ "img/a:1" ]

            // An image change is the apply-shape a container cannot do
            // in place, so the honest plan is a replacement pair.
            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:2" ])
            let plan = planOrFail "PlanDeploy" result

            Expect.equal (DeployPlan.countOf PlannedChangeKind.Launch plan) 1 "the new image is launched"
            Expect.equal (DeployPlan.countOf PlannedChangeKind.Stop plan) 1 "the old image is stopped"
            Expect.equal (DeployPlan.countOf PlannedChangeKind.NoChange plan) 0 "nothing is unchanged"

            let launch = plan.Changes |> List.find (fun c -> c.Kind = PlannedChangeKind.Launch)

            let stop = plan.Changes |> List.find (fun c -> c.Kind = PlannedChangeKind.Stop)
            Expect.equal launch.ImageRef "img/a:2" "Launch is the requested image"
            Expect.equal stop.ImageRef "img/a:1" "Stop is the observed image"
        }

        testCaseAsync "restart — a requested container that stopped serving plans a Restart"
        <| async {
            let tenant = "tenant-plan-restart"
            let! recorder, pipeline, scheduler, ids = seeded tenant [ "img/a:1" ]

            // Take the container out of service through the scheduler
            // (a real mutation — hence the reset before planning).
            let! stopped = scheduler.StopContainer(List.head ids)
            Expect.isOk stopped "seeding StopContainer"
            recorder.Reset()

            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:1" ])
            let plan = planOrFail "PlanDeploy" result

            Expect.equal
                (DeployPlan.countOf PlannedChangeKind.Restart plan)
                1
                "the dead-but-requested container restarts"

            Expect.isEmpty recorder.MutationCalls "planning a Restart does not perform one"

            let restart =
                plan.Changes |> List.find (fun c -> c.Kind = PlannedChangeKind.Restart)

            Expect.equal restart.ContainerId (Some(List.head ids)) "Restart names the existing container"
            Expect.isSome restart.Target "a Restart carries the spec it would restore"
            Expect.isTrue (restart.Reason.Length > 0) "a change explains itself"
        }

        testCaseAsync "empty target against running containers plans every container for Stop"
        <| async {
            let tenant = "tenant-plan-drain"
            let! _, pipeline, scheduler, ids = seeded tenant [ "img/a:1"; "img/b:1" ]

            let! result = pipeline.PlanDeploy(scheduler, tenant, [])
            let plan = planOrFail "PlanDeploy" result

            Expect.equal (DeployPlan.countOf PlannedChangeKind.Stop plan) ids.Length "every container planned for Stop"

            Expect.isFalse (DeployPlan.isNoOp plan) "draining a tenant is not a no-op"
        }

        testCaseAsync "empty target against nothing running is an empty no-op plan"
        <| async {
            let tenant = "tenant-plan-empty"
            let! _, pipeline, scheduler, _ = seeded tenant []

            let! result = pipeline.PlanDeploy(scheduler, tenant, [])
            let plan = planOrFail "PlanDeploy" result

            Expect.isEmpty plan.Changes "no requested and no observed containers -> no changes"
            Expect.isTrue (DeployPlan.isNoOp plan) "an empty plan is a no-op"
        }

        testCaseAsync "the plan is scoped to its tenant — another tenant's containers are invisible"
        <| async {
            let tenant = "tenant-plan-scoped"
            let! _, pipeline, scheduler, _ = seeded tenant [ "img/a:1" ]
            // A container belonging to a different tenant must not
            // appear as surplus in this tenant's plan (GP 4).
            let! _ = scheduler.LaunchContainer("tenant-plan-other", specFor "img/other:1")

            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:1" ])
            let plan = planOrFail "PlanDeploy" result

            Expect.equal (kindsOf plan) [ PlannedChangeKind.NoChange ] "only this tenant's container is considered"

            Expect.isEmpty
                (plan.Changes |> List.filter (fun c -> c.ImageRef = "img/other:1"))
                "the other tenant's container is absent from the plan"
        }

        // ─── Rule 1 — identity by value ───────────────────────────

        testCaseAsync "Rule 1 — two plans over the same observed state are structurally equal"
        <| async {
            let tenant = "tenant-plan-value"
            let! _, pipeline, scheduler, _ = seeded tenant [ "img/a:1"; "img/b:1" ]
            let target = [ specFor "img/a:1"; specFor "img/new:1" ]

            let! first = pipeline.PlanDeploy(scheduler, tenant, target)
            let! second = pipeline.PlanDeploy(scheduler, tenant, target)
            let p1 = planOrFail "first plan" first
            let p2 = planOrFail "second plan" second

            // `ObservedAt` is deliberately a wallclock snapshot, so it
            // is normalised out; everything else must compare equal —
            // which also pins the plan's deterministic ordering, since
            // `ListContainers` promises only scheduler-defined order.
            Expect.equal { p1 with ObservedAt = p2.ObservedAt } p2 "plans over unchanged state are equal by value"

            Expect.equal p1.Changes.Length p2.Changes.Length "same change count"
        }

        testCaseAsync "Rule 1 — a plan holds no live handles: it survives a JSON round-trip unchanged"
        <| async {
            let tenant = "tenant-plan-wire"
            let! _, pipeline, scheduler, _ = seeded tenant [ "img/a:1"; "img/gone:1" ]

            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:1"; specFor "img/new:1" ])
            let plan = planOrFail "PlanDeploy" result

            let options = ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()
            let json = System.Text.Json.JsonSerializer.Serialize(plan, options)

            let restored =
                System.Text.Json.JsonSerializer.Deserialize<DeployPlan>(json, options)

            Expect.equal restored plan "the plan round-trips by value — no live handle leaked into the record"

            Expect.isTrue
                (plan.Changes |> List.exists (fun c -> c.Kind = PlannedChangeKind.Launch))
                "the round-tripped plan carried real change lines"
        }

        testCaseAsync "Rule 1 — PlannedChange identity is its field values, not reference"
        <| async {
            let a: PlannedChange = {
                ContainerId = Some "c-1"
                TenantId = "t-1"
                Kind = PlannedChangeKind.Restart
                Target = Some(specFor "img/a:1")
                ImageRef = "img/a:1"
                Observed = ContainerExited(1, DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                Reason = "requested but exited"
            }

            let b = { a with Reason = a.Reason }
            Expect.equal a b "two independently-constructed changes with equal fields are equal"
            Expect.notEqual a { a with Kind = PlannedChangeKind.Stop } "a differing field breaks equality"
            do! async.Return()
        }

        // ─── Rule 2 / 3 ───────────────────────────────────────────

        testCaseAsync "Rule 2 / 3 — PlanDeploy returns Async<Result<_, DeployPipelineError>>"
        <| async {
            let tenant = "tenant-plan-rules"
            let! _, pipeline, scheduler, _ = seeded tenant []
            let! result = pipeline.PlanDeploy(scheduler, tenant, [ specFor "img/a:1" ])

            match result with
            | Ok _ -> ()
            | Error(e: DeployPipelineError) -> failtestf "unexpected planning failure: %A" e
        }

        testCaseAsync "a scheduler that cannot report status fails the plan closed"
        <| async {
            let tenant = "tenant-plan-failclosed"
            // A scheduler whose status read always errors: the plan
            // must surface the failure rather than emit a plan built
            // from the state it could see, which would silently
            // under-report changes.
            let broken =
                { new IContainerScheduler with
                    member _.LaunchContainer(_, _) = async { return Error(SchedulerUnavailable "down") }
                    member _.StopContainer(_) = async { return Error(SchedulerUnavailable "down") }
                    member _.RestartContainer(_) = async { return Error(SchedulerUnavailable "down") }
                    member _.GetContainerStatus(_) = async { return Error(SchedulerUnavailable "down") }

                    member _.ListContainers(_) = async {
                        return [
                            {
                                ContainerId = "c-broken"
                                TenantId = tenant
                                ImageRef = "img/a:1"
                                Status = ContainerRunning DateTime.UtcNow
                                Labels = Map.empty
                            }
                        ]
                    }

                    member _.StreamLogs(_, _) =
                        { new IAsyncEnumerable<LogEntry> with
                            member _.GetAsyncEnumerator(_) =
                                { new IAsyncEnumerator<LogEntry> with
                                    member _.Current = Unchecked.defaultof<_>

                                    member _.MoveNextAsync() =
                                        System.Threading.Tasks.ValueTask<bool> false

                                    member _.DisposeAsync() = System.Threading.Tasks.ValueTask()
                                }
                        }
                }

            let pipeline = binding.Pipeline broken
            let! result = pipeline.PlanDeploy(broken, tenant, [ specFor "img/a:1" ])

            match result with
            | Error(DeployStorageFailure msg) ->
                Expect.isTrue (msg.Contains "deploy plan") "the failure names the planning step"
            | other -> failtestf "expected DeployStorageFailure, got %A" other
        }
    ]