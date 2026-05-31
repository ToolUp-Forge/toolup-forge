module ToolUp.AI.ClientToolDispatch

open System
open System.Collections.Concurrent
open System.Threading.Tasks
open System.IO
open Microsoft.AspNetCore.Http
open Newtonsoft.Json
open Giraffe
open ToolUp.AI
open ToolUp.Platform

/// Phase 6g.A: in-memory registry of pending client-resident tool
/// calls. The agent loop registers a `TaskCompletionSource<string>`
/// when it dispatches a `ClientResident` tool to the browser via
/// the `ClientToolInvoke` SSE event; the `/api/ai/tool-result` POST
/// handler completes it when the browser POSTs the result.
///
/// Per-process singleton — distributed deployments running multiple
/// silos behind a load balancer need a Redis / SignalR-backed
/// equivalent so the result POST can find the suspended TCS even
/// when the SSE stream and the POST hit different instances. Out of
/// scope for Phase 6g.A; flagged for the eventual distributed
/// companion (mirror of Phase 9c half 2's planned scope).
type ClientToolDispatchRegistry() =
    let pending = ConcurrentDictionary<Guid, TaskCompletionSource<string>>()

    /// Register a pending tool call. Returns the Task the agent loop
    /// awaits — completes when the browser POSTs the result via
    /// `/api/ai/tool-result`. The task's continuation runs on the
    /// thread pool, not on the SSE stream's thread.
    member _.RegisterPending(toolCallId: Guid) : Task<string> =
        let tcs =
            TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

        pending[toolCallId] <- tcs
        tcs.Task

    /// Complete a pending tool call with the client-supplied result
    /// JSON. Returns true if a matching pending call was found, false
    /// if it timed out / was aborted / was never registered (e.g. a
    /// stale POST after the loop already gave up).
    member _.TryComplete(toolCallId: Guid, resultJson: string) : bool =
        match pending.TryRemove(toolCallId) with
        | true, tcs -> tcs.TrySetResult(resultJson)
        | false, _ -> false

    /// Abort a pending tool call (used by the agent loop's own
    /// timeout path, and by future cancellation flows). Sets an
    /// `InvalidOperationException` on the TCS so the awaiter sees a
    /// failure that the agent loop's existing `ToolThrew` recovery
    /// path can classify and surface back to the model.
    member _.TryAbort(toolCallId: Guid, reason: string) : bool =
        match pending.TryRemove(toolCallId) with
        | true, tcs -> tcs.TrySetException(InvalidOperationException(reason))
        | false, _ -> false

// ─── /api/ai/tool-result POST handler ────────────────────────────

/// Phase 6g.A: serialiser for the result payload. Newtonsoft with
/// `FableJsonConverter` matches the wire format the rest of the AI
/// surface uses (Fable.Remoting client serialises with the same
/// converter on the way in, AIStreamEvent rendering uses it on the
/// way out — see `SSEHandler.fableJsonSettings`).
let private resultJsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(ToolUp.Remoting.Json.FableJsonConverter())
    s

/// Phase 6g.A: Giraffe handler for `/api/ai/tool-result`. Browsers
/// POST a `ClientToolResultRequest` after running a `ClientResident`
/// tool; this handler resolves the registry singleton from DI and
/// completes the matching pending TCS so the suspended agent loop
/// can resume.
///
/// Unknown / stale / already-completed `toolCallId`s return 404 with
/// no body — the result is dropped silently rather than surfaced to
/// the user. This is the right shape for late-arriving POSTs (the
/// agent loop already timed out and recovered) and for legitimate
/// races where the agent loop's own `TryAbort` won.
///
/// Exceptions during JSON parse return 400. Authorisation is by the
/// caller's `AccessContext` — the same scope-resolution middleware
/// that gates `/api/*` endpoints applies here, so a user can't
/// complete another user's tool calls. (`toolCallId` is a Guid,
/// guessing one is computationally infeasible, but the scope gate
/// is the load-bearing protection.)
let clientToolResultHandler: HttpHandler =
    fun next (ctx: HttpContext) -> task {
        // Observational logger; silent no-op when DI has none (tests
        // bypassing `compose`). Used only to surface why a tool-result
        // POST was rejected — never logs the body (tool results may
        // carry sensitive payloads).
        let logger: ILogger =
            match ctx.RequestServices.GetService(typeof<ILogger>) with
            | :? ILogger as l -> l
            | _ ->
                { new ILogger with
                    member _.Debug _ = ()
                    member _.Info _ = ()
                    member _.Warn _ = ()
                    member _.Error(_, _) = ()
                }

        try
            use reader = new StreamReader(ctx.Request.Body)
            let! body = reader.ReadToEndAsync()

            let req =
                JsonConvert.DeserializeObject<ClientToolResultRequest>(body, resultJsonSettings)

            let registry =
                ctx.RequestServices.GetService(typeof<ClientToolDispatchRegistry>) :?> ClientToolDispatchRegistry

            let completed = registry.TryComplete(req.ToolCallId, req.ResultJson)

            if completed then
                ctx.Response.StatusCode <- 200
                return! next ctx
            else
                ctx.Response.StatusCode <- 404
                return! next ctx
        with ex ->
            // Malformed/undeserializable tool-result POST. Was a silent
            // blanket 400 — log the exception type/message (not the body)
            // so a client serialization regression is diagnosable instead
            // of presenting as an opaque 400.
            logger.Warn(sprintf "[ClientToolDispatch] tool-result POST rejected (400): %s" ex.Message)
            ctx.Response.StatusCode <- 400
            return! next ctx
    }