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

/// Phase 709 — per-tool **context budget** for the JSON result a tool
/// returns to the agent loop. A tool result is serialised straight into
/// model context, so a tool whose contract is "return the matching
/// records" floods the conversation at high cardinality: 10⁵ records
/// JSON-encoded into the prompt, every turn, until the provider refuses
/// the request. The budget is a context-hygiene ceiling on ONE tool
/// result, and it is deliberately NOT a member of the resource-
/// *exhaustion* budget family — compute (`ComputeBudget`), AI tokens,
/// monetary cost — that Phase 689 sets out to unify behind one seam.
///
/// Keeping the two shapes apart is a decision, not an oversight, and it
/// is worth stating before that seam lands so nobody folds this in on
/// the strength of the shared word "budget". An exhaustion budget meters
/// a consumable somebody is billed for: it accrues over a declared
/// period, is scoped to a submitter class, needs a store to remember
/// what has been spent, and its answer is allow / warn / REFUSE. This
/// budget meters one payload against one prompt: it holds no state,
/// spans no period, bills nobody, and never refuses — it substitutes a
/// steer and lets the turn continue. A single seam covering both would
/// have to make period, store and submitter class optional, at which
/// point it has stopped saying anything.
///
/// The unit is **characters of the returned JSON**, not tokens and not
/// UTF-8 bytes. Forge owns no tokenizer, and characters are the currency
/// every sibling bound in this area already speaks (RAG's
/// `SnippetCharLimit`, the fact-clause rendering) — a second unit here
/// would make two budgets incomparable for no gain in fidelity.
///
/// `DefaultResultBudget` is what every pre-709 tool carries, and it
/// resolves to a deliberately generous SDK ceiling
/// (`AIToolRegistry.DefaultToolResultBudgetChars`) that no well-behaved
/// tool result approaches — an existing deployment is byte-for-byte
/// unchanged (GP 11).
type AIToolResultBudget =
    /// Use the SDK-wide default ceiling. The value every tool declared
    /// before Phase 709 carries.
    | DefaultResultBudget
    /// This tool's own ceiling, in characters of the returned JSON.
    /// Must be positive; `AIToolRegistry.createTool` refuses a
    /// non-positive declaration at compose time rather than silently
    /// reading it as "unbounded".
    | ResultBudgetChars of int
    /// This tool's contract is legitimately large — never elide its
    /// result. The escape hatch for an export / bulk-transfer tool whose
    /// whole point is the payload.
    | NoResultBudget

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
    /// Declares that this tool reads or drives the **live interface** —
    /// the browser-resident module state the user is currently looking
    /// at — so a question about on-screen state is answerable without
    /// consulting the knowledge base. `false` (the default every
    /// pre-Phase-538 tool carries) leaves behaviour byte-for-byte
    /// unchanged (GP 11).
    ///
    /// Read by `RAGPromptBuilder.ToolFraming.fromTools` to decide
    /// whether a RAG deployment's knowledge-base-first framing needs the
    /// live-interface companion. `Location = ClientResident` implies
    /// live-interface by construction (the body runs in the browser
    /// against live UI state) and is honoured independently, so a
    /// client-resident tool need not set this flag; it exists for the
    /// cases `Location` cannot express — a **server-resident** tool that
    /// nonetheless projects live interface state, or a host-adapter tool
    /// whose live-interface intent must be declared rather than inferred
    /// from its name.
    ///
    /// This replaces the pre-538 `_platform.ui.*` name-prefix trigger:
    /// forge never emits that name itself, so keying framing off it
    /// coupled the SDK to a naming convention owned elsewhere — a
    /// differently-named live-interface tool silently missed the
    /// framing, and a server-resident tool that merely happened to start
    /// with `_platform.ui.` wrongly tripped it.
    IsLiveInterface: bool
    /// Phase 709 — the per-tool context budget applied to this tool's
    /// JSON result at agent-loop dispatch. `DefaultResultBudget` (the
    /// value every pre-709 tool carries) resolves to the generous
    /// SDK-wide ceiling and changes no existing behaviour (GP 11).
    ///
    /// Declared here rather than at the registration seam because the
    /// module that authors the tool is the party that knows whether its
    /// result is bounded by construction — a coverage listing emitting
    /// one row per (metric × hierarchy) knows its own cardinality
    /// exposure; the composition root that calls
    /// `AIToolRegistry.createTool` does not.
    ResultBudget: AIToolResultBudget
}