namespace ToolUp.Platform.Metrics

open System
open System.Collections.Concurrent
open System.Text
open ToolUp.Platform

// ─── PrometheusMetricsSink — Phase 9e default in-process sink ───────
//
// Zero-external-deps Prometheus / OpenMetrics text-format exporter. The
// sink maintains in-memory accumulators (counters, gauges, histograms)
// keyed by `(metricName, sortedTagSet)`. Render-on-demand: the
// `Render()` method walks the accumulators and emits the OpenMetrics
// representation. The `/metrics` endpoint calls `Render()` per request.
//
// **Cardinality cap.** Two-layer defence:
//   1. Tag-key allowlist (defined per metric in `MetricDefinition.Tags`)
//      drops incoming tags whose keys aren't in the allowlist before
//      they reach the accumulator. Cheap structural defence.
//   2. Per-metric series-count ceiling (default 1000, configurable per
//      metric via `MetricsSinkConfig.PerMetricMaxSeries`). Beyond the
//      ceiling, new tag-set combinations route to a single overflow
//      series tagged `_overflow="true"`. First overflow logs `Warn`
//      once; subsequent overflows are silent.
//
// **Thread-safety.** `ConcurrentDictionary` for the metric registry
// and per-series accumulators. Counter increments use
// `Interlocked.Add` (via `AddOrUpdate`); gauge writes use a volatile
// store; histogram bucket counts use one `Interlocked.Increment` per
// matching bucket. No global lock — emission is wait-free under
// contention.

/// Internal — sorted, normalised tag set used as the dictionary key
/// for the accumulator. Sorting by key gives a deterministic
/// fingerprint; using a list-of-pairs (rather than `Map`) keeps the
/// hash code stable across .NET runtime versions.
type private TagFingerprint = (string * string) list

[<RequireQualifiedAccess>]
module private TagFingerprint =
    let create (tags: Map<string, string>) : TagFingerprint = tags |> Map.toList |> List.sortBy fst

    let renderLabels (fp: TagFingerprint) : string =
        if List.isEmpty fp then
            ""
        else
            let escaped =
                fp
                |> List.map (fun (k, v) ->
                    let escapedV = v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")

                    sprintf "%s=\"%s\"" k escapedV)
                |> String.concat ","

            "{" + escaped + "}"

/// Per-series histogram state. Bucket counts are keyed by the upper
/// bound; `Sum` is the running total of observations; `Count` is the
/// total number of observations.
type private HistogramSeries = {
    Buckets: ConcurrentDictionary<float, int64>
    mutable Sum: float
    mutable Count: int64
    /// Snapshot bound on the buckets in registration order. The
    /// dictionary above lets emissions update individual bucket counts
    /// in O(1); this list preserves render-time ordering.
    Bounds: float list
    /// Lock for updating Sum / Count atomically. Shorter-held than
    /// renaming the whole accumulator; covers the floating-point
    /// addition which has no Interlocked equivalent.
    SumLock: obj
}

module private HistogramSeries =
    let create (bounds: float list) : HistogramSeries = {
        Buckets =
            ConcurrentDictionary<float, int64>(
                bounds |> List.map (fun b -> System.Collections.Generic.KeyValuePair(b, 0L))
            )
        Sum = 0.0
        Count = 0L
        Bounds = bounds
        SumLock = obj ()
    }

    let observe (value: float) (h: HistogramSeries) =
        for upperBound in h.Bounds do
            if value <= upperBound then
                h.Buckets.AddOrUpdate(upperBound, 1L, (fun _ existing -> existing + 1L))
                |> ignore

        lock h.SumLock (fun () ->
            h.Sum <- h.Sum + value
            h.Count <- h.Count + 1L)

/// Internal — one entry per registered metric. Carries the registration
/// metadata plus the per-series accumulators.
type private RegisteredMetric = {
    Name: string
    Definition: MetricDefinition
    /// Counter / gauge series store a single `float` observation. We
    /// use a `ConcurrentDictionary<TagFingerprint, float ref>` so
    /// gauge `SetGauge` overwrites the cell and counter `Increment`
    /// reads/writes through the cell under `Interlocked.Exchange`-
    /// equivalent semantics (lock for floats; double has no
    /// Interlocked.Add).
    NumericSeries: ConcurrentDictionary<TagFingerprint, float ref>
    /// Histogram series, only populated when `Definition.Kind` is
    /// `Histogram` or `Summary`.
    HistogramSeries: ConcurrentDictionary<TagFingerprint, HistogramSeries>
    /// Per-metric series-count cap snapshot at registration. Overrides
    /// from `MetricsSinkConfig.PerMetricMaxSeries` are applied here so
    /// the hot path doesn't need to consult the config map every
    /// emission.
    MaxSeries: int
    /// Set once, after the first overflow event, to suppress repeat
    /// log spam. Read on every emission — cheap because the read is
    /// after the cardinality check (only when overflow is imminent).
    mutable OverflowLogged: bool
    /// Lock for state changes that span more than one
    /// ConcurrentDictionary operation — series-count check + insert.
    AccessLock: obj
}

