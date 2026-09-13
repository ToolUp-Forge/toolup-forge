// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.McpHost

open System
open System.Diagnostics
open Microsoft.AspNetCore.Http
open Giraffe
open ToolUp.Platform
open ToolUp.AI.AIToolRegistry

// ─── Phase 489 — the MCP endpoint ────────────────────────────────────
//
// One route, `POST {Route}`, speaking MCP over the streamable-HTTP
// transport's JSON leg. An off-the-shelf client connects with an agent
// credential on the standard `Authorization: Bearer` header, handshakes
// with `initialize`, discovers with `tools/list` and invokes with
// `tools/call`.
//
// **What this host does NOT do, stated so it is not mistaken for an
// omission.** It does not open a server-initiated SSE stream on `GET`.
// The streamable-HTTP transport makes that OPTIONAL — a server with
// nothing to push is conformant answering `405 Method Not Allowed`, and
// this one has nothing to push: every tool call is request/response, and
// the tool list is fixed at compose, which is why `initialize` honestly
// advertises `tools.listChanged: false`. Advertising a stream and then
// never writing to it would leave a client holding a connection open
// forever.
//
// **Discovery is authorisation-filtered and invocation re-checks the
// same decision** — `McpAuthorization.visibleTools` is the whole of it,
// and `tools/call` looks its name up in that list rather than consulting
// a second predicate. See that module's header for why one predicate
// rather than two.
//
// **Ordering of the gates on a call, and why.** Authorisation first,
// then rate limit, then budget, then the tool. A refusal must never cost
// the agent a budget slot it was not going to use, and an unauthorised
// caller must not be able to burn a legitimate agent's allowance by
// naming tools it cannot reach. Rate limit before budget because the
// rate limit is the cheaper read and the one that bounds a burst; budget
// last because it holds a reservation that must then be released.

/// Compose-time configuration for the endpoint.
type McpHostOptions = {
    /// Path the endpoint is mounted at.
    Route: string
    /// Version reported in `initialize`'s `serverInfo`. Supplied by the
    /// composition rather than read from the assembly, so a deployment
    /// can report its own build identity to the agents that connect to
    /// it.
    ServerVersion: string
    /// Resource envelope applied to any agent with no override.
    DefaultPolicy: AgentPolicy
    /// Per-account overrides, keyed by `ServiceAccount.AccountId`. An
    /// account present here is governed ENTIRELY by its entry — the same
    /// replace-not-merge rule Phase 451's `ComputeBudget.ClassLimits`
    /// states, and for the same reason: an operator who wrote one agent's
    /// ceilings should not have to read a second place to know what that
    /// agent is allowed.
    PolicyOverrides: Map<string, AgentPolicy>
}

[<RequireQualifiedAccess>]
module McpHostOptions =
    /// The endpoint at its default path with no ceilings — the value a
    /// composition that declares nothing gets. Reaches no ledger and no
    /// rate-limit store (GP 13).
    let defaults: McpHostOptions = {
        Route = McpHostConstants.DefaultRoute
        ServerVersion = "0.0.0"
        DefaultPolicy = AgentPolicy.unrestricted
        PolicyOverrides = Map.empty
    }

    /// The policy governing one account.
    let policyFor (accountId: string) (options: McpHostOptions) : AgentPolicy =
        options.PolicyOverrides
        |> Map.tryFind accountId
        |> Option.defaultValue options.DefaultPolicy

/// MCP's session header, as named by the streamable-HTTP transport.
[<Literal>]
let SessionHeader = "Mcp-Session-Id"

let private sessionId (ctx: HttpContext) : string option =
    match ctx.Request.Headers.TryGetValue SessionHeader with
    | true, values when values.Count > 0 && not (String.IsNullOrWhiteSpace values[0]) -> Some values[0]
    | _ -> None

let private service<'T> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService typeof<'T> with
    | :? 'T as resolved -> Some resolved
    | _ -> None

/// Write one JSON-RPC response at the status its error class calls for.
///
/// A rate-limit refusal additionally carries `Retry-After`, because that
/// is the header every HTTP retry mechanism in the world already reads,
/// and an agent backing off correctly without understanding MCP's error
/// data is strictly better than one that does not back off at all.
let private respond (ctx: HttpContext) (status: int) (retryAfter: int option) (json: string) =
    ctx.SetStatusCode status
    ctx.SetContentType "application/json"

    match retryAfter with
    | Some seconds -> ctx.SetHttpHeader("Retry-After", string seconds)
    | None -> ()

    ctx.WriteStringAsync json

