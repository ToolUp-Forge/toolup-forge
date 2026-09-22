// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.MetricsHistoryFlusherTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Metrics
open ToolUp.Platform.MetricsHistoryFlusher

// ─── Phase 829 — metrics history ────────────────────────────────────
//
// Drives the flusher over a seeded `PrometheusMetricsSink` and the
// in-memory `ITimeSeriesStore`, i.e. the two shipped defaults, so the
// assertions are about the seam and not about a fake of either side.
// The `BackgroundService` scheduling loop is not exercised — the
// testable surface is `flushOnce` / `sweepRetention`, which the service
// calls inline; a scripted `now` per flush models time passing without a
// wall-clock wait (the shape `AlertRuleEngineTests` uses for `runTick`).
//
// The percentile assertions are deliberately arithmetic rather than
// approximate: the estimate is bucket interpolation over counts, so it
// is exactly reproducible, and a test that only asserted "somewhere in
// range" would pass over a swapped-in wrong quantile.

let private noopLogger =
    { new ILogger with
        member _.Debug(_: string) = ()
        member _.Info(_: string) = ()
        member _.Warn(_: string) = ()
        member _.Error(_: string, _: exn option) = ()
    }

let private baseTime = DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero)

let private counterMetric: MetricDefinition = {
    Name = "toolup.test.requests.total"
    Kind = Counter
    Description = "Requests"
    Unit = "1"
    Tags = [ "route" ]
}

let private gaugeMetric: MetricDefinition = {
    Name = "toolup.test.queue.depth"
    Kind = Gauge
    Description = "Queue depth"
    Unit = "1"
    Tags = []
}

let private histogramMetric: MetricDefinition = {
    Name = "toolup.test.duration.ms"
    Kind = Histogram [ 10.0; 50.0; 100.0 ]
    Description = "Duration"
    Unit = "ms"
    Tags = [ "route" ]
}

/// A sink carrying the three metric shapes, with nothing observed yet.
let private emptySink () =
    PrometheusMetricsSink(
        MetricsSinkConfig.defaults,
        [ counterMetric; gaugeMetric; histogramMetric ]
        |> List.map (fun d -> { Module = None; Definition = d }),
        noopLogger
    )

/// The sink the flush assertions run against: 3 requests on `/a`, a
/// queue depth of 7, and three durations spanning the bucket set
/// (5ms → bucket 10, 30ms → bucket 50, 300ms → past every bound).
let private seededSink () =
    let sink = emptySink ()
    let emit = sink :> IMetricsSink
    let route = Map.ofList [ "route", "/a" ]

    for _ in 1..3 do
        emit.Increment(counterMetric.Name, route)

    emit.SetGauge(gaugeMetric.Name, 7.0, Map.empty)

    for ms in [ 5.0; 30.0; 300.0 ] do
        emit.Record(histogramMetric.Name, ms, route)

    sink

let private routed (name: string) = name + "{route=\"/a\"}"

/// Every point of one series, ascending, over a window wide enough to
/// hold anything these tests write.
let private pointsOf (store: ITimeSeriesStore) (series: string) = async {
    let! result = store.QueryRange(ScopeId, series, baseTime.AddDays -365.0, baseTime.AddDays 365.0, None)

    return
        match result with
        | Ok points -> points
        | Error e -> failwithf "QueryRange failed: %s" (TimeSeriesError.describe e)
}

let private valuesOf (store: ITimeSeriesStore) (series: string) = async {
    let! points = pointsOf store series
    return points |> List.map _.Value
}

/// Register the flusher against a config and report how many hosted
/// services the registration added.
let private hostedServicesFor (config: ServerConfig) =
    let services = ServiceCollection()
    ComposeObservability.registerMetricsHistory (services :> IServiceCollection) config noopLogger

    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<Microsoft.Extensions.Hosting.IHostedService>)
    |> Seq.length

