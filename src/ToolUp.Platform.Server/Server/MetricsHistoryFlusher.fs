// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.MetricsHistoryFlusher

open System
open System.Threading
open System.Threading.Tasks
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── Phase 829 — metrics history ────────────────────────────────────
//
// A `BackgroundService` that gives the standard SDK metrics a HISTORY
// without adding a store. The live registry (Phase 9e) answers "what is
// it now" and forgets on restart; `ITimeSeriesStore` (Phase 161) already
// stores range-queryable numeric series. This is the seam between them:
// on a cadence, sample every observed `(metric, tag set)` and append one
// point per series.
//
// **The read tap.** `PrometheusMetricsSink.Snapshot()` — on the concrete
// default sink, never on `IMetricsSink`, which stays WRITE-ONLY so its
// six-rule hot-path exemption is preserved. Same seam `AlertRuleEngine`
// reads through, widened from one keyed lookup to an enumeration
// because a flusher cannot know which tag sets have been observed.
//
// **Series ids.** `<metric>{<tags>}` — the metric's registered name
// (SDK metrics already carry the reserved `toolup.` prefix, so these
// read `toolup.requests.total{route="/api"}`) followed by the sorted tag
// set in Prometheus label syntax. A histogram fans out to one series per
// field, the field appended to the NAME rather than after the braces
// (`toolup.request.duration.p95{route="/api"}`), mirroring the
// `_sum` / `_count` suffix convention the exporter already uses.
//
// **Counters are stored as DELTAS, everything else as read.** A counter
// accumulator only ever grows, so a history of totals is a history of
// one number's staircase; the useful series is what happened IN each
// interval. The first flush of a series has nothing to difference
// against, so it records the accumulated total — a cold-start artefact
// on the first point, not a running one. A total that has gone DOWN is a
// counter reset (a restarted process writing the same series): the
// current value is taken as the delta, the convention Prometheus' `rate`
// uses. Gauges are point-in-time by definition and are recorded as read.
// Histogram `count` / `sum` are recorded CUMULATIVELY, matching what the
// exporter renders — a reader differences adjacent points for a rate,
// exactly as it would against a scrape.
//
// **Retention re-appends what it keeps.** `ITimeSeriesStore` deletes
// whole series only (its Phase 439 note says so explicitly and names
// this pattern): the daily sweep queries the keep-window, deletes the
// series and appends the kept points back. It runs in the same loop as
// the flush, so nothing in this process writes between the query and the
// delete.
//
// **Single-instance caveat.** Each process samples its OWN registry —
// metric accumulators are in-memory and per-process. Two silos flushing
// into one store therefore interleave their samples under the same
// series ids. Same class as `HealthStateTracker` / `AlertRuleEngine`,
// and the reason the service is skipped on `ServerlessHost`, where no
// long-running tick can be kept alive at all.
//
// **Six-rule portability audit:**
//   1. Identity by value      — series are strings, points are records;
//                                 no live handle crosses the store seam.
//   2. Async                  — every store call is `Async<_>`; the tick
//                                 body is `Async`.
//   3. Retry as data          — none here: a failed append is dropped
//                                 with a named log line and the next
//                                 flush carries on. The counter state
//                                 advances anyway, so a transient store
//                                 outage loses an interval rather than
//                                 double-counting it into the next one.
//   4. Stateless handlers     — `flushOnce` takes the reader, the store
//                                 and the previous counter totals as
//                                 parameters and returns the next ones;
//                                 the service owns the only mutable
//                                 cell.
//   5. No cross-shard order   — ordering is promised within a series,
//                                 which is what the store promises too.
//   6. Precision              — cadence is seconds, floored at 1s;
//                                 points are `DateTimeOffset`.

/// The storage scope every metrics-history series is written under. The
/// reserved platform scope, not a tenant's: metric accumulators are
/// process-wide and carry no tenant identity, so filing them under a
/// tenant scope would assert an isolation that the source data does not
/// have (GP 4 — the partition is structural, and this data belongs
/// outside every tenant's).
[<Literal>]
let ScopeId = "_platform"

/// Render a series id: the metric name followed by its tag set in
/// Prometheus label syntax, or the bare name when the series is
/// tag-free. Tags arrive sorted from the snapshot, so the id is stable
/// across flushes. Values are escaped exactly as the exporter escapes
/// label values, so an id round-trips through a log line unambiguously.
let seriesId (metric: string) (tags: (string * string) list) : string =
    if List.isEmpty tags then
        metric
    else
        let rendered =
            tags
            |> List.map (fun (k, v) ->
                let escaped = v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n")

                sprintf "%s=\"%s\"" k escaped)
            |> String.concat ","

        metric + "{" + rendered + "}"

