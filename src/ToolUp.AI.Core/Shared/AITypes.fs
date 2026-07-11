// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AI

open System
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.VectorKnowledgeTypes

// `ToolParameterSchema` and `AIToolDefinition` (the module-facing tool
// declaration types) live in core at `Shared/ModuleAITypes.fs`; this file
// contains only AI-runtime types (conversation, agent protocol,
// branding/config, assistant API, streaming events).

// ─── Conversation types ──────────────────────────────────────────

/// Who sent a message in a conversation
type ParticipantType =
    | User
    | AIAssistant
    | System

/// Status of a tool call within a conversation
type ToolCallStatus =
    | Pending
    | Running
    | Completed
    | Failed of string

/// Record of a tool call made by the AI within a message
type ToolCallRecord = {
    ToolCallId: Guid
    ToolName: string
    /// JSON-encoded arguments
    Arguments: string
    /// JSON-encoded result, populated after execution
    Result: string option
    Status: ToolCallStatus
}

// ─── Numeric-fidelity verification (Phase 523) ───────────────────
//
// The numeric-fidelity answer gate extracts every numeric token from an
// assistant answer, canonicalises it under the metric registry's display
// rules, and verifies it matches a fact in the turn's retrieved set. The
// verdict rides three surfaces: the SSE stream (per-message summary), the
// persisted `ConversationMessage` (history reload), and an audit event per
// unmatched token. Fable-shared because the client renders the verdict as
// inline badges on both the live reply and history reload.

/// Per-token numeric-fidelity verdict (Phase 523.B).
type NumericVerdict =
    /// The token's canonical value matches a fact in the turn's retrieved
    /// set (any registry-legal rendering of that fact).
    | NumberVerified
    /// Facts WERE in scope this turn, but no retrieved fact's canonical
    /// value matches this token — the anti-hallucination flag.
    | NumberUnmatched
    /// No facts were retrieved this turn, so the token cannot be checked
    /// against the fact tier. Not a violation — an honest "unverifiable".
    | NoFactsInScope

/// One numeric token extracted from an assistant answer together with its
/// verification verdict (Phase 523).
type VerifiedNumber = {
    /// The numeric token exactly as it appeared in the answer text
    /// (e.g. `"£21,800"`, `"-134%"`, `"−1.3"`).
    Token: string
    /// The canonical decimal value the token normalised to, rendered as an
    /// invariant-culture string (percent folded to its fraction, currency /
    /// grouping / unicode-minus stripped). `None` when the token could not
    /// be parsed to a number (defensive — extraction only yields numerics).
    Canonical: string option
    Verdict: NumericVerdict
    /// The content-addressed id of the retrieved fact this token matched,
    /// when `NumberVerified`; `None` otherwise.
    MatchedFactId: string option
}

/// Per-message numeric-fidelity verdict summary (Phase 523.D). Rides the
/// SSE stream and the persisted `ConversationMessage`. `Off`-mode turns
/// never produce one (the field stays `None`).
type AnswerVerification = {
    /// Count of tokens with a `NumberVerified` verdict.
    Verified: int
    /// Count of tokens with a `NumberUnmatched` verdict (the flagged set).
    Unmatched: int
    /// Count of tokens that could not be checked because no facts were in
    /// scope this turn (`NoFactsInScope`).
    Unverifiable: int
    /// Every extracted numeric token with its verdict, in answer order.
    Numbers: VerifiedNumber list
    /// The gate mode that produced this verdict (`"Annotate"` / `"Strict"`).
    /// `"Off"` never emits an `AnswerVerification` at all.
    Mode: string
}

