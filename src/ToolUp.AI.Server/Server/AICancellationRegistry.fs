module ToolUp.AI.AICancellationRegistry

open System
open System.Collections.Concurrent
open System.Threading
open Microsoft.AspNetCore.Http
open Giraffe

// ─── Cancellation registry (Phase 6h) ────────────────────────────
//
// Per-task `CancellationTokenSource` so the user can stop a
// running agent loop mid-stream. Lifecycle:
//
//   1. `AIAssistantHandler.SubmitMessage` calls `Register taskId`
//      before kicking off the background agent loop. Receives a
//      `CancellationToken` to thread into `runAgentLoop`.
//   2. `runAgentLoop` checks `token.IsCancellationRequested` at
//      turn boundaries; on cancellation, emits a `StreamCancelled`
//      SSE event and exits cleanly with whatever content was
//      already streamed.
//   3. The user clicks Cancel in the chat UI → POST
//      `/api/ai/cancel/{taskId}` → `TryCancel` flips the token.
//   4. The handler's `try ... finally` block calls `Unregister` so
//      the CTS is disposed and the entry removed from the
//      dictionary. Stale entries don't accumulate.
//
// Per-process singleton — distributed deployments need a Redis-
// backed equivalent so the cancel POST can find the CTS even when
// the streaming task and the cancel POST hit different instances.
// Mirrors the same Phase 9c portability concern that
// `ClientToolDispatchRegistry` (Phase 6g.A) has.

type AICancellationRegistry() =
    let registry = ConcurrentDictionary<Guid, CancellationTokenSource>()

    /// Register a fresh `CancellationTokenSource` for the task.
    /// Returns the token to thread into `runAgentLoop`. Pre-existing
    /// entries (extremely unlikely — taskIds are fresh GUIDs) are
    /// disposed and replaced.
    member _.Register(taskId: Guid) : CancellationToken =
        let cts = new CancellationTokenSource()

        match registry.TryAdd(taskId, cts) with
        | true -> cts.Token
        | false ->
            // Defensive — same Guid twice means upstream bug; replace
            // the prior entry rather than silently sharing it.
            match registry.TryRemove(taskId) with
            | true, oldCts -> oldCts.Dispose()
            | false, _ -> ()

            registry[taskId] <- cts
            cts.Token

    /// User clicked Cancel. Returns true when a matching
    /// `CancellationTokenSource` was found and cancelled; false
    /// when the task already completed (and unregistered itself)
    /// or never registered.
    member _.TryCancel(taskId: Guid) : bool =
        match registry.TryRemove(taskId) with
        | true, cts ->
            try
                cts.Cancel()
            with _ ->
                ()

            cts.Dispose()
            true
        | false, _ -> false

    /// Called by the handler's `try ... finally` block once the
    /// task completes naturally. Disposes the CTS and removes the
    /// entry so cancel POSTs after natural completion silently 404.
    member _.Unregister(taskId: Guid) : unit =
        match registry.TryRemove(taskId) with
        | true, cts -> cts.Dispose()
        | false, _ -> ()

// ─── /api/ai/cancel/{taskId} POST handler ────────────────────────

let cancelHandler (taskId: Guid) : HttpHandler =
    fun next (ctx: HttpContext) -> task {
        let registry =
            ctx.RequestServices.GetService(typeof<AICancellationRegistry>) :?> AICancellationRegistry

        let cancelled = registry.TryCancel(taskId)

        if cancelled then
            ctx.Response.StatusCode <- 200
            return! next ctx
        else
            // Task already completed naturally, or the taskId is
            // unknown. Either way the user's intent ("stop this
            // task") is already satisfied — return 404 so the
            // client can clear its UI without surfacing an error.
            ctx.Response.StatusCode <- 404
            return! next ctx
    }