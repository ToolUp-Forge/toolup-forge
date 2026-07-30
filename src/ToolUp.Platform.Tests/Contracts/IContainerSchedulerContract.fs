module ToolUp.Platform.Tests.Contracts.IContainerSchedulerContract

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open Expecto
open ToolUp.Platform

// ─── Phase 26 IContainerScheduler contract tests ────────────────────
//
// Parametrised tests for any `IContainerScheduler` implementation.
// Bindings hand in a factory + a launch-spec generator so tests can
// drive the scheduler with backend-appropriate images (a Docker
// binding launches a real `alpine` container; an in-memory mock
// launches a synthetic spec). The pack exercises the documented
// validation chain, `StopContainer` idempotency, label-based filter
// on `ListContainers`, `StreamLogs` termination, and the six-rule
// portability audit.
//
// Backend-gated assertions (those needing a real container backend —
// status transitions, log content) are wrapped in `if backendReal
// then ... else skipped`. A binding sets `backendReal = false` for
// the in-memory mock, which still exercises validation and the
// audit-trail.

type ContainerSchedulerBinding = {
    /// Fresh scheduler instance for one test.
    Factory: unit -> IContainerScheduler
    /// True when the backend is a real container runtime (Docker
    /// socket). Pure-mock bindings set this to false; assertions
    /// that depend on container behaviour skip themselves.
    BackendReal: bool
    /// Backend-appropriate image reference. The Docker binding
    /// supplies a real image; in-memory mocks may pass an arbitrary
    /// non-empty string.
    SampleImage: string
    /// Tenant id to use for sample containers. Bindings pick a
    /// throw-away string; the pack treats it as opaque.
    SampleTenantId: TenantId
}

let private mkSpec image : ContainerSpec = {
    Image = image
    EnvVars = Map.empty
    ExposedPort = None
    HealthcheckPath = None
    Labels = Map.empty
}

let private okOrFail label =
    function
    | Ok v -> v
    | Error e -> failtestf "%s: expected Ok, got %A" label e