/// A single message in a conversation
type ConversationMessage = {
    Id: Guid
    ConversationId: Guid
    Participant: ParticipantType
    Content: string
    Timestamp: DateTime
    ToolCalls: ToolCallRecord list
    /// Knowledge-base chunks the agent retrieved when producing this
    /// message. Always `[]` for User and System messages; populated for
    /// AIAssistant messages when retrieval ran and returned matches.
    /// Surfaced verbatim by `AIAssistantApi.GetConversation` so the UI
    /// can render a Sources panel on history reload as well as on the
    /// initial reply.
    RetrievedSources: RetrievedSource list
    /// Phase 6o multimodal content parts. Empty for plain-text turns
    /// (`Content` carries the full message body). Populated for
    /// multipart turns: providers iterate `Parts` to emit vendor-native
    /// content-block arrays; the conversation-history round-trip
    /// preserves multipart shape so a replayed turn matches what the
    /// model originally received. The export renderer routes through
    /// `AIContentPart.exportPlaceholder` so embedded image bytes never
    /// reach paste-able markdown.
    Parts: AIContentPart list
    /// Phase 6j.D — recorded on the first server-persisted message of a
    /// conversation; the conversation's owner. The beacon handler and
    /// `SubmitMessage` reject appends from a caller whose `UserId` does
    /// not match the first message's `CreatedBy` (rejection is gated to
    /// non-empty values for backwards compatibility — pre-6j.D blobs
    /// without the field deserialise to empty and are not gated).
    /// Client-side construction (local debug notes, optimistic user
    /// echo) sets this to `""` — it is authoritative only when the
    /// server writes it.
    CreatedBy: string
    /// Phase 523 — numeric-fidelity verdict for this assistant message.
    /// `None` for user / system turns, for assistant turns produced with
    /// the gate `Off` (the default), and for conversation history
    /// persisted before this field existed — pre-523 wire payloads without
    /// the field deserialise to `None` (additive + backward-compatible, GP
    /// 11). Populated only when the answer gate ran in `Annotate` / `Strict`
    /// mode; the client renders it as inline verification badges on both the
    /// live reply and on history reload.
    Verification: AnswerVerification option
}

/// Conversation metadata
type Conversation = {
    Id: Guid
    Title: string option
    CreatedAt: DateTime
    UpdatedAt: DateTime
    MessageCount: int
    /// Per-conversation provider override. When `Some label`, the
    /// server prefers this configured-provider instance unless the
    /// request supplies its own one-off override. Persisted server-
    /// side via `SetConversationOverride` so picker selection
    /// survives reloads. `None` falls back to the account-level
    /// active-provider state.
    OverrideProviderLabel: string option
}

// ─── Task types ──────────────────────────────────────────────────

/// Status of an AI task submitted by the user
type AITaskStatus =
    | Queued
    | InProgress
    | AITaskCompleted
    | AITaskFailed of string

/// A task submitted to the AI agent
type AITask = {
    TaskId: Guid
    ConversationId: Guid
    Prompt: string
    Status: AITaskStatus
    CreatedAt: DateTime
    CompletedAt: DateTime option
}

// ─── SSE event types ─────────────────────────────────────────────

