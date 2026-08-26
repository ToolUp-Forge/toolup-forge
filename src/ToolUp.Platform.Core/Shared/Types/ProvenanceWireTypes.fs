// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Phase 648 — the provenance walk as a typed read-only contract ───
//
// The provenance graph (Phase 524, extended through registry
// attachments by Phase 646) is a **server-tier view**: its node and edge
// records already have a wire shape, but nothing shipped exposes them,
// so an out-of-process consumer — a document-emission tier binding
// governed results, an audit tool, a publication surface citing
// artifacts — cannot answer "where did this number come from" without
// the deployment hand-rolling an endpoint. Each such endpoint would be
// a second place the disclosure rules are decided, which is the shape
// this substrate exists to avoid.
//
// So the contract ships here, in the tier both hosts compile: the wire
// records below, and `IProvenanceQueryApi` over them. Three properties
// are load-bearing and each is pinned by a test rather than asserted in
// prose:
//
//   1. **Read-only by construction.** Every method is a query. No
//      member takes a mutation and none returns `unit` — there is no
//      write surface to forget to gate, because there is no write
//      surface.
//   2. **Bounded, never silently truncated.** Depth and node caps are
//      *declared* (`GetCaps`), and a request that would exceed either
//      is refused with a typed error naming the request and the cap. A
//      truncated answer is indistinguishable from a complete one, and a
//      caller reasoning about provenance from a silently-shortened
//      chain reaches a confident wrong conclusion.
//   3. **A withheld node is not an absent one.** A node the disclosure
//      plane suppresses crosses as a typed marker carrying its id, its
//      kind and the policy that refused it — never as a hole. "This
//      exists and you may not see it" and "there is no provenance here"
//      are different answers to a caller auditing a number, and
//      collapsing them turns a working control into apparent missing
//      data.
//
// ── Why these are mirrors and not the server types ───────────────────
//
// The walk's records live in `ToolUp.Platform.Server`, which the client
// tier does not compile. Moving them down would drag the graph's own
// vocabulary into every Fable consumer; sharing them across the tier
// boundary is not available at all. So the wire records mirror the
// server records **field for field and case for case**, the server
// types stay the source, and a conformance test asserts the mirror
// stays complete — the drift shows up as a failing assertion at the
// moment a case is added server-side, rather than as a silently
// unrepresentable node kind at a consumer months later.
//
// The mirrored unions carry `[<RequireQualifiedAccess>]` because their
// case names are deliberately identical to the server union's and both
// live in the `ToolUp.Platform` namespace. Qualification is what keeps
// "identical by design" from becoming "ambiguous at every server-tier
// call site".

/// Wire mirror of the server `ProvenanceNodeKind` — which island a node
/// identifies. Case-for-case identical to the source; the conformance
/// test fails if that stops being true.
[<RequireQualifiedAccess>]
type WireProvenanceNodeKind =
    | DataObjectVersion
    | AnalysisResult
    | FactNode
    | NarrativeDocument
    | ConversationMessage
    | AnswerPlanNode
    | ModelArtifactNode
    | ProvenanceAttachmentNode

/// Wire mirror of the server `ProvenanceNode`. Field-for-field: the
/// underlying store identity, the kind, the disclosure class carried for
/// fact and artifact nodes, and the human label.
type WireProvenanceNode = {
    Id: string
    Kind: WireProvenanceNodeKind
    /// The node's disclosure annotation where one exists (a fact node's
    /// classification, an artifact node's lifecycle status). `None` for
    /// nodes that carry neither.
    Disclosure: string option
    Label: string
}

/// Wire mirror of the server `ProvenanceEdgeKind`. Directed child →
/// source exactly as the source union is, so an upstream walk follows
/// edges `From → To`.
[<RequireQualifiedAccess>]
type WireProvenanceEdgeKind =
    | DerivedFrom
    | EvidenceFor
    | CitesFact
    | Supersedes
    | PlannedBy
    | HasAttachment

/// Wire mirror of the server `ProvenanceEdge`.
type WireProvenanceEdge = {
    From: string
    To: string
    Kind: WireProvenanceEdgeKind
}

/// Wire mirror of the server `ProvenanceRef` — the typed root a query
/// starts from.
[<RequireQualifiedAccess>]
type WireProvenanceRef =
    | DataObjectRef of string
    | ResultRef of string
    | FactRef of string
    /// **A message root resolves to nothing on its own, by contract.**
    /// No provenance store holds "which facts did this message cite" —
    /// that is an assertion the answer made, not a fact about the message
    /// — so a walk seeded here yields the bare message node and no edges,
    /// and a `GetNode` on it reads `Absent`. Read that as "this root needs its cited
    /// facts", never as "no such message": root the chain at the facts
    /// the message cited instead (the server-side
    /// `IProvenanceGraph.GetChainForMessage` takes them as an argument and
    /// attaches the `CitesFact` edges).
    | MessageRef of string
    | ModelArtifactRef of string

