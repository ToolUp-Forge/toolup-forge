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
    /// Phase 6j.E — the idempotency key of the fast-path beacon that
    /// appended this message, stamped on BOTH synthetic turns of the
    /// pair. The beacon handler scans the tail of the conversation for
    /// this value and no-ops (202) on a repeat, so a page reload
    /// mid-flight, a network blip, or any client retry cannot append the
    /// same synthetic `User + AIAssistant` pair twice. `""` everywhere
    /// else — ordinary agent-loop turns, client-side construction, and
    /// blobs persisted before this field existed (a missing field
    /// deserialises to `null` under `FableConverters` and is read
    /// through `String.IsNullOrEmpty`, so legacy history is untouched;
    /// additive + backward-compatible, GP 11).
    BeaconId: string
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
    /// Phase 36.D: ask the user to consent to a cross-module read before
    /// it happens. Emitted by a `_platform.ai.*` tool that is about to
    /// read from `targetModule` in a conversation where the user has not
    /// already allowed it; the tool is suspended on a
    /// `TaskCompletionSource` keyed by `consentId` until the browser
    /// POSTs an `AIConsentDecisionRequest` to `/api/ai/consent`, or the
    /// shared suspended-dispatch timeout fires.
    ///
    /// The payload is FSharp-primitive-only by construction (rule 1 —
    /// identity by value): every field is a `Guid` or a `string`, so no
    /// server type and no live handle crosses the wire, and a non-.NET
    /// client can render the prompt from the JSON alone.
    ///
    /// `intendedQueryKey` is the tool-specific discriminator of what is
    /// about to be read (a query key, a result type, an entity type) and
    /// is `""` when the tool has none. `redactedPayloadPreview` is a
    /// short, length-capped rendering of the model's own arguments —
    /// enough for the user to judge the request, never the module's data,
    /// which has not been read yet.
    | AIConsentRequired of
        taskId: Guid *
        consentId: Guid *
        conversationId: Guid *
        toolName: string *
        targetModule: string *
        intendedQueryKey: string *
        redactedPayloadPreview: string
    /// Phase 503: ask the user to approve a consequential tool call
    /// before it runs. Emitted by the agent loop's dispatch site when the
    /// deployment's `IToolApprovalPolicy` holds this invocation; the tool
    /// is suspended on a `TaskCompletionSource` keyed by `approvalId`
    /// until the browser POSTs a `ToolApprovalDecisionRequest` to
    /// `/api/ai/tool-approval`, or the shared suspended-dispatch timeout
    /// fires and the invocation is refused.
    ///
    /// The payload is FSharp-primitive-only by construction (rule 1 —
    /// identity by value): every field is a `Guid` or a `string`, so no
    /// server type and no live handle crosses the wire, and a non-.NET
    /// client can render the prompt from the JSON alone.
    ///
    /// `summary` and `detail` are the DEPLOYMENT's own words about the
    /// consequence, from the `ApprovalPrompt` its policy returned — the
    /// SDK cannot know which of a consumer's tools is irreversible.
    /// `redactedArgumentsPreview` is a short, length-capped rendering of
    /// the model's own arguments: what the tool would be called WITH,
    /// never the module's data, which has not been read.
    | ToolApprovalRequired of
        taskId: Guid *
        approvalId: Guid *
        conversationId: Guid *
        toolName: string *
        sourceModule: string *
        summary: string *
        detail: string *
        redactedArgumentsPreview: string

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
/// be added without changing the ToolUp.Remoting contract.
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
    /// (ToolUp.Remoting will see no field on the wire and fall through
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
    /// Phase 502.D — per-message metadata-equality filter scoping this
    /// turn's retrieval ("answer from document X only", "only tag=policy").
    /// Threaded onto `PromptContext.RetrievalFilters` by the assistant
    /// handler and merged with the deployment-level `RetrievalDefaults.Filters`
    /// by `RAGPromptBuilder`, which puts the result on
    /// `RetrievalRequest.Filters`. `None` (the default, and what an older
    /// client sending no field deserialises to) leaves the retrieval request
    /// byte-identical to its pre-502.D shape (GP 11), and the field has no
    /// effect at all on a deployment that composes no RAG prompt builder
    /// (GP 13).
    ///
    /// TRUST MODEL: this is a **client-supplied** field and the server takes
    /// it at face value — deliberately, and unlike `Surface` above, because
    /// a filter can only ever REMOVE candidates. It is AND-combined on top
    /// of the scope set the prompt builder derives server-side from the
    /// resolved `AccessContext`, so no value here reaches a chunk the caller
    /// could not already retrieve: the worst a hostile client achieves is a
    /// worse answer to its own question. It is therefore not a security
    /// boundary in either direction — do not use it to withhold content a
    /// caller should not have at all; that is what scopes are for (GP 4).
    RetrievalFilters: Map<string, string> option
}

