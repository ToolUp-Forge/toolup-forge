# Migration — Phase 205: blob-corruption chaos / fault-injection pack

**Status:** **test-tier only — no public runtime surface, no behaviour change (GP 11/13).** No
consumer action. This migration doc exists so the SDK-adoption matrix carries a row; every consumer
cell is ⛔ N-A. A consumer that never runs the pack is byte-for-byte unchanged and pays nothing.

## What changes

A fault-injection / chaos test pack lands, exercising the shipped [Phase
116](116-blob-rmw-integrity.md) fail-closed blob read-modify-write integrity paths under corruption
and concurrency — proving they fail **closed** rather than degrading to silent-empty or
last-writer-wins.

- **`ToolUp.Platform.Testing/Testing/FaultInjectingBlobStorage.fs`** (NEW) — a reusable, inert-by-
  default `IBlobStorage` decorator. With no faults registered it is a pure pass-through; faults are
  opt-in per `(container, blobName)`, so a test can seed valid data with faults disarmed and then arm
  exactly the blob under test, leaving sibling blobs pristine. Fault modes: `DownloadTruncate`
  (short read), `DownloadCorrupt` (seeded deterministic byte-flips), `DownloadGarbage` (arbitrary
  payload), `UploadPartial` (torn write), `UploadDrop` (silently lost write), `UploadFail` (hard
  write error), plus `WidenReadWriteGap` (widen the read→write window so an *unlocked* RMW is likely
  to lose a write) and `InjectBeforeUpload` (a racing-writer seam). Byte-corruption is a fixed XOR
  pattern keyed by a seed, so the suite is reproducible, not flaky.
- **`ToolUp.Platform.Tests/InProcess/BlobCorruptionChaosTests.fs`** (NEW) — the registered suite
  (`BlobCorruptionChaos`, deterministic seed `20260619`), wired into `Program.fs`'s `allTests`
  (Expecto only runs the supplied list). It drives the three shipped Phase 116 sites:
  - **Pending invites** (`InMemoryPendingInviteStore`). A torn write corrupts the
    `_platform/pending-invites.json` map blob; the next `Upsert` decodes fail-closed — the corrupt
    blob is quarantined aside, the operation returns `StorageFailed`, the canonical blob self-heals to
    empty, and **no map derived from a failed decode is ever written** (no empty-plus-one overwrite).
    A separate case characterises the single-instance lost-write boundary: a silently-dropped write
    leaves the prior entry durable and erases nothing (detecting the lost write itself is the deferred
    ETag-CAS story).
  - **Share-token `MarkUsed`** (`BlobShareTokenStore`). A corrupt / truncated / garbage claim read
    makes `MarkUsed` fail closed (`StorageFailed`) while a sibling token's blob stays fully usable
    (claims are one-blob-per-token, so a corrupt claim cannot erase its siblings). Under N=10
    concurrent `MarkUsed` against a `UseLimit = 1` token — with the read→write window widened — the
    `claimWriteLock` admits **exactly one** and surfaces the losers as `UseLimitExceeded`.
  - **KB `index.json`** (`IndexStorage.upsertIndexEntry`). N=8 concurrent additive upserts to one
    container — window widened — all appear in the index; the per-container lock loses no entry and
    orphans no index.

The stores under test (`InMemoryPendingInviteStore`, `BlobShareTokenStore`,
`KnowledgeBase.ServerIndexStorage`) are exercised under fault but **unchanged**.

## Scope note — the ETag half of Phase 116 is still deferred

Phase 205's spec was authored against the ETag-conditional-write half of Phase 116 (a generic guarded
`BlobMapStore` decorator over `IBlobStorage.UploadWithETag`). That half remains **deferred**: it is
gated on [Phase 9c](../../../Diametrical/roadmap/phases/09c-distributed-task-framework-companion-support.md)
half-2 (`UploadWithETag`), which has not shipped — so `BlobMapStore.fs` does not exist and is not
referenced by this pack. The pack therefore targets the *shipped* Phase 116 halves (fail-closed
decode + quarantine; single-instance `SemaphoreSlim` guards). The cross-replica CAS properties
(a dropped write surfaced as an error rather than silently lost; `UseLimit = 1` holding across
replicas) land with the ETag adoption; the lost-write case here documents the current single-instance
boundary honestly. When the ETag surface ships, extend this pack with the CAS cases against the new
`BlobMapStore` seam.

## Consumer action

None. No package runtime surface changed; no recompile required beyond a normal SDK bump. The new
`FaultInjectingBlobStorage` decorator is additive test kit in `ToolUp.Platform.Testing` — available
to consumers who want to chaos-test their own blob-backed RMW paths, ignorable otherwise.

## Verification

- `dotnet build ToolUp.Forge.sln` clean.
- `dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj --filter-test-list BlobCorruptionChaos`
  — the `BlobCorruptionChaos` list is green (7 tests): every injected fault yields a typed error +
  quarantine and never a map from a failed decode; a sibling token survives; `UseLimit = 1` admits
  exactly one of N; concurrent KB upserts lose no entry. Seeds deterministic.

## Rollback

Remove the two `<Compile>` entries (`FaultInjectingBlobStorage.fs` in `ToolUp.Platform.Testing`,
`BlobCorruptionChaosTests.fs` in `ToolUp.Platform.Tests`) + the `BlobCorruptionChaosTests.tests`
registration in `Program.fs`, and delete the two files. No production code is touched, so rollback is
inert — it only drops the regression gate over the Phase 116 fail-closed RMW paths.
