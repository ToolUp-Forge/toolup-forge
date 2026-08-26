// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

// ─── Provenance chain traversal (Phase 524) ──────────────────────────
//
// Lineage records (Phase 8a), analysis-run audit events (Phase 9), fact
// evidence refs (Phase 520), and retrieved sources exist as islands.
// `IProvenanceGraph` stitches them into one **read-only walkable graph** —
// ingestion → run → fact → narrative → cited answer — so "trace this
// sentence back to the CSV row" is one call. It is a **view**: no new
// persistence, the underlying stores stay authoritative (GP 13 —
// composing a graph over stores a deployment already has costs nothing
// until a caller asks).
//
// **Fact evidence via a seam, not a companion dependency.** The graph
// lives in `ToolUp.Platform.Server`, which cannot depend on the
// `ToolUp.Facts` companion (that would invert the dependency). Fact nodes
// therefore arrive through the `IFactEvidenceSource` seam — a generic
// interface a fact-store adapter fills (the forge pattern: interface in
// Platform, implementation in the companion). Absent a source, the graph
// still walks the data-object / result lineage; fact and message nodes
// simply do not appear.

/// The kind of island a `ProvenanceNode` identifies. Each maps to an
/// existing store's identity — no new id scheme (Phase 524.A).
type ProvenanceNodeKind =
    /// A versioned data object (`IDataObjectStore` — an ingested / derived
    /// payload version).
    | DataObjectVersion
    /// A persisted analysis result (`IResultStore`).
    | AnalysisResult
    /// A verified fact (`IFactStore`).
    | FactNode
    /// A committed narrative document (KB).
    | NarrativeDocument
    /// A conversation message that cited grounded sources.
    | ConversationMessage
    /// A recorded answer plan (Phase 560) — the typed
    /// question-to-triples resolution artifact a grounded answer was
    /// produced from ("EXPLAIN for answers"). Identity is the plan id;
    /// the plan body lives in the fact companion's audit trail, this
    /// node carries its place in the chain.
    | AnswerPlanNode
    /// Phase 646 — a governed model artifact (`IModelRegistry`). Identity
    /// is its composite-key hash, the id every registry read already keys
    /// on; the node carries its place in the chain, not its parameters.
    | ModelArtifactNode
    /// Phase 646 — one opaque provenance attachment on a model artifact.
    /// Identity is the attachment's content hash and the label is its
    /// declared media type. **The content is never interpreted, only
    /// cited** — which is the whole reason an attachment can be a chain
    /// node at all: a walk that had to understand an exploration record to
    /// place it would make every producing tool's schema a dependency of
    /// this graph.
    | ProvenanceAttachmentNode

/// One node in a provenance chain — an existing store identity, its kind,
/// an optional disclosure class (populated for fact nodes, Phase 524.D),
/// and a human label. Identity is by value (`Id`), reusing the underlying
/// store's id — no new id scheme.
type ProvenanceNode = {
    /// The underlying store identity (data-object version id, result id,
    /// fact id, document id, message id).
    Id: string
    Kind: ProvenanceNodeKind
    /// Disclosure class for a fact node (`Surfaceable` / `Internal` /
    /// `Restricted(policy)`), carried so a future egress-side renderer can
    /// seal restricted node *contents* while preserving chain *shape*
    /// (plan D16 — sealing itself lands with the export work, not here).
    /// `None` for non-fact nodes.
    Disclosure: string option
    Label: string
}

/// A typed edge relating two provenance nodes. Directed child → source
/// (the derived / downstream node is `From`, the thing it came from is
/// `To`), so an upstream walk follows edges `From → To`.
type ProvenanceEdgeKind =
    /// `From` (a result / object) was derived from `To` (a source object)
    /// — a lineage edge.
    | DerivedFrom
    /// `From` (a fact) is evidenced by `To` (the analysis result it was
    /// computed from).
    | EvidenceFor
    /// `From` (a message / narrative / answer plan) cites `To` (a fact).
    | CitesFact
    /// `From` (a fact) supersedes `To` (its lineage predecessor).
    | Supersedes
    /// `From` (a conversation message — a grounded answer) was produced
    /// from `To` (its recorded answer plan, Phase 560).
    | PlannedBy
    /// Phase 646 — `From` (a model artifact) carries `To` (an opaque
    /// provenance attachment) as attached evidence.
    ///
    /// Directed the same way every other edge here is: the artifact is the
    /// downstream thing, the attachment is what it came with, so an
    /// upstream walk from a published number reaches the exploration
    /// record that produced the model — with no reference to the
    /// deployment that built it.
    | HasAttachment

type ProvenanceEdge = {
    From: string
    To: string
    Kind: ProvenanceEdgeKind
}

/// A materialised provenance chain rooted at one node. `Nodes` and
/// `Edges` are deduplicated; a caller wanting order sorts itself.
type ProvenanceChain = {
    Root: string
    Nodes: ProvenanceNode list
    Edges: ProvenanceEdge list
}

/// Which way to walk from the root.
type ProvenanceDirection =
    /// Toward sources — "where did this come from?" (message → fact →
    /// result → data).
    | Upstream
    /// Toward consumers — "what was built on this?" (data → result → fact
    /// → message).
    | Downstream