module private RegisteredMetric =
    let create (config: MetricsSinkConfig) (reg: MetricRegistration) : string * RegisteredMetric =
        let resolvedName =
            match reg.Module with
            | Some moduleName ->
                if reg.Definition.Name.StartsWith(MetricDefinition.ReservedPrefix, StringComparison.Ordinal) then
                    failwithf
                        "Module-scoped metric '%s' (module '%s') must not declare the reserved '%s' prefix — module metrics are auto-namespaced to '%s%s.<name>'. Declare the post-namespace name only."
                        reg.Definition.Name
                        moduleName
                        MetricDefinition.ReservedPrefix
                        MetricDefinition.ReservedPrefix
                        (moduleName.ToLowerInvariant())

                let lower = moduleName.ToLowerInvariant()
                sprintf "%s%s.%s" MetricDefinition.ReservedPrefix lower reg.Definition.Name
            | None -> reg.Definition.Name

        let maxSeries =
            match Map.tryFind resolvedName config.PerMetricMaxSeries with
            | Some n -> n
            | None -> config.MaxSeriesPerMetric

        let metric = {
            Name = resolvedName
            Definition = reg.Definition
            NumericSeries = ConcurrentDictionary()
            HistogramSeries = ConcurrentDictionary()
            MaxSeries = maxSeries
            OverflowLogged = false
            AccessLock = obj ()
        }

        resolvedName, metric

