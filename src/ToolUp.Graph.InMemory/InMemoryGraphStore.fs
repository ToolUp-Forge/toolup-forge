// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Graph.InMemory

open System.Collections.Concurrent
open ToolUp.Graph

// ─── InMemoryGraphStore (Phase 68.C) ────────────────────────────────
//
// The zero-dependency default `IGraphStore` — the GP-2 floor. A consumer
// with default `ServerConfig` composition gets a working graph store with
// no external dependency and no added NuGet package. Suitable for
// development, tests, and small single-instance deployments; durable /
// distributed graph storage is what the engine companions (68a Kùzu / 68b
// Neo4j / 68c AGE) bring. It is NOT persisted to disk (out of scope for
// this phase) and NOT a full Cypher engine — it interprets the documented
// subset (`CypherSubsetInterpreter`) and throws `CypherSubsetException`
// for anything beyond it.
//
// Storage shape: per-scope adjacency lists held as an immutable
// `ScopeGraph` value. Each tenant scope keys its own `ScopeGraph` in a
// `ConcurrentDictionary`, so scopes are partitioned by construction (GP 4
// — a method only ever touches the `ScopeGraph` for its `scopeId`).
//
// Concurrency (rule 4): reads take the current immutable `ScopeGraph`
// snapshot from the dictionary with no lock — a grain could deactivate
// between two reads and observe the same value-derived result. Mutations
// take a short monitor lock to serialise the read-modify-write of one
// scope's snapshot; the lock wraps only synchronous, pure transforms and
// is never held across an `await` (there is none — the work is in-memory).
// The stored value is always a fully-formed immutable snapshot, so a
// concurrent reader never sees a half-applied mutation.

/// Immutable per-scope graph: node + edge maps plus outgoing / incoming
/// adjacency indexes keyed by node id.
type internal ScopeGraph = {
    Nodes: Map<NodeId, GraphNode>
    Edges: Map<EdgeId, GraphEdge>
    Outgoing: Map<NodeId, Set<EdgeId>>
    Incoming: Map<NodeId, Set<EdgeId>>
}

module internal ScopeGraph =
    let empty = {
        Nodes = Map.empty
        Edges = Map.empty
        Outgoing = Map.empty
        Incoming = Map.empty
    }

    let private addAdj (index: Map<NodeId, Set<EdgeId>>) (key: NodeId) (edgeId: EdgeId) =
        let current = index.TryFind key |> Option.defaultValue Set.empty
        index.Add(key, current.Add edgeId)

    let private removeAdj (index: Map<NodeId, Set<EdgeId>>) (key: NodeId) (edgeId: EdgeId) =
        match index.TryFind key with
        | Some current ->
            let pruned = current.Remove edgeId

            if pruned.IsEmpty then
                index.Remove key
            else
                index.Add(key, pruned)
        | None -> index

    let upsertNode (node: GraphNode) (g: ScopeGraph) : ScopeGraph = {
        g with
            Nodes = g.Nodes.Add(node.Id, node)
    }

    /// Detach an existing edge from the adjacency indexes (used before a
    /// re-point on upsert, and on delete).
    let private detachEdge (edge: GraphEdge) (g: ScopeGraph) : ScopeGraph = {
        g with
            Outgoing = removeAdj g.Outgoing edge.From edge.Id
            Incoming = removeAdj g.Incoming edge.To edge.Id
    }

    let upsertEdge (edge: GraphEdge) (g: ScopeGraph) : Result<ScopeGraph, GraphError> =
        if not (g.Nodes.ContainsKey edge.From) then
            Error(DanglingEdge(edge.Id, edge.From))
        elif not (g.Nodes.ContainsKey edge.To) then
            Error(DanglingEdge(edge.Id, edge.To))
        else
            // If an edge with this id already exists, detach its old
            // adjacency first so a re-pointed edge doesn't leak stale
            // index entries.
            let detached =
                match g.Edges.TryFind edge.Id with
                | Some existing -> detachEdge existing g
                | None -> g

            Ok {
                detached with
                    Edges = detached.Edges.Add(edge.Id, edge)
                    Outgoing = addAdj detached.Outgoing edge.From edge.Id
                    Incoming = addAdj detached.Incoming edge.To edge.Id
            }

    let deleteEdge (edgeId: EdgeId) (g: ScopeGraph) : ScopeGraph =
        match g.Edges.TryFind edgeId with
        | None -> g // idempotent
        | Some edge ->
            let detached = detachEdge edge g

            {
                detached with
                    Edges = detached.Edges.Remove edgeId
            }

    let deleteNode (nodeId: NodeId) (g: ScopeGraph) : ScopeGraph =
        match g.Nodes.TryFind nodeId with
        | None -> g // idempotent
        | Some _ ->
            // Remove every incident edge (both directions), then the node.
            let incidentEdges =
                let out = g.Outgoing.TryFind nodeId |> Option.defaultValue Set.empty
                let inc = g.Incoming.TryFind nodeId |> Option.defaultValue Set.empty
                Set.union out inc

            let withoutEdges = incidentEdges |> Set.fold (fun acc eid -> deleteEdge eid acc) g

            {
                withoutEdges with
                    Nodes = withoutEdges.Nodes.Remove nodeId
            }

    /// The edges stored under the outgoing index for a node.
    let outEdges (nodeId: NodeId) (g: ScopeGraph) : GraphEdge list =
        g.Outgoing.TryFind nodeId
        |> Option.defaultValue Set.empty
        |> Set.toList
        |> List.choose g.Edges.TryFind

    let inEdges (nodeId: NodeId) (g: ScopeGraph) : GraphEdge list =
        g.Incoming.TryFind nodeId
        |> Option.defaultValue Set.empty
        |> Set.toList
        |> List.choose g.Edges.TryFind

    /// The read-only view the interpreter consumes.
    let toView (g: ScopeGraph) : CypherSubsetInterpreter.GraphView = {
        AllNodes = fun () -> g.Nodes |> Map.toList |> List.map snd
        NodeById = fun id -> g.Nodes.TryFind id
        OutEdges = fun id -> outEdges id g
        InEdges = fun id -> inEdges id g
    }

