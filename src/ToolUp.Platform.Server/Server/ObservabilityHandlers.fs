// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.ObservabilityHandlers

open System
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 9x — self-hosted observability endpoints ──────────────────
//
//   GET /api/observability/sources
//   GET /api/observability/logs[?q=…&levels=Warn,Error&minutes=N
//                               &scope=…&correlationId=…&limit=N]
//   GET /api/observability/metrics?metric=…[&minutes=N]
//   GET /api/observability/alerts
//
// The read side of three shipped substrates, for the Observability admin
// module: the Phase 828 log store (`ILogStore.Search`), the series the
// Phase 829 flusher writes into `ITimeSeriesStore`, and the Phase 178
// alert engine's per-rule state (`AlertRuleEngine.AlertRuleStatusBoard`).
// No new store, no new evaluator — each endpoint is a projection.
//
// **Mounted per source (GP 13).** A data route exists only when its
// source is enabled — `LogStore` not `NoLogStore`, `MetricsHistory` not
// `NoMetricsHistory`, `AlertRules` non-empty — and `/sources` exists
// when any of them is. A deployment with all three off mounts nothing,
// and the admin module (which reads `/sources` first) renders only the
// tabs whose source answered.
//
// **Platform-Admin gated.** Logs, metric history and alert state are
// deployment-wide data, so the gate is deployment-wide too —
// `AccessContext.canModifyPlatformConfig`, the predicate the health
// monitor and the Datadog readback endpoints beside these already use.
// A team Owner is not sufficient.
//
// **SDK record shapes.** Every body is a record from
// `ObservabilityReadbackTypes.fs` in `Platform.Core`, so the Fable
// module parses exactly what is written here.
//
// **A mode on with nothing composed is a 503, not an empty panel** —
// the Datadog readback's rule: `SqliteLogStore` selected but a consumer
// removed the store, or `EnabledMetricsHistory` without an
// `ITimeSeriesStore`, is a composition defect, and an empty table would
// hide it behind the same render as "nothing logged yet".

let private jsonOptions = FableConverters.create ()

let private resolveAccessContext (ctx: HttpContext) : AccessContext =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as ac -> ac
    | _ ->
        let userId =
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) -> id
            | _ -> "anonymous"

        AccessContext.unrestricted (AnonymousSession userId)

let private resolve<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as service -> Some service
    | _ -> None

let private writeError (ctx: HttpContext) (statusCode: int) (message: string) : HttpFuncResult = task {
    ctx.Response.StatusCode <- statusCode
    return! ctx.WriteTextAsync message
}

let private writeJson (ctx: HttpContext) (payload: 'T) : HttpFuncResult = task {
    let json = JsonSerializer.Serialize(payload, jsonOptions)
    ctx.Response.StatusCode <- 200
    ctx.Response.ContentType <- "application/json; charset=utf-8"
    return! ctx.WriteTextAsync json
}

// ─── Query-string reads ───────────────────────────────────────────────

let private queryValue (ctx: HttpContext) (name: string) : string option =
    match ctx.Request.Query.TryGetValue name with
    | true, values when values.Count > 0 && not (String.IsNullOrWhiteSpace values[0]) -> Some(values[0].Trim())
    | _ -> None

let private queryInt (ctx: HttpContext) (name: string) : int =
    match queryValue ctx name with
    | Some raw ->
        match Int32.TryParse raw with
        | true, n -> n
        | _ -> 0
    | None -> 0

/// Comma-separated level names (`Warn,Error`). Unrecognised names are
/// dropped rather than failing the search; an absent or all-unrecognised
/// list means every level, which is `LogSearchQuery`'s own empty rule.
let private queryLevels (ctx: HttpContext) : LogLevel list =
    match queryValue ctx "levels" with
    | None -> []
    | Some raw ->
        raw.Split(',')
        |> Array.choose LogLevel.tryParse
        |> Array.distinct
        |> List.ofArray

// ─── Sources ──────────────────────────────────────────────────────────

/// Which sources `config` enables — the single definition the route
/// table, the `/sources` answer and the compose registration share.
let sourcesOf (config: ServerConfig) : ObservabilitySources = {
    Logs =
        match config.LogStore with
        | NoLogStore -> false
        | SqliteLogStore _ -> true
    Metrics =
        match config.MetricsHistory with
        | NoMetricsHistory -> false
        | EnabledMetricsHistory _ -> true
    Alerts = not (List.isEmpty config.AlertRules)
}

/// Whether any source is enabled — the condition for mounting anything.
let anyEnabled (sources: ObservabilitySources) : bool =
    sources.Logs || sources.Metrics || sources.Alerts

// ─── Pure projections ─────────────────────────────────────────────────

/// Project one declared rule and its latest observation into the record
/// the Alerts tab renders. The signal and condition are rendered by the
/// engine's own `describeSignal` / `describeCondition`, so the tab and
/// the rule's notifications read identically.
let ruleStatus (rule: AlertRule) (observation: AlertRuleObservation) : AlertRuleStatus = {
    Name = rule.Name
    Signal = AlertRuleEngine.describeSignal rule.Source
    Condition = AlertRuleEngine.describeCondition rule.Condition
    ForMinutes = rule.ForDuration.TotalMinutes
    Severity = string rule.Severity
    MetricName =
        match rule.Source with
        | Metric(name, _) -> Some name
        | HealthProbe _ -> None
    Observation = observation
}