/// Server-Sent Events for real-time streaming from server to client
type AIStreamEvent =
    | MessageDelta of conversationId: Guid * content: string
    | ToolCallStarted of conversationId: Guid * toolName: string * toolCallId: Guid
    | ToolCallCompleted of conversationId: Guid * toolCallId: Guid * result: string
    | TaskStatusChanged of taskId: Guid * status: AITaskStatus
    | MessageComplete of conversationId: Guid * messageId: Guid
    | StreamError of message: string
    /// Phase 6h: the user cancelled the in-flight agent loop via
    /// the `/api/ai/cancel/{taskId}` endpoint. The agent loop
    /// exits cleanly at the next turn boundary. The client
    /// already buffers streaming chunks for display; on this
    /// event it commits whatever's in the buffer to chat history
    /// (rather than discarding it) and clears any watchdog.
    | StreamCancelled of conversationId: Guid
    /// Phase 6g.A: ask the client to execute a `ClientResident` tool.
    /// The client looks up the tool by name in its local registry,
    /// runs the body (typically dispatching typed `Msg`s into a
    /// module's MVU), and POSTs the result to `/api/ai/tool-result`.
    /// The agent loop is suspended on a `TaskCompletionSource` keyed
    /// by `toolCallId` until that POST arrives (server-side timeout
    /// 90 s; client should set its own watchdog inside that envelope).
    ///
    /// `activeModule` / `activePage` flow from `AIMessageRequest` so
    /// tools that need to act on "whatever the user is currently
    /// viewing" (e.g. `_platform.ui.inspect_active_module`) don't
    /// have to receive that information through the AI's tool args
    /// — the surface that initiated the chat already knows.
    | ClientToolInvoke of
        taskId: Guid *
        toolCallId: Guid *
        toolName: string *
        argsJson: string *
        activeModule: string option *
        activePage: string option
    /// Phase 523: per-message numeric-fidelity verdict. Emitted once, after
    /// the final assistant text is settled, when the answer gate ran
    /// (`Annotate` / `Strict`). The client renders the verdict as inline
    /// verification badges under the reply. Absent entirely when the gate
    /// is `Off` (the default) — a deployment that never opts in sees the
    /// pre-523 event stream byte-for-byte (GP 11 / GP 13).
    | AnswerVerified of conversationId: Guid * verification: AnswerVerification

// ─── Client-resident tool result ─────────────────────────────────

/// Phase 6g.A: payload of the `/api/ai/tool-result` POST. The browser
/// sends this after running a `ClientResident` tool; the server's
/// `ClientToolDispatchRegistry` matches by `ToolCallId` and completes
/// the suspended `TaskCompletionSource` so the agent loop can resume.
type ClientToolResultRequest = {
    /// AITask the original ClientToolInvoke event belonged to. Used
    /// for audit / logging only — registry keying is by ToolCallId.
    TaskId: Guid
    /// Identifies the specific in-flight tool call. The server's
    /// registry has a `TaskCompletionSource<string>` keyed by this
    /// value; matching POSTs complete it.
    ToolCallId: Guid
    /// The tool's JSON result, identical in shape to a server-resident
    /// tool's return value. For errors, the client sends the same
    /// `{ "error": "...", ... }` shape `ToolInvocationError` produces
    /// server-side, so the agent loop's recovery path is unchanged.
    ResultJson: string
}

// ─── Client-resident tool authorization seam ─────────────────────

/// Outcome of a per-invocation authorization check for a
/// `ClientResident` tool. `Allow` is the seam-absent default (GP 13:
/// zero footprint on deployments that never compose the companion that
/// supplies the authorizer).
type ClientToolAuthDecision =
    | Allow
    | Deny of reason: string

/// Generic, companion-agnostic authorization seam for `ClientResident`
/// tool invocations. The agent loop resolves this from DI and consults
/// it immediately before emitting the `ClientToolInvoke` SSE event;
/// absent ⇒ every client-resident invocation is allowed. The
/// implementation is supplied by the companion that owns the
/// client-resident surface, so the AI core stays ignorant of
/// module/field semantics and the client controllable registry is
/// never mirrored server-side.
///
/// Implementations MUST NOT throw — a malformed `argsJson` is a `Deny`,
/// not an exception — and MUST be cheap: this runs on the hot
/// agent-loop path, so it is deliberately synchronous and
/// value-in/value-out (the same sync justification as the documented
/// `IMetricsSink` exception to the async-at-every-boundary rule).
type IClientToolAuthorizer =
    abstract member Authorize:
        toolName: string * argsJson: string * activeModule: string option * activePage: string option ->
            ClientToolAuthDecision

// ─── AI surface ───────────────────────────────────────────────────

