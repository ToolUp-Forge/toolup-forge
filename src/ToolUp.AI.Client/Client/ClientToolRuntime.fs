// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.ClientToolRuntime

open System
open System.Collections.Generic
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open ToolUp.Platform
open ToolUp.AI

let private log = Logger.forCategory "ai.tool"

// ─── Registry ────────────────────────────────────────────────────
//
// Phase 6g.A: client-side registry of `ClientResident` tool
// executors. Companion packages call `register` at startup to
// make their tools dispatchable. The SSE handler in `SSEClient`
// looks up incoming `ClientToolInvoke` events and invokes the
// matching executor.
//
// Sanctioned mutable global — same precedent as
// `NotificationClient.handlers` (per-tab singleton with an
// effectively-final lifetime). Dictionary instead of `Map` for
// O(1) lookup at SSE-event rate.

/// Context passed to client-resident tool executors. Carries the
/// session metadata an executor needs to act on "whatever the user
/// is currently viewing" without round-tripping that information
/// through the AI's tool args. Sourced from `AIMessageRequest`
/// (server-side) and serialised through the `ClientToolInvoke`
/// SSE event, so the active-module reading the executor sees
/// matches the server's view at the moment the AI's tool call was
/// dispatched.
type ClientToolContext = {
    ActiveModule: string option
    ActivePage: string option
}

/// A client-resident tool executor. Receives the per-invocation
/// `ClientToolContext` and the JSON-encoded args the model
/// supplied; returns the JSON-encoded result. Errors thrown from
/// the executor are caught by the runtime and rendered into the
/// same error envelope the server-side `ToolThrew` path produces —
/// so the model's recovery flow is identical regardless of whether
/// the failure happened on the server or in the browser.
///
/// Tuple input (`ClientToolContext * string`) rather than the
/// idiomatic curried form (`ClientToolContext -> string -> ...`).
/// Fable v5's `register` auto-wraps a 2-arg function with `curry2`
/// when storing it in a `Dictionary`; the resulting curried form
/// then mismatches with how 2-arg call sites are emitted, silently
/// dropping the second argument and returning the partial-application
/// function instead of the executor's `Async`. The tuple form has
/// no currying ambiguity — one argument in, one argument out.
type ClientToolExecutor = ClientToolContext * string -> Async<string>

let private registry = Dictionary<string, ClientToolExecutor>()

/// Register a client-resident tool. Call at startup from the
/// companion that owns the tool. Re-registration with the same
/// name overwrites silently — startup ordering is the only valid
/// timing, and a duplicate at startup means the deployment has two
/// companions claiming the same name (a bug, but not one we can
/// helpfully fail loudly about from the browser).
let register (name: string) (executor: ClientToolExecutor) = registry[name] <- executor

// ─── HTTP POST helpers ───────────────────────────────────────────

// `window.fetch` (not bare `fetch`) so this explicitly resolves the
// guarded global at call time — the CsrfClient request-guard wraps
// `window.fetch` to attach identity + `X-CSRF-Token`. Matches the
// other AI POST sites; removes the latent bypass if this is ever
// moved to a captured binding.
[<Emit("window.fetch($0, $1)")>]
let private fetch (url: string) (opts: obj) : JS.Promise<obj> = jsNative

/// Phase 6g.A: POST the result of a client-resident tool execution
/// back to the server's `/api/ai/tool-result` endpoint. The handler
/// looks up the matching `TaskCompletionSource` in the dispatch
/// registry and completes the suspended agent loop. Failure to POST
/// (network, server gone) leaves the agent loop awaiting until its
/// own 90 s timeout fires; we log to console rather than retry.
let private postResult (taskId: Guid) (toolCallId: Guid) (resultJson: string) : Async<unit> = async {
    let request: ClientToolResultRequest = {
        TaskId = taskId
        ToolCallId = toolCallId
        ResultJson = resultJson
    }

    let body = Json.serialize request

    let opts =
        createObj [
            "method" ==> "POST"
            "headers"
            ==> createObj [
                "Content-Type" ==> "application/json"
                "X-User-Id" ==> UserSession.getUserId ()
            ]
            "body" ==> body
        ]

    try
        do! fetch "/api/ai/tool-result" opts |> Async.AwaitPromise |> Async.Ignore
    with ex ->
        log.Error("Failed to POST client tool result", Some ex)
}

