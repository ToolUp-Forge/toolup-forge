// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open ToolUp.Platform.AI

// ─── Phase 53 — Conversation substrate types ─────────────────────
//
// Shared types for `IConversationStore` (Phase 53). The substrate
// promotes AI assistant conversations from ephemeral
// `AIAssistantHandler` state to a first-class persisted record so
// conversations are auditable, recoverable, and re-runnable against
// new SDK / system-prompt versions.
//
// Identity-by-value (Guiding Principle 12 rule 1): `ConversationId`
// and `TurnId` are strings, not `Guid` or live handles. The
// AIAssistantHandler bridges its existing `ConversationMessage.Id : Guid`
// to the substrate by stringifying at the seam — the two type
// universes coexist deliberately.
//
// **Versus the existing `ConversationMessage` shape (AITypes.fs).**
// `ConversationMessage` is the *client-surface* shape — what the UI
// renders. `ConversationTurn` is the *substrate* shape — what gets
// persisted, replayed, audited, and erased. Round-tripping multipart
// content + tool calls / results is the substrate's job; the client
// surface stays UI-friendly. The two shapes share content fields
// (`AIProviderToolCall`, `AIProviderToolResult`, `AIContentPart` from
// `IAIProvider.fs`) so the bridge is structural, not transformational.
//
// **SchemaVersion is load-bearing.** The wire format will evolve
// (new fields on `Conversation`, additional turn metadata, etc.).
// Persisted records carry `SchemaVersion : int` so the reader can
// dispatch on it; today's writes emit `SchemaVersion = 1`. Don't
// remove this field — its absence is a one-way migration cost.

/// Stable correlation id for a conversation. String-typed for GP 12
/// rule 1 — never a live handle, never an `IActorRef`, never a
/// `Guid` that callers would expect to round-trip through binary
/// serialisers. Conventionally a stringified GUID at the
/// `AIAssistantHandler` bridge today, but the substrate makes no
/// such promise — any URL-safe string is a valid `ConversationId`.
type ConversationId = string

/// Stable correlation id for one turn within a conversation.
/// Conventionally zero-padded so the lexicographic order matches the
/// emission order (`"000001"`, `"000002"`, …) — `PersistentConversationStore`
/// relies on this when listing turns. Other implementations are free
/// to use any monotonic-string convention as long as a turn's id
/// remains unique within its conversation.
type TurnId = string

/// Provider-facing content shape for one turn. Deliberately a
/// duplicate of `AIProviderMessage` (in `IAIProvider.fs`) so the
/// substrate isn't coupled to the agent-loop's wire shape — if
/// `AIProviderMessage` evolves, the substrate's serialised history
/// stays addressable by the substrate's own type. Field names
/// match `AIProviderMessage` for cheap structural translation at
/// the `AIAssistantHandler` seam.
type AIMessageContent = {
    /// `"user"` / `"assistant"` / `"system"` — the provider-wire role.
    Role: string
    /// Legacy textual content. Plain-text turns set `Parts = []` and
    /// carry the full body here; multipart turns populate `Parts`
    /// and may leave `Content = ""` (provider-dependent).
    Content: string
    /// Tool-use blocks emitted by the assistant — empty for user /
    /// system turns.
    ToolCalls: AIProviderToolCall list
    /// Tool-result blocks attached to a user-role turn responding to
    /// a prior assistant `ToolCalls` — empty otherwise.
    ToolResults: AIProviderToolResult list
    /// Phase 6o multipart content blocks. Empty for plain-text turns.
    Parts: AIContentPart list
}

/// Lifecycle of a persisted conversation. Mirrors the agent-loop's
/// own task lifecycle so a halted task's conversation is discoverable
/// by status query (`ListByScope` filtered by `Status = Active` would
/// surface a conversation whose process crashed mid-stream and never
/// flipped to `Completed`).
[<RequireQualifiedAccess>]
type ConversationStatus =
    /// `BeginConversation` happened; no `Completed` / `Errored` /
    /// `Cancelled` transition yet.
    | Active
    /// The conversation reached a natural terminal state — last
    /// assistant turn appended, no further activity expected.
    | Completed
    /// The conversation halted with an error. `reason` carries the
    /// human-readable message for audit / admin display.
    | Errored of reason: string
    /// User-initiated cancellation (cancel POST, browser close,
    /// task-level CTS flipped).
    | Cancelled

/// Frozen-at-start metadata for one conversation. Every field is
/// immutable from `BeginConversation` onward — replays produce a new
/// `Conversation` (GP 5 immutable-by-default), they do not mutate
/// originals.
///
/// `SchemaVersion = 1` for every record written today. Reader
/// implementations should pattern-match on it before deserialising
/// optional fields.
type Conversation = {
    ConversationId: ConversationId
    /// Wire-format schema version. `1` today; bump on field-rename
    /// or field-removal.
    SchemaVersion: int
    /// UTC creation timestamp.
    CreatedAt: DateTime
    /// `userId` of the user who started the conversation. Subject of
    /// DSR (Article 15 / 17) requests — the `IDataExporter` /
    /// `IErasureHandler` adapters match by this field.
    CreatedBy: string
    /// `StorageScope.ScopeId` resolved at start. Locks the
    /// conversation's scope — appends are rejected if a later turn's
    /// resolved scope differs.
    ScopeId: string
    /// Provider name (e.g. `"Claude"`) at start. Replays may use a
    /// different provider; the original record preserves the start
    /// provider for audit / replay-delta computation.
    Provider: string
    /// Model identifier (e.g. `"claude-sonnet-4-5"`).
    ModelName: string
    /// SHA-256 hex of the resolved system prompt at start. Lets a
    /// replay declare "I'm running against a different prompt
    /// digest" without persisting the prompt body itself (the body
    /// may contain tenant-specific configuration).
    SystemPromptDigest: string
    /// `AssemblyInformationalVersion` of the SDK at start, or the
    /// caller's chosen label. Audit / replay metadata only — the
    /// substrate does not interpret it.
    SdkVersion: string
}

