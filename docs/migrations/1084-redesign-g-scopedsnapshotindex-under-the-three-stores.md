# Phase 1084 — one `ScopedSnapshotIndex` under the three in-memory stores

**What changes for you.** Nothing you persisted moves: the three stores' blob names (`_rag/{scope}/index.json`,
`_rag/{scope}/bm25.json`, `_rag/{scope}/hnsw-index.json`) and their JSON shapes are unchanged. Two things a
deployment can observe change, and one exception type moves.

## 1. One unreadable-snapshot exception

`InMemoryVectorStore`, `InMemoryBM25Index` and `HnswVectorStore` share one base, `ScopedSnapshotIndex`
(`ToolUp.Platform.Infra`, in the Platform.Server Infra part). It raises one exception, which moved there:

| Before | After |
|---|---|
| `ToolUp.RAG.InMemoryVectorStore.RagScopeSnapshotUnreadableException` | `ToolUp.Platform.Infra.RagScopeSnapshotUnreadableException` |
| `ToolUp.RAG.VectorStores.Hnsw.HnswVectorStore.HnswScopeSnapshotUnreadableException` | `ToolUp.Platform.Infra.RagScopeSnapshotUnreadableException` |

The members (`ScopeKey`, `BlobLocation`, `Reason`) are unchanged; a new `Fault` says why the scope was refused:
`SnapshotFault.Unreadable` (the blob exists but the storage could not read it) or `SnapshotFault.Corrupt` (it read
but did not decode, under the refusing policy, section 2).

```diff
- | :? HnswVectorStore.HnswScopeSnapshotUnreadableException as ex -> …
+ | :? RagScopeSnapshotUnreadableException as ex -> …          // open ToolUp.Platform.Infra
```

The codemod rewrites both spellings (`dotnet run --project Build.fsproj -- Codemod --phase 1084`, rules
`1084-hnsw-snapshot-exception` and `1084-rag-snapshot-exception-home`) to the fully qualified name. A file that
named `RagScopeSnapshotUnreadableException` bare under `open ToolUp.RAG.InMemoryVectorStore` adds
`open ToolUp.Platform.Infra`.

## 2. The corruption policy reaches BM25 and HNSW

`TOOLUP_RAG_REFUSE_ON_INDEX_CORRUPTION` used to mean something only to the vector store: a corrupt BM25 or HNSW
snapshot was logged and started empty whatever the variable said, and only the vector store reported it to the
telemetry counter, the `KnowledgeIndexLoadFailed` audit and the admin `SystemMessage`. Now all three stores follow
it and report it:

- **Variable unset (the default):** a corrupt scope starts empty, as before, and every store now records that it
  did (counter, audit row, system message, once per scope per process).
- **Variable set:** a corrupt scope is refused with `RagScopeSnapshotUnreadableException` (`Fault = Corrupt`) on
  every access, searches included, until the blob is repaired or deleted. **A BM25 or HNSW deployment that set the
  variable expecting it to do nothing for those stores now gets the refusal it asked for.** Under the composition a
  corrupt platform or deployment snapshot fails the boot (it was the vector store's behaviour; it is now every
  composed store's). Before, a refused vector-store scope raised a bare `Exception` and emitted nothing; it now
  raises the typed exception and reports first.

The composition (`RAGCompose.withRAG`) reads the variable once and passes the policy to the stores it builds. A
store you construct yourself takes the new optional `corruptPolicy` argument (`CorruptPolicy.StartEmpty` or
`CorruptPolicy.Refuse`), and reads the variable when you leave it out, so an `HnswVectorStore` you supply through
`withVectorStore` follows it too. Pgvector keeps its own reading of the variable: it has no snapshot to hydrate.

## 3. Constructors do no I/O

No store constructor reads storage any more; each used to load the platform and deployment scopes with
`Async.RunSynchronously`. They load on first access, or when the new `Start()` member runs. The composition starts
the stores it builds at boot (`RetrievalStoreFlushService`, which also flushes them at stop). A store you construct
and supply yourself needs nothing: its first search loads the scopes it reads.

## 4. New, optional constructor arguments

| Store | Added |
|---|---|
| `InMemoryVectorStore` | `?corruptPolicy` |
| `InMemoryBM25Index` | `?telemetry`, `?auditLog`, `?notifications`, `?corruptPolicy` |
| `HnswVectorStore` | `?telemetry`, `?auditLog`, `?notifications`, `?corruptPolicy` |

Named-argument call sites compile unchanged. The new arguments are what a corrupt load reports to; without them a
store logs.

## 5. Smaller behaviour changes

- A keyword-index scope that holds no document has no `bm25.json` at rest: emptying a scope one chunk at a time now
  deletes its blob at the next flush, exactly as `DeleteByScope` already did. Reading it back is the same empty
  scope.
- `HnswVectorStore.ListScopes` no longer lists a loaded scope that holds no entry (a wipe not yet flushed), matching
  `InMemoryVectorStore`.
- A snapshot whose JSON is the literal `null` is corrupt (it was an empty BM25 scope, and a crash in the vector
  store).

## Building your own store on the base

A store that keeps a scope's corpus in memory over `IBlobStorage` may build on `ScopedSnapshotIndex<'Snapshot, 'Live>`:
a `SnapshotLayout`, a `BlobCodec<'Snapshot>`, the empty live form, `materialise` and `snapshot` between the two, and
a `SnapshotPolicy`. Its `Mutate` hydrates first and marks dirty; `Read` never loads; `EnsureLoaded` refuses an
unreadable scope and `EnsureLoadedForSearch` degrades it. A store that does not use the base is unchanged.

## Rollback

Pin the previous SDK version. Nothing persisted changed, so a rollback reads every blob this version wrote.
