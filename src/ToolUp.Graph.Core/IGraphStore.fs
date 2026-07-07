// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph

// ─── IGraphStore — the graph-data substrate interface ───────────────
//
// A portable, openCypher-keyed graph store. The in-memory default
// (`ToolUp.Graph.InMemory`) is the GP-2 zero-dependency floor; the engine
// tiers (Kùzu / Neo4j / AGE) are opt-in companions that answer the *same
// query text*, so a consumer develops against in-memory and deploys
// against an engine with zero domain-code change.
//
// **Scope isolation is structural (GP 4).** Every method takes `scopeId`
// as its first parameter — the same discipline `IEntityStore` /
// `IDataObjectStore` / `IBlobStorage` carry. An implementation derives
// its storage partition from `scopeId`, so a cross-tenant read is
// unrepresentable: there is no method that reaches nodes outside the
// supplied scope, and `Query` parameters cannot name another tenant's
// subgraph. The conformance pack (`IGraphStoreContract`) asserts a
// query in scope A cannot observe scope B's nodes — a tested invariant,
// not a "remember to filter" convention.
//
// Six-rule portability audit (GP 12):
//   1. Identity by value      — `NodeId` / `EdgeId` / `scopeId: string`.
//                               No live handles cross the boundary.
//   2. Async at every boundary — every method returns `Async<_>`.
//   3. Retry/supervision as data — mutating + query paths surface
//                               transient faults as `GraphError.TransientFailure`
//                               (a retryable value the caller loops on).
//                               There is no `OnFailure: exn -> unit`
//                               callback — supervision semantics never
//                               leak through the signature. Pure reads
//                               (`GetNode` / `Neighbours`) model their only
//                               failure — absence — as `option` / empty
//                               list.
//   4. Stateless between calls — no method assumes in-memory state
//                               survives between invocations. A grain that
//                               deactivates, or an actor that restarts,
//                               re-derives every result from `scopeId` +
//                               its backing store.
//   5. No cross-shard ordering — `Query` promises row order only when the
//                               query carries `ORDER BY`. Absent a sort,
//                               `Rows` order is unspecified (the
//                               conformance pack asserts the *contract*,
//                               not a specific order).
//   6. Precision at lower bound — `PropertyValue` tops out at `int64` /
//                               `float` (see `Types.fs`); no `decimal`
//                               promise an engine can't honour.

/// The portable graph-data store. Traversal-shaped queries the relational
/// `IEntityStore` cannot express in one round-trip: variable-length
/// paths, neighbourhood expansion, reachability.
///
/// **Stateless contract.** Every operation derives its result from
/// `scopeId` + the backing store; nothing is assumed to survive between
/// calls (rule 4).
type IGraphStore =
    /// Insert or replace a node by its `Id` within `scopeId`. Returns the
    /// node's id. Transient backend faults surface as
    /// `GraphError.TransientFailure`.
    abstract UpsertNode: scopeId: string * node: GraphNode -> Async<Result<NodeId, GraphError>>

    /// Insert or replace an edge by its `Id` within `scopeId`. Both
    /// endpoints must already exist in the scope; a missing endpoint
    /// returns `GraphError.DanglingEdge`.
    abstract UpsertEdge: scopeId: string * edge: GraphEdge -> Async<Result<EdgeId, GraphError>>

    /// Delete a node and every edge incident on it within `scopeId`.
    /// Idempotent: deleting an absent node returns `Ok`.
    abstract DeleteNode: scopeId: string * id: NodeId -> Async<Result<unit, GraphError>>

    /// Delete an edge within `scopeId`. Idempotent: deleting an absent
    /// edge returns `Ok`.
    abstract DeleteEdge: scopeId: string * id: EdgeId -> Async<Result<unit, GraphError>>

    /// Fetch a node by id. `None` when the `(scopeId, id)` pair has no
    /// node — absence is modelled as `option`, not an error.
    abstract GetNode: scopeId: string * id: NodeId -> Async<GraphNode option>

    /// The immediate neighbours of a node, following edges in `direction`
    /// and (optionally) filtered to `edgeLabel`. `None` label matches any
    /// relationship type. Returns the neighbour *nodes*, deduplicated.
    /// An absent source node yields an empty list.
    abstract Neighbours:
        scopeId: string * id: NodeId * direction: Direction * edgeLabel: EdgeLabel option -> Async<GraphNode list>

    /// Run a parameterised openCypher query against `scopeId`.
    ///
    /// Malformed queries and transient faults surface as `GraphError`.
    /// A construct *outside the implementation's documented subset* throws
    /// `CypherSubsetException` (naming the offending clause) rather than
    /// returning a wrong result — the acceptance-critical "never silently
    /// wrong" property. The in-memory default's subset is the documented
    /// portability floor: a query that runs against it is guaranteed to
    /// run against every engine companion (the reverse is not promised).
    abstract Query: scopeId: string * query: CypherQuery -> Async<Result<GraphResultSet, GraphError>>