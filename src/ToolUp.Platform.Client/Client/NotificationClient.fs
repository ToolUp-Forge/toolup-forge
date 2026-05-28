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
// Five module-level mutables for this per-tab singleton (documented
// under "No new side effects" in `src/ToolUp.Platform/README.md`):
//
//   connection         — lazily opened on first `subscribe` /
//                        `publishLocal` call, never closed (except by
//                        the Phase 58 404 fallback when the server has
//                        no `/api/notifications` mounted).
//   nextHandlerId      — monotonic id seed handed out on `subscribe`
//                        so the returned dispose thunk can find its
//                        slot.
//   handlers           — list of (id, handler) pairs rebuilt on every
//                        subscribe/unsubscribe so each subscriber can
//                        dispose independently without holding a
//                        reference to the function value.
//   connectionGivenUp  — Phase 58 latch set by the defensive 404
//                        fallback in `onError`; subsequent
//                        `ensureConnected` calls early-return so a
//                        404'd session does not retry-loop.
//   explicitOffLogged  — Phase 58 one-shot guard so the
//                        "notifications explicitly disabled" info
//                        line is logged once per tab rather than on
//                        every `subscribe`.
//
// All five survive until the tab is closed. No per-request state lives here.

let mutable private connection: EventSource option = None
let mutable private nextHandlerId = 0
let mutable private handlers: (int * (NotificationEnvelope -> unit)) list = []

// Phase 58 — `connectionGivenUp` is set by the defensive 404 fallback
// inside `onError` (below) when the first connect attempt fails fatally
// (`readyState = CLOSED`). Covers the upgrade path where the server is
// `NoNotifications` / `NoNotificationsExplicit` but the consumer has
// not yet wired `__TOOLUP_NOTIFICATIONS_DISABLED__` in their Vite
// config: the first connect 404s, we close out for the session, no
// further `subscribe` call re-opens. `explicitOffLogged` ensures the
// "notifications disabled via bundle constant" info message fires once
// per tab rather than on every `subscribe`.
let mutable private connectionGivenUp = false
let mutable private explicitOffLogged = false

let private fanOut (envelope: NotificationEnvelope) =
    // Iterate against a snapshot — if a handler unsubscribes during
    // dispatch we don't want to skip siblings or revisit them.
    let snapshot = handlers

    for _, handler in snapshot do
        try
            handler envelope
        with ex ->
            log.Warn $"handler threw; continuing: {ex.Message}"

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
        // The defensive 404 fallback below already concluded the
        // server is not serving `/api/notifications` for this session;
        // skip without re-attempting (a re-attempt would just re-404
        // and retry-loop).
        ()
    else
        match connection with
        | Some _ -> ()
        | None ->
            let userId = UserSession.getUserId ()
            let es = createEventSource $"/api/notifications?userId={userId}"

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

            // Phase 58 defensive 404 fallback. EventSource auto-reconnects
            // on transient failures (readyState = CONNECTING), so the prior
            // warn-and-keep-trying behaviour is preserved. But when the
            // browser surfaces a fatal failure (readyState = CLOSED — per
            // the WHATWG spec, the typical cause is an HTTP error response
            // whose MIME type is not `text/event-stream`, i.e. a 404 from a
            // server that did not mount `/api/notifications`), we treat the
            // notification channel as off for this session: close out the
            // handle and set `connectionGivenUp` so later `subscribe` calls
            // do not re-open. Handles the upgrade path where the server is
            // `NoNotifications`/`NoNotificationsExplicit` but the consumer
            // has not yet wired `__TOOLUP_NOTIFICATIONS_DISABLED__`.
            onError es (fun _ ->
                if getReadyState es = eventSourceClosed then
                    log.Warn "notifications disabled (404); treating as explicit-off for this session"

                    closeEventSource es
                    connection <- None
                    connectionGivenUp <- true
                else
                    log.Warn "SSE connection error — EventSource will retry automatically")

            connection <- Some es

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