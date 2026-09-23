// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Self-hosted observability readback types (Phase 9x) ─────────────
//
// The record shapes the four `/api/observability/*` endpoints answer
// with, and the helpers the Observability admin module and the handlers
// share. The read side of three shipped substrates:
//
//   * logs    — the Phase 828 `ILogStore`, whose `LogRecord` is already
//               Fable-safe and is returned as-is (no parallel DTO);
//   * metrics — the `ITimeSeriesStore` series the Phase 829 flusher
//               writes under `_platform`, ids `<metric>{<tags>}`;
//   * alerts  — the Phase 178 engine's per-rule state, projected through
//               `AlertRuleObservation`.
//
// **Why here and not in the server.** Only `Platform.Core` ships its
// sources to a Fable client, so a type the admin module renders must be
// declared in this project (GP 10) — the same split the Datadog readback
// types use beside it.

/// Which self-hosted observability sources this deployment composed. The
/// admin module reads this first and renders one tab per `true` field, so
/// a deployment that enabled only the log store shows only a Logs tab.
type ObservabilitySources = {
    /// `ServerConfig.LogStore` is not `NoLogStore` — the Logs tab and
    /// `GET /api/observability/logs` are available.
    Logs: bool
    /// `ServerConfig.MetricsHistory` is not `NoMetricsHistory` — the
    /// Metrics tab and `GET /api/observability/metrics` are available.
    Metrics: bool
    /// `ServerConfig.AlertRules` is non-empty — the Alerts tab and
    /// `GET /api/observability/alerts` are available.
    Alerts: bool
}

/// The answer to a log search. The records are the store's own
/// `LogRecord` values, newest first.
type ObservabilityLogsResponse = {
    /// The matching lines, newest first, at most `Limit` of them.
    Records: LogRecord list
    /// The look-back window actually searched, in minutes, after clamping.
    WindowMinutes: int
    /// The page size actually applied, after clamping.
    Limit: int
}

/// One stored series of a metric: one tag set (or one histogram field of
/// one tag set) as the Phase 829 flusher wrote it.
type ObservabilityMetricSeries = {
    /// The stored series id — `<metric>{<tags>}`, or the bare metric name
    /// for a tag-free series.
    SeriesId: string
    /// `(timestampUtc, value)` in ascending time order. Counters are
    /// stored as per-flush deltas, gauges and histogram fields as read.
    Points: (DateTime * float) list
}

/// The answer to a metric read: every stored series of one metric over
/// the window.
type ObservabilityMetricResponse = {
    /// The metric asked for, e.g. `toolup.requests.total` or a histogram
    /// field such as `toolup.requests.latency_ms.p99`.
    Metric: string
    /// The look-back window actually read, in minutes, after clamping.
    WindowMinutes: int
    /// One entry per stored tag set, ordered by series id. Empty when
    /// nothing has been flushed for the metric in the window.
    Series: ObservabilityMetricSeries list
    /// Set when one or more series could not be read from the store. The
    /// series that could be read are still returned, so a partial store
    /// failure degrades the chart rather than blanking it.
    Warning: string option
}

/// Where a Phase 178 alert rule stands, as the Alerts tab renders it.
/// `[<RequireQualifiedAccess>]` — `Clear` / `Pending` / `NoData` are
/// generic enough to collide in the widely-opened namespace.
[<RequireQualifiedAccess>]
type AlertRuleState =
    /// No engine tick has evaluated the rule in this process yet — the
    /// engine has not run a tick since start-up, or this process does not
    /// host it (a `WebOnly` / `DispatcherOnly` silo).
    | NotEvaluated
    /// The last tick had nothing to read: the metric series has had no
    /// observation, or the probe is not registered. The engine neither
    /// fires nor re-arms on absent data.
    | NoData
    /// The signal is inside its bound.
    | Clear
    /// The signal is past its bound but has not yet been for the rule's
    /// `ForDuration`.
    | Pending
    /// The rule has fired for the current breach and has not recovered.
    | Firing

/// The engine's view of one rule, recorded after every tick.
type AlertRuleObservation = {
    /// Where the rule stands after the latest tick.
    State: AlertRuleState
    /// When the current uninterrupted breach began; `None` when the
    /// signal is not breaching.
    BreachingSinceUtc: DateTime option
    /// When the rule last fired, in this process's lifetime.
    LastFiredUtc: DateTime option
    /// When the rule last recovered from a fired breach, in this
    /// process's lifetime.
    LastClearedUtc: DateTime option
    /// When a tick last evaluated the rule.
    LastEvaluatedUtc: DateTime option
}

