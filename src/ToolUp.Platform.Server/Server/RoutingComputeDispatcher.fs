// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Collections.Concurrent
open System.Threading
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.HealthChecks
open ToolUp.Platform.Metrics

// ─── Phase 484 — the routing dispatcher + the fleet's operator view ──────
//
// `IComputeBackendRegistry.fs` declares what each backend serves and
// decides where a spec belongs. This file is the dispatcher that applies
// that decision, and the three surfaces that make a multi-backend fleet
// legible to an operator: per-backend health, per-backend metrics, and one
// `/dev/inspect` row.
//
// **The router is an `IExternalComputeDispatcher`, so nothing above it
// changes.** A job handler still calls `Submit` and gets a handle; Phase
// 319's reconciliation still polls that handle on a later tick. Fan-out is
// invisible to every caller, which is the whole point — a deployment adds
// a second backend without a single handler edit.
//
// **`Backend` is a label, never a routing key.** The router fronts many
// backends and cannot honestly borrow one of their names, so it reports
// `"routing"`. That value never reaches a handle: `Submit` stamps each
// handle with the *selected registration's* `Kind`, which is what
// `Poll`/`Cancel` route on. This is the opposite choice from Phase 478's
// `ExecutionProfileGate.enforce`, which passes the inner `Backend` through
// — and correctly so, because a single-backend decorator has exactly one
// name it could truthfully use and a handle must poll against a dispatcher
// that answers to it.
//
// **Why `Submit` restamps `ExternalHandle.Backend`.** Phase 318 has the
// backend stamp its own `Backend` label onto the handle it mints. The
// registry, though, keys on `Kind` — deliberately separate, so one
// companion can be composed twice against two clusters. Where the two
// differ, an unstamped handle would carry a name `selectForHandle` cannot
// resolve, and every subsequent poll of a perfectly healthy job would be
// refused. The router therefore owns that one field. In the ordinary case
// (`Kind = Dispatcher.Backend`) the restamp is a no-op; where it is not, it
// is the difference between a routable handle and a lost job.
//
// **The router must NOT be wrapped in `ExecutionProfileGate.enforce`, and
// it deliberately does not implement `IIsolatedComputeBackend`.** A single
// posture for a fan-out router would be a false claim in one direction or
// the other: declare `standardOnly` and the gate refuses every `Isolated`
// spec even though an isolating backend is registered; declare the clauses
// and the gate waves through an `Isolated` spec that may land on a
// non-isolating backend. Not implementing the interface is Phase 478's
// "claiming nothing", which is the honest reading for a router — and the
// enforcement is not lost, it is *stronger*: routing step 1 filters to
// profile-eligible backends before selection, so an `Isolated` spec cannot
// reach a non-isolating backend at all, and a fleet with no isolating
// backend gets a refusal naming that gap rather than a generic posture
// complaint. Composing the gate on top would only add a branch that
// refuses work the router had already placed correctly.
//
// **What the counters do and do not mean.** In-flight is the router's own
// count of submissions it has accepted and not yet observed reach a
// terminal outcome. It is not backend queue depth: only the backend knows
// how much of its queue is ahead of a job, and Phase 318 deliberately does
// not put that on the seam. It is also a floor rather than an exact figure
// — a caller that submits and never polls to terminal leaves its
// submission counted, because the router only learns of completion through
// a `Poll` that passes through it. Stated here because a gauge that
// silently means something narrower than its name is worse than no gauge.

