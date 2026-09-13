// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.McpAudit

open System
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.AI

// ─── Phase 489 — the MCP interaction trail (GP 6) ────────────────────
//
// Every list, call, result and denial is recorded under the agent's
// identity. Two streams, because they answer different questions and
// have different owners:
//
//   * **`_platform.ai.mcp`** — this phase's own trail. Typed events
//     carrying the full agent identity (account, display name, token),
//     the tool, its source module, the outcome and the timing. This is
//     the record 489.D asks for, and it is the one an auditor reads to
//     answer "what did this agent do".
//
//   * **`_platform.ai.tool_allowlist_denial`** — Phase 47's rollup
//     stream, which `/dev/ai-allowlist` and the `HealthMonitorUI` admin
//     panel read. A denial ALSO lands there, payload-compatible with
//     what `AIAgentEngine.writeToolDenialAudit` writes, so an MCP-driven
//     denial campaign shows up on the same 60-minute slice an in-app one
//     does. The phase's requirement is precisely that MCP denials
//     "surface through the Phase 47 allowlist view", and the way to
//     satisfy it is to write that view's shape rather than to widen it:
//     the payload record is owned by that phase, and a companion adding
//     fields to it would be a cross-phase break for a view it does not
//     own.
//
// A third write goes through `AIToolRegistry.recordUnauthorizedToolDenial`
// — the shipped Phase 36.A / Phase 120 path onto the uniform
// `/dev/auth-denials` rollup. It is CALLED, never re-implemented, so an
// MCP refusal joins the same trail as every other authorization denial
// on the HTTP surface.
//
// **Best-effort, never blocking, never throwing** — the same contract
// `IAuditLog.Record` and the agent loop's own denial writer hold. The
// control is the refusal; the row is the record of it. A wedged event
// store must not turn an authorisation decision into a 500.
//
// **PII envelope.** The agent identity (an operator-chosen account id
// and display name, and a token id), the tool name, its source module,
// the outcome and the timing. Never the tool's ARGUMENTS and never its
// result body: both can carry anything the calling model chose to put in
// them, which is exactly the reasoning Phase 36.A records for the
// unauthorized-tool row.

[<Literal>]
let SessionInitializedEvent = "McpSessionInitialized"

[<Literal>]
let ToolsListedEvent = "McpToolsListed"

[<Literal>]
let ToolCalledEvent = "McpToolCalled"

[<Literal>]
let ToolCompletedEvent = "McpToolCompleted"

[<Literal>]
let DeniedEvent = "McpDenied"

let private jsonOptions = FableConverters.create ()

/// Identity fields every MCP audit payload carries. A record rather than
/// four repeated fields so a reader grepping the stream finds the same
/// shape on every row regardless of which event it is.
type McpAgentIdentity = {
    AccountId: string
    DisplayName: string
    TokenId: string
    ScopeId: string
}

[<RequireQualifiedAccess>]
module McpAgentIdentity =
    let ofPrincipal (agent: AgentPrincipal) : McpAgentIdentity = {
        AccountId = agent.AccountId
        DisplayName = agent.DisplayName
        TokenId = agent.TokenId
        ScopeId = agent.ScopeId
    }

/// The payload every `_platform.ai.mcp` row carries. One shape for all
/// five event types, with the per-event fields optional, so a query over
/// the stream does not have to branch on `EventType` before it can read
/// the identity or the outcome.
type McpAuditPayload = {
    Agent: McpAgentIdentity
    /// Correlates every row of one MCP request — the call and its
    /// completion, or the call and its denial.
    RequestId: Guid
    /// The MCP session this request arrived on, when the client supplied
    /// one. `None` for a client that does not use session ids.
    SessionId: string option
    /// The tool this row is about. `None` for `McpToolsListed` and
    /// `McpSessionInitialized`.
    ToolName: string option
    /// The tool's declaring module. `None` for the same two.
    SourceModule: string option
    /// Number of tools returned by `tools/list`. `None` for every other
    /// event.
    ToolCount: int option
    /// `McpProtocol.errorCaseName` for a denial, `"ok"` / `"error"` for a
    /// completion. `None` where nothing was decided.
    Outcome: string option
    /// Operator-facing account of a refusal. Sanitised and truncated
    /// before it reaches here.
    Reason: string option
    /// Wall-clock of a tool call, milliseconds. `None` for every event
    /// that did not run one.
    DurationMs: float option
}

