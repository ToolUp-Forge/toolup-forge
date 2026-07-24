# AG Grid + AG Charts Community cookbook (F# / Fable)

Authoring reference for the ToolUp Community AG Grid and AG Charts Fable
bindings, written top-down for humans and for AI agents (Claude Code, Cursor)
reading the repo. The Enterprise mirror is
[`src/AgGridEnterprise/COOKBOOK.md`](../../../AgGridEnterprise/COOKBOOK.md).

## Source of truth

- Bindings: [`AgGrid.fs`](AgGrid.fs) (`module ToolUp.Platform.AgGrid`) and
  [`AgChart.fs`](AgChart.fs) (`module ToolUp.Platform.AgChart`).
- Upstream API: the TypeScript `.d.ts` under
  `node_modules/ag-grid-community/dist/types/src/` and
  `node_modules/ag-charts-types/dist/types/src/`. Pinned versions:
  **ag-grid-community 35.3.0**, **ag-charts-community 13.3.0**.

## Critical constraints

Break one of these and charts stop animating, the build breaks, or a consumer
gets a runtime `SyntaxError`. Read before writing chart/grid code.

- **`AgChart.axes` is direction-keyed, not an array.** Pass a `seq<obj>` of
  axis objects; the binding keys each by `"x"` (category / time) or `"y"`
  (number), or by explicit `Axis.position`. Never hand-build an `axes` array.
- **No `prop.key` on the chart wrapper.** A changing key forces a React
  remount, destroying the chart instance and killing transition animations.
  Let `AgChart.chart` manage identity via `MemoizedChart`.
- **`MemoizedChart` (and any module value used by an `inline` member on an
  `[<Erase>]` type) stays non-`private`.** Fable inlines the call site and
  imports the value directly; `private` yields a runtime "does not provide an
  export" `SyntaxError`. The same rule governs `ChartPalette` and (Enterprise)
  `MemoizedSparkline`.
- **`data` always goes through `AgChart.data` / `Series.data`.** These call
  `Seq.toArray`; a raw F# list on the wire is not what AG Charts expects.
- **`ChartPalette` is the deployment brand surface.** Set its mutables once at
  boot (before the first chart renders) for a workspace-wide palette;
  per-chart overrides use `AgChart.chartTheme` / `ChartThemeBuilder`.
- **No Enterprise imports here.** Community code never imports from
  `ag-grid-enterprise` / `ag-charts-enterprise`; module CSS imports use
  `ag-grid-community/styles/`. Enterprise series (heatmap, waterfall, box-plot,
  range-bar/area, sankey, sunburst, treemap, candlestick, ohlc, sparkline) live
  in the AgGridEnterprise companion — they are Enterprise-only in 13.3.0.
- **`options` wraps the series list.** `AgChart.chart [ AgChart.options [ ... ] ]`
  — the palette/theme merge happens inside `AgChart.options`.
- **Run fantomas → build → fable → smoke** on every changed `.fs`.

## The shortest possible chart

Copy-paste runnable. A single line series over category x / number y.

```fsharp
open ToolUp.Platform.AgChart

type Point = { Month: string; Sales: float }

let data = [
    { Month = "Jan"; Sales = 42.0 }
    { Month = "Feb"; Sales = 51.0 }
    { Month = "Mar"; Sales = 47.0 }
]

let view =
    AgChart.chart [
        AgChart.options [
            AgChart.data data
            AgChart.series [
                Series.create [
                    Series.seriesKind Line
                    Series.xKey "Month"
                    Series.yKey "Sales"
                    Series.yName "Sales"
                ]
            ]
            AgChart.axes [
                Axis.create [ Axis.axisKind AxisKind.Category ]
                Axis.create [ Axis.axisKind AxisKind.Number ]
            ]
        ]
    ]
```

## Chart recipes

### Line over time

```fsharp
// Time axis auto-keys to "x"; number axis to "y" (direction-keyed rule).
AgChart.chart [
    AgChart.options [
        AgChart.data revenueByDay
        AgChart.series [
            Series.create [ Series.seriesKind Line; Series.xKey "Date"; Series.yKey "Revenue" ]
        ]
        AgChart.axes [
            Axis.create [ Axis.axisKind AxisKind.Time ]
            Axis.create [ Axis.axisKind AxisKind.Number; Axis.title "Revenue (£)" ]
        ]
    ]
]
```

### Bar, categorical

```fsharp
Series.create [ Series.seriesKind Bar; Series.xKey "Region"; Series.yKey "Units" ]
```

### Stacked / 100%-stacked bar

