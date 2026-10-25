# Phase 1082 — defects in RAG and the knowledge base

**What changes for you: three things a reader can see, and nothing else.** No public member was removed or
retyped. One locality declaration was added (`IEmbeddingCacheLocality`). The rest are fixes inside the
existing surface.

## 1. A Data Manager file on a non-team surface stays with its user

The post-save vectorisation hook chose a Data Manager file's vector scope from its storage container: `team-`
went to the team's scope and **every other container went to `Deployment`**. A `user-{id}` container is one
caller's private storage (Individual mode, AuthenticatedEphemeral, an anonymous session), and `Deployment` is
readable by every authenticated caller, so one user's saved file was retrievable by every other user of the
deployment. The `VectorScope.Deployment` hard rule forbids that shape; the knowledge base's own uploads had
already been routed to `User` scopes.

The hook now derives the scope the way the knowledge base does:

| Container | Vector scope |
|---|---|
| `team-{id}` | `Team id` (unchanged) |
| `_deployment` (`ScopeEnumeration.DeploymentContainer`) | `Deployment` |
| anything else (`user-{id}`, a session container) | `User id`, readable only by that caller |

**If your deployment relied on Data Manager chunks being deployment-wide on a non-team surface, it was relying
on the cross-user leak the hard rule forbids.** Content meant for everyone belongs in the deployment
container (or a team) deliberately. Chunks indexed before this change stay where they were written; re-save
the file to move them into its owner's scope.

## 2. One audit-scope spelling for one team's RAG events

Four emitters wrote one team's RAG events under different event-store scope ids. Every one now goes through
one function, `RetrievalTracers.auditScopeId`:

| Vector scope | Event-store scope id |
|---|---|
| `Team id` | `id` — the bare team id, the persisted stream key every other team audit row uses |
| `Platform` | `_platform` |
| `Deployment` | `_deployment` |
| `User id` | `user:id` |

What moved, for an event-stream reader:

- `KnowledgeRetrieved` and `KnowledgeRetrievalMiss` for a team caller were written under `team-{id}`; they are
  now under the bare `{id}`, beside `KnowledgeQueryRejected`, `KnowledgeChunkReembedded` /
  `KnowledgeChunkReembedFailed` and `KnowledgeVacuumCompleted`, which already used it. A caller with no team
  still writes under `_platform`.
- `KnowledgeVacuumCompleted` for a `Deployment` sweep was written under `_platform`; it is now under
  `_deployment`, as the re-embedding events for that scope already were.

A reader that joined the RAG families by `team-{id}` reads `{id}` instead. Rows already written keep the scope
they were written under.

## 3. What `/health/rag`'s hit / low-score / empty classification can and cannot see

No behaviour moved here. Under the default composition (a keyword index composed) the pipeline fuses the dense
and keyword rankings and normalises the pool onto `[0, 1]`, so the best returned candidate scores exactly
`1.0` whatever the corpus. The telemetry classifies from that returned pool, so under the default composition
`RetrievalAvgTopScore` reads `1.0` and a `LowScoreMiss` is recorded only when a configured `MinScore` drops
every chunk. Classifying "in the fused space" cannot change that: the absolute relevance signal is gone before
the reader sees the scores. The behaviour is pinned (`RagTelemetryTests`) so the change that carries an
absolute signal out of the pipeline turns the pin red deliberately. The dense-only composition
(`withoutSparseIndex`) still classifies on cosine scores, as before.

## The fixes with no reader-visible change

- **Every terminal knowledge-base status is announced.** `"KnowledgeBase.IngestionStatus"` was published only
  when a chunk callback settled a document `Complete` or `Failed`. An unsupported format, a missing OCR
  capability, a recognised-but-empty file, an unchanged re-upload and an empty narrative commit set their
  status directly and announced nothing. They now publish through the same publisher, with the same key and
  payload shape. `Outcome` gains the values `"UnsupportedFormat"`, `"OcrUnavailable"` and `"UploadRejected"`,
  with the detail in `ErrorReason`; a subscriber that matched only `"Complete"` / `"Failed"` ignores them, as
  the AI side panel does. An empty narrative commit also now settles its persisted index entry, which stayed
  `Queued`.
- **The knowledge base's notification channel is never null.** The base composition always registers an
  `INotificationChannel` (a no-op one when notifications are off), so the knowledge base now resolves it with a
  typed probe that refuses at construction, and the null guards are gone. A host that builds the knowledge
  base's dependencies without the `ServerApp` composition must register a channel.
- **The Data Manager observer's count is monotone and cheap.** A chunk failure no longer resets the indexed
  count, so a retried chunk completes the file, and a document's "is this a Data Manager file" answer is read
  once per document instead of once per chunk of every knowledge-base document.
- **The HNSW store's scope map** is a `ConcurrentDictionary`, read lock-free and written under the existing
  lock — the convention the two in-process stores use. A first upsert into a new scope could corrupt the map
  under a concurrent search.
- **The durable queue's depth gauge** is re-read from the store after a release or a stranded-lease reclaim,
  either of which can drop a job at its attempt cap. `/health/rag` reported a dropped job as still queued.
- **CSV is read by `ToolUp.Tabular`'s RFC 4180 reader.** A quoted field keeps its commas and line breaks; an
  unquoted file reads exactly as before, so its chunk text is unchanged. A record with malformed quoting is
  skipped.
- **The embedding cache declares its locality.** The cross-replica warning used to test for the
  `InMemoryEmbeddingCache` type, so a decorator over it lifted the warning. `InMemoryEmbeddingCache` now
  declares `EmbeddingCacheLocality.InProcess` through `IEmbeddingCacheLocality`, and the predicate reads the
  declaration. **If you wrap the in-memory cache**, forward the declaration so the warning stays:

  ```diff
   type MeteredCache(inner: IEmbeddingCache) =
  +    interface IEmbeddingCacheLocality with
  +        member _.CacheLocality = EmbeddingCacheLocality.declared (box inner)
       interface IEmbeddingCache with
           ...
  ```

  An undeclared cache reads as cross-replica, as an unrecognised cache did before.
- **The RAG bounds validator's doc is true.** It said the setters' clamps were all one-sided lower bounds. The
  setters clamp lower bounds (and `MinScore` / `MmrLambda` onto `[0, 1]`) and the clamp is warned at startup
  (`withIngestionConcurrency` now records its clamp too). The validator's lower-bound arms stay: `RAGServerApp`
  is a public record, and a value set by record update reaches them unclamped.

## Verification

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "Vectorisation hook scope"`
and the same for `RAG telemetry and audit scope`, `KnowledgeBase CSV extraction`, `Ingestion status stream`,
`HnswVectorStore`, `Phase 509`, `Phase 173` and `Phase 9m.B — RAG-specific config validators`.

## Rollback

Revert the phase's commits. The vector scope of chunks indexed while it was live does not move back on its
own; re-save the affected files.
