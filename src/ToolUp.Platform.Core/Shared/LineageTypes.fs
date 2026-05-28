// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

// ─── Lineage types ───────────────────────────────────────────────
//
// Lineage is a query layer over `IEventStore` filtered to events
// whose `EventType = LineageEventType.Link`. Each link records "this
// object was derived from that object", with a coarse `LinkType` so
// query consumers can colour edges by relationship.
//
// No separate persistence — every `LineageLink` is a `ModuleEvent`
// with `SourceModule = "_platform.lineage"` and JSON-serialised
// payload. `ILineageStore` builds graphs in-memory from
// `IEventStore.ReadByType` results; cross-scope leakage is
// structurally impossible because reads are scope-prefixed.

/// Coarse classification of how the source object contributed to
/// the derived object. The platform doesn't enforce a difference in
/// behaviour between cases — they exist for query consumers (admin
/// UIs, AI tools, auditors) to colour edges and answer questions
/// like "which descendants reused X verbatim vs. transformed it?".
type LinkType =
    /// New result computed from the source (default for analytical
    /// outputs).
    | Derived
    /// Source content reshaped without semantic loss (e.g. format
    /// conversion, column rename).
    | Transformed
    /// Source rolled up with siblings (group-by, summary).
    | Aggregated
    /// Source consulted but not consumed (configuration, lookup
    /// table).
    | Referenced

/// One edge in the lineage graph: "the object at `ToObjectId` was
/// produced from the object at `FromObjectId` by `ModuleName`".
/// Link identity is by `LinkId` so the same source/destination pair
/// emitted twice produces two records (the second supersedes the
/// first only at the application's discretion — the store does not
/// dedupe).
type LineageLink = {
    LinkId: Guid
    FromObjectId: string
    ToObjectId: string
    ModuleName: string
    LinkType: LinkType
    Timestamp: DateTime
}

/// One node in a lineage graph. `ModuleName` is the producer; `None`
/// means the object has no recorded producer in this graph (typically
/// a user-uploaded input file that predates lineage tracking, or sat
/// outside any saved result).
type LineageNode = {
    ObjectId: string
    ModuleName: string option
}

/// Materialised lineage graph rooted at one object. `Root` matches
/// the `objectId` passed to `GetAncestors` / `GetDescendants`.
/// `Nodes` and `Edges` are deduplicated; ordering is not specified
/// (callers that need topological order sort themselves on
/// `Timestamp`).
type LineageGraph = {
    Root: string
    Nodes: LineageNode list
    Edges: LineageLink list
}

/// Reserved `EventType` constant for lineage events. The store
/// writes one `ModuleEvent` per `LineageLink`; queries filter on
/// this constant.
module LineageEventType =
    [<Literal>]
    let Link = "LineageLink"