# Phase 173 + 220 — Data Manager ingestion-status badges, retry & status filter

**Status:** additive, opt-in, default-off. **No consumer action required.**

## What changed

Per-file RAG **ingestion status** is now surfaced on both Data Manager file
lists — the built-in `FileManagerUI` and the mapping-aware
`MappingDataManagerUI` — closing the asymmetry with Knowledge Base, which
already shows a per-document status badge.

- **Phase 173 — badges.** A new `FileIngestionStatus` DU (`NotIngested` /
  `Pending` / `Indexed` / `Failed of reason`) in `ToolUp.Platform.Core`. The
  post-save vectorisation hook marks each Data Manager file `Pending` on
  enqueue and `Failed reason` on the skip / reject / drop arms; a new
  `DataManagerIngestionObserver` (RAG) marks it `Indexed` once the last chunk
  lands. The status is persisted by a new platform-tier `IIngestionStatusStore`
  (default: an `IDataObjectStore`-backed sidecar, in-memory fallback), joined
  onto the `FileListSnapshot` via a new additive `Ingestion` field, and updated
  live over the existing notification channel
  (`DataManagerIngestionStatusKey`).

- **Phase 220 — retry & filter.** `FileManagementApi.RetryIngestion: string ->
  Async<Result<unit,string>>` re-fires the post-save hooks for a single
  `Failed` file (idempotent against an in-flight `Pending`). A "Retry" control
  appears on `Failed` rows; a status-filter dropdown narrows the list
  client-side. New pure predicates `FileIngestionStatus.isRetryable` /
  `matchesFilter` drive both.

## Do I need to do anything?

**No.** The whole surface is gated on a `VectorisationHandler` being composed
(i.e. RAG). A deployment with no RAG composes no `IIngestionStatusStore`, so
`FileListSnapshot.Ingestion` is `[]`, the status column / retry / filter do not
render, and the file list is byte-for-byte unchanged (GP 11 / GP 13).

- **Composing RAG (`RAGServerApp.run` / `withRAG`):** you get the badges, live
  updates, retry, and filter for free — no wiring. The store + observer are
  registered automatically inside the RAG compose path.
- **Not composing RAG:** nothing changes.

### Edge cases

- `FileListSnapshot` gained a third field (`Ingestion`). The SDK is the only
  producer; if you construct a `FileListSnapshot` literal in your own code or
  tests, add `Ingestion = []`.
- A custom `FileManagementApi` implementation (rare) must add the
  `RetryIngestion` member; the default handler already implements it.

## Verify

```
dotnet build ToolUp.Forge.sln
dotnet run --project Build.fsproj -- VerifyAll   # IngestionStatusTests + IngestionRetryTests green
cd samples/MinimalClient && dotnet fable -o output --noCache
```

## Rollback

Revert the Wave 26 commits. The change is additive, so reverting restores the
prior file list with no data migration (the `_ingestionstatus__` sidecars are
inert once the reader is gone).