/// Phase 484 — what the router has observed about one backend. Plain
/// counters plus the last refusal text; the payload behind the fleet row
/// and the per-backend health probe.
type ComputeBackendStats = {
    Kind: string
    /// Submissions this backend accepted.
    Submitted: int64
    /// Submissions this backend refused, for any reason.
    Refused: int64
    /// Accepted submissions not yet observed reaching a terminal
    /// outcome. A floor, not an exact figure — see the file header.
    InFlight: int64
    Succeeded: int64
    Failed: int64
    Cancelled: int64
    /// Consecutive **retriable** refusals since the last acceptance. Only
    /// retriable refusals count: `Retriable = true` is Phase 318's way of
    /// saying the backend is the problem (saturation, a lease expiry, an
    /// unreachable endpoint), whereas a terminal refusal is usually the
    /// request's fault — an unknown `Kind`, an unhonourable hint — and a
    /// caller sending malformed work must not be able to mark a perfectly
    /// healthy backend unhealthy. This is the field the health probe
    /// reads.
    ConsecutiveBackendFailures: int64
    /// The most recent refusal message, whatever its retriability.
    LastRefusal: string option
    /// When the router last recorded anything for this backend (UTC).
    LastActivityAt: DateTime option
}

/// Phase 484 — one backend's live counters. `internal` because it is the
/// mutable innards of `ComputeFleetTelemetry`, not a surface a consumer
/// composes against; `ComputeBackendStats` is the immutable read model.
///
/// A class with `let mutable` fields rather than a record, so every write
/// can go through `Interlocked` — a record would need a whole new
/// allocation per increment on the submit path.
type internal ComputeBackendCounters() =
    let mutable submitted = 0L
    let mutable refused = 0L
    let mutable inFlight = 0L
    let mutable succeeded = 0L
    let mutable failed = 0L
    let mutable cancelled = 0L
    let mutable consecutiveBackendFailures = 0L
    let mutable lastRefusal: string option = None
    let mutable lastActivityAt: DateTime option = None

    member _.Submitted = Volatile.Read(&submitted)
    member _.Refused = Volatile.Read(&refused)
    member _.InFlight = Volatile.Read(&inFlight)
    member _.Succeeded = Volatile.Read(&succeeded)
    member _.Failed = Volatile.Read(&failed)
    member _.Cancelled = Volatile.Read(&cancelled)
    member _.ConsecutiveBackendFailures = Volatile.Read(&consecutiveBackendFailures)
    member _.LastRefusal = lastRefusal
    member _.LastActivityAt = lastActivityAt

    member _.Touch() = lastActivityAt <- Some DateTime.UtcNow

    member _.RecordAccepted() =
        Interlocked.Increment(&submitted) |> ignore
        Interlocked.Increment(&inFlight) |> ignore
        Interlocked.Exchange(&consecutiveBackendFailures, 0L) |> ignore

    member _.RecordRefused(retriable: bool, message: string) =
        Interlocked.Increment(&refused) |> ignore

        if retriable then
            Interlocked.Increment(&consecutiveBackendFailures) |> ignore

        lastRefusal <- Some message

    /// Decrement in-flight, floored at zero.
    ///
    /// A compare-and-swap loop rather than `Interlocked.Decrement` plus a
    /// clamp on read: a handle minted before this router was composed can
    /// reach a terminal outcome through it with no matching acceptance, so
    /// over-decrementing is a legitimate boundary rather than a bug, and
    /// clamping only at read would let the underlying field drift
    /// arbitrarily negative — at which point a later genuine acceptance
    /// would be swallowed and the gauge would sit at zero while work was
    /// actually in flight. Flooring the stored value keeps the counter
    /// self-correcting.
    member _.DecrementInFlight() =
        let mutable settled = false

        while not settled do
            let current = Volatile.Read(&inFlight)

            if current <= 0L then
                settled <- true
            elif Interlocked.CompareExchange(&inFlight, current - 1L, current) = current then
                settled <- true

    member _.RecordSucceeded() =
        Interlocked.Increment(&succeeded) |> ignore

    member _.RecordFailed() =
        Interlocked.Increment(&failed) |> ignore

    member _.RecordCancelled() =
        Interlocked.Increment(&cancelled) |> ignore

