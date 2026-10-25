module ToolUp.Platform.Tests.InProcess.HealthCheckAggregatorTests

open System
open System.Threading
open System.Threading.Tasks
open Expecto
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open ToolUp.Platform.HealthChecks

// ─── Phase 9k aggregator + adapter tests ─────────────────────────────
//
// Covers behaviour the contract pack can't: BCL adapter timeout
// enforcement, exception → Unhealthy translation, the HTTP status
// mapping (Healthy/Degraded → 200, Unhealthy → 503), the per-Kind tag
// partitioning, and the deferred resolution of factory and
// type-registered probes (Phase 1064).

/// Build an `IHealthCheck` from a concrete `Check` thunk so tests can
/// inject Healthy / Degraded / Unhealthy / throwing / hanging
/// behaviour without scaffolding a full impl per case.
let private mkProbe (name: string) (kind: HealthKind) (timeout: TimeSpan) (body: unit -> Async<HealthResult>) =
    { new IHealthCheck with
        member _.Name = name
        member _.Kind = kind
        member _.Timeout = timeout
        member _.Check() = body ()
    }

/// A probe with a parameterless constructor, for the type-registered
/// (`AddSingleton<IHealthCheck, T>()`) form.
type TypedProbe() =
    static member ProbeName = "typed_probe"

    interface IHealthCheck with
        member _.Name = TypedProbe.ProbeName
        member _.Kind = Liveness
        member _.Timeout = TimeSpan.FromSeconds 1.0
        member _.Check() = async { return Healthy }

/// Build a `BclHealthCheckAdapter` for a probe and run its
/// `CheckHealthAsync` to inspect the BCL-side result. The adapter is
/// `internal` to the SDK module, so the test reaches it via the
/// aggregator's BCL registration: register the probe, build a service
/// provider, resolve the BCL `IHealthCheck`, and invoke it.
let private runThroughAdapter (probe: IHealthCheck) : Async<HealthCheckResult> = async {
    let services = ServiceCollection()
    services.AddSingleton<IHealthCheck>(probe) |> ignore
    ToolUp.Platform.HealthCheckAggregator.register services

    let sp = services.BuildServiceProvider()

    let registrations =
        sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>().Value.Registrations

    let registration = registrations |> Seq.find (fun r -> r.Name = probe.Name)

    // The adapter is registered as a HealthCheckRegistration with an
    // instance-based factory. Resolve it by invoking the factory
    // directly with the live ServiceProvider.
    let bclCheck = registration.Factory.Invoke sp

    let context = HealthCheckContext(Registration = registration)

    let! result = bclCheck.CheckHealthAsync(context, CancellationToken.None) |> Async.AwaitTask
    return result
}

