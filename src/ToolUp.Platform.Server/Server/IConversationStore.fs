// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open ToolUp.Platform.EntityQueryTypes

// ─── Phase 53 — IConversationStore (ISP-split) ───────────────────
//
// Conversation-substrate interface for `AIAssistantHandler` /
// `ConversationReplay` / the DSR pipeline. Three role-segregated
// interfaces (Reader / Writer / Eraser) over the same underlying
// `IConversationStore` implementation — the split keeps a read-only
// consumer decoupled from the AI handler (write-side) and the
// erasure pipeline (DSR contributor).
//
// **Substrate decision: built on `IDataObjectStore`, not
// `IBlobStorage` raw.** `PersistentConversationStore` (the default
// impl, in `ConversationStore.fs`) delegates to `IDataObjectStore`
// for both the conversation manifest (`Versioned` policy — bumps on
// status change) and the turn blobs (`StrictlyVersioned` —
// append-only, never overwritten). Inherited for free:
//   - SHA-256 content-hash dedup within scope.
//   - Per-`(scopeId, objectId)` shard semantics (GP 12 rule 5).
//   - DSR erasure pre-wired (`IDataObjectStore.Erase` honours all
//     three `ErasurePolicy` values).
//   - Audit emission for the underlying object stores —
//     conversation-level audit (the five `_platform.conversations.*`
//     cases) rides on top.
// The phase body's `manifest.json + turns/{N}.json` layout is an
// *implementation detail* of `PersistentConversationStore`, not a
// substrate contract.
//
// **Future indexed-query wiring.** `Query` accepts an
// `EntityQuery<Conversation>` shape that's structurally compatible
// with Phase 19a's `IEntityStore.Query`. Today's MVP impl runs a
// linear scan + predicate evaluation against manifest metadata —
// adequate for deployments under ~1K conversations per scope. A
// later phase can register `Conversation` headers with `IEntityStore`
// for indexed lookup without changing the substrate's public
// surface; consumers code against the `Query` signature and pay no
// migration cost.
//
// **Six-rule portability audit (Guiding Principle 12):**
//   1. Identity by value      — `ConversationId`, `TurnId` are
//                               strings; no live handles.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — errors flow through
//                                  `ConversationError` / `ErasureError`;
//                                  no callback parameters.
//   4. Stateless between calls — every method takes `scopeId` +
//                                ids; implementations cache nothing
//                                that survives between invocations
//                                in the contract. An Orleans grain /
//                                Akka actor could deactivate between
//                                any two calls and behave identically.
//   5. No cross-shard ordering — turns are linearly numbered within
//                                a single `(scopeId, conversationId)`
//                                shard. Ordering across different
//                                conversations or scopes is not
//                                promised.
//   6. Precision at lower bound — n/a (no time-scheduling
//                                 primitives; turn ordering is
//                                 integer-monotonic).

/// Read surface for `IConversationStore`. A read-only consumer
/// consumes this interface exclusively. AI consumers typically
/// consume the full `IConversationStore` (which extends all three
/// role interfaces).
type IConversationReader =
    /// Fetch the conversation header + every turn in append order +
    /// the current `ConversationStatus`. Returns `NotFound` when the
    /// conversation doesn't exist *or* exists in a different scope
    /// (cross-scope existence is itself information disclosure —
    /// the substrate refuses to leak it).
    abstract GetConversation:
        scopeId: string * conversationId: ConversationId ->
            Async<Result<Conversation * ConversationTurn list * ConversationStatus, ConversationError>>

    /// Fetch one turn by id. `TurnNotFound` when the conversation
    /// exists but the turn doesn't. `NotFound` when the conversation
    /// itself doesn't exist.
    abstract GetTurn:
        scopeId: string * conversationId: ConversationId * turnId: TurnId ->
            Async<Result<ConversationTurn, ConversationError>>

    /// Every conversation header in `scopeId`. Returned order is
    /// reverse-chronological by `Conversation.CreatedAt` — newest
    /// first — matching `IResultStore.ListResults` / admin UI
    /// convention. Empty list when the scope has no conversations.
    abstract ListByScope: scopeId: string -> Async<Conversation list>

    /// Every conversation header in `scopeId` whose `CreatedBy`
    /// matches `userId`. Same ordering as `ListByScope`. Empty list
    /// when the user has no conversations in this scope.
    abstract ListByUser: scopeId: string * userId: string -> Async<Conversation list>

    /// Predicate-shaped query over conversation headers. The query's
    /// `EntityType` field is ignored (the substrate knows it's
    /// querying conversations); other fields are honoured.
    ///
    /// **MVP performance.** Today's `PersistentConversationStore`
    /// runs a linear scan + in-memory predicate evaluation against
    /// every conversation header in scope. Adequate for ~1K
    /// conversations per scope; degrades linearly above that. A
    /// later phase wires `IEntityStore` registration for indexed
    /// lookup without changing this signature.
    abstract Query: scopeId: string * query: EntityQuery<Conversation> -> Async<Conversation list>

