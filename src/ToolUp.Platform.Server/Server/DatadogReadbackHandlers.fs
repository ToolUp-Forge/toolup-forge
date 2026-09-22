// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.DatadogReadbackHandlers

open System
open System.Text.Json
open Giraffe
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Metrics

// ─── Phase 9w — Datadog readback endpoints ────────────────────────────
//
//   GET /api/observability/datadog/monitors[?tags=a:b,c:d]
//   GET /api/observability/datadog/logs[?query=...&minutes=N&limit=N]
//   GET /api/observability/datadog/metric?metric=...[&tags=a:b&minutes=N]
//
// Read-only surface over a DI-registered `IDatadogReadbackApi` — the
// `ToolUp.Observability.Datadog` companion is the shipped
// implementation. Mounted only when
// `ServerConfig.DatadogReadback = EnabledDatadogReadback`, so a
// deployment that does not read from Datadog carries no route and pays
// nothing (GP 13).
//
// **Platform-Admin gated.** Monitor state, error logs and metric series
// are deployment-wide data, so the gate is deployment-wide too —
// `AccessContext.canModifyPlatformConfig`, the same predicate the health
// monitor's own handler was re-gated onto at Phase 4b. A team Owner is
// not sufficient.
//
// **SDK record shapes, never vendor JSON.** Every response body is one
// of the three `Datadog*Response` records declared in `Platform.Core`;
// Datadog's own attribute bags never reach a client. That is what keeps
// the eventual `IObservabilityReadback` generalisation a rename rather
// than a wire break.
//
// **Soft failure is the contract, not an accident.** A Datadog outage,
// a rotated-away key or a misconfigured region must degrade the admin
// surface to "no data plus one warning", never blank it or 500 it. So
// every handler answers 200 with an empty payload and a `Warning`
// string, and records the failure in the two places an operator looks:
// the `toolup.datadog_readback.errors_total` counter and a
// `DatadogReadbackFailed` event on `IEventStore`.
//
// **Why the failure is an `IEventStore` event and not an `AuditEvent`
// case.** `AuditEvent` is a closed union matched exhaustively in a dozen
// places under repo-wide `FS0025`-as-error, with a codec registry and a
// generated reference document pinned by a reflection census — adding a
// case for one companion's failure is a breaking change to every one of
// them. The audit substrate's own wire format IS `ModuleEvent`
// (`AuditTypes.fs` says so), so writing one directly under a reserved
// `SourceModule` lands in the same blob layout, the same retention
// policy and the same `ReadBySource` query surface, and breaks nothing.

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

let private resolveApi (ctx: HttpContext) : IDatadogReadbackApi option =
    match ctx.RequestServices.GetService(typeof<IDatadogReadbackApi>) with
    | :? IDatadogReadbackApi as api -> Some api
    | _ -> None

let private resolveMetrics (ctx: HttpContext) : IMetricsSink option =
    match ctx.RequestServices.GetService(typeof<IMetricsSink>) with
    | :? IMetricsSink as sink -> Some sink
    | _ -> None

let private resolveEventStore (ctx: HttpContext) : IEventStore option =
    match ctx.RequestServices.GetService(typeof<IEventStore>) with
    | :? IEventStore as store -> Some store
    | _ -> None

let private writeError (ctx: HttpContext) (statusCode: int) (message: string) : HttpFuncResult = task {
    ctx.Response.StatusCode <- statusCode
    return! ctx.WriteTextAsync message
}

let private writeJson (ctx: HttpContext) (statusCode: int) (payload: 'T) : HttpFuncResult = task {
    let json = JsonSerializer.Serialize(payload, jsonOptions)
    ctx.Response.StatusCode <- statusCode
    ctx.Response.ContentType <- "application/json; charset=utf-8"
    return! ctx.WriteTextAsync json
}

// ─── Query-string reads ───────────────────────────────────────────────