```fsharp
// Multiple series sharing a stack: set `stacked true` on each.
// For 100%-stacked, add `Series.normalizedTo 100` to each series.
let stackedSeries yKey name =
    Series.create [
        Series.seriesKind Bar
        Series.xKey "Quarter"
        Series.yKey yKey
        Series.yName name
        Series.stacked true
        // Series.normalizedTo 100   // uncomment for 100%-stacked
    ]

AgChart.series [ stackedSeries "Online" "Online"; stackedSeries "Retail" "Retail" ]
```

### Multi-series area

```fsharp
AgChart.series [
    Series.create [ Series.seriesKind Area; Series.xKey "Month"; Series.yKey "A"; Series.fillOpacity 0.4 ]
    Series.create [ Series.seriesKind Area; Series.xKey "Month"; Series.yKey "B"; Series.fillOpacity 0.4 ]
]
```

### Scatter with a size key (bubble)

```fsharp
// Bubble is its own builder — the size dimension diverges from plain scatter.
BubbleSeries.create [
    BubbleSeries.seriesType
    BubbleSeries.xKey "Spend"
    BubbleSeries.yKey "Revenue"
    BubbleSeries.sizeKey "Deals"
    BubbleSeries.sizeName "Deals"
    BubbleSeries.sizeDomain (4.0, 40.0)   // px marker size range
]
```

### Pie / donut

```fsharp
// Pie: angleKey drives the slice angle; calloutLabelKey labels each slice.
PieSeries.create [
    PieSeries.pie
    PieSeries.angleKey "Amount"
    PieSeries.calloutLabelKey "Category"
    PieSeries.sectorLabelKey "Amount"
]

// Donut: same, plus an inner radius ratio.
PieSeries.create [
    PieSeries.donut
    PieSeries.angleKey "Amount"
    PieSeries.legendItemKey "Category"
    PieSeries.innerRadiusRatio 0.6
]
```

### Line + bar combo

```fsharp
// Two series of different kinds sharing one x; give the line its own y axis.
AgChart.series [
    Series.create [ Series.seriesKind Bar; Series.xKey "Month"; Series.yKey "Units" ]
    Series.create [ Series.seriesKind Line; Series.xKey "Month"; Series.yKey "Margin" ]
]
```

### Dual-axis (left + right)

```fsharp
// Explicit position keys the axis by that side (overrides the direction rule).
AgChart.axes [
    Axis.create [ Axis.axisKind AxisKind.Category; Axis.position Bottom ]
    Axis.create [ Axis.axisKind AxisKind.Number; Axis.position Left; Axis.title "Units" ]
    Axis.create [ Axis.axisKind AxisKind.Number; Axis.position Right; Axis.title "Margin %" ]
]
```

### Inside a module

```fsharp
// Charts render inside a PageContent layout. Two common shells:
//   PageContent.SplitPanel  — chart beside a control/detail pane.
//   PageContent.Dashboard   — a grid of chart cards.
// The chart element is just the `AgChart.chart [...]` ReactElement; drop it in
// wherever the layout expects content. Keep the whole `options` list in the
// module's `view`, derived from Model — MVU re-renders re-run `options`, and
// MemoizedChart absorbs no-op re-renders so animations survive.
```

### Custom tooltip renderer

```fsharp
Series.create [
    Series.seriesKind Line
    Series.xKey "Month"
    Series.yKey "Sales"
    // Return a typed content record; AG Charts renders it into the tooltip DOM.
    Series.tooltipRenderer (fun p ->
        {
            title = p.title
            content = sprintf "£%.0f in %O" (unbox<float> p.yValue) p.xValue
            backgroundColor = "#59229D"
            color = "#ffffff"
        })
]
```

### Custom value formatter (currency / %)

```fsharp
// Axis label formatter — receives value/index/fractionDigits/tickInterval.
Axis.create [
    Axis.axisKind AxisKind.Number
    Axis.label { AxisLabel.empty with formatter = Some(fun p -> sprintf "£%O" p.value) }
]
```

### Crosshair + crosslines

```fsharp
Axis.create [
    Axis.axisKind AxisKind.Number
    Axis.crosshair true
    Axis.crosslines [ 100.0; 200.0 ]                 // horizontal reference lines
    // Axis.crosslines [ { low = 90.0; high = 110.0; colour = "#eee"; fillOpacity = 0.3 } ]  // band
]
```

### Error bars

```fsharp
Series.create [
    Series.seriesKind Scatter
    Series.xKey "X"; Series.yKey "Y"
    Series.errorBar (ErrorBar.create [ ErrorBar.yLowerKey "Lo"; ErrorBar.yUpperKey "Hi" ])
]
```

## Grid recipes

### Basic table

