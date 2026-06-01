module ToolUp.Platform.NotificationHandler

open System
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open Newtonsoft.Json
open ToolUp.Remoting.Json
open ToolUp.Platform

// ─── JSON serialization ──────────────────────────────────────────
// `FableJsonConverter` so `Notification` DU cases round-trip through
// `Fable.SimpleJson` on the client as `{"CaseName": [fields]}`. Using
// Newtonsoft's `DiscriminatedUnionConverter` would emit
// `{"Case":"X","Fields":[...]}` which the client cannot parse. Same
// rule as `ToolUp.AI.SSEHandler` — any server-authored JSON that the
// Fable client will read manually (SSE, persisted DU data) must go
// through this converter.

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private serializeEnvelope (env: NotificationEnvelope) =
    JsonConvert.SerializeObject(env, jsonSettings)

// ─── Giraffe handler ─────────────────────────────────────────────

/// Giraffe `HttpHandler` for the generic notification SSE endpoint at
/// `GET /api/notifications`. Resolves the subscriber's scope from the
/// authenticated request, subscribes to `INotificationChannel` for
/// that scope, and streams each envelope as a named SSE event. The
/// event name is the `NotificationKind` (e.g. `SystemMessage`) so the
/// client router can `addEventListener` per kind and dispatch without
/// parsing the payload first.
///
/// **Scope resolution** matches `ToolUp.AI.SSEHandler.sseHandler`:
/// prefers `ctx.Items["ToolUp.UserId"]` populated by
/// `ScopeResolutionMiddleware`; falls back to `userId` query param
/// (EventSource cannot set custom headers), then `X-User-Id`, then
/// `"anonymous"`. The query-param fallback is a legacy allowance for
/// Anonymous-mode dev — in authenticated modes the middleware-resolved
/// value always wins.
///
/// **Subscription lifecycle.** One subscription per connection. The
/// callback writes directly to this request's `Response.Body`; no
/// `SSEConnectionManager.Broadcast` fan-out is needed because each
/// connection has its own subscription. On disconnect the handler
/// unsubscribes and removes the connection from the manager. This
/// deliberately trades N in-memory callbacks per publish (for N open
/// tabs under the same scope) for simpler lifecycle management —
/// acceptable for the in-memory default; distributed implementations
/// are free to multiplex differently behind the same interface.
// Phase 6l.D — pre-handshake scope-at-capacity refusal lives below
// inside the handler body so the response stays a plain 429 with
// Retry-After when the scope is at cap. The legacy unconditional
// writeReadyResponse moved underneath the Result.Ok branch.

let notificationHandler (channel: INotificationChannel) (manager: SSEConnectionManager) : HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) -> task {
        let scopeId =
            // Guard excludes "anonymous" so EventSource connections (which
            // cannot set `X-User-Id`) fall through to the `userId` query
            // param. `ScopeResolutionMiddleware` writes "anonymous" when no
            // header is present, which would otherwise register the
            // connection under a shared scope and drop notifications
            // destined for the real user.
            match ctx.Items.TryGetValue "ToolUp.UserId" with
            | true, (:? string as id) when not (String.IsNullOrEmpty id) && id <> "anonymous" -> id
            | _ ->
                match ctx.Request.Query.TryGetValue "userId" with
                | true, values when values.Count > 0 -> string values[0]
                | _ ->
                    match ctx.Request.Headers.TryGetValue "X-User-Id" with
                    | true, values when values.Count > 0 -> string values[0]
                    | _ -> "anonymous"

        let sink = SseConnectionSink.fromHttpResponse ctx.Response ctx.RequestAborted

        match manager.Add(scopeId, sink) with
        | Result.Error refusal ->
            // Phase 6l.D — scope at capacity. Refuse with 429 +
            // Retry-After before any text/event-stream headers
            // commit so the client sees a clean HTTP error.
            ctx.Response.StatusCode <- 429
            ctx.Response.Headers["Retry-After"] <- "30"
            ctx.Response.ContentType <- "text/plain; charset=utf-8"

            do!
                ctx.Response.WriteAsync(
                    sprintf
                        "Too many concurrent SSE connections for scope %s (cap=%d, current=%d). Retry after 30s."
                        refusal.ScopeId
                        refusal.Cap
                        refusal.CurrentCount
                )

            return Some ctx
        | Result.Ok() ->
            do! SSE.writeReadyResponse ctx.Response

            // Write directly to this connection on each envelope. Exceptions
            // are swallowed — a dead response cannot be recovered here, and
            // the next keepalive iteration will fail and exit the loop.
            //
            // Phase 6f filter: transactional kinds (`TransactionalEmail`,
            // `TransactionalSms`, `MobilePush`) ride the same channel for
            // free portability, but they are out-of-band deliveries — only
            // an `INotificationSink` should consume them. Skipping them
            // here keeps the SSE wire UI-only and makes the channel itself
            // a leak-tolerant fan-out (a sink running in another process
            // will still see the publish via Redis; an in-process tab will
            // not).
            let writeEnvelope (env: NotificationEnvelope) =
                if NotificationKind.isTransactional env.Notification then
                    ()
                elif not ctx.RequestAborted.IsCancellationRequested then
                    try
                        let json = serializeEnvelope env
                        let eventName = NotificationKind.ofNotification env.Notification
                        let bytes = SSE.namedFrame eventName json
                        ctx.Response.Body.WriteAsync(bytes, 0, bytes.Length) |> ignore
                        ctx.Response.Body.FlushAsync() |> ignore
                    with _ ->
                        ()

            let! subscriptionId = channel.Subscribe(scopeId, writeEnvelope)

            // Bridge: also subscribe to the reserved `_platform` topic for
            // events that affect this user specifically. `MembershipChanged`
            // is published cross-scope (one publication per write, every
            // subscriber sees it) so the wire delivers only those that
            // target `scopeId`. Client filters again by `MembershipChangeKind`.
            //
            // Platform Knowledge Base writes (`DataRefreshed(
            // "PlatformKnowledgeBase", _)`) are intentionally fan-out to
            // every connected client — the Platform Library page is
            // readable by every authenticated user, so a Platform Admin
            // upload / delete / promote must refresh everyone's view.
            // Filtering is by data-type id (`"PlatformKnowledgeBase"`),
            // not by the affected user, so the same envelope reaches every
            // subscriber. Future cross-scope DataRefreshed kinds add
            // additional `DataRefreshed(<kind>, _)` clauses here.
            let writePlatformEnvelope (env: NotificationEnvelope) =
                match env.Notification with
                | MembershipChanged payload when payload.AffectedUserId = scopeId -> writeEnvelope env
                | DataRefreshed("PlatformKnowledgeBase", _) -> writeEnvelope env
                | _ -> ()

            let! platformSubscriptionId =
                channel.Subscribe(NotificationKind.PlatformReservedScope, writePlatformEnvelope)

            try
                while not ctx.RequestAborted.IsCancellationRequested do
                    do! Task.Delay(15000, ctx.RequestAborted)
                    do! ctx.Response.Body.WriteAsync(SSE.keepaliveBytes, 0, SSE.keepaliveBytes.Length)
                    do! ctx.Response.Body.FlushAsync()
            with
            | :? TaskCanceledException -> ()
            | :? OperationCanceledException -> ()

            do! channel.Unsubscribe subscriptionId
            do! channel.Unsubscribe platformSubscriptionId
            manager.Remove(scopeId, sink)
            return Some ctx
    }