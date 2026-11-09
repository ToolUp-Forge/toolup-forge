# Phase 1083 — `VectorScope` projections, `ChunkId`, and the scored pool

**What changes for you.** Nothing you persisted moves: every store blob name (`_rag/{scope}/…`), every `scope`
column value, every chunk id and every audit stream id is byte-identical before and after. The surface gains
the functions that spell them, a scored sibling of `IRetrievalPipeline`, and one threshold rule. One type
moves assembly (section 4).

## 1. A vector scope is spelled by `VectorScope`, never by hand

`ToolUp.Platform.VectorKnowledgeTypes.VectorScope` (Platform.Core) holds the three renderings and their inverses:

| Function | What it spells | Example |
|---|---|---|
| `VectorScope.key` / `tryOfKey` | the store key every vector store and keyword index persists under | `team:acme`, `user:u1`, `platform`, `deployment` |
| `VectorScope.container` / `ofContainer` | the storage container a principal's scope lives in, via `ScopeKey.container` | `team-acme`, `user-u1` |
| `VectorScope.auditScopeId` | the event-store stream a scope's RAG audit rows are written under | `acme`, `user:u1`, `_platform`, `_deployment` |

`VectorScope.DeploymentContainer` is the `_deployment` container (`ScopeEnumeration.DeploymentContainer` now
names it). `ofContainer` is the one derivation from a `StorageScope` to the scope its chunks belong to: a team
container is the team's, the deployment container is `Deployment`, and every other container is that caller's
own `User` scope keyed by the scope id. It never yields `Platform`: only the platform knowledge API writes there.

```diff
- let scopeKey = match scope with Platform -> "platform" | Team t -> $"team:{t}" | …
+ let scopeKey = VectorScope.key scope
- let vectorScope = match StorageScope.tryTeamId scope with Some t -> Team t.Value | None -> User scope.ScopeId
+ let vectorScope = VectorScope.ofContainer scope
```

`RetrievalTracers.auditScopeId` (internal) is gone; `VectorScope.auditScopeId` is the same spelling.

**Behaviour that moved with the derivation.** Three readers that re-derived a scope from a container by their
own rule now read `ofContainer`, so they agree with what ingestion wrote: the knowledge base's request scope maps
the `_deployment` container to `Deployment`; the retention sweep maps a session container to its session id's
`User` scope (it read `User "session-{id}"`, a scope nothing was written under); and the RAG offboarding hook
deletes a `user-{id}` scope's chunks from that user's scope (it deleted `Team "user-{id}"`, which held nothing).

The local embedder's per-scope state key (`team-{escaped id}`) is a different, escaped encoding and stays its
own, by design: it must not move if the container grammar does.

## 2. A chunk id is a `ChunkId`

```fsharp skip=fragment
type ChunkId = { DocumentId: string; Ordinal: int }
ChunkId.render { DocumentId = docId; Ordinal = i }   // "{docId}:chunk:{i}"
ChunkId.tryParse "doc:chunk:3"                        // Some { DocumentId = "doc"; Ordinal = 3 }
ChunkId.ofDocument docId count                        // ordinals 0 .. count - 1
```

`IVectorStore` and `ISparseIndex` keep `chunkId: string`. Replace `sprintf "%s:chunk:%d" docId i` with
`ChunkId.render`, and a hand parse of `":chunk:"` with `ChunkId.tryParse` (it reads at the last marker and
returns `None`, never throws, for a foreign id). The post-save hook keys its chunks by file name in the same
grammar. `SemanticSearchHandler.docsLinkByChunkId` now links only a chunk id that parses.

## 3. Retrieval returns its score space

```fsharp skip=fragment
// IRetrievalPipeline.fs (ToolUp.Platform.Server.Infra)
type ScoredPool = { Space: RetrievalScoreSpace; Unit: float; Matches: VectorMatch list; Relevance: VectorMatch list option }
type IScoredRetrievalPipeline = abstract RetrieveScored: RetrievalRequest -> AccessContext -> Async<ScoredPool>
RetrievalScoreSpace.admits space threshold score   // THE threshold rule
```

`IRetrievalPipeline` is unchanged; the scored sibling is a capability by probe. The default `RetrievalPipeline`
implements it (`Retrieve` returns the pool's `Matches`, byte for byte) and so does
`StaticCorpusRetrievalPipeline`, declaring `Cosine`. **A custom pipeline needs no change.** If you want its
readers to see its real space, implement `IScoredRetrievalPipeline` beside `IRetrievalPipeline`. Until you do,
`withRAG` reads its matches in the space the composition implies (`ScoredPool.declaring`).

A reader that compares `VectorMatch.Score` with a threshold calls `RetrievalScoreSpace.admits` with the pool's
space instead of comparing raw floats. The prompt builder's `MinScore` gate, the startup validator's reachability
finding and the telemetry read it.

**Telemetry.** `RollingRagTelemetry` also implements the new `RagTelemetry.IScoredRagTelemetry` and is handed the
chunk pool: a pool with chunks none of which clears `MinScore` is now a **low-score miss** (it was counted as
empty, so `/health/rag` never reported one). In the fused space the pool's best candidate is `1.0` by
construction, so a fused deployment reports hits and empties only: the space is relative and carries no absolute
floor to miss. A custom `IRagTelemetry` is unchanged and keeps receiving `RecordRetrieval`.

## 4. `RetrievalScoreSpace` moved to the seam

The union now lives beside the interface that returns it, as
`ToolUp.Platform.Infra.IRetrievalPipeline.RetrievalScoreSpace` (assembly `ToolUp.Platform.Server.Infra`), so a
pipeline outside `ToolUp.RAG.Server` can declare its space. `ToolUp.RAG.RetrievalPipeline.RetrievalScoreSpace`
remains as an abbreviation of it, and its module (`label`, `stageMark`, `ofComposition`, `upperBound`,
`thresholdFinding`) is unchanged, so F# source compiles as before. A binary compiled against the old type
identity must be rebuilt.

## Verification

`VectorScopeProjectionTests` pins every rendering against the blob names the in-memory stores write;
`SparseIndexCompositionTests` pins `admits` per space; `RagTelemetryTests` pins each composition's space and its
classification. Rollback is the previous package version: nothing at rest changed.