/// Phase 484 — the router's per-backend observation record. Written by
/// `RoutingComputeDispatcher`, read by the health probes and the
/// `/dev/inspect` contributor.
///
/// **Mutable by design, and the sanctioned kind.** GP 5 makes immutability
/// the default and mutation a documented exception; this is the
/// `IMetricsSink` exception — a hot write-only path on every submit and
/// poll, where allocating a new immutable map per observation would cost
/// more than the thing being measured. Counters are `Interlocked`, the
/// per-backend rows live in a `ConcurrentDictionary`, and `StatsFor` is
/// the only read — so a reader never sees a torn count, though it may see
/// two backends' rows from moments a few microseconds apart. That is the
/// right trade for a diagnostic surface: an operator panel does not need a
/// globally consistent instant, and taking a lock to give it one would put
/// the dev endpoint on the submit path's critical section.
type ComputeFleetTelemetry() =

    let rows = ConcurrentDictionary<string, ComputeBackendCounters>()

    let counters (kind: string) =
        rows.GetOrAdd(kind, (fun _ -> ComputeBackendCounters()))

    /// Record that `kind` accepted a submission. Clears the consecutive
    /// backend-failure run — one acceptance is the evidence that whatever
    /// the previous refusals were about has passed.
    member _.RecordAccepted(kind: string) =
        let row = counters kind
        row.RecordAccepted()
        row.Touch()

    /// Record that `kind` refused a submission. Only a retriable refusal
    /// advances the backend-failure run — see
    /// `ComputeBackendStats.ConsecutiveBackendFailures`.
    member _.RecordRefused(kind: string, error: ExternalComputeError) =
        let row = counters kind
        row.RecordRefused(error.Retriable, ExternalComputeError.describe error)
        row.Touch()

    /// Record that one of `kind`'s handles reached a terminal outcome.
    /// Non-terminal outcomes are not recorded — a poll that reports
    /// `Running` is not an event, and counting it would make the row a
    /// measure of how often the caller polls.
    member _.RecordOutcome(kind: string, outcome: ExternalOutcome) =
        if ExternalOutcome.isTerminal outcome then
            let row = counters kind
            row.DecrementInFlight()

            match outcome with
            | ExternalOutcome.Succeeded _ -> row.RecordSucceeded()
            | ExternalOutcome.Failed _ -> row.RecordFailed()
            | ExternalOutcome.Cancelled -> row.RecordCancelled()
            | ExternalOutcome.Pending
            | ExternalOutcome.Running _ -> ()

            row.Touch()

    /// The current observation for `kind`. A backend the router has never
    /// recorded anything for reads as an all-zero row rather than absent,
    /// so a fleet panel lists every registered backend including the idle
    /// ones — "no submissions yet" and "not registered" are different
    /// answers and an operator needs to tell them apart.
    member _.StatsFor(kind: string) : ComputeBackendStats =
        match rows.TryGetValue kind with
        | false, _ -> {
            Kind = kind
            Submitted = 0L
            Refused = 0L
            InFlight = 0L
            Succeeded = 0L
            Failed = 0L
            Cancelled = 0L
            ConsecutiveBackendFailures = 0L
            LastRefusal = None
            LastActivityAt = None
          }
        | true, row -> {
            Kind = kind
            Submitted = row.Submitted
            Refused = row.Refused
            InFlight = row.InFlight
            Succeeded = row.Succeeded
            Failed = row.Failed
            Cancelled = row.Cancelled
            ConsecutiveBackendFailures = row.ConsecutiveBackendFailures
            LastRefusal = row.LastRefusal
            LastActivityAt = row.LastActivityAt
          }

    /// Every backend the router has observed, keyed by kind. Registered
    /// backends with no traffic are absent here by construction — use
    /// `StatsFor` per registration when the caller needs the full fleet,
    /// as the `/dev/inspect` contributor does.
    member this.Snapshot() : Map<string, ComputeBackendStats> =
        rows.Keys |> Seq.map (fun kind -> kind, this.StatsFor kind) |> Map.ofSeq

