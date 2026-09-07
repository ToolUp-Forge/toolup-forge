// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AdAnalyticsApiHandler

open System
open System.Collections.Concurrent
open System.Text
open Giraffe
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.Metrics

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

// ─── Observability (Phase 466) ────────────────────────────────────
//
// Two of the three gates above degrade SILENTLY, and both silences
// look identical to health from outside: a rate-limit-store outage
// fail-opens (the correct availability default — see `rateGate`) and
// so removes the per-IP budget with nothing written anywhere, and a
// malformed payload is dropped to a bare 400 with no record that ad
// events are being lost. An operator asking "why did ad events stop"
// or "why is one address flooding us" had no signal for either.
//
// Both now emit a COUNTER unconditionally and a `Warn` under a
// throttle. The split is the point: the counter is what a dashboard
// alerts on and must count every occurrence, while the log line is
// what a human reads and must not be turnable into the denial of
// service by whatever is causing the failure. Same posture — and the
// same in-process mechanism — as `ExternalComputeCallback`'s
// rate-limited refusal warning.
//
// The behaviour of the endpoints is deliberately unchanged: fail-open
// stays fail-open, a malformed payload still answers 400.

/// Metric names emitted by the ad-analytics endpoints.
///
/// Registered in `MetricsMiddleware`'s `StandardMetrics.coreRegistrations`,
/// which compiles after this file — an unregistered series is silently
/// dropped by the sink, which would reinstate exactly the silence this
/// phase closes. The definitions live beside the emission (the Phase
/// 740 convention) so each metric's tag allowlist sits next to the code
/// that emits those tags.
module AdAnalyticsMetrics =
    /// Incremented whenever the Phase 56 `IRateLimitStore` returns an
    /// error and the handler fail-opens, so a store outage that has
    /// disabled the per-IP ad budget is dashboard-visible rather than
    /// inferable only from traffic. Tagged by `endpoint`
    /// (`impression` / `click`).
    [<Literal>]
    let RateLimitStoreFailuresTotal = "toolup.ads.rate_limit_store_failures_total"

    /// Incremented whenever a posted body fails to deserialise into
    /// `AdImpression` / `AdClick` and the event is dropped. Tagged by
    /// `endpoint`. NOT tagged by client address — that is unbounded
    /// caller-controlled cardinality on an anonymous endpoint, which is
    /// what the sink's series cap exists to refuse; the address is on
    /// the log line instead, where the throttle bounds it.
    [<Literal>]
    let MalformedPayloadsTotal = "toolup.ads.malformed_payloads_total"

/// At most one warning per throttle key per this many minutes, however
/// many occurrences arrive. Bounds log volume only — the counters
/// above are unconditional, so the trail stays complete precisely when
/// the log has gone quiet.
[<Literal>]
let private warningWindowMinutes = 5.0

/// Documented mutable-state exception (GP 5), and the same one
/// `ExternalComputeCallback` takes: a "when did I last warn about this"
/// suppressor IS state, and this dictionary is the whole of it.
/// Concurrent by construction; benign race on a simultaneous first
/// occurrence emits at most one extra line.
let private lastWarned = ConcurrentDictionary<string, DateTime>()

/// Should the warning for `key` be logged right now? First occurrence
/// in a window: yes. Subsequent ones: suppressed.
let private shouldWarn (key: string) : bool =
    let now = DateTime.UtcNow

    match lastWarned.TryGetValue key with
    | true, at when (now - at).TotalMinutes < warningWindowMinutes -> false
    | _ ->
        lastWarned[key] <- now
        true

/// Test seam: clear the in-process warning-throttle state so packs do
/// not inherit each other's suppression windows. Never called from the
/// request path. Mirrors `ExternalComputeCallback.resetThrottleState`;
/// deliberately not in the `CacheReset` registry, which covers caches
/// whose staleness changes another pack's OUTCOME — this one only
/// suppresses a log line.
let resetObservabilityState () = lastWarned.Clear()

let private resolveLogger (ctx: HttpContext) : ILogger option =
    match ctx.RequestServices.GetService(typeof<ILogger>) with
    | :? ILogger as l -> Some l
    | _ -> None

let private resolveMetrics (ctx: HttpContext) : IMetricsSink option =
    match ctx.RequestServices.GetService(typeof<IMetricsSink>) with
    | :? IMetricsSink as m -> Some m
    | _ -> None

// ─── Gates ────────────────────────────────────────────────────────

let private clientIp (ctx: HttpContext) : string =
    match ctx.Connection.RemoteIpAddress with
    | null -> "unknown"
    | addr -> addr.ToString()

