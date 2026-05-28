module ToolUp.Platform.Tracing.OpenTelemetry

open System.Diagnostics
open ToolUp.Platform
open ToolUp.Platform.Tracing

// ─── OtelActivitySink — Phase 9l OpenTelemetry tracing companion ────
//
// Sister sink to `OtelMetricsSink`. Implements `IActivitySink` against
// the BCL `System.Diagnostics.ActivitySource` API — .NET 10's OTel-
// native span primitive. Apps that want OTLP / Datadog APM / Honeycomb
// / Jaeger / Application Insights tracing add this companion's
// `OtelActivitySink.create` to their composition root; the same
// OpenTelemetry SDK they wire for metrics (`MeterProviderBuilder
// .AddMeter("ToolUp")`) also picks up tracing via
// `TracerProviderBuilder.AddSource("ToolUp")`.
//
// **Why a shared package.** Phase 9e's `OtelMetricsSink` and this
// `OtelActivitySink` ship in the same companion (`ToolUp.Platform
// .Metrics.OpenTelemetry`) — operators wiring OTel almost always want
// both surfaces; separating them would force two `<ProjectReference>`
// entries plus two `using` directives for one logical observability
// stack. The package name stays as-is (re-naming the published nupkg
// would break consumers); the `OpenTelemetry` brand covers both
// signals.
//
// **Lifecycle.** The sink owns its `ActivitySource`. `IDisposable`
// cleans up the source on host shutdown; while the host is alive,
// activities created from it ride the BCL's async-local
// `Activity.Current` cursor (no per-sink state). The
// `ActivitySource` constructor is cheap — one allocation, zero
// scarce resources held — so a `using` block at app shutdown is
// purely tidiness.
//
// **Sampling.** Sampling decisions belong to the OpenTelemetry SDK,
// not the sink. `ActivitySource.StartActivity` returns `null` when
// either no listener is registered or the registered listener
// declined the activity (sampled out); `Option.ofObj` folds both
// cases into `None`, which the SDK seams handle via `Option.iter`
// disposal (effectively a no-op). Apps that want head sampling
// configure it on `TracerProviderBuilder.SetSampler(...)`; tail
// sampling lives on the collector. The companion does not impose a
// default.
//
// **Six-rule portability audit.** All six rules satisfied — the
// companion is a thin wrapper over the BCL's stateless
// `ActivitySource` primitive. Spans carry only string/byte-span ids
// across processes (W3C Trace Context); the cardinality / retention
// trade-offs live on the exporter, not the sink. See
// `IActivitySink.fs` for the full audit notes.

[<Literal>]
let private DefaultSourceName = "ToolUp"

[<Literal>]
let private DefaultSourceVersion = "1.0.0"

/// OTel-native tracing sink. Constructs a single `ActivitySource`
/// (default name `"ToolUp"`) and creates activities lazily on each
/// `StartActivity` call. The returned `Activity option` is `None`
/// whenever the BCL declines to create the activity (no listener
/// registered, or the listener's sampler returned `RecordingButNot
/// Sampled` / `None`) — the calling seam's `Option.iter` disposal
/// then elides cleanly.
type OtelActivitySink(?sourceName: string, ?sourceVersion: string) =

    let resolvedName = defaultArg sourceName DefaultSourceName
    let resolvedVersion = defaultArg sourceVersion DefaultSourceVersion
    let source = new ActivitySource(resolvedName, resolvedVersion)

    interface System.IDisposable with
        member _.Dispose() = source.Dispose()

    interface IActivitySink with
        member _.StartActivity(name, parentContext) =
            let activity =
                match parentContext with
                | Some ctx -> source.StartActivity(name, ActivityKind.Internal, ctx)
                | None ->
                    // Implicit `Activity.Current` parent — the BCL
                    // overload without an explicit `ActivityContext`
                    // picks up the async-local current when one is
                    // set, otherwise begins a fresh trace.
                    source.StartActivity(name)

            Option.ofObj activity

module OtelActivitySink =
    /// Construct an `OtelActivitySink` ready for
    /// `ServerApp.withActivitySink`. The returned sink wraps a fresh
    /// BCL `ActivitySource` named `"ToolUp"`; deployments that want
    /// OTLP export add `OpenTelemetry.Exporter.OpenTelemetryProtocol`
    /// to their server project's package references and call
    /// `TracerProviderBuilder.AddSource("ToolUp")` in their startup
    /// code. See the companion's `README.md` for the boilerplate.
    let create () : IActivitySink = new OtelActivitySink() :> IActivitySink