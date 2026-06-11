// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Threading.Tasks
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection

// ─── Phase 112 — live-session compose surface ─────────────────────────
//
// `LiveSessionsCompose.withLiveSessions` follows the companion compose
// idiom (`withPublicRendering` / `withRenderCache`): a deployment that
// never calls it mounts no endpoint, registers no service, and
// allocates nothing (GP 11 / GP 13). When composed it:
//
//   - registers the `ILiveSessionHost` (the in-process default or a
//     caller-supplied distributed impl) as a DI singleton, and
//   - mounts the SSE subscribe endpoint at `{Route}/{sessionId}` under
//     scope isolation — the subscriber's frames come from the session
//     resolved INSIDE the caller's own scope partition, so a session id
//     leaked across tenants resolves to 404, never to frames (GP 4).
//
// **Rate limiting.** When an `IRateLimitStore` is registered (the
// Phase 56 substrate) and the options set a budget, each subscribe is
// counted per-scope per-minute; over-budget requests get the standard
// 429 + `Retry-After`. No store registered → no limiting (the same
// posture as the general inbound rate-limit middleware).
//
// **Security headers.** The Phase 9j `SecurityHeadersMiddleware` is
// global (`OnStarting` fires for every response), so the live-session
// endpoint is covered with zero extra wiring — documented here so the
// coverage is explicit rather than incidental.

/// Options for `LiveSessionsCompose.withLiveSessions`.
type LiveSessionOptions = {
    /// Path prefix the SSE subscribe endpoint mounts under; a client
    /// subscribes to `GET {Route}/{sessionId}`.
    Route: string
    /// Per-scope concurrent-session cap enforced by the in-process
    /// default host's `OpenSession`. `None` = uncapped.
    MaxSessionsPerScope: int option
    /// Per-scope subscribe budget per minute, enforced via the
    /// registered `IRateLimitStore`. `None` = no budget.
    SubscribesPerMinutePerScope: int option
    /// Override the host implementation (a distributed companion).
    /// `None` → `InMemoryLiveSessionHost` (single-instance default).
    Host: ILiveSessionHost option
}

module LiveSessionOptions =
    let defaults: LiveSessionOptions = {
        Route = "/api/live-sessions"
        MaxSessionsPerScope = None
        SubscribesPerMinutePerScope = None
        Host = None
    }

