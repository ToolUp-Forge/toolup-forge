// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.UiAwareness.Server.ListActiveActionsTool

// ─── Phase 542 — `_platform.ui.list_active_actions` ───────────────────
//
// A narrower sibling of `_platform.ui.inspect_active_module` (Phase 537):
// the same read-only, client-resident answer to "what is on my screen?",
// projected down to just the active module's action states — the subset a
// model needs when the question is "what can I do next?" rather than the
// full field/selection/action snapshot. It reads the same `UiStateReport`
// (Phase 536, `ClientModule.withInspectState` / `ModuleStateObserver.tryInspect`)
// through the same forge substrate; no new server-side state is introduced.
//
// The body runs in the BROWSER, exactly as Phase 537's does. `Location =
// ClientResident` makes the agent loop emit a `ClientToolInvoke` SSE event
// carrying the request's active module; the executor registered by
// `ToolUp.AI.UiAwareness.Client.ListActiveActions.install` reads the report
// and POSTs the result back. The server-side executor below is a stub that
// only a broken dispatch wiring could reach.
//
// What gates it — the same as Phase 537, by design (same source, same
// effect ceiling, same authorizer seam):
//   * Per-module RBAC. Sourced from the SDK-reserved `_platform.ui`, so the
//     Phase 36.A filter admits it unless the deployment names `_platform.ui`
//     in its permission map, in which case the caller needs `Read` on it.
//     The report it returns is the calling user's OWN browser state for the
//     module they are looking at, so it reaches nothing the caller cannot
//     already see.
//   * `IClientToolAuthorizer` (Phase 46). The agent loop consults the
//     composed authorizer for every client-resident call before the SSE is
//     emitted; with none composed the answer is `Allow`.
//   * The Phase 793 effect ceiling: the tool declares `ReadFacts` only, so
//     a deployment bounding its tools to the read-only classes keeps it.

open ToolUp.Platform
open ToolUp.AI.UiAwareness

/// The tool definition the agent loop sees. No parameters: the executor
/// reads the active module from the request context, never from the
/// model's arguments, so the model cannot ask about a module the user is
/// not looking at.
let toolDefinition: AIToolDefinition = {
    Name = ListActiveActionsToolName
    Description =
        "List which actions are currently enabled or disabled on the module the user is viewing "
        + "right now - for example which buttons are clickable and which are greyed out. Call this "
        + "when the user asks what they can do next, or why an action is unavailable, instead of "
        + "answering from documentation. Read-only: it changes nothing. Returns { moduleId, actions: "
        + "[{ id, enabled }] }, or { status } when there is no active module (\"no-active-module\") "
        + "or the module exposes no observable state (\"no-observable-state\")."
    Parameters = []
    SourceModule = UiAwarenessSourceModule
    EmitsActions = None
    Location = ClientResident
    Surface = Both
    IsLiveInterface = true
    ResultBudget = DefaultResultBudget
    Effects = ToolEffectDeclaration.readFacts
}

/// Server-side executor. Never runs in a correctly-wired deployment: the
/// agent loop routes a `ClientResident` tool to the browser before it
/// would reach here, so arriving here is a dispatch-wiring bug and fails
/// loudly rather than answering as if no module were active.
let serverExecutor (_ctx: Microsoft.AspNetCore.Http.HttpContext) (_argsJson: string) : Async<string> = async {
    return
        failwith
            "_platform.ui.list_active_actions is ClientResident; its server-side executor was called. The agent loop's per-tool dispatch in AIAgentEngine should route it to the browser before reaching here - this is a bug in the dispatch wiring."
}