/// Phase 484 — metric names the routing dispatcher emits. Declared here
/// rather than in `MetricsMiddleware.StandardMetrics` for the reason
/// `AuditLog` declares its own: that module compiles later than this file.
[<RequireQualifiedAccess>]
module ComputeFleetMetrics =
    /// Counter — submissions the router handed to a backend, tagged
    /// `backend` + `outcome` (`accepted` / `refused`).
    let SubmissionsTotal = "toolup.compute.submissions.total"

    /// Counter — routing refusals, i.e. specs no backend could serve.
    /// Tagged `profile` only: there is no backend to attribute it to,
    /// which is exactly what the metric says.
    let RoutingRefusalsTotal = "toolup.compute.routing.refusals.total"

    /// Gauge — accepted submissions not yet observed terminal, tagged
    /// `backend`. A floor; see the file header.
    let InFlight = "toolup.compute.in_flight"

    /// Counter — handles observed reaching a terminal outcome, tagged
    /// `backend` + `outcome` (`succeeded` / `failed` / `cancelled`).
    let TerminalOutcomesTotal = "toolup.compute.terminal.total"

/// Phase 484 — an `IExternalComputeDispatcher` over a fleet of them.
///
/// `Submit` selects per `ComputeBackendRouting.select` (profile, then hard
/// resource classes, then advisory hint fit, then the declared default)
/// and stamps the chosen kind onto the returned handle. `Poll` and
/// `Cancel` route by that stamp, so a handle always returns to the backend
/// that minted it.
///
/// `metrics` is supplied rather than service-located because the router is
/// registered in DI as an **instance** — Phase 319's hand-off
/// reconciliation introspects the service collection for one and disables
/// itself if it finds only a factory — and an instance cannot resolve from
/// a provider that does not exist yet at registration time. Pass
/// `NoOpMetricsSink()` for a deployment that emits no metrics; it costs
/// nothing (GP 13).
type RoutingComputeDispatcher(registry: ComputeBackendRegistry, telemetry: ComputeFleetTelemetry, metrics: IMetricsSink)
    =

    /// The label the router reports as its own `Backend`. Never stamped
    /// onto a handle — see the file header.
    static member BackendName = "routing"

    /// The fleet this router dispatches over.
    member _.Registry = registry

    /// The router's observation record.
    member _.Telemetry = telemetry

    interface IExternalComputeDispatcher with
        member _.Backend = RoutingComputeDispatcher.BackendName

        member _.Submit(scopeId: string, spec: ExternalWorkSpec) = async {
            match ComputeBackendRouting.select registry spec with
            | Error routingRefusal ->
                metrics.Increment(
                    ComputeFleetMetrics.RoutingRefusalsTotal,
                    Map [ "profile", ExecutionProfile.label spec.Profile ]
                )

                return Error routingRefusal
            | Ok registration ->
                let tags = Map [ "backend", registration.Kind ]
                let! result = registration.Dispatcher.Submit(scopeId, spec)

                match result with
                | Error backendRefusal ->
                    telemetry.RecordRefused(registration.Kind, backendRefusal)

                    metrics.Increment(ComputeFleetMetrics.SubmissionsTotal, tags |> Map.add "outcome" "refused")

                    return Error backendRefusal
                | Ok handle ->
                    telemetry.RecordAccepted registration.Kind

                    metrics.Increment(ComputeFleetMetrics.SubmissionsTotal, tags |> Map.add "outcome" "accepted")

                    metrics.SetGauge(
                        ComputeFleetMetrics.InFlight,
                        float (telemetry.StatsFor registration.Kind).InFlight,
                        tags
                    )

                    // The registry's Kind is the routing key, so the
                    // router owns this field. A no-op when the backend's
                    // own label already matches.
                    return
                        Ok {
                            handle with
                                Backend = registration.Kind
                        }
        }

        member _.Poll(handle: ExternalHandle) = async {
            match ComputeBackendRouting.selectForHandle registry handle with
            | Error unroutable ->
                // Phase 318's contract for a handle the backend does not
                // recognise: a terminal `Failed`, never an exception and
                // never an invented `Cancelled`.
                return ExternalOutcome.Failed unroutable
            | Ok registration ->
                let! outcome = registration.Dispatcher.Poll handle

                if ExternalOutcome.isTerminal outcome then
                    telemetry.RecordOutcome(registration.Kind, outcome)
                    let tags = Map [ "backend", registration.Kind ]

                    metrics.Increment(
                        ComputeFleetMetrics.TerminalOutcomesTotal,
                        tags |> Map.add "outcome" (ExternalOutcome.label outcome)
                    )

                    metrics.SetGauge(
                        ComputeFleetMetrics.InFlight,
                        float (telemetry.StatsFor registration.Kind).InFlight,
                        tags
                    )

                return outcome
        }

        member _.Cancel(handle: ExternalHandle) = async {
            match ComputeBackendRouting.selectForHandle registry handle with
            | Error _ ->
                // `Cancel` returns `unit` and Phase 318 requires it be
                // idempotent and non-throwing, so an unroutable handle
                // can only be a no-op. The refusal is observable through
                // `Poll`, which can carry it; raising here would make
                // cancelling an unknown handle an error, contradicting
                // the contract.
                return ()
            | Ok registration -> return! registration.Dispatcher.Cancel handle
        }