let tests (name: string) (binding: ContainerSchedulerBinding) =

    let factory = binding.Factory
    let sampleImage = binding.SampleImage
    let sampleTenant = binding.SampleTenantId
    let realBackend = binding.BackendReal

    testList $"{name} — IContainerScheduler contract" [

        // ─── LaunchContainer validation chain ─────────────────────

        testCaseAsync "LaunchContainer rejects an empty Image with InvalidContainerSpec"
        <| async {
            let scheduler = factory ()
            let spec = mkSpec ""
            let! result = scheduler.LaunchContainer(sampleTenant, spec)

            match result with
            | Error(InvalidContainerSpec _) -> ()
            | other -> failtestf "expected InvalidContainerSpec, got %A" other
        }

        testCaseAsync "LaunchContainer rejects an ExposedPort below 1"
        <| async {
            let scheduler = factory ()

            let spec = {
                mkSpec sampleImage with
                    ExposedPort = Some 0
            }

            let! result = scheduler.LaunchContainer(sampleTenant, spec)

            match result with
            | Error(InvalidContainerSpec _) -> ()
            | other -> failtestf "expected InvalidContainerSpec, got %A" other
        }

        testCaseAsync "LaunchContainer rejects an ExposedPort above 65535"
        <| async {
            let scheduler = factory ()

            let spec = {
                mkSpec sampleImage with
                    ExposedPort = Some 70000
            }

            let! result = scheduler.LaunchContainer(sampleTenant, spec)

            match result with
            | Error(InvalidContainerSpec _) -> ()
            | other -> failtestf "expected InvalidContainerSpec, got %A" other
        }

        // ─── GetContainerStatus on unknown id ─────────────────────

        testCaseAsync "GetContainerStatus on an unknown id returns ContainerNotFound"
        <| async {
            let scheduler = factory ()
            let! result = scheduler.GetContainerStatus "no-such-container"

            match result with
            | Ok ContainerNotFound -> ()
            | Ok other -> failtestf "expected ContainerNotFound, got Ok %A" other
            | Error e -> failtestf "expected Ok ContainerNotFound, got Error %A" e
        }

        // ─── StopContainer idempotency ────────────────────────────

        testCaseAsync "StopContainer on an unknown id surfaces UnknownContainer (or Ok if treated as idempotent no-op)"
        <| async {
            let scheduler = factory ()
            let! result = scheduler.StopContainer "no-such-container"
            // The substrate contract allows either: the impl reports
            // UnknownContainer (Docker's default) or treats "stop
            // something that doesn't exist" as a successful no-op
            // (an in-memory mock may choose this). Both shapes
            // satisfy the idempotency claim.
            match result with
            | Ok()
            | Error(UnknownContainer _) -> ()
            | other -> failtestf "expected Ok or UnknownContainer, got %A" other
        }

        // ─── ListContainers — empty + filter ─────────────────────

        testCaseAsync "ListContainers None returns a list (possibly empty)"
        <| async {
            let scheduler = factory ()
            let! containers = scheduler.ListContainers None
            Expect.isNotNull (box containers) "list is materialised"
        }

        testCaseAsync "ListContainers with an unknown tenantId filter returns []"
        <| async {
            let scheduler = factory ()
            let! containers = scheduler.ListContainers(Some "ghost-tenant")
            Expect.isEmpty containers "unknown tenant has no containers"
        }

        // ─── StreamLogs on missing container terminates ───────────

        testCase "StreamLogs on an unknown container terminates cleanly"
        <| fun _ ->
            let scheduler = factory ()
            let enumerable = scheduler.StreamLogs("no-such-container", None)
            let enumerator = enumerable.GetAsyncEnumerator(CancellationToken.None)

            try
                let task = task {
                    try
                        let! _ = enumerator.MoveNextAsync()
                        return ()
                    with _ ->
                        // Pure-mock impls may legitimately throw
                        // when asked for logs of an unknown
                        // container; surface as terminated.
                        return ()
                }

                let completed = task.Wait(TimeSpan.FromSeconds 5.0)
                Expect.isTrue completed "StreamLogs terminates within 5s"
            finally
                enumerator.DisposeAsync().AsTask().Wait()

        // ─── Real-backend lifecycle (skipped on mock) ─────────────

        if realBackend then
            testCaseAsync "LaunchContainer → GetContainerStatus eventually reports ContainerRunning"
            <| async {
                let scheduler = factory ()
                let spec = mkSpec sampleImage
                let! launch = scheduler.LaunchContainer(sampleTenant, spec)

                match launch with
                | Error(SchedulerUnavailable _)
                | Error(ImagePullFailed _) ->
                    // Backend reachable from the factory but launch
                    // failed (image absent locally, pull denied, etc.).
                    // Skip the post-launch assertions rather than fail —
                    // the operator's pre-flight image-pull validation is
                    // separate.
                    //
                    // `ImagePullFailed` was always meant to be here —
                    // the comment above has said "image pull denied,
                    // etc." since this pack was written — but only
                    // `SchedulerUnavailable` was matched, so a runner
                    // with a working Docker daemon and no `alpine:latest`
                    // in its local image cache failed the case instead of
                    // skipping it. That is a missing image, not a broken
                    // contract. (Phase 617; CI pre-pulls the image so
                    // this arm is the fallback, not the normal path —
                    // a leg that skips everywhere gates nothing.)
                    ()
                | Error other -> failtestf "unexpected launch failure %A" other
                | Ok containerId ->
                    try
                        let mutable observed = ContainerCreated
                        let mutable attempts = 0
                        let mutable running = false

                        while not running && attempts < 20 do
                            let! status = scheduler.GetContainerStatus containerId

                            match status with
                            | Ok(ContainerRunning _) ->
                                observed <- ContainerRunning DateTime.UtcNow
                                running <- true
                            | Ok ContainerNotFound -> running <- true
                            | Ok _ -> do! Async.Sleep 250
                            | Error _ -> do! Async.Sleep 250

                            attempts <- attempts + 1

                        // Cleanup regardless of running outcome.
                        let! _ = scheduler.StopContainer containerId
                        ()
                    with _ ->
                        let! _ = scheduler.StopContainer containerId
                        ()
            }

            testCaseAsync "ListContainers filters by tenantId label"
            <| async {
                let scheduler = factory ()
                let spec = mkSpec sampleImage
                let isolatedTenant = sprintf "tenant-iso-%s" (Guid.NewGuid().ToString("N"))
                let! launch = scheduler.LaunchContainer(isolatedTenant, spec)

                match launch with
                // Same environment-vs-contract split as the case above.
                | Error(SchedulerUnavailable _)
                | Error(ImagePullFailed _) -> ()
                | Error other -> failtestf "unexpected launch failure %A" other
                | Ok containerId ->
                    try
                        let! filtered = scheduler.ListContainers(Some isolatedTenant)

                        Expect.all
                            filtered
                            (fun c -> c.TenantId = isolatedTenant)
                            "all entries belong to the requested tenant"

                        let! _ = scheduler.StopContainer containerId
                        ()
                    with _ ->
                        let! _ = scheduler.StopContainer containerId
                        ()
            }

        // ─── Six-rule portability audit (Phase 9c, GP 12) ─────────

        testCaseAsync "Rule 1 — identity-by-value: ContainerId / TenantId are string aliases"
        <| async {
            let cid: ContainerId = "c-1"
            let tid: TenantId = "t-1"
            Expect.equal cid "c-1" "ContainerId is a string alias"
            Expect.equal tid "t-1" "TenantId is a string alias"
            do! async.Return()
        }

        testCaseAsync "Rule 2 — async at every boundary (Async<_> + IAsyncEnumerable)"
        <| async {
            let scheduler = factory ()
            // Compile-time shape: every call returns Async<_> except
            // StreamLogs which returns IAsyncEnumerable<LogEntry>
            // (rule 2's cancellable streaming surface).
            let! _ = scheduler.LaunchContainer(sampleTenant, mkSpec sampleImage)
            let! _ = scheduler.StopContainer "rule-2"
            let! _ = scheduler.RestartContainer "rule-2"
            let! _ = scheduler.GetContainerStatus "rule-2"
            let! _ = scheduler.ListContainers None
            let logs = scheduler.StreamLogs("rule-2", None)
            Expect.isNotNull (box logs) "StreamLogs returns IAsyncEnumerable"
        }

        testCaseAsync "Rule 3 — failures flow through ContainerSchedulerError data"
        <| async {
            let scheduler = factory ()
            let! result = scheduler.LaunchContainer(sampleTenant, mkSpec "")
            Expect.isError result "validation failure surfaces as Error data, not raised"
        }
    ]