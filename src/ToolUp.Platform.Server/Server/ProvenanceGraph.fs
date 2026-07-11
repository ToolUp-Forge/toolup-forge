// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System.Collections.Generic

// ─── DefaultProvenanceGraph (Phase 524) ──────────────────────────────
//
// A read-only view composing `ILineageStore` (the data-object / result
// lineage graph) with an optional `IFactEvidenceSource` (fact evidence +
// supersession). No persistence of its own — every walk recomputes from
// the authoritative stores. Bounded, cycle-safe BFS: a `visited` set
// prevents re-expansion, and `depth` bounds the fact / message hops.
// Scope-bounded throughout (GP 4) — every underlying read takes `scopeId`.

/// Construct via `ProvenanceGraph.create` / `createWithFacts`.
type DefaultProvenanceGraph(lineage: ILineageStore, factSource: IFactEvidenceSource option) =

    let refId =
        function
        | DataObjectRef id
        | ResultRef id
        | FactRef id
        | MessageRef id -> id

    let refKind =
        function
        | DataObjectRef _ -> DataObjectVersion
        | ResultRef _ -> AnalysisResult
        | FactRef _ -> FactNode
        | MessageRef _ -> ConversationMessage

    let node id kind disclosure : ProvenanceNode = {
        Id = id
        Kind = kind
        Disclosure = disclosure
        Label = id
    }

    // Merge a lineage subgraph (transitive ancestors / descendants) into
    // the accumulator as DataObjectVersion nodes + DerivedFrom edges. A
    // LineageLink is source→consumer; a provenance DerivedFrom edge is
    // child(consumer) → source, so it flips to `{ From = ToObjectId; To =
    // FromObjectId }`.
    let mergeLineage
        (g: LineageGraph)
        (nodes: Dictionary<string, ProvenanceNode>)
        (edges: HashSet<string * string * ProvenanceEdgeKind>)
        =
        for n in g.Nodes do
            if not (nodes.ContainsKey n.ObjectId) then
                nodes[n.ObjectId] <- node n.ObjectId DataObjectVersion None

        for e in g.Edges do
            if not (nodes.ContainsKey e.FromObjectId) then
                nodes[e.FromObjectId] <- node e.FromObjectId DataObjectVersion None

            if not (nodes.ContainsKey e.ToObjectId) then
                nodes[e.ToObjectId] <- node e.ToObjectId DataObjectVersion None

            edges.Add((e.ToObjectId, e.FromObjectId, DerivedFrom)) |> ignore

    // Walk from a set of seed refs, returning a chain rooted at the first
    // seed. `nodes`/`edges` accumulate; `visited` guards cycles +
    // re-expansion; `maxDepth` bounds the fact / message hops.
    let run
        (scopeId: string)
        (seeds: ProvenanceRef list)
        (direction: ProvenanceDirection)
        (maxDepth: int)
        : Async<ProvenanceChain> =
        async {
            let nodes = Dictionary<string, ProvenanceNode>()
            let edges = HashSet<string * string * ProvenanceEdgeKind>()
            let visited = HashSet<string>()

            for s in seeds do
                let id = refId s

                if not (nodes.ContainsKey id) then
                    nodes[id] <- node id (refKind s) None

            let queue = Queue<ProvenanceRef * int>()

            for s in seeds do
                queue.Enqueue(s, maxDepth)

            while queue.Count > 0 do
                let ref, remaining = queue.Dequeue()
                let id = refId ref

                if remaining > 0 && visited.Add id then
                    match ref, direction with
                    | FactRef factId, _ ->
                        match factSource with
                        | None -> ()
                        | Some source ->
                            let! ev = source.GetFact(scopeId, factId)

                            match ev with
                            | None -> ()
                            | Some ev ->
                                nodes[factId] <- node factId FactNode (Some ev.Disclosure)

                                if direction = Upstream then
                                    match ev.ResultRef with
                                    | Some resultId ->
                                        nodes[resultId] <- node resultId AnalysisResult None
                                        edges.Add((factId, resultId, EvidenceFor)) |> ignore
                                        queue.Enqueue(ResultRef resultId, remaining - 1)
                                    | None -> ()

                                    for h in ev.InputHashes do
                                        if not (nodes.ContainsKey h) then
                                            nodes[h] <- node h DataObjectVersion None

                                        edges.Add((factId, h, DerivedFrom)) |> ignore

                                    match ev.Supersedes with
                                    | Some predId ->
                                        edges.Add((factId, predId, Supersedes)) |> ignore
                                        queue.Enqueue(FactRef predId, remaining - 1)
                                    | None -> ()

                    | (ResultRef objId | DataObjectRef objId), Upstream ->
                        let! g = lineage.GetAncestors(scopeId, objId)
                        mergeLineage g nodes edges

                    | (ResultRef objId | DataObjectRef objId), Downstream ->
                        let! g = lineage.GetDescendants(scopeId, objId)
                        mergeLineage g nodes edges

                        // Each descendant object may itself be a result facts
                        // were computed from — enqueue so FactsForResult runs
                        // on it (data → result → fact reach downstream). The
                        // visited set makes re-expansion a no-op.
                        for n in g.Nodes do
                            if n.ObjectId <> objId then
                                queue.Enqueue(DataObjectRef n.ObjectId, remaining - 1)

                        match factSource with
                        | Some source ->
                            let! facts = source.FactsForResult(scopeId, objId)

                            for ev in facts do
                                nodes[ev.FactId] <- node ev.FactId FactNode (Some ev.Disclosure)
                                edges.Add((ev.FactId, objId, EvidenceFor)) |> ignore
                                queue.Enqueue(FactRef ev.FactId, remaining - 1)
                        | None -> ()

                    | MessageRef _, _ -> ()

            let rootId =
                match seeds with
                | s :: _ -> refId s
                | [] -> ""

            return {
                Root = rootId
                Nodes = nodes.Values |> List.ofSeq
                Edges = edges |> Seq.map (fun (f, t, k) -> { From = f; To = t; Kind = k }) |> List.ofSeq
            }
        }

    interface IProvenanceGraph with
        member _.GetChain(scopeId, root, direction, depth) = run scopeId [ root ] direction depth

        member _.GetChainForMessage(scopeId, messageId, citedFactIds, depth) = async {
            // Root the chain at the message; each cited fact is a CitesFact
            // edge whose upstream is then walked. Facts are seeded together
            // so one BFS covers them.
            let! chain = run scopeId (citedFactIds |> List.map FactRef) Upstream depth

            let messageNode: ProvenanceNode = {
                Id = messageId
                Kind = ConversationMessage
                Disclosure = None
                Label = messageId
            }

            let citeEdges =
                citedFactIds
                |> List.map (fun fid -> {
                    From = messageId
                    To = fid
                    Kind = CitesFact
                })

            return {
                chain with
                    Root = messageId
                    Nodes = messageNode :: chain.Nodes
                    Edges = citeEdges @ chain.Edges
            }
        }

/// Construction for `DefaultProvenanceGraph`.
module ProvenanceGraph =

    /// A provenance graph over lineage only (no fact evidence). Data-object
    /// / result chains walk; fact and message nodes are absent until an
    /// `IFactEvidenceSource` is composed.
    let create (lineage: ILineageStore) : IProvenanceGraph =
        DefaultProvenanceGraph(lineage, None) :> IProvenanceGraph

    /// A provenance graph over lineage + a fact-evidence source (the full
    /// ingestion → run → fact → message chain).
    let createWithFacts (lineage: ILineageStore) (factSource: IFactEvidenceSource) : IProvenanceGraph =
        DefaultProvenanceGraph(lineage, Some factSource) :> IProvenanceGraph