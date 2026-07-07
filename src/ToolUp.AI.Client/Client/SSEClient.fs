// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.SSEClient

open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.Platform
open ToolUp.AI

let private log = Logger.forCategory "ai.sse"

// Sanctioned module-level mutable: per-tab one-shot warn-once latch.
// Phase 117 parity — one-shot guard for the "authenticated session fell
// back to query-param identity" AuthDiagnostics warn (mirrors the
// `QueryParamFallbackWarned` field on `NotificationClient`'s state
// record). Module-scoped so the per-tab AI stream logs it once rather
// than on every identity re-key.
let mutable private queryParamFallbackWarned = false

// ─── EventSource interop ─────────────────────────────────────────

[<Erase>]
type EventSource =
    abstract close: unit -> unit

[<Emit("new EventSource($0)")>]
let private createEventSource (url: string) : EventSource = jsNative

[<Emit("$0.onmessage = $1")>]
let private onMessage (es: EventSource) (handler: obj -> unit) : unit = jsNative

[<Emit("$0.onerror = $1")>]
let private onError (es: EventSource) (handler: obj -> unit) : unit = jsNative

[<Emit("$0.data")>]
let private getData (event: obj) : string = jsNative

[<Emit("$0.readyState")>]
let private getReadyState (es: EventSource) : int = jsNative

// EventSource readyState constants per WHATWG spec:
//   0 = CONNECTING — initial connect or auto-reconnect in progress.
//                    `onerror` fires on every transient blip while in
//                    this state; the browser will keep retrying.
//   1 = OPEN       — connection established.
//   2 = CLOSED     — connection permanently closed; no further retries.
[<Literal>]
let private ReadyStateClosed = 2

// ─── JSON deserialization ────────────────────────────────────────

type private ParseOutcome =
    | Parsed of AIStreamEvent
    | ParseFailure of preview: string

let private previewLength = 80

let private truncatePreview (raw: string) : string =
    if isNull raw then ""
    elif raw.Length <= previewLength then raw
    else raw.Substring(0, previewLength) + "…"

let private parseEvent (json: string) : ParseOutcome =
    try
        Parsed(Json.parseAs<AIStreamEvent> json)
    with _ ->
        ParseFailure(truncatePreview json)

// ─── Elmish subscription ─────────────────────────────────────────

