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

Edges are directed **child → source** (the derived node is `From`), so an
upstream walk follows `From → To`:

| `ProvenanceEdgeKind` | Meaning |
|---|---|
| `DerivedFrom` | a result/object was derived from a source object (lineage) |
| `EvidenceFor` | a fact is evidenced by the result it was computed from |
| `CitesFact` | a message/narrative cites a fact |
| `Supersedes` | a fact supersedes its lineage predecessor |

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

```fsharp
// Lineage only — data-object / result chains walk; fact + message nodes absent.
let graph = ProvenanceGraph.create lineageStore

// Full chain — an IFactEvidenceSource adapter over IFactStore supplies fact nodes.
let graph = ProvenanceGraph.createWithFacts lineageStore factEvidenceSource
```

## A worked walk

Given a pipeline where `res-1` was derived from data object `obj-1`, fact
`fact-2` was computed from `res-1` (superseding `fact-1`), and message
`msg-1` cited `fact-2`:

```fsharp
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

```fsharp
let! downstream = graph.GetChain(scopeId, DataObjectRef "obj-1", Downstream, depth = 5)
// obj-1 ◀──DerivedFrom── res-1 ◀──EvidenceFor── fact-2
```

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
