// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AdAnalyticsApiHandler

open System
open System.Text
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
//
// **Hardening (2026-06-12 audit, Platform Gap 7).** The two
// endpoints are anonymous by design (ads run on logged-out traffic)
// and previously accepted unbounded bodies verbatim into the
// `_platform` scope of the same event store that holds compliance
// audit rows. Three gates now run before any record:
//
//   1. A per-IP per-minute budget via the Phase 56 `IRateLimitStore`
//      when one is composed (`RateLimitStore <> NoRateLimitStore`).
//      No store → no gate; `AdAnalyticsRateLimitValidator` warns
//      about that combination at startup so the residual exposure is
//      loud, not silent.
//   2. A body-size cap — the event records are ~200 bytes; anything
//      larger is hostile or malformed and is refused before
//      deserialisation.
//   3. Field validation plus a slot-id sanity check against the
//      configured ad units (`AdUnitConfigApi`) so arbitrary junk
//      can't be fired into the audit event store under invented
//      slot ids.

let private jsonOptions = FableConverters.create ()

let private PlatformScope = "_platform"

/// Body cap. `AdImpression` / `AdClick` are small flat records — a
/// generous 8 KB admits any legitimate payload (long SPA paths
/// included) while refusing bulk junk before it is deserialised or
/// recorded.
[<Literal>]
let private MaxBodyBytes = 8192

/// Per-IP per-minute budgets. Impressions fire once per slot per page
/// view, so a fast-navigating user on a multi-slot site stays well
/// under 120/min; clicks are rarer by an order of magnitude.
[<Literal>]
let private ImpressionsPerMinutePerIp = 120

[<Literal>]
let private ClicksPerMinutePerIp = 30

// ─── Gates ────────────────────────────────────────────────────────

let private clientIp (ctx: HttpContext) : string =
    match ctx.Connection.RemoteIpAddress with
    | null -> "unknown"
    | addr -> addr.ToString()

/// Per-IP fixed-window budget via the Phase 56 store when one is
/// composed. Returns `Some error` on deny. Store absent → no gate
/// (validator warns at startup); store failure → fail-open, matching
/// `RateLimitMiddleware`'s contract (a store outage is the operator's
/// problem, not the caller's).
let private rateGate
    (ctx: HttpContext)
    (endpoint: string)
    (threshold: int)
    : System.Threading.Tasks.Task<RateLimitedError option> =
    task {
        match ctx.RequestServices.GetService(typeof<IRateLimitStore>) with
        | :? IRateLimitStore as store ->
            let key = InboundComposite(sprintf "ads:%s|ip:%s" endpoint (clientIp ctx))

            let! decision = store.IncrementAndCheck(key, PerMinute, threshold) |> Async.StartAsTask

            match decision with
            | Ok(AllowWithRemaining _) -> return None
            | Ok(DenyWithError rle) -> return Some rle
            | Error _ -> return None
        | _ -> return None
    }

let private writeRateLimited (ctx: HttpContext) (rle: RateLimitedError) : HttpFuncResult = task {
    ctx.SetHttpHeader("Retry-After", string rle.RetryAfterSeconds)
    ctx.Response.StatusCode <- 429
    return! ctx.WriteTextAsync "Rate limit exceeded"
}

/// Read the request body up to `MaxBodyBytes`. `None` when the
/// declared or actual length exceeds the cap.
let private readBodyCapped (ctx: HttpContext) : System.Threading.Tasks.Task<string option> = task {
    let declared = ctx.Request.ContentLength

    if declared.HasValue && declared.Value > int64 MaxBodyBytes then
        return None
    else
        // Read manually rather than trusting Content-Length — chunked
        // requests carry none, and a hostile client can lie about it.
        let buffer = Array.zeroCreate (MaxBodyBytes + 1)
        let mutable total = 0
        let mutable finished = false

        while not finished && total <= MaxBodyBytes do
            let! read = ctx.Request.Body.ReadAsync(buffer, total, buffer.Length - total)

            if read = 0 then finished <- true else total <- total + read

        if total > MaxBodyBytes then
            return None
        else
            return Some(Encoding.UTF8.GetString(buffer, 0, total))
}

// ─── Field validation ─────────────────────────────────────────────

let private validIdField (s: string) =
    not (String.IsNullOrWhiteSpace s) && s.Length <= 128

let private validPathField (s: string) = not (isNull s) && s.Length <= 2048

let private validImpression (ev: AdImpression) =
    validIdField ev.SlotId
    && validIdField ev.AdClientId
    && validPathField ev.PathAtImpression

