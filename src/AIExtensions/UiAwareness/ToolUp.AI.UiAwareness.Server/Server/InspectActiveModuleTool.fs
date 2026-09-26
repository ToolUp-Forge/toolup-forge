// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.UiAwareness.Server.InspectActiveModuleTool

// ─── Phase 537 — `_platform.ui.inspect_active_module` ────────────────
//
// The read-only, client-resident tool that answers "what is on my
// screen?" from forge's own substrate: a module declares its live state
// as a `UiStateReport` projection (`ClientModule.withInspectState`,
// Phase 536), the shell records the latest report at its publish points
// (`ModuleStateObserver.tryInspect`), and this tool hands that report to
// the model. No external UI-orchestration layer is involved.
//
// The body runs in the BROWSER. `Location = ClientResident` makes the
// agent loop emit a `ClientToolInvoke` SSE event carrying the request's
// active module and page; the executor registered by
// `ToolUp.AI.UiAwareness.Client.InspectActiveModule.install` reads the
// report and POSTs the result back. The server-side executor below is a
// stub that only a broken dispatch wiring could reach.
//
// What gates it — nothing new, by design:
//   * Per-module RBAC. The tool is sourced from the SDK-reserved
//     `_platform.ui`, so the Phase 36.A filter admits it unless the
//     deployment names `_platform.ui` in its permission map, in which case
//     the caller needs `Read` on it — the same predicate every tool passes
//     at list time and again at dispatch. The report it returns is the
//     calling user's OWN browser state for the module they are looking at,
//     so it reaches nothing the caller cannot already see.
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
    Name = InspectActiveModuleToolName
    Description =
        "Read the live state of the module the user is currently viewing: its current field values "
        + "(filters, inputs, toggles), its current selections, and which actions are enabled. "
        + "Call this whenever the user asks about what is on their screen right now - for example "
        + "which filters they have applied, what they have selected, or what they can do next - "
        + "instead of answering from documentation. Read-only: it changes nothing. Returns "
        + "{ moduleId, activePage, snapshot }, or { status } when there is no active module "
        + "(\"no-active-module\") or the module exposes no observable state (\"no-observable-state\")."
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
            "_platform.ui.inspect_active_module is ClientResident; its server-side executor was called. The agent loop's per-tool dispatch in AIAgentEngine should route it to the browser before reaching here - this is a bug in the dispatch wiring."
}