# Phase 1385: a released fit diagnostic becomes a chart on the modeller's side

**Who this is for:** a modeller that calls the model-execution governed-diagnostics contract for a fit diagnostic
(Phase 1384) and wants to look at the answer rather than read its cells. **Additive surface**: nothing existing
changes shape; `ToolUp.InterPlatform` gains package references to `Fuaran.UI`, `Fuaran.UI.Charts` and
`Fuaran.Core.Column` at the existing `FuaranUIVersion` and `FuaranCoreVersion` pins.

## What changes

1. **`FitDiagnosticCharts.toChart id diagnostic declaration answer`** draws a released fit diagnostic as a validated
   Fuaran-UI `Chart` node: `ResidualHistogram` and `InfluenceSummary` as bars, `ResidualSummaryByBin`,
   `Calibration` and `ThresholdCurve` as lines, `QuantileComparison` as a scatter over its probabilities. The value
   axis takes the declaration's precision (counts are whole numbers). It returns a `FitDiagnosticChart`: the chart
   node (`None` when nothing is drawn), what the answer says was withheld (`FitDiagnosticWithheld`), and the notes
   to show beside the chart.
2. **`FitDiagnosticAnswer.ofReply template reply`** reads the reply to a fit-diagnostic call under the approved
   template: `Named` (a `ReleasedCohort`) when the template names withheld cells, `Unnamed` (a `CohortResult`) when
   it does not, and `Withheld` carrying the gate's own words when the gate withheld the whole result. Any other
   error stays an error.
3. **Released, absent and withheld cells are kept apart.** The chart's rows are exactly the released cells; a
   released zero is drawn as zero. A withheld cell is never drawn (the shipped chart arms read a missing value as
   zero) and is named in the notes; an answer that does not name withheld cells says so in the notes. An absent cell
   is not drawn, except that inside a drawn bin of a count diagnostic a series the answer neither releases nor
   withholds is drawn as the zero it counted. A bin missing a drawn series' value is left out and named.
4. **An answer that is not the diagnostic's is refused** as a `FitDiagnosticChartRefusal` (not a fit diagnostic,
   shape mismatch, unreadable cell, chart refused), never drawn as a guess.

## Do this

Nothing, unless you want the chart:

```fsharp skip=fragment
let declaration =
    FitDiagnosticDeclaration.parse template.Release.Declarations[FitDiagnostics.Calibration]
    |> Result.defaultWith failwith

match FitDiagnosticAnswer.ofReply template reply with
| Ok answer ->
    match FitDiagnosticCharts.toChart "calibration" FitDiagnostics.Calibration declaration answer with
    | Ok drawn -> show drawn.Chart drawn.Notes        // always show the notes beside the chart
    | Error refusal -> report (FitDiagnosticChartRefusal.describe refusal)
| Error peerError -> report (JsonRpc.errorMessage peerError)
```

Show `Notes` wherever you show `Chart`: they are where a withheld cell is named.

## What is not done here

- **Withheld cells inside the drawing.** Until the Fuaran-UI chart vocabulary carries a withheld state (fuaran#2809)
  and forge's `FuaranUIVersion` is raised past it, withheld cells stay out of the drawing and are named beside it.
- **Two-dimensional diagnostics and interval bands** (forge#1386).

## Verify

`dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj -- --filter-test-list "fit-diagnostic charts"`
runs the cases, including each shipped projection released by the shipped broker and drawn.

## Rollback

Revert the commit. Nothing persisted or on the wire changes.
