# Provenance chain traversal

*Phase 524.* Lineage records, analysis-run events, fact evidence refs, and
retrieved sources exist as **islands**. `IProvenanceGraph` stitches them
into one read-only, walkable graph — *ingestion → run → fact → narrative →
cited answer* — so "trace this sentence back to the CSV row" is a single
call.

It is a **view**: no new persistence, the underlying stores stay
authoritative. Composing the graph over stores a deployment already has
costs nothing until a caller asks (GP 13).

## The graph

`ProvenanceNode` reuses each island's existing identity (no new id scheme):

| Kind | Identity | Store |
|---|---|---|
| `DataObjectVersion` | data-object version id | `IDataObjectStore` |
| `AnalysisResult` | result id | `IResultStore` |
| `FactNode` | fact id | `IFactStore` (via the seam below) |
| `NarrativeDocument` | document id | KB |
| `ConversationMessage` | message id | conversation store |
| `AnswerPlanNode` | answer-plan id | the recorded question-to-triples resolution a grounded answer was produced from |
| `ModelArtifactNode` | composite-key hash | `IModelRegistry` (via the artifact seam) |
| `ProvenanceAttachmentNode` | attachment content hash | an opaque attachment on a model artifact — **cited, never interpreted** |

Edges are directed **child → source** (the derived node is `From`), so an
upstream walk follows `From → To`:

| `ProvenanceEdgeKind` | Meaning |
|---|---|
| `DerivedFrom` | a result/object was derived from a source object (lineage) |
| `EvidenceFor` | a fact is evidenced by the result it was computed from |
| `CitesFact` | a message/narrative cites a fact |
| `Supersedes` | a fact supersedes its lineage predecessor |
| `PlannedBy` | a grounded answer was produced from its recorded answer plan |
| `HasAttachment` | a model artifact carries an opaque provenance attachment as evidence |

Fact nodes carry the fact's **disclosure class** (`Surfaceable` /
`Internal` / `Restricted(policy)`), so a future egress-side renderer can
seal restricted node *contents* while preserving chain *shape* (plan D16 —
the sealing itself lands with the export work, not here).

## Composing over the fact store — the seam

`IProvenanceGraph` lives in `ToolUp.Platform.Server`, which cannot depend on
the `ToolUp.Facts` companion (that would invert the dependency). Fact nodes
therefore arrive through the generic **`IFactEvidenceSource`** seam — a
fact-store adapter fills it (interface in Platform, implementation in the
companion). Compose it two ways:

```fsharp skip=fragment
// Lineage only — data-object / result chains walk; fact + message nodes absent.
let graph = ProvenanceGraph.create lineageStore

// Full chain — an IFactEvidenceSource adapter over IFactStore supplies fact nodes.
let graph = ProvenanceGraph.createWithFacts lineageStore factEvidenceSource
```

## A worked walk

Given a pipeline where `res-1` was derived from data object `obj-1`, fact
`fact-2` was computed from `res-1` (superseding `fact-1`), and message
`msg-1` cited `fact-2`:

```fsharp skip=fragment
// "Show the working" for an answer — from the message to the CSV row:
let! chain = graph.GetChainForMessage(scopeId, "msg-1", citedFactIds = [ "fact-2" ], depth = 5)
```

The chain is rooted at `msg-1` and reaches `obj-1`:

```
msg-1 ──CitesFact──▶ fact-2 ──EvidenceFor──▶ res-1 ──DerivedFrom──▶ obj-1
                        │
                        ├──DerivedFrom──▶ obj-1        (the fact's input data)
                        └──Supersedes───▶ fact-1       (the prior value)
```

Walk the other direction — "what was built on this data?":

```fsharp skip=fragment
let! downstream = graph.GetChain(scopeId, DataObjectRef "obj-1", Downstream, depth = 5)
// obj-1 ◀──DerivedFrom── res-1 ◀──EvidenceFor── fact-2
```

## Out-of-process consumers — the read-only wire contract

`IProvenanceGraph` is a **server-tier** view. A consumer running in
another process — a document-emission tier binding governed results, an
audit tool, a publication surface citing artifacts — cannot reach it, and
before the contract below every such deployment hand-rolled an endpoint.
Each hand-rolled endpoint is a second place the disclosure rules get
decided, which is precisely what the one-gate design exists to prevent.

**`IProvenanceQueryApi`** (`ToolUp.Platform.Core`) is the shipped surface.
Four methods, all queries:

| Method | Answers |
|---|---|
| `GetCaps` | the depth + node bounds this deployment declares |
| `GetNode` | one node by ref — `Found`, `Withheld`, or `Absent` |
| `GetEdges` | the edges incident on a ref, split `Outgoing` / `Incoming` |
| `GetChain` | a bounded walk from a starting ref |

