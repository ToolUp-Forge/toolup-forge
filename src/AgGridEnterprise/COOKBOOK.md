# AG Grid + AG Charts Enterprise cookbook (F# / Fable)

Authoring reference for the ToolUp **Enterprise** AG Grid and AG Charts Fable
bindings. Everything here needs an AG Grid Enterprise licence and the
`AgGridEnterprise` companion composed into the deployment. The Community
cookbook is
[`src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md`](../ToolUp.Platform.Client/Client/UI/COOKBOOK.md);
read its constraints first — they all still apply.

## Source of truth

- Bindings: [`AgGridEnterpriseTypes.fs`](AgGridEnterpriseTypes.fs) (grid) and
  [`AgChartEnterpriseTypes.fs`](AgChartEnterpriseTypes.fs) (charts), both under
  `module Enterprise` / `module AgChartEnterpriseTypes`. Module registration +
  the sole enterprise imports live in [`AgGridEnterprise.fs`](AgGridEnterprise.fs).
- Upstream API: `node_modules/ag-grid-enterprise/dist/types/src/` and
  `node_modules/ag-charts-types/dist/types/src/`. Pinned:
  **ag-grid-enterprise 35.3.0**, **ag-charts-enterprise 13.3.0**.

## Critical constraints

- **All `ag-grid-enterprise` / `ag-charts-enterprise` imports stay in
  `AgGridEnterprise.fs`**, at module-top-level. The typed series/param files
  (`AgGridEnterpriseTypes.fs`, `AgChartEnterpriseTypes.fs`) are erased and emit
  **no** JS imports. No file outside `src/AgGridEnterprise/` may import an
  enterprise package.
- **`MemoizedSparkline` stays non-`private`** (same Fable inline-export rule as
  `MemoizedChart`; it's referenced by an `inline` member on an `[<Erase>]`
  type). It reuses the community `AgCharts` component — a sparkline is a preset,
  not a separate import.
- **Enterprise features degrade, they don't crash.** With an empty/absent
  licence the app boots and Enterprise grids show AG Grid's own "License
  Required" overlay. Strip the companion + its registration and the same code
  compiles Community-only (the strip-imports acceptance test).
- **Enterprise series render through the same community `AgCharts` component.**
  An Enterprise series is an options object with a distinguishing `type`
  ("sankey", "candlestick", …); the enterprise chart module registered in
  `AgGridEnterprise.fs` at boot supplies the runtime behaviour.
- All Community constraints hold: direction-keyed `axes`, no `prop.key` on chart
  wrappers, `data` via the builders, fantomas → build → fable → smoke.

## The shortest possible chart

An Enterprise sparkline — a small preset chart, no axes.

```fsharp
open AgChartEnterpriseTypes

SparklineOptions.chart [
    SparklineOptions.line
    SparklineOptions.data [ 3.0; 5.0; 4.0; 8.0; 6.0 ]
    SparklineOptions.stroke "#59229D"
]
```

## Enterprise grid recipes

`open ToolUp.Platform.AgGrid` and `open AgGridEnterpriseTypes` (the `Enterprise`
sub-module carries the grid members).

### Set Filter

```fsharp
ColumnDef.create [
    ColumnDef.field _.Country
    ColumnDef.filter Enterprise.RowFilter.Set
    Enterprise.ColumnDef.setFilterParams
        { SetFilterParams.values = Some [| "UK"; "US"; "DE" |]
          valuesCallback = None; valueFormatter = None; cellRenderer = None
          suppressMiniFilter = Some false; suppressSelectAll = None
          suppressRemoveEntries = None; suppressSorting = None; excelMode = Some "windows"
          defaultToNothingSelected = None; treeList = None; treeListPathGetter = None
          keyCreator = None; comparator = None; caseSensitive = Some false }
]
```

### Multi Filter