let private respondError (ctx: HttpContext) (rawId: string option) (error: McpError) =
    let retryAfter =
        match error with
        | McpError.RateLimited limited -> Some limited.RetryAfterSeconds
        | _ -> None

    if error = McpError.Unauthorized then
        // The MCP authorization specification requires a challenge on a
        // 401 so a client knows what to present. `Bearer` names the
        // scheme; the realm is the endpoint itself.
        ctx.SetHttpHeader("WWW-Authenticate", "Bearer")

    respond ctx (McpProtocol.httpStatus error) retryAfter (McpProtocol.errorResponse rawId error)

/// Execute one admitted tool call and render its MCP result.
///
/// A tool that RAISES becomes `isError: true` carrying its sanitised
/// message, not a JSON-RPC error — see `McpProtocol.toolCallResult`. The
/// Phase 709 result budget is applied on the way out for the same reason
/// the agent loop applies it: an MCP result is serialised straight into
/// a model's context, so an unbounded payload floods exactly the same
/// place, and a tool that declared its result bounded meant it whichever
/// caller asked.
let private runTool (ctx: HttpContext) (tool: RegisteredTool) (argumentsJson: string) : Async<string * bool> = async {
    try
        let! raw = tool.Execute ctx argumentsJson

        let bounded, _ =
            ToolResultBudget.apply tool.Definition.Name tool.Definition.ResultBudget raw

        return bounded, false
    with ex ->
        return McpProtocol.sanitise ex.Message, true
}

/// The `tools/call` path, from parsed params to written response.
let private handleToolCall
    (options: McpHostOptions)
    (ctx: HttpContext)
    (agent: AgentPrincipal)
    (access: AccessContext)
    (requestId: Guid)
    (session: string option)
    (rawId: string option)
    (parameters: Text.Json.JsonElement option)
    =
    task {
        match McpProtocol.parseToolCall parameters with
        | Error error ->
            McpAudit.recordRefusal ctx agent access options.Route requestId session None None error
            return! respondError ctx rawId error
        | Ok(toolName, argumentsJson) ->
            let registry = service<AIToolRegistry> ctx

            match registry with
            | None ->
                // No AI tool registry in DI means the companion was
                // mounted on something other than a composed AI server.
                // `McpCompose.run` refuses that composition at startup, so
                // this branch is unreachable in a deployment that started;
                // it exists because a handler that assumed the service was
                // there would fail with a null reference instead of a
                // sentence.
                let error = McpError.ToolNotFound toolName
                McpAudit.recordRefusal ctx agent access options.Route requestId session (Some toolName) None error
                return! respondError ctx rawId error
            | Some registry ->
                let grantGate = moduleGrantGate ctx

                match McpAuthorization.classifyCall registry access grantGate agent toolName with
                | Error error ->
                    let sourceModule =
                        registry.FindByName toolName |> Option.map _.Definition.SourceModule

                    McpAudit.recordRefusal
                        ctx
                        agent
                        access
                        options.Route
                        requestId
                        session
                        (Some toolName)
                        sourceModule
                        error

                    return! respondError ctx rawId error
                | Ok tool ->
                    let policy = McpHostOptions.policyFor agent.AccountId options

                    let! rate =
                        McpGovernance.admitRateLimit ctx policy.RateLimit access.Subject
                        |> Async.StartAsTask

                    match rate with
                    | Error error ->
                        McpAudit.recordRefusal
                            ctx
                            agent
                            access
                            options.Route
                            requestId
                            session
                            (Some tool.Definition.Name)
                            (Some tool.Definition.SourceModule)
                            error

                        return! respondError ctx rawId error
                    | Ok() ->
                        let! admitted = McpGovernance.admitBudget ctx policy.Budget agent |> Async.StartAsTask

                        match admitted with
                        | Error error ->
                            McpAudit.recordRefusal
                                ctx
                                agent
                                access
                                options.Route
                                requestId
                                session
                                (Some tool.Definition.Name)
                                (Some tool.Definition.SourceModule)
                                error

                            return! respondError ctx rawId error
                        | Ok reservation ->
                            McpAudit.recordToolCalled
                                ctx
                                agent
                                requestId
                                session
                                tool.Definition.Name
                                tool.Definition.SourceModule
                            |> Async.Start

                            let stopwatch = Stopwatch.StartNew()

                            try
                                let! text, isError = runTool ctx tool argumentsJson |> Async.StartAsTask
                                stopwatch.Stop()

                                McpAudit.recordToolCompleted
                                    ctx
                                    agent
                                    requestId
                                    session
                                    tool.Definition.Name
                                    tool.Definition.SourceModule
                                    (if isError then "error" else "ok")
                                    stopwatch.Elapsed.TotalMilliseconds
                                |> Async.Start

                                return! respond ctx 200 None (McpProtocol.toolCallResult rawId text isError)
                            finally
                                // Released whatever happened, including a
                                // cancelled request: a reservation that is
                                // not released leaks a concurrency slot
                                // until the period rolls.
                                McpGovernance.releaseBudget reservation |> Async.Start
    }