```fsharp
open ToolUp.Platform.AgGrid

type Row = { Name: string; Qty: int; Price: float }

AgGrid.grid [
    AgGrid.rowData rows
    AgGrid.columnDefs [
        ColumnDef.create [ ColumnDef.field _.Name;  ColumnDef.headerName "Name" ]
        ColumnDef.create [ ColumnDef.field _.Qty;   ColumnDef.headerName "Qty" ]
        ColumnDef.create [ ColumnDef.field _.Price; ColumnDef.headerName "Price" ]
    ]
]
```

### Sortable + filterable

```fsharp
// defaultColDef applies to every column.
AgGrid.defaultColDef [
    ColumnDef.sortable true
    ColumnDef.filter true
    ColumnDef.resizable true
]
// Per column, a typed filter + its params:
ColumnDef.create [
    ColumnDef.field _.Price
    ColumnDef.filter RowFilter.Number
    ColumnDef.cellFilterParams { NumberFilterParams.empty with buttons = Some [| "apply"; "reset" |] }
]
```

### Editable cells

```fsharp
ColumnDef.create [
    ColumnDef.field _.Qty
    ColumnDef.editable true
    ColumnDef.cellEditor AgCellEditor.NumberCellEditor
]
// Handle the change grid-wide:
AgGrid.onCellValueChanged (fun (row: Row) -> dispatch (RowEdited row))
```

### Custom cell renderer

```fsharp
ColumnDef.create [
    ColumnDef.field _.Status
    ColumnDef.cellRenderer (fun p ->
        Html.span [ prop.className "badge"; prop.text (string p.value) ])
]
```

### Pinned bottom totals

```fsharp
AgGrid.grid [
    AgGrid.rowData rows
    AgGrid.pinnedBottomRowData [| { Name = "Total"; Qty = totalQty; Price = totalPrice } |]
    AgGrid.columnDefs cols
]
```

### Publish the grid api (for a client tool / UIControl)

```fsharp
// From inside onGridReady, publish the IGridApi under a stable key so a
// companion (e.g. a client-resident AI tool) can drive the grid later.
AgGrid.onGridReady (fun ev -> ev.Publish "SalesAnalysis.skuList")
// Elsewhere:
// GridApiRegistry.tryGet "SalesAnalysis.skuList" |> Option.iter (fun api -> api.selectAll ())
```

### Grid events

```fsharp
AgGrid.grid [
    AgGrid.columnDefs cols
    AgGrid.rowData rows
    AgGrid.onCellClicked (fun (e: ICellEvent<Row, obj>) -> dispatch (CellClicked e.rowIndex))
    AgGrid.onSortChanged (fun _ -> dispatch SortChanged)
    AgGrid.onFirstDataRendered (fun _ -> dispatch DataReady)
]
```

### Theming (v31+ Theming API)

```fsharp
// Build a theme once, pass it to AgGrid.theme. Supersedes ThemeClass strings.
let gridTheme =
    Theme.themeQuartz
    |> Theme.withParams { ThemeParams.empty with accentColor = Some "#59229D"; spacing = Some 8 }
    |> Theme.withPart Theme.colorSchemeDark

AgGrid.grid [ AgGrid.theme gridTheme; AgGrid.columnDefs cols; AgGrid.rowData rows ]
```

### CSV export

```fsharp
// The api takes obj; the typed record boxes cleanly.
GridApiRegistry.tryGet "myGrid"
|> Option.iter (fun api ->
    api.exportDataAsCsv (box { CsvExportParams.empty with fileName = Some "rows.csv"; onlySelected = Some true }))
```

## Anti-patterns

Smallest wrong snippet next to its fix.

```fsharp
// WRONG — prop.key on the chart wrapper remounts + kills animations.
Html.div [ prop.key model.Tab; AgChart.chart [ ... ] ]
// RIGHT — no key; MemoizedChart manages identity.
AgChart.chart [ ... ]
```

```fsharp
// WRONG — hand-built axes array; AG Charts v13 can't resolve primary axes.
"axes" ==> [| categoryAxis; numberAxis |]
// RIGHT — AgChart.axes direction-keys them.
AgChart.axes [ categoryAxis; numberAxis ]
```

```fsharp
// WRONG — private breaks the inline-export at the consumer call site.
let private MemoizedChart props = ...
// RIGHT — non-private (Fable imports it directly from the erased inline member).
[<ReactComponent>]
let MemoizedChart props = ...
```

```fsharp
// WRONG — raw list on the wire; AG Charts expects an array.
"data" ==> myList
// RIGHT — AgChart.data / Series.data call Seq.toArray for you.
AgChart.data myList
```

## See also

- Enterprise cookbook: [`src/AgGridEnterprise/COOKBOOK.md`](../../../AgGridEnterprise/COOKBOOK.md).
- Binding reference: [`src/ToolUp.Platform/TECHNICAL_GUIDE.md`](../../TECHNICAL_GUIDE.md)
  "AG Grid / AG Charts binding reference".