/// Phase 484 — per-backend readiness probe over the router's own
/// observations. One instance per registered backend, named
/// `"external_compute:<kind>"` per `IHealthCheck.Name`'s
/// suffix-the-instance-id rule.
///
/// **What it can and cannot assert.** It deliberately does not submit
/// probe work: `Submit` is a side effect on someone else's cluster and
/// `/ready` is polled on every load-balancer interval, so a probe that
/// submitted would bill the operator for its own health checking. What it
/// reads instead is the router's record of what the backend has actually
/// done with real traffic — an in-memory counter read, comfortably inside
/// the contributor budget.
///
/// It reports on **retriable** refusals only, so a caller submitting an
/// unknown work kind cannot flip a healthy backend to `Unhealthy`. The
/// three-way split is: never refused retriably (or never used) →
/// `Healthy`; refusing now but has accepted work before → `Degraded`, the
/// dependency is reachable-but-misbehaving case `HealthResult.Degraded`
/// exists for; refusing and has never once accepted anything →
/// `Unhealthy`, which is the shape of a backend that was mis-composed
/// rather than one having a bad afternoon.
type ComputeBackendHealthCheck(kind: string, telemetry: ComputeFleetTelemetry) =

    /// The `IHealthCheck.Name` for a backend of this kind.
    static member NameFor(kind: string) = sprintf "external_compute:%s" kind

    interface IHealthCheck with
        member _.Name = ComputeBackendHealthCheck.NameFor kind

        member _.Kind = Readiness

        member _.Timeout = IHealthCheck.defaultTimeout

        member _.Check() = async {
            let stats = telemetry.StatsFor kind

            if stats.ConsecutiveBackendFailures = 0L then
                return Healthy
            else
                let detail =
                    match stats.LastRefusal with
                    | Some message -> message
                    | None -> "no refusal message recorded"

                let summary =
                    sprintf
                        "backend '%s' has refused %d consecutive retriable submission(s); last refusal: %s"
                        kind
                        stats.ConsecutiveBackendFailures
                        detail

                if stats.Submitted > 0L then
                    return Degraded summary
                else
                    return
                        Unhealthy(
                            sprintf
                                "%s. This backend has never accepted a submission, so the refusals are more likely a composition or credential defect than transient saturation."
                                summary
                        )
        }

