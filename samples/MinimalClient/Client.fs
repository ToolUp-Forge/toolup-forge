module MinimalClient.Client

open Fable.Core.JsInterop
open ToolUp.Elmish
open ToolUp.Elmish.React
open Feliz
open ToolUp.Platform
open ToolUp.Platform.AgGrid

importSideEffects "./index.css"

// ─── Phase 12d worked example — AG Grid value-provenance overlay ──
// A small results grid whose Revenue column opts into the provenance
// overlay via `ColumnDef.provenance`, pointing each cell at a lineage
// record. On hover the overlay shows the source; the click-through button
// emits a provenance-click that navigates to the linked lineage record
// (here: a console log stand-in for the sample). The grid opts in with
// `AgGrid.showProvenanceOverlay true`. Compiling this sample through Fable
// exercises the whole Community-tier provenance surface.

type ResultRow = { Region: string; Revenue: float }

let private demoRows = [
    { Region = "North"; Revenue = 12_400.0 }
    { Region = "South"; Revenue = 9_875.0 }
]

let private revenueProvenance (row: ResultRow) (colKey: string) : CellProvenance option =
    if colKey = "Revenue" then
        Some {
            SourceLabel = sprintf "%s sales rollup" row.Region
            SourceLocation = ProvenanceLocation.DataObject(sprintf "sales-%s" row.Region, 3)
            Detail = Some "summed from monthly ingests"
            LinkedEntity = Some("LineageRecord", sprintf "lineage-%s" row.Region)
        }
    else
        None

// Subscribe once: a real app would route this to its lineage page; the
// sample logs it, standing in for the click-through navigation. The
// returned dispose thunk is dropped — the sample's subscription lives for
// the tab.
subscribeProvenanceClick (fun prov ->
    match prov.LinkedEntity with
    | Some(entityType, entityId) -> Fable.Core.JS.console.log ("navigate to", entityType, entityId)
    | None -> ())
|> ignore

let provenanceDemo () : ReactElement =
    let columns = [
        ColumnDef.create<string> [ ColumnDef.field _.Region; ColumnDef.headerName "Region" ]
        ColumnDef.create<float> [
            ColumnDef.field _.Revenue
            ColumnDef.headerName "Revenue"
            yield! ColumnDef.provenance revenueProvenance
        ]
    ]

    Html.div [
        prop.className "ag-theme-alpine"
        prop.style [ style.height 160; style.marginTop 16 ]
        prop.children [
            AgGrid.grid [
                AgGrid.rowData demoRows
                AgGrid.columnDefs columns
                AgGrid.showProvenanceOverlay true
            ]
        ]
    ]

// ─── Phase 12e gallery — exercise the new Community bindings ──────
// Every binding referenced here is a `static member inline` on an `[<Erase>]`
// type (or a module value used by one). Compiling this sample through Fable
// therefore inlines each at a real call site — which is exactly what catches
// the inline-export / type-erasure class of runtime bug (the MemoizedChart
// "does not provide an export" problem). The Enterprise call-site gallery
// (MemoizedSparkline, Set Filter, Master/Detail, …) is the consumer-repo half
// — it needs a project that ProjectReferences AgGridEnterprise — and is
// deferred with the rest of the UiGallery consumer work.

open ToolUp.Platform.AgChart

type private GalleryRow = {
    Month: string
    Sales: float
    Margin: float
    Deals: float
}

let private galleryRows = [
    {
        Month = "Jan"
        Sales = 42.0
        Margin = 12.0
        Deals = 8.0
    }
    {
        Month = "Feb"
        Sales = 51.0
        Margin = 15.0
        Deals = 12.0
    }
    {
        Month = "Mar"
        Sales = 47.0
        Margin = 14.0
        Deals = 10.0
    }
]