/// Default in-process metrics sink. Owns the metric registry and
/// renders OpenMetrics text on demand. Sink consumers (the
/// `MetricsMiddleware`, the job scheduler, SSE connection manager)
/// only see the `IMetricsSink` interface; `Render` is exposed
/// concretely so `MetricsEndpoint.fs` can serialise the registry.
type PrometheusMetricsSink(config: MetricsSinkConfig, registrations: MetricRegistration list, logger: ILogger) =
    /// Registry keyed by the post-namespace metric name. Built once
    /// at construction; never mutated thereafter — module / SDK
    /// metric registration happens at compose time.
    let registry: ConcurrentDictionary<string, RegisteredMetric> =
        let d = ConcurrentDictionary<string, RegisteredMetric>()

        for reg in registrations do
            let name, metric = RegisteredMetric.create config reg

            if not (d.TryAdd(name, metric)) then
                failwithf "Duplicate metric registration: %s" name

        d

    /// Drop tags whose key is not in the metric's `Tags` allowlist.
    /// Empty allowlist means the metric is tag-free — every incoming
    /// tag is dropped silently.
    let filterTags (metric: RegisteredMetric) (tags: Map<string, string>) : Map<string, string> =
        if Map.isEmpty tags then
            tags
        elif List.isEmpty metric.Definition.Tags then
            Map.empty
        else
            let allowlist = Set.ofList metric.Definition.Tags

            tags |> Map.filter (fun k _ -> Set.contains k allowlist)

    /// Numeric (counter / gauge) emission. Returns the cell holding
    /// the current value so callers can apply Increment / SetGauge.
    let getNumericCell (metric: RegisteredMetric) (tags: Map<string, string>) : float ref =
        let filtered = filterTags metric tags

        // The cell-existing predicate doubles as the insert in the
        // hot path: AddOrUpdate either returns the existing cell or
        // creates a new one. We need a separate path for the
        // overflow check — the cap MUST be enforced before insertion.
        let fingerprint = TagFingerprint.create filtered
        let existing = metric.NumericSeries.TryGetValue fingerprint

        match existing with
        | true, cell -> cell
        | false, _ ->
            // New fingerprint — check the cap.
            lock metric.AccessLock (fun () ->
                // Re-check after acquiring the lock.
                match metric.NumericSeries.TryGetValue fingerprint with
                | true, cell -> cell
                | false, _ ->
                    if metric.NumericSeries.Count < metric.MaxSeries then
                        let newCell = ref 0.0
                        metric.NumericSeries[fingerprint] <- newCell
                        newCell
                    else
                        if not metric.OverflowLogged then
                            metric.OverflowLogged <- true

                            logger.Warn(
                                sprintf
                                    "[Metrics] cardinality overflow for %s: capped at %d series; subsequent tag-sets fold to _overflow=true"
                                    metric.Name
                                    metric.MaxSeries
                            )

                        let overflowKey: TagFingerprint = [ ("_overflow", "true") ]

                        match metric.NumericSeries.TryGetValue overflowKey with
                        | true, cell -> cell
                        | false, _ ->
                            let newCell = ref 0.0
                            metric.NumericSeries[overflowKey] <- newCell
                            newCell)

    let getHistogramSeries (metric: RegisteredMetric) (tags: Map<string, string>) : HistogramSeries =
        let filtered = filterTags metric tags
        let fingerprint = TagFingerprint.create filtered

        let bounds =
            match metric.Definition.Kind with
            | Histogram bs -> bs
            | Summary -> MetricDefinition.defaultLatencyBucketsMs
            | _ -> []

        match metric.HistogramSeries.TryGetValue fingerprint with
        | true, h -> h
        | false, _ ->
            lock metric.AccessLock (fun () ->
                match metric.HistogramSeries.TryGetValue fingerprint with
                | true, h -> h
                | false, _ ->
                    if metric.HistogramSeries.Count < metric.MaxSeries then
                        let h = HistogramSeries.create bounds
                        metric.HistogramSeries[fingerprint] <- h
                        h
                    else
                        if not metric.OverflowLogged then
                            metric.OverflowLogged <- true

                            logger.Warn(
                                sprintf
                                    "[Metrics] cardinality overflow for %s: capped at %d series; subsequent tag-sets fold to _overflow=true"
                                    metric.Name
                                    metric.MaxSeries
                            )

                        let overflowKey: TagFingerprint = [ ("_overflow", "true") ]

                        match metric.HistogramSeries.TryGetValue overflowKey with
                        | true, h -> h
                        | false, _ ->
                            let h = HistogramSeries.create bounds
                            metric.HistogramSeries[overflowKey] <- h
                            h)

    /// Render the entire registry as OpenMetrics text. One block per
    /// metric: `# HELP`, `# TYPE`, `# UNIT`, then one line per series.
    /// Histograms emit `_bucket` lines per bound, plus `_sum` and
    /// `_count`. Counters and gauges emit one line each.
    member _.Render() : string =
        let sb = StringBuilder()

        let renderName (n: string) = n.Replace('.', '_')

        let renderType (k: MetricKind) =
            match k with
            | Counter -> "counter"
            | Gauge -> "gauge"
            | Histogram _ -> "histogram"
            | Summary -> "histogram"

        let renderFloat (v: float) =
            if Double.IsNaN v then
                "NaN"
            elif Double.IsPositiveInfinity v then
                "+Inf"
            elif Double.IsNegativeInfinity v then
                "-Inf"
            else
                v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)

        for KeyValue(_, metric) in registry do
            let promName = renderName metric.Name

            sb.AppendFormat("# HELP {0} {1}\n", promName, metric.Definition.Description)
            |> ignore

            sb.AppendFormat("# TYPE {0} {1}\n", promName, renderType metric.Definition.Kind)
            |> ignore

            if not (String.IsNullOrEmpty metric.Definition.Unit) then
                sb.AppendFormat("# UNIT {0} {1}\n", promName, metric.Definition.Unit) |> ignore

            match metric.Definition.Kind with
            | Counter
            | Gauge ->
                for KeyValue(fp, cellRef) in metric.NumericSeries do
                    let labels = TagFingerprint.renderLabels fp

                    sb.Append(promName).Append(labels).Append(' ').Append(renderFloat cellRef.Value).Append('\n')
                    |> ignore
            | Histogram _
            | Summary ->
                for KeyValue(fp, h) in metric.HistogramSeries do
                    // One _bucket line per bound, then _bucket{le="+Inf"}, _sum, _count.
                    for upperBound in h.Bounds do
                        let labelsWithLe =
                            let le = sprintf "le=\"%s\"" (renderFloat upperBound)

                            if List.isEmpty fp then
                                "{" + le + "}"
                            else
                                let inner = TagFingerprint.renderLabels fp
                                inner.TrimEnd('}') + "," + le + "}"

                        let count =
                            match h.Buckets.TryGetValue upperBound with
                            | true, c -> c
                            | false, _ -> 0L

                        sb
                            .Append(promName)
                            .Append("_bucket")
                            .Append(labelsWithLe)
                            .Append(' ')
                            .Append(count)
                            .Append('\n')
                        |> ignore

                    let labelsInf =
                        if List.isEmpty fp then
                            "{le=\"+Inf\"}"
                        else
                            let inner = TagFingerprint.renderLabels fp
                            inner.TrimEnd('}') + ",le=\"+Inf\"}"

                    sb.Append(promName).Append("_bucket").Append(labelsInf).Append(' ').Append(h.Count).Append('\n')
                    |> ignore

                    let labels = TagFingerprint.renderLabels fp

                    sb.Append(promName).Append("_sum").Append(labels).Append(' ').Append(renderFloat h.Sum).Append('\n')
                    |> ignore

                    sb.Append(promName).Append("_count").Append(labels).Append(' ').Append(h.Count).Append('\n')
                    |> ignore

            sb.Append('\n') |> ignore

        sb.Append("# EOF\n") |> ignore
        sb.ToString()

    /// Phase 178 — read the current accumulated value of a counter /
    /// gauge series for the `AlertRuleEngine`. This is the metric-read
    /// tap the alert engine consumes so `IMetricsSink` stays write-only
    /// (its rule-2 hot-path exemption is preserved — no read method is
    /// added to the emission interface; the read lives on the concrete
    /// default sink, resolved from DI by the engine's BackgroundService).
    ///
    /// Read-only: unlike the emission path's `getNumericCell`, this
    /// never creates a series. Returns `None` for an unregistered
    /// metric, a `Histogram` / `Summary` metric (threshold rules target
    /// scalar counter / gauge series), or a `(name, tags)` series that
    /// has had no observation yet. Tags are filtered through the
    /// metric's allowlist first, matching the emission path so a rule's
    /// tag set selects the same series a caller's emission wrote.
    member _.TryRead(name: string, tags: Map<string, string>) : float option =
        match registry.TryGetValue name with
        | true, metric ->
            match metric.Definition.Kind with
            | Counter
            | Gauge ->
                let filtered = filterTags metric tags
                let fingerprint = TagFingerprint.create filtered

                match metric.NumericSeries.TryGetValue fingerprint with
                | true, cell -> Some(lock cell (fun () -> cell.Value))
                | false, _ -> None
            | _ -> None
        | false, _ -> None

    interface IMetricsSink with
        member _.Record(name, value, tags) =
            match registry.TryGetValue name with
            | true, metric ->
                match metric.Definition.Kind with
                | Histogram _
                | Summary ->
                    let h = getHistogramSeries metric tags
                    HistogramSeries.observe value h
                | _ ->
                    // Counter / Gauge — Record is a no-op against a
                    // non-histogram metric. Operators using the wrong
                    // method are a misuse but not worth crashing over.
                    ()
            | false, _ -> ()

        member _.Increment(name, tags) =
            match registry.TryGetValue name with
            | true, metric ->
                match metric.Definition.Kind with
                | Counter ->
                    let cell = getNumericCell metric tags
                    lock cell (fun () -> cell.Value <- cell.Value + 1.0)
                | _ -> ()
            | false, _ -> ()

        member _.SetGauge(name, value, tags) =
            match registry.TryGetValue name with
            | true, metric ->
                match metric.Definition.Kind with
                | Gauge ->
                    let cell = getNumericCell metric tags
                    lock cell (fun () -> cell.Value <- value)
                | _ -> ()
            | false, _ -> ()