/// The per-field series a histogram fans out to, in the order they are
/// appended. Named here rather than inline so the tests and any future
/// reader agree on the vocabulary.
let histogramFields = [ "count"; "sum"; "min"; "max"; "p50"; "p95"; "p99" ]

/// Turn one snapshot into the points to append and the counter totals to
/// carry into the next flush. Pure — the whole decision surface of the
/// flusher, driven by the tests without a store, a clock or a sink.
///
/// `previous` maps a counter's series id to the total observed at the
/// last flush; ids absent from it are flushing for the first time. The
/// returned map REPLACES it: a series that has disappeared from the
/// registry (impossible today — the registry never drops a series — but
/// cheap to be right about) drops out rather than accumulating forever.
let planFlush
    (now: DateTimeOffset)
    (previous: Map<string, float>)
    (samples: MetricSeriesSample list)
    : (string * TimeSeriesPoint) list * Map<string, float> =
    let point (value: float) : TimeSeriesPoint = { Timestamp = now; Value = value }

    /// One sample's points, beside the counter total it carries forward
    /// (empty for everything that is not a counter).
    let perSample (sample: MetricSeriesSample) =
        let id = seriesId sample.Metric sample.Tags

        match sample.Kind, sample.Value with
        | Counter, MetricSampleValue.Scalar total ->
            let delta =
                match Map.tryFind id previous with
                // A total that has gone down is a reset, not a negative
                // rate — take the current value.
                | Some prior when total >= prior -> total - prior
                | _ -> total

            [ id, point delta ], [ id, total ]
        | _, MetricSampleValue.Scalar value -> [ id, point value ], []
        | _, MetricSampleValue.Histogram h ->
            let byField = [ float h.Count; h.Sum; h.Min; h.Max; h.P50; h.P95; h.P99 ]

            let points =
                List.zip histogramFields byField
                |> List.map (fun (field, value) -> seriesId (sample.Metric + "." + field) sample.Tags, point value)

            points, []

    let planned = samples |> List.map perSample

    planned |> List.collect fst, planned |> List.collect snd |> Map.ofList

/// Sample the registry once and append the resulting points. Returns the
/// counter totals for the next flush beside the store failures this
/// flush hit — the service logs them; a test asserts on them without a
/// logger. The counter state advances even when the append failed, so a
/// store outage costs one interval rather than folding it into the next.
let flushOnce
    (readSamples: unit -> MetricSeriesSample list)
    (store: ITimeSeriesStore)
    (previous: Map<string, float>)
    (now: DateTimeOffset)
    : Async<Map<string, float> * (string * TimeSeriesError) list> =
    async {
        let points, nextTotals = planFlush now previous (readSamples ())
        let failures = ResizeArray<string * TimeSeriesError>()

        for id, p in points do
            match! store.Append(ScopeId, id, [ p ]) with
            | Ok() -> ()
            | Error e -> failures.Add((id, e))

        return nextTotals, List.ofSeq failures
    }

/// Drop every point older than `cutoff`, series by series. The store
/// deletes whole series only, so each series is queried for the points
/// worth keeping, deleted, and the keepers appended back — the pattern
/// `ITimeSeriesStore`'s own `DeleteSeries` note prescribes. A series
/// entirely inside the keep-window is left alone rather than
/// delete-and-restored, so the common case touches nothing.
let sweepRetention
    (store: ITimeSeriesStore)
    (cutoff: DateTimeOffset)
    (now: DateTimeOffset)
    : Async<int * (string * TimeSeriesError) list> =
    async {
        // `QueryRange` is half-open and refuses `until <= from`, so the
        // upper bound has to be strictly past the newest possible point.
        let horizon = now.AddDays 1.0
        let! series = store.ListSeries ScopeId
        let failures = ResizeArray<string * TimeSeriesError>()
        let swept = ref 0

        for s in series do
            let! all = store.QueryRange(ScopeId, s, DateTimeOffset.MinValue, horizon, None)

            match all with
            | Error e -> failures.Add((s, e))
            | Ok points ->
                let kept = points |> List.filter (fun p -> p.Timestamp >= cutoff)

                if kept.Length < points.Length then
                    swept.Value <- swept.Value + 1

                    match! store.DeleteSeries(ScopeId, s) with
                    | Error e -> failures.Add((s, e))
                    | Ok() ->
                        match! store.Append(ScopeId, s, kept) with
                        | Error e -> failures.Add((s, e))
                        | Ok() -> ()

        return swept.Value, List.ofSeq failures
    }

