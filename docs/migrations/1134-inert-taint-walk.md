# The disclosure gate skips an inert taint walk

**Ships in:** ToolUp.Facts.Core 0.36.0 (`DisclosureTaintConfig.isTaintInert`) and ToolUp.Facts.Server 0.36.0
(`FactDisclosureGate`, `BlobFactStore`). Rides the 0.36.0 draft (untagged, publicly unpinned): one function is
added, and no record field, union case or interface member changes.

**Affected:** nobody has to change code. A deployment whose `DisclosureTaintConfig` registers only `Plain` policies,
with no `ContributorScope` and no declassification routine, stops loading the derivation graph on every disclosure
check. That graph load is a download of every fact in the scope on the blob store. Every chart, browse page and
retrieval then costs what its own read costs. A deployment that registers a `TaintPropagating` policy, a contributor
scope or a declassifier is unchanged.

## What changes

1. **`DisclosureTaintConfig.isTaintInert config`** is true when no registered policy is `TaintPropagating`, none
   declares a `ContributorScope`, and no declassification routine is registered. Under such a config the taint walk
   can seed no taint, record no crossing and name no contributor, so it would end where an empty walk starts. An
   empty config is inert.
2. **The gate decides an inert config's checks without the graph**, in the id, held and row forms alike. The
   policies still resolve each `Restricted` ref's per-surface stance, which reads no graph. Verdicts and audit rows
   are unchanged: the disclosure suite compares them, with and without the skip, on every surface, over a corpus
   with the shapes of the taint, multi-party, budget and egress fixtures.
3. **Concurrent cold reads of one metric share one surface convergence.** `BlobFactStore` runs one rebuild or fold
   per (scope, metric, listed census) in process, and concurrent readers await it. A cold page of three charts
   rebuilds the snapshot once and writes it once, where each chart used to rebuild it.

## What to do

Nothing. A config that should taint must say so (`Mode = TaintPropagating`). A `Plain` policy never tainted
anything, and it still does not.

The posture and the measurements:
[`docs/platform/facts.md`](../platform/facts.md#a-charts-disclosure-check-costs-nothing-when-the-policies-cannot-taint-phase-1134).

## Verifying

```powershell
dotnet ./src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Phase 1134"
```

The browse cases count a warm chart's fact downloads under the inert registration (zero) at 2,000 and 20,000 facts.
They also count a cold three-chart page (one scope read, one snapshot write, and each chart equal to its answer
alone), and a chart under each live ingredient (the graph is loaded). The disclosure cases hold the verdicts and
audit rows equal with and without the skip.

## Rolling back

Pin ToolUp.Facts.Server to 0.35.0. A deployment that wants the walk under a `Plain`-only vocabulary has no
behaviour to recover: the walk never changed a verdict there.