/// Create an Elmish subscription that connects to the SSE endpoint
/// and dispatches AIStreamEvent messages. Returns a dispose function.
/// The user ID is passed as a query parameter because EventSource
/// does not support custom headers.
///
/// Phase 6g.A: `ClientToolInvoke` events bypass the Elmish dispatch
/// and route into `ClientToolRuntime.handleInvoke`. The agent-loop
/// suspend/resume mechanism is a transport concern, not a UI-state
/// concern — Elmish handlers see the preceding `ToolCallStarted` and
/// the subsequent `ToolCallCompleted` events for chat-UI rendering.
///
/// Phase 6h follow-up: parse failures and connection errors now
/// dispatch a `StreamError` event instead of silently swallowing.
/// The chat surface (with `StreamError` ungated from `DebugMode`)
/// renders the failure inline so the user can act on it before the
/// 60-second watchdog fires.
/// Open one EventSource under the current identity and wire the message
/// + error handlers. Factored out of `subscribe` so the re-keying path
/// (below) can reopen the stream under a fresh identity with identical
/// wiring.
///
/// Phase 117 parity with `NotificationClient.openConnection` — the
/// connect handshake prefers cookie auth: when an auth token is present
/// the URL carries no `?userId=` at all (the JWT cookie authenticates
/// the handshake and the server's middleware-resolved principal becomes
/// the scope — a query param would at best be redundant and at worst be
/// audited as a mismatch under `CookieRequired`). `?userId=` is the
/// anonymous-session marker only. An authenticated-kind session with no
/// token yet (the documented dev escape hatch where no `IAuthBridge` is
/// wired) still falls back to the param so dev deployments keep working,
/// but the degradation surfaces once per tab through `AuthDiagnostics`
/// instead of silently. EventSource sends same-origin cookies on the
/// handshake by default, so the cookie path needs no `withCredentials`.
let private openStream (userId: string) (dispatch: AIStreamEvent -> unit) : EventSource =
    let url =
        match UserSession.getAuthToken () with
        | Some _ -> "/api/ai/events"
        | None ->
            (match UserSession.getSubjectKind () with
             | AnonymousKind -> ()
             | UserKind
             | TeamMemberKind
             | ClaimBearerKind ->
                 if not queryParamFallbackWarned then
                     queryParamFallbackWarned <- true

                     log.Warn
                         "authenticated session has no auth token; AI SSE falling back to query-param identity (dev escape hatch — production deployments should wire an IAuthBridge so the cookie handshake carries identity)"

                     AuthDiagnostics.emitErr None "ai-sse-queryparam-fallback" {
                         Kind = "SseQueryParamIdentity"
                         SubCause = None
                     })

            $"/api/ai/events?userId={userId}"

    let es = createEventSource url

    onMessage es (fun event ->
        let data = getData event

        match parseEvent data with
        | Parsed(ClientToolInvoke(taskId, toolCallId, toolName, argsJson, activeModule, activePage)) ->
            ToolUp.AI.Client.ClientToolRuntime.handleInvoke taskId toolCallId toolName argsJson activeModule activePage
            |> Async.StartImmediate
        | Parsed evt -> dispatch evt
        | ParseFailure preview ->
            log.Warn $"SSE parse failure: {preview}"
            dispatch (StreamError $"Malformed SSE event: {preview}"))

    // EventSource fires `onerror` on every transient reconnect — HMR
    // drops in dev, brief network blips, server restarts. The browser
    // auto-retries while `readyState` stays at `CONNECTING (0)`, so
    // surfacing those to the user as chat errors would be noisy and
    // misleading (most "errors" resolve themselves within a second).
    //
    // We chat-surface a `StreamError` only when `readyState` reaches
    // `CLOSED (2)` — the browser has permanently given up. Transient
    // retries keep the existing console warning so an operator can
    // still see the activity in devtools without spamming the user.
    onError es (fun _ ->
        let state = getReadyState es

        if state = ReadyStateClosed then
            log.Warn "SSE connection closed — no further retries"
            dispatch (StreamError "Stream connection closed — refresh the page or check the server")
        else
            log.Warn "SSE connection error — auto-reconnecting")

    es

let subscribe (dispatch: AIStreamEvent -> unit) =
    // The stream URL pins `?userId=` at open time. Binding it once and
    // never re-keying leaves the stream stuck on whatever subject was
    // resolved at subscribe — so a sign-in that completes after the page
    // mounted, or any later auth-token / subject transition (e.g. switching
    // identity in `multiTeam`), would silently keep delivering the *old*
    // subject's AI events and drop the new one's. Mirror `NotificationClient`:
    // observe identity transitions and reopen the stream under the fresh id,
    // comparing the connected id so a same-user token refresh doesn't
    // needlessly cycle the connection.
    let mutable connectedUserId = UserSession.getUserId ()
    let mutable es = openStream connectedUserId dispatch

    // `onAuthTokenChange` is the canonical sign-in / sign-out / token-identity
    // signal (a short poll under the hood). On each transition, re-read the
    // subject and reopen only when it actually differs from the connected id.
    let stopWatcher =
        UserSession.onAuthTokenChange (fun _ ->
            let fresh = UserSession.getUserId ()

            if fresh <> connectedUserId then
                log.Warn $"SSE re-keying stream from '{connectedUserId}' to '{fresh}' after an identity change"
                es.close ()
                connectedUserId <- fresh
                es <- openStream fresh dispatch)

    // Return dispose function — stop the identity watcher, then close the
    // currently-open stream.
    fun () ->
        stopWatcher ()
        es.close ()