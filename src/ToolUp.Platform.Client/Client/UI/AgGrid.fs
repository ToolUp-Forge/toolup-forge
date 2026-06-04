// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.AgGrid

open System

open Fable.Core
open Fable.Core.JsInterop
open Feliz

// Suppress unused value warnings - they are often necessary for Fable bindings.
#nowarn "1182"

// ─── Module configuration ────────────────────────────────────────
// AG Grid v35+ requires explicit module registration. Two approaches:
//
// Variant B (recommended): Use AgGridProvider to supply modules via React Context.
//   SDK.Client wraps the app in the provider; grids inherit modules automatically.
//
// Variant A (classic): Call ModuleRegistry.registerModules globally before rendering.
//   Still fully supported as a fallback.

let private allCommunityModule: obj =
    import "AllCommunityModule" "ag-grid-community"

/// Configuration for which AG Grid modules to load and optional license key.
/// Used by the AgGridProvider (Variant B) to supply modules via React Context.
type AgGridModuleConfig = {
    /// AG Grid module objects (e.g. AllCommunityModule, AllEnterpriseModule).
    Modules: obj array
    /// Enterprise license key. None for Community-only.
    LicenseKey: string option
}

module AgGridModuleConfig =
    /// Community-only configuration — zero-config default. No license key required.
    let community: AgGridModuleConfig = {
        Modules = [| allCommunityModule |]
        LicenseKey = None
    }

// ─── AgGridProvider (Variant B — recommended) ────────────────────

let private agGridProvider: obj = import "AgGridProvider" "ag-grid-react"

/// Wrap a React subtree in AgGridProvider, supplying AG Grid modules and
/// optional license key to all <AgGridReact> instances within the subtree.
/// This is the recommended approach for AG Grid v35+.
let provider (config: AgGridModuleConfig) (children: ReactElement list) : ReactElement =
    let props =
        createObj [
            "modules" ==> config.Modules
            match config.LicenseKey with
            | Some key -> "licenseKey" ==> key
            | None -> ()
            "children" ==> React.Fragment children
        ]

    ReactLegacy.createElement (unbox<ReactElement> agGridProvider, props)

// ─── Variant A fallback (global registration) ────────────────────
// Kept for backward compatibility. If AgGridEnterprise.register is called
// before Client.run, it sets the flag to prevent Community fallback.

let private gridModuleRegistry: obj = import "ModuleRegistry" "ag-grid-community"

let mutable private gridModulesRegistered = false

/// Mark that grid modules have been registered externally (e.g. by AgGridEnterprise).
let setGridModulesRegistered () = gridModulesRegistered <- true

let ensureGridModulesRegistered () =
    if not gridModulesRegistered then
        gridModuleRegistry?registerModules ([| allCommunityModule |])
        gridModulesRegistered <- true

let agGrid: obj = import "AgGridReact" "ag-grid-react"

/// React-memoised wrapper around `AgGridReact`. The Elmish runtime feeds the
/// grid a fresh props object on every shell re-render (state churn, prefetch
/// arrivals, parent re-render); without memoisation `AgGridReact` would
/// internally diff the new props against the prior render, often hitting the
/// destroy-and-recreate path on prop-shape changes that aren't structurally
/// meaningful (a closed-over callback reference flipping reference while
/// pointing at the same logic, a fresh `rowData` array carrying byte-identical
/// rows, a re-created `columnDefs` literal). The same problem hit `AgChart`
/// and was resolved by the `MemoizedChart` pattern; this is the AG Grid twin.
///
/// Discriminates props by `JS.JSON.stringify`. That gives us reference
/// stability when the data side of props is unchanged, at the cost of a
/// known limitation: function-typed props (cellRenderer / valueFormatter /
/// onCellClicked closures) are omitted by JSON serialisation, so a render
/// whose ONLY change is a different callback closure will be deduplicated
/// and the grid will keep the prior callbacks. In practice the data side of
/// props changes alongside callback closures (an MVU state transition mints
/// new rowData / configs at the same time it mints new closures), so this
/// rarely surfaces; if a consumer hits a stale-callback bug, the escape
/// hatch is to thread the changing state into a non-function prop (e.g.
/// inject a token into `context` or `rowData`).
///
/// Must NOT be `private` — `AgGrid.grid` is a `static member inline` on an
/// `[<Erase>]` type, so Fable inlines the call site and imports
/// `MemoizedGrid` directly from consumer modules. The same rule that gates
/// `MemoizedChart` (see forge `CLAUDE.md` "Build verification") applies.
[<ReactComponent>]
let MemoizedGrid (reactProps: obj) =
    let prevJsonRef = React.useRef ""
    let stableRef = React.useRef reactProps
    let json = JS.JSON.stringify reactProps

    if json <> prevJsonRef.current then
        prevJsonRef.current <- json
        stableRef.current <- reactProps

    ReactLegacy.createElement (unbox<ReactElement> agGrid, stableRef.current)

