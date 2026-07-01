module ToolUp.RAG.RagHealthHandler

open Microsoft.AspNetCore.Http
open Giraffe
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.IRagTelemetry
open ToolUp.RAG.IngestionTypes

let private jsonOptions = FableConverters.create ()

/// `/health/rag` route: returns the current `RagTelemetrySnapshot` as JSON,
/// with the ingestion queue's drop counters (Phase 303) merged on under
/// `IngestionQueueDrops`. Resolves `IRagTelemetry` from DI — `composeWithRAG`
/// registers a default rolling-window implementation, so the endpoint is
/// always reachable when RAG is wired. Deployments wanting Prometheus / OTel
/// export register a custom implementation; the same snapshot shape works for
/// any backend.
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

            // Phase 303 — merge the ingestion queue's backpressure counters
            // onto the snapshot. Additive: the snapshot's existing fields stay
            // at the top level; queue drops (rolling 60s + cumulative) plus the
            // live depth / capacity gauge appear under `IngestionQueueDrops`.
            // The queue is a singleton `composeRAG` registers whenever this
            // route is mounted; if it is somehow absent the block is omitted
            // rather than failing the endpoint.
            let node = JsonSerializer.SerializeToNode(snapshot, jsonOptions)

            match ctx.RequestServices.GetService(typeof<IngestionQueue>) with
            | :? IngestionQueue as queue ->
                let drops = {|
                    Cumulative = queue.Dropped
                    RollingLast60s = queue.DroppedLast60s
                    Depth = queue.Count
                    Capacity = queue.Capacity
                |}

                node["IngestionQueueDrops"] <- JsonSerializer.SerializeToNode(drops, jsonOptions)
            | _ -> ()

            let json = node.ToJsonString(jsonOptions)
            return! text json next ctx
    }

/// Mount under `route "/health/rag"` in `composeWithRAG`'s extension handlers.
let route: HttpHandler = route "/health/rag" >=> healthHandler