/// The endpoint handler, parameterised by its compose-time options.
///
/// Every per-request dependency — the grant store, the tool registry,
/// the event store, the budget ledger, the rate-limit store — is
/// resolved from `ctx.RequestServices`, so one handler value serves
/// every request and none of them is captured at composition (GP 12
/// rule 4).
let handler (options: McpHostOptions) : HttpHandler =
    fun (_next: HttpFunc) (ctx: HttpContext) -> task {
        let! body = ctx.ReadBodyFromRequestAsync()
        let session = sessionId ctx
        let requestId = Guid.NewGuid()

        match McpProtocol.parseRequest body with
        | Error error -> return! respondError ctx None error
        | Ok request ->
            let isNotification = request.RawId.IsNone

            match service<IAgentToolGrantStore> ctx with
            | None ->
                // Unreachable in a composed deployment — `run`
                // registers the store — and answered rather than
                // thrown for the reason given in `handleToolCall`.
                return! respondError ctx request.RawId McpError.Unauthorized
            | Some grants ->
                let! resolved = McpAuthorization.resolvePrincipal grants ctx |> Async.StartAsTask

                match resolved with
                | Error error -> return! respondError ctx request.RawId error
                | Ok agent ->
                    // **No fallback `AccessContext`, deliberately.** The
                    // platform reads an EMPTY `ModulePermissions` map as
                    // unrestricted (opt-in RBAC), so a synthesised
                    // context would hand a machine credential everything
                    // at exactly the moment the real one could not be
                    // resolved. Core `compose` registers this service, so
                    // its absence means the companion is mounted on
                    // something that is not a composed server — which
                    // `run` refuses at startup. Fail closed.
                    match service<AccessContext> ctx with
                    | None -> return! respondError ctx request.RawId McpError.Unauthorized
                    | Some access ->

                        match request.Method with
                        | McpProtocol.Method.Initialize ->
                            McpAudit.recordSessionInitialized ctx agent requestId session |> Async.Start

                            return!
                                respond ctx 200 None (McpProtocol.initializeResult request.RawId options.ServerVersion)

                        | McpProtocol.Method.Ping ->
                            return! respond ctx 200 None (McpProtocol.emptyResult request.RawId)

                        | McpProtocol.Method.ToolsList ->
                            match service<AIToolRegistry> ctx with
                            | None -> return! respond ctx 200 None (McpProtocol.toolsListResult request.RawId [])
                            | Some registry ->
                                let visible =
                                    McpAuthorization.visibleTools registry access (moduleGrantGate ctx) agent

                                McpAudit.recordToolsListed ctx agent requestId session visible.Length
                                |> Async.Start

                                return!
                                    respond
                                        ctx
                                        200
                                        None
                                        (McpProtocol.toolsListResult
                                            request.RawId
                                            (visible |> List.map McpAuthorization.toolView))

                        | McpProtocol.Method.ToolsCall ->
                            return!
                                handleToolCall options ctx agent access requestId session request.RawId request.Params

                        | _ when isNotification ->
                            // Every notification — `notifications/initialized`
                            // included — is accepted and answered with no
                            // body, which is what JSON-RPC requires of a
                            // request carrying no id. An UNKNOWN notification
                            // is accepted rather than refused, by the same
                            // rule: there is nowhere to report a refusal to,
                            // and a 4xx on a notification is a response the
                            // client cannot correlate with anything.
                            ctx.SetStatusCode 202
                            return! ctx.WriteBytesAsync [||]

                        | method ->
                            return!
                                respond
                                    ctx
                                    200
                                    None
                                    (McpProtocol.errorResponse
                                        request.RawId
                                        (McpError.Protocol(sprintf "unknown method '%s'" (McpProtocol.sanitise method))))
    }

/// The Giraffe surface a composed MCP host mounts.
///
/// `GET` on the same path answers `405` — see the file header on why
/// this host serves no server-initiated stream.
let routes (options: McpHostOptions) : HttpHandler =
    choose [
        POST >=> route options.Route >=> handler options
        GET
        >=> route options.Route
        >=> (fun _next ctx ->
            ctx.SetStatusCode 405
            ctx.SetHttpHeader("Allow", "POST")
            ctx.SetContentType "application/json"

            ctx.WriteStringAsync "{\"error\":\"This MCP endpoint serves no server-initiated stream; use POST.\"}")
    ]