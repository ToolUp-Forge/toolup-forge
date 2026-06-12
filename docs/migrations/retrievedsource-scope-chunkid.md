# `RetrievedSource` lineage widening — `Scope` + `ChunkId`

**Ships in:** ToolUp.Platform.Core (shared wire type `ToolUp.Platform.VectorKnowledgeTypes.RetrievedSource`).

## What changes

`RetrievedSource` — the wire record the AI client renders in its Sources panel,
attached to every assistant `ConversationMessage` — gains two additive fields:

- `Scope: VectorScope option` — the scope the chunk was retrieved from, so a
  Sources panel can render a Platform-vs-Deployment-vs-Team authority badge
  (previously the authority label existed only as prose inside the system
  prompt).
- `ChunkId: string option` — the vector-store chunk id, a stable join key
  between a source and the `[¹]` citation markers in the reply (markers are
  positional and survive `CitationNormaliser` rewrites only by index).

Both are `Some` on every source produced after the upgrade. `None` appears only
when replaying conversation history persisted before the widening — the same
GP 11 pattern as Phase 103's `OriginalRef` field (missing wire fields absorb to
`None`; a renderer omits the badge rather than guessing, GP 9).

## Diff to apply

Nothing, for most consumers. Reading code (`source.DocumentName`,
`source.Snippet`, …) and `RetrievedSources = []` list literals are unaffected.

The only break is consumer code that **constructs a `RetrievedSource` record
literal** (typically tests). Add the two fields:

```fsharp
// Before
{ DocumentId = "doc-1"; DocumentName = "report.pdf"; Snippet = "…"
  Score = 0.9; Origin = ChunkOrigin.Document; LocationHint = None
  OriginalRef = None }

// After
{ DocumentId = "doc-1"; DocumentName = "report.pdf"; Snippet = "…"
  Score = 0.9; Origin = ChunkOrigin.Document; LocationHint = None
  OriginalRef = None; Scope = None; ChunkId = None }
```

(Use `Some scope` / `Some chunkId` where the test asserts on lineage.)

## Verification

- `dotnet build` — surfaces any record-literal construction sites.
- Open an AI conversation with RAG enabled and confirm the Sources panel still
  renders; new turns carry `Scope` / `ChunkId` on the wire (inspect the
  `/api/ai/events` SSE payload or `GetConversation` response).
- Reload a conversation recorded before the upgrade: sources render, both new
  fields deserialise as `None`, no client error.

## Rollback

Revert the SDK version pin. Persisted conversations written by the widened
version carry the two extra JSON fields; the prior deserialiser ignores unknown
fields, so rollback is wire-safe.
