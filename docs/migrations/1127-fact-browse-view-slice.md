# A fact-browse view reads once and refines locally — the view slice

**Ships in:** ToolUp.Facts.Shared, ToolUp.Facts.Server and ToolUp.Facts.Client 0.35.0. Rides the 0.35.0 draft
(untagged, publicly unpinned, breaking-class since Phase 1123). The class here is breaking too: `IFactBrowseApi` and
`FactTableListing` each gain a record field.

**Affected:** code that implements or fakes `IFactBrowseApi`, and code that constructs a `FactTableListing` in full.
A composed deployment changes nothing. Its drill-down now reads each table once and answers every page, sort, metric
and branch change locally.

## What changes

1. **A slice read.** `IFactBrowseApi.QuerySlice: FactSliceQuery -> Async<Result<FactSlice, string>>` returns one
   table's current heads as a single columnar table. The request names a period range, an optional subject prefix
   and a metric set (empty means all of the table's metrics). The answer carries the table in the
   `Fuaran.Core.Column` canonical codec (`FactSlice.Table`), with the run stamp, the cell count, the cap and the
   withheld counts beside it.
   - **The gate.** Every cell is judged at `FactBrowse` before encoding. A withheld cell keeps its subject, period,
     fact id and ranking position, and has no value or rendering.
   - **The cap.** `FactBrowseApi.MaxSliceCells` is 25,000. A larger slice is refused before the gate, and the
     refusal names the cap.
2. **Local reads.** `FactBrowseSlice.hold` decodes a slice and prepares it once. `FactBrowseSlice.rowPage` and
   `FactBrowseSlice.series` then answer a `FactRowQuery` or a `FactSeriesQuery` inside it as
   `Fuaran.Compute.DataFrame` pipelines. Within the slice they return the same rows, order, ranks, withheld counts and
   paging flags as `QueryRows` and `QuerySeries`. `coversRows` / `coversSeries` say whether a request falls inside a
   slice. One difference: the population summary's minimum, maximum and mean are withheld whenever any matched head
   is withheld. That is stricter than the server, never looser.
3. **A run stamp on the listing.** `FactTableListing.RunStamp: string option` is the current run's watermark token.
   A slice whose `RunStamp` differs from it is stale.
4. **The browse client** holds the open table's slice and reads it again when the stamp moves or the run notification
   arrives. When a slice is refused it falls back to server pages.
5. **Two packages join the graph from nuget.org:** `Fuaran.Core.Column` 0.40.2 and `Fuaran.Compute.DataFrame` 0.42.0,
   pulling `Fuaran.Core.Unit`, `.Wire` and `.Text` with them. All are Apache-2.0, FSharp.Core-only and Fable-clean,
   and all restore anonymously.

Nothing else moves. `QueryRows`, `QuerySeries`, `GetFact` and `GetRunMovers` keep their contracts.

## What to do

A fake or a decorator of `IFactBrowseApi` adds the field:

```fsharp skip=fragment
let api: IFactBrowseApi = {
    // … the seven existing fields …
    QuerySlice = fun _ -> async { return Error "slices are not offered here" }
}
```

An `Error` is a complete answer, because the browse client then falls back to page reads. A forwarding decorator
passes the call through instead.

Code that constructs a `FactTableListing` in full adds `RunStamp = None`, or the stamp when it knows one.

## Verification

`dotnet build` is the check for the new fields. The Platform pack's `Phase 1127` cases hold the slice to the server
reads. The Fable pack's cases run the same pipelines under Fable.

## Rollback

Revert to 0.34.x. The slice is derived from the store on every read and nothing is persisted, so there is no data to
migrate back.
