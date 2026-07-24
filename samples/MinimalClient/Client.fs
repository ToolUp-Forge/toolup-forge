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
        ]
    ]

Program.mkProgram init update view
|> Program.withReactSynchronous "elmish-app"
|> Program.run