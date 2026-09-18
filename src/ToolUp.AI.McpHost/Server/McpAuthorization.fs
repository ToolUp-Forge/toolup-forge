// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.McpHost.McpAuthorization

open System
open Microsoft.AspNetCore.Http
open ToolUp.Platform
open ToolUp.AI.AIToolRegistry

// ─── Phase 489 — who may see what, answered ONCE ─────────────────────
//
// The phase's acceptance turns on a single property: an ungranted tool
// is invisible AND uninvokable. Two filters that must agree is how that
// property is lost, and this repo has already learned it once — the AI
// tool registry's own header records Phase 36.A putting `list` and
// `dispatch` and the completion gate and `GetAvailableTools` behind ONE
// predicate precisely "so the four surfaces cannot drift apart".
//
// So `visibleTools` below is the whole authorisation decision, and both
// `tools/list` and `tools/call` are expressed in terms of it: listing
// renders it, calling looks a name up IN it. There is no second
// predicate for a later change to get wrong.
//
// The decision is a conjunction of three independent gates, each owned
// elsewhere and none of them re-implemented here:
//
//   1. **Per-module RBAC (Phase 36.A)** — the caller holds `Read` on the
//      tool's `SourceModule`. Applied by `AIToolRegistry.ListAccessible`
//      over the resolved `AccessContext`, which for a service-account
//      request is the account's declared permission map (the Phase 527
//      middleware stamps it and the `AccessContext` DI factory prefers
//      it over the team-derived one).
//   2. **Grant / consent liveness (Phase 730)** — the module grant is
//      accepted and its counterparty consent has not been revoked.
//      Applied by the same call's `isModuleGrantLive` argument, built
//      with the shipped `moduleGrantGate`.
//   3. **The agent's own tool grants (this phase)** — default-deny, by
//      authored tool name.
//
// Gate 3 is applied LAST and is deliberately not folded into the other
// two: it is the only one an agent principal has that a human caller
// does not, and keeping it separate is what lets the audit trail say
// which of "you may not reach that module" and "that tool is not in your
// grant set" happened.

/// `HttpContext.Items` key the Phase 527 middleware stamps the validated
/// machine principal on. Read rather than re-validated: the middleware
/// has already re-read the account and the token from storage on THIS
/// request, which is what makes revocation bite on the next one.
let private principalItemsKey =
    ServiceAccountTokenHandler.ServiceAccountPrincipalItemsKey

/// The validated service-account principal on this request, if any.
///
/// `None` for every request that did not present a `tusa_` bearer token,
/// including one presenting an ordinary user session: an MCP endpoint is
/// for agents, and a human session reaching it is `Unauthorized` rather
/// than silently granted the human's own authority. That is not a
/// nicety — a browser session arriving at `/mcp` with cookies attached
/// is a CSRF shape, and admitting it would let a page drive the tool
/// surface as the logged-in user.
let servicePrincipal (ctx: HttpContext) : ServiceAccountPrincipal option =
    match ctx.Items.TryGetValue principalItemsKey with
    | true, (:? ServiceAccountPrincipal as principal) -> Some principal
    | _ -> None

/// Resolve the agent principal for this request: the validated machine
/// credential, plus its stored tool grants.
///
/// A grant-store failure is `Unauthorized`, not an empty grant set. The
/// two are behaviourally identical for this request — neither admits
/// anything — but they are different facts, and reporting a storage
/// outage as a successful empty listing would tell an operator their
/// agent's grants had been revoked when they had not.
let resolvePrincipal (grants: IAgentToolGrantStore) (ctx: HttpContext) : Async<Result<AgentPrincipal, McpError>> = async {
    match servicePrincipal ctx with
    | None -> return Error McpError.Unauthorized
    | Some principal ->
        match! grants.Read(principal.ScopeId, principal.AccountId) with
        | Error _ -> return Error McpError.Unauthorized
        | Ok grantSet ->
            return
                Ok {
                    AccountId = principal.AccountId
                    DisplayName = principal.DisplayName
                    ScopeId = principal.ScopeId
                    TokenId = principal.TokenId
                    ExpiresAt = principal.ExpiresAt
                    GrantedTools = grantSet.GrantedTools
                }
}

