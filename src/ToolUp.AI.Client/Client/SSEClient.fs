// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.SSEClient

open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.Platform
open ToolUp.AI

let private log = Logger.forCategory "ai.sse"

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
let subscribe (dispatch: AIStreamEvent -> unit) =
    let userId = UserSession.getUserId ()
    let es = createEventSource $"/api/ai/events?userId={userId}"

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

    // Return dispose function
    fun () -> es.close ()