module LiveSessionHandler =

    /// SSE event name live-session frames ride under — the client shim
    /// `addEventListener`s this one name.
    [<Literal>]
    let FrameEventName = "live-frame"

    /// The subscriber's scope id: the per-request `AccessContext`'s
    /// active team when present, else its stable subject id — matching
    /// the partition `ILiveSessionHost` sessions are opened under (the
    /// team resolver's `StorageScope.ScopeId` is the team id; the user
    /// resolver's is the user id). Falls back to the SSE-conventional
    /// `X-User-Id` / `userId`-query resolution when no `AccessContext`
    /// is registered (matching `NotificationHandler`).
    let private scopeIdOf (ctx: HttpContext) : string =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac.TeamId |> Option.defaultValue ac.UserId
        | _ ->
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) when not (String.IsNullOrEmpty id) && id <> "anonymous" -> id
            | _ ->
                match ctx.Request.Query.TryGetValue "userId" with
                | true, values when values.Count > 0 -> string values[0]
                | _ ->
                    match ctx.Request.Headers.TryGetValue "X-User-Id" with
                    | true, values when values.Count > 0 -> string values[0]
                    | _ -> "anonymous"

    let private write429 (ctx: HttpContext) (retryAfterSeconds: int) (message: string) = task {
        ctx.Response.StatusCode <- 429
        ctx.Response.Headers["Retry-After"] <- string retryAfterSeconds
        ctx.Response.ContentType <- "text/plain; charset=utf-8"
        do! ctx.Response.WriteAsync message
        return Some ctx
    }

    /// The SSE subscribe endpoint: `GET {Route}/{sessionId}`. Non-match
    /// paths skip to the next handler. Unknown session (or a session id
    /// from another scope — structurally indistinguishable) → 404.
    let handler (options: LiveSessionOptions) (host: ILiveSessionHost) : HttpHandler =
        let prefix = options.Route.TrimEnd('/') + "/"

        fun (_next: HttpFunc) (ctx: HttpContext) ->
            let path = ctx.Request.Path.Value

            if
                not (HttpMethods.IsGet ctx.Request.Method)
                || not (path.StartsWith(prefix, StringComparison.Ordinal))
            then
                skipPipeline
            else
                task {
                    let sessionId = path.Substring(prefix.Length).Trim('/')

                    if String.IsNullOrEmpty sessionId || sessionId.Contains '/' then
                        return! skipPipeline
                    else
                        let scopeId = scopeIdOf ctx

                        // Per-scope subscribe budget via the Phase 56
                        // substrate, when both the store and a budget are
                        // present.
                        let! rateDecision = task {
                            match options.SubscribesPerMinutePerScope with
                            | None -> return Ok()
                            | Some budget ->
                                match ctx.RequestServices.GetService(typeof<IRateLimitStore>) with
                                | :? IRateLimitStore as store ->
                                    let key = InboundComposite $"live-sessions:%s{scopeId}"
                                    let! decision = store.IncrementAndCheck(key, PerMinute, budget)

                                    match decision with
                                    | Ok(AllowWithRemaining _) -> return Ok()
                                    | Ok(DenyWithError err) -> return Error err.RetryAfterSeconds
                                    | Error _ ->
                                        // Store blip: fail open for the
                                        // subscribe path (matching the
                                        // inbound middleware posture) —
                                        // scope isolation is unaffected.
                                        return Ok()
                                | _ -> return Ok()
                        }

                        match rateDecision with
                        | Error retryAfter ->
                            return!
                                write429 ctx retryAfter $"Live-session subscribe budget exceeded for scope %s{scopeId}."
                        | Ok() ->
                            // Subscribe inside the caller's own scope
                            // partition — a cross-scope session id resolves
                            // to None here, structurally (GP 4).
                            let writeFrame (payload: string) =
                                if not ctx.RequestAborted.IsCancellationRequested then
                                    try
                                        let bytes = SSE.namedFrame FrameEventName payload
                                        ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length) |> ignore
                                        ctx.Response.Body.FlushAsync() |> ignore
                                    with _ ->
                                        ()

                            let! subscription = host.Subscribe(scopeId, sessionId, writeFrame)

                            match subscription with
                            | None ->
                                ctx.Response.StatusCode <- 404
                                ctx.Response.ContentType <- "text/plain; charset=utf-8"
                                do! ctx.Response.WriteAsync "Unknown live session."
                                return Some ctx
                            | Some subscriptionId ->
                                do! SSE.writeReadyResponse ctx.Response

                                try
                                    while not ctx.RequestAborted.IsCancellationRequested do
                                        do! Task.Delay(15000, ctx.RequestAborted)

                                        do!
                                            ctx.Response.Body.WriteAsync(
                                                SSE.keepaliveBytes,
                                                0,
                                                SSE.keepaliveBytes.Length
                                            )

                                        do! ctx.Response.Body.FlushAsync()
                                with
                                | :? TaskCanceledException -> ()
                                | :? OperationCanceledException -> ()

                                do! host.Unsubscribe(scopeId, sessionId, subscriptionId)
                                return Some ctx
                }

module LiveSessionsCompose =

    /// Compose scope-isolated live sessions onto a `ServerApp`:
    ///
    /// ```
    /// ServerApp.create config modules
    /// |> LiveSessionsCompose.withLiveSessions (fun o ->
    ///     { o with SubscribesPerMinutePerScope = Some 30 })
    /// |> ServerApp.run
    /// ```
    ///
    /// Registers the `ILiveSessionHost` singleton (server-side code
    /// resolves it from DI to open sessions and push frames) and mounts
    /// the SSE subscribe endpoint. Not calling this composes nothing —
    /// no endpoint, no service, no allocation (GP 11 / GP 13).
    let withLiveSessions (configure: LiveSessionOptions -> LiveSessionOptions) (app: ServerApp) : ServerApp =
        let options = configure LiveSessionOptions.defaults

        let host =
            options.Host
            |> Option.defaultWith (fun () -> LiveSessionHost.createInMemory options.MaxSessionsPerScope)

        let registerHost (services: IServiceCollection) =
            services.AddSingleton<ILiveSessionHost>(host)

        {
            app with
                Extensions.Handlers = app.Extensions.Handlers @ [ LiveSessionHandler.handler options host ]
                Extensions.ServiceConfig =
                    match app.Extensions.ServiceConfig with
                    | Some existing -> Some(existing >> registerHost)
                    | None -> Some registerHost
        }