let private queryValue (ctx: HttpContext) (name: string) : string option =
    match ctx.Request.Query.TryGetValue name with
    | true, values when values.Count > 0 && not (String.IsNullOrWhiteSpace values[0]) -> Some values[0]
    | _ -> None

let private queryInt (ctx: HttpContext) (name: string) : int =
    match queryValue ctx name with
    | Some raw ->
        match Int32.TryParse raw with
        | true, n -> n
        | _ -> 0
    | None -> 0

/// Comma-separated `key:value` tags from the query string. An absent or
/// blank parameter falls back to the deployment's configured default
/// narrowing rather than to "no narrowing" — on a shared Datadog
/// organization the unnarrowed query returns other services' data, so
/// the wider read has to be asked for explicitly.
let private queryTags (ctx: HttpContext) (config: DatadogReadbackConfig) : string list =
    match queryValue ctx "tags" with
    | None -> config.DefaultTags
    | Some raw ->
        raw.Split(',')
        |> Array.map (fun (tag: string) -> tag.Trim())
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> List.ofArray

// ─── Failure recording ────────────────────────────────────────────────

/// The payload the `DatadogReadbackFailed` event carries. A record
/// rather than a formatted string so a downstream reader can filter by
/// endpoint or status without parsing prose.
type DatadogReadbackFailure = {
    /// Which of the three endpoints failed — `monitors` / `logs` / `metric`.
    Endpoint: string
    /// The failure class, matching the metric's `status` tag.
    Status: string
    /// The operator-facing one-line reason.
    Reason: string
}

let private countDispatch (ctx: HttpContext) (endpoint: string) : unit =
    match resolveMetrics ctx with
    | None -> ()
    | Some sink ->
        sink.Increment(
            DatadogReadbackMetrics.RequestsTotal,
            Map.ofList [ DatadogReadbackMetrics.EndpointTagKey, endpoint ]
        )

/// Record one soft failure: increment the error counter and write the
/// `DatadogReadbackFailed` event. Best-effort by design — an event
/// store that throws must not turn a degraded panel into a 500, which
/// is the failure this whole path exists to avoid.
let private recordFailure (ctx: HttpContext) (endpoint: string) (error: DatadogReadbackError) : Async<unit> = async {
    let status = DatadogReadbackError.statusTag error
    let reason = DatadogReadbackError.describe error

    match resolveMetrics ctx with
    | None -> ()
    | Some sink ->
        sink.Increment(
            DatadogReadbackMetrics.ErrorsTotal,
            Map.ofList [
                DatadogReadbackMetrics.EndpointTagKey, endpoint
                DatadogReadbackMetrics.StatusTagKey, status
            ]
        )

    match resolveEventStore ctx with
    | None -> ()
    | Some store ->
        let payload: DatadogReadbackFailure = {
            Endpoint = endpoint
            Status = status
            Reason = reason
        }

        let event: ModuleEvent = {
            Id = Guid.NewGuid()
            OccurredAt = DateTime.UtcNow
            ScopeId = DatadogReadback.SecretScope
            SourceModule = DatadogReadback.AuditSourceModule
            EventType = DatadogReadback.ReadbackFailedEvent
            Payload = JsonSerializer.Serialize(payload, jsonOptions)
        }

        try
            do! store.Write event
        with _ ->
            ()
}

// ─── Handlers ─────────────────────────────────────────────────────────

/// Shared prologue: gate on Platform-Admin, resolve the companion, count
/// the dispatch. `None` means the caller already has its response.
let private withApi
    (ctx: HttpContext)
    (endpoint: string)
    (onReady: IDatadogReadbackApi -> HttpFuncResult)
    : HttpFuncResult =
    task {
        let accessContext = resolveAccessContext ctx

        if not (AccessContext.canModifyPlatformConfig accessContext) then
            return! writeError ctx 403 "platform admin role required"
        else
            match resolveApi ctx with
            | None ->
                // The mode is on but no companion was composed. A 503 is
                // the honest answer — this is a composition defect, not a
                // Datadog outage, and degrading it to an empty panel would
                // hide it behind the same warning a real outage produces.
                return!
                    writeError ctx 503 "no IDatadogReadbackApi is registered — compose the Datadog readback companion"
            | Some api ->
                countDispatch ctx endpoint
                return! onReady api
    }