let private validClick (ev: AdClick) =
    validIdField ev.SlotId
    && validIdField ev.AdClientId
    && validPathField ev.PathAtClick
    && not (isNull ev.ClickToken)
    && ev.ClickToken.Length <= 512

// ─── Slot sanity check ────────────────────────────────────────────

/// Snapshot of the configured slot-id set, refreshed at most once per
/// minute. Module-level mutable is the documented server-side-cache
/// exception: these are hot anonymous endpoints, and an entity-store
/// list per impression would dominate the handler's cost. Benign
/// race — concurrent refreshes do duplicate work, last writer wins;
/// a slot configured mid-window is honoured within ~60s.
let mutable private slotSnapshot: (DateTimeOffset * Set<string> option) option =
    None

/// Test-only: clear the slot snapshot so tests against different
/// entity-store contents don't see a stale window. Registered via
/// `ToolUp.Platform.Tests.Support.CacheReset.invalidateAll`.
let internal __internal_resetForTests () = slotSnapshot <- None

let private configuredSlotIds (ctx: HttpContext) : System.Threading.Tasks.Task<Set<string> option> = task {
    let now = DateTimeOffset.UtcNow

    match slotSnapshot with
    | Some(fetchedAt, slots) when now - fetchedAt < TimeSpan.FromSeconds 60.0 -> return slots
    | _ ->
        let! slots = AdUnitConfigApi.tryListConfiguredSlotIds ctx
        slotSnapshot <- Some(now, slots)
        return slots
}

/// `true` when the slot id is acceptable. The check only bites when
/// the deployment manages slot configs server-side (entity store
/// present AND at least one `AdSlotConfig` saved via the Phase 61
/// admin CRUD). Deployments whose slot configs live in static client
/// config have nothing server-side to validate against — for them
/// field validation is the gate.
let private slotIsKnown (ctx: HttpContext) (slotId: string) : System.Threading.Tasks.Task<bool> = task {
    let! known = configuredSlotIds ctx

    match known with
    | Some slots when not slots.IsEmpty -> return slots.Contains slotId
    | _ -> return true
}

// ─── Handlers ─────────────────────────────────────────────────────

let private impressionHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let! denied = rateGate ctx "impression" ImpressionsPerMinutePerIp

        match denied with
        | Some rle -> return! writeRateLimited ctx rle
        | None ->
            let! body = readBodyCapped ctx

            match body with
            | None ->
                ctx.Response.StatusCode <- 413
                return! ctx.WriteTextAsync "Payload too large"
            | Some body ->
                let event =
                    try
                        Some(JsonSerializer.Deserialize<AdImpression>(body, jsonOptions))
                    with _ ->
                        None

                match event with
                | None ->
                    ctx.Response.StatusCode <- 400
                    return! ctx.WriteTextAsync "Malformed AdImpression payload"
                | Some ev when not (validImpression ev) ->
                    ctx.Response.StatusCode <- 400
                    return! ctx.WriteTextAsync "Invalid AdImpression field(s)"
                | Some ev ->
                    let! slotOk = slotIsKnown ctx ev.SlotId

                    if not slotOk then
                        // Slot ids are public (they render into the
                        // page's ad markup), so rejecting explicitly
                        // gives integrators a debuggable signal
                        // without handing probers anything new.
                        ctx.Response.StatusCode <- 400
                        return! ctx.WriteTextAsync "Unknown SlotId"
                    else
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
        let! denied = rateGate ctx "click" ClicksPerMinutePerIp

        match denied with
        | Some rle -> return! writeRateLimited ctx rle
        | None ->
            let! body = readBodyCapped ctx

            match body with
            | None ->
                ctx.Response.StatusCode <- 413
                return! ctx.WriteTextAsync "Payload too large"
            | Some body ->
                let event =
                    try
                        Some(JsonSerializer.Deserialize<AdClick>(body, jsonOptions))
                    with _ ->
                        None

                match event with
                | None ->
                    ctx.Response.StatusCode <- 400
                    return! ctx.WriteTextAsync "Malformed AdClick payload"
                | Some ev when not (validClick ev) ->
                    ctx.Response.StatusCode <- 400
                    return! ctx.WriteTextAsync "Invalid AdClick field(s)"
                | Some ev ->
                    let! slotOk = slotIsKnown ctx ev.SlotId

                    if not slotOk then
                        ctx.Response.StatusCode <- 400
                        return! ctx.WriteTextAsync "Unknown SlotId"
                    else
                        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
                        | :? IAuditLog as auditLog ->
                            auditLog.Record(PlatformScope, AuditEvent.AdClickRecorded ev) |> Async.Start
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