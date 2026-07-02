# Migration — Phase 14x KB document content-hash dedup

**Status.** Behavioural change, on by default. Re-uploading byte-identical content into the same Knowledge Base scope now returns the *existing* `KnowledgeDocument` (same `docId`) instead of ingesting a duplicate. A one-line opt-out restores the prior behaviour byte-for-byte.

## What changed

- `KnowledgeApi.UploadDocument` SHA-256-hashes the raw uploaded bytes before persisting anything. A scope-local match returns the existing document verbatim, skips ingestion entirely, emits a `KnowledgeDocumentDeduplicated` audit event, and publishes an Info `SystemMessage` toast to the uploader ("this document already exists in the knowledge base").
- `KnowledgeDocument` gained `ContentHash: string option` (last field). `None` for notes, narratives, and pre-14x documents; persisted `knowledge/index.json` blobs written before 14x deserialise leniently (`None`), so existing indexes survive the upgrade untouched.
- The hash is registered in a per-scope Phase 9f `BlobIndex` at `_platform/kb-content-hash/{hash}/{docId}.ref` for O(1) lookup; `DeleteDocument` removes the ref alongside the document.
- New compose lever: `KnowledgeBase.Server.withDocumentDedup : bool -> ServerApp -> ServerApp`.

## Per-consumer diff

**Nothing required** if idempotent uploads are acceptable (they almost always are — this is the "stop corpus bloat" fix).

**Opt out** if your deployment relies on byte-identical re-uploads creating distinct documents (e.g. contract revisions uploaded as separate records):

```fsharp
// Composition root — before ServerApp.run / AIServerApp.run / RAGServerApp.run:
let app =
    ServerApp.create config
    |> KnowledgeBase.Server.withDocumentDedup false   // pre-14x behaviour, byte-for-byte
```

**If you construct `KnowledgeDocument` values directly** (test doubles, custom KB tooling): add `ContentHash = None` to the construction. Copy-update (`{ doc with ... }`) sites are unaffected.

## Verification

1. Upload the same PDF twice into one scope → the document list shows one entry; the second call returned the first `docId`.
2. The audit trail shows one `KnowledgeDocumentDeduplicated` row for the second upload.
3. Retrieval results are identical before and after the deduplicated upload.
4. With `withDocumentDedup false`: two uploads → two documents, no dedup audit row, `ContentHash = None` on both.

## Rollback

Compose `withDocumentDedup false`. Already-stamped `ContentHash` values and `_platform/kb-content-hash/` refs are inert while the lever is off (never read, never written).
