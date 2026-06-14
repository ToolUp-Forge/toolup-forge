# Phase 115 — Unified index-lifecycle seam (`IIndexLifecycle`) (consumer migration)

**What changes.** Deletion and data-subject erasure now fan out across **every** retrieval index — vector store, BM25 sparse index, embedding cache — through one seam (`IIndexLifecycle`, registered by `composeWithRAG`). Pre-115, KB deletion paths reached the vector store only: deleted/erased content kept surfacing through the hybrid sparse leg into AI answers and remained at rest in `_rag/{scope}/bm25.json` (which also survived `DeleteByScope` across restarts). KB's `deleteDocument` ordering is fixed too: index entries are removed only after a clean index deletion, so a partial failure leaves the document listed and retryable instead of orphaned-and-invisible.

**Scope.** `ToolUp.Platform.Server` (new interface + default impl, `ISparseIndex` widening, erasure-handler fan-out), `ToolUp.RAG.Server` (BM25 persistence fix, DI registration), `ToolUp.KnowledgeBase.Server` (deletion-path routing). No client change. No wire change.

**Backward compatibility.**

- **Stock consumers:** nothing to do. Upgrade and the seam is registered + consumed automatically.
- **Custom `ISparseIndex` implementors** (companion lexical indexes): the interface gains `Erase(scope, subjectUserId, policy, dryRun)` — a **source-breaking** addition (0.x minor per the SemVer posture). Implement with the same matching contract as `IVectorStore.eraseSubject` (subject id appears in chunk content or any metadata value); if your index has no tombstone tier, hard-delete and say so in the returned summary. `InMemoryBM25Index` is the reference.
- **Custom `KnowledgeApiDeps` construction sites** (test fixtures): the record gains `IndexLifecycle: IIndexLifecycle option` — add `IndexLifecycle = None` (or a `DefaultIndexLifecycle` wrapper).
- **`VectorStoreErasureHandler`**: constructor gains an optional `?sparseIndex` (source-compatible). Deployments that wire the DSR erasure handler manually should switch to `VectorStoreErasureHandler.erasureHandlerHybrid vectorStore embeddingCache sparseIndex` so erasure covers the sparse leg.
- **Behavioural:** `deleteDocument` now returns `Error` (document still listed) when index deletion partially fails — previously it reported `Ok` and left invisible orphans. `bm25.json` is deleted from blob storage when its scope is deleted.
- **New audit event (`KnowledgeScopeErased`):** a KB scope wipe via `ResetIndex` now emits a structured `AuditEvent.KnowledgeScopeErased` row (actor, scope, document count, surviving-chunk count) in addition to the dispatcher's generic `Custom:KnowledgeIndexReset` action row — so a half-completed fan-out is loud in the audit trail (GP 6 + GP 9). Additive and registered in the Phase 114 codec registry: stock consumers (and the SDK audit replicator / SIEM sinks that decode via the registry) need nothing. Custom `IAuditSink`s that pattern-match `AuditEvent` exhaustively gain one new case to handle (or fall through their existing default).

## Diff to apply

Stock consumers: none. Manual DSR-handler wiring:

```fsharp
// Before (sparse leg not erased):
ServerApp.withErasureHandler (VectorStoreErasureHandler.erasureHandler vectorStore embeddingCache)

// After:
ServerApp.withErasureHandler (VectorStoreErasureHandler.erasureHandlerHybrid vectorStore embeddingCache sparseIndex)
```

## Verification

1. `dotnet build ToolUp.Forge.sln` (clean tree) — green.
2. Manual: upload a KB document, confirm a hybrid retrieval hit, delete the document → both retrieval legs miss immediately; restart the server → still miss; `_rag/{scope}/bm25.json` no longer contains the deleted text.
3. DSR erasure on a subject named in KB content → vector store, sparse index, and persisted snapshot all shed the matching chunks; embedding cache flushed.
4. Force a chunk-delete failure mid-delete → the document stays listed, the API returns a retryable error, and the log carries a survivor summary.

## Rollback

Revert forge commit `2f25eff`. No data migration (the `bm25.json` blob-delete is forward-only but harmless — a reverted server simply rewrites the snapshot on next flush).
