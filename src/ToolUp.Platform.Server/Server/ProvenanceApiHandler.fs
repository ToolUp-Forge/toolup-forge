// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// The disclosure seam (`IFactDisclosureGate`, `FactEgressSurface`,
// `FactDisclosureVerdict`) lives in the Core-tier `VectorKnowledgeTypes`
// module — the same import `ReportApiHandler` takes to reach the export
// door.
open ToolUp.Platform.VectorKnowledgeTypes

// ─── Phase 648 — the provenance walk's remoting handler ──────────────
//
// Builds an `IProvenanceQueryApi` (the Core-tier read-only contract)
// over a composed `IProvenanceGraph`, for a caller whose scope was
// resolved upstream. Pure projection plus two policies — the disclosure
// door and the declared caps — and no new state: the graph stays the
// only reader of the underlying stores, exactly as Phase 524 built it.
//
// **Consumer-wired, like the rest of the family.** Nothing here is
// force-composed. The handler is a factory the deployment mounts into
// its own composition root beside its other remoting APIs, the shape
// `ReportApiHandler` established — a deployment that never wants an
// out-of-process provenance surface constructs none of this and is
// byte-for-byte unchanged (GP 11 / GP 13). Mounting it is a deliberate
// act, which is the right posture for a surface whose whole content is
// how this deployment's numbers were produced.
//
// ── The disclosure door (Phase 525) ──────────────────────────────────
//
// Fact nodes are the only nodes the disclosure plane can judge — the
// gate's predicate is over facts, and a lineage or artifact node is not
// one. So the fact ids in an answer are checked through the one shipped
// `IFactDisclosureGate` at the **`FactExport`** surface, and every other
// node crosses as it stands.
//
// `FactExport` rather than a new surface, deliberately. The grounding
// certificate already materialises a Phase 524 chain, filters its fact
// nodes at `FactExport`, and projects a structure-only body — the same
// act this handler performs, differing only in that the certificate is
// signed and this is answered live. Minting a second surface for it
// would split one operator decision ("what may leave as evidence of how
// a number was produced") across two knobs that would then have to be
// kept in agreement by hand.
//
// **Marker, not suppression.** A denied node crosses as a
// `WireWithheldNode` and its edges stay in the chain. That is the export
// door's posture, and it is right *here* for the reason it is right
// there and wrong at the federation seam: an out-of-process consumer of
// this contract sits inside the deployment's trust boundary, so naming
// the refusal shows an operator the control working, where telling a
// counterparty that an invisible fact exists would itself be the
// disclosure.
//
// Every deny is audited by the gate itself (GP 6) in the trail the
// retrieval, tool-result, publication, export and webhook doors already
// write to. This surface adds a door; it adds no bookkeeper.
//
// ── The caps ─────────────────────────────────────────────────────────
//
// Depth is checked *before* the walk, node count *after* it, and both
// refuse rather than trim. Trimming would be the cheaper implementation
// and the worse contract: a caller cannot tell a shortened chain from a
// complete one, so a partial answer does not degrade its conclusion, it
// falsifies it.