/// Phase 484 — the compute-fleet row on `/dev/inspect`. One panel listing
/// every registered backend with its declared capabilities beside what the
/// router has observed, so "which backends exist, what do they claim, and
/// which one is actually taking work" is one read rather than three.
///
/// Every **registered** backend appears, including ones with no traffic —
/// an absent row would make an idle backend indistinguishable from an
/// unregistered one.
type ComputeFleetDiagnosticsContributor(registry: ComputeBackendRegistry, telemetry: ComputeFleetTelemetry) =

    interface IDevDiagnosticsContributor with
        member _.Contribute() = async {
            let rows =
                registry.Registrations
                |> List.map (fun registration ->
                    let stats = telemetry.StatsFor registration.Kind

                    {|
                        Kind = registration.Kind
                        BackendLabel = registration.Dispatcher.Backend
                        IsDefault = registration.IsDefault
                        Profiles =
                            ComputeBackendRegistration.supportedProfiles registration
                            |> List.map ExecutionProfile.label
                        IsolationPosture = ComputeBackendRegistration.posture registration |> IsolationPosture.describe
                        ResourceClasses = registration.ResourceClasses |> Set.toList
                        EnvelopeVersions = registration.EnvelopeVersions |> Set.toList
                        Submitted = stats.Submitted
                        Refused = stats.Refused
                        InFlight = stats.InFlight
                        Succeeded = stats.Succeeded
                        Failed = stats.Failed
                        Cancelled = stats.Cancelled
                        ConsecutiveBackendFailures = stats.ConsecutiveBackendFailures
                        LastRefusal = stats.LastRefusal
                        LastActivityAt = stats.LastActivityAt
                    |})

            return
                "Compute fleet",
                box {|
                    Backends = rows
                    DefaultKind = registry.Default |> Option.map _.Kind
                    GeneratedAt = DateTime.UtcNow
                |}
        }

/// Phase 484 — compose a multi-backend compute fleet.
///
/// **Entirely opt-in, and nothing about a single-dispatcher deployment
/// changes (GP 13).** `ComposeStores.registerExternalCompute` is
/// untouched: `NoExternalCompute` still registers
/// `NoExternalComputeDispatcher` and still costs nothing, and a
/// deployment composing one companion dispatcher still gets exactly that
/// dispatcher with no registry, no telemetry, no health probes and no
/// panel in the graph. Every type in this file is constructed only by a
/// call to `register` that a deployment makes on purpose.
[<RequireQualifiedAccess>]
module ComputeFleetCompose =

    /// Register the fleet: the registry, the router as the deployment's
    /// `IExternalComputeDispatcher`, one readiness probe per backend, and
    /// the `/dev/inspect` row.
    ///
    /// Call this **with** `ServerConfig.ExternalCompute =
    /// CustomExternalCompute` — that mode is precisely "the deployment
    /// composed its own dispatcher", which is what this does — and before
    /// `ServerApp.run`, so the router is in the collection when the job
    /// scheduler looks for it.
    ///
    /// The registry is constructed **eagerly, here**, so a duplicate kind
    /// or a second declared default fails at compose with a named
    /// diagnostic rather than at the first submission. The router is
    /// registered as an *instance* for the reason its doc comment gives:
    /// Phase 319's reconciliation introspects for one and silently
    /// disables the hand-off path if it finds only a factory.
    ///
    /// Returns the router so a caller can decorate it or keep a handle on
    /// its telemetry.
    let register
        (services: IServiceCollection)
        (registrations: ComputeBackendRegistration list)
        (metrics: IMetricsSink)
        : RoutingComputeDispatcher =
        let registry = ComputeBackendRegistry registrations
        let telemetry = ComputeFleetTelemetry()
        let router = RoutingComputeDispatcher(registry, telemetry, metrics)

        services.AddSingleton<ComputeBackendRegistry>(registry) |> ignore
        services.AddSingleton<ComputeFleetTelemetry>(telemetry) |> ignore

        services.AddSingleton<IExternalComputeDispatcher>(router :> IExternalComputeDispatcher)
        |> ignore

        for registration in registry.Registrations do
            services.AddSingleton<IHealthCheck>(ComputeBackendHealthCheck(registration.Kind, telemetry) :> IHealthCheck)
            |> ignore

        services.AddSingleton<IDevDiagnosticsContributor>(
            ComputeFleetDiagnosticsContributor(registry, telemetry) :> IDevDiagnosticsContributor
        )
        |> ignore

        router