// ─── Error envelope ──────────────────────────────────────────────
//
// Mirrors the server-side `ToolInvocationError.toToolResultContent`
// shape so the model sees the same JSON regardless of which side
// the failure originated on. Hand-built JSON rather than running
// it through Json.serialize — the wire shape is fixed and the
// values are simple strings.

let private errorEnvelope (kind: string) (toolName: string) (detail: string) : string =
    let escape (s: string) =
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r")

    $"""{{"error":"{escape kind}","tool":"{escape toolName}","detail":"{escape detail}"}}"""

// ─── Invocation entry point ──────────────────────────────────────

/// Phase 6g.A: client watchdog for tool execution. The server's own
/// 90 s timeout is the outer envelope; the client times out earlier
/// (30 s) so the user-facing failure mode is "client gave up" with
/// an explicit error envelope rather than "server timed out" with a
/// generic abort. Tools that legitimately need longer should be
/// either (a) re-architected to dispatch async work and report
/// completion via a separate channel, or (b) declared
/// `ServerResident` so they don't sit on the SSE path.
[<Literal>]
let private ClientWatchdogMs = 30_000

/// Race a tool execution against the watchdog. Returns either the
/// executor's result or a watchdog timeout error. We don't try to
/// cancel the executor on timeout — F# `Async` cancellation in Fable
/// is best-effort and most browser-side work isn't truly cancellable
/// anyway. The orphaned executor finishes whenever; nothing observes
/// its result.
///
/// `Async.StartChild(comp, ms)` is the Fable-native timeout primitive:
/// the computation runs as a hot promise, raced against a `setTimeout`
/// that rejects with `TimeoutException` after `ms`. Whichever settles
/// first wins.
let private runWithWatchdog
    (executor: ClientToolExecutor)
    (toolCtx: ClientToolContext)
    (argsJson: string)
    : Async<Result<string, string>> =
    async {
        try
            let! child = Async.StartChild(executor (toolCtx, argsJson), ClientWatchdogMs)
            let! result = child
            return Ok result
        with :? TimeoutException ->
            return Error $"Client watchdog fired after {ClientWatchdogMs / 1000} seconds"
    }

/// Phase 6g.A: handle an incoming `ClientToolInvoke` SSE event. Runs
/// the matching registered executor (or returns an unknown-tool
/// error envelope) and POSTs the result back to the server.
///
/// Called by `SSEClient.subscribe` rather than dispatched into the
/// Elmish loop — the agent-loop suspend/resume is a transport
/// concern, not a UI-state concern. The Elmish handlers see the
/// preceding `ToolCallStarted` and the subsequent `ToolCallCompleted`
/// events for chat-UI rendering.
let handleInvoke
    (taskId: Guid)
    (toolCallId: Guid)
    (toolName: string)
    (argsJson: string)
    (activeModule: string option)
    (activePage: string option)
    : Async<unit> =
    async {
        let toolCtx: ClientToolContext = {
            ActiveModule = activeModule
            ActivePage = activePage
        }

        let resultJson = async {
            match registry.TryGetValue(toolName) with
            | true, executor ->
                try
                    let! result = runWithWatchdog executor toolCtx argsJson

                    match result with
                    | Ok r -> return r
                    | Error e -> return errorEnvelope "ClientWatchdogTimeout" toolName e
                with ex ->
                    return errorEnvelope "ToolThrew" toolName ex.Message
            | false, _ ->
                return
                    errorEnvelope
                        "UnknownClientTool"
                        toolName
                        "No client-resident executor is registered for this tool."
        }

        let! json = resultJson
        do! postResult taskId toolCallId json
    }