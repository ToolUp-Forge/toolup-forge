namespace ToolUp.Platform.Tracing

open System.Diagnostics

// ─── IActivitySink — Phase 9l portable distributed-tracing seam ─────
//
// Peer to `IMetricsSink` (Phase 9e). Where the metrics sink emits
// scalar samples, the activity sink starts BCL `System.Diagnostics
// .Activity` instances under the SDK's `ActivitySource` so any
// OpenTelemetry-compatible exporter (OTLP / Datadog APM / Honeycomb /
// Jaeger / Application Insights) consumes the resulting span tree.
// The interface is the same shape — DI-resolved, no-op default
// registered when no companion is wired, real exporter provided by
// `ToolUp.Platform.Metrics.OpenTelemetry`'s `OtelActivitySink`.
//
// **Why we expose BCL `Activity` on the surface.** `Activity` is .NET 10's
// canonical W3C-Trace-Context primitive — `traceparent` parsing,
// TraceId / SpanId allocation, parent-child linkage, and async-local
// propagation all live in the BCL. The OpenTelemetry SDK consumes
// `Activity` instances; Application Insights consumes `Activity`
// instances; the BCL `ActivitySource` integration is what every
// production observability vendor on .NET binds to. A custom value-
// typed wrapper here would force every exporter to translate twice
// and break automatic parent-child linkage with framework-emitted
// activities (Kestrel request activities, `HttpClient` outbound
// activities, EF Core query activities). The interface returns
// `Activity option` rather than nullable `Activity` so F# call sites
// stay idiomatic — `Option.iter (_.Dispose())` is the disposal
// pattern, no null-check pyramid.
//
// **Six-rule portability audit (Phase 9c — GP 12).**
//   1. Identity by value      — `name : string`; `parentContext :
//                                ActivityContext option` (a BCL
//                                value type carrying TraceId / SpanId
//                                / flags / state — strings + byte
//                                spans, no framework handle). The
//                                returned `Activity` is the standard
//                                BCL identity carrier; a distributed
//                                companion (Akka/Orleans cluster-
//                                wide tracing) inspects its
//                                `TraceId` / `SpanId` properties as
//                                strings, never the wrapper instance
//                                identity.
//   2. Async exemption        — `StartActivity` is sync `unit ->
//                                Activity option` by design. Same
//                                exemption as `IMetricsSink`: hot
//                                path, fire-and-forget, no return
//                                value to await. Span export is
//                                handled by the exporter's batch
//                                processor, off-thread, owned by the
//                                companion's `OpenTelemetry SDK`
//                                lifecycle (not the sink).
//   3. Retry as data          — Sinks do not retry. A span that
//                                cannot be exported (collector down,
//                                BCL listener thread saturated) is
//                                dropped silently inside the exporter
//                                — same posture as metrics. Operators
//                                see degradation through their
//                                collector's own monitors.
//   4. Stateless boundary     — Each `StartActivity` call carries the
//                                full `(name, parentContext)` pair.
//                                The `ActivitySource` instance held
//                                by the OTel companion is impl
//                                detail; distributed replacements
//                                (an Orleans-grain-resident tracer)
//                                must work without in-memory
//                                continuity between calls.
//   5. No cross-shard ordering — Spans within a trace are linked by
//                                parent-child reference, not by
//                                emission order. Two sibling spans
//                                under the same parent may export in
//                                any order; collectors stitch the
//                                tree by ID.
//   6. Precision documented   — Span timing precision is "best-effort
//                                wall-clock" via `Activity.StartTime`
//                                (`DateTime.UtcNow` underneath, OS-
//                                tick precision). Spans short enough
//                                to fall under one tick (~15 ms on
//                                some Windows hosts) collapse to
//                                zero-duration. Latency-grade
//                                measurements use the metrics
//                                histograms instead.

/// Distributed-tracing activity factory. Implementations create
/// `System.Diagnostics.Activity` instances rooted under the SDK's
/// `ActivitySource` (default name `"ToolUp"`); an OpenTelemetry SDK
/// consumer registers a `Listener` for that source and exports the
/// resulting span tree to an OTLP collector / Datadog APM / Honeycomb
/// / Jaeger / Application Insights.
///
/// **Caller pattern.**
/// ```fsharp
/// let activityOpt = sink.StartActivity("name", parentContext)
/// try
///     // ... work ...
/// finally
///     activityOpt |> Option.iter (fun a -> a.Dispose())
/// ```
///
/// `StartActivity` returns `None` when the no-op default sink is
/// registered (no listener / no exporter wired) so the entire tracing
/// path elides at zero cost. Callers MUST NOT rely on the returned
/// activity carrying user-visible side effects beyond span linkage —
/// the BCL is free to suppress activity creation under sampling
/// pressure even when an exporter is wired.
type IActivitySink =
    /// Start a new activity with `name`. When `parentContext` is
    /// `Some`, the new activity inherits TraceId / SpanId / flags
    /// from it (the W3C parent-child link); when `None`, the
    /// implementation uses `Activity.Current` as its parent if one is
    /// set on the async-local context (the default cross-async
    /// propagation), otherwise begins a new trace.
    abstract StartActivity: name: string * parentContext: ActivityContext option -> Activity option

/// SDK-default no-op implementation of `IActivitySink`. Registered
/// when no companion is wired so emission sites in `compose`-
/// controlled code (request middleware, job scheduler, webhook
/// dispatcher, transactional dispatcher, module query bus) can
/// resolve the service unconditionally — `IActivitySink` is never
/// `null` in DI when the SDK is composed. Mirrors the
/// `NoOpMetricsSink` / `NoOpAuditLog` pattern.
type NoOpActivitySink() =
    interface IActivitySink with
        member _.StartActivity(_name, _parentContext) = None

/// Helpers shared by SDK-side instrumentation sites — parsing
/// inbound W3C `traceparent` strings, capturing the current
/// activity's id for envelope stamping. Lives next to `IActivitySink`
/// rather than inside each seam so all five instrumented sites use
/// the same parse semantics.
module ActivityContextHelpers =
    /// Parse a `traceparent` header value (W3C Trace Context spec —
    /// `00-<32 hex traceId>-<16 hex spanId>-<2 hex flags>`) into a
    /// BCL `ActivityContext`. Malformed input → `None` (silent —
    /// observability metadata is best-effort by design).
    let tryParseTraceparent (traceparent: string) : ActivityContext option =
        if System.String.IsNullOrEmpty traceparent then
            None
        else
            match ActivityContext.TryParse(traceparent, null) with
            | true, ctx -> Some ctx
            | false, _ -> None

    /// Capture the current ambient activity's W3C id (`traceparent`
    /// format). Returns `None` when no activity is in flight; used
    /// by channels to stamp `NotificationEnvelope.TraceContext`
    /// before delivery so out-of-band subscribers can re-join the
    /// trace.
    let currentTraceparent () : string option =
        match Activity.Current with
        | null -> None
        | a when a.IdFormat = ActivityIdFormat.W3C -> Option.ofObj a.Id
        | _ -> None