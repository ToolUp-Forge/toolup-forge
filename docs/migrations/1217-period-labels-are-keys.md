# Phase 1217 — a period or a subject label in a key position is a key, not a figure

**Class:** breaking on the 0.42.0 draft (already breaking) for code that builds a `GroundingPolicy` as a full
record or matches `NarrativeGrounding.Claim` exhaustively; additive for everyone else. A narrative that
passed the grounding gate before still passes, and nothing it refused in prose is admitted now.

## What changes

- **Key positions.** A table's column header, the first cell of a table row (when it holds text alone), a
  key/value grid's key, and each tick of a chart component's `axis` prop are key positions. A label there is
  a key, and passes without a reference, when it is a period (`NarrativeGrounding.isPeriodLabel`: a calendar
  day, an ISO week, a month, a quarter, a year from 1900 to 2199, or a period the policy declares), or when it
  names the subject of every Fact its row, column or entry cites. Anything else there is prose, as before, so
  a free number or a currency figure in a header is still refused. A key is not an admission: verdicts,
  audit rows and certificates record nothing for it.
- **`GroundingPolicy` gains `PeriodPatterns`** (a deployment's own periods, empty by default), and
  `NarrativeGrounding.withPeriodPatterns` declares them. **`NarrativeGrounding.Claim` gains `KeyClaim`.**
  `NarrativeGrounding.AxisProp` (`"axis"`) names the chart prop.
- **`PresentationNarrative` draws a trend** as a table with a row per day and a column per subject; a gap is
  named by subject and reason, never by day. A `trend` request is no longer refused by name. Requests that
  carry annotations still are.

## Adopting it

A full record construction of the policy adds the field; prefer copying the default:

```fsharp skip=fragment
let policy =
    NarrativeGrounding.defaultPolicy
    |> NarrativeGrounding.withPeriodPatterns [ @"FY\d{2} P(0[1-9]|1[0-3])" ]

FactNarrativeGroundingGate.createWithPolicy policy store disclosure registry kinds issuer
```

An exhaustive match over `NarrativeGrounding.Claim` adds a `KeyClaim(location, label, cited)` arm.

A deployment that checked its own tables before a run, to drop the rows whose period or product label the
gate refused, can delete that check: those rows now pass on their keys. To let the model request trends,
register the kind as for any other:

```fsharp skip=fragment
|> GroundedNarrativeDefinition.withRequestBuilder "trend" (fun context request ->
    PresentationNarrative.project (FactBrowseHandler.factBrowseApi context.Request).QuerySeries request.Json)
```

## Verification

`dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "Phase 1217 K"` runs the key cases: a
period table with every value cited passes and admits nothing; one uncited value is refused on that cell
only; a number disguised as a header, a row label or a grid key is refused; a period in prose is judged as
before; a chart axis of periods passes and a figure on it is refused at its tick; a declared fiscal period
passes only under the policy that declares it; a subject label carrying a digit keeps its row only when it
names the cited Fact's subject; a column header is checked against its column; and a trend draws and
publishes through the fact-tier gate.

## Rollback

Pin the previous SDK version. Nothing is persisted in a new shape.
