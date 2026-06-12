// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.NotificationClient

open System
open Fable.Core
open Fable.SimpleJson
open ToolUp.Platform

let private log = Logger.forCategory "client.notification"

// ─── EventSource interop ─────────────────────────────────────────
// Named events (`event: <kind>\ndata: ...`) can't be observed from
// `onmessage` — that handler only receives default-typed events. We
// use `addEventListener(kind, handler)` per known kind so the client
// router can dispatch by kind without parsing the payload.

[<Erase>]
type EventSource =
    abstract close: unit -> unit

[<Emit("new EventSource($0)")>]
let private createEventSource (url: string) : EventSource = jsNative

[<Emit("$0.addEventListener($1, $2)")>]
let private addEventListener (es: EventSource) (name: string) (handler: obj -> unit) : unit = jsNative

[<Emit("$0.onerror = $1")>]
let private onError (es: EventSource) (handler: obj -> unit) : unit = jsNative

[<Emit("$0.readyState")>]
let private getReadyState (es: EventSource) : int = jsNative

// EventSource.CLOSED per the WHATWG spec — the browser gave up on the
// connection (typical cause: HTTP error response with a non-
// `text/event-stream` MIME type, e.g. a 404 from a server that did not
// mount `/api/notifications`). CONNECTING (0) and OPEN (1) are
// transient / live; only CLOSED (2) signals a fatal failure that the
// browser will not auto-retry.
[<Literal>]
let private eventSourceClosed = 2

[<Emit("$0.close()")>]
let private closeEventSource (es: EventSource) : unit = jsNative

[<Emit("$0.data")>]
let private getData (event: obj) : string = jsNative

// ─── JSON deserialization ────────────────────────────────────────

let private parseEnvelope (json: string) : NotificationEnvelope option =
    try
        Some(Json.parseAs<NotificationEnvelope> json)
    with _ ->
        None

// ─── Per-tab singleton fan-out ───────────────────────────────────
//
// One `EventSource` per tab — as promised by the comment on `subscribe`
// below. Multiple callers (ToastCentre, shell `ModuleAction` router, AI
// companion streams) register independent handlers; the single connection
// fans each envelope out to all of them.
//
// Module-level mutables for this per-tab singleton (documented under
// "No new side effects" in `src/ToolUp.Platform/README.md`):
//
//   connection         — lazily opened on first `subscribe` /
//                        `publishLocal` call; closed by `reset` /
//                        `reconnect` (auth transitions) and by the
//                        fatal-close handling in `onError`.
//   nextHandlerId      — monotonic id seed handed out on `subscribe`
//                        so the returned dispose thunk can find its
//                        slot.
//   handlers           — list of (id, handler) pairs rebuilt on every
//                        subscribe/unsubscribe so each subscriber can
//                        dispose independently without holding a
//                        reference to the function value.
//   connectionGivenUp  — latch set when the Phase 117 bounded retry
//                        budget (`fatalCloseRetryPolicy`) is
//                        exhausted; subsequent `ensureConnected` calls
//                        early-return so a dead endpoint does not
//                        retry-loop forever. Cleared by `reset` /
//                        `reconnect` — an auth transition earns a
//                        fresh budget. (Phase 58 shipped this as a
//                        permanent first-strike latch; Phase 117 made
//                        it the post-retry terminal state.)
//   explicitOffLogged  — Phase 58 one-shot guard so the
//                        "notifications explicitly disabled" info
//                        line is logged once per tab rather than on
//                        every `subscribe`.
//   connectedUserId    — identity the live EventSource was opened
//                        under; `reconnect` compares it against the
//                        fresh `UserSession.getUserId ()` so same-user
//                        token refreshes don't cycle the stream.
//   retryAttempts      — fatal-close retries consumed since the last
//                        successful open (reset on `onopen`).
//   retryTimerHandle   — pending `setTimeout` handle for the next
//                        retry, so `reset` can cancel it.
//   queryParamFallbackWarned — one-shot guard for the Phase 117
//                        "authenticated session fell back to
//                        query-param identity" AuthDiagnostics warn.
//
// All survive until the tab is closed. No per-request state lives here.

let mutable private connection: EventSource option = None
let mutable private nextHandlerId = 0
let mutable private handlers: (int * (NotificationEnvelope -> unit)) list = []

// Phase 117 — fatal-close retry budget, as data. Fatal closes
// (`readyState = CLOSED` — the browser will not auto-retry) were
// previously a permanent first-strike latch (Phase 58), which meant a
// single 502 during a server deploy silently killed notifications for
// the life of the tab. Three attempts spread over ~60 s ride out a
// rolling restart; a genuinely absent endpoint (404 from a server with
// no `/api/notifications` mounted) exhausts the budget in a minute and
// latches, preserving Phase 58's no-retry-loop guarantee.
let private fatalCloseRetryDelaysMs = [ 5_000; 15_000; 40_000 ]

