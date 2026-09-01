// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.TelemetryApiHandler

open System
open System.Text
open Giraffe
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── Phase 163 — server-side product-telemetry fan-out endpoint ─────────
//
// Receives `TelemetryEvent` posts from the client-tier `Telemetry.track`
// helper and fans them out to the composed `ITelemetrySink` (GA4, Mixpanel,
// Segment — whatever the deployment registered), tagged with the caller's
// resolved scope.
//
// **Mounted only under `ServerConfig.TelemetrySink = CustomTelemetrySink`**
// — `routesFor` returns the empty list on the `NoTelemetrySink` default, so
// a deployment that composes no analytics has no route, no handler and no
// allocation (GP 13). The client helper swallows the resulting 404, which is
// a legitimate steady state rather than an error.
//
// **Authorisation is the fail-closed default, deliberately.** This route
// declares no `SurfaceRequirement`, so `SurfaceEnforcementMiddleware`'s
// strict global default (`userOrTeam`) applies on an authenticating
// deployment, and the `/api/` prefix's `public_` default applies in
// Anonymous mode. That is the right posture for both: product analytics is
// emitted by the app surface the deployment already authenticates, and an
// anonymous-mode deployment has no authenticated surface to emit from. It is
// deliberately NOT registered as `public_` the way the ad sinks are — an
// unauthenticated write into a third-party analytics product is an abuse
// vector, and the ad sinks needed a rate limiter precisely because they are.
//
// **The consent gate is client-side** (`Telemetry.track` against the
// client-tier `IConsentProvider`) — an event that never leaves the browser
// cannot breach consent. This handler ships whatever reaches it.
//
// **PII by construction = none.** `TelemetryEvent.Properties` are
// operator-declared keys and nothing here adds to them. The only field this
// handler contributes is the scope, which the request already carries.

let private jsonOptions = FableConverters.create ()

/// Fallback scope when the caller resolves to none (anonymous mode, or a
/// subject with no config scope). Matches the ad / consent sinks'
/// deployment-wide bucket.
[<Literal>]
let private PlatformScope = "_platform"

/// Body cap. A `TelemetryEvent` is a name plus a small property bag — a
/// generous 8 KB admits any legitimate payload while refusing bulk junk
/// before it is deserialised. Same shape and same reasoning as the ad
/// analytics sink's cap.
[<Literal>]
let private MaxBodyBytes = 8192

/// Property-bag ceilings. Analytics events are a handful of short
/// operator-declared keys; anything past these is malformed or hostile.
[<Literal>]
let private MaxProperties = 64

[<Literal>]
let private MaxFieldLength = 512

/// Read the request body up to `MaxBodyBytes`. `None` when the declared or
/// actual length exceeds the cap.
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

/// Absent-field coercion: `FableConverters` initialises a missing
/// reference-typed field to `null`, and a null F# `Map` NREs on every read.
/// A post carrying only `{"Event":"page_view"}` is legitimate (a bare
/// event), so normalise rather than reject.
let internal normalise (event: TelemetryEvent) : TelemetryEvent =
    if isNull (box event.Properties) then
        { event with Properties = Map.empty }
    else
        event

/// Field validation. Runs after `normalise`, so `Properties` is never null
/// here.
let internal isValid (event: TelemetryEvent) : bool =
    not (String.IsNullOrWhiteSpace event.Event)
    && event.Event.Length <= MaxFieldLength
    && event.Properties.Count <= MaxProperties
    && event.Properties
       |> Seq.forall (fun kv ->
           not (String.IsNullOrWhiteSpace kv.Key)
           && kv.Key.Length <= MaxFieldLength
           && not (isNull kv.Value)
           && kv.Value.Length <= MaxFieldLength)

/// The scope the event is tagged with — the caller's already-resolved
/// config scope (team id / user id), falling back to the deployment-wide
/// `_platform` bucket. Nothing subject-identifying is derived here beyond
/// the scope the request already carries.
let private scopeFor (ctx: HttpContext) : string =
    match ctx.RequestServices.GetService(typeof<AccessContext>) with
    | :? AccessContext as accessContext ->
        accessContext
        |> AccessContext.configScope
        |> Option.map _.ScopeId
        |> Option.defaultValue PlatformScope
    | _ -> PlatformScope

let private trackHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let! body = readBodyCapped ctx

        match body with
        | None ->
            ctx.Response.StatusCode <- 413
            return! ctx.WriteTextAsync "Payload too large"
        | Some body ->
            let event =
                try
                    Some(normalise (JsonSerializer.Deserialize<TelemetryEvent>(body, jsonOptions)))
                with _ ->
                    None

            match event with
            | None ->
                ctx.Response.StatusCode <- 400
                return! ctx.WriteTextAsync "Malformed TelemetryEvent payload"
            | Some ev when not (isValid ev) ->
                ctx.Response.StatusCode <- 400
                return! ctx.WriteTextAsync "Invalid TelemetryEvent field(s)"
            | Some ev ->
                match ctx.RequestServices.GetService(typeof<ITelemetrySink>) with
                | :? ITelemetrySink as sink ->
                    // Awaited rather than `Async.Start`-ed, unlike the audit
                    // sinks: this request IS the telemetry emission, so there
                    // is no other request a slow sink could delay, and the
                    // 204 then means the sink was actually reached. `Track`
                    // never throws across the boundary by contract; the guard
                    // holds a contract-violating sink to the same best-effort
                    // posture rather than turning it into a 500.
                    try
                        do! sink.Track(scopeFor ctx, ev) |> Async.StartAsTask
                    with _ ->
                        ()
                | _ -> ()

                ctx.Response.StatusCode <- 204
                return! next ctx
    }

/// Routes table. Reached through `routesFor`, never mounted directly.
let routes: HttpHandler list = [ POST >=> route "/api/_platform/telemetry" >=> trackHandler ]

/// The routes for a given sink mode. `NoTelemetrySink` (the default) yields
/// the empty list — no route on the routing table, no handler allocated, a
/// clean 404 from the Giraffe terminal middleware, and a deployment that
/// composes no analytics unchanged byte for byte (GP 13).
let routesFor (mode: TelemetrySinkMode) : HttpHandler list =
    match mode with
    | NoTelemetrySink -> []
    | CustomTelemetrySink -> routes