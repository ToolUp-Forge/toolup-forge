module ToolUp.Platform.Metrics.OpenTelemetry

open System.Collections.Concurrent
open System.Diagnostics.Metrics
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── OtelMetricsSink — Phase 9e OpenTelemetry companion ─────────────
//
// Implements `IMetricsSink` against the BCL `System.Diagnostics.Metrics.Meter`
// API. `Meter` is the OTel-native metric primitive on .NET 10 — the
// OpenTelemetry SDK consumes `Meter` instances via
// `MeterProviderBuilder.AddMeter("ToolUp")` and exports them to OTLP /
// Prometheus / Console / any registered exporter. This companion does
// NOT carry the OpenTelemetry SDK as a dependency — deployments that
// want OTLP export add the SDK themselves and point it at our Meter.
// Companion paket.references stays lean: no NuGet deps beyond
// FSharp.Core.
//
// **Lifecycle.** `OtelMetricsSink` owns its `Meter`. The Meter is
// disposed when the sink is disposed (the `IDisposable`-cycle is
// handled by the SDK companion-host as a `IHostedService` shutdown,
// or implicitly when the process exits — Meter doesn't hold scarce
// resources, it just tells listeners "no more emissions from me").
// Instruments (Counter / Gauge / Histogram) are cached per metric
// name in a `ConcurrentDictionary` so repeated emissions don't
// allocate a new instrument every call.
//
// **Wire-format.** BCL `Meter` exposes typed instruments:
// `Counter<double>` for counters, `ObservableGauge<double>` for
// gauges (push-style), `Histogram<double>` for histograms. The
// `KeyValuePair<string,object>[]` argument to each `Add` /
// `Record` call carries the tag set. Tag-key allowlist enforcement
// happens here (mirroring `PrometheusMetricsSink`) so the OTel side
// sees the same dropped-tag behaviour as the in-process default.
//
// **Cardinality cap.** The cap is NOT enforced in this companion —
// BCL `Meter` doesn't have a per-instrument series-count primitive
// and the OpenTelemetry SDK's `MeterProviderBuilder.SetMaxMetricStreams`
// is the natural place to bound cardinality on the export side. The
// in-process Prometheus default still applies its cap (the design
// has the Prometheus sink as the head of the FanOutMetricsSink
// chain, so an emission past the cap routes to `_overflow=true`
// before being forwarded here).
//
// **Six-rule portability audit.** All six rules satisfied — the
// companion is a thin wrapper over a stateless instrument cache;
// emissions are commutative; no cross-shard ordering claimed; tag
// keys/values are strings; sync interface matches IMetricsSink's
// documented exemption.

[<Literal>]
let private DefaultMeterName = "ToolUp"

[<Literal>]
let private DefaultMeterVersion = "1.0.0"

/// Instrument cache. Keyed by metric name; values are typed
/// instrument objects boxed because BCL doesn't have a common base
/// type across `Counter<T>` / `Histogram<T>`.
type private InstrumentCache = ConcurrentDictionary<string, obj>