Its records are **Fable-safe mirrors** of the server records above —
`WireProvenanceNode` / `WireProvenanceEdge` / `WireProvenanceRef` /
`WireProvenanceDirection`, case-for-case and field-for-field. The server
types stay the source; the mirror is a pinned snapshot, and a conformance
test fails the build the moment a case is added on one side only. The
mirrored unions carry `[<RequireQualifiedAccess>]` because their case
names are deliberately identical to the server union's and both live in
the `ToolUp.Platform` namespace.

### Wiring it

Consumer-wired, like `IReportApi` — nothing composes it for you, so a
deployment that does not want an out-of-process provenance surface builds
none of it (GP 13). Mount the handler in your composition root, per
resolved scope:

```fsharp skip=fragment
// No fact tier: every node the walk reaches crosses as it stands.
let api = ProvenanceApiHandler.create graph WireProvenanceCaps.defaults scopeId

// With the fact tier: fact nodes are checked at the FactExport door,
// and `principal` is the caller the gate audits denies against.
let api =
    ProvenanceApiHandler.createWithDisclosureGate gate principal graph WireProvenanceCaps.defaults scopeId
```

Every method carries `[<RequiresClaim "scope">]` — the gate for a
scope-owned surface that is never anonymous, the same one `IConfigApi`
and `IReportApi` apply. That a caller can only see *its own* scope is
structural upstream (GP 4): the handler is built per resolved scope, so a
ref from another tenant is unresolvable and reads as `Absent`.

### Read-only by construction

There is no write member, and no member answers with `unit` — a `unit`
answer is the shape a mutation takes. That is asserted by a reflection
test over the contract's fields rather than left to review, so a
provenance write path cannot be added by accident.

### Bounded, and refused rather than truncated

Depth is checked before the walk and node count after it, both against
the declared `WireProvenanceCaps`. A request that exceeds either is
refused with a typed `ProvenanceQueryError` naming both the request and
the cap:

```fsharp skip=fragment
match! api.GetChain { Root = WireProvenanceRef.FactRef factId
                      Direction = WireProvenanceDirection.Upstream
                      Depth = 5 } with
| Ok page -> render page          // complete by construction
| Error e -> report (ProvenanceQueryError.describe e)
```

There is no cursor and no `HasMore`. The alternative to refusing an
over-cap walk is handing back a partial chain — and a caller cannot tell
a shortened chain from a complete one, so a partial provenance answer
does not degrade its conclusion, it falsifies it.

### A withheld node is not an absent one

Fact nodes route through the one shipped `IFactDisclosureGate` at the
**`FactExport`** surface — the same door a rendered export takes, never a
second predicate. The grounding-certificate path already materialises a
chain and filters it at that surface; this is the same act answered live
rather than signed.

A refused node crosses as a `WireWithheldNode` carrying its **id, its
kind and the policy ref** — never its label or its value — and its edges
stay in the chain. Chain *shape* is not a disclosure; the node's content
is. So a consumer can say "this number's provenance includes something I
may not read, refused under policy X" instead of reporting a hole.

That is the export door's posture rather than the federation seam's,
which suppresses a denied reference outright. The difference is the trust
boundary: a consumer of this contract sits inside the deployment's, so
naming the refusal shows an operator the control working, where telling a
counterparty that an invisible fact *exists* would itself be the
disclosure.

`Absent` is reserved for the genuinely different answer — an unknown id,
an id belonging to another scope, or a ref with nothing recorded about
it. Collapsing the two would turn a working control into apparent missing
data.

Every deny is audited by the gate itself (GP 6), in the trail the
retrieval, tool-result, publication, export and webhook doors already
write to. This surface adds a door; it adds no bookkeeper. A deployment
with no fact tier composes no gate, so its answers carry no withheld
markers — nothing classified them (GP 11 / GP 13).

## Guarantees

- **Read-only.** No write surface, no new store — the graph recomputes
  from the authoritative stores per call (GP 3).
- **Scope-bounded (GP 4).** Every underlying read takes `scopeId`; a chain
  never crosses a team scope. Querying `obj-1`'s chain under a different
  scope returns nothing of the original.
- **Cycle-safe + bounded.** A `visited` set prevents re-expansion; `depth`
  bounds the fact / message hops.
- **Default composition unchanged (GP 13).** A deployment that never
  constructs a graph builds nothing.

The signed-export / certificate work (plan D11/D16) is a future consumer of
this traversal — this phase is the read substrate underneath it.
