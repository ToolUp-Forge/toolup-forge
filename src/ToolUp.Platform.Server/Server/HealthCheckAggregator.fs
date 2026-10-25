module ToolUp.Platform.HealthCheckAggregator

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
open Microsoft.Extensions.Options
open ToolUp.Platform.HealthChecks

// ─── Phase 9k aggregator + adapter ───────────────────────────────────
//
// The adapter wraps one ToolUp `IHealthCheck` as a BCL `IHealthCheck`,
// enforcing the per-probe `Timeout` and translating outcomes. The
// aggregator walks the `IServiceCollection` for `IHealthCheck`
// descriptors and registers each as a BCL `HealthCheckRegistration`,
// tagged with the probe's `Kind` so `MapHealthChecks` predicates can
// partition `/health` (Liveness only) from `/ready` (both kinds).
//
// **Registration timing**: must be called near the END of `compose`,
// after every companion has had a chance to call
// `services.AddSingleton<IHealthCheck>(...)`. Calling it earlier means
// later registrations are silently ignored — they exist as DI services
// but never feed into BCL's `MapHealthChecks` aggregation.
//
// **Companion contract**: register a probe by instance
// (`AddSingleton<IHealthCheck>(instance)`), by factory, or by type. An
// instance is inspected (`Name` / `Kind` / `Timeout`) and registered at
// compose; a factory or type registration has no name until a provider
// builds it, so it is resolved from the BUILT provider — the runtime
// container's own singleton, never a second copy — when the health-check
// options are materialised (Phase 1064; until then the aggregator refused
// both, which made `Backup = BackupEnabled` unbootable).

/// Truncate a free-text message to a safe length so the unauthenticated
/// `/ready` JSON cannot leak large stack traces or internal detail.
let private truncate (max: int) (s: string) =
    if isNull s then ""
    elif s.Length <= max then s
    else s.Substring(0, max)

/// Adapts one ToolUp `IHealthCheck` into the BCL interface. Caches the
/// underlying probe so the aggregator can construct the adapter once
/// and reuse it across every `/ready` poll.
type internal BclHealthCheckAdapter(check: IHealthCheck) =
    interface Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck with
        member _.CheckHealthAsync(_context, cancellationToken) = task {
            use cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            cts.CancelAfter(check.Timeout)

            try
                let probeTask = Async.StartImmediateAsTask(check.Check(), cts.Token)
                let! result = probeTask

                return
                    match result with
                    | Healthy -> HealthCheckResult.Healthy "Healthy"
                    | Degraded msg -> HealthCheckResult.Degraded(truncate 500 msg)
                    | Unhealthy msg -> HealthCheckResult.Unhealthy(truncate 500 msg)
            with
            | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                // The host did not request cancellation, so the timeout
                // is ours. Report Degraded — a slow dependency may be
                // recovering and we don't want to flap /ready to 503.
                let ms = int check.Timeout.TotalMilliseconds
                return HealthCheckResult.Degraded(sprintf "probe exceeded %dms" ms)
            | :? OperationCanceledException ->
                // Host shutdown — let the BCL infrastructure handle it.
                return HealthCheckResult.Unhealthy "host cancelled probe"
            | ex ->
                // The probe threw rather than returning Unhealthy. The
                // "probe threw: " prefix lets operators distinguish a
                // clean Unhealthy signal from a probe-implementation bug.
                let msg = "probe threw: " + truncate 500 ex.Message
                return HealthCheckResult.Unhealthy msg
        }

/// The BCL registration for one ToolUp probe: the adapter, `Unhealthy`
/// as the failure status, and the probe's `Kind` as its one tag.
let private registrationFor (check: IHealthCheck) =
    HealthCheckRegistration(
        check.Name,
        (BclHealthCheckAdapter(check) :> Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck),
        Nullable HealthStatus.Unhealthy,
        [ HealthKind.toString check.Kind ]
    )

/// The `IHealthCheck` descriptors `register` feeds into the BCL pipeline,
/// in registration order. Keyed descriptors are skipped: they are not part
/// of `GetServices<IHealthCheck>()`, so counting one would misalign the
/// positions `register` defers.
let private healthCheckDescriptors (services: IServiceCollection) =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<IHealthCheck> && not d.IsKeyedService)
    |> List.ofSeq

/// Walk `services` for every `IHealthCheck` registration and feed each into
/// BCL's `AddHealthChecks` builder. Call once, near end of compose, after
/// all companions have contributed.
///
/// An INSTANCE registration (`AddSingleton<IHealthCheck>(instance)`) is
/// registered here, at compose, exactly as it always was. A FACTORY or
/// TYPE registration (Phase 1064 — `registerBackupCoordinator`'s restore
/// drill is the platform's own) cannot be inspected without a provider, so
/// an options configurator registered here resolves it from the BUILT
/// provider: the runtime container's own singleton, never a second copy.
/// That runs at host start, not at the first `/ready` poll — the BCL
/// health-check service validates its registrations on construction, and
/// `AddHealthChecks` registers the publisher hosted service that constructs
/// it — so a factory that throws fails the boot. A collection with no such
/// registration adds nothing (GP 11).
let register (services: IServiceCollection) : unit =
    let builder = services.AddHealthChecks()
    let descriptors = healthCheckDescriptors services

    for desc in descriptors do
        match desc.ImplementationInstance with
        | :? IHealthCheck as instance -> builder.Add(registrationFor instance) |> ignore
        | _ -> ()

    // The positions, among the probes the built provider returns from
    // `GetServices<IHealthCheck>()`, of every factory / type registration.
    let deferred =
        descriptors
        |> List.indexed
        |> List.choose (fun (i, d) ->
            match d.ImplementationInstance with
            | :? IHealthCheck -> None
            | _ -> Some i)

    if not deferred.IsEmpty then
        // `IOptions<>` itself: with no instance probe, nothing above has
        // configured the options, so nothing has registered it.
        services.AddOptions() |> ignore

        services.AddSingleton<IConfigureOptions<HealthCheckServiceOptions>>(fun (sp: IServiceProvider) ->
            let resolved = sp.GetServices<IHealthCheck>() |> Array.ofSeq

            ConfigureOptions<HealthCheckServiceOptions>(fun options ->
                for i in deferred do
                    options.Registrations.Add(registrationFor resolved[i]))
            :> IConfigureOptions<HealthCheckServiceOptions>)
        |> ignore

/// Snapshot every registered `IHealthCheck` as `(name, kind, timeout)`
/// triples, read from the instance registrations at compose time.
///
/// A factory or type-registered probe has no name until a provider builds
/// it, so a compose-time snapshot cannot describe one; it raises naming the
/// implementation rather than silently returning a partial list (Phase 1064).
/// Resolve `IServiceProvider.GetServices<IHealthCheck>()` instead when the
/// collection may hold one.
let snapshot (services: IServiceCollection) : (string * HealthKind * TimeSpan) list =
    healthCheckDescriptors services
    |> List.map (fun d ->
        match d.ImplementationInstance with
        | :? IHealthCheck as c -> c.Name, c.Kind, c.Timeout
        | _ ->
            let implementation =
                if not (isNull d.ImplementationType) then
                    d.ImplementationType.FullName
                elif not (isNull d.ImplementationFactory) then
                    "<factory>"
                else
                    "<unknown>"

            invalidOp
                $"HealthCheckAggregator.snapshot: the IHealthCheck registered as %s{implementation} has no name until a provider builds it, so a compose-time snapshot cannot describe it. Resolve IServiceProvider.GetServices<IHealthCheck>() instead.")