[<RequireQualifiedAccess>]
module McpAuditPayload =
    /// The payload with only the fields every row carries. Every other
    /// constructor below starts from this, so adding a field to the
    /// record cannot leave one event type silently unpopulated.
    let create (agent: AgentPrincipal) (requestId: Guid) (sessionId: string option) : McpAuditPayload = {
        Agent = McpAgentIdentity.ofPrincipal agent
        RequestId = requestId
        SessionId = sessionId
        ToolName = None
        SourceModule = None
        ToolCount = None
        Outcome = None
        Reason = None
        DurationMs = None
    }

/// Mirror of the payload `AIAgentEngine.writeToolDenialAudit` serialises
/// onto a Phase 47 denial row, field for field.
///
/// Kept in step with that writer deliberately: `AIAllowlistDiagnosticsHandler`
/// deserialises the stream into its own copy of this shape, and a row
/// whose fields do not match is a row the rollup drops. `TaskId` and
/// `ConversationId` are real values here rather than placeholders — the
/// MCP request id and the MCP session — so the rollup's correlation
/// columns mean the same thing they mean for an in-app denial.
type private AllowlistDenialPayload = {
    ToolName: string
    Reason: string
    ActiveModule: string option
    ActivePage: string option
    TaskId: Guid
    ConversationId: Guid
}

/// The event store on this request, if one is composed. A deployment
/// with no event store records nothing and pays nothing (GP 13).
let private eventStore (ctx: HttpContext) : IEventStore option =
    match ctx.RequestServices.GetService typeof<IEventStore> with
    | :? IEventStore as store -> Some store
    | _ -> None

let private write (ctx: HttpContext) (scopeId: string) (sourceModule: string) (eventType: string) (payload: 'T) = async {
    match eventStore ctx with
    | None -> return ()
    | Some store ->
        try
            let evt: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scopeId
                SourceModule = sourceModule
                EventType = eventType
                Payload = JsonSerializer.Serialize(payload, jsonOptions)
            }

            do! store.Write evt
        with _ ->
            // Deliberately silent at this level: the caller starts every
            // audit write with `Async.Start`, so there is nobody to
            // report to, and a throw here would surface as an unobserved
            // task exception rather than anything an operator reads. The
            // store's own implementation logs its failures.
            return ()
}

/// Record a completed `initialize` handshake.
let recordSessionInitialized (ctx: HttpContext) (agent: AgentPrincipal) (requestId: Guid) (sessionId: string option) =
    write
        ctx
        agent.ScopeId
        McpHostConstants.AuditSourceModule
        SessionInitializedEvent
        (McpAuditPayload.create agent requestId sessionId)

/// Record a `tools/list`, with the number of tools the agent was shown.
///
/// The COUNT rather than the names: a listing row exists so an operator
/// can see that discovery happened and how wide it was, and the names
/// are already recoverable from the grant record plus the registry.
let recordToolsListed
    (ctx: HttpContext)
    (agent: AgentPrincipal)
    (requestId: Guid)
    (sessionId: string option)
    (toolCount: int)
    =
    write ctx agent.ScopeId McpHostConstants.AuditSourceModule ToolsListedEvent {
        McpAuditPayload.create agent requestId sessionId with
            ToolCount = Some toolCount
    }

/// Record an admitted `tools/call`, before the tool runs.
///
/// Written BEFORE rather than only after, so a tool that hangs or takes
/// the process down still leaves evidence that it was entered. The
/// completion row below is the other half.
let recordToolCalled
    (ctx: HttpContext)
    (agent: AgentPrincipal)
    (requestId: Guid)
    (sessionId: string option)
    (toolName: string)
    (sourceModule: string)
    =
    write ctx agent.ScopeId McpHostConstants.AuditSourceModule ToolCalledEvent {
        McpAuditPayload.create agent requestId sessionId with
            ToolName = Some toolName
            SourceModule = Some sourceModule
    }