/// Phase 6g.A: which AI surface the user is chatting from. Determines
/// per-turn tool filtering (see `AIToolRegistry.toProviderDef` and the
/// `AISurfaceFilter` declared by each tool in
/// `src/ToolUp.Platform/Shared/ModuleAITypes.fs`).
///
/// `SidePanel` — the lightweight side-panel chat. Mode 1 only; tools
/// declared as `FullPageOnly` are filtered out. Cross-module work
/// happens server-side end-to-end.
///
/// `FullPage` — the full-page AI assistant module. Mode 1 + Mode 2;
/// includes any client-resident UI tools a companion has wired in
/// to drive the active module's UI in front of the user.
type AISurface =
    | SidePanel
    | FullPage

// ─── Request contract ─────────────────────────────────────────────

/// Client → server request when submitting a chat message. A record rather
/// than a tuple so future metadata (selected file, UI location, etc.) can
/// be added without changing the Fable.Remoting contract.
type AIMessageRequest = {
    ConversationId: Guid
    Content: string
    /// The module the user was viewing when they submitted the prompt.
    /// Flows to the system prompt builder so the agent can tailor its
    /// response to that module's domain. `None` for prompts submitted
    /// from a non-module context (e.g. the dashboard home).
    ActiveModule: string option
    /// The sidebar page route within the active module when the user
    /// submitted the prompt. `Some "/price-elasticity"`, etc. `None`
    /// for single-page modules or prompts submitted from a non-module
    /// context. Carried alongside `ActiveModule` so `SystemPromptBuilder`
    /// and `AITools` can distinguish pages of the same multi-page module.
    ActivePage: string option
    /// Snapshot of the structured narrative currently shown on the
    /// active page, when the module exposes one via `ProvidesNarrative`.
    /// The built-in `SystemPromptBuilder.currentNarrativeContext`
    /// renders this into a markdown block inside the system prompt
    /// so the agent can answer "what does this page say?" without a
    /// tool call. Reflects the user's last render of the page — not
    /// a canonical store.
    ActivePageNarrative: ToolUp.Platform.Narrative.NarrativeDocument option
    /// Optional per-request provider override. When `Some label`, the
    /// handler resolves the provider via `factory.TryResolveByLabel`
    /// rather than the user's active-label state — lets the UI picker
    /// run a single message against a specific configured instance
    /// without disturbing the account-level active setting. `None`
    /// uses the normal active-provider resolution.
    OverrideProviderLabel: string option
    /// Phase 6g.A: which AI surface the user submitted from. Drives
    /// per-turn tool filtering — `SidePanel` strips tools declared as
    /// `FullPageOnly`, `FullPage` exposes them.
    /// Defaults to `SidePanel` to keep behaviour conservative for
    /// existing client code that doesn't yet attach the field
    /// (Fable.Remoting will see no field on the wire and fall through
    /// to default-record-construction; explicit construction sites
    /// must set this field).
    ///
    /// TRUST MODEL (Phase 6g.F): this is a **client-supplied** field.
    /// Under the default `ServerConfig.AISurfaceDerivation = TrustClient`
    /// the server does not verify it, so a caller can send `FullPage`
    /// from a side-panel context and unlock `FullPageOnly` tools. The
    /// blast radius is bounded — `FullPageOnly` tools are client-resident
    /// and act on the *calling client's own browser/session*, so there is
    /// no cross-user reach, and tenant isolation + the per-call
    /// `IClientToolAuthorizer` gate on the resolved `AccessContext`, never
    /// on `Surface`. Surface gating is a UX affordance, **not** a security
    /// boundary; do not use it to withhold a capability the caller should
    /// not have at all. Deployments that want defence-in-depth set
    /// `AISurfaceDerivation = DeriveFromCookie` to derive the surface from
    /// a signed server-issued capability cookie instead of this field.
    /// See `src/ToolUp.AI/TECHNICAL_GUIDE.md` §"Surface determination &
    /// trust model".
    Surface: AISurface
}

// ─── API contract (Fable.Remoting) ──────────────────────────────

