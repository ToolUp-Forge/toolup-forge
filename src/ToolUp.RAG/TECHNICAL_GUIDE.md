# ToolUp.RAG Technical Guide

Deep technical reference for the `ToolUp.RAG` companion package. Assumes familiarity with the [`ToolUp.Platform` Technical Guide](../ToolUp.Platform/TECHNICAL_GUIDE.md) (Giraffe / Fable / Elmish extensions, props injection, async chain, scope resolution, `ComposeExtensions`) and the [`ToolUp.AI` Technical Guide](../ToolUp.AI/TECHNICAL_GUIDE.md) (agent loop, `SystemPromptBuilder`, `PromptContext`).

## Architecture overview

```
Browser                             Server
──────                              ──────
File upload                         composeWithRAG (ToolUp.RAG)
   │                                       │ wraps
   │ ToolUp.Remoting                       ▼
   ▼                                composeWithAI (ToolUp.AI)
SessionFileStore.AddFile            │     │ wraps
   │                                │     ▼
   │ post-save hook fires           │   Server.compose (ToolUp.Platform)
   ▼                                │
IngestionQueue.Enqueue              │
   │                                │
   ▼                                │
IngestionBackgroundService          │
   │  dequeue + SemaphoreSlim       │
   ▼                                │
IRetrievalPipeline.Index            │
   │ embed → store.Upsert           │
   │                                │
   ▼                                │
InMemoryVectorStore                 │
   │ markDirty(scope)               │
   │                                │
   └─► background flush loop ───► IBlobStorage.Upload
                                    (debounced, 2s tick)

                                    AI chat request
                                           │
                                           ▼
                                    AIAssistantHandler builds PromptContext
                                           │  (CurrentMessage = Some query)
                                           ▼
                                    SystemPromptBuilder.compose [..., withRetrieval]
                                           │
                                           ▼
                                    IRetrievalPipeline.Retrieve
                                           │ authorisedScopes(AccessContext)
                                           │ embed query
                                           │ store.Search
                                           ▼
                                    Retrieved chunks injected into system prompt
                                           │
                                           ▼
                                    IAIProvider.SendMessage
```

Two pipelines touch `IRetrievalPipeline`: **ingestion** (write path, background) and **retrieval** (read path, per request). Both share the same `IVectorStore` + `IEmbeddingProvider` pair wired in DI. Neither path knows which embedding provider or which vector store is wired — the interfaces satisfy Phase 9c portability, so a distributed vector-database companion can replace the in-memory store without touching either pipeline.

## Pipeline lifecycle — ingestion path