/// See https://www.ag-grid.com/react-data-grid/row-object/.
[<Erase>]
type IRowNode<'row> = {
    id: string
    data: 'row
    updateData: 'row -> unit
    setData: 'row -> unit
    setSelected: bool -> unit
    rowIndex: int
    rowTop: int
    displayed: bool
    isHovered: bool
    isFullWidthCell: bool
    isSelected: bool
    level: int
    rowPinned: string option
}

[<Erase>]
type ICellRange = {
    id: string
    startRow: obj
    endRow: obj
} with

    member this.startRowIndex: int = this.startRow?rowIndex
    member this.endRowIndex: int = this.endRow?rowIndex


/// See https://www.ag-grid.com/react-data-grid/column-object/.
[<Erase>]
type IColumn = { getColId: unit -> string }

/// See https://www.ag-grid.com/react-data-grid/grid-interface/#grid-api.
[<Erase>]
type IGridApi<'row> =
    abstract refreshCells: unit -> unit
    abstract redrawRows: unit -> unit
    abstract setGridOption: string -> obj -> unit
    abstract getSelectedNodes: unit -> IRowNode<'row>[]
    abstract getCellRanges: unit -> ICellRange[]
    abstract getColumns: unit -> IColumn array
    abstract autoSizeColumns: string array -> unit
    abstract exportDataAsCsv: obj -> unit
    abstract moveColumnByIndex: int -> int -> unit
    abstract forEachNodeAfterFilter: (IRowNode<'row> -> int -> unit) -> unit
    /// Phase 6g.C: get a row node by its row Id (the value returned
    /// by the grid's `getRowId` callback, or AG Grid's internal id).
    /// Returns the node when found; null/undefined when the id
    /// doesn't resolve. AG Grid's documented signature returns
    /// `IRowNode | undefined`; we type as `IRowNode<'row>` and rely
    /// on JS null-check at the call site.
    abstract getRowNode: string -> IRowNode<'row>
    /// Phase 6g.C: scroll a row node into view. AG Grid signature is
    /// `ensureNodeVisible(comparator, position?)` where `comparator`
    /// can be the IRowNode itself; `position` is "top" / "middle" /
    /// "bottom" or null. The position arg is `obj` here so callers
    /// can pass a string or null.
    abstract ensureNodeVisible: IRowNode<'row> -> obj -> unit
    /// AG Grid v31+ — returns `true` once the grid has been destroyed
    /// (React unmount, deliberate `destroy()` call, etc.). Per the
    /// AG Grid lifecycle docs (warning #26), every API method called
    /// after destroy logs a console warning and returns `undefined`;
    /// callers that may run on a deferred callback (e.g. inside a
    /// `setTimeout` scheduled from `onGridReady`) must gate on this
    /// before touching any other API method.
    abstract isDestroyed: unit -> bool

// ─── Grid API registry (Phase 6g.C) ─────────────────────────────
//
// Per-tab module-level store that lets external code reach into a
// rendered AG Grid by string key. The grid's own `IGridApi` is
// captured inside `onGridReady` callbacks today; this registry
// makes a chosen subset of grids addressable from companion
// packages (e.g. a client-resident `select_row` AI tool).
//
// Module authors opt in by calling `AgGrid.publishApi "myKey" api`
// from inside their existing `onGridReady` callback. Grids without
// the call are invisible to the registry — same opt-in shape as
// `withAIObservableState`. The key namespace is the deployment's
// concern; module authors should prefix with module Id to avoid
// collisions (e.g. `"SalesAnalysis.skuList"`).
//
// Sanctioned mutable global — same precedent as
// `NotificationClient.handlers` / `ClientToolRuntime.registry`.

module GridApiRegistry =
    open System.Collections.Generic

    let private registry = Dictionary<string, obj>()

    /// Publish a grid's `IGridApi` under a key. Stored as `obj` to
    /// erase the row-type generic so the registry can hold grids of
    /// different row types together. Consumers cast back to
    /// `IGridApi<obj>` and rely on AG Grid's JS-level method
    /// dispatch (which doesn't care about the row type).
    let publish (key: string) (api: IGridApi<'row>) = registry[key] <- box api

    /// Look up a previously-published grid api. Returns `None` when
    /// the key was never published (or the grid never mounted).
    let tryGet (key: string) : IGridApi<obj> option =
        match registry.TryGetValue(key) with
        | true, api -> Some(unbox<IGridApi<obj>> api)
        | false, _ -> None

    /// Enumerate the currently-registered keys (for diagnostics /
    /// admin tooling — an inspect-style AI tool could surface this
    /// list).
    let listKeys () : string list = registry.Keys |> List.ofSeq

    /// Remove a key from the registry. Useful when a module unmounts
    /// a grid; not strictly necessary since stale keys just resolve
    /// to dead grid apis whose method calls fail silently.
    let remove (key: string) = registry.Remove(key) |> ignore


[<Erase>]
type IColumnDefProp<'row, 'value> = interface end

type IRowHeightParameters<'row> = {
    data: 'row option
    node: IRowNode<'row>
    api: IGridApi<'row>
}

let columnDefProp<'row, 'value> = unbox<IColumnDefProp<'row, 'value>>

// Although the AG Grid docs suggest that this should have two type params, we only give it one so that column defs
// with different underlying value types can be used in the same list (for example in AgGrid.columnDefs).
[<Erase>]
type IColumnDef<'row> = interface end

let columnDef<'row> = unbox<IColumnDef<'row>>

[<AutoOpen>]
module CallbackParams =
    /// See https://www.ag-grid.com/react-data-grid/column-properties/#reference-editing-valueSetter.
    /// See https://www.ag-grid.com/react-data-grid/column-properties/#reference-editing-valueParser.
    [<Erase>]
    type IValueChangedParams<'row, 'value> = {
        oldValue: 'value
        newValue: 'value
        node: IRowNode<'row>
        data: 'row
        column: IColumn
        colDef: IColumnDef<'row>
        api: IGridApi<'row>
    } with

        member this.rowIndex = this.node.rowIndex

    /// See https://www.ag-grid.com/react-data-grid/cell-editors/#custom-components.
    [<Erase>]
    type IValueParams<'row, 'value> = {
        value: 'value option
        data: 'row option
        node: IRowNode<'row>
        colDef: IColumnDef<'row>
        column: IColumn
        api: IGridApi<'row>
        rowIndex: int
    }

    /// See https://www.ag-grid.com/react-data-grid/grid-events/#reference-selection-cellFocused.
    [<Erase>]
    type ICellFocusedEvent<'row> = {
        api: IGridApi<'row>
        rowIndex: int
        column: IColumn
        isFullWidthCell: bool
    }

    //see https://www.ag-grid.com/react-data-grid/grid-events/#reference-miscellaneous-gridReady
    [<Erase>]
    type IGridReadyEvent<'row> = { api: IGridApi<'row> }


    /// See https://www.ag-grid.com/react-data-grid//grid-options/#reference-rowModels-getRowId.
    [<Erase>]
    type IGetRowIdParams<'row> = {
        data: 'row
        level: int
        parentKeys: string[]
        api: IGridApi<'row>
        context: obj
    }

    [<Erase>]
    type ICellRendererParams<'row, 'value> = {
        value: 'value option
        data: 'row option
        node: IRowNode<'row>
        colDef: IColumnDef<'row>
        column: IColumn
        api: IGridApi<'row>
        rowIndex: int
    }

    [<Erase>]
    type IPasteEvent<'row> = {
        source: string
        api: IGridApi<'row>
        context: obj
        ``type``: string
    }

    [<Erase>]
    type IProcessDataFromClipboardParams<'row> = {
        data: string[][]
        api: IGridApi<'row>
        context: obj
    }


    type TooltipLocation =
        | AdvancedFilter
        | Cell
        | ColumnToolPanelColumn
        | ColumnToolPanelColumnGroup
        | FilterToolPanelColumnGroup
        | FullWidthRow
        | Header
        | HeaderGroup
        | Menu
        | PivotColumnsList
        | RowGroupColumnsList
        | SetFilterValue
        | ValueColumnsList

    type ITooltipParams<'row, 'value> = {
        location: TooltipLocation
        value: 'value option
        valueFormatted: string option
        rowIndex: int option
        node: IRowNode<'row> option
        data: 'row option
        hideTooltipCallback: unit -> unit option
        api: IGridApi<'row>
    }