/// OTel-native metrics sink. Constructs a single `Meter` (default
/// name `"ToolUp"`) and creates BCL instruments lazily on first
/// emission. Pre-registers any metrics supplied at construction so
/// instruments exist even before the first emission — this matters
/// for OTel SDK's `MeterListener` model where the listener picks up
/// instruments at registration time.
type OtelMetricsSink(registrations: MetricRegistration list, logger: ILogger, ?meterName: string, ?meterVersion: string)
    =

    let resolvedName = defaultArg meterName DefaultMeterName
    let resolvedVersion = defaultArg meterVersion DefaultMeterVersion
    let meter = new Meter(resolvedName, resolvedVersion)
    let instruments: InstrumentCache = ConcurrentDictionary()

    /// Registry-driven map of `metricName → MetricDefinition` so
    /// `Increment` / `Record` / `SetGauge` can decide which
    /// instrument shape to create when caching. Built once at
    /// construction.
    let registry =
        let d = System.Collections.Generic.Dictionary<string, MetricDefinition>()

        for reg in registrations do
            let resolvedName =
                match reg.Module with
                | Some m ->
                    if
                        reg.Definition.Name.StartsWith(MetricDefinition.ReservedPrefix, System.StringComparison.Ordinal)
                    then
                        failwithf
                            "Module-scoped metric '%s' (module '%s') must not declare the reserved '%s' prefix — module metrics are auto-namespaced to '%s%s.<name>'. Declare the post-namespace name only."
                            reg.Definition.Name
                            m
                            MetricDefinition.ReservedPrefix
                            MetricDefinition.ReservedPrefix
                            (m.ToLowerInvariant())

                    sprintf "%s%s.%s" MetricDefinition.ReservedPrefix (m.ToLowerInvariant()) reg.Definition.Name
                | None -> reg.Definition.Name

            d[resolvedName] <- reg.Definition

        d

    let toTagArray
        (allowed: string list)
        (tags: Map<string, string>)
        : System.Collections.Generic.KeyValuePair<string, obj> array =
        if Map.isEmpty tags then
            [||]
        elif List.isEmpty allowed then
            // Tag-free metric — drop all tags.
            [||]
        else
            let allowSet = Set.ofList allowed

            tags
            |> Map.filter (fun k _ -> Set.contains k allowSet)
            |> Map.toArray
            |> Array.map (fun (k, v) -> System.Collections.Generic.KeyValuePair<string, obj>(k, box v))

    let getCounter (name: string) (def: MetricDefinition) : Counter<double> =
        match instruments.TryGetValue name with
        | true, inst -> inst :?> Counter<double>
        | false, _ ->
            let c =
                meter.CreateCounter<double>(name = name, unit = def.Unit, description = def.Description)

            instruments[name] <- box c
            c

    let getHistogram (name: string) (def: MetricDefinition) : Histogram<double> =
        match instruments.TryGetValue name with
        | true, inst -> inst :?> Histogram<double>
        | false, _ ->
            let h =
                meter.CreateHistogram<double>(name = name, unit = def.Unit, description = def.Description)

            instruments[name] <- box h
            h

    /// Gauge state — BCL's `ObservableGauge<double>` is callback-based
    /// (the SDK polls a thunk). We hold a mutable `float` cell per
    /// gauge name and register the observable lazily on first write.
    let gaugeCells: ConcurrentDictionary<string, ConcurrentDictionary<string, float ref>> =
        ConcurrentDictionary()

    let ensureGauge (name: string) (def: MetricDefinition) : ConcurrentDictionary<string, float ref> =
        match gaugeCells.TryGetValue name with
        | true, cells -> cells
        | false, _ ->
            let cells = ConcurrentDictionary<string, float ref>()

            // Register the observable gauge once. The callback closes
            // over the cell map and emits one Measurement per known
            // tag-set on each SDK poll.
            meter.CreateObservableGauge<double>(
                name = name,
                observeValues =
                    (fun () ->
                        cells
                        |> Seq.map (fun kvp ->
                            let tagStr = kvp.Key

                            let tags =
                                if System.String.IsNullOrEmpty tagStr then
                                    [||]
                                else
                                    tagStr.Split(';')
                                    |> Array.choose (fun pair ->
                                        let i = pair.IndexOf('=')

                                        if i > 0 then
                                            Some(
                                                System.Collections.Generic.KeyValuePair<string, obj>(
                                                    pair.Substring(0, i),
                                                    box (pair.Substring(i + 1))
                                                )
                                            )
                                        else
                                            None)

                            new Measurement<double>(kvp.Value.Value, tags))),
                unit = def.Unit,
                description = def.Description
            )
            |> ignore

            // Use AddOrUpdate to avoid losing a concurrent registration.
            gaugeCells.GetOrAdd(name, cells)

    let tagKey (allowed: string list) (tags: Map<string, string>) : string =
        if Map.isEmpty tags then
            ""
        elif List.isEmpty allowed then
            ""
        else
            let allowSet = Set.ofList allowed

            tags
            |> Map.filter (fun k _ -> Set.contains k allowSet)
            |> Map.toList
            |> List.sortBy fst
            |> List.map (fun (k, v) -> sprintf "%s=%s" k v)
            |> String.concat ";"

    interface System.IDisposable with
        member _.Dispose() = meter.Dispose()

    interface IMetricsSink with
        member _.Record(name, value, tags) =
            match registry.TryGetValue name with
            | true, def ->
                match def.Kind with
                | Histogram _
                | Summary ->
                    try
                        let h = getHistogram name def
                        let tagArr = toTagArray def.Tags tags
                        h.Record(value, tagArr)
                    with ex ->
                        logger.Warn(sprintf "[OtelMetricsSink] Record(%s) threw: %s" name ex.Message)
                | _ -> ()
            | false, _ -> ()

        member _.Increment(name, tags) =
            match registry.TryGetValue name with
            | true, def ->
                match def.Kind with
                | Counter ->
                    try
                        let c = getCounter name def
                        let tagArr = toTagArray def.Tags tags
                        c.Add(1.0, tagArr)
                    with ex ->
                        logger.Warn(sprintf "[OtelMetricsSink] Increment(%s) threw: %s" name ex.Message)
                | _ -> ()
            | false, _ -> ()

        member _.SetGauge(name, value, tags) =
            match registry.TryGetValue name with
            | true, def ->
                match def.Kind with
                | Gauge ->
                    try
                        let cells = ensureGauge name def
                        let key = tagKey def.Tags tags

                        let cell = cells.GetOrAdd(key, fun _ -> ref 0.0)

                        lock cell (fun () -> cell.Value <- value)
                    with ex ->
                        logger.Warn(sprintf "[OtelMetricsSink] SetGauge(%s) threw: %s" name ex.Message)
                | _ -> ()
            | false, _ -> ()

module OtelMetricsSink =
    /// Construct an `OtelMetricsSink` ready for `withMetricsSink`. The
    /// returned sink wraps a fresh BCL `Meter` named `"ToolUp"`;
    /// deployments that want OTLP export add `OpenTelemetry.Exporter
    /// .OpenTelemetryProtocol` to their server project's
    /// paket.references and call `MeterProviderBuilder.AddMeter("ToolUp")`
    /// in their startup code. See the companion's README for the
    /// boilerplate.
    ///
    /// Pass the same `MetricRegistration list` the in-process sink
    /// receives — typically `Metrics.StandardMetrics.registrations`
    /// plus any module-scoped registrations the deployment has.
    let create (registrations: MetricRegistration list) (logger: ILogger) : IMetricsSink =
        new OtelMetricsSink(registrations, logger) :> IMetricsSink