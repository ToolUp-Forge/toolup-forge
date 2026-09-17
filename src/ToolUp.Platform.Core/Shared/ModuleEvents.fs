// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// Phase 347 — carved out of SDK.Shared.fs (the inter-module event envelope,
// IEventStore, retention policy and replay helpers). Behaviour-preserving:
// same namespace, same type and module names — only the file boundary moved.

// ─── Inter-module communication ────────────────────────────────────

/// Persisted event envelope for inter-module communication via the datastore.
/// Events are team-scoped via the `ScopeId` field — every read path must
/// filter by scope to preserve team isolation.
type ModuleEvent = {
    Id: Guid
    OccurredAt: DateTime
    ScopeId: string
    SourceModule: string
    EventType: string
    Payload: string // JSON-serialised
}

/// Interface for persisting and querying module events.
///
/// **Ordering contract.** Each method documents its own ordering guarantee; no
/// cross-method ordering is promised. Distributed implementations (journal-based
/// stores, DynamoDB, etc.) may not preserve wall-clock order across partitions
/// or across filter dimensions. Callers that need strict ordering must sort by
/// `OccurredAt` after reading, and must not rely on "read all of type X then type Y
/// and assume total order."
///
/// **Scoping.** All read methods take a `scopeId` parameter; implementations
/// MUST return only events whose `ModuleEvent.ScopeId` matches. Events from
/// other scopes are never returned under any circumstance — a bug here is a
/// team-isolation breach, not a feature gap. The reserved `_platform` scope
/// is for SDK-level events that span all tenants (audit events, health events).
type IEventStore =
    /// Persist an event. The event's `ScopeId` determines which scope the
    /// event belongs to; subsequent reads filtered to that scope will return
    /// it. Write ordering across concurrent callers is serialised by the
    /// implementation (per-store FIFO); `Id` and `OccurredAt` are the
    /// authoritative timestamps for downstream consumers.
    abstract Write: ModuleEvent -> Async<unit>
    /// Read all events for a given scope, reverse-chronological by
    /// `OccurredAt`. No guarantee across partitions in distributed
    /// implementations.
    abstract ReadAll: scopeId: string -> Async<ModuleEvent list>
    /// Read events for a given scope filtered by event type. No ordering
    /// guarantee — callers must sort by `OccurredAt` if order matters.
    abstract ReadByType: scopeId: string * eventType: string -> Async<ModuleEvent list>
    /// Read events for a given scope from a specific source module. No
    /// ordering guarantee — callers must sort by `OccurredAt` if order matters.
    abstract ReadBySource: scopeId: string * sourceModule: string -> Async<ModuleEvent list>
    /// Enumerate every scope id that has at least one persisted event in
    /// this store. Used by background sweeps that fan out per-scope reads
    /// (`AuditReplicator`'s startup recovery + catch-up sweep, GDPR
    /// erasure, …). Implementations may be expensive — distributed
    /// stores list all scope-prefixed records; callers must not invoke
    /// per-request. Order is not guaranteed. Mirrors the existing
    /// `IJobStore.ListScopesWithJobs` and `IVectorStore.ListScopes`
    /// patterns. Portability rule 1 — identity by string.
    abstract ListScopes: unit -> Async<string list>

    /// Phase 9h — GDPR Article 17 erasure surface. Erase (or redact)
    /// every event in `scopeId` whose `Payload` names `subjectUserId`,
    /// interpreting `policy` per the event-store's own semantics:
    ///
    ///  - `HardDelete` — remove the matching events entirely. Breaks
    ///    event-log integrity for the subject; only valid where no
    ///    compliance-driven retention applies.
    ///  - `Tombstone` — keep the envelope (`Id` / `OccurredAt` /
    ///    `ScopeId` / `SourceModule` / `EventType`) but replace the
    ///    whole `Payload` with `Erasure.TombstoneMarker`. The store
    ///    has no schema knowledge of module payloads, so it redacts
    ///    the entire payload rather than risk leaving PII in an
    ///    unrecognised field. Preserves the sequence chain + makes
    ///    the erasure discoverable.
    ///  - `RetainPerCompliance` — refuse. The event log is the
    ///    "what happened" record; for regimes where event/audit
    ///    retention legally overrides Article 17, the handler returns
    ///    `HandlerRefused` and the run records the refusal.
    ///
    /// **Subject match.** `ModuleEvent.Payload` is opaque JSON; the
    /// store has no structured subject column, so "names the subject"
    /// is a substring match of `subjectUserId` within the serialised
    /// payload. Declared precision: substring-within-payload,
    /// best-effort. A blank `subjectUserId` is a zero-count no-op
    /// (it would otherwise match every payload).
    ///
    /// **Scope isolation (GP 4).** Only events whose `ScopeId`
    /// equals `scopeId` are ever touched — Team A's erasure never
    /// reaches Team B even when the same `subjectUserId` substring
    /// appears in both.
    ///
    /// `dryRun = true` computes the affected count without mutating
    /// (the two-phase-commit preview path). `dryRun = false` applies
    /// the policy. Portability audit (GP 12): identity by value,
    /// async at boundary, failure as `ErasureError` data, stateless
    /// between calls, single-scope (no cross-shard ordering),
    /// precision declared above.
    abstract Erase:
        scopeId: string * subjectUserId: string * policy: ErasurePolicy * dryRun: bool ->
            Async<Result<ErasureSummary, ErasureError>>

