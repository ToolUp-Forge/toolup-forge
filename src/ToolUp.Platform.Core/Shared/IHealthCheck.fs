// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.HealthChecks

open System

// ─── IHealthCheck — extensibility ────────────────────────────────────
//
// Portable health-check interface for the SDK. Companions implement
// this and self-register via `services.AddSingleton<IHealthCheck>(...)`
// in their `.Server.props` injection; the SDK aggregator
// (`HealthCheckAggregator.register`, called near end-of-compose) walks
// the service collection and wires each registered instance into ASP.NET
// Core's `MapHealthChecks` pipeline, tagging by `Kind` so `/health` and
// `/ready` partition correctly.
//
// Six-rule portability audit (the portability contract):
//   1. Identity by value      — `Name : string` is the registration key.
//                                 No live framework handle on the surface.
//   2. Async                  — `Check : unit -> Async<HealthResult>`.
//                                 No sync escape hatch.
//   3. Retry as data          — no built-in retry. Probes are polled by
//                                 the orchestrator (Kubernetes
//                                 `failureThreshold`, AWS ALB
//                                 `HealthyThresholdCount`); retry is the
//                                 orchestrator's concern, not the SDK's.
//   4. Stateless handlers     — every `Check ()` invocation must read
//                                 fresh dependency state. No assumption of
//                                 cached / in-memory continuity between
//                                 calls — Orleans / Akka.Persistence
//                                 implementations may deactivate or
//                                 restart between probes.
//   5. No cross-shard ordering — probes are independent; the aggregator
//                                 runs them in parallel and reports
//                                 per-probe outcomes. No probe-to-probe
//                                 ordering claim.
//   6. Precision at the lower bound — `Timeout : TimeSpan` is the
//                                 contract; the aggregator caps each
//                                 probe at this duration. Per-probe (not
//                                 per-aggregator) so a Redis PING can use
//                                 200ms while an OIDC discovery fetch can
//                                 use 10s.

/// Three-valued probe outcome. The aggregator translates these to BCL
/// `HealthCheckResult` cases when wiring into ASP.NET Core. `Degraded`
/// is a deliberate halfway state — the dependency is reachable but
/// behaving abnormally (slow, partially failing, falling back to a
/// secondary). It does NOT trip `/ready` to 503; only `Unhealthy` does.
type HealthResult =
    | Healthy
    | Degraded of message: string
    | Unhealthy of message: string

/// Whether the probe answers "is the process up at all?" (Liveness) or
/// "can it serve real traffic?" (Readiness). `/health` runs only
/// Liveness probes (so a dependency outage does not flip /health and
/// trigger orchestrator restarts); `/ready` runs both (a failing
/// liveness probe implies readiness is also broken).
type HealthKind =
    | Liveness
    | Readiness

/// A health probe contributed by a companion or the SDK itself.
/// Implementations should be inexpensive — `/ready` is polled on every
/// load-balancer health-check interval, so anything that touches
/// expensive remote resources or writes platform state will compound at
/// scale. Prefer cheap reads (PING, EXISTS, configuration presence) over
/// full round-trip exercises.
type IHealthCheck =
    /// Stable identifier used as the BCL registration name and as the
    /// key in the JSON response. Must be unique across all registered
    /// probes — companions that may register more than one instance
    /// (e.g. multiple AI providers) should suffix the instance id, e.g.
    /// `"ai_provider:claude"` and `"ai_provider:openai"`.
    abstract member Name: string

    /// Whether this probe contributes to liveness or readiness. See
    /// `HealthKind` doc for partitioning semantics.
    abstract member Kind: HealthKind

    /// Maximum wallclock the aggregator allows for a single `Check`
    /// invocation. On expiry the probe is reported `Degraded` with
    /// message `"probe exceeded {ms}ms"` — it does NOT escalate to
    /// `Unhealthy` (the dependency may simply be slow). Use
    /// `IHealthCheck.defaultTimeout` (5s) unless the probe has
    /// well-characterised performance.
    abstract member Timeout: TimeSpan

    /// Run the probe. Implementations must be safe to invoke
    /// concurrently — the aggregator may call multiple probes in
    /// parallel during `/ready` aggregation, and the same probe may be
    /// invoked again before a previous call completes if a slow probe
    /// has not yet returned.
    abstract member Check: unit -> Async<HealthResult>

module HealthKind =
    let toString =
        function
        | Liveness -> "Liveness"
        | Readiness -> "Readiness"

module HealthResult =
    /// String form of the variant — used in JSON response and dev
    /// diagnostics. Stable wire format: external monitors may key off
    /// these strings.
    let status =
        function
        | Healthy -> "Healthy"
        | Degraded _ -> "Degraded"
        | Unhealthy _ -> "Unhealthy"

    let message =
        function
        | Healthy -> ""
        | Degraded m -> m
        | Unhealthy m -> m

module IHealthCheck =
    /// Default per-probe timeout. Picked to match the recommended
    /// ceiling (5s). Well-characterised probes should declare a tighter
    /// bound — Redis PING in a healthy network finishes in single-digit
    /// ms, and a 5s ceiling there hides real latency under the timeout.
    let defaultTimeout = TimeSpan.FromSeconds 5.0