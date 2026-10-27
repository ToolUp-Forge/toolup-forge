# A chart is one download — the series read answers from the metric surface's rows

**Ships in:** ToolUp.Facts.Server 0.35.0 (`HeldFactRow`, `IHeldFactRowDisclosureGate`, `FactDisclosureGate`,
`FactBrowseHandler`, the blob store's metric surface). Rides the 0.35.0 draft (untagged, publicly unpinned,
breaking-class since Phase 1123): the record, its module and the interface are additions, and nothing existing
changes shape.

**Affected:** nobody has to change code. A deployment on the blob fact store sees its browse charts download one
surface snapshot instead of one fact per point. A deployment with its own `IHeldFactDisclosureGate` registered (a
decorator) keeps the fact path until that gate also offers the row form.

## What changes

1. **The surface rows carry what a point needs.** Each row of a metric surface now holds the head's period label
   and, for a non-scalar head, its value. The snapshot format is version 3; a version-2 snapshot is rebuilt from the
   fact log on its first read after the upgrade — one enumeration per metric, once, as for any absent snapshot.
2. **The browse's chart reads rows.** `FactBrowseHandler.querySeries` builds its points from the rows when the store
   can project them and the gate can judge them; the points, their order, the cap, the withheld counts and the
   audit rows are the fact path's. `IFactStore.Query` is unchanged.
3. **A row-form gate entry.** `IHeldFactRowDisclosureGate.CheckHeldRows(scope, principal, surface, factIds, held)`
   judges `factIds` from the `HeldFactRow`s the caller read (fact id, metric, classification), under the held form's
   vouching rule: in-process only, rows read from this scope's store in this request. Verdicts and audit rows are the
   id form's in every configuration, a taint configuration included (the taint stage reads the derivation graph by
   id, never the judged facts). `FactDisclosureGate` implements it.

## What to do

Nothing, for a composed deployment. A gate decorator that wants the saving implements the row form beside the held
form, forwarding to the platform gate:

```fsharp skip=fragment
interface IHeldFactRowDisclosureGate with
    member _.CheckHeldRows(scope, principal, surface, factIds, held) =
        (inner :> IHeldFactRowDisclosureGate).CheckHeldRows(scope, principal, surface, factIds, held)
```

The posture and the measurements: [`docs/platform/facts.md`](../platform/facts.md#a-chart-is-one-download-the-browses-series-read-answers-from-the-rows-phase-1126).

## Verifying

```powershell
dotnet ./src/ToolUp.Platform.Tests/bin/Debug/net10.0/ToolUp.Platform.Tests.dll --filter-test-list "Phase 1126"
```

The browse cases count a chart's fact downloads (zero) at 2,000 and 20,000 facts, warm and first after the upload,
and hold the row path's `FactSeries` equal to the fact path's over a mixed corpus, with and without a taint
configuration; the store cases hold every value shape and label through the codec, an assert-built snapshot equal to
a read-built one, and a version-2 snapshot rebuilt.

## Rolling back

Register an `IHeldFactDisclosureGate` that does not implement the row form (for example
`FactDisclosureGate.held` over a decorating gate): the chart then reads its points as facts, exactly as before. The
snapshot format does not roll back by itself — a pre-0.35.0 reader refuses a version-3 snapshot and rebuilds it.