type RowSelection =
    | Single
    | Multiple

[<RequireQualifiedAccess>]
type RowFilter =
    | Number
    | Text
    | Date

    member this.FilterText = sprintf "ag%OColumnFilter" this

[<RequireQualifiedAccess>]
type CellDataType =
    | Text
    | Number
    | Date
    | DateString
    | Boolean
    | Object
    | Custom of string

    member this.CellDataTypeText =
        match this with
        | Text -> "text"
        | Number -> "number"
        | Date -> "date"
        | DateString -> "dateString"
        | Boolean -> "boolean"
        | Object -> "object"
        | Custom s -> s

[<RequireQualifiedAccess>]
type AgCellEditor =
    | SelectCellEditor
    | NumberCellEditor
    | DateCellEditor
    | DateStringCellEditor
    | CheckboxCellEditor
    | LargeTextCellEditor
    | TextCellEditor

    member this.CellEditorText = sprintf "ag%O" this

type DOMLayout =
    | Normal
    | AutoHeight
    | Print

    member this.LayoutText =
        match this with
        | Normal -> "normal"
        | AutoHeight -> "autoHeight"
        | Print -> "print"

module ThemeClass =
    let Alpine = "ag-theme-alpine"
    let AlpineDark = "ag-theme-alpine-dark"
    let Balham = "ag-theme-balham"
    let BalhamDark = "ag-theme-balham-dark"
    let Material = "ag-theme-material"