let private galleryChart () =
    AgChart.chart [
        AgChart.options [
            AgChart.data galleryRows
            AgChart.tooltip true
            AgChart.padding 8
            AgChart.legend (LegendOptions.empty |> fun o -> { o with position = Some Bottom })
            AgChart.series [
                Series.create [
                    Series.seriesKind Bar
                    Series.xKey "Month"
                    Series.yKey "Sales"
                    Series.stacked true
                    Series.tooltipRenderer (fun p -> {
                        title = p.title
                        content = sprintf "%O" p.yValue
                        backgroundColor = "#59229D"
                        color = "#fff"
                    })
                ]
                Series.create [ Series.seriesKind Line; Series.xKey "Month"; Series.yKey "Margin" ]
            ]
            AgChart.axes [
                Axis.create [ Axis.axisKind AxisKind.Category ]
                Axis.create [
                    Axis.axisKind AxisKind.Number
                    Axis.gridLine true
                    Axis.tick true
                    Axis.label {
                        AxisLabel.empty with
                            fractionDigits = Some 0
                    }
                ]
            ]
        ]
    ]

let private galleryPie () =
    AgChart.chart [
        AgChart.options [
            AgChart.data galleryRows
            AgChart.series [
                PieSeries.create [
                    PieSeries.donut
                    PieSeries.angleKey "Sales"
                    PieSeries.calloutLabelKey "Month"
                    PieSeries.innerRadiusRatio 0.6
                ]
            ]
        ]
    ]

let private galleryBubble () =
    AgChart.chart [
        AgChart.options [
            AgChart.data galleryRows
            AgChart.series [
                BubbleSeries.create [
                    BubbleSeries.seriesType
                    BubbleSeries.xKey "Sales"
                    BubbleSeries.yKey "Margin"
                    BubbleSeries.sizeKey "Deals"
                    BubbleSeries.sizeDomain (4.0, 30.0)
                ]
            ]
            AgChart.axes [
                Axis.create [ Axis.axisKind AxisKind.Number; Axis.position Bottom ]
                Axis.create [ Axis.axisKind AxisKind.Number; Axis.position Left ]
            ]
        ]
    ]

let private galleryTheme =
    Theme.themeQuartz
    |> Theme.withParams {
        ThemeParams.empty with
            accentColor = Some "#59229D"
    }
    |> Theme.withPart Theme.colorSchemeLight

let private galleryGrid () =
    AgGrid.grid [
        AgGrid.theme galleryTheme
        AgGrid.rowData galleryRows
        AgGrid.suppressMovableColumns true
        AgGrid.localeText {
            LocaleText.empty with
                noRowsToShow = Some "Nothing here"
        }
        AgGrid.onCellClicked (fun (e: ICellEvent<GalleryRow, obj>) -> Browser.Dom.console.log ("cell", e.rowIndex))
        AgGrid.onSortChanged (fun _ -> Browser.Dom.console.log "sort")
        AgGrid.columnDefs [
            ColumnDef.create [ ColumnDef.field _.Month; ColumnDef.headerName "Month" ]
            ColumnDef.create [
                ColumnDef.field _.Sales
                ColumnDef.filter RowFilter.Number
                ColumnDef.cellFilterParams {
                    NumberFilterParams.empty with
                        buttons = Some [| "apply"; "reset" |]
                }
                ColumnDef.lockPinned true
            ]
        ]
    ]

let private phase12eGallery () =
    Html.div [
        Html.h2 [ prop.text "Phase 12e — binding gallery" ]
        galleryChart ()
        galleryPie ()
        galleryBubble ()
        galleryGrid ()
    ]

type Model = { Count: int }

type Msg =
    | Increment
    | Decrement

let init () : Model * Cmd<Msg> = { Count = 0 }, Cmd.none

let update (msg: Msg) (model: Model) : Model * Cmd<Msg> =
    match msg with
    | Increment -> { model with Count = model.Count + 1 }, Cmd.none
    | Decrement -> { model with Count = model.Count - 1 }, Cmd.none

let view (model: Model) (dispatch: Msg -> unit) : ReactElement =
    Html.div [
        prop.children [
            Html.h1 [ prop.text "MinimalClient — Fable smoke test" ]
            Html.p [ prop.text (sprintf "Count: %d" model.Count) ]
            Html.button [ prop.text "Increment"; prop.onClick (fun _ -> dispatch Increment) ]
            Html.button [ prop.text "Decrement"; prop.onClick (fun _ -> dispatch Decrement) ]
            provenanceDemo ()
            phase12eGallery ()
        ]
    ]

Program.mkProgram init update view
|> Program.withReactSynchronous "elmish-app"
|> Program.run