let tests =
    testList "HealthCheckAggregator" [
        testCaseAsync "Adapter translates Healthy to BCL Healthy"
        <| async {
            let probe =
                mkProbe "h" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Healthy })

            let! result = runThroughAdapter probe
            Expect.equal result.Status HealthStatus.Healthy "Healthy → BCL.Healthy"
        }

        testCaseAsync "Adapter translates Degraded to BCL Degraded with message"
        <| async {
            let probe =
                mkProbe "d" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Degraded "slow" })

            let! result = runThroughAdapter probe
            Expect.equal result.Status HealthStatus.Degraded "Degraded → BCL.Degraded"
            Expect.stringContains result.Description "slow" "message preserved"
        }

        testCaseAsync "Adapter translates Unhealthy to BCL Unhealthy with message"
        <| async {
            let probe =
                mkProbe "u" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Unhealthy "broken" })

            let! result = runThroughAdapter probe
            Expect.equal result.Status HealthStatus.Unhealthy "Unhealthy → BCL.Unhealthy"
            Expect.stringContains result.Description "broken" "message preserved"
        }

        testCaseAsync "Adapter wraps thrown exceptions as Unhealthy with 'probe threw:' prefix"
        <| async {
            let probe =
                mkProbe "x" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    failwith "boom"
                    return Healthy
                })

            let! result = runThroughAdapter probe
            Expect.equal result.Status HealthStatus.Unhealthy "exception → Unhealthy"

            Expect.stringStarts
                result.Description
                "probe threw:"
                "exception message prefixed so operators can distinguish from clean Unhealthy"

            Expect.stringContains result.Description "boom" "exception message included"
        }

        testCaseAsync "Adapter reports timeout as Degraded (does not flip /ready to 503)"
        <| async {
            let probe =
                mkProbe "t" Readiness (TimeSpan.FromMilliseconds 100.0) (fun () -> async {
                    do! Async.Sleep 5000
                    return Healthy
                })

            let stopwatch = System.Diagnostics.Stopwatch.StartNew()
            let! result = runThroughAdapter probe
            stopwatch.Stop()

            Expect.equal result.Status HealthStatus.Degraded "timeout → Degraded (not Unhealthy)"
            Expect.stringContains result.Description "exceeded" "message describes timeout"

            // Adapter should cancel before the underlying operation
            // completes (5000ms). Generous upper bound here — the
            // contract is "doesn't block on the underlying op," not a
            // tight latency target. CI scheduler jitter under heavy
            // build load can make a 2s bound flake; 4s still proves
            // cancellation happened.
            Expect.isLessThan
                (stopwatch.Elapsed.TotalMilliseconds)
                4000.0
                "adapter cancelled the long-running probe rather than blocking on it"
        }

        testCaseAsync "Adapter truncates very long exception messages to 500 chars"
        <| async {
            let longMessage = String.replicate 1000 "x"

            let probe =
                mkProbe "long" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async {
                    failwith longMessage
                    return Healthy
                })

            let! result = runThroughAdapter probe

            // "probe threw: " is 13 chars; 500 char cap on the message
            // → total ≤ 513.
            Expect.isLessThanOrEqual
                result.Description.Length
                513
                "long exception message truncated so unauthenticated /ready cannot leak full content"
        }

        testCase "Aggregator registers each IHealthCheck instance with Kind tag"
        <| fun _ ->
            let livenessProbe =
                mkProbe "liveness_one" Liveness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Healthy })

            let readinessProbe =
                mkProbe "readiness_one" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Healthy })

            let services = ServiceCollection()
            services.AddSingleton<IHealthCheck>(livenessProbe) |> ignore
            services.AddSingleton<IHealthCheck>(readinessProbe) |> ignore
            ToolUp.Platform.HealthCheckAggregator.register services

            let sp = services.BuildServiceProvider()

            let registrations =
                sp
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
                    .Value.Registrations
                |> List.ofSeq

            let liveness = registrations |> List.tryFind (fun r -> r.Name = "liveness_one")

            let readiness = registrations |> List.tryFind (fun r -> r.Name = "readiness_one")

            Expect.isSome liveness "liveness probe was registered with BCL"
            Expect.isSome readiness "readiness probe was registered with BCL"
            Expect.contains liveness.Value.Tags "Liveness" "liveness probe tagged Liveness"
            Expect.contains readiness.Value.Tags "Readiness" "readiness probe tagged Readiness"

        testCase "Aggregator resolves a type-registered IHealthCheck from the built provider (Phase 1064)"
        <| fun _ ->
            // Until Phase 1064 the type-based form (AddSingleton<IHealthCheck, T>())
            // was refused at compose: Name/Kind/Timeout cannot be read without a
            // provider, and building a separate one would create divergent
            // singletons. Resolving from the BUILT provider has neither problem,
            // so it takes the same deferred path as a factory registration.
            let services = ServiceCollection()
            services.AddSingleton<IHealthCheck, TypedProbe>() |> ignore
            ToolUp.Platform.HealthCheckAggregator.register services

            let sp = services.BuildServiceProvider()

            let registration =
                sp
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
                    .Value.Registrations
                |> Seq.tryFind (fun r -> r.Name = TypedProbe.ProbeName)

            Expect.isSome registration "the type-registered probe reaches the BCL pipeline"
            Expect.contains registration.Value.Tags "Liveness" "tagged by its Kind"

        testCase "Aggregator returns success when no probes are registered (GP 13 — lightweight default)"
        <| fun _ ->
            // GP 13 contract: a deployment that imports zero companions
            // must not pay any cost. The aggregator must return without
            // throwing when invoked against a service collection with no
            // IHealthCheck registrations. /ready will then return 200
            // vacuously through the BCL pipeline (a higher-level
            // integration test, not exercised here).
            let services = ServiceCollection()
            ToolUp.Platform.HealthCheckAggregator.register services
            // No assertion — reaching this line means the call did not throw.
            ()

        testCase "Snapshot returns name/kind/timeout per registered probe"
        <| fun _ ->
            let probeA =
                mkProbe "a" Readiness (TimeSpan.FromSeconds 5.0) (fun () -> async { return Healthy })

            let probeB =
                mkProbe "b" Liveness (TimeSpan.FromMilliseconds 500.0) (fun () -> async { return Healthy })

            let services = ServiceCollection()
            services.AddSingleton<IHealthCheck>(probeA) |> ignore
            services.AddSingleton<IHealthCheck>(probeB) |> ignore

            let snapshot = ToolUp.Platform.HealthCheckAggregator.snapshot services

            Expect.equal snapshot.Length 2 "both probes captured in snapshot"
            Expect.contains snapshot ("a", Readiness, TimeSpan.FromSeconds 5.0) "probe a captured"
            Expect.contains snapshot ("b", Liveness, TimeSpan.FromMilliseconds 500.0) "probe b captured"

        // Phase 1064 — defect 4. `registerBackupCoordinator` registers the
        // `backup_restore_drill` probe by FACTORY, and the aggregator used to
        // `failwithf` on it at compose, so a `BackupEnabled` deployment could
        // not boot. A factory probe is now resolved from the BUILT provider —
        // the runtime container's own singleton, so there is no divergent
        // instance — beside the instance probes, which register exactly as
        // before.
        testCase "Aggregator resolves a factory-registered IHealthCheck from the built provider (Phase 1064)"
        <| fun _ ->
            let constructed = ref 0

            let services = ServiceCollection()

            services.AddSingleton<IHealthCheck>(
                Func<IServiceProvider, IHealthCheck>(fun _ ->
                    Interlocked.Increment(&constructed.contents) |> ignore

                    mkProbe "factory_probe" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Healthy }))
            )
            |> ignore

            services.AddSingleton<IHealthCheck>(
                mkProbe "instance_probe" Liveness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Healthy })
            )
            |> ignore

            ToolUp.Platform.HealthCheckAggregator.register services

            let sp = services.BuildServiceProvider()

            let registrations =
                sp
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>()
                    .Value.Registrations
                |> List.ofSeq

            let factory = registrations |> List.tryFind (fun r -> r.Name = "factory_probe")
            let instance = registrations |> List.tryFind (fun r -> r.Name = "instance_probe")

            Expect.isSome factory "the factory probe reaches the BCL pipeline"
            Expect.isSome instance "the instance probe still reaches it"
            Expect.contains factory.Value.Tags "Readiness" "the factory probe is tagged by its Kind"

            let bcl = factory.Value.Factory.Invoke sp

            let result =
                bcl.CheckHealthAsync(HealthCheckContext(Registration = factory.Value), CancellationToken.None)
                |> Async.AwaitTask
                |> Async.RunSynchronously

            Expect.equal result.Status HealthStatus.Healthy "the factory probe runs through the adapter"

            let containerCopy =
                sp.GetServices<IHealthCheck>() |> Seq.find (fun c -> c.Name = "factory_probe")

            Expect.equal containerCopy.Name "factory_probe" "the container resolves the same probe"
            Expect.equal constructed.Value 1 "constructed once: the runtime container's singleton, not a second copy"

        testCase "A factory probe that throws fails host start, not the first /ready poll (Phase 1064)"
        <| fun _ ->
            // The deferred probes resolve when the BCL health-check service
            // is constructed, and `AddHealthChecks` registers the publisher
            // hosted service that constructs it — so resolving the hosted
            // services, which host start does, is where a bad factory fails.
            let hostedServicesOf (factory: IServiceProvider -> IHealthCheck) =
                let services = ServiceCollection()
                services.AddLogging() |> ignore

                services.AddSingleton<IHealthCheck>(Func<IServiceProvider, IHealthCheck>(factory))
                |> ignore

                ToolUp.Platform.HealthCheckAggregator.register services
                let sp = services.BuildServiceProvider()

                sp.GetServices<Microsoft.Extensions.Hosting.IHostedService>()
                |> List.ofSeq
                |> ignore

            hostedServicesOf (fun _ ->
                mkProbe "fine_probe" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Healthy }))

            Expect.throws
                (fun () -> hostedServicesOf (fun _ -> failwith "Phase 1064 fake: the probe's dependency is missing"))
                "a throwing factory surfaces when the host starts its services"

        testCase "Snapshot refuses a factory-registered probe rather than silently skipping it (Phase 1064)"
        <| fun _ ->
            let services = ServiceCollection()

            services.AddSingleton<IHealthCheck>(
                Func<IServiceProvider, IHealthCheck>(fun _ ->
                    mkProbe "factory_probe" Readiness (TimeSpan.FromSeconds 1.0) (fun () -> async { return Healthy }))
            )
            |> ignore

            Expect.throws
                (fun () -> ToolUp.Platform.HealthCheckAggregator.snapshot services |> ignore)
                "a compose-time snapshot cannot name a factory probe, and says so instead of omitting it"
    ]