/// Helpers for creating and emitting events
module Events =
    /// Create a new ModuleEvent scoped to the given scopeId. Callers should
    /// obtain the scopeId from the request's resolved `StorageScope` or use
    /// the reserved `_platform` value for SDK-level events.
    let create (scopeId: string) (sourceModule: string) (eventType: string) (payload: string) = {
        Id = Guid.NewGuid()
        OccurredAt = DateTime.UtcNow
        ScopeId = scopeId
        SourceModule = sourceModule
        EventType = eventType
        Payload = payload
    }

/// Retention policy for persistent event stores. Both dimensions are
/// optional — set neither to keep events forever (useful when the
/// event log is the audit trail and the deployment's compliance regime
/// requires full history).
type EventRetentionPolicy = {
    /// Maximum age of an event. Events older than this are eligible
    /// for pruning. `None` = no age-based pruning.
    MaxAge: TimeSpan option
    /// Maximum number of events retained per scope. When the count is
    /// exceeded, the oldest events (by `OccurredAt`) are pruned first.
    /// `None` = no count-based pruning.
    MaxCountPerScope: int option
}

module EventRetentionPolicy =
    /// No pruning — events are retained indefinitely.
    let unlimited = {
        MaxAge = None
        MaxCountPerScope = None
    }

    /// Retain events for the given age; no per-scope count cap.
    let byAge (maxAge: TimeSpan) = {
        MaxAge = Some maxAge
        MaxCountPerScope = None
    }

    /// Retain the most recent `n` events per scope; no age limit.
    let byCount (maxCount: int) = {
        MaxAge = None
        MaxCountPerScope = Some maxCount
    }

    /// Common default: 90 days, no count cap. Suitable for audit trails
    /// where regulators typically require 90-day retention.
    let ninetyDays = byAge (TimeSpan.FromDays 90.0)

/// Replay helpers for reconstructing state from an event history. The
/// store always returns reverse-chronological; `EventReplay` re-orders
/// to chronological before folding, so callers write a natural
/// left-fold over past events.
module EventReplay =
    /// Fold every event for a scope (chronological, ascending by
    /// `OccurredAt`) through the given folder. Useful for rebuilding a
    /// projection on startup, debugging a timeline, or replaying onto
    /// a test double.
    let foldScope
        (store: IEventStore)
        (scopeId: string)
        (initialState: 'State)
        (folder: 'State -> ModuleEvent -> 'State)
        : Async<'State> =
        async {
            let! events = store.ReadAll scopeId
            // ReadAll contract: reverse-chronological. Replay wants chronological.
            let ordered = events |> List.sortBy _.OccurredAt
            return ordered |> List.fold folder initialState
        }

    /// Same as `foldScope` but filtered to a single event type. Offloads
    /// the filter to the store so distributed implementations can push
    /// it down when possible.
    let foldScopeOfType
        (store: IEventStore)
        (scopeId: string)
        (eventType: string)
        (initialState: 'State)
        (folder: 'State -> ModuleEvent -> 'State)
        : Async<'State> =
        async {
            let! events = store.ReadByType(scopeId, eventType)
            let ordered = events |> List.sortBy _.OccurredAt
            return ordered |> List.fold folder initialState
        }