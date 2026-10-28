# Fact presentation builders — from a fact series to a Fuaran-UI node

**Ships in:** ToolUp.Facts.Presentation 0.36.0, a new package. Rides the 0.36.0 draft (untagged, publicly
unpinned, breaking-class since Phase 1128). The class here is additive: a new assembly, and nothing existing moves.

**Affected:** nobody until they opt in. A consumer that draws fact series with Fuaran-UI by hand can replace its
chart code with the library.

## What changes

1. **A new package, `ToolUp.Facts.Presentation`,** compiled by both compilers (sources under `fable/`):
   - `PresentationRequest`: kind (`Trend` | `Breakdown` | `Comparison` | `Table`), table id, metrics, subject prefix
     and level, period (`Ending(day, rangeDays)` or `Between(from, until)`), compare-to, title, annotations. Its JSON
     codec (`toJson` / `ofJson`) refuses a malformed request by name. `toQueries` gives the series reads it needs.
   - `Presentation.build`: a request plus one `SeriesAnswer` per metric (`Answered series` or `Refused(metric, why)`)
     gives a `PresentationBlock` (the validated node, its `BlockProvenance` and its notes) or a `PresentationDefect`.
   - The gap discipline: a withheld or absent point is never left in a chart's rows. It is named, with its reason,
     beside the block. A refused read is a callout.
2. **Two packages join the graph from nuget.org:** `Fuaran.UI` and `Fuaran.UI.Charts` 0.98.1 (Apache-2.0,
   Fable-clean), pinned by one property for the Fuaran-UI repository, `FuaranUIVersion`. They bring
   `Fuaran.UI.Renderer.Core` and the Fuaran.Core packages, resolved at the existing Core pin.

Nothing else moves. No existing package references the new one.

## What to do

Reference the package. A consumer's charts module then shrinks to its own reads and layout. The builders take the
request and the gated series, and the page keeps its own actions:

```fsharp skip=fragment
module Charts

open ToolUp.Facts
open ToolUp.Facts.Presentation

/// The page's charts, as requests: what to draw, never a number.
let trend metric title day : PresentationRequest = {
    Kind = PresentationKind.Trend
    TableId = "trade.site"
    Metrics = [ metric ]
    SubjectPrefix = []
    SubjectLevel = None
    Period = PresentationPeriod.Ending(day, 28)
    CompareTo = None
    Title = Some title
    Annotations = []
}

let breakdown tableId metric title day =
    { trend metric title day with Kind = PresentationKind.Breakdown; TableId = tableId }

let comparison tableId metric title day =
    { breakdown tableId metric title day with Kind = PresentationKind.Comparison }

/// One read per metric, through the fact browse, then the block.
let panel (api: IFactBrowseApi) (request: PresentationRequest) = async {
    let! answers =
        PresentationRequest.toQueries FactBrowseApi.MaxSeriesPoints request
        |> List.map (fun q -> async {
            match! api.QuerySeries q with
            | Ok series -> return SeriesAnswer.Answered series
            | Error why -> return SeriesAnswer.Refused(q.Metric, why) })
        |> Async.Sequential

    return Presentation.build request (List.ofArray answers)
}
```

The page renders `block.Node` and appends its own "open the table" button beside it. A `PresentationDefect` is a
builder defect to report, not a state to draw.

## Verification

The Platform pack's `Phase 1129` cases hold each kind to fixture facts, including that a withheld point is absent
from the rows and present in the notes, that a refused series is a callout, and that every built node passes the
Fuaran-UI pre-emit validator. `dotnet restore` resolves the two new packages from nuget.org with no credentials.

## Rollback

Drop the package reference. Nothing is persisted and no other package depends on it.