/// Per-IP fixed-window budget via the Phase 56 store when one is
/// composed. Returns `Some error` on deny. Store absent → no gate
/// (validator warns at startup); store failure → fail-open, matching
/// `RateLimitMiddleware`'s contract (a store outage is the operator's
/// problem, not the caller's) — but counted and logged since Phase
/// 466, because a throttle that has silently switched itself off is
/// indistinguishable from one that is working.
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
            | Error err ->
                let reason =
                    match err with
                    | StoreUnavailable r -> r
                    | StoreContractViolation r -> r

                resolveMetrics ctx
                |> Option.iter (fun m ->
                    m.Increment(AdAnalyticsMetrics.RateLimitStoreFailuresTotal, Map [ "endpoint", endpoint ]))

                // Throttled per ENDPOINT and not per client address: a
                // store outage fails every caller at once, so an
                // address-keyed suppressor would emit one line per
                // distinct client — the flood it exists to prevent.
                if shouldWarn (sprintf "store-failure|%s" endpoint) then
                    resolveLogger ctx
                    |> Option.iter (fun l ->
                        l.Warn(
                            sprintf
                                "[ad-analytics] event=rate_limit_store_failed endpoint=%s reason=%s — the per-IP ad-analytics budget is NOT being enforced while the store is failing; requests are admitted (fail-open, by design). Further warnings for this endpoint are suppressed for %g minutes; %s counts every one."
                                endpoint
                                reason
                                warningWindowMinutes
                                AdAnalyticsMetrics.RateLimitStoreFailuresTotal
                        ))

                // Fail-open — unchanged, and the correct availability
                // default. Phase 466 makes it observable, not different.
                return None
        | _ -> return None
    }

/// Emit the parse-drop signal: the counter unconditionally, the `Warn`
/// under the throttle.
///
/// Keyed per `(endpoint, client address)` rather than per endpoint: a
/// malformed payload is usually ONE broken integration or one hostile
/// caller, so collapsing every address into a single suppressor would
/// hide a second, genuinely-different breakage behind the first one's
/// window — while a per-address key still bounds any single caller to
/// one line per window. The counter is what stays complete.
///
/// Phase 763 takes the bind ERROR rather than nothing, so a body of the
/// JSON literal `null` is counted and warned on exactly the path a
/// truncated one is. The counter is deliberately NOT split by cause:
/// the series is what a dashboard alerts on, "ad events are being
/// dropped" is the alert, and a second series would halve every
/// existing threshold for no operational gain. The cause is on the log
/// line, where a human reads it.
let private recordParseDrop (ctx: HttpContext) (endpoint: string) (error: HttpBodyBinding.BodyBindError) : unit =
    resolveMetrics ctx
    |> Option.iter (fun m -> m.Increment(AdAnalyticsMetrics.MalformedPayloadsTotal, Map [ "endpoint", endpoint ]))

    let ip = clientIp ctx

    if shouldWarn (sprintf "parse-drop|%s|%s" endpoint ip) then
        resolveLogger ctx
        |> Option.iter (fun l ->
            l.Warn(
                sprintf
                    "[ad-analytics] event=malformed_payload endpoint=%s from=%s reason=%s — the posted body did not deserialise (%s) and the ad event was DROPPED (answered 400). A steady rate here usually means a client/server wire-format skew rather than abuse. Further warnings for this endpoint and address are suppressed for %g minutes; %s counts every one."
                    endpoint
                    ip
                    (HttpBodyBinding.BodyBindError.reason error)
                    (HttpBodyBinding.BodyBindError.describe error)
                    warningWindowMinutes
                    AdAnalyticsMetrics.MalformedPayloadsTotal
            ))

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
                // Phase 763 — bound through the shared seam, after the
                // size cap above, which must keep running first. A body
                // of the JSON literal `null` used to bind to `Some null`
                // and reach `validImpression` below, where the first
                // field access threw OUTSIDE the `try`: a 500 for a
                // 400-class input, with the parse-drop counter silent.
                match HttpBodyBinding.tryBindJsonString<AdImpression> jsonOptions body with
                | Error err ->
                    recordParseDrop ctx "impression" err
                    ctx.Response.StatusCode <- HttpBodyBinding.statusCodeFor err
                    return! ctx.WriteTextAsync "Malformed AdImpression payload"
                | Ok ev when not (validImpression ev) ->
                    ctx.Response.StatusCode <- 400
                    return! ctx.WriteTextAsync "Invalid AdImpression field(s)"
                | Ok ev ->
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
                // Phase 763 — the same bind seam as the impression
                // endpoint above, for the same reason: `null` is valid
                // JSON, so the old `try … with _ -> None` shape never
                // saw it and `validClick` took the dereference.
                match HttpBodyBinding.tryBindJsonString<AdClick> jsonOptions body with
                | Error err ->
                    recordParseDrop ctx "click" err
                    ctx.Response.StatusCode <- HttpBodyBinding.statusCodeFor err
                    return! ctx.WriteTextAsync "Malformed AdClick payload"
                | Ok ev when not (validClick ev) ->
                    ctx.Response.StatusCode <- 400
                    return! ctx.WriteTextAsync "Invalid AdClick field(s)"
                | Ok ev ->
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