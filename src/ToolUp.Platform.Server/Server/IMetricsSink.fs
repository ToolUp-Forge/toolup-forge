namespace ToolUp.Platform.Metrics

// ─── IMetricsSink — Phase 9e portable metrics interface ──────────────
//
// Six-rule portability audit (Phase 9c — Guiding Principle 12):
//   1. Identity by value      — Metric `name : string` is the registration
//                                key; tag keys are `string` and tag values
//                                are `string`. No live framework handle on
//                                the surface.
//   2. Async exemption        — Methods are sync `unit`-returning by
//                                deliberate design. Metrics are hot-path —
//                                every request produces 3-5 emissions and
//                                allocating an `Async<unit>` per emission
//                                compounds. Mirrors the documented
//                                exemption on `IAIProvider.SendMessage`'s
//                                `onStream` callback. Sinks have no return
//                                value to await: write-only, fire-and-
//                                forget, all retry/back-pressure lives
//                                inside the implementation.
//   3. Retry as data          — Sinks do not surface retry. A failing
//                                emission (network blip on the OTel
//                                exporter, exporter-queue full) is
//                                swallowed inside the implementation;
//                                operators see degradation through their
//                                own monitor on the export side, not
//                                through SDK back-pressure.
//   4. Stateless boundary     — Each call carries `(name, value, tags)`
//                                in full. The accumulator state inside
//                                the impl (Prometheus series counts,
//                                OTel `Meter` instrument cache) is
//                                implementation detail, not part of the
//                                contract. Distributed companions
//                                (StatsD-style UDP exporter, push-gateway
//                                relay) must work with no in-memory
//                                continuity.
//   5. No cross-shard ordering — Metric points are commutative. The order
//                                in which two `Increment` calls land in
//                                the sink does not affect the total. No
//                                ordering guarantees claimed.
//   6. Precision documented   — Histogram bucket boundaries are floats
//                                in the unit declared on
//                                `MetricDefinition.Unit`. Latency
//                                histograms use milliseconds (matches
//                                Phase 9 `SlowRequestThreshold`).

/// Hot-path emission interface. Implementations must be thread-safe —
/// callers may invoke from request threads, background timers, SSE
/// streams, and DI construction concurrently.
///
/// **Tag-key allowlist.** Tags whose key is not in the
/// `MetricDefinition.Tags` list registered for the metric are silently
/// dropped by the sink. This is the structural defence against
/// caller-side cardinality explosions.
///
/// **Series-count cap.** Each metric also carries a per-metric distinct
/// `(tag-set)` ceiling (default 1 000, configurable per-metric).
/// Combinations beyond the ceiling are routed to a single overflow
/// series tagged `_overflow=true` and the first overflow logs `Warn`
/// once.
type IMetricsSink =
    /// Record one observation against a `Histogram` or `Summary`. For
    /// `Counter` / `Gauge` metrics, callers should use `Increment` /
    /// `SetGauge` instead — implementations may treat `Record` against
    /// a non-histogram metric as a no-op or a `Warn`-level log.
    abstract Record: name: string * value: float * tags: Map<string, string> -> unit

    /// Increment a `Counter` by 1. The bare-tag form is the hot-path
    /// shape — every request emits one or more counter increments and
    /// the API-surface cost matters.
    abstract Increment: name: string * tags: Map<string, string> -> unit

    /// Set a `Gauge` to the absolute value `value`. Replaces any prior
    /// observation for the same `(name, tags)` key. Callers updating a
    /// gauge from a `BackgroundService` tick (queue depth, active
    /// connection count) typically `SetGauge` once per tick rather
    /// than computing deltas.
    abstract SetGauge: name: string * value: float * tags: Map<string, string> -> unit

/// SDK-default no-op implementation of `IMetricsSink`. Registered when
/// `ServerConfig.MetricsEndpoint = Disabled` so emission sites in
/// `compose`-controlled code (request middleware, job scheduler, SSE
/// connection manager) can resolve the service unconditionally —
/// `IMetricsSink` is never `null` in DI when the SDK is composed.
/// Mirrors the `NoOpUsageLog` / `NoOpAuditLog` pattern.
type NoOpMetricsSink() =
    interface IMetricsSink with
        member _.Record(_name, _value, _tags) = ()
        member _.Increment(_name, _tags) = ()
        member _.SetGauge(_name, _value, _tags) = ()