/// `BackgroundService` host for the flusher. Resolves both its
/// dependencies from the captured `IServiceProvider` per tick — the
/// concrete `PrometheusMetricsSink` (absent when metrics are disabled)
/// and `ITimeSeriesStore` (absent when no time-series substrate is
/// composed) — so a companion store registered up to end-of-compose is
/// seen. Either absence is named ONCE at `Warn` and then tolerated
/// silently: the deployment asked for a history it cannot have, and
/// repeating that every minute would be the noise, not the signal.
///
/// The tick body is `flushOnce` / `sweepRetention`, which the tests call
/// directly without the scheduling overhead.
type MetricsHistoryFlusherService(serviceProvider: IServiceProvider, config: MetricsHistoryConfig, logger: ILogger) =
    inherit BackgroundService()

    /// Floored at one second: a mis-set cadence must not spin the loop.
    let interval = TimeSpan.FromSeconds(float (max 1 config.FlushSeconds))

    let mutable counterTotals: Map<string, float> = Map.empty
    let mutable warnedNoSink = false
    let mutable warnedNoStore = false
    let mutable lastSweep = DateTimeOffset.MinValue

    let readSink () =
        match serviceProvider.GetService typeof<PrometheusMetricsSink> with
        | :? PrometheusMetricsSink as sink -> Some sink
        | _ -> None

    let readStore () =
        match serviceProvider.GetService typeof<ITimeSeriesStore> with
        | :? ITimeSeriesStore as store -> Some store
        | _ -> None

    let logFailures (what: string) (failures: (string * TimeSeriesError) list) =
        match failures with
        | [] -> ()
        | (series, err) :: _ ->
            logger.Warn(
                sprintf
                    "[MetricsHistory] %s: %d series failed; first was '%s': %s"
                    what
                    failures.Length
                    series
                    (TimeSeriesError.describe err)
            )

    /// One scheduled tick: flush, then sweep if a day has passed since
    /// the last sweep. Exposed so a test can drive the service's own
    /// sequencing decisions without a wall-clock wait.
    member _.Tick(now: DateTimeOffset) : Async<unit> = async {
        match readSink (), readStore () with
        | None, _ ->
            if not warnedNoSink then
                warnedNoSink <- true

                logger.Warn(
                    "[MetricsHistory] enabled, but no PrometheusMetricsSink is composed — "
                    + "set ServerConfig.MetricsEndpoint = EnabledMetricsEndpoint. No history is being recorded."
                )
        | _, None ->
            if not warnedNoStore then
                warnedNoStore <- true

                logger.Warn(
                    "[MetricsHistory] enabled, but no ITimeSeriesStore is composed — "
                    + "set ServerConfig.TimeSeriesStore, or register a companion store. "
                    + "No history is being recorded."
                )
        | Some sink, Some store ->
            let! nextTotals, failures = flushOnce sink.Snapshot store counterTotals now
            counterTotals <- nextTotals
            logFailures "flush" failures

            if config.RetentionDays > 0 && now - lastSweep >= TimeSpan.FromDays 1.0 then
                lastSweep <- now
                let cutoff = now.AddDays(float -config.RetentionDays)
                let! swept, sweepFailures = sweepRetention store cutoff now
                logFailures "retention sweep" sweepFailures

                if swept > 0 then
                    logger.Info(sprintf "[MetricsHistory] retention sweep trimmed %d series" swept)
    }

    override this.ExecuteAsync(stoppingToken: CancellationToken) =
        task {
            // The first sweep is due one full day after startup, not on
            // the first tick: a process that restarts hourly would
            // otherwise rewrite every series on every boot.
            lastSweep <- DateTimeOffset.UtcNow

            while not stoppingToken.IsCancellationRequested do
                try
                    do! Task.Delay(interval, stoppingToken)

                    do! this.Tick DateTimeOffset.UtcNow |> Async.StartAsTask :> Task
                with
                | :? OperationCanceledException -> ()
                | ex -> logger.Error("[MetricsHistory] flush tick failed", Some ex)
        }
        :> Task