let mutable private connectionGivenUp = false
let mutable private explicitOffLogged = false
let mutable private connectedUserId: string option = None
let mutable private retryAttempts = 0
let mutable private retryTimerHandle: int option = None
let mutable private queryParamFallbackWarned = false

[<Emit("setTimeout($0, $1)")>]
let private setTimeout (callback: unit -> unit) (delayMs: int) : int = jsNative

[<Emit("clearTimeout($0)")>]
let private clearTimeout (handle: int) : unit = jsNative

[<Emit("$0.onopen = $1")>]
let private onOpen (es: EventSource) (handler: obj -> unit) : unit = jsNative

let private fanOut (envelope: NotificationEnvelope) =
    // Iterate against a snapshot — if a handler unsubscribes during
    // dispatch we don't want to skip siblings or revisit them.
    let snapshot = handlers

    for _, handler in snapshot do
        try
            handler envelope
        with ex ->
            log.Warn $"handler threw; continuing: {ex.Message}"

/// Open the EventSource under the current identity. Phase 117 — the
/// connect handshake prefers cookie auth: when an auth token is present
/// the URL carries no `?userId=` at all (the JWT cookie written by
/// `UserSession.setAuthToken` authenticates the handshake and the
/// server's middleware-resolved principal becomes the scope — a query
/// param would at best be redundant and at worst audited as a
/// mismatch). `?userId=` is the anonymous-session marker only. An
/// authenticated-kind session with no token yet (the documented dev
/// escape hatch where no `IAuthBridge` is wired) still falls back to
/// the param so dev deployments keep working, but the degradation is
/// surfaced once per tab through `AuthDiagnostics` instead of silently.
let rec private openConnection () =
    let userId = UserSession.getUserId ()

    let url =
        match UserSession.getAuthToken () with
        | Some _ -> "/api/notifications"
        | None ->
            (match UserSession.getSubjectKind () with
             | AnonymousKind -> ()
             | UserKind
             | TeamMemberKind
             | ClaimBearerKind ->
                 if not queryParamFallbackWarned then
                     queryParamFallbackWarned <- true

                     log.Warn
                         "authenticated session has no auth token; SSE falling back to query-param identity (dev escape hatch — production deployments should wire an IAuthBridge so the cookie handshake carries identity)"

                     AuthDiagnostics.emitErr None "notifications-sse-queryparam-fallback" {
                         Kind = "SseQueryParamIdentity"
                         SubCause = None
                     })

            $"/api/notifications?userId={userId}"

    let es = createEventSource url
    connectedUserId <- Some userId

    let forward (event: obj) =
        match parseEnvelope (getData event) with
        | Some env -> fanOut env
        | None -> ()

    // Register one listener per known `NotificationKind`. Adding a new
    // kind requires an entry here and a matching case on the server's
    // `Notification` DU — mirrors the Fable-compat rule that kinds are
    // kept in sync manually (no reflection on the client).
    addEventListener es NotificationKind.SystemMessage forward
    addEventListener es NotificationKind.JobCompleted forward
    addEventListener es NotificationKind.DataRefreshed forward
    addEventListener es NotificationKind.TeamActivity forward
    addEventListener es NotificationKind.ModuleAction forward
    addEventListener es NotificationKind.CustomNotification forward
    addEventListener es NotificationKind.MembershipChanged forward

    // Each successful open earns a fresh fatal-close retry budget —
    // a server that restarts twice a week should not creep towards
    // the latch.
    onOpen es (fun _ -> retryAttempts <- 0)

    // Phase 58 / Phase 117 fatal-close handling. EventSource
    // auto-reconnects on transient failures (readyState = CONNECTING),
    // so the warn-and-keep-trying behaviour is preserved for those.
    // When the browser surfaces a fatal failure (readyState = CLOSED —
    // per the WHATWG spec the typical cause is an HTTP error response
    // whose MIME type is not `text/event-stream`: a 404 from a server
    // with no `/api/notifications` mounted, a 401 under CookieRequired,
    // or a 502 mid-deploy), retry on the bounded
    // `fatalCloseRetryDelaysMs` backoff schedule and only then latch
    // `connectionGivenUp` so later `subscribe` calls stop re-opening.
    // Phase 58 latched on the first fatal close and asserted "404" in
    // the log without evidence — one 502 during a deploy killed
    // notifications for the life of the tab.
    onError es (fun _ ->
        if getReadyState es = eventSourceClosed then
            closeEventSource es
            connection <- None
            connectedUserId <- None

            match List.tryItem retryAttempts fatalCloseRetryDelaysMs with
            | Some delayMs ->
                retryAttempts <- retryAttempts + 1

                log.Warn
                    $"SSE connection closed fatally (HTTP error or non-SSE response); retrying ({retryAttempts}/{List.length fatalCloseRetryDelaysMs}) in {delayMs}ms"

                retryTimerHandle <-
                    Some(
                        setTimeout
                            (fun () ->
                                retryTimerHandle <- None

                                if not connectionGivenUp && connection.IsNone then
                                    openConnection ())
                            delayMs
                    )
            | None ->
                connectionGivenUp <- true

                log.Warn
                    "notifications unavailable after exhausting reconnect attempts; giving up for this session (the server may not mount /api/notifications, or the endpoint kept failing)"

                AuthDiagnostics.emitErr None "notifications-sse-gave-up" {
                    Kind = "SseRetriesExhausted"
                    SubCause = None
                }
        else
            log.Warn "SSE connection error — EventSource will retry automatically")

    connection <- Some es

