# Phase 867 — ingestion jobs carry their attempt

**Who is affected:** code that constructs `DocumentIngestionJob`, `IngestionJob` or
`IngestionRetryPayload` with a full record literal, or that read
`KnowledgeBase.ServerIndexStorage.progressCache`. Deployments that only compose the SDK's knowledge
base and RAG companions need no change.

## What changed

- `ToolUp.RAG.IngestionTypes` gains `IngestionAttempt = { AttemptId: string; EnqueuedChunks: int }`,
  and the three job records gain `Attempt: IngestionAttempt option`. A record literal that omits it
  no longer compiles (FS0764).
- The knowledge base's ingestion observer completes a document against the attempt's
  `EnqueuedChunks` (an incremental re-index enqueues fewer chunks than the document holds), keeps
  its progress in the persisted document status rather than a per-process counter, treats
  `Complete` and `Failed` as terminal for the attempt, and drops a callback whose attempt a newer
  enqueue of the same document has superseded. `Complete` still reports the document's full chunk
  count; the `KnowledgeBase.IngestionStatus` notification is unchanged.
- The public `progressCache` is removed; there is no per-process counter left to read or clear.
- `classifyIndexFailure` classifies `EmbeddingProviderUnavailableException` (a 401 / 403 from an
  API-backed embedding provider) as `Permanent` wherever it sits in the exception chain, so a
  revoked key is dead-lettered at once and raises the Owner/Admin alert instead of being retried.

## Migrating

A producer that does not track attempts adds one field:

```fsharp skip=fragment
let job: DocumentIngestionJob = {
    DocumentId = documentId
    DocumentName = "q3-strategy.md"
    Chunks = chunks
    Scope = Team teamId
    ScopeId = teamId
    Container = $"team-{teamId}"
    OriginatingUserId = None
    Attempt = None // completion then falls back to the document's own chunk count
}
```

A producer that enqueues knowledge-base documents calls
`KnowledgeBase.ServerIngestionObserver.beginIngestionAttempt storage logger container docId
enqueuedCount` **before** the enqueue and passes `Attempt = Some attempt`. Code that called
`progressCache.TryRemove docId` deletes the call; when a document is removed outside
`deleteDocument`, call `forgetIngestionAttempt storage container docId` alongside `clearStatus`.

Jobs and retry payloads persisted before the upgrade read back with `Attempt = None` and complete
as they would have before.

## Verifying

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 867"`
runs the pins: an incremental re-index reaches `Complete`, a failure is not overwritten by a later
success, a superseded attempt's callbacks are dropped, and a typed credentials rejection is permanent
and alerts.

## Rollback

Revert the Phase 867 commit. Earlier versions ignore a `knowledge/{docId}/ingestion-attempt.json`
record left behind; they do not delete it with its document, so remove the
`knowledge/*/ingestion-attempt.json` blobs by hand if that matters (a scope reset removes them).
