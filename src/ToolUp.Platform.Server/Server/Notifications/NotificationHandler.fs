module ToolUp.Platform.NotificationHandler

open System
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Giraffe
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform

// ─── JSON serialization ──────────────────────────────────────────
// `FableConverters` so `Notification` DU cases round-trip through
// `Fable.SimpleJson` on the client as `{"CaseName": [fields]}`. The
// bare-STJ default contract would emit
// `{"Case":"X","Fields":[...]}` which the client cannot parse. Same
// rule as `ToolUp.AI.SSEHandler` — any server-authored JSON that the
// Fable client will read manually (SSE, persisted DU data) must go
// through this converter.

let private jsonOptions = FableConverters.create ()

let private serializeEnvelope (env: NotificationEnvelope) =
    JsonSerializer.Serialize(env, jsonOptions)

// ─── Giraffe handler ─────────────────────────────────────────────

/// Giraffe `HttpHandler` for the generic notification SSE endpoint at
/// `GET /api/notifications`. Resolves the subscriber's scope from the
/// authenticated request, subscribes to `INotificationChannel` for
/// that scope, and streams each envelope as a named SSE event. The
/// event name is the `NotificationKind` (e.g. `SystemMessage`) so the
/// client router can `addEventListener` per kind and dispatch without
/// parsing the payload first.
///
/// **Scope resolution** is the shared `SseScopeResolution.resolve` —
/// the same code path `ToolUp.AI.SSEHandler.sseHandler` calls, so the
/// two SSE endpoints cannot drift (Phase 117; they previously did —
/// this handler honoured `?userId=` unconditionally while the AI twin
/// gated it on `SseAuthMode`). Middleware-resolved identity always
/// wins and a mismatching `?userId=` is audited; the query-param /
/// `X-User-Id` fallback is honoured only under `QueryParamFallback`;
/// under `CookieRequired` an unauthenticated connect is refused 401.
///
/// **Subscription lifecycle.** One subscription per connection, and ONE
/// writer per response (Phase 1064). The connection is registered with the
/// manager, which hands back its `ConnectionWriter`; each envelope is
/// framed and enqueued on that writer, which writes and flushes one frame
/// at a time, and the manager's keepalive goes through the same queue. The
/// handler writes nothing to `Response.Body` after the ready comment and
/// runs no keepalive loop of its own. (Until 1064 the channel callback
/// wrote straight to the body, unawaited, beside a 15 s keepalive loop and
/// the manager's 30 s one: three writers on one response — the race Phase
/// 870 closed for live sessions.) A full queue or a failed write ends the
/// connection; on disconnect the handler unsubscribes, removes the
/// connection, and waits for the writer to stop. Each connection keeps its
/// own subscription — N callbacks per publish for N open tabs under one
/// scope — which is acceptable for the in-memory default; distributed
/// implementations are free to multiplex differently behind the same
/// interface.
// Phase 6l.D — pre-handshake scope-at-capacity refusal lives below
// inside the handler body so the response stays a plain 429 with
// Retry-After when the scope is at cap. The legacy unconditional
// writeReadyResponse moved underneath the Result.Ok branch.

let notificationHandler
    (channel: INotificationChannel)
    (manager: SSEConnectionManager)
    (sseAuthMode: SseAuthMode)
    : HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) -> task {
        match SseScopeResolution.resolve sseAuthMode ctx with
        | SseScopeResolution.SseUnauthenticated ->
            // CookieRequired + no resolved identity. Refuse rather than
            // trust a client-supplied userId.
            do! SseScopeResolution.refuseUnauthenticated ctx
            return Some ctx
        | SseScopeResolution.SseScope scopeId ->

            // Opens once the SSE headers and the ready comment are on the
            // wire, so a frame (an envelope, or the manager's keepalive)
            // enqueued before then waits in the writer's queue instead of
            // writing a body ahead of the headers.
            let opened =
                TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)

            let response = SseConnectionSink.fromHttpResponse ctx.Response ctx.RequestAborted

            let sink =
                { new IConnectionSink with
                    member _.DisconnectToken = ctx.RequestAborted

                    member _.WriteFrame(bytes, ct) = async {
                        do! opened.Task.WaitAsync(ct) |> Async.AwaitTask
                        do! response.WriteFrame(bytes, ct)
                    }
                }

            match manager.AddWriter(scopeId, sink) with
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
            | Result.Ok writer ->
                // Frame and enqueue one envelope. A full queue is a
                // connection that has stopped reading: end it, exactly
                // as the manager's own fan-out does.
                //
                // Phase 6f filter: transactional kinds (`TransactionalEmail`,
                // `TransactionalSms`, `MobilePush`) ride the same channel for
                // free portability, but they are out-of-band deliveries — only
                // an `INotificationSink` should consume them. Skipping them
                // here keeps the SSE wire UI-only and makes the channel itself
                // a leak-tolerant fan-out (a sink running in another process
                // will still see the publish via Redis; an in-process tab will
                // not).
                let sendEnvelope (env: NotificationEnvelope) =
                    if not (NotificationKind.isTransactional env.Notification) then
                        let eventName = NotificationKind.ofNotification env.Notification
                        let frame = SSE.namedFrame eventName (serializeEnvelope env)

                        if not (writer.TryEnqueue frame) then
                            manager.Remove(scopeId, sink)

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
                let sendPlatformEnvelope (env: NotificationEnvelope) =
                    match env.Notification with
                    | MembershipChanged payload when payload.AffectedUserId = scopeId -> sendEnvelope env
                    | DataRefreshed("PlatformKnowledgeBase", _) -> sendEnvelope env
                    | _ -> ()

                // Both subscriptions are registered BEFORE the ready comment,
                // so a client that has seen it has missed nothing: an
                // envelope published in between waits in the writer's queue
                // behind the `opened` gate.
                let! subscriptionId = channel.Subscribe(scopeId, sendEnvelope)

                let! platformSubscriptionId =
                    channel.Subscribe(NotificationKind.PlatformReservedScope, sendPlatformEnvelope)

                let failure = ref (null: exn)

                try
                    do! SSE.writeReadyResponse ctx.Response
                    opened.TrySetResult() |> ignore

                    // Park until the client disconnects or the writer ends
                    // the connection (a failed write, a full queue). Neither
                    // task faults: the writer's loop catches its own
                    // shutdown, and `WhenAny` does not observe a cancelled
                    // delay as an exception.
                    let disconnected = Task.Delay(Timeout.Infinite, ctx.RequestAborted)
                    let! _ = Task.WhenAny(writer.Completion, disconnected)
                    ()
                with ex ->
                    failure.Value <- ex

                // Every exit path tears down the same way. `Remove` stops the
                // writer, and its loop exits without another write, so no
                // frame reaches this response after the handler returns.
                manager.Remove(scopeId, sink)
                do! channel.Unsubscribe subscriptionId
                do! channel.Unsubscribe platformSubscriptionId
                do! writer.Completion

                if not (isNull failure.Value) then
                    ExceptionDispatchInfo.Capture(failure.Value).Throw()

                return Some ctx
    }