module WireProvenanceRef =
    /// The underlying store identity a ref names, whatever its kind.
    let id (r: WireProvenanceRef) : string =
        match r with
        | WireProvenanceRef.DataObjectRef v
        | WireProvenanceRef.ResultRef v
        | WireProvenanceRef.FactRef v
        | WireProvenanceRef.MessageRef v
        | WireProvenanceRef.ModelArtifactRef v -> v

/// Wire mirror of the server `ProvenanceDirection`.
[<RequireQualifiedAccess>]
type WireProvenanceDirection =
    /// Toward sources — "where did this come from?".
    | Upstream
    /// Toward consumers — "what was built on this?".
    | Downstream

/// A node the disclosure plane refused, as it crosses the contract.
///
/// **Id and kind, never label or disclosure class.** The marker exists
/// so a caller can tell a suppressed node from a missing one and can
/// name what it could not read when it says so; the node's *content* is
/// exactly what the refusal withheld. This is the export door's shipped
/// posture — a rendered report's "Withheld values" section names the
/// reference and the policy and never the value — rather than the
/// federation door's, which suppresses the reference outright because a
/// counterparty learning that a fact *exists* is itself a disclosure. An
/// out-of-process consumer of this contract is inside the deployment's
/// trust boundary, so naming the refusal is the control demonstrably
/// working.
type WireWithheldNode = {
    /// The withheld node's underlying store identity — the same id the
    /// chain's edges reference, so chain *shape* survives the refusal.
    Id: string
    Kind: WireProvenanceNodeKind
    /// Why: the classification (`"Internal"`), the `Restricted` policy
    /// ref, `"unknown-fact"` for an id this scope cannot resolve, or a
    /// purpose ref for a claim outside the surface's allowed set.
    PolicyRef: string
}

/// The answer to a single-node lookup. Three outcomes, deliberately not
/// two: a caller must be able to distinguish "suppressed" from "no
/// provenance recorded".
[<RequireQualifiedAccess>]
type WireProvenanceNodeAnswer =
    /// The node, as the graph resolved it.
    | Found of WireProvenanceNode
    /// The node exists and the disclosure plane refused it.
    | Withheld of WireWithheldNode
    /// The graph produced no provenance for this ref at this scope —
    /// an unknown id, an id belonging to another scope (GP 4 makes the
    /// two indistinguishable by design), or a node with nothing
    /// recorded about it.
    | Absent

/// The edges incident on one ref, split by direction so a caller need
/// not re-derive which side of each edge its ref sat on.
type WireProvenanceEdgeSet = {
    /// The ref the edges were enumerated for.
    Ref: string
    /// Edges whose `From` is this ref — what it was derived from, cites,
    /// supersedes, or carries.
    Outgoing: WireProvenanceEdge list
    /// Edges whose `To` is this ref — what was derived from it, cites
    /// it, or supersedes it.
    Incoming: WireProvenanceEdge list
}

/// A bounded chain request: where to start, which way, how far.
type WireProvenanceChainRequest = {
    Root: WireProvenanceRef
    Direction: WireProvenanceDirection
    /// Hops to walk. Must be at least 1 and at most the declared
    /// `MaxDepth`; anything else is refused rather than clamped.
    Depth: int
}

/// One materialised chain, complete by construction.
///
/// "Page" names the unit a caller receives, not a window over a larger
/// result: this contract has no cursor and no `HasMore`, because the
/// alternative to refusing an over-cap walk is handing back a partial
/// chain, and a partial provenance chain answers the caller's question
/// wrongly rather than incompletely. Every node the walk reached is
/// here, either in `Nodes` or — where the disclosure plane refused it —
/// in `Withheld`.
type WireProvenanceChainPage = {
    /// The root id the chain is rooted at.
    Root: string
    /// Nodes the caller may read.
    Nodes: WireProvenanceNode list
    /// Every edge the walk found, including edges incident on a withheld
    /// node: the refusal seals content, it does not reshape the graph.
    Edges: WireProvenanceEdge list
    /// Nodes the disclosure plane refused, as typed markers.
    Withheld: WireWithheldNode list
    /// The depth the walk was bounded to — echoed so a caller reading a
    /// stored answer knows what bound produced it.
    Depth: int
}

/// The bounds a deployment's provenance surface declares. Read by
/// `GetCaps` so an out-of-process consumer can size its request instead
/// of discovering the limit as a refusal.
type WireProvenanceCaps = {
    /// The largest `Depth` a chain request may ask for.
    MaxDepth: int
    /// The largest number of nodes (readable plus withheld) a single
    /// chain answer may carry. A walk producing more is refused, never
    /// trimmed.
    MaxNodes: int
}

