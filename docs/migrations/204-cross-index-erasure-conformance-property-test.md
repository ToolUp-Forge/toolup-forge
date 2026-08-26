# Phase 204 — cross-index erasure conformance (property test)

**Consumer action required: none.** This phase is test-only. It adds no public API, no
runtime code path and no configuration knob, so a consumer that upgrades and never runs the
SDK's own test suite is byte-for-byte unchanged (GP 11) and pays nothing at runtime (GP 13).

Read on only if you implement `IVectorStore` or `ISparseIndex` yourself, or if you compose
`IIndexLifecycle` and want to know what is now guaranteed.

## What changed

Phase 115 introduced `IIndexLifecycle` so a deletion or a data-subject erasure fans out
across **every** retrieval index a deployment composes — the dense `IVectorStore`, the BM25
`ISparseIndex`, and the embedding cache — rather than reaching the vector store and silently
missing the sparse leg. That guarantee was asserted once, at the phase's exit criterion, over
three hand-written interleavings.

Phase 204 turns it into a property. `ToolUp.Platform.Tests` gains
`InProcess/CrossIndexErasureConformanceTests.fs`, which generates randomised
ingest / delete / erase sequences across several `VectorScope`s, drives them through
`DefaultIndexLifecycle.{DeleteChunk, DeleteDocument, DeleteByScope, Erase}`, and re-asserts
after **every** command that nothing deleted or erased is retrievable from any leg —
including through the fused hybrid path a grounded AI answer is actually built on, and
including across a simulated process restart that re-hydrates both indexes from blob storage.

## What the property actually asserts

Each generated command is interpreted twice: once against the real stores, once against a
pure model of what should remain. Both directions are checked after every step.

| Assertion | Why it is there |
|---|---|
| No deleted / erased chunk appears in a dense `Search` | the original Phase 115 claim |
| No deleted / erased chunk appears in a BM25 `Search` | the leg Phase 115 exists because of |
| No deleted / erased chunk appears in hybrid retrieval | the surface an AI answer is grounded on |
| Every **live** chunk is still retrievable from every leg | an index that returned nothing would satisfy the three rows above vacuously |
| The invariant still holds after a restart | a chunk removed from memory but surviving at rest resurrects exactly here |
| `Erase(dryRun = true)` leaves both indexes, the cache, and both persisted snapshots byte-identical | a preview that mutates is not a preview |
| `IndexLifecycleReport.Succeeded` names exactly the indexes composed | a report naming an uncomposed target is the same lie as one omitting a composed one |
| `ErasureSummary.RecordsAffected` sums the per-index counts | one leg's count alone under-reports a hybrid erasure |

The generator is a seeded `System.Random` rather than a property-testing framework: the test
pack carries no such dependency, and adding one is a CPM and supply-chain change a test-only
phase has no business making. Every failure message carries the seed and the command prefix,
so a failure is reproducible from the message alone.

## If you implement `IVectorStore` or `ISparseIndex`

The pack runs against the in-process reference implementations, so it is a conformance bar
rather than a contract pack you can bind your own store into today. The behaviours it pins
are nonetheless the ones any implementation must honour, and they are worth reading as a
checklist:

- `DeleteChunk` is a **tombstone** write on the dense store — filtered from `Search`, still
  visible to `ListChunks scope true`. The sparse leg has no tombstone tier, so the same call
  is a hard delete there.
- `Erase` with `ErasurePolicy.HardDelete` must **physically purge**, not tombstone.
- `Erase` with `dryRun = true` must count without mutating anything, the persisted snapshot
  included.
- A chunk names a subject when the subject id appears in `Content` **or in any metadata
  value** — identically on both legs, or an erasure clears one index and not the other.
- Deletion must survive a process restart. A store that removes an entry from memory without
  the persisted snapshot following is a right-to-be-forgotten failure, not a caching detail.

## Known defect this pack surfaced

The pack ships one **pending** test, `KNOWN DEFECT: DeleteByScope on a lazily-hydrated scope
is undone by the next read`. It is not a test-harness artefact — it is a live defect in the
in-process reference stores that the property found on its first run, and it is deliberately
left visible rather than designed around silently.

`InMemoryVectorStore` and `InMemoryBM25Index` both decide whether a lazily-hydrated `Team` /
`User` scope has been loaded by asking whether they currently hold anything for it, so
emptiness reads as absence. `DeleteByScope` empties the scope in memory and marks it dirty,
but the persisted snapshot survives until the next flush — so the next read re-hydrates the
scope from that snapshot and restores everything the wipe removed. `Platform` and
`Deployment` are unaffected: they are loaded eagerly at construction and never re-hydrated.

Fixing it is a runtime change outside this test-only phase. Until it lands, the restart
property runs without `DeleteByScope`; the pending test carries the full reasoning and the
minimal reproducer, and flipping it from `ptestAsync` to `testAsync` is the whole of the
verification once the hydration guard is corrected.

## See also

- [`docs/migrations/`](.) — the per-refactor migration index.
- Phase 115 — the `IIndexLifecycle` fan-out seam this pack certifies.
- Phase 9h — the data-subject-erasure surface the guarantee ultimately serves.