1. **Trigger.** `SessionFileStore.AddFile` completes. The post-save hook registered by `composeWithRAG` runs the `makeVectorisationHook` closure with `(processedData, entry, scope)`.
2. **Handler lookup.** The hook finds the first `VectorisationHandler` whose `DataTypeId` matches `entry.DataType`. No match → the hook returns without emitting anything (non-breaking for modules that don't vectorise).
3. **Chunk production.** `handler.Vectorise processedData` returns a `TextChunk list`. Empty list → no jobs enqueued.
4. **Scope resolution.** Storage scope (`"team-<id>"` or `"user-<id>"`) is translated to `VectorScope`: team containers map to `Team teamId`; anything else to `Deployment`. `Platform` scope is never produced by the hook — platform content is loaded out-of-band by administrators.
5. **Enqueue.** One `IngestionJob` per chunk is written to the `Channel<IngestionJob>` via `queue.Enqueue` (non-blocking; unbounded channel).
6. **Background dequeue.** `IngestionBackgroundService.ExecuteAsync` is a `while not cancellation` loop that awaits `queue.Reader.ReadAsync`. Each dequeued job is handed off via `Async.Start(processJob job, cancellationToken)` — the dequeue loop never blocks on a single job's completion.
7. **Per-job work.** `processJob` takes a `SemaphoreSlim` slot (default concurrency 4), calls `pipeline.Index chunkId chunk scope`, and releases the semaphore in `finally`. The pipeline embeds the text and upserts into the vector store.
8. **Audit event.** On success, `KnowledgeChunkIndexed` is written to `IEventStore`; on failure, `KnowledgeChunkFailed` with the error message. Both events carry the document id and scope id so audit logs can reconstruct the indexing stream.

### Concurrency model

| Layer | Mechanism | Cap |
|---|---|---|
| Enqueue | Bounded `Channel<DocumentIngestionJob>` (default 5,000, `withIngestionQueueCapacity`) | Capacity cap; a full queue is handled per `IngestionOverflowPolicy` (see [Backpressure + overflow surfaces](#backpressure--overflow-surfaces-phase-303)) — `DropWrite` drops with audit + notification + `/health/rag` counters, `Block` awaits space, `Refuse` fails loud |
| Dequeue | `while` loop awaiting `ReadAsync` | One reader at a time (channel is `SingleReader = true`) |
| Per-job embedding | `SemaphoreSlim` acquired inside `processJob` | `maxConcurrency`, default 4 — sized to respect embedding-provider rate limits |
| Audit emission | `IEventStore.Write` inline | No additional cap — event store is expected to be cheap |

The dequeue-and-fire-without-awaiting pattern is deliberate: if `processJob` waited on a slow embedding call, the channel would never drain and back-pressure would be invisible. The semaphore caps in-flight work; the channel absorbs bursts; the audit stream reports the outcome.

### Failure handling

- **Embedding provider throws.** `processJob`'s inner `try/with` catches the exception and emits `KnowledgeChunkFailed` with the exception message. The semaphore is released; the next job proceeds.
- **Audit write throws.** Uncaught inside `processJob` (the `try/with` only wraps the index call). The outer `Async.Start` default handler logs via `ILogger.Error` if the operation faults — but the dequeue loop continues.
- **Dequeue loop throws unexpectedly.** The `while` loop's `try/with` catches `OperationCanceledException` (graceful shutdown) silently and everything else via `logger.Error` — the loop then iterates. The service does not self-terminate on transient errors.
- **Process shutdown.** ASP.NET Core cancels the `stoppingToken`, the `ReadAsync` call throws `OperationCanceledException`, the `while` exits, and `Dispose` runs — releasing the `SemaphoreSlim`. Any in-flight `processJob` tasks not yet started are silently abandoned; started jobs either complete or fault based on the token they captured.

### Backpressure + overflow surfaces (Phase 303)

The ingestion queue is a **bounded** `Channel<DocumentIngestionJob>` (default capacity 5,000, `RAGServerApp.withIngestionQueueCapacity`), not unbounded — a bulk-upload spike is rejected at the door rather than buffering unboundedly and tripping the embedding provider's rate limiter. When the post-save hook offers a document to a full queue, the behaviour is set by `RAGServerApp.withIngestionQueueOverflowPolicy : IngestionOverflowPolicy`:

| Policy | Full-queue behaviour |
|---|---|
| `DropWrite` (default) | Bounded retry (`[100; 250; 500; 1000]` ms — 5 tries over ~1.85 s on the fire-and-forget path), then **drop** with the observability triple below. Favours upload responsiveness. |
| `Block` | Never drop — the hook awaits queue space via `IngestionQueue.EnqueueBlocking`. The upload HTTP response has already returned, so this delays *indexing* under sustained load but never loses a document. Suited to bulk backfills. |
| `Refuse` | Drops like `DropWrite`, and **additionally raises `ConfigPreflightFailedException`** once ≥ `refuseSaturationThreshold` (5) documents have been dropped inside the queue's rolling 60 s window — compliance-grade fail-loud. |

**The drop observability triple** (`DropWrite` / `Refuse`, when the bounded retry is exhausted) — emitted by `RAGCompose.emitIngestionDrop`:

1. **`KnowledgeIngestionDropped` audit** under the deployment-wide `_platform.knowledge` scope (`KnowledgeSourceModule.value`), via `IAuditLog.Record`. Payload: `{ ScopeKey; DocId; ChunkCount; QueueCapacity; Reason }` — identifiers + cardinality only, no chunk content. Queryable per-document in isolation, distinct from per-tenant activity (mirrors the corrupt-index `KnowledgeIndexLoadFailed` trail).
2. **Warning `SystemMessage`** published to the uploading user's scope via `INotificationChannel` (when the enqueue carried a `OriginatingUserId`), so the client can surface "this document could not be indexed — try again in a moment". No user attribution (e.g. the system hydration path) ⇒ no notification; the file-list badge still refreshes on the next `ListFiles`.
3. **Queue `Dropped` counters** — `IngestionQueue.Dropped` (cumulative, process-lifetime) and `IngestionQueue.DroppedLast60s` (rolling 60 s), bumped by `RecordDrop`.

Alongside the triple, the existing per-scope `DocumentVectorisationDropped` event (RAG event trail) and `FileIngestionStatus.Failed` badge are still written.

**`/health/rag` exposure.** The `RagHealthHandler` merges the queue counters onto the telemetry snapshot under an `IngestionQueueDrops` object: `{ Cumulative; RollingLast60s; Depth; Capacity }`. The snapshot's existing top-level fields are unchanged (additive), so an operator dashboard sees a non-zero `RollingLast60s` the moment saturation starts dropping documents, without trawling the audit trail.

**`Refuse` propagation nuance.** The post-save hook is fire-and-forget — `SessionFileStore.AddFile` runs it on `Async.Start` *after* the HTTP response is sent, wrapping any thrown exception in a `PostSaveHooksLogger.Error` (falling back to stderr). So `Refuse`'s `ConfigPreflightFailedException` surfaces as a **loud, named error in the operator log** alongside the audit trail — it does not crash the already-returned upload request. The screaming-error signal is the point; the process stays up.

## Pipeline lifecycle — retrieval path

1. **Trigger.** `AIAssistantHandler` receives a `SubmitMessage` request, builds a `PromptContext` with `CurrentMessage = Some request.Content`, and invokes the configured `SystemPromptBuilder`.
2. **Builder composition.** `composeWithRAG` appends `withRetrieval pipelineRef.Value.Value` to the builder chain. If the caller supplied no `aiConfig`, the retrieval builder alone becomes the system prompt. If the caller supplied one, `SystemPromptBuilder.compose` layers their builder with `withRetrieval` so retrieval content appears after their static/module content.
3. **Skip condition.** `withRetrieval` returns `""` immediately when `ctx.CurrentMessage = None` — a graceful no-op for builder invocations outside a user message (startup health checks, etc.).
4. **Scope selection.** Team users query `[Platform; Deployment; Team ctx.Access.TeamId]`; anonymous or teamless users query `[Platform; Deployment]`. Platform and Deployment are universally readable; Team is filtered below.
5. **Pipeline call.** `IRetrievalPipeline.Retrieve` runs `authorisedScopes` to drop any `Team teamId` whose `teamId ≠ ctx.TeamId`. If the permitted list is empty, return `[]` (graceful — no auth error, just no context).
6. **Embedding + search.** The pipeline embeds the query once, calls `store.Search permitted queryVector request.TopK`, and applies `MergeStrategy` — `Interleaved` re-ranks all matches by score; `Separate` returns them in scope-grouped order.
7. **Citation formatting.** `formatMatch` reads `_source` metadata if present (set by the `KnowledgeBase` module's extractors) and renders each chunk as `[<source>]\n<content>`. The formatted block is concatenated and prefixed with "Relevant context from your team's knowledge base:".
8. **Injection.** The returned string becomes part of the system prompt delivered to `IAIProvider.SendMessage`. The user never sees this — it's metadata to the model, same as any other `SystemPromptBuilder` output.

### Access-control invariants

- **Team isolation is enforced by `authorisedScopes`, not by the vector store.** A buggy caller that constructed a `RetrievalRequest` with another team's `Team teamId` would still pass the request to the pipeline — but the pipeline filters the scope out before reaching `IVectorStore.Search`. The store never sees a team scope the caller is not entitled to.
- **`Platform` and `Deployment` are readable by everyone, including anonymous users.** If a deployment indexes sensitive content to those scopes, it's visible to every authenticated session. Treat those scopes as "publishable" and use `Team` for anything confidential.
- **Retrieval never raises auth errors.** Mismatched scope requests yield `[]`. This lets prompt builders layer retrieval without fault handling; builders that need to branch on "no team" do so on `ctx.Access.TeamId`, not on exception catching.

## InMemoryVectorStore internals

### Pre-normalised storage

Every vector passed to `Upsert` is divided by its magnitude before storage. At query time the query vector is normalised once, then similarity reduces to a single `dotProduct` call per candidate. For a 50,000-entry 512-dim index, this roughly halves query CPU (one `magnitude` pass instead of three per candidate, plus the final division avoided).

The trade-off: the in-memory representation no longer preserves the original vector. Callers who need the raw vector back are out of luck — but `IVectorStore` has no such contract, so this is safe. Indexes loaded from an earlier un-normalised format are re-normalised during `loadScope`; normalising an already-unit vector is a float-rounding-precision no-op.

### Debounced `IBlobStorage` persistence

`Upsert`, `DeleteByScope`, and `DeleteChunk` no longer call `storage.Upload` inline. Instead, they mark the affected scope dirty via `dirty.[scopeKey] <- scope` and return immediately. A background loop (`flushLoop`) wakes every `flushIntervalMs` (default 2000 ms), snapshots-and-clears the dirty set, and persists each scope once. A 10,000-chunk bulk import triggers O(1) persistence passes per flush-interval instead of 10,000 full-index JSON serialisations.

Failure handling:

- **Upload error** (storage returns `Error _`). Logged at `Warn`, scope re-marked dirty so the next flush retries. No loss of in-memory state.
- **Unexpected exception inside `persistScope`.** Caught by `flushAll`'s `try/with`, logged at `Error` with the exception, scope re-marked dirty.
- **`flushLoop` itself faults.** The loop's outer `try/with` logs at `Error`, swallows, and iterates. `OperationCanceledException` exits cleanly.

### `IDisposable` contract

`InMemoryVectorStore` implements `IDisposable`. ASP.NET Core's DI container disposes it during shutdown:

1. `cts.Cancel()` — signals the `flushLoop` to exit after its current `Async.Sleep`.
2. `flushAll ()` runs synchronously via `Async.RunSynchronously` — one final pass that drains the dirty set and persists every pending scope.
3. `cts.Dispose()` — releases the `CancellationTokenSource`.

If the final flush faults, it is logged at `Error` but not re-raised — shutdown continues. The rationale: a process that refuses to shut down because of a transient blob-storage failure creates operational pain worse than the occasional lost-write.

### Concurrency invariants

- `store` (`ConcurrentDictionary<(string * string), float32 array * TextChunk>`) handles concurrent `Upsert` / `DeleteChunk` / `Search` safely.
- `dirty` (`ConcurrentDictionary<string, VectorScope>`) is written during `Upsert`/`Delete` and read+cleared during flush. Per-key writes are atomic; the `TryRemove` snapshot-and-clear in `flushAll` captures whatever is present at that instant — concurrent writes racing with the flush re-mark the scope for the next tick (no livelock, no lost write).
- `loadScope` is called from construction (eager for Platform/Deployment) and lazily from `Search` (team scopes). Lazy hydration checks `store.Keys |> Seq.exists` — a benign race if two concurrent searches both trigger hydration is fine; `AddOrUpdate` makes the second load idempotent.

### Tombstone soft-delete (Phase 14h)

`IVectorStore.DeleteChunk` is a tombstone write, not a hard delete. The implementation:

1. **`DeleteChunk scope chunkId`** stamps the existing entry's metadata with `_deletedAt = <ISO 8601 UTC>` and writes it back via `AddOrUpdate`. The vector and content remain — only the metadata changes. Scope is marked dirty so the next flush persists the tombstone.
2. **`Search`** is enforced via `not (isTombstoned chunk)` in the candidate filter. Tombstoned chunks are invisible to retrieval the moment the tombstone is written (lock-free for the reader).
3. **`Upsert scope chunkId vector chunk`** strips any `_deletedAt` from the incoming chunk's metadata before storing. Re-uploading content under an existing chunk id implicitly clears the tombstone — the new content supersedes the old whether or not it had been vacuumed yet.
4. **`RestoreChunk scope chunkId`** removes `_deletedAt` from the entry's metadata if present (no-op otherwise). Reverses `DeleteChunk` within the retention window. Past `Vacuum`, the entry is gone and `RestoreChunk` is a silent no-op.
5. **`Vacuum scope olderThan`** parses each entry's `_deletedAt` via `DateTimeOffset.TryParse`, filters those whose timestamp is `< olderThan`, and removes them via `TryRemove`. Returns the count of purged entries. Non-tombstoned chunks are untouched.
6. **`DeleteByScope`** continues to bypass the tombstone path — it hard-removes every entry in the scope. There is no recovery from `DeleteByScope`; it is a config-grade reset (used by the `KnowledgeBase` companion's "reset team knowledge base" admin path).
7. **`ListChunks scope includeDeleted`** honours the flag — `false` (default for re-embed scans) skips tombstoned chunks; `true` returns them so callers can introspect `_deletedAt` themselves.
8. **`ListScopes ()`** returns the distinct `VectorScope` values currently in `store.Keys`. Lazy-load semantics mean cold scopes that have never been queried don't appear; the documentation is explicit about this.

The retention window itself is not stored — `Vacuum` takes the cutoff timestamp from the caller. A composing app schedules `Vacuum` from its own admin / cron path and decides retention. An auto-vacuum scheduler (`BackgroundService` driving `Vacuum` on a timer) is a Phase 14h follow-up.

## Vector store scale story (Phase 14k)

`IVectorStore` is one interface with a deliberate three-rung capacity
ladder. Pick the rung by corpus size; the contract (scope isolation,
`IBlobStorage` persistence, soft-delete tombstones) is identical at
every rung, so moving up is a `RAGServerApp.withVectorStore` swap with
no pipeline change.

| Rung | Store | Corpus | Mechanism | When |
|---|---|---|---|---|
| 1 | `InMemoryVectorStore` (SDK default) | **< ~50k chunks** | linear cosine scan | dev, CI, small single-team deployments. p95 crosses ~50 ms near 50k. |
| 2 | `ToolUp.VectorStores.Hnsw` companion | **< ~1M chunks** | per-scope `SmallWorld` graph (graph-based ANN, lossy by tunable degree) | multi-year analyst archives; the middle rung before an operational DB dependency. |
| 3 | external `Pgvector` / `Qdrant` companion | **> ~1M chunks** | bare-metal Postgres / managed ANN service | interface is ready; concrete companions deferred (Phase 14k out-of-scope) until the HNSW ceiling is the bottleneck. |

HNSW is opt-in (GP 13): a deployment that stays under 50k pulls none
of the `HNSW.Net` weight. Compose it before `composeWithRAG`:

```fsharp
RAGServerApp.create factory configStore
|> RAGServerApp.withVectorStore (HnswVectorStore.create blobStorage logger)
|> RAGServerApp.run
```

**Dimension-partition routing rule (multimodal — Phase 14k WS5).**
The HNSW companion enforces **one graph per scope**, which implies one
embedding dimensionality per scope. This is load-bearing today because
no concrete image embedder (`IImageEmbedder`, Phase 14i) ships in this
round: a scope only ever holds text chunks at the text embedder's
dimensionality. The contract a future CLIP/SigLIP companion must honour
when that changes: an `ImageRegion` chunk embeds at the image model's
dimensionality and MUST route to a dimension-isolated partition within
the same scope — text and image chunks never share one `SmallWorld`
graph. The current defence is `cosineDistance` returning the maximum
distance (`1.0`) whenever query and corpus vectors disagree on length,
so a mismatched-dim intruder ranks last and falls outside the top-K
window — see `HnswVectorStore.fs:137`. Multi-graph parallel fan-out
across a scope's text + image partitions is a future refinement,
useful only once a real CLIP-side query path exists.

**When to opt into HNSW** (sample-deployment guidance). The SDK's
default `InMemoryVectorStore` is flat-scan exact-cosine — perfectly
adequate up to ~50k chunks per scope on commodity hardware. Beyond
that, flat scan's O(n) per query starts to crowd the request budget:
flip to HNSW via
`RAGServerApp.withVectorStore (HnswVectorStore.create blobStorage logger)`
as shown above. Smaller corpora gain nothing measurable from HNSW —
the build cost amortises slowly and ANN recall is necessarily ≤ exact.
A deployment running mixed-dim chunks (e.g., when a future image
embedder lands) should partition by data type into separate scopes
until the multi-graph fan-out support arrives.

**Fidelity audit** lives in
`src/ToolUp.Platform.Tests/InProcess/HnswFidelityTests.fs`:
self-retrieval correctness + overlap-vs-exact-cosine floor + a loose
latency ceiling + the **mixed-dimension defence test** (a 96-dim
intruder in a 48-dim corpus must never surface in 48-dim query
results). The strict acceptance bounds — recall@10 within 2 % of flat
scan, p95 < 50 ms on a 500k representative corpus with `efSearch = 50`
— remain deferred pending a representative corpus fixture (Phase 14k
notes this explicitly; the synthetic smoke is the CI-safe stand-in,
not the perf gate). The multi-dim recall-fidelity test + latency
benchmark items similarly defer
until a corpus pairing text and image embeddings at mismatched dims is
authored — the eval harness in `ToolUp.RAG.Evaluation` and the
benchmark orchestrator in `ToolUp.RAG.Benchmarks` are already
fixture-driven, so the deferral is purely "fixture not yet authored".

## `IEmbeddingCache` + `CachingEmbeddingProvider`

The supplied `IEmbeddingProvider` is wrapped transparently inside `composeWithRAG` with `CachingEmbeddingProvider`. The wrapper preserves the inner provider's `ProviderId` / `ModelId` / `Dimensions` so version stamping downstream still records the underlying identity.

**Cache key.** `EmbeddingCacheKey = { Version: EmbeddingVersion; TextHash: string }` where `Version` is the wrapped provider's identity and `TextHash` is the SHA256 hex digest of the input text. SHA256 (rather than the raw text) means the cache never retains user content — a privacy property and a memory-bounding one.

**Implementation.** `InMemoryEmbeddingCache` uses a `LinkedList<EmbeddingCacheKey * float32 array>` for recency order and a `Dictionary<EmbeddingCacheKey, LinkedListNode<...>>` for O(1) lookup. A coarse `lockObj` covers reads (which touch recency by removing the node and re-adding at head) and writes (which evict the LRU tail when at capacity, default 10000 entries). Hit/miss counters are tracked atomically inside the same lock; `HitRate` returns the steady-state ratio.

**Why an LRU.** Realistic query traffic exhibits locality — the top-of-mind questions in a session repeat, and chunk re-embeds during reingestion repeat the same content under the same key. Strict LRU is a good default; a more sophisticated policy (size-aware, frequency-aware) is a future companion. The cache is per-process; a distributed cache (Redis) is a future companion implementing the same `IEmbeddingCache` contract.

**Why decorate at compose time, not at provider creation.** Callers register a *raw* `IEmbeddingProvider`. They don't need to know the cache exists. `composeWithRAG` wraps internally, and the cache is accessible to admin endpoints via DI (`IEmbeddingCache.HitRate()`). This keeps the embedding-provider companion surface clean — `LocalEmbeddingProvider.create ()` and `OpenAIEmbeddingProvider.create secretStore` both return raw providers; caching is a runtime property of the RAG composition, not of the provider library.

## `EmbeddingVersion` stamping + `ReembeddingBackgroundService`

`RetrievalPipeline.Index` builds a stamped chunk before calling `IVectorStore.Upsert`:

```
let stamped = { chunk with Metadata =
                    chunk.Metadata
                    |> Map.add EmbeddingVersion.MetadataProviderKey embedder.ProviderId
                    |> Map.add EmbeddingVersion.MetadataModelKey embedder.ModelId
                    |> Map.add EmbeddingVersion.MetadataDimensionsKey (string embedder.Dimensions) }
```

Both the dense store and (when present) the sparse index see the stamped chunk. The stamp travels through `IBlobStorage` persistence and is visible in `ListChunks`. The `ReembeddingBackgroundService` reads it back and decides whether the chunk needs re-indexing.

**Detection logic.** A chunk needs re-embed if any of:

- The `_embedProvider` / `_embedModel` / `_embedDim` keys are absent (pre-versioning chunk).
- Any of the three values disagrees with the current `IEmbeddingProvider`'s identity.

When any of these is true, `pipeline.Index` is re-run — embedding is regenerated against the current provider, the upsert replaces the prior vector, and the metadata is re-stamped with the current identity.

**Driving the service.** `ReembeddingQueue` is an unbounded `Channel<VectorScope>` exposed in DI. `ReembeddingBackgroundService.ExecuteAsync`:

1. **Initial drain.** Reads `IVectorStore.ListScopes()` once and pushes any known scopes onto the queue. For lazy-loading stores (`InMemoryVectorStore`) this is empty on cold start — scopes only appear after their first access. Composing apps push specific scopes from admin endpoints to force a scan.
2. **Loop.** Awaits `queue.Reader.ReadAsync(stoppingToken)`, calls `scanScope`, repeats. Cancellation exits cleanly via `OperationCanceledException`.
3. **Per-scope scan.** Calls `ListChunks scope false`, filters via `needsReembed`, and fires `processOne` for each stale chunk via `Async.Start` with the cancellation token.
4. **Per-chunk re-embed.** `processOne` takes a `SemaphoreSlim` slot (default concurrency 2 — kept lower than ingestion's 4 because the typical use case is bulk re-embed after a model swap, where flooding the embedding API would race the new ingest path), calls `pipeline.Index`, emits `KnowledgeChunkReembedded` on success or `KnowledgeChunkReembedFailed` on exception. Failures leave the prior vector in place — a botched re-embed never corrupts retrieval.

The service is registered alongside `IngestionBackgroundService` as a second `IHostedService`. Both run for the lifetime of the host; they share `IVectorStore`, `IRetrievalPipeline`, and `IEventStore` via DI.

## `RAGPromptBuilder.withRetrieval` composition rules

The builder consumes `PromptContext.CurrentMessage` as the query and returns formatted context. Three composition rules to know:

- **It is position-independent.** `SystemPromptBuilder.compose` runs builders in parallel and joins outputs with blank lines. Put the retrieval builder anywhere in the list — typically last so retrieved context appears after static framing and module-specific instructions.
- **It tolerates absent context.** Returns `""` when no matches or when the current message is `None`. An empty string composes cleanly — there's no marker for "I had no context to add."
- **It is read-only per call.** No side effects (no events emitted, no logging of the query). Add a higher-level wrapper if retrieval observability is needed.

The builder does not implement caching directly — it relies on the `CachingEmbeddingProvider` wrapper installed by `composeWithRAG` (Phase 14h). A conversation that sends ten turns with the same query string hits `IEmbeddingCache` on turns 2–10 and never re-runs the underlying embedding call. Cache key includes provider + model + dimensions, so a model swap automatically invalidates the entry without an explicit flush.

## Tool-aware framing (Phase 14r) — how framing composes with tool-calling

The retrieval framing is intentionally knowledge-base-first: it teaches the model that the search has already run, that KB content is authoritative for the team's data, and that an empty result means "the search found nothing, don't invent facts". That framing is exactly right for a KB-only deployment, but it backfires when the deployment *also* loads live-interface tools (the `_platform.ui.*` inspection / mutation family, or any client-resident tool). A question like *"what filters do I currently have applied?"* has no KB answer — the correct move is to call the interface inspection tool, not to reply "I don't have that in the knowledge base". Left unframed, a KB-first model reads the empty retrieval block and refuses or speculates from history instead of inspecting.

Phase 14r makes the framing **tool-aware** so this composition is handled by the SDK, not patched at each deployment's system-prompt layer:

- **Detection is compose-time, from the deployment's tool list.** `composeRAG` reads the aggregated `ServerApp.AITools` and derives a `RAGPromptBuilder.ToolFraming` via `ToolFraming.fromTools`. A tool counts as *live-interface* when its `Location = ClientResident` or its name is in the `_platform.ui.*` family. Server-resident analytical tools — including the `_platform.ai.*` cross-module read family — are **not** live-interface tools: they read persisted data, so the KB-first framing still applies to them. A deployment with no such tools yields `HasLiveUiTools = false` and sees the pre-Phase-14r framing byte-for-byte (no regression).

- **The framing companion is `Preferred`-only.** `RAGCompose.resolveFramingWithTools` appends `uiToolFramingCompanion` — one paragraph, naming the capability by *purpose* ("interface inspection tool"), never by tool ID — after the base framing, but **only** under `GroundingMode.Preferred` (the default). `Permissive` emits no framing at all (the model is already free to call any tool), and `StrictlyGrounded` deliberately refuses on a retrieval miss, so redirecting to a tool would contradict its contract — both ignore the tool summary and fall through to plain `resolveFraming`.

- **The empty-retrieval message is per-request tool-aware.** The framing preamble is composed once and can't see per-turn retrieval results (`SystemPromptBuilder.compose` resolves builders in parallel), so the *empty-result* redirect lives where emptiness is known: `withRetrievalToolAware` (the tool-aware sibling of `withRetrieval`) threads the same `ToolFraming` into its empty branch. On a miss with live-interface tools present, the injected block says "…if the user's question is about the live state of the interface they are viewing, call the interface inspection tool before concluding you cannot answer", instead of the neutral "returned no relevant matches". With no such tools it returns the historical wording verbatim.

**Division of labour:** the *framing companion* (compose-time, KB stays authoritative for documents/analyses; live on-screen state defers to inspection) sets the standing policy; the *empty-retrieval redirect* (per-request) is the concrete nudge at the moment a KB miss would otherwise read as a dead end. Together they let a RAG deployment with UIControl tools answer live-screen questions via the inspection tool **without** a deployment-level system-prompt patch. The back-compat `withRetrieval` delegates to `withRetrievalToolAware` with `ToolFraming.none`, so any caller that doesn't thread a tool list keeps the original behaviour.

## `composeWithRAG` wiring order

`composeWithRAG` is invoked via `RAGServerApp.run`, which flattens the `RAGServerApp → AIServerApp → ServerApp` record stack into the parameter list expected by `composeWithRAG`. (Apps that still call `composeWithRAG` directly work identically.) Internally `composeWithRAG` runs in three phases:

1. **Pre-compose construction.** Build the `IngestionQueue`, install the post-save hook via `configurePostSaveHooks` if any handlers were supplied, allocate a deferred `pipelineRef : IRetrievalPipeline option ref`, and construct the RAG-aware system prompt builder (which reads `pipelineRef.Value` lazily).
2. **`serviceConfig` callback.** Executed by `compose` when DI is being built:
   - Register AI services (factory, config store, tool registry, SSE manager).
   - Resolve `IBlobStorage` (with warning if absent).
   - Construct `InMemoryVectorStore` with the shared `ILogger`. Construct `InMemoryBM25Index` (when sparse retrieval is wired).
   - Construct `InMemoryEmbeddingCache` and wrap the supplied `IEmbeddingProvider` with `CachingEmbeddingProvider`. The wrapped provider is what flows into `RetrievalPipeline` — every embed call (query path and ingestion path) is cached.
   - Construct `RetrievalPipeline` with the wrapped embedder, vector store, and (optional) sparse index + reranker / MMR options.
   - **Fill `pipelineRef.Value <- Some pipeline`** — this is what wires the deferred builder to a live pipeline.
   - Resolve `IEventStore` (fallback to `InMemoryEventStore` if none registered).
   - Construct `IngestionBackgroundService` with the shared logger.
   - Construct `ReembeddingQueue` and `ReembeddingBackgroundService`. Both register as singletons; the service runs alongside `IngestionBackgroundService` as a second `IHostedService`.
   - Register `IVectorStore`, `ISparseIndex` (when present), `IEmbeddingProvider` (wrapped), `IEmbeddingCache`, `IRetrievalPipeline`, `IngestionQueue`, `ReembeddingQueue`, and the two `IHostedService`s.
3. **Handlers + inner compose.** Pass the composed handler list (AI routes + `/api/ai/events`) and extensions to `compose`, which finishes the ASP.NET Core + Giraffe pipeline setup.

The `pipelineRef` pattern is deliberate: the system-prompt builder is constructed *before* DI is built, but needs *a reference to* the pipeline that DI will construct. Using a mutable ref cell for this single fill-once-at-startup handoff is the simplest correct answer. The ref is written once during service-config and read lock-free from every request — there is no race.

### `RAGServerApp` record — fluent composition surface

`RAGServerApp` is the record-based counterpart to `composeWithRAG`. It wraps an `AIServerApp` in an `AI` field and carries the embedding provider alongside:

```fsharp
type RAGServerApp = {
    AI: AIServerApp
    EmbeddingProvider: IEmbeddingProvider option
}
```

`RAGServerApp.run` flattens the nested `AIServerApp → ServerApp` records and calls `composeWithRAG`, re-using the underlying AI wiring. Per-module `VectorisationHandler`s flow in via `ServerModule.withVectorisation` (on the innermost `ServerApp`); they appear in `composeWithRAG`'s `vectorisationHandlers` parameter after the flatten step. Apps without RAG use `AIServerApp.run` directly and never touch this companion.

## Null-blob-storage fallback

If the caller passes `blobStorage = None`, `composeWithRAG`:

1. Logs `"[RAGCompose] No IBlobStorage supplied — vector index persistence is disabled. Ingested chunks will be lost across process restart."` at `Warn`.
2. Substitutes `makeNullBlobStorage ()` — a no-op implementation whose `Upload` returns `Ok ""` and whose `Download` returns `Error`.
3. Proceeds as normal. The in-memory store works; the debounced flush loop runs and posts every tick to the null storage (which no-ops); every restart re-starts with an empty index.

This is a development-mode convenience: it lets an engineer run the full RAG pipeline end-to-end without configuring blob storage. It is *not* a supported production mode. The warning is deliberate and loud so that a misconfigured production deployment surfaces the loss-on-restart risk before it's needed.

## Document-understanding extension points (Phase 14i)

Three optional interfaces let document-aware companions enrich extraction without leaking heavyweight dependencies (`Tesseract`, `Camelot`, `CLIP`) into core or the RAG companion. All three are no-op by default; with nothing wired, ingestion behaviour is byte-equivalent to the pre-Phase 14i path.

### `IOcrProvider`

```fsharp
type OcrPage = { PageNumber: int; Text: string }

type IOcrProvider =
    abstract Name : string
    abstract IsScanned : byte[] -> mimeType:string -> Async<bool>
    abstract ExtractText : byte[] -> mimeType:string -> Async<OcrPage list>
```

Used by `ToolUp.KnowledgeBase.Server.extractPdf`: if `IsScanned` returns `true`, the path skips `PdfPig.GetPages()` text extraction and uses `ExtractText` instead. The default `NoOpOcrProvider` (in `src/ToolUp.RAG.Server/Server/NoOpDocUnderstanding.fs`) reports `IsScanned = false` for everything, so the existing native-text path runs unchanged.

### `ITableExtractor`

```fsharp
type ExtractedTable =
    { Page    : int option
      Headers : string array
      Rows    : (int * string array) list }

type ITableExtractor =
    abstract Name : string
    abstract ExtractTables : byte[] -> mimeType:string -> Async<ExtractedTable list>
```

The `ExtractedTable` shape is deliberately compatible with `Chunking.SheetData` — `SheetName` is synthesised from `Page` (`"Page N"` or `"Table"`), and `Headers` / `Rows` map straight across. KB pipes each extracted table through `chunkSpreadsheet ChunkingConfig.defaults`, so future companions like `Camelot.NET` produce the same row-preserving, header-repeating chunks that XLSX/CSV ingestion already produces. The default `NoOpTableExtractor` returns `[]`.

### `IImageEmbedder`

```fsharp
type IImageEmbedder =
    abstract ProviderId : string
    abstract ModelId    : string
    abstract Dimensions : int
    abstract EmbedImage : byte[] -> mimeType:string -> Async<float[]>
    abstract EmbedQuery : string -> Async<float[]>

[<Literal>]
let ImageRegionDataTypeId = "ImageRegion"

module ImageEmbeddingMetadata =
    [<Literal>] let ProviderKey   = "_imageProvider"
    [<Literal>] let ModelKey      = "_imageModel"
    [<Literal>] let DimensionsKey = "_imageDim"
```

Image vectors live in a different vector space than text vectors — typically a CLIP-style shared text/image modality. **Dimension-isolation routing requirement:** an image-aware ingestion path must not write image vectors into the same `IVectorStore` namespace as text vectors. The supported route is to maintain a separate scope (or a separate `IVectorStore` registration) for `dataTypeId = ImageRegionDataTypeId`, and to stamp `ImageEmbeddingMetadata.{ProviderKey, ModelKey, DimensionsKey}` rather than `EmbeddingVersion`'s text-side keys (`_embedProvider` / `_embedModel` / `_embedDim`). No default `IImageEmbedder` is registered: there is no honest no-op for image vectors, so consumers null-check the DI lookup and skip image-region work when nothing is wired.

### KB PDF extractor — three-stage flow (post-Phase 14i)

`ToolUp.KnowledgeBase.Server.extractPdf` resolves both `IOcrProvider` and `ITableExtractor` from DI (NoOp fallback at handler entry) and runs:

1. `let! isScanned = ocr.IsScanned bytes "application/pdf"`. If `true`, call `ocr.ExtractText` and emit one chunk per page (page number stamped on `SourceLocation`); skip stages 2–3.
2. `let! tables = tables.ExtractTables bytes "application/pdf"`. For each `ExtractedTable`, build a `SheetData`, run `chunkSpreadsheet`, and emit chunks with `Page` (or `Page 0` if none) on `SourceLocation` and a header line `"<sheet> (table)"` or `"<sheet> (table, part i of n)"` for multi-chunk tables.
3. Run the existing `PdfPig`-based per-page text extraction with `splitByTokens ChunkingConfig.defaults`. Emit text chunks alongside the table chunks (text + table chunks for the same page coexist by design — surrounding prose is preserved at the cost of mild retrieval-time duplication; a per-page table-region exclusion is a deferred follow-up).

With both no-op providers wired (the default), stage 1 is `false`, stage 2 returns `[]`, and stage 3 is the entire path — byte-equivalent to the pre-Phase 14i extractor.

### Companion pattern (deferred)

Concrete implementations land at `src/Ocr/<Name>/`, `src/TableExtractors/<Name>/`, `src/ImageEmbedders/<Name>/`, with the same `.fsproj` + `.Server.props` + `create` factory shape as `src/EmbeddingProviders/Local/`. Deployments wire a companion by importing the props and registering the implementation in DI ahead of `RAGServerApp.run`; the NoOp fallback in `composeWithRAG` only activates when nothing else is registered.

## `IRetrievalTracer` + `EventStoreRetrievalTracer` (Phase 14j)

Retrieval observability is a separate write path from ingestion observability. Ingestion emits `KnowledgeChunkIndexed` / `KnowledgeChunkFailed` from `IngestionBackgroundService.processJob`; retrieval emits `KnowledgeRetrieved` from inside `RetrievalPipeline.Retrieve` after the final stage. Both write through the same `IEventStore`, but the retrieval path runs on the request thread (the trace is emitted before `Retrieve` returns), so the tracer's failure isolation is critical.

### Why a single trace per `Retrieve`

The trace is built from a `RetrievalTrace` record held on the stack across the pipeline run. A `Stopwatch` starts on entry; the stage list grows as each stage runs (`"AuthoriseScopes"`, `"Dense"`, `"Sparse"`, `"RRF"`, `"Rerank"`, `"MMR"`, `"AdaptiveK"`, `"Merge"`); on exit the watch is stopped, top-score and result-count are read off the final list, the query is hashed, and the record is handed to `IRetrievalTracer.Trace`. Per-stage telemetry is collapsed into one envelope because consumers want one row per retrieval, not eight.

### Privacy contract — `QueryHash`, never plaintext

`hashQuery` (in `RetrievalTracers.fs`) is the only path that reads the raw query string for tracing purposes:

```fsharp
let hashQuery (input: string) =
    let bytes = Encoding.UTF8.GetBytes(input ?? "")
    use sha = SHA256.Create()
    let digest = sha.ComputeHash(bytes)
    // hex-encode, lowercase, 64 chars
```

`RetrievalPipeline.Retrieve` calls `hashQuery request.Query` once, stamps the digest into `RetrievalTrace.QueryHash`, and never references the plaintext again. Trace consumers (admin UIs, replay tooling, `IAuditSink` exporters once that ships) join historical traces by hash so plaintext stays in request memory only. `QueryLength` is exposed separately so analysts can spot degenerate inputs (`""`, single-token queries) without reading them.

This is a hard contract: implementing `IRetrievalTracer` is allowed, but reaching back to the original query string from inside `Trace` is not. The interface signature gives the implementation only the `RetrievalTrace` record and the `AccessContext`. A custom tracer that wants plaintext queries needs an entirely separate, non-`IRetrievalTracer` extension point — and a different privacy review.

### Failure isolation

`Trace` is an `Async<unit>`-returning method documented as "must not throw — failure is swallowed and logged at most." `EventStoreRetrievalTracer` honours this: every `eventStore.Write` call is wrapped in `try / with`, with the exception logged at `Warn` and discarded. The contract reasoning:

- Tracing is observability infrastructure. It must never break the primary operation.
- Failed traces leave gaps but don't lose data — the retrieval result the user sees is unaffected.
- A tracer that throws would cause user-facing retrieval failures driven entirely by audit-side issues (`IEventStore` outages, payload-serialisation bugs). That's a worse outcome than a missing trace.

`NoOpRetrievalTracer.Trace` is `async.Return ()` — no allocation, no work. Cost-conscious deployments can opt out; the rest of the pipeline doesn't care.

### `KnowledgeRetrieved` payload — wire format

`KnowledgeRetrievedPayload` (in `RetrievalTracers.fs`) is the JSON-serialised shape, distinct from `RetrievalTrace`. The serialiser uses `FableJsonConverter` (matches every other event in the platform), and `VectorScope` is flattened to a string form (`"platform"` / `"deployment"` / `"team:<id>"`) before serialisation so the payload is readable without the SDK on hand. The reserved literals — `KnowledgeRetrievedEventType = "KnowledgeRetrieved"` and `RetrievalTraceSourceModule = "_platform.retrieval"` — are wire-format contracts; downstream consumers match on these strings. Renaming either is a breaking change for trace consumers.

The event's `ScopeId` is the per-request resolved scope (`"team-<id>"` for team users, `"_platform"` for platform-mode / anonymous requests). Trace consumers filter the event store by scope to recover per-team retrieval streams without scanning the global event store. Indexing shape is identical to other platform events (`PersistentEventStore` puts each `_platform/events/<scopeId>/` partition on its own append-log file).

## Evaluation harness — `src/ToolUp.RAG.Evaluation/`

The evaluation harness is a standalone `Microsoft.NET.Sdk.Web` console project that imports the relevant `.Server.props` files (`ToolUp.Platform`, `ToolUp.AI`, `ToolUp.RAG`, `EmbeddingProviders/Local`) and constructs a real in-memory pipeline. The web SDK is required for ASP.NET Core shared-framework access (the `Server.props` files inject server-side files compiled against `Microsoft.AspNetCore.Http` etc.); nothing serves HTTP — the entry point is a normal `[<EntryPoint>]`.

### Pipeline construction (`Program.buildPipeline`)

Each fixture run gets a fresh temp directory, a per-run `LocalFileStorage`, a `LocalEmbeddingProvider` (TF-IDF, no API key, deterministic for CI), an `InMemoryVectorStore` and `InMemoryBM25Index` with `flushIntervalMs = 50` (so `Index` calls become visible to the next `Retrieve` without waiting on the 2-second debounce), a `NoOpRetrievalTracer` (eval runs don't pollute the event store), and a default-configured `RetrievalPipeline`. The temp directory is deleted in `finally` so consecutive fixtures don't share state.

The lower flush interval is the only deviation from production wiring. Every other component is the same code path the live server uses — fixture queries exercise the real candidate retrieval, RRF fusion, scope authorisation, and merge logic.

### Fixture format (`FixtureLoader`)

JSON fixtures use a flat string form for `VectorScope` (`"platform"`, `"deployment"`, `"team:<id>"`) because Newtonsoft cannot round-trip the `VectorScope` DU without a custom converter, and the harness intentionally avoids depending on `Fable.Remoting.Json` for fixture parsing — fixtures are user-authored JSON, not platform wire payloads. `parseScope` does the lookup and `failwithf`s on unknown values so a typo in a fixture aborts the run rather than producing silent zero recall.

The three JSON-shape records are `[<CLIMutable>]` so Newtonsoft can deserialise them via reflection; F# records with no parameterless constructor cannot otherwise be deserialised. Arrays (`'a array`) rather than F# lists are used at the JSON boundary because Newtonsoft handles them natively; `toCorpusEntry` / `toLabelledQuery` convert to lists for downstream code. Null guards on `corpus` and `queries` keep an empty fixture from crashing the loader.

### Metrics (`Metrics`)

| Metric | Implementation |
|---|---|
| `Recall@K` | `\|relevant ∩ retrieved[..K]\| / \|relevant\|`. `0.0` when no relevant set defined |
| `nDCG@K` | Binary-relevance DCG normalised by the ideal-DCG of `min(\|relevant\|, K)` ones. Position discount `1 / log2(rank+1)` |
| `MRR` | Mean of `1 / firstRelevantRank` per query; `0` for queries with no relevant match |
| `AvgLatencyMs` | Mean of per-query `Stopwatch.ElapsedMilliseconds` |

`buildReport` averages each metric across the per-query results; per-query rows are retained on the report for diagnostic output. The metrics are deliberately classical — recall, nDCG, MRR are the evaluation surface for retrieval quality literature, and the regression-check API thresholds against the same definitions.

### Regression check (`RetrievalEval.detectRegression`)

```
detectRegression : tolerance:float -> baseline:EvalReport -> candidate:EvalReport -> Result<unit, string>
```

Returns `Ok ()` if `Recall@5` and `nDCG@10` are within `tolerance` (default `0.05` absolute) of baseline; `Error msg` otherwise. The two metrics are chosen because they map most directly to user-visible RAG quality — `Recall@5` measures whether the top-5 chunks include the right answer; `nDCG@10` measures whether the right answer is ranked above noise within the top-10 window. CI integration (running the harness on every PR) is the next operational step; the regression-check API is ready and tested.

### Why a separate project, not a test harness

Eval and tests are different shapes. Tests assert exact equality (or near-equality with a fixed tolerance) and fail loud. Eval reports a numeric metric and is consumed by humans first (PR review, dashboards) and CI second. The eval project ships a `Program.fs` that prints a metrics table and writes a JSON `EvalReport` — a structured artefact a CI pipeline can compare against a baseline JSON across runs. A test runner doesn't fit this shape; a runnable console app does.

## BEIR benchmark suite — `src/ToolUp.RAG.Benchmarks/` (Phase 14q)

The eval project at `ToolUp.RAG.Evaluation` is a regression-gate runner over hand-curated JSON fixtures (one specific corpus, exits non-zero on regression). The benchmark project at `ToolUp.RAG.Benchmarks` is a complementary tool: a batch orchestrator that pulls real-world IR ground truth (BEIR datasets — SciFact, NFCorpus, FiQA, …) through the production retrieval pipeline and writes a CSV matrix of quality + latency metrics per (embedder × vector-store × MMR × topK × replicate) cell.

Both projects deliberately import `ToolUp.Platform.Server.props` and the relevant companion props rather than referencing each other; this avoids the dual-injection problem where two assemblies independently compile their own copy of `IRetrievalPipeline` and produce CLR-incompatible types. The benchmark project inlines the small subset of the eval project's symbols it needs (`Fixture`, `LabelledQuery`, `recallAt`, `ndcgAt`, `seedCorpus`) at `EvalCore.fs` — see the file's header comment for the rationale.

The benchmark adds three concerns the eval project doesn't:

- **BEIR loader** (`Beir/BeirLoader.fs`). Streams `corpus.jsonl` / `queries.jsonl` line-by-line so FiQA's ~80 MB corpus doesn't peak memory. `replicate` perturbs corpus IDs as `{origId}-rep{k}` for stress-testing HNSW at scale; the production `CachingEmbeddingProvider` collapses identical content to one underlying embedder call so a 10× replication only costs 1× the embedding budget. `toFixture` adapts to the inlined `Fixture` shape with `Title + "\n\n" + Text` concatenation under `Deployment` scope.
- **Latency aggregation** (`Metrics/LatencyMetrics.fs`). p50 / p95 / p99 / avg / count via nearest-rank percentile. Cheap; the BEIR test sets are 200–650 queries.
- **Pipeline factory** (`Runner/PipelineFactory.fs`). Branches embedder (`Local` / `OpenAI`) and vector-store (`Flat` / `Hnsw`) from CLI flags. Always wraps with `CachingEmbeddingProvider` + `InMemoryBM25Index` to match production retrieval shape.

Reference numbers from the validation runs land here as the production retrieval baseline:

| Dataset | Embedder | Vector store | nDCG@10 | Recall@100 | p95 latency |
|---|---|---|---|---|---|
| SciFact | local (TF-IDF) | flat | _populate after first run_ | _populate_ | _populate_ |
| SciFact | OpenAI text-embedding-3-small | flat | _populate_ | _populate_ | _populate_ |
| FiQA | OpenAI text-embedding-3-small | flat | _populate_ | _populate_ | _populate_ |
| FiQA | OpenAI text-embedding-3-small | hnsw | _populate_ | _populate_ | _populate_ |

These numbers are the comparison point for future retrieval changes (reranker swap, hybrid weighting tweaks, vector-store substitution). A change that drops nDCG@10 by more than 2% absolute on these datasets without a documented reason should be questioned in review.

## Portability rule audit

| Rule | Interface | Status |
|---|---|---|
| 1 — Identity by value | `IEmbeddingProvider`, `IEmbeddingCache`, `IVectorStore`, `IRetrievalPipeline`, `IOcrProvider`, `ITableExtractor`, `IImageEmbedder`, `IRetrievalTracer`, `IngestionQueue`, `ReembeddingQueue` | ✓ All identifiers are `string`, `Guid`, or records — no live handles. `EmbeddingCacheKey = (Version, TextHash)`, `OcrPage`, `ExtractedTable`, `RetrievalTrace` are pure value records |
| 2 — Async at every boundary | All nine interfaces + both queues | ✓ Every method returns `Async<T>` or the service respects `IHostedService`; queue read/write is `Channel`-based (`ValueTask`-bridgeable). `IRetrievalTracer.Trace : RetrievalTrace -> AccessContext -> Async<unit>` |
| 3 — Retry/supervision as data | All nine | ✓ No callback parameters; failure handling is local. Audit events (`KnowledgeChunkIndexed`, `KnowledgeChunkFailed`, `KnowledgeChunkReembedded`, `KnowledgeChunkReembedFailed`, `KnowledgeRetrieved`) are data |
| 4 — Stateless handlers between invocations | `IEmbeddingProvider` (distributed), `IEmbeddingCache`, `IVectorStore`, `IRetrievalPipeline`, `IOcrProvider`, `ITableExtractor`, `IImageEmbedder`, `IRetrievalTracer` | ✓ All parameters passed per call. **Documented exceptions:** `LocalEmbeddingProvider` retains mutable IDF state intentionally; `InMemoryEmbeddingCache` retains LRU state intentionally — both documented in their file headers and disqualify them from distributed use without explicit replication contracts |
| 5 — No cross-scope ordering promises | `IVectorStore.Search`, `IRetrievalPipeline.Retrieve`, `IOcrProvider.ExtractText`, `ITableExtractor.ExtractTables` | ✓ Results ranked by score (search) or document-natural order (extraction); callers do not assume cross-scope ordering |
| 6 — Precision at the lower bound | `IngestionBackgroundService`, `ReembeddingBackgroundService`, `IRetrievalTracer` | ✓ Bounded by semaphore concurrency or by `Stopwatch` precision (`LatencyMs : int64`); no sub-second guarantee claimed |

A distributed `IVectorStore` companion (pgvector, Qdrant, Pinecone) is the key portability story this package sets up: the same `composeWithRAG` wiring, `RetrievalPipeline`, `IngestionBackgroundService`, and `RAGPromptBuilder` work unchanged — only the `InMemoryVectorStore` registration swaps for the new implementation. The Phase 14i extension points follow the same companion-replacement model — `IOcrProvider` / `ITableExtractor` / `IImageEmbedder` slot into DI without touching KB or RAG call sites.

## Known limitations

- **TF-IDF quality.** `LocalEmbeddingProvider` produces ~512-dim sparse vectors over an evolving vocabulary. It has no semantic understanding — synonyms, paraphrases, and cross-lingual matches are invisible to it. Use it for local dev and CI only; production deployments switch to a neural embedding provider.
- **Chunk size policy is in modules, not in the SDK.** A module that returns a 50,000-character "chunk" will embed it as-is. Some embedding providers truncate inputs silently; others error. A chunking-helper function (split by token count with overlap, preserve sentence boundaries) is a sensible follow-up but not yet in package.
- **Cosine is the only similarity.** `InMemoryVectorStore` computes cosine via a dot product of pre-normalised vectors. Use cases that need dot-product (un-normalised) or L2-distance similarity need a different store.
- **Scale ceiling.** `InMemoryVectorStore` keeps every vector in a `ConcurrentDictionary` and scans all in-scope entries per query. Above ~50,000 chunks the per-query CPU dominates; the SDK's documented scale story is **in-memory < 50k → HNSW < 1M → external (Pgvector / Qdrant)**. The HNSW companion ships at `src/VectorStores/Hnsw/` (`HNSW.Net`, MIT) and is wired in via `RAGServerApp.withVectorStore (HnswVectorStore.create blobStorage logger)` before `RAGServerApp.run`. Each scope owns an independent `SmallWorld<float[], float>` graph — scope isolation is structural, not a post-hoc filter — and the on-disk shape is JSON-of-entries (graph rebuilt lazily on first query) so the persisted blob remains implementation-portable. External companions (Pgvector, Qdrant) are the next rung; the `IVectorStore` contract supports them but no concrete companion ships in this round.
- **`Platform` and `Deployment` scopes have no writer path through the SDK.** They are populated out-of-band by administrators (direct blob edits, maintenance scripts). Runtime writes from modules go exclusively to `Team` scope via the post-save hook.
- **No retry on embedding failure.** A failed `GenerateEmbedding` in `IngestionBackgroundService.processJob` emits `KnowledgeChunkFailed` and moves on — the job is not re-enqueued. Bulk re-ingestion is the operational remediation.
- **No auto-vacuum scheduler.** `IVectorStore.Vacuum` is callable but no `BackgroundService` drives it on a timer in this round (Phase 14h). Composing apps invoke `Vacuum` from their own admin / cron path with whatever retention they choose.
- **Lazy-load + reembed coverage.** `ReembeddingBackgroundService` only scans scopes that are already loaded (or pushed onto `ReembeddingQueue` by an admin endpoint). Cold scopes that have never been queried since process start are not auto-detected after a model swap. Composing apps that need full coverage either push every team scope onto the queue at startup (with the corresponding eager-load cost) or accept that re-embed happens lazily, scope-by-scope, as scopes are first touched.