```fsharp
ColumnDef.create [
    ColumnDef.field _.Name
    ColumnDef.filter Enterprise.RowFilter.Multi
    Enterprise.ColumnDef.multiFilterParams
        { filters = [|
            { filter = "agTextColumnFilter"; filterParams = None; display = Some "subMenu"; title = None }
            { filter = "agSetColumnFilter";  filterParams = None; display = Some "accordion"; title = None } |] }
]
```

### Master / Detail

```fsharp
AgGrid.grid [
    Enterprise.AgGrid.masterDetail true
    Enterprise.AgGrid.detailCellRendererParams
        { detailRowAutoHeight = Some true; detailRowHeight = None
          detailGridOptions = Some (createObj !![ AgGrid.columnDefs detailCols ])
          getDetailRowData = Some (fun p -> p?successCallback (p?data?children))
          keepDetailRows = Some true; keepDetailRowsCount = None }
    AgGrid.columnDefs masterCols
    AgGrid.rowData masterRows
]
```

### Status Bar

```fsharp
Enterprise.AgGrid.statusBar [
    Enterprise.statusPanelBuiltIn BuiltInStatusPanel.TotalRowCount (Some "left")
    Enterprise.statusPanelBuiltIn BuiltInStatusPanel.SelectedRowCount (Some "center")
    Enterprise.statusPanelBuiltIn BuiltInStatusPanel.Aggregation (Some "right")
]
```

### Sidebar Tool Panels

```fsharp
Enterprise.AgGrid.sideBar
    { toolPanels = [|
        Enterprise.toolPanelBuiltIn BuiltInToolPanel.Columns "columns" "Columns"
        Enterprise.toolPanelBuiltIn BuiltInToolPanel.Filters "filters" "Filters" |]
      defaultToolPanel = Some "columns"; position = Some "right"; hiddenByDefault = Some false }
// Or the shorthand for the default Columns + Filters layout:
Enterprise.AgGrid.sideBar true
```

### Excel Export

```fsharp
AgGrid.grid [
    Enterprise.AgGrid.excelStyles [
        { id = "negative"; alignment = None; borders = None
          font = Some (box {| color = "#c0392b" |}); interior = None
          numberFormat = Some (box {| format = "£#,##0.00" |}); protection = None; dataType = None } ]
    AgGrid.columnDefs cols
    AgGrid.rowData rows
]
// Trigger via the published api:
// api.exportDataAsExcel { ExcelExportParams... with fileName = Some "report.xlsx" }
```

### Charts integration (createRangeChart)

```fsharp
Enterprise.AgGrid.enableCharts true   // + Enterprise.AgGrid.cellSelection true
// From a captured IGridApi:
api.createRangeChart
    { chartType = "groupedColumn"
      cellRange = Some (box {| columns = [| "Region"; "Units" |] |})
      suppressChartRanges = None; chartContainer = None
      chartThemeName = Some "ag-polychroma"; chartThemeOverrides = None
      unlinkChart = None; switchCategorySeries = None }
|> ignore
```

### Server-Side Row Model

```fsharp
AgGrid.grid [
    Enterprise.AgGrid.rowModelType Enterprise.RowModelType.ServerSide
    Enterprise.AgGrid.serverSideDataSource
        { getRows = fun p -> loadBlock p.request (fun rows -> p.success { rowData = rows; rowCount = rows.Length })
          destroy = fun () -> () }
    AgGrid.columnDefs cols
]
// Later: api.applyServerSideTransaction { route = None; add = Some newRows; addIndex = None; remove = None; update = None } |> ignore
```

### Custom aggregate function

```fsharp
Enterprise.AgGrid.aggFuncs (Map [
    "weightedAvg", fun (p: IAggFuncParams<float>) ->
        if p.values.Length = 0 then 0.0 else Array.average p.values ])
// Reference it on a column:
ColumnDef.create [ ColumnDef.field _.Score; Enterprise.ColumnDef.aggFunc (Enterprise.AggregateFunction.Custom "weightedAvg") ]
```

## Enterprise chart recipes

`open AgChartEnterpriseTypes`. Each series is added to `AgChart.series [ ... ]`.

### Sankey