/// A reference to a node to start a walk from — the typed root of a
/// `GetChain`.
type ProvenanceRef =
    | DataObjectRef of string
    | ResultRef of string
    | FactRef of string
    | MessageRef of string
    /// Phase 646 — a governed model artifact, by composite-key hash. The
    /// root a grounding certificate cites when the number it certifies came
    /// out of a model rather than out of a query.
    | ModelArtifactRef of string

/// The fact-evidence a fact node contributes to the chain — the seam the
/// graph reads instead of depending on `IFactStore`. A fact-store adapter
/// (in the `ToolUp.Facts` companion, or any consumer) implements it.
type FactEvidence = {
    FactId: string
    /// Readable subject reference.
    Subject: string
    Metric: string
    /// Disclosure class string (`Surfaceable` / `Internal` /
    /// `Restricted(policy)`).
    Disclosure: string
    /// The analysis-result id this fact was computed from (`EvidenceFor`
    /// edge), when any.
    ResultRef: string option
    /// Content hashes of the input data-object versions (`DerivedFrom`
    /// edges), so a fact's chain includes the data it read.
    InputHashes: string list
    /// The fact id this one superseded (`Supersedes` edge), when any.
    Supersedes: string option
}

// ─── Phase 646 — model-artifact provenance via the same seam shape ───
//
// The graph compiles ahead of `IModelRegistry` (it is a view over lineage,
// and the registry is a store built on top of it), so it reads an
// artifact's provenance through an interface a registry adapter fills —
// the shape `IFactEvidenceSource` already established, for the same
// reason and with the same consequence: absent a source, the walk simply
// omits artifact and attachment nodes and everything else behaves as it
// always did.
//
// **Strings, not `ProvenanceAttachment`.** The attachment type is declared
// with the registry, above this file; carrying the bytes here would also be
// wrong on its own terms, because a chain node CITES an attachment and a
// walk that materialised every payload would turn "show the working" into
// a bulk download.

/// One opaque provenance attachment, as the chain cites it: its content
/// hash and its declared media type, and nothing else.
type ProvenanceAttachmentRef = {
    /// `sha256:<lowercase hex>` over the attachment's bytes — the node id.
    ContentHash: string
    /// The declared media type, carried as the node's label so a reader
    /// knows what the citation points at without the graph interpreting it.
    MediaType: string
}

/// What a model artifact contributes to a chain: its lifecycle status, the
/// dataset vintage it was fit against (the lineage hop the walk continues
/// through), and the attachments it carries.
type ArtifactProvenance = {
    /// The artifact's composite-key hash.
    ArtifactKey: string
    /// Stable `ModelArtifactStatus` label, carried as the node's disclosure
    /// annotation so a chain renderer can distinguish an approved evidence
    /// base from a retired one without a second read.
    Status: string
    /// The `{scopeId}/{datasetId}@v{version}` key the fit read. `""` when
    /// unknown; a `DerivedFrom` edge is emitted only for a non-empty one.
    DatasetVersion: string
    /// The attachments, cited by hash + media type.
    Attachments: ProvenanceAttachmentRef list
}

/// Read-only seam over the model-artifact registry, scope-bounded (GP 4).
/// A registry adapter (`ModelArtifactProvenance.source`) fills it; the
/// provenance graph composes over it without depending on the registry.
/// Absent (not composed), the graph omits artifact and attachment nodes.
type IArtifactProvenanceSource =
    /// The provenance of one artifact, or `None` when the scope holds no
    /// artifact with that key.
    abstract GetArtifact: scopeId: string * artifactKey: string -> Async<ArtifactProvenance option>

/// Read-only seam over a fact store, scope-bounded (GP 4). A fact-store
/// adapter fills it; the provenance graph composes over it without a
/// companion dependency. Absent (not composed), the graph omits fact
/// nodes.
type IFactEvidenceSource =
    /// The evidence for one fact, or `None`.
    abstract GetFact: scopeId: string * factId: string -> Async<FactEvidence option>
    /// Every fact whose evidence is the given result (the downstream
    /// "what facts were computed from this result" step). Empty when none.
    abstract FactsForResult: scopeId: string * resultId: string -> Async<FactEvidence list>

/// Read-only provenance chain traversal — a view over `ILineageStore` +
/// (optionally) `IFactEvidenceSource`. Scope-bounded throughout (GP 4): a
/// chain never crosses a team scope.
type IProvenanceGraph =
    /// Walk the provenance chain from `root` in `direction`, bounded to
    /// `depth` hops. Every node is one of the typed `ProvenanceNodeKind`s
    /// and every edge one of the typed `ProvenanceEdgeKind`s.
    ///
    /// **A `MessageRef` root returns only its own seed node, by
    /// contract** — no edges, nothing annotated onto it. Every
    /// other root names something a store answers for; nothing here holds
    /// "which facts did this message cite" — that is an assertion the
    /// answer made, and it is the `citedFactIds` argument of
    /// `GetChainForMessage` below. Use that for a message root; this
    /// method's empty answer is honest, not a gap.
    abstract GetChain:
        scopeId: string * root: ProvenanceRef * direction: ProvenanceDirection * depth: int -> Async<ProvenanceChain>

    /// The answer-side hook (Phase 524.C): from a conversation message and
    /// the fact ids it cited, resolve the upstream chain in one call — the
    /// "show the working" API a client surface or export renders. The
    /// message is the root; each cited fact is a `CitesFact` edge whose
    /// upstream is then walked.
    abstract GetChainForMessage:
        scopeId: string * messageId: string * citedFactIds: string list * depth: int -> Async<ProvenanceChain>