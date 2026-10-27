# A batch that holds the scope builds its metrics' absent surfaces

**Ships in:** ToolUp.Facts.Server 0.35.0 (`BlobFactStore`). No public surface moves: `IFactStore`, every options
record and every wire type are unchanged. Rides the 0.35.0 draft.

**Affected:** every deployment on the default blob fact store with the metric surface enabled
(`FactSurfaceOptions.defaults`, 512 heads). Answers do not change; how many blobs the first read after an upload
downloads does.

## What changes

1. **A first upload leaves its metrics' surfaces warm.** An `AssertBatch` that loads the whole scope to find
   lineage heads (the point-read index below its threshold, crossing it in this batch, or incomplete) now builds the
   Phase 702 surface of every metric it touched that had none, when the scope reaches `MinimumHeads`. It builds from
   the facts it read plus the facts it wrote, through the read-time rebuild's own path, so the snapshot is the one a
   read would have built. Measured on one batch into an empty scope: the first series read went from 22,503
   downloads to 2,502 (its points) at 20,000 facts and from 2,253 to 252 at 2,000; the first population read from
   20,011 and 2,011 to 10 (its page). The fact browse's first `QueryRows` after a 2,400-fact table run went from
   2,421 downloads to 20.
2. **One extra blob listing per such batch**, after its writes, and one snapshot write per absent metric. A batch
   that finds the index complete, and a scalar `Assert`, are unchanged: their metrics still build on first read.

The posture and the measurements:
[`docs/platform/facts.md`](../platform/facts.md#a-first-upload-leaves-its-metrics-surfaces-warm-phase-1124).

## What to do

Nothing. The build is best effort and can never fail a batch; a snapshot it could not write is built by the next
read, as before. `FactSurfaceOptions.disabled` turns it off along with every other surface write.

## Verifying

```powershell
dotnet ./src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Phase 1124"
```

The second case asserts that a snapshot built at assert and one rebuilt by a read over the same log are byte-equal.

## Rolling back

Compose `FactSurfaceOptions.disabled`, or delete the snapshots (`FactSurface.drop`): they are derived, and the next
read rebuilds them.