module WireProvenanceCaps =
    /// The shipped defaults. `MaxDepth` matches the depth the grounding
    /// certificate path already walks comfortably; `MaxNodes` is a
    /// response-size bound, not a modelling statement — a deployment
    /// whose chains are genuinely larger raises it at composition.
    let defaults = { MaxDepth = 10; MaxNodes = 2000 }

/// Why a chain request was refused. Every case names both what was
/// asked and what the limit is, so a caller can correct the request
/// without a second round-trip.
type ProvenanceQueryError =
    /// `Depth` was below 1. A zero-or-negative walk is a caller bug, not
    /// a request for the seed node.
    | ProvenanceDepthInvalid of requested: int
    /// `Depth` exceeded the declared `MaxDepth`.
    | ProvenanceDepthExceedsCap of requested: int * cap: int
    /// The walk completed and produced more nodes than `MaxNodes`
    /// allows. The answer is refused whole — nothing is truncated.
    | ProvenanceChainExceedsNodeCap of nodes: int * cap: int

module ProvenanceQueryError =
    /// Human-readable refusal text. One place, so a diagnostic, a test
    /// and a consumer's error surface all read the same wording.
    let describe (e: ProvenanceQueryError) : string =
        match e with
        | ProvenanceDepthInvalid requested ->
            sprintf "depth %d is invalid — a chain walk needs at least 1 hop" requested
        | ProvenanceDepthExceedsCap(requested, cap) ->
            sprintf "depth %d exceeds this deployment's provenance depth cap of %d" requested cap
        | ProvenanceChainExceedsNodeCap(nodes, cap) ->
            sprintf
                "the chain reached %d nodes, above this deployment's cap of %d — narrow the walk (a smaller depth, or a nearer root) rather than expecting a partial answer"
                nodes
                cap

/// The read-only provenance contract an out-of-process consumer binds.
///
/// **Every method is a query, and that is structural rather than
/// conventional.** There is no method that writes, and none returns
/// `unit` — a `unit` return is the shape a mutation takes, so its
/// absence is what a shipped test asserts over this record's fields. A
/// deployment therefore cannot expose a provenance write path by
/// composing this contract, whatever it wires behind it.
///
/// **Gate.** Every method carries `[<RequiresClaim "scope">]` — the
/// forge-conventional gate for a scope-owned surface that is neither
/// role-gated nor tenant-only, but never anonymous, matching
/// `IConfigApi` / `IReportApi`. Provenance answers describe how a
/// deployment's governed numbers were produced; an unauthenticated
/// caller must not reach them. That the caller can only see *its own*
/// scope is structural upstream (GP 4): the handler is built per
/// resolved scope and every underlying read is scope-bounded, so a ref
/// from another tenant is unresolvable and reads as `Absent`.
///
/// **Disclosure.** Fact nodes are checked through the one shipped
/// `IFactDisclosureGate` at the `FactExport` surface before they cross
/// — the same door a rendered export takes, never a second predicate.
/// A deployment with no fact tier composes no gate and pays nothing
/// (GP 13); its answers carry no withheld markers because nothing
/// classified them.
type IProvenanceQueryApi = {
    /// The bounds this deployment declares. Cheap and constant — a
    /// consumer reads it once at startup and sizes its walks.
    [<RequiresClaim "scope">]
    GetCaps: unit -> Async<WireProvenanceCaps>
    /// One node by ref: `Found`, `Withheld`, or `Absent`.
    ///
    /// **`Absent` on a `MessageRef` is not "no such message"** — a
    /// message root carries no provenance of its own on this contract.
    /// See `WireProvenanceRef.MessageRef`.
    [<RequiresClaim "scope">]
    GetNode: WireProvenanceRef -> Async<WireProvenanceNodeAnswer>
    /// The edges incident on a ref, split into outgoing and incoming.
    /// Edges touching a withheld node are still returned — chain shape
    /// is not a disclosure, the node's content is.
    [<RequiresClaim "scope">]
    GetEdges: WireProvenanceRef -> Async<WireProvenanceEdgeSet>
    /// A bounded walk from a starting ref. Refuses typed rather than
    /// truncating when the request or its result exceeds a declared cap.
    [<RequiresClaim "scope">]
    GetChain: WireProvenanceChainRequest -> Async<Result<WireProvenanceChainPage, ProvenanceQueryError>>
}

module ProvenanceQueryApi =
    /// Remoting endpoint prefix. Matches the pattern `IConfigApi` /
    /// `PlatformApi` use, so a consumer wiring this alongside them
    /// routes it identically.
    let routeBuilder (typeName: string) (methodName: string) = $"/api/{typeName}/{methodName}"