// ─── API contract (ToolUp.Remoting) ──────────────────────────────

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
// ─── Phase 498 — provider fallback / failover routing ────────────
//
// Phase 43.B shipped `FallbackChain` on every `ProviderProfile` — an
// ordered list of entry labels, "tried left-to-right when the resolved
// primary fails" — but nothing server-side consumed it. These are the
// two things the runtime needs to honour it: the classification of
// WHICH errors justify a re-route, and the shape of the record each
// re-route emits.
//
// Both are Fable-safe (records + a total function over the wire-tier
// `AIProviderError` union), so the client tier can read a failover
// record back off the audit stream without a server-only reference.

/// Phase 498 — which `AIProviderError` values justify advancing to the
/// next entry of a deployment's `FallbackChain`.
module AIProviderFailover =

    /// True when `err` is an OUTAGE: the provider (or the network to
    /// it) is unavailable, so the identical request to a DIFFERENT
    /// provider may well succeed.
    ///
    /// Deliberately a thin recursion over the shipped
    /// `AIProviderError.isRetryable` taxonomy rather than a second
    /// classification — "retry the same provider" and "try another
    /// provider" answer to the same underlying question ("is the
    /// failure about this request, or about this endpoint?"), and two
    /// tables that must agree would eventually not.
    ///
    /// The one place the two differ is `RetriesExhausted`: it is NOT
    /// retryable (the provider's own budget has lapsed — another
    /// attempt at the same endpoint is what just failed repeatedly)
    /// yet it IS the commonest outage signal a chain exists to
    /// survive, so it is unwrapped and judged on the inner cause.
    ///
    /// Everything else is false, and three of them are load-bearing:
    /// - `PermanentClient` — a bad key, a malformed request, a
    ///   content-policy refusal. The same request fails identically
    ///   at the secondary, so re-routing spends a second provider's
    ///   budget to reach the same answer AND masks the misconfiguration
    ///   the operator needs to see. The turn ends.
    /// - `StreamingAborted` — partial content has ALREADY been
    ///   delivered to the user's stream. A re-route would replay the
    ///   answer from the top, duplicating output mid-message.
    /// - `MalformedResponse` / `UnsupportedCapability` /
    ///   `SchemaUnsupported` — the endpoint answered; it is the shape
    ///   of the answer that is wrong. Not an outage.
    let rec isOutageClass (err: AIProviderError) : bool =
        match err with
        | RetriesExhausted(_, inner) -> isOutageClass inner
        | other -> AIProviderError.isRetryable other

/// Phase 498 — one recorded failover. Written through `IEventStore`
/// under `AIProviderFailoverRecord.SourceModule` each time the runtime
/// advances a turn from one chain entry to the next, so "the primary
/// was down and the secondary served the turn" is answerable from the
/// audit trail rather than inferred from a latency outlier.
///
/// Carries no key material and no prompt content — provider and model
/// identifiers, the vendor's own error text, and timings only.
type AIProviderFailoverRecord = {
    OccurredAt: DateTime
    /// Storage scope the failing turn belonged to — the same scope key
    /// `AILatencyRecord`s are written under, so the two streams join.
    ScopeId: string
    /// `Capabilities.ProviderName` / `.Model` of the entry that failed.
    FromProvider: string
    FromModel: string
    /// `ProviderProfile` entry label of the chain position advanced TO.
    ToLabel: string
    /// `Capabilities.ProviderName` / `.Model` of the entry advanced to.
    /// Empty when `Resolved` is false — the label no longer names a
    /// usable entry, so there is no provider to name.
    ToProvider: string
    ToModel: string
    /// `AIProviderError.toMessage` of the error that triggered the
    /// re-route, extended with the resolution failure when `Resolved`
    /// is false.
    Reason: string
    /// Wall-clock milliseconds the FAILED attempt consumed. The
    /// per-attempt half of the phase's latency requirement; the
    /// serving attempt's latency is the `AILatencyRecord` the turn
    /// emits as usual.
    AttemptDurationMs: float
    /// 1-based position within `FallbackChain.Ordered` that was
    /// advanced to, and the chain's length. `ChainPosition =
    /// ChainLength` on the last entry, so "the chain is exhausted" is
    /// readable from a single record.
    ChainPosition: int
    ChainLength: int
    /// False when the chain entry could not be resolved (a stale label,
    /// a missing key). The runtime skips on to the next entry; the
    /// record is still written, because a chain that cannot be walked
    /// is precisely what an operator needs to be told.
    Resolved: bool
}

