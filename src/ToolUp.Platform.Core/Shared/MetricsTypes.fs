// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Metrics

// ─── Shared metrics types ────────────────────────────────────────────
//
// Pure data types describing metric definitions and registrations. Lives
// in Shared because future module-scoped metric declarations (server-
// side `ServerModule.withMetrics`) and client-side telemetry contracts
// reference the same shapes. No dependencies on ASP.NET Core, Giraffe,
// or any non-BCL framework.
//
// The six-rule portability audit for the broader IMetricsSink contract
// is recorded against the interface in `Server/IMetricsSink.fs` — these
// data types are purely structural.

/// What kind of metric this is. Bucket boundaries on `Histogram` are
/// the upper bound of each bucket in the unit declared on
/// `MetricDefinition.Unit`. The Prometheus exporter renders boundaries
/// as `le=...` labels; the OTel companion uses them as the explicit
/// bucket configuration on `Meter.CreateHistogram`.
///
/// `Summary` is included for parity with the Prometheus type vocabulary
/// but the SDK-shipped sinks treat it as `Histogram` with default
/// boundaries — true client-side quantile sketches (p50/p95/p99) are a
/// future companion concern.
type MetricKind =
    | Counter
    | Gauge
    | Histogram of buckets: float list
    | Summary

/// A metric the SDK or a module declares. The `Tags` allowlist is the
/// load-bearing cardinality defence: tags whose key is not in this list
/// are silently dropped at the sink. Module authors declare exactly the
/// tags they emit, and rogue caller-side tags can never widen the
/// cardinality space.
type MetricDefinition = {
    /// Dotted, lowercase, snake. Must match `^[a-z][a-z0-9_.]*$`. The
    /// Prometheus exporter translates `.` to `_` per OpenMetrics
    /// convention. SDK-owned metrics use the `toolup.*` prefix; module
    /// metrics are auto-namespaced to `toolup.{moduleName}.{metricName}`
    /// at registration time and must declare the post-namespace name
    /// (the auto-prefix is added by the registration helper, not by the
    /// module author).
    Name: string
    Kind: MetricKind
    /// Free-text description surfaced on Prometheus `# HELP` lines and
    /// on the OTel `Meter` instrument description. Human-readable; not
    /// a wire-format identifier.
    Description: string
    /// Unit of the observed value: `"ms"`, `"bytes"`, `"1"`, `"tokens"`.
    /// Surfaced on Prometheus `# UNIT` lines and on the OTel instrument.
    /// Histograms use this unit for their bucket boundaries too — a
    /// histogram with `Unit = "ms"` and `buckets = [5.;10.;...]` records
    /// observations whose upper-bound buckets are 5ms, 10ms, etc.
    Unit: string
    /// Allowlist of tag keys this metric may emit. Tags emitted with
    /// keys NOT in this list are silently dropped by the sink. Empty
    /// list means the metric carries no tags.
    Tags: string list
}

/// A registration entry surfaced via DI. The default Prometheus sink
/// uses this list to pre-allocate counters/gauges/histograms at
/// startup; the OTel companion uses it to declare instruments to the
/// `Meter`. Module-scoped registrations carry `Module = Some name` so
/// the registration helper can auto-namespace the metric name to
/// `toolup.{moduleName}.{name}`.
type MetricRegistration = {
    /// `None` = SDK-owned metric registered with the literal name from
    /// `Definition.Name` (must already start `toolup.`). `Some "MyMod"`
    /// = module-scoped; the registration helper rewrites
    /// `Definition.Name = "foo.total"` to `"toolup.mymod.foo.total"`
    /// before passing it to the sink.
    Module: string option
    Definition: MetricDefinition
}

module MetricDefinition =
    /// Reserved prefix for SDK-owned metrics. Module-scoped metrics are
    /// rewritten to `toolup.{moduleName}.{name}`; the registration
    /// helper rejects module declarations whose name already starts
    /// with `toolup.` to avoid colliding with the SDK namespace.
    [<Literal>]
    let ReservedPrefix = "toolup."

    /// Default histogram bucket boundaries for request latency, in
    /// milliseconds. Matches the `SlowRequestThreshold` default
    /// (1000ms) at the centre of the distribution and extends to 10s
    /// on the long tail so the histogram can characterise both the
    /// fast path and pathological slow requests without an unbounded
    /// upper bucket.
    let defaultLatencyBucketsMs: float list = [
        5.0
        10.0
        25.0
        50.0
        100.0
        250.0
        500.0
        1000.0
        2500.0
        5000.0
        10000.0
    ]