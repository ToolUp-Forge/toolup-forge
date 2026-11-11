# 1087 — `KnowledgePolicies`, the hook over `VectorisationDeps`, and the deletions

**Class:** breaking. Rides 0.42.0. The codemod covers the mechanical renames and lists every other site:
`dotnet run --project Build.fsproj -- Codemod <dir> --phase 1087`.

## The knowledge base's policies are one record

`KnowledgePolicies` (KnowledgeBase.Core, `SharedTypes`) holds every compose-time policy the knowledge base reads:

```fsharp skip=signature
type KnowledgePolicies = {
    Upload: KnowledgeUploadPolicy
    Dedup: bool
    Versioning: bool
    ObjectRetention: bool
    Quota: KnowledgeQuotaPolicy
    Retention: KnowledgeRetentionPolicy
    Scan: ContentScanPolicy
    DocxMode: DocxExtractionMode
    Archive: ArchiveImportPolicy
    Url: UrlIngestionPolicy
}
```

`KnowledgePolicies.defaults` is what a deployment that composed no policy reads, and it is the behaviour every
deployment had before: uploads uncapped, dedup on, versioning and object retention off, no quota, retained forever,
scanning fail-closed, the flat DOCX extractor, the shipped archive guards, URL ingestion inert.

- **The builders keep their names.** `withUploadPolicy`, `withDocumentDedup`, `withDocumentVersioning`,
  `withObjectStoreRetention`, `withKnowledgeQuota`, `withKnowledgeRetention`, `withContentScanning`,
  `withArchiveImportPolicy` and `withUrlIngestion` each set their field on the one registered record. The record is
  updated in place, so every other registration lands in the order it did (pinned by `KnowledgeUploadPolicyTests`).
- **Removed:** `KnowledgeDedupPolicy`, `KnowledgeVersioningPolicy`, `KnowledgeObjectRetentionPolicy` and their
  `enabled` / `disabled` values. A consumer that registered one directly registers a `KnowledgePolicies` (or calls the
  builder). `KnowledgeObjectRetentionPolicy.ObjectDataType` is `KnowledgePolicies.OriginalObjectDataType`.
- **`KnowledgeApiDeps`** carries `Policies: KnowledgePolicies` in place of `UploadPolicy`, `DedupPolicy`,
  `VersioningPolicy`, `QuotaPolicy`, `RetentionPolicy`, `ScanPolicy`, `ArchiveImportPolicy` and `UrlIngestionPolicy`,
  and `Queue` is an `IngestionQueue option` (`None` when no RAG tier is composed). A test that builds the record in
  full sets `Policies = { KnowledgePolicies.defaults with … }`.
- **The DOCX mode is a policy.** `DocxExtractionMode` moves to KnowledgeBase.Core. The module-level setters
  `DocxExtraction.enableStructuredDocxExtraction` / `resetToFlatDocxExtraction` and the module are deleted: compose
  `KnowledgeBase.ServerUploadPolicy.withDocxExtraction DocxExtractionMode.Structured`. An extraction outside the API
  passes the mode: `ExtractDeps.composed ocr tables mode`, and `extractChunks` takes an `ExtractDeps`.
- `resolveFrom` reads every service through `ServiceComposition.tryService`; no hand-written probe is left.

## A corrupt index refuses

`IndexStorage.loadIndex storage container` returns `Result<KnowledgeDocument list, IndexReadError>`. An absent index
is `Ok []`; an index that exists and cannot be read (`Unreadable`) or does not decode (`Corrupt`, its bytes copied
aside) is an `Error`. What decides from the index refuses on it: the corpus quota, content-hash dedup and versioning
refuse the upload (`UploadRejected` naming the index), deletion, tag edits, note updates, narrative ingestion and
original retrieval return the error, and the retention sweep purges nothing and reports it. The listing surfaces
(`GetDocuments`, scope usage, versions, suggested questions, the inventory, the AI tools) read
`loadListing logger storage container`, which renders an empty list and records the failure at `Error`.
`ResetIndex` is the recovery path and still runs: it wipes the scope and records that it could not list the documents
whose chunks it would have dropped.

## The vectorisation hook, and `Refuse`

The post-save hook is `ToolUp.RAG.VectorisationHook` over a `VectorisationDeps` record built inside RAG's staged
contribution from the `Substrate`; `emitIngestionDrop` moved with it (`RAGCompose.emitIngestionDrop` →
`VectorisationHook.emitIngestionDrop`). RAG composed with no `IBlobStorage` now persists its in-process index through
the deployment's resolved storage (the one every platform store uses) instead of a null store that discarded the
bytes; `ragPersistenceValidator` still refuses that shape on a persistent deployment, because the default local file
store does not survive a container restart.

**`IngestionOverflowPolicy.Refuse` is a validator finding.** It used to raise `ConfigPreflightFailedException` from
the fire-and-forget post-save path, where nothing caught it. Now the `rag-ingestion-instance` validator reports an
`Error` when, under `Refuse`, the queue has dropped `RefuseSaturationThreshold` (5) documents inside its rolling 60s
window. Read it at preflight and in the `/dev/inspect` Validators panel. Every dropped document still carries its
`Failed` status, its `DocumentVectorisationDropped` event and its `KnowledgeIngestionDropped` audit row.

## Validators are values

`ConfigValidator.create name (fun () -> async { … })` (ToolUp.Platform.Config) builds an `IConfigValidator` at the
default timeout. The thirteen RAG validator classes are functions of the same tupled arguments, minus the optional
timeout: `TeamModeLocalEmbedderValidator(...)` → `teamModeLocalEmbedderValidator (...)`, and likewise
`ragPersistenceValidator`, `teamModeSharedEmbeddingCacheValidator`, `retrievalDefaultsValidator`,
`chunkingConfigValidator`, `citationDevEndpointValidator`, `ragIngestionInstanceValidator` (which also takes the
overflow policy and the queue's `DroppedLast60s`), `vacuumScheduleValidator`, `ragRateLimitConfiguredValidator`,
`localEmbeddingProviderInProductionModeValidator`, `ragHandlersRegisteredValidator`, `ragConfigBoundsValidator` and
`inProcessIndexReplicaValidator`. Validator names, findings and the `/dev/inspect` contributors are unchanged.

## Deleted, with no replacement

Each had no caller in the SDK.

| Member | Instead |
|---|---|
| `IngestionQueue.Enqueue` (synchronous) | `EnqueueAsync` — the sync form ran the durable arm with `Async.RunSynchronously` |
| `IngestionQueue.Reader` | `IIngestionQueue.Dequeue` / `TryDequeue` |
| `IngestionQueue.RecordDequeue` | nothing: `IIngestionQueue.Dequeue` keeps the depth gauge itself |
| `RagOptions.IndexConversations`, `RagOptions.withIndexConversations` | nothing: the flag was never read |
| `Chunking.withContextualHeader` | call the `ITextSummariser` directly |

Internal duplicates also went: the knowledge base's second suggested-question heuristic (one `suggestedQuestions` in
`Inventory.fs`), the narrative path's copy of the title sanitiser (`Notes.sanitiseTitle`), and the client's private
terminal-status set (`IngestionStatus.isTerminal`). A note update now logs a partial chunk delete as its siblings do.