[<RequireQualifiedAccess>]
module AlertRuleObservation =
    /// A rule no tick has evaluated.
    let notEvaluated: AlertRuleObservation = {
        State = AlertRuleState.NotEvaluated
        BreachingSinceUtc = None
        LastFiredUtc = None
        LastClearedUtc = None
        LastEvaluatedUtc = None
    }

/// One declared rule and where it stands. The rule's definition is
/// carried pre-rendered (strings and a float) rather than as the
/// `AlertRule` itself, so the client renders exactly the text the
/// engine's own notifications use.
type AlertRuleStatus = {
    /// The rule's identity (`AlertRule.Name`).
    Name: string
    /// The watched signal, rendered — `toolup.jobs.queued`,
    /// `toolup.errors.total{route_class=api}` or `probe:<name>`.
    Signal: string
    /// The breach condition, rendered — `> 5`, `unhealthy`, …
    Condition: string
    /// The debounce window, in minutes.
    ForMinutes: float
    /// The severity the rule's in-app delivery is styled with.
    Severity: string
    /// The metric name for a `Metric` source (so the module can link to
    /// its chart); `None` for a health-probe rule.
    MetricName: string option
    /// Where the rule stands.
    Observation: AlertRuleObservation
}

/// The answer to an alerts read: every declared rule, in declaration
/// order.
type ObservabilityAlertsResponse = {
    /// One entry per declared rule, in declaration order.
    Rules: AlertRuleStatus list
}

[<RequireQualifiedAccess>]
module ObservabilityReadback =
    /// The route prefix the four endpoints share.
    [<Literal>]
    let RoutePrefix = "/api/observability"

    /// The look-back window a read uses when the caller names none.
    [<Literal>]
    let DefaultWindowMinutes = 60

    /// The longest look-back window a read accepts — one day. The metric
    /// read returns raw points, so this is what bounds a response.
    [<Literal>]
    let MaxWindowMinutes = 1440

    /// The log page size a search uses when the caller names none.
    [<Literal>]
    let DefaultLogLimit = 100

    /// The largest log page a search returns.
    [<Literal>]
    let MaxLogLimit = 500

    /// Clamp a caller-supplied look-back window, in minutes. A
    /// non-positive request takes the default; anything past a day is
    /// cut to a day.
    let clampWindowMinutes (requested: int) : int =
        if requested <= 0 then DefaultWindowMinutes
        elif requested > MaxWindowMinutes then MaxWindowMinutes
        else requested

    /// Clamp a caller-supplied log page size. A non-positive request
    /// takes the default rather than meaning "none".
    let clampLogLimit (requested: int) : int =
        if requested <= 0 then DefaultLogLimit
        elif requested > MaxLogLimit then MaxLogLimit
        else requested

    /// The standard SDK metrics the Metrics tab charts, as the series
    /// names the Phase 829 flusher writes: counters and gauges by their
    /// registered name, the latency histogram by its percentile fields.
    let defaultChartedMetrics: string list = [
        "toolup.requests.total"
        "toolup.errors.total"
        "toolup.requests.latency_ms.p50"
        "toolup.requests.latency_ms.p95"
        "toolup.requests.latency_ms.p99"
        "toolup.sse.active_connections"
        "toolup.jobs.runs.total"
    ]

    /// Whether a stored series id belongs to `metric`: the bare name (a
    /// tag-free series) or the name followed by its `{…}` tag set. A
    /// prefix match alone would be wrong — `toolup.requests.total` must
    /// not claim `toolup.requests.total_bytes`.
    let isSeriesOf (metric: string) (seriesId: string) : bool =
        seriesId = metric || seriesId.StartsWith(metric + "{", StringComparison.Ordinal)

    /// How the series of one metric combine into the single line a chart
    /// draws. A percentile or maximum field takes the MAXIMUM across tag
    /// sets — the slowest route's p99 is an upper bound on the fleet's,
    /// where a sum would be meaningless; a minimum field takes the
    /// minimum; everything else (counter deltas, gauges, histogram
    /// count / sum) is summed.
    let combine (metric: string) (values: float list) : float =
        match values with
        | [] -> 0.0
        | _ ->
            let field =
                match metric.LastIndexOf '.' with
                | -1 -> ""
                | i -> metric.Substring(i + 1)

            match field with
            | "p50"
            | "p95"
            | "p99"
            | "max" -> List.max values
            | "min" -> List.min values
            | _ -> List.sum values

    /// Fold every series of `metric` into one ascending line, combining
    /// the points that share a timestamp per `combine`. The flusher
    /// stamps every series of one flush with the same instant, so the
    /// points of one flush line up.
    let combineSeries (metric: string) (series: ObservabilityMetricSeries list) : (DateTime * float) list =
        series
        |> List.collect _.Points
        |> List.groupBy fst
        |> List.sortBy fst
        |> List.map (fun (at, points) -> at, combine metric (List.map snd points))