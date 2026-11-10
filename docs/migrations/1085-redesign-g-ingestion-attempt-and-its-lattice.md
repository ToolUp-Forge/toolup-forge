# Phase 1085 — the ingestion attempt and its lattice

**What changes for you.** Nothing on the wire moves: `IngestionStatus`, `KnowledgeApi.UploadDocument` /
`GetStatus`, the `"KnowledgeBase.IngestionStatus"` and `"DataManager.IngestionStatus"` notification keys and their
payloads are byte-identical. What moves is composition: the Data Manager observer, the knowledge base's status
helpers and one field of `KnowledgeApiDeps`. One behaviour changes for a short `.txt` upload (section 6).

## 1. One terminal set, one step

Which ingestion statuses are terminal is decided once, beside the type, in `ToolUp.KnowledgeBase.Core`:

| Before | After |
|---|---|
| `KnowledgeBase.ServerIngestionStatusStream.isTerminalStatus` | `IngestionStatus.isTerminal` |
| the observer's private `next` | `IngestionStatus.step target status outcome` (`ChunkOutcome = ChunkIndexed \| ChunkFailed of reason`) |

The codemod rewrites `isTerminalStatus` (rule `1085-is-terminal-status`).

## 2. The ingestion attempt is a seam: `IIngestionAttemptStore`

`ToolUp.Platform.Infra.IIngestionAttemptStore` (in the Platform.Server Infra part) is what both observers advance:

- `Begin(scope, documentId, attemptId, enqueuedChunks)` — called by the producer BEFORE the enqueue; the attempt
  becomes current and the previous attempt's late callbacks are dropped;
- `Advance(report)` — one chunk callback; returns `AttemptTransition.Dropped` (superseded attempt, chunk already
  counted, attempt settled, document not tracked), `Stepped`, or `Terminal of AttemptAnnouncement option`;
- `Forget(scope, documentId)` — the document was deleted or swept.

Progress is persisted per attempt, as one small record beside the document (`AttemptRecords<'Status>` over the
guarded `BlobDocumentStore`), so a chunk counted on one replica is never counted again on another, and no
process-local counter decides a status. A failure settles the attempt `Failed` for both producers; a retry is a new
attempt. Two implementations ship, both bound to the `IIngestionAttemptStoreContract` pack:

- the knowledge base's `KnowledgeAttemptStore` (KnowledgeBase.Server): records at
  `knowledge/{docId}/ingestion-attempt.json` (the attempt sentinel the document already had), the scope's
  `knowledge/index.json` written when the attempt changes class — seeded in flight at `Begin`, settled at the
  terminal — instead of once per chunk;
- the Data Manager's `IngestionAttemptStore.dataManager status storage logger`: records at
  `_ingestion-attempts/{escaped file name}.json` in the scope's container, settling `IIngestionStatusStore`.

## 3. One observer

`ToolUp.RAG.DataManagerIngestionObserver` is deleted. There is one observer, `ToolUp.RAG.IngestionObserver.create
store channel logger`, over any attempt store; it publishes a store's terminal announcement, and only to the user
the job was attributed to (a job whose `OriginatingUserId` names someone else is not announced).

```diff
- let observer = DataManagerIngestionObserver.create statusStore (Some channel) logger
+ let attempts = IngestionAttemptStore.dataManager statusStore blobStorage logger
+ let observer = IngestionObserver.create attempts (Some channel) logger
```

`RAGCompose.withRAG` composes this for you; a deployment that composed the old observer by hand makes the change
above (codemod review rule `1085-data-manager-observer`). The post-save hook now begins each Data Manager file's
attempt before it enqueues, and its job carries that attempt. `KnowledgeBase.Server.makeIngestionStatusObserver
storage channel logger` keeps its signature: it is the one observer over a `KnowledgeAttemptStore`.

## 4. `KnowledgeApiDeps` loses `MarkIngestionFailed`

The knowledge base enqueues through one function and settles through one function:

- `KnowledgeIngest.enqueue deps doc chunkPairs : Async<Result<IngestionAttempt option, EnqueueRefused>>` — begins the
  attempt, enqueues the job, records the enqueue, and on a refusal settles the document `Failed` with
  `EnqueueRefused.describe` (the "queue is full" sentence, unchanged);
- `KnowledgeIngest.settle deps docId status` — a terminal no chunk callback will reach: forgets the attempt, writes
  the index entry, announces it.

A consumer that built `KnowledgeApiDeps` by full literal deletes the field:

```diff
-     MarkIngestionFailed = fun _ _ _ -> async.Return()
```

The codemod blanks that line (rule `1085-mark-ingestion-failed-stub`); any other use of the field is reported
(`1085-mark-ingestion-failed`) and becomes `KnowledgeIngest.settle deps docId (Failed reason)`.

## 5. Status is read from storage

`KnowledgeBase.ServerIndexStorage.statusCache`, `setStatus` and `updateStatus` are deleted, with
`beginIngestionAttempt`, `publishTerminalStatus` and `updateIndexChunkCount` (codemod review rule
`1085-status-helpers`). `GetStatus` reads `persistedStatus storage container docId` — the attempt's progress while it
is in flight, else the index entry — so every replica answers the same. `ExtractingText` is announced to
status-stream subscribers but no longer persisted: the index goes `Queued` → `Embedding(0, n)` in one write.

`ingestionStatusStream` seeds from storage, so it takes where to read:

```diff
- ingestionStatusStream docId
+ ingestionStatusStream deps.Storage deps.Scope.Container docId
```

(review rule `1085-status-stream`). `clearStatus` keeps its name and announces a removal to subscribers.

## 6. A short `.txt` is one chunk

A plain-text upload shorter than the splitter's minimum used to yield no chunk and settle `Complete 0` — a
successful index of nothing — while every other format kept short text whole. Short prose is now one whole chunk
in every format (the choice over an explicit empty-extraction terminal: the text is there and is searchable). The
`txt-short` extractor golden records the one chunk.

## Verification

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Phase 1085"`
runs the contract pack over both stores, the index-write bound, the replica case and the byte-for-byte payloads.

## Rollback

Pin the previous SDK version; nothing persisted needs migrating back (an attempt record the older version finds
decodes as its attempt sentinel).
