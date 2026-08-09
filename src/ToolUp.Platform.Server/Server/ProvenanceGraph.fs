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

/// Construct via `ProvenanceGraph.create` / `createWithFacts` /
/// `createWithArtifacts` / `createWith`.
type DefaultProvenanceGraph
    (lineage: ILineageStore, factSource: IFactEvidenceSource option, artifactSource: IArtifactProvenanceSource option) =

    let refId =
        function
        | DataObjectRef id
        | ResultRef id
        | FactRef id
        | MessageRef id
        | ModelArtifactRef id -> id

    let refKind =
        function
        | DataObjectRef _ -> DataObjectVersion
        | ResultRef _ -> AnalysisResult
        | FactRef _ -> FactNode
        | MessageRef _ -> ConversationMessage
        | ModelArtifactRef _ -> ModelArtifactNode

    let node id kind disclosure : ProvenanceNode = {
        Id = id
        Kind = kind
        Disclosure = disclosure
        Label = id
    }

    /// An attachment node. **The label is the media type and the id is the
    /// hash** — the citation, and nothing that required reading the bytes
    /// to produce.
    let attachmentNode (attachment: ProvenanceAttachmentRef) : ProvenanceNode = {
        Id = attachment.ContentHash
        Kind = ProvenanceAttachmentNode
        Disclosure = None
        Label = attachment.MediaType
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
                    // Phase 646 — a model artifact and the opaque
                    // attachments it carries.
                    //
                    // Enumerated in BOTH directions, unlike the lineage
                    // arms above, and that is the point of the phase rather
                    // than a shortcut: an attachment is not a thing the
                    // artifact was derived from OR a thing derived from it,
                    // it is evidence the artifact carries, and it is
                    // equally part of the answer whichever way the caller
                    // came to the artifact. The dataset-version hop is
                    // direction-sensitive and is handled by re-entering the
                    // lineage walk below.
                    | ModelArtifactRef artifactKey, _ ->
                        match artifactSource with
                        | None -> ()
                        | Some source ->
                            let! provenance = source.GetArtifact(scopeId, artifactKey)

                            match provenance with
                            | None -> ()
                            | Some provenance ->
                                nodes[artifactKey] <- node artifactKey ModelArtifactNode (Some provenance.Status)

                                for attachment in provenance.Attachments do
                                    nodes[attachment.ContentHash] <- attachmentNode attachment

                                    edges.Add((artifactKey, attachment.ContentHash, HasAttachment)) |> ignore

                                // The artifact → dataset-version edge the
                                // registry already writes to lineage is
                                // what carries the walk on to the assembly
                                // spec and the sources beneath it, so the
                                // artifact re-enters the walk as an
                                // ordinary lineage node. The visited set
                                // makes that free when the key is already
                                // expanded.
                                if not (System.String.IsNullOrEmpty provenance.DatasetVersion) then
                                    if not (nodes.ContainsKey provenance.DatasetVersion) then
                                        nodes[provenance.DatasetVersion] <-
                                            node provenance.DatasetVersion DataObjectVersion None

                                    edges.Add((artifactKey, provenance.DatasetVersion, DerivedFrom)) |> ignore

                                    queue.Enqueue(DataObjectRef provenance.DatasetVersion, remaining - 1)

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
        DefaultProvenanceGraph(lineage, None, None) :> IProvenanceGraph

    /// A provenance graph over lineage + a fact-evidence source (the full
    /// ingestion → run → fact → message chain).
    let createWithFacts (lineage: ILineageStore) (factSource: IFactEvidenceSource) : IProvenanceGraph =
        DefaultProvenanceGraph(lineage, Some factSource, None) :> IProvenanceGraph

    /// Phase 646 — a provenance graph over lineage + a model-artifact
    /// source: artifact → attachments and artifact → dataset version →
    /// assembly spec → sources, all of it resolvable from this deployment's
    /// own stores.
    let createWithArtifacts (lineage: ILineageStore) (artifactSource: IArtifactProvenanceSource) : IProvenanceGraph =
        DefaultProvenanceGraph(lineage, None, Some artifactSource) :> IProvenanceGraph

    /// The whole graph: lineage, fact evidence and model artifacts. The
    /// composition a data host that answers grounding certificates over
    /// promoted models wants — a cited number walks back through the fact
    /// that produced it, the artifact that fit it, and the exploration
    /// record attached to that artifact, with no reference to the peer that
    /// built it.
    let createWith
        (lineage: ILineageStore)
        (factSource: IFactEvidenceSource option)
        (artifactSource: IArtifactProvenanceSource option)
        : IProvenanceGraph =
        DefaultProvenanceGraph(lineage, factSource, artifactSource) :> IProvenanceGraph