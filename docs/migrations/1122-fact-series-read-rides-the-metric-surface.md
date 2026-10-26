# A fact series read rides the metric surface; a threshold-crossing batch indexes the scope

**Ships in:** ToolUp.Facts.Server (`BlobFactStore`). No public surface moves: `IFactStore`, `FactQuery`,
`FactBrowseApi` and every options record are unchanged, so nothing recompiles and no client regenerates. Rides the
0.34.1 patch.

**Affected:** every deployment on the default blob fact store whose scope holds at least 512 facts (the
`FactSurfaceOptions.defaults` and `FactIndexOptions.defaults` thresholds). Answers do not change; how many blobs a
read downloads does.

## What changes

1. **The series read stops downloading the scope.** A current-heads `IFactStore.Query` that names a metric and no
   subject — the fact browse's chart read (`QuerySeries`, Phase 1113) — used to enumerate every fact in the scope.
   It now reads the metric's Phase 702 surface and downloads only the heads whose period and method the query
   admits. Measured: 20,000 downloads before and 2,250 after for a 2,250-point read at 20,000 facts; 2,000 and 225
   at 2,000 facts.
2. **A batch that crosses the index threshold indexes the scope.** An `AssertBatch` that takes a scope from below
   512 facts to at or above it now writes a point-read index leaf for every fact. Before, a first upload into an
   empty scope wrote none, and the first point read after it (a fact-detail open, `GetFact`) enumerated the scope.
3. **The first chart of a metric with no surface yet builds it** — one enumeration, once per metric, as the first
   population read of that metric always has.

Why the surface rather than a second index, and the per-read scaling posture:
[`docs/platform/facts.md`](../platform/facts.md#how-the-blob-store-serves-a-series-read-phase-1122).

## What to do

Nothing. To keep the pre-0.34.1 read path exactly, compose the store with `FactSurfaceOptions.disabled`
(`BlobFactStore.createWithSurface`); that also turns off the surface-backed population read. A scope filled before
the upgrade by one large batch has no index leaves until its first point read writes them, which costs that one read
an enumeration.

## Verifying

The `Phase 1122` test lists in `ToolUp.Platform.Tests` count downloads through a storage probe:

```powershell
dotnet ./src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Phase 1122"
```

## Rolling back

Compose `FactSurfaceOptions.disabled` for the series read, as above. The index leaves a crossing batch writes are
derived and safe to delete; `RebuildIndex` writes them back.
