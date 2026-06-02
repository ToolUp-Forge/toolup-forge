// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AdAnalyticsApiHandler

open System
open Giraffe
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 60 — server-side ad-analytics endpoint ─────────────────
//
// Mounted only when `ServerConfig.AdAnalytics = EnabledAdAnalytics`.
// Receives `AdImpression` / `AdClick` posts from the client-side
// `ServerSinkAdAnalytics` and records via `IAuditLog` under
// `_platform` scope.
//
// Recorded under `_platform` scope (deployment-wide; no tenant
// scope — ads run on anonymous traffic).

let private jsonOptions = FableConverters.create ()

let private PlatformScope = "_platform"

let private readBody (ctx: HttpContext) : System.Threading.Tasks.Task<string> = task {
    use reader = new System.IO.StreamReader(ctx.Request.Body)
    return! reader.ReadToEndAsync()
}

let private impressionHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let! body = readBody ctx

        let event =
            try
                Some(JsonSerializer.Deserialize<AdImpression>(body, jsonOptions))
            with _ ->
                None

        match event with
        | None ->
            ctx.Response.StatusCode <- 400
            return! ctx.WriteTextAsync "Malformed AdImpression payload"
        | Some ev ->
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as auditLog ->
                auditLog.Record(PlatformScope, AuditEvent.AdImpressionRecorded ev)
                |> Async.Start
            | _ -> ()

            ctx.Response.StatusCode <- 204
            return! next ctx
    }

let private clickHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let! body = readBody ctx

        let event =
            try
                Some(JsonSerializer.Deserialize<AdClick>(body, jsonOptions))
            with _ ->
                None

        match event with
        | None ->
            ctx.Response.StatusCode <- 400
            return! ctx.WriteTextAsync "Malformed AdClick payload"
        | Some ev ->
            match ctx.RequestServices.GetService(typeof<IAuditLog>) with
            | :? IAuditLog as auditLog -> auditLog.Record(PlatformScope, AuditEvent.AdClickRecorded ev) |> Async.Start
            | _ -> ()

            ctx.Response.StatusCode <- 204
            return! next ctx
    }

/// Routes table. Mounted by `compose` only when
/// `ServerConfig.AdAnalytics = EnabledAdAnalytics`.
let routes: HttpHandler list = [
    POST >=> route "/api/_platform/ads/impression" >=> impressionHandler
    POST >=> route "/api/_platform/ads/click" >=> clickHandler
]