module AIProviderFailoverRecord =
    /// Reserved `IEventStore` source-module namespace for provider
    /// failover records. Sibling of `AILatencyRecord.SourceModule`;
    /// read back with `ReadBySource(scope, _)`.
    [<Literal>]
    let SourceModule = "_platform.ai.provider_failover"

    /// Reserved `IEventStore` event-type for one recorded failover.
    [<Literal>]
    let EventType = "AIProviderFailover"

// ─── Phase 504 — conversation retention / TTL policy ─────────────

/// Per-scope retention policy for AI conversations (Phase 504.A).
///
/// A conversation is **expired** when EITHER limit selects it: its last
/// activity is older than `MaxAge`, or it is not among the `MaxCount`
/// most-recently-active conversations of its scope. Both `None` is the
/// default and means retain forever — the pre-504 behaviour, byte for
/// byte (GP 11): `withConversationRetention` registers no sweep for an
/// inert policy, and `ConversationRetention.sweepContainer` has no code
/// path from an inert policy to a delete.
///
/// "Last activity" is the newest `ConversationMessage.Timestamp` in the
/// UI blob, falling back to the blob's own `LastModified` for an empty
/// conversation. A conversation whose activity cannot be dated at all
/// (unreadable blob AND no metadata) is never purged — an undated row
/// is a repair job, not an expired one.
type ConversationRetentionPolicy = {
    /// Conversations whose last activity is older than this are purged
    /// on the next sweep. `None` = no age limit.
    MaxAge: TimeSpan option
    /// Keep at most this many conversations per scope, newest-activity
    /// first; the rest are purged. `None` = no count limit. A value of
    /// zero or less is read as "keep none" — every conversation in the
    /// scope expires — which is what the literal says, so the policy is
    /// never silently widened; compose validation is where a deployment
    /// that did not mean that is told so.
    MaxCount: int option
    /// Five-field cron expression for the sweep, in the subset
    /// `IJobScheduler` validates (`*`, integers, comma lists, `*/N`).
    /// Defaults to 04:00 daily — off the chat peak, an hour after the
    /// Knowledge Base sweep, and `Minute` precision so the in-process
    /// scheduler accepts it.
    SweepSchedule: string
}

module ConversationRetentionPolicy =
    /// The default: nothing ever expires; daily 04:00 cadence if a limit
    /// is later set.
    let retainForever: ConversationRetentionPolicy = {
        MaxAge = None
        MaxCount = None
        SweepSchedule = "0 4 * * *"
    }

    /// `true` when the policy can never expire anything — neither limit
    /// set. The compose helper reads this to decide whether to register
    /// the sweep job at all, and the sweep reads it to skip the scan.
    let isInert (policy: ConversationRetentionPolicy) : bool =
        policy.MaxAge.IsNone && policy.MaxCount.IsNone

    /// Select the conversations `policy` expires at `now`, given each
    /// conversation's id and last-activity instant. Pure — the sweep
    /// and its tests share this exact selection.
    ///
    /// Age: `now - lastActivity > MaxAge` (strictly older; a conversation
    /// exactly at the limit is kept). Count: everything past the first
    /// `MaxCount` when ordered newest-activity first, ties broken by id
    /// so two runs over the same data expire the same rows. The result
    /// is the union, oldest first, each id once.
    let selectExpired
        (now: DateTime)
        (policy: ConversationRetentionPolicy)
        (conversations: (Guid * DateTime) list)
        : Guid list =
        if isInert policy then
            []
        else
            let byAge =
                match policy.MaxAge with
                | None -> []
                | Some maxAge ->
                    conversations
                    |> List.filter (fun (_, lastActivity) -> now - lastActivity > maxAge)
                    |> List.map fst

            let byCount =
                match policy.MaxCount with
                | None -> []
                | Some maxCount ->
                    conversations
                    |> List.sortBy (fun (id, lastActivity) -> (-lastActivity.Ticks, id))
                    |> List.skip (min (max maxCount 0) conversations.Length)
                    |> List.map fst

            let expired = Set.ofList (byAge @ byCount)

            conversations
            |> List.filter (fun (id, _) -> expired.Contains id)
            |> List.sortBy (fun (id, lastActivity) -> (lastActivity.Ticks, id))
            |> List.map fst