/// The in-memory `IGraphStore`. Construct once and register as the default
/// graph store; safe to share across requests (scopes are partitioned, and
/// per-scope mutations are serialised).
type InMemoryGraphStore() =
    let scopes = ConcurrentDictionary<string, ScopeGraph>()
    let writeLock = obj ()

    let snapshot (scopeId: string) : ScopeGraph =
        match scopes.TryGetValue scopeId with
        | true, g -> g
        | _ -> ScopeGraph.empty

    /// Serialise the read-modify-write of one scope. `transform` is pure
    /// and synchronous, so the lock is never held across an await.
    let mutate
        (scopeId: string)
        (transform: ScopeGraph -> Result<ScopeGraph * 'a, GraphError>)
        : Result<'a, GraphError> =
        lock writeLock (fun () ->
            let current = snapshot scopeId

            match transform current with
            | Error e -> Error e
            | Ok(updated, result) ->
                scopes.[scopeId] <- updated
                Ok result)

    interface IGraphStore with

        member _.UpsertNode(scopeId, node) = async {
            return mutate scopeId (fun g -> Ok(ScopeGraph.upsertNode node g, node.Id))
        }

        member _.UpsertEdge(scopeId, edge) = async {
            return
                mutate scopeId (fun g ->
                    match ScopeGraph.upsertEdge edge g with
                    | Ok g' -> Ok(g', edge.Id)
                    | Error e -> Error e)
        }

        member _.DeleteNode(scopeId, id) = async { return mutate scopeId (fun g -> Ok(ScopeGraph.deleteNode id g, ())) }

        member _.DeleteEdge(scopeId, id) = async { return mutate scopeId (fun g -> Ok(ScopeGraph.deleteEdge id g, ())) }

        member _.GetNode(scopeId, id) = async { return (snapshot scopeId).Nodes.TryFind id }

        member _.Neighbours(scopeId, id, direction, edgeLabel) = async {
            let g = snapshot scopeId

            let labelOk (e: GraphEdge) =
                match edgeLabel with
                | Some l -> e.Label = l
                | None -> true

            let outward = ScopeGraph.outEdges id g |> List.filter labelOk |> List.map _.To

            let inward = ScopeGraph.inEdges id g |> List.filter labelOk |> List.map _.From

            let neighbourIds =
                match direction with
                | Outgoing -> outward
                | Incoming -> inward
                | Both -> outward @ inward

            return neighbourIds |> List.distinct |> List.choose g.Nodes.TryFind
        }

        member _.Query(scopeId, query) = async {
            let view = ScopeGraph.toView (snapshot scopeId)

            try
                return Ok(CypherSubsetInterpreter.interpret view query)
            with CypherSubsetInterpreter.MalformedQueryException msg ->
                // Out-of-subset (CypherSubsetException) deliberately
                // propagates — a query engine mismatch must be loud, never a
                // silently-wrong result. Only genuine syntax errors map to
                // the GraphError data channel.
                return Error(MalformedQuery msg)
        }