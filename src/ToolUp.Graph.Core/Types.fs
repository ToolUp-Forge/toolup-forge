// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph

// ─── Graph store substrate — value types ────────────────────────────
//
// The graph-shaped peer of the entity store (`IEntityStore`): where the
// entity store answers relational-algebra lookups over indexed records,
// `IGraphStore` answers traversal queries — variable-length paths,
// neighbourhood expansion, shortest-path — that the relational layer
// cannot express in one round-trip.
//
// Every type here is Fable-compatible (DUs / records / Map / Set /
// primitives only) so the source ships under `fable/` in the nupkg and a
// browser-tier consumer can hold graph values without a server hop.
//
// Six-rule portability audit (Guiding Principle 12) — the value model:
//   1. Identity by value      — `NodeId` / `EdgeId` are string-wrapped
//                               single-case DUs; never a live handle
//                               (`IActorRef` / `IGrainReference`).
//   6. Precision at lower bound — `PropertyValue` tops out at `int64` /
//                               `float`; there is deliberately NO
//                               `decimal`-in-graph case. Graph engines
//                               (Kùzu / Neo4j / AGE) standardise on
//                               64-bit ints + IEEE-754 doubles; promising
//                               decimal precision some engine can't honour
//                               would be a rule-6 violation.
// The remaining rules land on the interface (`IGraphStore.fs`).

/// Stable identifier for a node, unique within `(scopeId)`. A value, not
/// a live handle — a distributed engine returns the same string a grain
/// deactivation / reactivation would (rule 1). String-wrapped rather than
/// `Guid` because some domains carry natural keys (an org-unit code, a
/// document URI); the substrate imposes no key shape, only value-identity.
type NodeId = NodeId of string

/// Stable identifier for an edge, unique within `(scopeId)`. Same
/// value-identity contract as `NodeId`.
type EdgeId = EdgeId of string

[<RequireQualifiedAccess>]
module NodeId =
    /// Unwrap to the raw string.
    let value (NodeId s) = s

[<RequireQualifiedAccess>]
module EdgeId =
    /// Unwrap to the raw string.
    let value (EdgeId s) = s

/// A property cell on a node or edge. Precision floor (rule 6): integers
/// at `int64`, reals at `float`, timestamps at `DateTime`. No `decimal`
/// case — see the module header.
type PropertyValue =
    | PString of string
    | PInt of int64
    | PFloat of float
    | PBool of bool
    | PDateTime of System.DateTime

/// A node in the graph: a value-identity `NodeId`, a set of labels
/// (`:Person`, `:Account` — order-independent, hence `Set`), and a
/// property map. Immutable (GP 5): mutations produce a new record.
type GraphNode = {
    Id: NodeId
    Labels: Set<string>
    Properties: Map<string, PropertyValue>
}

/// A directed edge (relationship) between two nodes. Carries a single
/// `Label` (Cypher relationship type, e.g. `KNOWS`), the endpoints by
/// value, and its own property map.
type GraphEdge = {
    Id: EdgeId
    Label: string
    From: NodeId
    To: NodeId
    Properties: Map<string, PropertyValue>
}

/// Traversal direction for `Neighbours` and relationship patterns.
type Direction =
    /// Follow outgoing edges (`(a)-[:E]->(b)` — neighbours are the `b`s).
    | Outgoing
    /// Follow incoming edges (`(a)<-[:E]-(b)` — neighbours are the `b`s).
    | Incoming
    /// Follow edges in either direction.
    | Both

/// A relationship-type filter for `Neighbours`. `None` matches any label.
type EdgeLabel = string

/// A parameterised openCypher query. Parameters are supplied as data
/// (`$name` bindings) rather than string-concatenated into `Text` — the
/// same discipline SQL parameterisation enforces, closing the
/// injection-class hazard. Every engine tier reads the same `Text`; the
/// in-memory default interprets the documented subset and throws
/// `CypherSubsetException` for anything outside it.
type CypherQuery = {
    Text: string
    Parameters: Map<string, PropertyValue>
}

[<RequireQualifiedAccess>]
module CypherQuery =
    /// A parameterless query.
    let ofText (text: string) : CypherQuery = { Text = text; Parameters = Map.empty }

    /// A query with a parameter map.
    let create (text: string) (parameters: Map<string, PropertyValue>) : CypherQuery = {
        Text = text
        Parameters = parameters
    }

