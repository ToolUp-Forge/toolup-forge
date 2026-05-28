module ToolUp.Platform.HealthCheckAggregator

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Diagnostics.HealthChecks
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
// **Companion contract**: registrations must use
// `AddSingleton<IHealthCheck>(instance)`. The aggregator reads
// `desc.ImplementationInstance` to inspect `Name` / `Kind` / `Timeout`
// at registration time. Constructor-injected impls
// (`AddSingleton<IHealthCheck, T>()`) cannot be inspected without
// building a separate `IServiceProvider`, which would create different
// singleton instances than the runtime container — failing loudly is
// safer than silently registering a divergent probe.

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

/// Walk `services` for every `AddSingleton<IHealthCheck>(instance)`
/// registration and feed each into BCL's `AddHealthChecks` builder.
/// Idempotent only by accident — call once, near end of compose, after
/// all companions have contributed.
let register (services: IServiceCollection) : unit =
    let builder = services.AddHealthChecks()

    let descriptors =
        services
        |> Seq.filter (fun d -> d.ServiceType = typeof<IHealthCheck>)
        |> List.ofSeq

    for desc in descriptors do
        let instance =
            match desc.ImplementationInstance with
            | :? IHealthCheck as c -> c
            | _ ->
                let implTypeName =
                    if isNull desc.ImplementationType then
                        "<unknown>"
                    else
                        desc.ImplementationType.FullName

                failwithf
                    "IHealthCheck must be registered as an instance via services.AddSingleton<IHealthCheck>(instance). Descriptor for implementation type %s uses a factory or constructor-injected pattern that the aggregator cannot introspect at compose time. See TECHNICAL_GUIDE.md 'Companion authoring an IHealthCheck'."
                    implTypeName

        let registration =
            HealthCheckRegistration(
                instance.Name,
                (BclHealthCheckAdapter(instance) :> Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck),
                Nullable HealthStatus.Unhealthy,
                [ HealthKind.toString instance.Kind ]
            )

        builder.Add(registration) |> ignore

/// Snapshot every registered `IHealthCheck` for the dev diagnostics
/// endpoint. Captured at compose time alongside the BCL registration so
/// `/dev/inspect` can run the same probes without going through BCL's
/// aggregator. Returns `(name, kind, timeout)` triples — the dev
/// handler invokes `IServiceProvider.GetServices<IHealthCheck>()` at
/// request time to actually run them.
let snapshot (services: IServiceCollection) : (string * HealthKind * TimeSpan) list =
    services
    |> Seq.choose (fun d ->
        if d.ServiceType = typeof<IHealthCheck> then
            match d.ImplementationInstance with
            | :? IHealthCheck as c -> Some(c.Name, c.Kind, c.Timeout)
            | _ -> None
        else
            None)
    |> List.ofSeq