[<Tests>]
let tests =
    testList "Phase 829 — MetricsHistoryFlusher" [
        testList "series ids" [
            test "a tag-free series is the bare metric name" {
                Expect.equal (seriesId "toolup.test.queue.depth" []) "toolup.test.queue.depth" "no braces when tag-free"
            }

            test "tags render in Prometheus label syntax, in the order given" {
                Expect.equal
                    (seriesId "toolup.test.requests.total" [ "method", "GET"; "route", "/a" ])
                    "toolup.test.requests.total{method=\"GET\",route=\"/a\"}"
                    "label syntax"
            }

            test "a quoting tag value is escaped, not left ambiguous" {
                Expect.equal
                    (seriesId "toolup.test.requests.total" [ "route", "/a\"b" ])
                    "toolup.test.requests.total{route=\"/a\\\"b\"}"
                    "escaped quote"
            }
        ]

        testCaseAsync "one flush appends one point per observed series"
        <| async {
            let sink = seededSink ()
            let store = InMemoryTimeSeriesStore.create ()
            let! _, failures = flushOnce sink.Snapshot store Map.empty baseTime
            Expect.isEmpty failures "no store failures"

            let! series = store.ListSeries ScopeId

            // 1 counter + 1 gauge + 7 histogram fields.
            Expect.equal (List.length series) 9 "one series per (metric, tag set), histograms fanned out by field"

            let! counter = valuesOf store (routed counterMetric.Name)
            Expect.equal counter [ 3.0 ] "the first flush of a counter records the accumulated total"

            let! gauge = valuesOf store gaugeMetric.Name
            Expect.equal gauge [ 7.0 ] "a gauge is recorded as read"

            let! points = pointsOf store gaugeMetric.Name
            Expect.equal (points |> List.map _.Timestamp) [ baseTime ] "points are stamped with the flush time"

            // A registered metric nothing has emitted contributes nothing:
            // the tag-free counter series was never created.
            Expect.isFalse
                (series |> List.contains counterMetric.Name)
                "an unobserved tag set is not invented by the snapshot"
        }

        testCaseAsync "a second flush appends the counter DELTA, not the total"
        <| async {
            let sink = seededSink ()
            let store = InMemoryTimeSeriesStore.create ()
            let! totals, _ = flushOnce sink.Snapshot store Map.empty baseTime

            // Two more requests between the flushes.
            for _ in 1..2 do
                (sink :> IMetricsSink).Increment(counterMetric.Name, Map.ofList [ "route", "/a" ])

            let! _, failures = flushOnce sink.Snapshot store totals (baseTime.AddMinutes 1.0)
            Expect.isEmpty failures "no store failures"

            let! values = valuesOf store (routed counterMetric.Name)
            Expect.equal values [ 3.0; 2.0 ] "second point is the interval's increment, not the running total of 5"

            // The gauge is NOT differenced — it is a reading, not a
            // counter, and re-reading the same value twice is a fact
            // about the queue, not a zero.
            let! gauge = valuesOf store gaugeMetric.Name
            Expect.equal gauge [ 7.0; 7.0 ] "a gauge repeats its reading"
        }

        testCaseAsync "a counter that has gone backwards is treated as a reset"
        <| async {
            let sink = seededSink ()
            let store = InMemoryTimeSeriesStore.create ()
            let id = routed counterMetric.Name

            // A prior total ABOVE the current one models the process
            // that owned the series restarting its accumulator.
            let! _, _ = flushOnce sink.Snapshot store (Map.ofList [ id, 11.0 ]) baseTime

            let! values = valuesOf store id
            Expect.equal values [ 3.0 ] "the post-reset value is the delta, never a negative rate"
        }

        testCaseAsync "histogram fields round-trip through the store"
        <| async {
            let sink = seededSink ()
            let store = InMemoryTimeSeriesStore.create ()
            let! _, failures = flushOnce sink.Snapshot store Map.empty baseTime
            Expect.isEmpty failures "no store failures"

            let field name =
                routed (histogramMetric.Name + "." + name)

            let! count = valuesOf store (field "count")
            Expect.equal count [ 3.0 ] "count"

            let! sum = valuesOf store (field "sum")
            Expect.equal sum [ 335.0 ] "sum of 5 + 30 + 300"

            let! minV = valuesOf store (field "min")
            Expect.equal minV [ 5.0 ] "min is exact, not a bucket edge"

            let! maxV = valuesOf store (field "max")
            Expect.equal maxV [ 300.0 ] "max is exact, and past every declared bound"

            // p50 over cumulative counts {10 → 1, 50 → 2, 100 → 2} with
            // 3 observations: the 1.5th observation sits in the (10, 50]
            // bucket, half way through its single member → 10 + 40·0.5.
            let! p50 = valuesOf store (field "p50")
            Expect.equal p50 [ 30.0 ] "p50 interpolates within the bucket that reaches the rank"

            // p95 / p99 fall past the largest bound, where the textbook
            // form yields +Inf; the observed max is the honest answer.
            let! p95 = valuesOf store (field "p95")
            Expect.equal p95 [ 300.0 ] "p95 resolves to the observed max rather than +Inf"

            let! p99 = valuesOf store (field "p99")
            Expect.equal p99 [ 300.0 ] "p99 likewise"
        }

        testCaseAsync "an unobserved histogram reports zeroes rather than infinities"
        <| async {
            let sink = emptySink ()
            (sink :> IMetricsSink).Increment(counterMetric.Name, Map.ofList [ "route", "/a" ])

            // Touch the histogram's series without observing into it is
            // not possible through the write surface, so assert the
            // snapshot's own guard instead: nothing was recorded, so no
            // histogram series exists at all.
            let samples = sink.Snapshot()

            Expect.isFalse
                (samples
                 |> List.exists (fun s ->
                     match s.Value with
                     | MetricSampleValue.Histogram _ -> true
                     | _ -> false))
                "no histogram series before the first observation"

            (sink :> IMetricsSink).Record(histogramMetric.Name, 7.0, Map.ofList [ "route", "/a" ])

            let after =
                sink.Snapshot()
                |> List.pick (fun s ->
                    match s.Value with
                    | MetricSampleValue.Histogram h -> Some h
                    | _ -> None)

            Expect.equal after.Count 1L "one observation"
            Expect.isFalse (Double.IsInfinity after.Min) "min is a real observation, never the sentinel"
            Expect.isFalse (Double.IsInfinity after.Max) "max is a real observation, never the sentinel"
        }

        testCaseAsync "the Phase 161 downsample works unchanged over flushed points"
        <| async {
            let sink = seededSink ()
            let store = InMemoryTimeSeriesStore.create ()
            let emit = sink :> IMetricsSink

            // Three flushes a minute apart, the gauge moving each time.
            let totals = ref Map.empty

            for minute, depth in [ 0.0, 7.0; 1.0, 9.0; 2.0, 11.0 ] do
                emit.SetGauge(gaugeMetric.Name, depth, Map.empty)
                let! next, _ = flushOnce sink.Snapshot store totals.Value (baseTime.AddMinutes minute)
                totals.Value <- next

            let! downsampled =
                store.QueryRange(
                    ScopeId,
                    gaugeMetric.Name,
                    baseTime,
                    baseTime.AddMinutes 5.0,
                    Some {
                        Bucket = TimeSpan.FromMinutes 5.0
                        Aggregation = TimeSeriesAggregation.Average
                    }
                )

            match downsampled with
            | Error e -> failtestf "QueryRange failed: %s" (TimeSeriesError.describe e)
            | Ok points ->
                Expect.equal (List.length points) 1 "all three minutes fall in one five-minute bucket"
                Expect.equal points.Head.Value 9.0 "mean of 7, 9 and 11"
                Expect.equal points.Head.Timestamp baseTime "the bucket is stamped at its start"
        }

        testCaseAsync "the retention sweep drops points past the cutoff and keeps the rest"
        <| async {
            let sink = seededSink ()
            let store = InMemoryTimeSeriesStore.create ()
            let totals = ref Map.empty

            for daysAgo in [ 40.0; 31.0; 2.0 ] do
                let! next, _ = flushOnce sink.Snapshot store totals.Value (baseTime.AddDays -daysAgo)
                totals.Value <- next

            let! swept, failures = sweepRetention store (baseTime.AddDays -30.0) baseTime
            Expect.isEmpty failures "no store failures"
            Expect.equal swept 9 "every series carried points past the cutoff"

            let! points = pointsOf store gaugeMetric.Name

            Expect.equal
                (points |> List.map _.Timestamp)
                [ baseTime.AddDays -2.0 ]
                "only the in-window point survives, and it survives intact"

            // A second sweep with nothing to do touches nothing — the
            // delete-and-re-append is not paid by the common case.
            let! sweptAgain, _ = sweepRetention store (baseTime.AddDays -30.0) baseTime
            Expect.equal sweptAgain 0 "a series entirely inside the window is left alone"
        }

        testList "registration" [
            test "the default mode registers nothing" {
                Expect.equal (hostedServicesFor ServerConfig.defaults) 0 "NoMetricsHistory composes no hosted service"
            }

            test "the enabled mode registers the flusher" {
                let config = {
                    ServerConfig.defaults with
                        MetricsHistory = EnabledMetricsHistory MetricsHistoryConfig.defaults
                }

                Expect.equal (hostedServicesFor config) 1 "one hosted service"
            }

            test "a serverless host registers nothing even when enabled" {
                let config = {
                    ServerConfig.defaults with
                        MetricsHistory = EnabledMetricsHistory MetricsHistoryConfig.defaults
                        ServerlessHost = ServerlessHost
                }

                Expect.equal (hostedServicesFor config) 0 "no long-running tick where none can be kept alive"
            }

            test "the documented defaults are the ones the config carries" {
                Expect.equal MetricsHistoryConfig.defaults.FlushSeconds 60 "60-second cadence"
                Expect.equal MetricsHistoryConfig.defaults.RetentionDays 30 "30 days of history"
            }
        ]
    ]