/// Phase 9e — fan-out wrapper. When `compose` registers more than one
/// `IMetricsSink` (the in-process Prometheus default + a companion
/// like the OpenTelemetry exporter), it wraps them in this and
/// registers the wrapper as the singleton `IMetricsSink` consumers
/// resolve. Each emission dispatches to every wrapped sink in
/// registration order. A failing inner sink (network blip, exporter
/// queue full) does NOT propagate — the wrapper swallows the
/// exception so a misbehaving companion can't take out the in-process
/// metrics path.
type FanOutMetricsSink(sinks: IMetricsSink list, logger: ILogger) =
    interface IMetricsSink with
        member _.Record(name, value, tags) =
            for s in sinks do
                try
                    s.Record(name, value, tags)
                with ex ->
                    logger.Warn(sprintf "[Metrics] sink %s threw on Record: %s" (s.GetType().Name) ex.Message)

        member _.Increment(name, tags) =
            for s in sinks do
                try
                    s.Increment(name, tags)
                with ex ->
                    logger.Warn(sprintf "[Metrics] sink %s threw on Increment: %s" (s.GetType().Name) ex.Message)

        member _.SetGauge(name, value, tags) =
            for s in sinks do
                try
                    s.SetGauge(name, value, tags)
                with ex ->
                    logger.Warn(sprintf "[Metrics] sink %s threw on SetGauge: %s" (s.GetType().Name) ex.Message)