/// API for conversation/task management (request/response, not streaming).
/// Streaming progress is delivered via SSE, not this API.
type AIAssistantApi = {
    /// Submit a message to a conversation (creates conversation if new).
    /// Anonymous-mode deployments chat with the assistant; per-scope
    /// isolation + the Phase 6j.D ownership gate run in the handler.
    // Phase 69g.tail — LLM inference is the platform's most expensive
    // per-call path (provider tokens + optional RAG retrieval + tool
    // loops). Conservative per-subject burst cap; dormant until an
    // `IRateLimitStore` is composed.
    [<AllowAnonymous>]
    [<RateLimit(30, RateLimitSeconds.perMinute)>]
    SubmitMessage: AIMessageRequest -> Async<AITask>
    /// Get conversation history
    [<AllowAnonymous>]
    GetConversation: Guid -> Async<ConversationMessage list>
    /// List all conversations for current user/scope
    [<AllowAnonymous>]
    ListConversations: unit -> Async<Conversation list>
    /// Get available tools
    [<AllowAnonymous>]
    GetAvailableTools: unit -> Async<AIToolDefinition list>
    /// Get task status
    [<AllowAnonymous>]
    GetTaskStatus: Guid -> Async<AITask option>
    /// Delete a conversation
    [<AllowAnonymous>]
    [<Audit "Custom:ConversationDeleted">]
    DeleteConversation: Guid -> Async<Result<unit, string>>
    /// Persist a per-conversation provider override. `Some label`
    /// records the configured-provider instance the agent loop
    /// should prefer for subsequent messages on this conversation;
    /// `None` clears any stored override (subsequent messages fall
    /// back to per-request override or the account-level active
    /// provider). Idempotent.
    [<AllowAnonymous>]
    SetConversationOverride: Guid * string option -> Async<Result<unit, string>>
}

// ─── Branding ─────────────────────────────────────────────────────

/// Client-visible branding for the AI assistant module and side panel.
/// Shared between client (UI rendering) and server (`AIAssistantServerConfig`
/// nests it as one field). Intentionally does NOT contain `SystemPromptPrefix`
/// or other server-only behaviour — those live in `SystemPromptBuilder` on
/// the server side, where `AccessContext` is available.
type AIAssistantBranding = {
    /// Display name for the AI module page
    Name: string
    /// Icon path
    Icon: string
    /// Whether to show the conversation side panel
    ShowSidePanel: bool
}

// `AIAssistantMode` previously lived here. It moved to
// `Client/AIClientTypes.fs` (still under `namespace ToolUp.AI`) when
// the `ConfiguredAIAssistant` case switched from carrying the shared
// `AIAssistantBranding` (Icon: string) to the new client-only
// `AIAssistantClientBranding` (Icon: ReactElement). `ReactElement` is
// Fable-only so the DU can no longer live in shared types. Server-side
// AI configuration uses `AIAssistantServerConfig.Branding` directly
// (still typed as the shared `AIAssistantBranding`).

// ─── Module AI context ────────────────────────────────────────────

/// A module's domain-expert system-prompt contribution. Each module that
/// wants the AI to understand its specialism declares one at compose time;
/// the SDK combines it into the system prompt only when that module is the
/// user's active view. "Private" in the sense that the user never sees it
/// in chat history — it is metadata sent to the model, not a user message.
///
/// Keep these contributions short and factual (typical inputs/outputs,
/// interpretation pitfalls, key jargon). Long prompts cost tokens on every
/// turn. Dynamic contributions (that depend on the module's current model
/// state) are deferred — the static string covers the majority use case
/// without needing to break type erasure.
type ModuleAIContext = {
    /// Must match the module's `ModuleDefinition.Id` (the stable
    /// permission-key identifier, not the display name) so the
    /// active-module lookup works. The client sends
    /// `AIMessageRequest.ActiveModule = Some moduleId` and the server
    /// keys into `moduleAIContextMap` using that id.
    ModuleName: string
    /// System-prompt text injected when this module is active.
    SystemPrompt: string
}