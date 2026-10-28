# ToolUp.Facts.Presentation

From a gated fact series to a [Fuaran-UI](https://www.nuget.org/packages/Fuaran.UI) node. A
*presentation request* says what to draw — a trend, a breakdown, a comparison or a table of one
fact table's metric(s) over a period — and a pure builder draws the series the fact browse's series
read answered as a validated Fuaran-UI node, with the `BlockProvenance` that produced it.

The request is the whole of what an author (a person or a model) writes for a block. It carries no
number: every number on the page comes from the series read, under the disclosure gate the facts are
published under.

Compiled by both compilers (the sources ship under `fable/`), so a browser rebuilds exactly the node
a server built.

## What it gives you

- **`PresentationRequest`** — kind, table id, metrics, subject prefix and level, period
  (`{ day; rangeDays }` or `{ from; to }`), compare-to, title and annotations — with a JSON codec
  (`PresentationRequest.toJson` / `ofJson`) that refuses a malformed request **by name**: the field
  it is missing, the kind it does not know. `PresentationRequest.toQueries` gives the series reads
  the request asks for.
- **`Presentation.build`** — from a request and one `SeriesAnswer` per metric (the gated
  `FactSeries`, or the read's refusal) to a `PresentationBlock`: the node, its provenance and its
  notes.
- **The gap discipline.** Fuaran-UI's chart lowering reads a missing cell as 0, so a withheld or
  absent point is never left in a chart's rows: a trend drops the day and marks it on the axis, a
  breakdown and a comparison drop the subject, a table drops the cell. Each is named, with its
  reason, beside the block. A refused read is a callout naming the refusal.
- **Validated before it leaves.** Every chart is drawn from a canonical columnar table, so the
  Fuaran-UI pre-emit validator grounds every field it names; a node the validator refuses is a
  builder defect (`PresentationDefect.NodeRefused`), never emitted.
- **Fact cells.** A table cell is a Fact node carrying its fact id as `data-fact`.

```fsharp
open ToolUp.Facts.Presentation

match PresentationRequest.ofJson requestJson with
| Error e -> Error(PresentationRequestError.describe e)
| Ok request ->
    // run PresentationRequest.toQueries through IFactBrowseApi.QuerySeries, then:
    Presentation.build request [ SeriesAnswer.Answered series ]
```

## Dependencies

`ToolUp.Facts.Shared`, `ToolUp.AI.Wire`, and the published Apache-2.0 `Fuaran.UI`,
`Fuaran.UI.Charts` and `Fuaran.Core.Column` packages from nuget.org.

Apache-2.0.
