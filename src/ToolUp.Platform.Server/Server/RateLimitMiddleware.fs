// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives

// ─── RateLimitMiddleware — Phase 56 inbound HTTP rate limiting ───────
//
// Composes against `ServerConfig.RateLimits : RouteLimit list`, the
// `IRateLimitStore` substrate, and the existing scope-resolution
// middleware. Mounts in `configurePipeline` AFTER scope resolution (so
// `ByUserId` policies see a populated `AccessContext.UserId`) and
// BEFORE the Giraffe router (so 429 short-circuits hit before any
// handler work).
//
// **Phase 16 compatibility.** The middleware is registered via
// standard `app.UseMiddleware<RateLimitMiddleware>()`. Both Kestrel
// (`IServerHost.RunBlocking`) and serverless host adapters
// (`IServerHost.Invoke`) drive the same configured pipeline, so the
// middleware applies identically on either host without further
// wiring.
//
// **Fail-open semantics.** When `IRateLimitStore.IncrementAndCheck`
// returns `Error`, the middleware logs at `Warn` and admits the
// request rather than blanket-denying every caller during a store
// outage. Operators get a metric on the failure; the deployment stays
// up.

module RateLimitMiddleware =

    /// Locate the first `RouteLimit` whose `Route` is a case-
    /// insensitive `StartsWith` match against `path`. `None` when
    /// the request doesn't match any policy.
    let internal matchPolicy (limits: RouteLimit list) (path: string) : RouteLimit option =
        limits
        |> List.tryFind (fun limit -> path.StartsWith(limit.Route, StringComparison.OrdinalIgnoreCase))

    /// Extract the `InboundRateLimitKey` for the given policy from
    /// the `HttpContext`. `ByIp` reads `Connection.RemoteIpAddress`
    /// (falling back to `"unknown"` when the connection has no
    /// remote address — local dev edge case). `ByUserId` reads
    /// `HttpContext.Items["ToolUp.UserId"]` populated by
    /// `ScopeResolutionMiddleware`; `"anonymous"` if absent.
    /// `ByComposite` returns the operator-supplied custom key
    /// verbatim — operators wanting per-(ip, route) or per-team
    /// dimensions build the string themselves before calling
    /// `RouteLimit.Key = ByComposite "..."`.
    let internal extractKey (ctx: HttpContext) (policy: RouteLimit) : InboundRateLimitKey =
        match policy.Key with
        | ByIp ->
            let ip =
                match ctx.Connection.RemoteIpAddress with
                | null -> "unknown"
                | addr -> addr.ToString()

            IpAddressKey ip
        | ByUserId ->
            let userId =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            UserIdKey userId
        | ByComposite key -> InboundComposite key

    /// Stamp `RateLimit-Limit` / `RateLimit-Remaining` /
    /// `RateLimit-Reset` standard response headers per RFC 9239
    /// (IETF rate-limit headers — draft, widely adopted).
    /// `Retry-After` is added on the 429 path only.
    let internal stampAllowHeaders (ctx: HttpContext) (policy: RouteLimit) (remaining: int) (resetSeconds: int) : unit =
        ctx.Response.Headers["RateLimit-Limit"] <- StringValues(string policy.Threshold)
        ctx.Response.Headers["RateLimit-Remaining"] <- StringValues(string (max 0 remaining))
        ctx.Response.Headers["RateLimit-Reset"] <- StringValues(string resetSeconds)

    let internal stampDenyHeaders (ctx: HttpContext) (rle: RateLimitedError) : unit =
        ctx.Response.Headers["RateLimit-Limit"] <- StringValues(string rle.Limit)
        ctx.Response.Headers["RateLimit-Remaining"] <- StringValues "0"
        ctx.Response.Headers["RateLimit-Reset"] <- StringValues(string rle.RetryAfterSeconds)
        ctx.Response.Headers["Retry-After"] <- StringValues(string rle.RetryAfterSeconds)

    /// Approximate seconds until the next `RateLimitWindow` boundary.
    /// `PerSecond` / `PerMinute` / `PerHour` / `PerDay` round down to
    /// the next calendar boundary; `SlidingWindow` uses `duration`
    /// directly (the in-memory store resets at the full duration; a
    /// Redis-Lua store may report a tighter value via the typed
    /// `RateLimitedError.RetryAfterSeconds`).
    let internal secondsUntilWindowEnd (window: RateLimitWindow) : int =
        let now = DateTimeOffset.UtcNow

        match window with
        | PerSecond -> 1
        | PerMinute -> max 1 (60 - now.Second)
        | PerHour -> max 1 ((60 - now.Minute) * 60 - now.Second)
        | PerDay -> max 1 (((23 - now.Hour) * 3600) + ((60 - now.Minute) * 60) + (60 - now.Second))
        | SlidingWindow(duration, _) -> max 1 (int (Math.Ceiling duration.TotalSeconds))

    /// Render the typed `RateLimitedError` as the JSON body of the
    /// 429 response. The wire shape mirrors the Fable.Remoting
    /// envelope so module client code can `JSON.deserialize` it into
    /// the same record. Hand-rolled rather than via Fable.Remoting
    /// because the middleware sits OUTSIDE the Remoting pipeline
    /// (short-circuits before any handler runs).
    let internal renderDenyBody (rle: RateLimitedError) : byte[] =
        let windowLabel =
            match rle.Window with
            | PerSecond -> "PerSecond"
            | PerMinute -> "PerMinute"
            | PerHour -> "PerHour"
            | PerDay -> "PerDay"
            | SlidingWindow(d, b) -> sprintf "SlidingWindow(%fs, %d buckets)" d.TotalSeconds b

        let json =
            sprintf
                """{"error":"rate_limited","retryAfterSeconds":%d,"limit":%d,"window":"%s"}"""
                rle.RetryAfterSeconds
                rle.Limit
                windowLabel

        Encoding.UTF8.GetBytes json

    /// Inbound rate-limit middleware. Constructed via
    /// `app.UseMiddleware<InboundRateLimitMiddleware>()` so ASP.NET
    /// Core's middleware factory wires `RequestDelegate` +
    /// `IRateLimitStore` (resolved from DI) automatically.
    ///
    /// When `ServerConfig.RateLimits` is empty OR `IRateLimitStore`
    /// is not registered (`RateLimitStoreMode = NoRateLimitStore`),
    /// the middleware should NOT be registered at all —
    /// `compose` gates the call. The class is unconditional in DI
    /// only so `app.UseMiddleware<...>()` resolves cleanly.
    type InboundRateLimitMiddleware(next: RequestDelegate, store: IRateLimitStore, logger: ILogger) =
        member _.InvokeAsync(ctx: HttpContext, config: ServerConfig) : Task =
            task {
                let path =
                    if isNull ctx.Request.Path.Value then
                        "/"
                    else
                        ctx.Request.Path.Value

                match matchPolicy config.RateLimits path with
                | None -> do! next.Invoke ctx
                | Some policy ->
                    let key = extractKey ctx policy

                    let! result = store.IncrementAndCheck(key, policy.Window, policy.Threshold)

                    match result with
                    | Error err ->
                        // Fail-open — log + admit. The store outage
                        // is the operator's problem, not the
                        // caller's; refusing every request during a
                        // Redis flap is worse than over-admitting
                        // briefly.
                        let reason =
                            match err with
                            | StoreUnavailable r -> r
                            | StoreContractViolation r -> r

                        logger.Warn(
                            sprintf "[RateLimitMiddleware] IRateLimitStore failed; admitting request (%s)" reason
                        )

                        do! next.Invoke ctx

                    | Ok(AllowWithRemaining remaining) ->
                        stampAllowHeaders ctx policy remaining (secondsUntilWindowEnd policy.Window)
                        do! next.Invoke ctx

                    | Ok(DenyWithError rle) ->
                        match policy.OnExceeded with
                        | Return429 ->
                            stampDenyHeaders ctx rle
                            ctx.Response.StatusCode <- 429
                            ctx.Response.ContentType <- "application/json"
                            let body = renderDenyBody rle
                            do! ctx.Response.Body.WriteAsync(body, 0, body.Length)

                        | DelayAndAllow maxDelay ->
                            let delayMs = min (int maxDelay.TotalMilliseconds) (rle.RetryAfterSeconds * 1000)

                            do! Task.Delay(delayMs)
                            stampAllowHeaders ctx policy 0 (secondsUntilWindowEnd policy.Window)
                            do! next.Invoke ctx

                        | DenySilently ->
                            stampDenyHeaders ctx rle
                            ctx.Response.StatusCode <- 204
            }
            :> Task