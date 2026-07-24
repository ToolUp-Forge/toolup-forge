// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.Projection

// ─── Entity↔Graph projection bridge — result values (68d.A / 68d.C) ──
//
// The value types the projector returns. Kept dependency-free (no
// IEntityStore / IGraphStore reference) so the report + error surface can
// be pattern-matched by any caller. `ProjectionError` follows the six-rule
// portability contract's rule 3 — failures are DATA the caller loops on,
// never a callback that would leak a framework's supervision semantics.

/// Why a projection operation failed, as data (GP 12 rule 3). A sync
/// failure is retryable: the projection records enough state
/// (`lastProjectedVersion`) that a missed / failed signal is reconcilable
/// via `RebuildProjection`, never a silent drop from the graph.
type ProjectionError =
    /// The entity type has no projection enrollment. The bridge only
    /// projects entity types the deployment enlisted; an unenrolled type
    /// is intentionally not projected (not an error the caller must fix at
    /// runtime, but surfaced so the miswiring is visible).
    | UnknownProjectedType of entityType: string
    /// The graph store rejected an upsert / delete. Carries the underlying
    /// `GraphError.message`. Retryable when the underlying fault was
    /// transient (the graph substrate surfaces `TransientFailure` as its
    /// own retryable value).
    | GraphWriteFailed of message: string
    /// The entity store could not be read while projecting. Retryable —
    /// a rebuild re-reads and heals.
    | EntityLoadFailed of message: string

module ProjectionError =
    let message (err: ProjectionError) : string =
        match err with
        | UnknownProjectedType t -> sprintf "No projection enrollment for entity type '%s'" t
        | GraphWriteFailed m -> sprintf "Graph write failed during projection: %s" m
        | EntityLoadFailed m -> sprintf "Entity load failed during projection: %s" m

/// Observability counts from a `RebuildProjection` run. A rebuild walks
/// every enrolled entity type, reconciles the graph to match the entity
/// store, and reports what it changed. A second rebuild over an unchanged
/// store is a no-op — every count is `0` (`ProjectionReport.isNoOp`).
type ProjectionReport = {
    /// Nodes actually written because they were absent or had drifted from
    /// the projected value. An unchanged node is not counted (idempotency).
    NodesUpserted: int
    /// Edges newly created (a relationship edge that did not already exist
    /// between the two endpoints under that label). An already-present edge
    /// is not counted (idempotency).
    EdgesUpserted: int
    /// Projected-type nodes deleted because their source entity is gone
    /// (drift healing — a missed delete signal, or an out-of-band graph
    /// mutation).
    OrphansRemoved: int
}

module ProjectionReport =
    let empty: ProjectionReport = {
        NodesUpserted = 0
        EdgesUpserted = 0
        OrphansRemoved = 0
    }

    /// `true` when the rebuild changed nothing — the graph already matched
    /// the entity store. The idempotency acceptance property: a second
    /// rebuild over an unchanged store is a no-op.
    let isNoOp (r: ProjectionReport) : bool =
        r.NodesUpserted = 0 && r.EdgesUpserted = 0 && r.OrphansRemoved = 0