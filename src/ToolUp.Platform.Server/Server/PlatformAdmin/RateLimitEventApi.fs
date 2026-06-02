// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.RateLimitEventApi

open System
open Giraffe
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 61 — RateLimit recent-decisions admin endpoint ─────────
//
//   GET /api/_platform/admin/rate-limits[?count=N]
//
// Read-only surface over `IRateLimitStore.GetRecentDecisions`.
// Platform-Admin gated (Phase 4b). Always mounted — the default
// `InMemoryRateLimitStore` is always present (Phase 56), so the
// route returns at worst an empty list rather than 404-ing.
//
// **Six-rule portability audit on `IRateLimitStore.GetRecentDecisions`**
// (Guiding Principle 12; reproduces the per-rule analysis in
// `IRateLimitStore.fs`):
//
//   1. Identity by value     — `InboundRateLimitKey` is a serialisable
//                              DU over `string`; the returned
//                              `RateLimitDecisionEvent` carries only
//                              by-value primitives (string route,
//                              DateTimeOffset, enum DU cases).
//   2. Async at every method — yes (`Async<RateLimitDecisionEvent list>`).
//   3. Retry / supervision as data — the read path does not throw;
//                              best-effort retention is documented on
//                              the interface (last N events). No
//                              `OnFailure` callback.
//   4. Stateless between calls — the read derives from store state
//                              alone; an Orleans grain that deactivates
//                              between calls re-resolves correctly.
//   5. No cross-shard ordering promises — events return newest-first
//                              within the store's retention buffer; no
//                              global ordering across partitions is
//                              claimed (in-memory default is single-
//                              instance and so trivially in-order;
//                              future Redis impl will be best-effort).
//   6. Precision at the lower bound — n/a; the read returns timestamps
//                              the writer set, no scheduling semantics.

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

let private resolveStore (ctx: HttpContext) : IRateLimitStore option =
    match ctx.RequestServices.GetService(typeof<IRateLimitStore>) with
    | :? IRateLimitStore as store -> Some store
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

let private parseCount (ctx: HttpContext) : int =
    match ctx.Request.Query.TryGetValue "count" with
    | true, values when values.Count > 0 ->
        match Int32.TryParse(values[0]) with
        | true, n when n > 0 && n <= 1000 -> n
        | _ -> 100
    | _ -> 100

let private recentDecisionsHandler: HttpHandler =
    fun _next (ctx: HttpContext) -> task {
        let accessContext = resolveAccessContext ctx

        if not (AccessContext.canModifyPlatformConfig accessContext) then
            return! writeError ctx 403 "platform admin role required"
        else
            match resolveStore ctx with
            | None -> return! writeError ctx 503 "rate-limit store substrate not configured"
            | Some store ->
                let count = parseCount ctx
                let! events = store.GetRecentDecisions(keyFilter = None, count = count) |> Async.StartAsTask
                return! writeJson ctx 200 events
    }

/// Routes table. Always mounted — the read handler gates on
/// Platform-Admin role server-side, so unconditional registration is
/// zero-cost when the deployment doesn't enable rate-limit policies
/// (the default `InMemoryRateLimitStore` returns an empty event list).
let routes: HttpHandler list = [ GET >=> route "/api/_platform/admin/rate-limits" >=> recentDecisionsHandler ]