let private monitorsHandler (config: DatadogReadbackConfig) : HttpHandler =
    fun _next (ctx: HttpContext) ->
        withApi ctx DatadogReadbackMetrics.EndpointMonitors (fun api -> task {
            let tags = queryTags ctx config
            let! result = api.GetMonitors tags |> Async.StartAsTask

            match result with
            | Ok monitors -> return! writeJson ctx 200 { Monitors = monitors; Warning = None }
            | Error error ->
                do!
                    recordFailure ctx DatadogReadbackMetrics.EndpointMonitors error
                    |> Async.StartAsTask

                return!
                    writeJson ctx 200 {
                        Monitors = []
                        Warning = Some(DatadogReadbackError.describe error)
                    }
        })

let private logsHandler (config: DatadogReadbackConfig) : HttpHandler =
    fun _next (ctx: HttpContext) ->
        withApi ctx DatadogReadbackMetrics.EndpointLogs (fun api -> task {
            let query =
                queryValue ctx "query" |> Option.defaultValue DatadogReadback.DefaultErrorQuery

            let minutes =
                DatadogReadback.clampWindowMinutes config.DefaultWindowMinutes (queryInt ctx "minutes")

            let limit = DatadogReadback.clampLogLimit config.LogPageLimit (queryInt ctx "limit")
            let sinceUtc = DateTime.UtcNow.AddMinutes(-(float minutes))
            let! result = api.SearchLogs(query, sinceUtc, limit) |> Async.StartAsTask

            match result with
            | Ok events -> return! writeJson ctx 200 { Events = events; Warning = None }
            | Error error ->
                do! recordFailure ctx DatadogReadbackMetrics.EndpointLogs error |> Async.StartAsTask

                return!
                    writeJson ctx 200 {
                        Events = []
                        Warning = Some(DatadogReadbackError.describe error)
                    }
        })

let private metricHandler (config: DatadogReadbackConfig) : HttpHandler =
    fun _next (ctx: HttpContext) ->
        withApi ctx DatadogReadbackMetrics.EndpointMetric (fun api -> task {
            match queryValue ctx "metric" with
            | None -> return! writeError ctx 400 "the 'metric' query parameter is required"
            | Some metric ->
                let tags = queryTags ctx config

                let minutes =
                    DatadogReadback.clampWindowMinutes config.DefaultWindowMinutes (queryInt ctx "minutes")

                let toUtc = DateTime.UtcNow
                let fromUtc = toUtc.AddMinutes(-(float minutes))
                let! result = api.QueryMetric(metric, tags, fromUtc, toUtc) |> Async.StartAsTask

                match result with
                | Ok series -> return! writeJson ctx 200 { Series = series; Warning = None }
                | Error error ->
                    do!
                        recordFailure ctx DatadogReadbackMetrics.EndpointMetric error
                        |> Async.StartAsTask

                    return!
                        writeJson ctx 200 {
                            Series = {
                                Metric = metric
                                Points = []
                                Tags = tags
                            }
                            Warning = Some(DatadogReadbackError.describe error)
                        }
        })

/// The route table, built from the deployment's readback configuration.
/// Mounted only under `EnabledDatadogReadback` — see
/// `BuildRouteHandlers`.
let routes (config: DatadogReadbackConfig) : HttpHandler list = [
    GET >=> route "/api/observability/datadog/monitors" >=> monitorsHandler config
    GET >=> route "/api/observability/datadog/logs" >=> logsHandler config
    GET >=> route "/api/observability/datadog/metric" >=> metricHandler config
]