type ColumnType =
    | RightAligned
    | NumericColumn

[<StringEnum>]
type SortDirection =
    | Asc
    | Desc

let openClosed =
    function
    | true -> "open"
    | false -> "closed"

[<ReactComponent>]
let CellRendererComponent<'row, 'value>
    (render: ICellRendererParams<'row, 'value> -> ReactElement, p: ICellRendererParams<'row, 'value>)
    =
    render p

[<Erase>]
type ColumnDef<'row> =
    // Constrain all props for a given column to be for the same value.
    static member inline create<'value>(props: IColumnDefProp<'row, 'value> seq) = createObj !!props |> columnDef<'row>

    static member inline autoComparator() =
        columnDefProp<'row, 'value> ("comparator" ==> compare)

    static member inline cellClass(setClass: 'value -> 'row -> #seq<string>) =
        columnDefProp<'row, 'value> ("cellClass" ==> fun p -> setClass p?value p?data |> Seq.toArray)

    static member inline cellClassRules(rules: (string * ('value -> 'row -> bool)) list) =
        columnDefProp<'row, 'value> (
            "cellClassRules"
            ==> (rules
                 |> List.map (fun (className, rule) -> className ==> fun p -> rule p?value p?data)
                 |> createObj)
        )

    static member cellDataType(v: bool) =
        columnDefProp<'row, 'value> ("cellDataType" ==> v)

    static member cellDataType(v: CellDataType) =
        columnDefProp<'row, 'value> ("cellDataType" ==> v.CellDataTypeText)

    static member cellRenderer(render: ICellRendererParams<'row, 'value> -> ReactElement) =
        columnDefProp<'row, 'value> ("cellRenderer" ==> fun p -> CellRendererComponent(render, p))

    static member cellEditor(render: ICellRendererParams<'row, 'value> -> ReactElement) =
        columnDefProp<'row, 'value> ("cellEditor" ==> fun p -> CellRendererComponent(render, p))

    static member cellEditor(v: string) =
        columnDefProp<'row, 'value> ("cellEditor" ==> v)

    static member cellEditor(v: AgCellEditor) =
        columnDefProp<'row, 'value> ("cellEditor" ==> v.CellEditorText)

    static member cellEditorParams(v: string seq) =
        columnDefProp<'row, 'value> ("cellEditorParams" ==> {| values = v |> Seq.toArray |})

    static member cellEditorParams(v: obj) =
        columnDefProp<'row, 'value> ("cellEditorParams" ==> v)

    static member cellEditorPopup(v: bool) =
        columnDefProp<'row, 'value> ("cellEditorPopup" ==> v)

    static member inline cellStyle(setStyle: 'value -> 'row -> _) =
        columnDefProp<'row, 'value> ("cellStyle" ==> fun p -> setStyle p?value p?data)

    static member inline checkboxSelection(v: bool) =
        columnDefProp<'row, 'value> ("checkboxSelection" ==> v)

    static member inline colId(v: string) =
        columnDefProp<'row, 'value> ("colId" ==> v)

    static member inline columnGroupShow(v: bool) =
        columnDefProp<'row, 'value> ("columnGroupShow" ==> openClosed v)

    static member inline columnType ct =
        columnDefProp<'row, 'value> (
            "type"
            ==> match ct with
                | RightAligned -> "rightAligned"
                | NumericColumn -> "numericColumn"
        )

    static member inline comparator(callback: 'a -> 'a -> int) =
        columnDefProp<'row, 'value> ("comparator" ==> fun a b -> callback a b)

    static member inline editable(callback: 'value -> 'row -> bool) =
        columnDefProp<'row, 'value> ("editable" ==> fun p -> callback p?value p?data)

    static member inline editable(v: bool) =
        columnDefProp<'row, 'value> ("editable" ==> v)

    static member inline equals(callback: 'value -> 'value -> bool) =
        columnDefProp<'row, 'value> ("equals" ==> callback)

    static member inline enableCellChangeFlash(v: bool) =
        columnDefProp<'row, 'value> ("enableCellChangeFlash" ==> v)

    static member inline field(v: string) =
        columnDefProp<'row, 'value> ("field" ==> v)

    /// Usage: `ColumnDef.field _.FieldName` or `ColumnDef.field (fun x -> x.FieldName)`
    static member inline field(f: 'row -> _) =
        // The accessor's emitted JS is parsed for the property name.
        // String fields emit `(r) => r.Name`, but Fable coerces int /
        // int64 field reads to `(r) => (r.Name | 0)` — so "everything
        // after the first dot" wrongly yields "Name | 0)" and AG Grid
        // binds to a non-existent column (blank numeric cells). Take only
        // the leading identifier run after the first dot, which is
        // identical to the old result for string fields.
        let s = string f
        let after = s.Substring(s.IndexOf('.') + 1)

        let field =
            after
            |> Seq.takeWhile (fun c -> Char.IsLetterOrDigit c || c = '_' || c = '$')
            |> Seq.toArray
            |> System.String

        columnDefProp<'row, 'value> ("field" ==> field)

    static member inline filter(v: RowFilter) =
        columnDefProp<'row, 'value> ("filter" ==> v.FilterText)

    static member inline filter(v: bool) =
        columnDefProp<'row, 'value> ("filter" ==> v)

    static member inline floatingFilter(v: bool) =
        columnDefProp<'row, 'value> ("floatingFilter" ==> v)

    static member inline headerCheckboxSelection(v: bool) =
        columnDefProp<'row, 'value> ("headerCheckboxSelection" ==> v)

    static member inline headerClass(v: string) =
        columnDefProp<'row, 'value> ("headerClass" ==> v)

    static member inline headerComponent(callback: 'colId -> 'props -> ReactElement) =
        columnDefProp<'row, 'value> ("headerComponent" ==> fun p -> callback p?column?colId p)

    static member inline headerName(v: string) =
        columnDefProp<'row, 'value> ("headerName" ==> v)

    static member inline wrapHeaderText(v: bool) =
        columnDefProp<'row, 'value> ("wrapHeaderText" ==> v)

    static member inline autoHeaderHeight(v: bool) =
        columnDefProp<'row, 'value> ("autoHeaderHeight" ==> v)

    static member inline hide(v: bool) =
        columnDefProp<'row, 'value> ("hide" ==> v)

    static member inline maxWidth(v: int) =
        columnDefProp<'row, 'value> ("maxWidth" ==> v)

    static member inline minWidth(v: int) =
        columnDefProp<'row, 'value> ("minWidth" ==> v)

    static member inline flex(v: int) =
        columnDefProp<'row, 'value> ("flex" ==> v)

    static member inline onCellClicked(handler: 'value -> 'row -> unit) =
        columnDefProp<'row, 'value> ("onCellClicked" ==> (fun p -> handler p?value p?data))

    static member inline pinned(v: bool) =
        columnDefProp<'row, 'value> ("pinned" ==> v)


    static member inline resizable(v: bool) =
        columnDefProp<'row, 'value> ("resizable" ==> v)

    static member inline rowDrag(v: bool) =
        columnDefProp<'row, 'value> ("rowDrag" ==> v)

    static member inline sortable(v: bool) =
        columnDefProp<'row, 'value> ("sortable" ==> v)

    static member inline suppressKeyboardEvent callback =
        columnDefProp<'row, 'value> ("suppressKeyboardEvent" ==> fun x -> callback x?event)

    static member inline suppressMovable() =
        columnDefProp<'row, 'value> ("suppressMovable" ==> true)

    static member inline valueFormatter(callback: IValueParams<'row, 'value> -> string) =
        columnDefProp<'row, 'value> ("valueFormatter" ==> callback)

    static member inline valueGetter(f: 'row -> 'value) =
        columnDefProp<'row, 'value> ("valueGetter" ==> (fun (x: {| data: 'row option |}) -> x.data |> Option.map f))

    static member inline valueSetter(f: IValueChangedParams<'row, 'value> -> unit) =
        columnDefProp<'row, 'value> ("valueSetter" ==> f)

    static member inline valueSetter(f: IValueChangedParams<'row, 'value> -> bool) =
        columnDefProp<'row, 'value> ("valueSetter" ==> f)

    static member inline valueParser(f: IValueChangedParams<'row, 'value> -> obj) =
        columnDefProp<'row, 'value> ("valueParser" ==> f) // Is never called by AgGrid

    static member inline width(v: int) =
        columnDefProp<'row, 'value> ("width" ==> v)

    /// https://www.ag-grid.com/react-data-grid/tooltips/#reference-ColDef-headerTooltip
    static member inline headerTooltip text =
        columnDefProp<'row, 'value> ("headerTooltip" ==> text)

    /// https://www.ag-grid.com/react-data-grid/tooltips/#reference-ColDef-tooltipValueGetter
    static member inline tooltipValueGetter(f: ITooltipParams<'row, 'value> -> string option) =
        columnDefProp<'row, 'value> ("tooltipValueGetter" ==> f)

    static member inline sort(direction: SortDirection) =
        columnDefProp<'row, 'value> ("sort" ==> direction)

[<Erase>]
type IColumnGroupDefProp<'row> = interface end

let columnGroupDefProp<'row> = unbox<IColumnGroupDefProp<'row>>

[<Erase>]
type ColumnGroup<'row> =
    static member inline headerName(v: string) =
        columnGroupDefProp<'row> ("headerName" ==> v)

    static member inline marryChildren(v: bool) =
        columnGroupDefProp<'row> ("marryChildren" ==> v)

    static member inline openByDefault(v: bool) =
        columnGroupDefProp<'row> ("openByDefault" ==> v)

    static member inline create (props: seq<IColumnGroupDefProp<'row>>) (children: seq<IColumnDef<'row>>) =
        let combinedProps = seq {
            yield! props
            columnGroupDefProp<'row> ("children" ==> Seq.toArray children)
        }

        createObj !!combinedProps |> columnDef<'row>

[<Erase>]
type IAgGridProp<'row> = interface end

let agGridProp<'row> (x: obj) = unbox<IAgGridProp<'row>> x

[<Erase>]
type AgGrid<'row> =
    static member inline animateRows(v: bool) = agGridProp<'row> ("animateRows" ==> v)

    static member inline alwaysShowVerticalScroll(v: bool) =
        agGridProp<'row> ("alwaysShowVerticalScroll" ==> v)

    static member inline columnDefs(columns: IColumnDef<'row> seq) =
        agGridProp<'row> ("columnDefs", Seq.toArray !!columns)

    static member inline debug(v: bool) = agGridProp<'row> ("debug" ==> v)

    static member inline domLayout(l: DOMLayout) =
        agGridProp<'row> ("domLayout", l.LayoutText)

    static member inline enableCellTextSelection(v: bool) =
        agGridProp<'row> ("enableCellTextSelection" ==> v)

    static member inline ensureDomOrder(v: bool) =
        agGridProp<'row> ("ensureDomOrder" ==> v)

    static member inline enterNavigatesVertically(v: bool) =
        agGridProp<'row> ("enterNavigatesVertically" ==> v)

    static member inline getRowId(callback: IGetRowIdParams<'row> -> string) = agGridProp<'row> ("getRowId", callback)

    static member inline onCellEditRequest(callback: obj -> unit) =
        agGridProp<'row> ("onCellEditRequest", callback)

    static member inline onCellValueChanged callback =
        agGridProp<'row> ("onCellValueChanged", (fun x -> callback x?data))

    static member inline onPasteStart(callback: IPasteEvent<'row> -> unit) =
        agGridProp<'row> ("onPasteStart", callback)

    static member inline onPasteEnd(callback: IPasteEvent<'row> -> unit) =
        agGridProp<'row> ("onPasteEnd", callback)

    static member inline onRowClicked(handler: 'value -> 'row -> unit) =
        agGridProp<'row> ("onRowClicked" ==> (fun p -> handler p?value p?data))

    static member inline onSelectionChanged(callback: 'row array -> unit) =
        agGridProp<'row> ("onSelectionChanged", (fun x -> x?api?getSelectedRows () |> callback))

    static member inline readOnlyEdit(v: bool) = agGridProp<'row> ("readOnlyEdit" ==> v)

    static member inline singleClickEdit(v: bool) =
        agGridProp<'row> ("singleClickEdit" ==> v)

    static member inline rowDeselection(v: bool) = agGridProp<'row> ("rowDeselection", v)

    static member inline rowSelection(s: RowSelection) =
        agGridProp<'row> ("rowSelection", s.ToString().ToLower())

    static member inline isRowSelectable(callback: 'row -> bool) =
        agGridProp<'row> ("isRowSelectable" ==> fun x -> x?data |> callback)

    static member inline suppressRowClickSelection(v: bool) =
        agGridProp<'row> ("suppressRowClickSelection" ==> v)

    static member inline rowHeight(h: int) = agGridProp<'row> ("rowHeight", h)

    /// Converts your data to a JS array
    static member inline rowData(data: 'row seq) =
        agGridProp<'row> ("rowData", Seq.toArray data)

    static member inline rowData(data: 'row array) = agGridProp<'row> ("rowData", data)

    static member inline rowDragManaged(v: bool) =
        agGridProp<'row> ("rowDragManaged" ==> v)

    static member inline defaultColDef(defaults: IColumnDefProp<'row, 'value> seq) =
        agGridProp<'row> ("defaultColDef", defaults |> unbox<_ seq> |> createObj)

    static member inline getRowHeight(v: IRowHeightParameters<'row> -> int option) =
        agGridProp<'row> ("getRowHeight", v)

    static member inline getRowId(callback: 'row -> string) =
        agGridProp<'row> ("getRowId", fun x -> x?data |> callback)

    static member onColumnGroupOpened(callback: _ -> unit) = // This can't be inline otherwise Fable produces invalid JS
        let onColumnGroupOpened =
            fun ev ->
                {|
                    AutoSizeGroupColumns =
                        fun () ->
                            // Runs the column autoSize in a 0ms timeout so that the cellRenderer cells render before
                            // the grid calculates how large each cell is. Same destroy-window
                            // hazard as `AutoSizeAllColumns` above: gate on `isDestroyed()` so
                            // warning #26 doesn't fire when the grid was torn down between the
                            // user's column-group toggle and the deferred autoSize call.
                            JS.setTimeout
                                (fun () ->
                                    if not (ev?api?isDestroyed ()) then
                                        let colIds =
                                            ev?columnGroups
                                            |> Seq.head
                                            |> fun cg -> cg?children
                                            |> Array.map (fun x -> x?colId)

                                        ev?api?autoSizeColumns colIds)
                                0
                            |> ignore
                |}
                |> callback

        agGridProp<'row> ("onColumnGroupOpened", onColumnGroupOpened)

    static member inline paginationPageSize(pageSize: int) =
        agGridProp<'row> ("paginationPageSize", pageSize)

    static member inline paginationAutoPageSize(v: bool) =
        agGridProp<'row> ("paginationAutoPageSize", v)

    static member inline pagination(v: bool) = agGridProp<'row> ("pagination", v)

    static member onGridReady(callback: _ -> unit) = // This can't be inline otherwise Fable produces invalid JS
        let onGridReady =
            fun (ev: IGridReadyEvent<'row>) ->
                {|
                    AutoSizeAllColumns =
                        fun () ->
                            // Runs the column autoSize in a 0ms timeout so the cellRenderer
                            // cells render before the grid measures them. An Elmish re-render
                            // (commonly the post-auth `Prefetch.onAllReady` active-module
                            // re-init) can destroy the grid before the timeout fires; AG Grid
                            // v31+ exposes `isDestroyed()` for exactly this case and warns
                            // (#26) on every API call past destroy. Gate the whole block on
                            // it so the warning never fires and we don't touch a dead api.
                            JS.setTimeout
                                (fun () ->
                                    if not (ev.api.isDestroyed ()) then
                                        let cols = ev.api.getColumns ()

                                        if not (isNull (box cols)) then
                                            cols |> Array.map _.getColId() |> ev.api.autoSizeColumns)
                                0
                            |> ignore
                    Export = fun () -> ev.api.exportDataAsCsv (obj ())
                    Custom = fun f -> f ev
                    // Phase 6g.C: Publish this grid's IGridApi under
                    // a string key in the per-tab GridApiRegistry.
                    // Companion packages (e.g. a client-resident
                    // `select_row` AI tool) look it up by the same
                    // key. Modules opt in by calling this from their
                    // onGridReady callback; grids that don't call
                    // Publish are invisible to external lookup.
                    Publish = fun (key: string) -> GridApiRegistry.publish key ev.api
                |}
                |> callback

        agGridProp<'row> ("onGridReady", onGridReady)

    static member inline headerHeight height =
        agGridProp<'row> ("headerHeight", height)


    static member inline groupHeaderHeight height =
        agGridProp<'row> ("groupHeaderHeight", height)

    static member inline onCellFocused callback =
        agGridProp<'row> ("onCellFocused", (fun (e: ICellFocusedEvent<'row>) -> callback e))

    static member inline popupParent parent =
        agGridProp<'row> ("popupParent", parent)

    static member inline stopEditingWhenCellsLoseFocus(v: bool) =
        agGridProp<'row> ("stopEditingWhenCellsLoseFocus", v)

    static member inline suppressRowHoverHighlight(v: bool) =
        agGridProp<'row> ("suppressRowHoverHighlight", v)

    static member inline suppressScrollOnNewData(v: bool) =
        agGridProp<'row> ("suppressScrollOnNewData", v)

    static member inline key(v: string) = agGridProp<'row> (prop.key v)
    static member inline key(v: int) = agGridProp<'row> (prop.key v)
    static member inline key(v: Guid) = agGridProp<'row> (prop.key v)

    static member inline dataTypeDefinitions(v: obj) =
        agGridProp<'row> ("dataTypeDefinitions", v)

    static member inline pinnedBottomRowData(data: 'row array) =
        agGridProp<'row> ("pinnedBottomRowData", data)

    static member inline onFilterChanged(callback: IGridApi<'row> -> unit) =
        agGridProp<'row> ("onFilterChanged", (fun x -> callback x?api))

    static member inline grid(props: IAgGridProp<'row> seq) =
        ensureGridModulesRegistered ()
        MemoizedGrid(createObj !!props)