/// Factory for the Phase 648 read-only provenance contract.
module ProvenanceApiHandler =

    // ── Mirror projection ────────────────────────────────────────────
    //
    // Exhaustive by construction: each match is over the server union,
    // so adding a case server-side fails this file at compile time
    // rather than silently mapping to a default. The mirror's
    // completeness in the other direction (a wire case with no server
    // source) is pinned by the conformance test.

    let private toWireNodeKind (kind: ProvenanceNodeKind) : WireProvenanceNodeKind =
        match kind with
        | DataObjectVersion -> WireProvenanceNodeKind.DataObjectVersion
        | AnalysisResult -> WireProvenanceNodeKind.AnalysisResult
        | FactNode -> WireProvenanceNodeKind.FactNode
        | NarrativeDocument -> WireProvenanceNodeKind.NarrativeDocument
        | ConversationMessage -> WireProvenanceNodeKind.ConversationMessage
        | AnswerPlanNode -> WireProvenanceNodeKind.AnswerPlanNode
        | ModelArtifactNode -> WireProvenanceNodeKind.ModelArtifactNode
        | ProvenanceAttachmentNode -> WireProvenanceNodeKind.ProvenanceAttachmentNode

    let private toWireEdgeKind (kind: ProvenanceEdgeKind) : WireProvenanceEdgeKind =
        match kind with
        | DerivedFrom -> WireProvenanceEdgeKind.DerivedFrom
        | EvidenceFor -> WireProvenanceEdgeKind.EvidenceFor
        | CitesFact -> WireProvenanceEdgeKind.CitesFact
        | Supersedes -> WireProvenanceEdgeKind.Supersedes
        | PlannedBy -> WireProvenanceEdgeKind.PlannedBy
        | HasAttachment -> WireProvenanceEdgeKind.HasAttachment

    let private toWireNode (node: ProvenanceNode) : WireProvenanceNode = {
        Id = node.Id
        Kind = toWireNodeKind node.Kind
        Disclosure = node.Disclosure
        Label = node.Label
    }

    let private toWireEdge (edge: ProvenanceEdge) : WireProvenanceEdge = {
        From = edge.From
        To = edge.To
        Kind = toWireEdgeKind edge.Kind
    }

    let private ofWireRef (r: WireProvenanceRef) : ProvenanceRef =
        match r with
        | WireProvenanceRef.DataObjectRef id -> DataObjectRef id
        | WireProvenanceRef.ResultRef id -> ResultRef id
        | WireProvenanceRef.FactRef id -> FactRef id
        | WireProvenanceRef.MessageRef id -> MessageRef id
        | WireProvenanceRef.ModelArtifactRef id -> ModelArtifactRef id

    let private ofWireDirection (d: WireProvenanceDirection) : ProvenanceDirection =
        match d with
        | WireProvenanceDirection.Upstream -> Upstream
        | WireProvenanceDirection.Downstream -> Downstream

    // ── The disclosure door ──────────────────────────────────────────

    /// Split a walk's nodes into what the caller may read and what the
    /// disclosure plane refused. Non-fact nodes always pass — the gate's
    /// predicate is over facts and has nothing to say about a lineage or
    /// artifact node. One gate call per answer, fact ids only.
    ///
    /// Fail-closed on every non-affirmative outcome: denied, unknown, and
    /// unresolvable-in-scope all withhold, because a fact the gate did
    /// not affirmatively permit is one nothing said may cross.
    let private applyDisclosure
        (disclosure: (IFactDisclosureGate * string) option)
        (scopeId: string)
        (nodes: ProvenanceNode list)
        : Async<WireProvenanceNode list * WireWithheldNode list> =
        async {
            let factIds =
                nodes
                |> List.choose (fun n ->
                    match n.Kind with
                    | FactNode -> Some n.Id
                    | _ -> None)
                |> List.distinct

            match disclosure, factIds with
            // No gate composed, or an answer citing no fact: nothing to
            // judge, and the gate is never consulted (GP 13).
            | None, _
            | _, [] -> return nodes |> List.map toWireNode, []
            | Some(gate, principal), ids ->
                let! verdicts = gate.Check(scopeId, principal, FactExport, ids)

                let refusalOf (id: string) : string option =
                    match verdicts.TryFind id with
                    | Some FactDisclosable -> None
                    | Some(FactNotDisclosable policyRef) -> Some policyRef
                    | None -> Some "unknown-fact"

                let classified =
                    nodes
                    |> List.map (fun n ->
                        match n.Kind with
                        | FactNode ->
                            match refusalOf n.Id with
                            | Some policyRef ->
                                Choice2Of2 {
                                    Id = n.Id
                                    Kind = toWireNodeKind n.Kind
                                    PolicyRef = policyRef
                                }
                            | None -> Choice1Of2(toWireNode n)
                        | _ -> Choice1Of2(toWireNode n))

                let readable =
                    classified
                    |> List.choose (function
                        | Choice1Of2 n -> Some n
                        | Choice2Of2 _ -> None)

                let withheld =
                    classified
                    |> List.choose (function
                        | Choice1Of2 _ -> None
                        | Choice2Of2 w -> Some w)

                return readable, withheld
        }

    // ── Single-ref resolution ────────────────────────────────────────

    /// How much a walk actually learned about a node, as opposed to
    /// having merely echoed the seed it was handed. The graph always
    /// seeds the root into its result, so "the answer contains a node
    /// with this id" is not evidence the ref resolved to anything.
    ///
    /// A node scores when it carries a disclosure annotation (a fact's
    /// classification, an artifact's lifecycle status) or a label the
    /// walk supplied rather than defaulted to the id.
    let private evidenceScore (n: ProvenanceNode) =
        (if Option.isSome n.Disclosure then 2 else 0)
        + (if n.Label <> n.Id then 1 else 0)

    /// Both one-hop neighbourhoods of a ref, merged.
    ///
    /// Two walks rather than one because a single direction answers only
    /// half the question: an ingested data object has no ancestors and a
    /// terminal answer has no descendants, so a one-directional probe
    /// reports a node that plainly exists as having no provenance at
    /// all. Where both walks produced the same node, the richer of the
    /// two wins — the direction-sensitive arms of the walk are what
    /// attach a fact's disclosure class or an artifact's status.
    let private incident
        (graph: IProvenanceGraph)
        (scopeId: string)
        (r: ProvenanceRef)
        : Async<ProvenanceNode list * ProvenanceEdge list> =
        async {
            let! upstream = graph.GetChain(scopeId, r, Upstream, 1)
            let! downstream = graph.GetChain(scopeId, r, Downstream, 1)

            let nodes =
                (upstream.Nodes @ downstream.Nodes)
                |> List.fold
                    (fun (acc: Map<string, ProvenanceNode>) n ->
                        match acc.TryFind n.Id with
                        | Some existing when evidenceScore existing >= evidenceScore n -> acc
                        | _ -> acc.Add(n.Id, n))
                    Map.empty
                |> Map.toList
                |> List.map snd

            return nodes, (upstream.Edges @ downstream.Edges) |> List.distinct
        }

    // ── The handler ──────────────────────────────────────────────────

    let private createCore
        (disclosure: (IFactDisclosureGate * string) option)
        (graph: IProvenanceGraph)
        (caps: WireProvenanceCaps)
        (scopeId: string)
        : IProvenanceQueryApi =
        {
            GetCaps = fun () -> async.Return caps

            GetNode =
                fun r -> async {
                    let id = WireProvenanceRef.id r
                    let! nodes, edges = incident graph scopeId (ofWireRef r)

                    let seed = nodes |> List.tryFind (fun n -> n.Id = id)

                    // The ref resolved when the walk learned something
                    // about it: an annotation on the node itself, or any
                    // edge incident on it. Neither ⇒ this scope has no
                    // provenance for the ref, which is the same answer
                    // whether the id is unknown, belongs to another
                    // tenant (GP 4 makes those indistinguishable by
                    // design), or is simply unrecorded.
                    let resolved =
                        match seed with
                        | None -> false
                        | Some n -> evidenceScore n > 0 || edges |> List.exists (fun e -> e.From = id || e.To = id)

                    match seed, resolved with
                    | Some node, true ->
                        let! readable, withheld = applyDisclosure disclosure scopeId [ node ]

                        match readable, withheld with
                        | n :: _, _ -> return WireProvenanceNodeAnswer.Found n
                        | _, w :: _ -> return WireProvenanceNodeAnswer.Withheld w
                        | _ -> return WireProvenanceNodeAnswer.Absent
                    | _ -> return WireProvenanceNodeAnswer.Absent
                }

            GetEdges =
                fun r -> async {
                    let id = WireProvenanceRef.id r
                    let! _, edges = incident graph scopeId (ofWireRef r)

                    return {
                        Ref = id
                        Outgoing = edges |> List.filter (fun e -> e.From = id) |> List.map toWireEdge
                        Incoming = edges |> List.filter (fun e -> e.To = id) |> List.map toWireEdge
                    }
                }

            GetChain =
                fun request -> async {
                    if request.Depth < 1 then
                        return Result.Error(ProvenanceDepthInvalid request.Depth)
                    elif request.Depth > caps.MaxDepth then
                        return Result.Error(ProvenanceDepthExceedsCap(request.Depth, caps.MaxDepth))
                    else
                        let! chain =
                            graph.GetChain(
                                scopeId,
                                ofWireRef request.Root,
                                ofWireDirection request.Direction,
                                request.Depth
                            )

                        let reached = List.length chain.Nodes

                        if reached > caps.MaxNodes then
                            // Refused whole. A trimmed chain would be
                            // indistinguishable from a complete one.
                            return Result.Error(ProvenanceChainExceedsNodeCap(reached, caps.MaxNodes))
                        else
                            let! readable, withheld = applyDisclosure disclosure scopeId chain.Nodes

                            return
                                Result.Ok {
                                    Root = chain.Root
                                    Nodes = readable
                                    // Edges incident on a withheld node
                                    // stay: the refusal seals content, it
                                    // does not reshape the graph.
                                    Edges = chain.Edges |> List.map toWireEdge
                                    Withheld = withheld
                                    Depth = request.Depth
                                }
                }
        }

    /// A provenance query surface over the composed graph, with no
    /// disclosure gate: every node the walk reaches crosses as it stands.
    /// The right shape for a deployment with no fact tier — nothing
    /// classified its nodes, so there is nothing for a gate to judge and
    /// none is constructed (GP 13).
    let create (graph: IProvenanceGraph) (caps: WireProvenanceCaps) (scopeId: string) : IProvenanceQueryApi =
        createCore None graph caps scopeId

    /// `create` with the disclosure door engaged: every fact node in
    /// every answer is checked through the supplied gate at the
    /// `FactExport` surface, and a refused node crosses as a
    /// `WireWithheldNode` marker naming the policy rather than vanishing.
    ///
    /// `principal` is the resolved caller the gate audits denies against
    /// — resolve it upstream alongside `scopeId`, since both are
    /// per-caller. The gate arrives from DI wherever the fact store is
    /// composed; the fact tier cannot be composed without it.
    let createWithDisclosureGate
        (gate: IFactDisclosureGate)
        (principal: string)
        (graph: IProvenanceGraph)
        (caps: WireProvenanceCaps)
        (scopeId: string)
        : IProvenanceQueryApi =
        createCore (Some(gate, principal)) graph caps scopeId