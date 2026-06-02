module ToolUp.RAG.RagHealthHandler

open Microsoft.AspNetCore.Http
open Giraffe
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.IRagTelemetry

let private jsonOptions = FableConverters.create ()

/// `/health/rag` route: returns the current `RagTelemetrySnapshot` as JSON.
/// Resolves `IRagTelemetry` from DI — `composeWithRAG` registers a default
/// rolling-window implementation, so the endpoint is always reachable when
/// RAG is wired. Deployments wanting Prometheus / OTel export register a
/// custom implementation; the same snapshot shape works for any backend.
///
/// Intentionally unauthenticated. The shape contains only aggregate
/// counts and latencies — no query plaintext, no per-team / per-user
/// breakdowns. Operations dashboards typically scrape it; lock down at
/// the reverse proxy if your deployment requires it.
let healthHandler: HttpHandler =
    fun (next: HttpFunc) (ctx: HttpContext) -> task {
        let telemetry =
            ctx.RequestServices.GetService(typeof<IRagTelemetry>) :?> IRagTelemetry

        if isNull (box telemetry) then
            ctx.Response.StatusCode <- 503
            return! text "RAG telemetry not registered" next ctx
        else
            let! snapshot = telemetry.Snapshot() |> Async.StartAsTask
            ctx.Response.ContentType <- "application/json"
            // Cache for ~one telemetry tick. The underlying snapshot is a
            // 60s rolling-window aggregate; dashboards scraping at 1-10 Hz
            // would otherwise let CDN/proxy layers cache it indefinitely.
            ctx.SetHttpHeader("Cache-Control", "max-age=10, must-revalidate")
            let json = JsonSerializer.Serialize(snapshot, jsonOptions)
            return! text json next ctx
    }

/// Mount under `route "/health/rag"` in `composeWithRAG`'s extension handlers.
let route: HttpHandler = route "/health/rag" >=> healthHandler