let private ensureConnected () =
    // Phase 58 — `Notifications = NoNotificationsExplicit` on the
    // server pairs with `__TOOLUP_NOTIFICATIONS_DISABLED__ = true` in
    // the consumer's Vite config; when the bundle constant is set we
    // never open EventSource, so no `/api/notifications` request fires
    // and no 404 retry loop burns CPU.
    if BundleConstants.notificationsDisabledExplicitly then
        if not explicitOffLogged then
            log.Info
                "Notifications explicitly disabled (__TOOLUP_NOTIFICATIONS_DISABLED__); skipping EventSource for this session"

            explicitOffLogged <- true
    elif connectionGivenUp then
        // The bounded fatal-close retry above already exhausted its
        // budget for this session; skip without re-attempting. `reset`
        // / `reconnect` (auth transitions) clear the latch.
        ()
    else
        match connection with
        | Some _ -> ()
        | None -> openConnection ()

/// Phase 117 — tear down the per-tab connection state: cancel any
/// pending fatal-close retry, close the EventSource, clear the give-up
/// latch and retry counter. Does NOT reopen — callers that want a live
/// stream again call `reconnect`, or the next `subscribe` reopens
/// lazily. The shell calls this on sign-out so the previous user's
/// stream is closed rather than outliving the session.
let reset () =
    match retryTimerHandle with
    | Some handle ->
        clearTimeout handle
        retryTimerHandle <- None
    | None -> ()

    match connection with
    | Some es ->
        closeEventSource es
        connection <- None
    | None -> ()

    connectedUserId <- None
    connectionGivenUp <- false
    retryAttempts <- 0

/// Phase 117 — cycle the stream onto the current identity
/// (`UserSession.getUserId ()` at call time, not at original connect
/// time). No-op when already connected under the same identity: the
/// auth watcher fires on every token change including same-user
/// refreshes (~hourly via the bridge), and cycling the connection for
/// those would drop envelopes for no benefit. Clears the give-up latch
/// via `reset` — an identity transition always earns a fresh connect
/// attempt even when the previous identity's retry budget was
/// exhausted.
let reconnect () =
    let freshUserId = UserSession.getUserId ()

    let alreadyCurrent =
        match connection, connectedUserId with
        | Some _, Some current -> current = freshUserId
        | _ -> false

    if not alreadyCurrent then
        reset ()
        ensureConnected ()

// ─── Elmish subscription ─────────────────────────────────────────

/// Register a handler for every `NotificationEnvelope` delivered to
/// this tab, from both the server stream and `publishLocal` calls.
/// Opens the underlying `EventSource` the first time any caller
/// subscribes; later subscribers join the shared connection without
/// opening a second one.
///
/// Returns a dispose function the caller invokes on teardown.
/// Unsubscribing is by identity — two disposers for the same handler
/// are safe (second is a no-op).
///
/// One `EventSource` per tab: the router dispatches to feature-specific
/// handlers (toasts, refresh banners, module-action decoders, AI panels)
/// from this single stream. Historically this opened a fresh connection
/// per subscriber; the singleton form matches the commented intent and
/// lets `publishLocal` work.
let subscribe (handler: NotificationEnvelope -> unit) : unit -> unit =
    ensureConnected ()
    let id = nextHandlerId
    nextHandlerId <- nextHandlerId + 1
    handlers <- (id, handler) :: handlers

    fun () -> handlers <- handlers |> List.filter (fun (hid, _) -> hid <> id)

/// Dispatch a locally-synthesised envelope to every registered handler
/// as though it had arrived from the server. Used by the shell's
/// `ModuleAction` router to pop a "Results available in {module}" toast
/// when an action routes to an inactive module — the envelope never
/// travels over the wire, but it reaches the same subscribers.
///
/// Callers stamp `Id` / `OccurredAt` themselves (via
/// `NotificationEnvelope.create`); `ScopeId` should be the caller's own
/// scope to keep the replay semantics intact.
let publishLocal (envelope: NotificationEnvelope) : unit = fanOut envelope