/// Record the outcome of a `tools/call` that ran.
let recordToolCompleted
    (ctx: HttpContext)
    (agent: AgentPrincipal)
    (requestId: Guid)
    (sessionId: string option)
    (toolName: string)
    (sourceModule: string)
    (outcome: string)
    (durationMs: float)
    =
    write ctx agent.ScopeId McpHostConstants.AuditSourceModule ToolCompletedEvent {
        McpAuditPayload.create agent requestId sessionId with
            ToolName = Some toolName
            SourceModule = Some sourceModule
            Outcome = Some outcome
            DurationMs = Some durationMs
    }

/// Record a refusal on this phase's own stream.
let recordDenial
    (ctx: HttpContext)
    (agent: AgentPrincipal)
    (requestId: Guid)
    (sessionId: string option)
    (toolName: string option)
    (error: McpError)
    =
    write ctx agent.ScopeId McpHostConstants.AuditSourceModule DeniedEvent {
        McpAuditPayload.create agent requestId sessionId with
            ToolName = toolName
            Outcome = Some(McpProtocol.errorCaseName error)
            Reason = Some(McpProtocol.sanitise (McpProtocol.errorMessage error))
    }

/// Record the same refusal on Phase 47's allowlist stream, so
/// `/dev/ai-allowlist` and the admin panel see MCP denials alongside
/// in-app ones.
///
/// The agent's account id rides `Reason`, which is what that view
/// renders. It is the only place in the Phase 47 payload an identity can
/// go without widening a record this phase does not own — and the
/// reason string is sanitised by the rollup on the way out, so an
/// operator sees it safely whatever the tool name contained.
let recordAllowlistDenial
    (ctx: HttpContext)
    (agent: AgentPrincipal)
    (requestId: Guid)
    (sessionId: string option)
    (toolName: string)
    (error: McpError)
    =
    let sessionGuid =
        match sessionId with
        | Some raw ->
            match Guid.TryParse raw with
            | true, parsed -> parsed
            | _ -> Guid.Empty
        | None -> Guid.Empty

    let payload: AllowlistDenialPayload = {
        ToolName = toolName
        Reason =
            sprintf
                "MCP agent '%s' (%s): %s"
                agent.AccountId
                (McpProtocol.errorCaseName error)
                (McpProtocol.errorMessage error)
        ActiveModule = None
        ActivePage = None
        TaskId = requestId
        ConversationId = sessionGuid
    }

    write
        ctx
        agent.ScopeId
        AIAllowlistDiagnosticsHandler.SourceModule
        AIAllowlistDiagnosticsHandler.DenialEventType
        payload

/// Fire every audit write for one refusal: this phase's row, Phase 47's
/// row, and — for a tool-scoped refusal — the shipped Phase 36.A /
/// Phase 120 `IAuthAuditHook` row that joins the uniform
/// `/dev/auth-denials` rollup.
///
/// All three are started and none is awaited: the refusal has already
/// been decided and returned by the time this runs.
let recordRefusal
    (ctx: HttpContext)
    (agent: AgentPrincipal)
    (access: AccessContext)
    (route: string)
    (requestId: Guid)
    (sessionId: string option)
    (toolName: string option)
    (sourceModule: string option)
    (error: McpError)
    : unit =
    try
        recordDenial ctx agent requestId sessionId toolName error |> Async.Start

        match toolName with
        | Some name ->
            recordAllowlistDenial ctx agent requestId sessionId name error |> Async.Start

            ToolUp.AI.AIToolRegistry.recordUnauthorizedToolDenial
                ctx
                access
                route
                name
                (sourceModule |> Option.defaultValue McpHostConstants.AuditSourceModule)
        | None -> ()
    with _ ->
        ()