/// A single cell in a query result row. Richer than `PropertyValue`
/// because `RETURN n` yields a whole node, `RETURN r` a whole edge,
/// `RETURN n.prop` a scalar, and `RETURN collect(...)` a list.
type GraphValue =
    /// A scalar property value (`RETURN n.name`).
    | VProperty of PropertyValue
    /// A whole node (`RETURN n`).
    | VNode of GraphNode
    /// A whole edge (`RETURN r`).
    | VEdge of GraphEdge
    /// An ordered collection (variable-length path expansion, aggregation).
    | VList of GraphValue list
    /// A null cell — an optional match that did not bind.
    | VNull

/// The result of a `Query`. `Columns` is the ordered `RETURN` projection;
/// each row maps every column name to its `GraphValue`. Row order is
/// promised only when the query carries `ORDER BY` (rule 5 — no
/// cross-shard ordering guarantee without an explicit sort).
type GraphResultSet = {
    Columns: string list
    Rows: Map<string, GraphValue> list
}

[<RequireQualifiedAccess>]
module GraphResultSet =
    /// An empty result with the given projected columns.
    let empty (columns: string list) : GraphResultSet = { Columns = columns; Rows = [] }

/// Why a graph-store operation failed, as data (rule 3 — retry /
/// supervision expressed as values, never as an `OnFailure: exn -> unit`
/// callback that would leak a framework's supervision semantics). A
/// `TransientFailure` is the retryable shape a caller loops on; the
/// interface exposes no callbacks, so any distributed implementation
/// surfaces the same DU.
///
/// Note: an *out-of-subset* Cypher construct is NOT a `GraphError` — it
/// throws `CypherSubsetException` (a programmer error, like a malformed
/// argument), so a silently-wrong result is impossible.
type GraphError =
    /// The referenced node does not exist in this scope.
    | NodeNotFound of NodeId
    /// The referenced edge does not exist in this scope.
    | EdgeNotFound of EdgeId
    /// One endpoint of an upserted edge is absent.
    | DanglingEdge of edge: EdgeId * missingEndpoint: NodeId
    /// The query is syntactically malformed (distinct from out-of-subset,
    /// which throws). Diagnostic message for developer-facing logs.
    | MalformedQuery of message: string
    /// A retryable transient fault (backend unreachable, contended swap).
    /// The caller retries per its own `RetryPolicy` — the shape is data.
    | TransientFailure of message: string
    /// Underlying storage failure that maps to nothing more specific.
    | StorageFailure of message: string

[<RequireQualifiedAccess>]
module GraphError =
    let message (err: GraphError) : string =
        match err with
        | NodeNotFound id -> sprintf "Node not found: %s" (NodeId.value id)
        | EdgeNotFound id -> sprintf "Edge not found: %s" (EdgeId.value id)
        | DanglingEdge(edge, endpoint) ->
            sprintf "Edge %s references a missing endpoint node: %s" (EdgeId.value edge) (NodeId.value endpoint)
        | MalformedQuery msg -> sprintf "Malformed query: %s" msg
        | TransientFailure msg -> sprintf "Transient graph failure (retryable): %s" msg
        | StorageFailure msg -> sprintf "Graph storage failure: %s" msg

/// Thrown by an `IGraphStore.Query` when the supplied openCypher uses a
/// construct outside the implementation's documented subset. The
/// in-memory default interprets a bounded portability floor; anything
/// beyond it throws this rather than silently returning wrong results
/// (an acceptance-critical property of the substrate). `Clause` names the
/// offending construct so a caller can decide whether to compose an
/// engine-backed companion (Kùzu / Neo4j / AGE) for full Cypher.
type CypherSubsetException(clause: string) =
    inherit
        System.Exception(
            sprintf
                "openCypher construct outside the in-memory subset: %s. The in-memory IGraphStore interprets a documented portability floor; compose an engine-backed companion (ToolUp.Graph.Kuzu / .Neo4j / .AGE) for full Cypher."
                clause
        )

    /// The unsupported construct (e.g. `"MERGE"`, `"multi-hop pattern"`).
    member _.Clause = clause