```fsharp
SankeyChartSeries.create [
    SankeyChartSeries.series
    SankeyChartSeries.fromKey "From"; SankeyChartSeries.toKey "To"; SankeyChartSeries.sizeKey "Flow"
    SankeyChartSeries.node (20, 10, "justify")
]
```

### Sunburst / Treemap (hierarchy)

```fsharp
let tree =
    HierarchyNode.branch "root" [|
        HierarchyNode.branch "A" [| HierarchyNode.leaf "A1" 4.0; HierarchyNode.leaf "A2" 6.0 |]
        HierarchyNode.leaf "B" 8.0 |]

SunburstChartSeries.create [
    SunburstChartSeries.series
    SunburstChartSeries.data [ tree ]
    SunburstChartSeries.labelKey "name"; SunburstChartSeries.sizeKey "size"
]
// Treemap: same hierarchy, TreemapChartSeries.series + tile(gap, padding).
```

### Candlestick / OHLC

```fsharp
CandlestickSeries.create [
    CandlestickSeries.series
    CandlestickSeries.xKey "Date"
    CandlestickSeries.openKey "Open"; CandlestickSeries.highKey "High"
    CandlestickSeries.lowKey "Low";   CandlestickSeries.closeKey "Close"
    CandlestickSeries.up ("#2ecc71", "#27ae60")
    CandlestickSeries.down ("#e74c3c", "#c0392b")
]
// OhlcSeries mirrors this (bar-style, no filled body).
```

### Heatmap / Waterfall / Box plot / Range

```fsharp
HeatmapChartSeries.create [ HeatmapChartSeries.series; HeatmapChartSeries.xKey "Hour"; HeatmapChartSeries.yKey "Day"; HeatmapChartSeries.colorKey "Load"; HeatmapChartSeries.colorRange [ "#eef"; "#59229D" ] ]
WaterfallChartSeries.create [ WaterfallChartSeries.series; WaterfallChartSeries.xKey "Step"; WaterfallChartSeries.yKey "Delta" ]
BoxPlotSeries.create [ BoxPlotSeries.series; BoxPlotSeries.xKey "Group"; BoxPlotSeries.minKey "Lo"; BoxPlotSeries.q1Key "Q1"; BoxPlotSeries.medianKey "Med"; BoxPlotSeries.q3Key "Q3"; BoxPlotSeries.maxKey "Hi" ]
RangeBarSeries.create [ RangeBarSeries.series; RangeBarSeries.xKey "Month"; RangeBarSeries.yLowKey "Lo"; RangeBarSeries.yHighKey "Hi" ]
```

### Sparkline in a grid cell

```fsharp
// Render a sparkline as a cell renderer.
ColumnDef.create [
    ColumnDef.field _.Trend
    ColumnDef.cellRenderer (fun p ->
        SparklineOptions.chart [
            SparklineOptions.line
            SparklineOptions.data (unbox<float array> p.value)
            SparklineOptions.stroke "#59229D"
            SparklineOptions.height 24
        ])
]
```

## Anti-patterns

```fsharp
// WRONG — importing an enterprise package outside AgGridEnterprise.fs.
let x : obj = import "AllEnterpriseModule" "ag-grid-enterprise"   // in a module file
// RIGHT — the import lives in AgGridEnterprise.fs; types here are erased.
```

```fsharp
// WRONG — private MemoizedSparkline → runtime "does not provide an export".
let private MemoizedSparkline props = ...
// RIGHT — non-private (matches MemoizedChart).
[<ReactComponent>]
let MemoizedSparkline props = ...
```

```fsharp
// WRONG — assuming heatmap/waterfall/range are Community (they were, historically).
// In AG Charts 13.3.0 they are Enterprise-only — bound here, not in AgChart.fs.
```

## See also

- Community cookbook:
  [`src/ToolUp.Platform.Client/Client/UI/COOKBOOK.md`](../ToolUp.Platform.Client/Client/UI/COOKBOOK.md).
- Companion setup: [`README.md`](README.md).
