# `QuerySeries` reads a registered view

**Ships in:** ToolUp.Facts.Shared, ToolUp.Facts.Server, ToolUp.Facts.Presentation.Server 0.42.0. Breaking only for a
full-literal construction of `FactSeriesQuery`, `FactSeries` or `FactBrowseDeps`, each of which gains one field, and for
a caller of `PageRequestResolvers.create`, which gains a parameter. The wire is additive: a series request with no
`View` member reads its table as before.

**Affected:** a deployment that declares views and wants its charts to draw them; code that builds one of the three
records above in full (tests, mostly); a caller of `PageRequestResolvers.create`.

## What changes

1. **ToolUp.Facts.Shared:** `FactSeriesQuery.View: FactSeriesViewSource option` (a view id and its arguments as
   `FactViewArgument` values); when it is set, `Metric` names the view's result column to draw. `FactSeries.View:
   FactSeriesViewIdentity option` (view id, version, pipeline hash, model hash). `FactBrowseSlice.coversSeries` is false
   for a view-sourced request and `FactBrowseSlice.series` refuses one. `FactBrowseSlice.parseDecimal` is public, and `FactBrowseApi.periodRangeRefusal` / `seriesCapRefusal` hold the series
   reads' two refusal sentences.
2. **ToolUp.Facts.Server:** `FactBrowseDeps.Views: IFactViewSeriesReader option`; `QuerySeries` resolves a view-sourced
   request through it and refuses one by name when it is `None`. `ViewRegistry` gains `argument`, `seriesArguments`,
   `seriesColumns`, `seriesOf`, `series`, `seriesReader` and `withSeries`.
3. **ToolUp.Facts.Presentation.Server:** `PipelineAnsweringTool.rerunUnder` is public; `PageRequestResolver.createUnder`
   takes the pipeline profile (`create` is `createUnder None`); `PageRequestResolvers.create` takes the profile, and
   `PageRequestResolvers.register` reads the registered one from the container. A stored pipeline that joins along a
   declared relation now re-resolves on a page instead of being refused.

## Adopting it

```fsharp skip=fragment
let models, views = ViewRegistry.registerOrRefuse app

app
|> FactsCompose.withDatasetModels resolver models
|> ViewRegistry.withSeries views
```

A chart then asks:

```fsharp skip=fragment
let query: FactSeriesQuery = {
    TableId = "daily-trade"                 // the view's own table
    Metric = "net"                          // a result column of the view
    SubjectPrefix = []
    SubjectLevel = None                     // a view decides its own rows
    PeriodFrom = from
    PeriodTo = until
    MaxPoints = FactBrowseApi.MaxSeriesPoints
    View = Some { ViewId = "trade_net"; Arguments = Map.ofList [ "min", FactViewArgument.Integer 0 ] }
}
```

A view drawn as a series answers `subject`, `subject_path`, `period_from` and `period_to`. A full-literal
`FactSeriesQuery` or `FactSeries` adds `View = None`; a full-literal `FactBrowseDeps` adds `Views = None`.

## Verifying

`dotnet run --project src/ToolUp.Platform.Tests -- --filter-test-list "QuerySeries reads a registered view"`: the
series draws the cells and withheld marks of the view's own evaluation, refusals by name, the cap, a table read
unchanged byte for byte and decoding with no view member, and a stored joined pipeline re-resolving under the profile.

## Rolling back

Drop `ViewRegistry.withSeries` and every `View = Some …` request; table reads are unaffected.
