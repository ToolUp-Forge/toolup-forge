// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts

open System

// ─── Fact-store audit events (Phase 520) ─────────────────────────────
//
// The fact store audits to `IEventStore` under a **reserved source
// module** (`_facts`) rather than adding cases to the core `AuditEvent`
// DU — the `ILineageStore` pattern (which writes `_platform.lineage`
// events). This keeps the companion self-contained (GP 1: no core edit)
// while producing a durable, scope-isolated, queryable audit record
// (`IEventStore.ReadBySource scope "_facts"`) that the later provenance
// chain (Phase 524) walks. PII-free: identifiers + classification only.

/// Reserved source-module + event-type discriminators for the fact-store
/// audit trail.
module FactEvents =
    /// `ModuleEvent.SourceModule` for every fact-store audit event. Filter
    /// `IEventStore.ReadBySource scope FactEvents.SourceModule` for the
    /// fact audit trail in isolation.
    [<Literal>]
    let SourceModule = "_facts"

    /// A new fact was asserted.
    [<Literal>]
    let AssertedType = "FactAsserted"

    /// An assertion superseded the current head of its lineage.
    [<Literal>]
    let SupersededType = "FactSuperseded"

/// Payload of a `FactAsserted` audit event (JSON-serialised into
/// `ModuleEvent.Payload`).
type FactAssertedEvent = {
    FactId: string
    /// Readable subject reference (`hierarchy/level>level`).
    Subject: string
    Metric: string
    /// Method identity (`computed:op:ver:hash` / `asserted:principal` /
    /// `imported:cert`).
    Method: string
    /// Disclosure class at birth (`Surfaceable` / `Internal` /
    /// `Restricted(policy)`).
    Disclosure: string
    /// Transaction time the fact entered the store.
    AsOf: DateTime
}

/// Payload of a `FactSuperseded` audit event.
type FactSupersededEvent = {
    NewFactId: string
    SupersededFactId: string
    Subject: string
    Metric: string
    AsOf: DateTime
}

// ─── IFactStore (Phase 520) ──────────────────────────────────────────
//
// The server-side, team-scoped surface over the bitemporal fact base.
// Append-only: `Assert` never mutates — a changed input supersedes the
// current lineage head with a *derived* edge; an identical tuple is
// idempotent (content-addressed). Reads honour law L4 visibility
// (`AsOf`), and supersession chains are walkable.
//
// **Six portability rules (GP 12).**
//  1. Identity by value — `string` scope / fact ids, domain records; no
//     live handles.
//  2. Async at every boundary — every method returns `Async<_>`.
//  3. Retry / failure as data — `Assert` returns `Result<_, string>`; no
//     `OnFailure` callbacks.
//  4. Stateless between calls — no per-call state survives on the store;
//     every read recomputes from the backing store.
//  5. No cross-shard ordering — ordering is only meaningful within one
//     `scopeId` (the shard key); the store promises none across scopes.
//  6. Precision at the lower bound — transaction time (`AsOf`) is stamped
//     at second precision from the store's injected clock; callers must
//     not assume sub-second `AsOf` ordering.

/// A caller's proposed fact — everything except the machine-derived
/// fields. The store computes the content-addressed `FactId`, stamps the
/// transaction time (`AsOf`) from its clock, and derives the `Supersedes`
/// edge; a caller never supplies those.
type FactDraft = {
    Subject: SubjectRef
    Metric: MetricRef
    Value: FactValue
    Period: TemporalExtent
    Method: MethodRef
    Evidence: Evidence
    Confidence: Confidence option
    Disclosure: Disclosure
}

/// Read filter for `IFactStore.Query`. Every clause is optional and
/// AND-combined; an all-`None` query returns every current fact in scope.
type FactQuery = {
    /// Restrict to one subject instance.
    Subject: SubjectRef option
    /// Restrict to one metric.
    Metric: MetricRef option
    /// Restrict to facts whose `Period` overlaps this extent.
    PeriodOverlaps: TemporalExtent option
    /// Restrict to one method's lineage — the mechanism for picking one
    /// among *competing* facts (plan D19: several methods computing one
    /// metric is normal; the store never merges them). Registry-declared
    /// *canonical*-method selection is a later phase; until then a caller
    /// disambiguates by naming the method here.
    Method: MethodRef option
    /// Law L4 visibility instant — the fact base *as of* this transaction
    /// time. `None` = now (the current head of each lineage). `Some t`
    /// reconstructs "what we knew at `t`".
    AsOf: DateTime option
    /// Include superseded facts (full history per lineage). `false` (the
    /// default) returns only the current head visible at `AsOf`.
    IncludeSuperseded: bool
}

/// Construction helpers for the common query shapes.
module FactQuery =

    /// The empty query — every current fact in scope, visible now.
    let all: FactQuery = {
        Subject = None
        Metric = None
        PeriodOverlaps = None
        Method = None
        AsOf = None
        IncludeSuperseded = false
    }

    /// Every current fact for one (subject, metric), visible now.
    let forSubjectMetric (subject: SubjectRef) (metric: MetricRef) : FactQuery = {
        all with
            Subject = Some subject
            Metric = Some metric
    }

    /// Reconstruct the fact base as of a transaction time (law L4).
    let asOf (t: DateTime) (q: FactQuery) : FactQuery = { q with AsOf = Some t }

/// The bitemporal, append-only, content-addressed fact base. Team-scoped
/// throughout (GP 4): `scopeId` is a resolved storage scope, and a fact
/// asserted in one scope is structurally unreachable from another.
type IFactStore =
    /// Assert a fact (law L1/L2/L3). Content-addressed: asserting an
    /// identical (subject, metric, period, method, inputHashes) returns
    /// the existing fact unchanged (idempotent). A fact with the same
    /// lineage key (subject, metric, period, method id) but changed inputs
    /// supersedes the current head — the `Supersedes` edge is derived and
    /// its `AsOf` strictly exceeds its predecessor's. A different method
    /// yields a *competing* fact (both current, never merged — D19).
    /// Returns the stored fact with `FactId` / `AsOf` / `Supersedes`
    /// populated. Every assert (and supersession) is audited (GP 6).
    abstract Assert: scopeId: string * draft: FactDraft -> Async<Result<Fact, string>>

    /// A fact by its content-addressed id, or `None`.
    abstract Get: scopeId: string * factId: string -> Async<Fact option>

    /// Query the fact base under `query` (see `FactQuery`). Honours L4
    /// `AsOf` visibility and the competing-fact / supersession rules.
    abstract Query: scopeId: string * query: FactQuery -> Async<Fact list>

    /// The supersession chain a fact belongs to — every fact in its
    /// lineage from the earliest to the latest, ordered by `AsOf`
    /// ascending. A single-fact lineage returns just that fact; an unknown
    /// id returns the empty list.
    abstract QuerySupersessionChain: scopeId: string * factId: string -> Async<Fact list>