/// **The** authorisation decision: the registered tools this agent may
/// both see and invoke, in registry order.
///
/// Pure over its arguments (the `HttpContext`-shaped gates are passed in
/// already applied), so every branch is exercisable without a server.
/// Phase 793 — the gate's verdict on a tool for an EXTERNAL principal:
/// the same `ToolGate.decide` the list and the agent loop run, under the
/// policy's `ExternalPrincipalCeiling` rather than its human ceiling. A
/// bounded policy admits an agent to no class it did not name — Phase
/// 489's default-deny, expressed by class rather than only by name.
let externalVerdict
    (registry: AIToolRegistry)
    (access: AccessContext)
    (isModuleGrantLive: string -> bool)
    (tool: RegisteredTool)
    : ToolGateVerdict =
    ToolGate.decide access isModuleGrantLive (ToolPolicy.forExternalPrincipal registry.Policy) tool.Definition

let visibleTools
    (registry: AIToolRegistry)
    (access: AccessContext)
    (isModuleGrantLive: string -> bool)
    (agent: AgentPrincipal)
    : RegisteredTool list =
    registry.ListAccessible(access, isModuleGrantLive)
    |> List.filter (fun tool -> agent.GrantedTools |> Set.contains tool.Definition.Name)
    |> List.filter (fun tool -> ToolGate.admits (externalVerdict registry access isModuleGrantLive tool))

/// Classify a `tools/call` against the same decision.
///
/// Returns the tool when the agent may invoke it, and otherwise the
/// typed refusal naming WHICH gate refused — a distinction the audit
/// trail keeps and the wire deliberately does not (see
/// `McpProtocol.ErrorCode.toolUnavailable`).
///
/// The order of the checks is what makes the refusal informative: a name
/// no tool in the deployment carries is `ToolNotFound`, a name the agent
/// was not granted is `ToolDenied` naming the grant set, and a name it
/// WAS granted but cannot reach is `ToolDenied` naming the module — so
/// an operator reading the trail can tell "I forgot to grant this" from
/// "the grant is there and the RBAC is not".
let rec classifyCall
    (registry: AIToolRegistry)
    (access: AccessContext)
    (isModuleGrantLive: string -> bool)
    (agent: AgentPrincipal)
    (toolName: string)
    : Result<RegisteredTool, McpError> =
    match registry.FindByName toolName with
    | None -> Error(McpError.ToolNotFound toolName)
    | Some tool ->
        // Match on the AUTHORED name: `FindByName` also matches the
        // provider-sanitised alias, and a grant set written against the
        // authored name must not be defeated by a client that happened to
        // use the alias — nor satisfied by one, which is the direction
        // that would matter.
        if not (agent.GrantedTools |> Set.contains tool.Definition.Name) then
            Error(
                McpError.ToolDenied(
                    tool.Definition.Name,
                    sprintf "agent '%s' has no grant for this tool" agent.AccountId
                )
            )
        else
            match externalVerdict registry access isModuleGrantLive tool with
            | ToolRefusedUndeclared
            | ToolRefusedEffects _ as refused ->
                Error(
                    McpError.ToolDenied(
                        tool.Definition.Name,
                        sprintf
                            "agent '%s' is granted this tool by name, but not by effect class: %s"
                            agent.AccountId
                            (ToolGate.describe tool.Definition.Name refused)
                    )
                )
            | ToolAdmitted
            | ToolRefusedSource _
            | ToolRefusedGrant _ -> classifyReach registry access isModuleGrantLive agent tool

/// The Phase 489 reach check, unchanged: a name the agent was granted but
/// cannot reach under the platform's own RBAC and grant filters.
and private classifyReach
    (registry: AIToolRegistry)
    (access: AccessContext)
    (isModuleGrantLive: string -> bool)
    (agent: AgentPrincipal)
    (tool: RegisteredTool)
    : Result<RegisteredTool, McpError> =
    if
        not (
            visibleTools registry access isModuleGrantLive agent
            |> List.exists (fun t -> t.Definition.Name = tool.Definition.Name)
        )
    then
        Error(
            McpError.ToolDenied(
                tool.Definition.Name,
                sprintf
                    "agent '%s' is granted this tool but holds no live Read on its source module '%s'"
                    agent.AccountId
                    tool.Definition.SourceModule
            )
        )
    else
        Ok tool

/// The MCP view of one registered tool.
///
/// The authored `Definition.Name` is the wire name — see
/// `McpProtocol.McpToolView`. `ProviderDef.InputSchema` is the JSON
/// Schema `AIToolRegistry.toProviderDef` already built from the tool's
/// declared parameters, reused rather than re-derived so the schema an
/// agent reads and the schema a model reads cannot diverge.
let toolView (tool: RegisteredTool) : McpProtocol.McpToolView = {
    Name = tool.Definition.Name
    Description = tool.Definition.Description
    InputSchemaJson = tool.ProviderDef.InputSchema
}