/// Build the log query a request asks for. `now` is the exclusive upper
/// bound's anchor; one second is added so a line stamped in the current
/// (truncated) second is inside the half-open window.
let logQuery
    (text: string option)
    (levels: LogLevel list)
    (minutes: int)
    (scopeId: string option)
    (correlationId: string option)
    (limit: int)
    (now: DateTime)
    : LogSearchQuery =
    let window = ObservabilityReadback.clampWindowMinutes minutes

    {
        (LogSearchQuery.window (now.AddMinutes(-(float window))) (now.AddSeconds 1.0)) with
            TextMatch = text
            Levels = levels
            ScopeId = scopeId
            CorrelationId = correlationId
            Limit = ObservabilityReadback.clampLogLimit limit
    }

/// Read every stored series of `metric` over `[fromUtc, untilUtc)`.
/// Series that fail to read are reported in `Warning` and omitted, as are
/// series with no point in the window; the rest are returned, ordered by
/// series id.
let readMetric
    (store: ITimeSeriesStore)
    (metric: string)
    (windowMinutes: int)
    (fromUtc: DateTimeOffset)
    (untilUtc: DateTimeOffset)
    : Async<ObservabilityMetricResponse> =
    async {
        let! ids = store.ListSeries MetricsHistoryFlusher.ScopeId

        let owned =
            ids
            |> List.filter (ObservabilityReadback.isSeriesOf metric)
            |> List.distinct
            |> List.sort

        let series = ResizeArray<ObservabilityMetricSeries>()
        let failures = ResizeArray<string>()

        for id in owned do
            match! store.QueryRange(MetricsHistoryFlusher.ScopeId, id, fromUtc, untilUtc, None) with
            | Ok [] -> ()
            | Ok points ->
                series.Add {
                    SeriesId = id
                    Points = points |> List.map (fun p -> p.Timestamp.UtcDateTime, p.Value)
                }
            | Error error -> failures.Add(sprintf "%s: %s" id (TimeSeriesError.describe error))

        return {
            Metric = metric
            WindowMinutes = windowMinutes
            Series = List.ofSeq series
            Warning =
                if failures.Count = 0 then
                    None
                else
                    Some(String.concat "; " failures)
        }
    }

// ─── Handlers ─────────────────────────────────────────────────────────

/// Shared prologue: the Platform-Admin gate.
let private gated (ctx: HttpContext) (onAllowed: unit -> HttpFuncResult) : HttpFuncResult =
    if AccessContext.canModifyPlatformConfig (resolveAccessContext ctx) then
        onAllowed ()
    else
        writeError ctx 403 "platform admin role required"

let private sourcesHandler (sources: ObservabilitySources) : HttpHandler =
    fun _next (ctx: HttpContext) -> gated ctx (fun () -> writeJson ctx sources)

let private logsHandler: HttpHandler =
    fun _next (ctx: HttpContext) ->
        gated ctx (fun () -> task {
            match resolve<ILogStore> ctx with
            | None -> return! writeError ctx 503 "no ILogStore is registered — compose the self-hosted log store"
            | Some store ->
                let query =
                    logQuery
                        (queryValue ctx "q")
                        (queryLevels ctx)
                        (queryInt ctx "minutes")
                        (queryValue ctx "scope")
                        (queryValue ctx "correlationId")
                        (queryInt ctx "limit")
                        DateTime.UtcNow

                let! records = store.Search query |> Async.StartAsTask

                let response: ObservabilityLogsResponse = {
                    Records = records
                    WindowMinutes = ObservabilityReadback.clampWindowMinutes (queryInt ctx "minutes")
                    Limit = query.Limit
                }

                return! writeJson ctx response
        })

let private metricsHandler: HttpHandler =
    fun _next (ctx: HttpContext) ->
        gated ctx (fun () -> task {
            match queryValue ctx "metric" with
            | None -> return! writeError ctx 400 "the 'metric' query parameter is required"
            | Some metric ->
                match resolve<ITimeSeriesStore> ctx with
                | None -> return! writeError ctx 503 "no ITimeSeriesStore is registered — compose a time-series store"
                | Some store ->
                    let window = ObservabilityReadback.clampWindowMinutes (queryInt ctx "minutes")
                    let untilUtc = DateTimeOffset.UtcNow
                    let fromUtc = untilUtc.AddMinutes(-(float window))
                    let! response = readMetric store metric window fromUtc untilUtc |> Async.StartAsTask
                    return! writeJson ctx response
        })

let private alertsHandler (rules: AlertRule list) : HttpHandler =
    fun _next (ctx: HttpContext) ->
        gated ctx (fun () ->
            let observationOf =
                match resolve<AlertRuleEngine.AlertRuleStatusBoard> ctx with
                | Some board -> board.Get
                | None -> fun _ -> AlertRuleObservation.notEvaluated

            let response: ObservabilityAlertsResponse = {
                Rules = rules |> List.map (fun rule -> ruleStatus rule (observationOf rule.Name))
            }

            writeJson ctx response)

/// The route table for `config`. Empty when no source is enabled; each
/// data route present only when its own source is.
let routes (config: ServerConfig) : HttpHandler list =
    let sources = sourcesOf config

    if not (anyEnabled sources) then
        []
    else
        [
            GET
            >=> route (ObservabilityReadback.RoutePrefix + "/sources")
            >=> sourcesHandler sources
            if sources.Logs then
                GET >=> route (ObservabilityReadback.RoutePrefix + "/logs") >=> logsHandler
            if sources.Metrics then
                GET
                >=> route (ObservabilityReadback.RoutePrefix + "/metrics")
                >=> metricsHandler
            if sources.Alerts then
                GET
                >=> route (ObservabilityReadback.RoutePrefix + "/alerts")
                >=> alertsHandler config.AlertRules
        ]