/// One turn within a conversation. Append-only — no overwrite paths.
/// `ContentDigest` is the SHA-256 over the canonical JSON of
/// `Content` and lets the manifest record a roll-up digest for
/// tamper detection without re-reading every turn blob.
type ConversationTurn = {
    TurnId: TurnId
    ConversationId: ConversationId
    /// Wire-format schema version. `1` today.
    SchemaVersion: int
    /// Duplicated from `Content.Role` for cheap filter / list views
    /// that don't want to deserialise the full content blob.
    Role: string
    Content: AIMessageContent
    /// UTC append timestamp.
    Timestamp: DateTime
    /// Provider-reported prompt tokens (when available). `None` when
    /// the provider doesn't surface usage on this turn (e.g.
    /// streaming providers that drop usage on cancel).
    TokensIn: int option
    /// Provider-reported completion tokens (when available).
    TokensOut: int option
    /// SHA-256 hex of the canonical JSON of `Content`. The manifest
    /// rolls these into a chained digest for `O(1)` tamper detection
    /// from the header read.
    ContentDigest: string
}

/// Failure modes for `IConversationStore` operations. Distinguishes
/// "the conversation doesn't exist" from "the conversation exists in
/// a different scope" — both are protected against scope-leak
/// attacks where a malicious caller passes a known-foreign id to a
/// scope they have access to.
[<RequireQualifiedAccess>]
type ConversationError =
    /// No conversation with this id exists in the given scope. Also
    /// returned when the id exists in a *different* scope — leaking
    /// "exists-but-not-yours" is itself an information disclosure.
    | NotFound of conversationId: ConversationId
    /// The conversation exists but doesn't contain a turn with this
    /// id.
    | TurnNotFound of conversationId: ConversationId * turnId: TurnId
    /// `AppendTurn` was called with a `turn.ConversationId` that
    /// doesn't match the path parameter, or `MarkStatus` was called
    /// for a conversation whose persisted `ScopeId` differs from the
    /// caller's. Both indicate a programmer error (the AI handler
    /// stitched the wrong ids together) — not a security event.
    | ScopeMismatch of expected: string * actual: string
    /// `MarkStatus` was called with a transition that the substrate
    /// refuses (e.g. `Completed → Active` would erase the terminal
    /// state). Carries the original + attempted state so the caller
    /// can diagnose without re-reading the store.
    | StatusForbidden of from: ConversationStatus * toStatus: ConversationStatus
    /// Underlying `IDataObjectStore` / `IBlobStorage` failed. Carries
    /// the propagated detail for diagnostics.
    | StoreUnreachable of detail: string

module ConversationError =
    let toMessage =
        function
        | ConversationError.NotFound id -> $"Conversation {id} not found"
        | ConversationError.TurnNotFound(cid, tid) -> $"Turn {tid} not found in conversation {cid}"
        | ConversationError.ScopeMismatch(expected, actual) ->
            $"Conversation scope mismatch — expected {expected}, got {actual}"
        | ConversationError.StatusForbidden(from, toStatus) -> $"Status transition forbidden: {from} → {toStatus}"
        | ConversationError.StoreUnreachable detail -> $"Conversation store unreachable: {detail}"

/// Options for `ConversationReplay.replay`. **Every field is
/// operator-supplied audit metadata or a load-time override —
/// nothing here loads a different SDK version.** The replay always
/// runs against the currently-loaded SDK / tool registry / agent
/// loop; the substrate captures the operator's chosen labels so
/// the audit row records "what the operator claims this replay
/// represents." See `ConversationReplay.fs` for the rationale.
type ConversationReplayOptions = {
    /// Override system prompt for the replay run. The replay audit
    /// row records both the original conversation's digest and this
    /// prompt's digest so the delta is reconstructable. `None`
    /// re-uses the original prompt.
    SystemPrompt: string option
    /// Override provider label. Resolution falls through
    /// `IAIProviderFactory.TryResolveByLabel`; `None` re-uses the
    /// original conversation's `Provider`.
    ProviderLabel: string option
    /// Free-form label the operator wants stamped on the replay's
    /// `SdkVersion` field. Conventionally an SDK version string but
    /// the substrate does not interpret it. `None` re-uses the
    /// original conversation's `SdkVersion`.
    SdkAnnotation: string option
}

module ConversationReplayOptions =
    /// Replay with no overrides — re-run against the original
    /// prompt / provider / SDK label. Useful for "did the same
    /// conversation produce the same answer today?" sanity checks.
    let empty: ConversationReplayOptions = {
        SystemPrompt = None
        ProviderLabel = None
        SdkAnnotation = None
    }

/// Outcome of a replay run.
type ConversationReplayResult = {
    /// Id of the newly-persisted replay conversation. The original
    /// is unchanged (GP 5 immutable-by-default).
    ReplayConversationId: ConversationId
    /// Id of the conversation that was replayed.
    OriginalConversationId: ConversationId
    /// New assistant turns produced by the replay. The replay does
    /// not re-emit user turns — those are taken verbatim from the
    /// original — so this list contains only the assistant turns the
    /// fresh agent loop produced. One per replayed user turn in
    /// emission order.
    NewAssistantTurns: ConversationTurn list
    /// Human-readable comparison summary for the audit row. Today
    /// reports the turn count + token deltas + first-divergence
    /// turn index. Format is operator-readable; do not parse it.
    Delta: string
}