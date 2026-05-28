// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Module-facing AI tool declarations ──────────────────────────
//
// These types live in the core SDK so modules can declare AI tools without
// having to reference the `ToolUp.AI` companion. The AI runtime
// infrastructure (registry, provider interface, agent loop, conversation
// persistence, assistant API) all lives in `ToolUp.AI`; only the
// *declaration shape* is shared here.

/// JSON Schema representation for tool parameter descriptions.
/// Kept simple and Fable-compatible (no `System.Text.Json` dependency).
type ToolParameterSchema = {
    Name: string
    /// "string" | "number" | "boolean" | "object" | "array"
    Type: string
    Description: string
    Required: bool
    /// JSON-encoded default value
    Default: string option
}

/// Where a tool executes. `ServerResident` is the existing default —
/// the executor takes `HttpContext + argsJson` and runs entirely on
/// the server. `ClientResident` flips the dispatch direction: the
/// agent loop emits a `ClientToolInvoke` SSE event, the browser runs
/// the F# tool body (typically dispatching typed `Msg`s into a
/// module's MVU), and POSTs the result back to `/api/ai/tool-result`
/// where the suspended agent loop resumes. ClientResident tools
/// require the client-resident runtime to be present.
type ToolLocation =
    | ServerResident
    | ClientResident

/// Per-tool surface gate. Filters which tools are visible to the model
/// per-turn based on which AI surface the user is chatting from.
/// `Both` (default) — tool is always offered, regardless of surface.
/// `SidePanelOnly` — Mode 1 only; e.g. a server-side analytical tool
/// that doesn't make sense in the "watch me work" full-page surface.
/// `FullPageOnly` — Mode 2 only; e.g. UI-mutation tools that need the
/// active full-page module to drive. The agent loop reads
/// `AIMessageRequest.Surface` and `AIToolRegistry.toProviderDef`
/// applies the filter when constructing the per-turn tool list.
type AISurfaceFilter =
    | Both
    | SidePanelOnly
    | FullPageOnly

/// Declares one client-side action a tool may emit alongside its JSON
/// text result. Used by the two-tier pattern: the tool runs
/// server-side (agent loop sees the JSON), AND it publishes a
/// `Notification.ModuleAction` that the client routes into the named
/// module's state via its `ActionDecoder`. Tools that only return JSON
/// leave `EmitsActions = None`.
///
/// The declaration is an inspection surface — for documentation, for
/// the data catalog, and so modules can reason about which
/// action keys they should decode. It is not an enforcement contract;
/// an executor that emits an undeclared action is a bug, not a
/// permission violation.
type ActionDeclaration = {
    /// Target module. Matches the `ModuleDefinition.Id` the client
    /// routes to; also the RBAC key used to gate delivery.
    ModuleId: string
    /// Module-owned discriminator passed to the module's `ActionDecoder`.
    /// E.g. "apply-optimised-budget".
    ActionKey: string
    /// Human-readable description of what the action does. Surfaces in
    /// admin tooling; not shown to the AI model.
    Description: string
    /// Optional JSON-Schema string describing the payload shape. The
    /// decoder on the client is hand-written today; the schema is
    /// advisory. Future: codegen decoders from the schema.
    PayloadSchema: string option
}

/// A tool that an AI agent can invoke. Registered by modules.
/// Contains metadata only — the Execute function is server-only and lives
/// in `ToolUp.AI.RegisteredTool` alongside the rest of the AI runtime.
type AIToolDefinition = {
    /// Unique tool name, e.g. "media_optimisation.run"
    Name: string
    /// Human-readable description for the AI model
    Description: string
    /// Parameter schema for the AI model
    Parameters: ToolParameterSchema list
    /// Which module provides this tool
    SourceModule: string
    /// Client-side actions this tool may publish in addition
    /// to returning its JSON text result. `None` — tool is chat-only
    /// (the default, existing behaviour). `Some list` — enumerates the
    /// module+actionKey pairs the tool can target; each requires the
    /// named module to register a matching `ActionDecoder` to be
    /// consumed (undeclared decoders drop silently).
    EmitsActions: ActionDeclaration list option
    /// Where the tool's body runs. `ServerResident` — the
    /// executor takes `HttpContext + argsJson` and runs entirely on
    /// the server (existing behaviour, the default for tools that
    /// predate client-resident execution). `ClientResident` — the
    /// agent loop dispatches the call back to the browser via SSE,
    /// the client runs the body
    /// (typically dispatching typed `Msg`s into a module's MVU) and
    /// POSTs the result to `/api/ai/tool-result`.
    Location: ToolLocation
    /// Per-tool surface gate. `Both` (default) — tool is
    /// always offered to the model. `SidePanelOnly` / `FullPageOnly`
    /// — tool is filtered out of the per-turn tool list when the
    /// chat request comes from the other surface. The platform-built-in
    /// `_platform.ui.set_field` / `_platform.ui.click_button` tools
    /// declare `FullPageOnly` because they need a full-page module to
    /// drive; `_platform.ui.inspect_active_module` declares `Both`
    /// because read-only awareness is useful in either surface.
    Surface: AISurfaceFilter
}