/// Write surface for `IConversationStore`. `AIAssistantHandler`
/// consumes this to record the agent-loop's turn-by-turn behaviour
/// when `ConversationStore = EnabledConversationStore _`.
type IConversationWriter =
    /// Start a new conversation. The supplied `Conversation` record's
    /// `ConversationId` becomes the durable id; the record is
    /// frozen — subsequent `MarkStatus` / `AppendTurn` calls do not
    /// rewrite it. Emits `ConversationStarted` audit.
    ///
    /// Returns `ScopeMismatch` when the supplied `Conversation.ScopeId`
    /// disagrees with the `scopeId` parameter (caller error — the
    /// substrate refuses to silently re-stamp).
    abstract BeginConversation: scopeId: string * conversation: Conversation -> Async<Result<unit, ConversationError>>

    /// Append a turn. The supplied `turn.ConversationId` must match
    /// the `conversationId` parameter — `ScopeMismatch` otherwise.
    /// `NotFound` when the conversation doesn't exist (no implicit
    /// create; callers always call `BeginConversation` first).
    /// Emits `ConversationTurnAppended` audit.
    abstract AppendTurn:
        scopeId: string * conversationId: ConversationId * turn: ConversationTurn ->
            Async<Result<unit, ConversationError>>

    /// Transition the conversation's `ConversationStatus`. The
    /// substrate refuses backwards transitions out of a terminal
    /// state (`Completed → Active`, `Errored → Active`,
    /// `Cancelled → Active`); transitions between terminal states
    /// are accepted (e.g. `Active → Errored` after a partial run).
    /// Emits `ConversationCompleted` audit on terminal transitions.
    abstract MarkStatus:
        scopeId: string * conversationId: ConversationId * status: ConversationStatus ->
            Async<Result<unit, ConversationError>>

    /// Hard-delete the conversation and all its turns. Distinct from
    /// `IConversationEraser.Erase` (which is policy-aware DSR
    /// erasure) — `DeleteConversation` is the "user clicked the
    /// trash icon" path. Idempotent.
    abstract DeleteConversation:
        scopeId: string * conversationId: ConversationId -> Async<Result<unit, ConversationError>>

/// Erasure surface for `IConversationStore`. The DSR pipeline (Phase
/// 9h `ErasurePipeline`) consumes this via the
/// `ConversationEraseHandler` `IErasureHandler` adapter, which is one
/// of several store contributors composed into a single DSR run.
type IConversationEraser =
    /// Erase or redact every conversation in `scopeId` whose
    /// `CreatedBy` matches `subjectUserId`, per the supplied
    /// `ErasurePolicy`. Returns the count of affected conversations
    /// plus a human-readable note.
    ///
    ///  - `HardDelete` — remove every matched conversation + every
    ///    turn. Underlying `IDataObjectStore.Erase` GCs the
    ///    deduplicated content blobs.
    ///  - `Tombstone` — preserve the manifest + turn metadata, but
    ///    replace `Content.Content` / `Content.Parts` / tool-call
    ///    arguments / tool-call results with
    ///    `Erasure.TombstoneMarker` so the conversation shape +
    ///    timing + token counts remain auditable while the user's
    ///    bytes are removed.
    ///  - `RetainPerCompliance` — redact `CreatedBy` to the
    ///    tombstone marker on the manifest only; preserve turn
    ///    content. For regimes where audit retention legally
    ///    overrides the erasure request but the structured-id
    ///    fields the store can see must be redacted.
    ///
    /// `dryRun = true` counts matched conversations without
    /// mutating. `subjectUserId` blank ⇒ zero-count no-op (same
    /// catastrophic-over-erasure guard as the other
    /// `IErasureHandler` participants).
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>

/// Aggregate interface — the default implementation
/// (`PersistentConversationStore` / `InMemoryConversationStore`)
/// implements all three role interfaces and exposes them through
/// this single binding. Consumers SHOULD depend on the narrowest
/// role interface they need (`IConversationReader` for the portal,
/// `IConversationWriter` for the AI handler) — `IConversationStore`
/// exists for the composition root + the substrate's own tests.
type IConversationStore =
    inherit IConversationReader
    inherit IConversationWriter
    inherit IConversationEraser