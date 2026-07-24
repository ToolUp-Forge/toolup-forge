// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module AgGridEnterpriseTypes

// Enterprise-feature typed surface for AG Grid. Lifted from
// `src/ToolUp.Platform.Client/Client/UI/AgGrid.fs` (`module Enterprise`) into the
// companion package so the SDK proper compiles without referencing
// Enterprise feature names. The types are erased and emit no JS imports;
// the actual `import "..." "ag-grid-enterprise"` calls stay in
// AgGridEnterprise.fs at module-top-level.
//
// This file is injected into the consuming client project via
// AgGridEnterprise.Client.props, after ToolUp.Platform.Client.props has
// already injected AgGrid.fs — so `open ToolUp.Platform.AgGrid` resolves
// to the Community types this file extends.

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform.AgGrid

module Enterprise =

    type LoadSuccessParams<'row> = { rowData: 'row array; rowCount: int }

    type ColV0 = {
        id: string
        displayName: string
        field: string option
        aggFunc: string option
    }

    type SortModelItem = { colId: string; sort: string }

    [<StringEnum(caseRules = CaseRules.SnakeCaseAllCaps)>]
    type JoinOperator =
        | Or
        | And

    [<StringEnum>]
    type FilterType =
        | Empty
        | Equals
        | NotEqual
        | LessThan
        | LessThanOrEqual
        | GreaterThan
        | GreaterThanOrEqual
        | InRange
        | Contains
        | NotContains
        | StartsWith
        | EndsWith
        | Blank
        | NotBlank

    [<Erase>]
    type IFilterCondition =
        abstract member ``type``: FilterType with get, set
        abstract member filter: string option with get, set
        abstract member filterTo: string option with get, set
        abstract member dateFrom: string option with get, set
        abstract member dateTo: string option with get, set

    [<Erase>]
    type IFilterModel =
        inherit IFilterCondition
        abstract member filterType: string with get, set
        abstract member operator: JoinOperator option with get, set
        abstract member conditions: IFilterCondition array option with get, set

    [<Erase>]
    type FilterModelMap =
        [<EmitIndexer>]
        abstract member Item: string -> IFilterModel option

    type ServerSideGetRowsRequest = {
        startRow: int option
        endRow: int option
        groupKeys: obj array
        rowGroupCols: ColV0 array
        valueCols: ColV0 array
        sortModel: SortModelItem array
        filterModel: FilterModelMap
    }

    type ServerSideGetRowsParams<'row> = {
        request: ServerSideGetRowsRequest
        success: LoadSuccessParams<'row> -> unit
        fail: unit -> unit
        api: IGridApi<'row>
        parentNode: IRowNode<'row>
    }

    type ServerSideDataSource<'row> = {
        getRows: ServerSideGetRowsParams<'row> -> unit
        destroy: unit -> unit
    }

    /// There are more supported types, but only these are supported by Feliz.Aggrid
    [<StringEnum>]
    type RowModelType =
        | ClientSide
        | ServerSide

    [<RequireQualifiedAccess>]
    type RowFilter =
        | Number
        | Text
        | Date
        | Set
        | Multi

        member this.FilterText = sprintf "ag%OColumnFilter" this

    [<RequireQualifiedAccess>]
    type AgCellEditor =
        | SelectCellEditor
        | NumberCellEditor
        | DateCellEditor
        | DateStringCellEditor
        | CheckboxCellEditor
        | LargeTextCellEditor
        | TextCellEditor
        | RichSelectCellEditor

        member this.RichCellEditorText = sprintf "ag%O" this

    [<RequireQualifiedAccess>]
    type RowGroupingDisplayType =
        | SingleColumn
        | MultipleColumns
        | GroupRows
        | Custom

        member this.RowGroupingDisplayTypeText =
            match this with
            | SingleColumn -> "singleColumn"
            | MultipleColumns -> "multipleColumns"
            | GroupRows -> "groupRows"
            | Custom -> "custom"

    [<RequireQualifiedAccess>]
    type RowGroupPanelShow =
        | Always
        | OnlyWhenGrouping
        | Never

        member this.RowGroupPanelShowText =
            match this with
            | Always -> "always"
            | OnlyWhenGrouping -> "onlyWhenGrouping"
            | Never -> "never"

    [<RequireQualifiedAccess>]
    type AggregateFunction =
        | Sum
        | Min
        | Max
        | Count
        | Avg
        | First
        | Last
        // Phase 12e — reference a custom aggregate registered via
        // `Enterprise.AgGrid.aggFuncs`. The string is the registration key.
        | Custom of string

        member this.AggregateText =
            match this with
            | Custom name -> name
            | _ -> (sprintf "%O" this).ToLower()

    type MenuItemDef = {
        name: string
        action: (unit -> unit) option
        shortcut: string option
        icon: obj option //HtmlElement
    }

    type IGroupCellRendererParams<'row, 'value> = {
        suppressCount: bool
        suppressDoubleClickExpand: bool
        checkBox: bool
        innerRenderer: ICellRendererParams<'row, 'value> -> ReactElement
        innerRendererParams: obj array
        totalValueGetter: string
    }

    [<RequireQualifiedAccess>]
    type BuiltInMenuItem =
        | AutoSizeAll
        | ExpandAll
        | ContractAll
        | Copy
        | CopyWithHeaders
        | CopyWithGroupHeaders
        | Cut
        | Paste
        | ResetColumns
        | Export
        | CsvExport
        | ExcelExport
        | ChartRange
        | PivotChart

        member this.BuiltInMenuItemText =
            match this with
            | AutoSizeAll -> "autoSizeAll"
            | ExpandAll -> "expandAll"
            | ContractAll -> "contractAll"
            | Copy -> "copy"
            | CopyWithHeaders -> "copyWithHeaders"
            | CopyWithGroupHeaders -> "copyWithGroupHeaders"
            | Cut -> "cut"
            | Paste -> "paste"
            | ResetColumns -> "resetColumns"
            | Export -> "export"
            | CsvExport -> "csvExport"
            | ExcelExport -> "excelExport"
            | ChartRange -> "chartRange"
            | PivotChart -> "pivotChart"

    type MenuItem =
        | BuiltIn of BuiltInMenuItem
        | Custom of MenuItemDef

    // ═══ Phase 12e — Enterprise grid feature params ══════════════════
    // Sourced from node_modules/ag-grid-enterprise/dist/types/src/.
    // All records use option fields (None erases to undefined) so a caller
    // sets only what a feature needs. Long-tail fields stay as `obj`.

    /// Set Filter params. Bound on `Enterprise.ColumnDef.setFilterParams`.
    type SetFilterParams<'row, 'value> = {
        values: 'value array option
        /// Async value supplier: `(params) => void` calling `params.success(vals)`.
        valuesCallback: (obj -> unit) option
        valueFormatter: (obj -> string) option
        cellRenderer: (obj -> ReactElement) option
        suppressMiniFilter: bool option
        suppressSelectAll: bool option
        suppressRemoveEntries: bool option
        suppressSorting: bool option
        excelMode: string option
        defaultToNothingSelected: bool option
        treeList: bool option
        treeListPathGetter: (obj -> string array) option
        keyCreator: (obj -> string) option
        comparator: (obj -> obj -> int) option
        caseSensitive: bool option
    }

    /// A single entry in a Multi Filter's filter stack.
    type MultiFilterEntry = {
        /// "agTextColumnFilter" | "agNumberColumnFilter" | "agSetColumnFilter" | ...
        filter: string
        filterParams: obj option
        /// "subMenu" | "accordion"
        display: string option
        title: string option
    }

    /// Multi Filter params. Bound on `Enterprise.ColumnDef.multiFilterParams`.
    type MultiFilterParams = { filters: MultiFilterEntry array }

    /// Excel cell style. `id` is referenced from a cell's `cellClass`.
    /// Nested style groups stay `obj` — build with anonymous records
    /// (`{| bold = true |}` etc.). See exceljs-shaped ExcelStyle upstream.
    type ExcelStyle = {
        id: string
        alignment: obj option
        borders: obj option
        font: obj option
        interior: obj option
        numberFormat: obj option
        protection: obj option
        dataType: string option
    }

    /// Excel export params. Bound on `IGridApi.exportDataAsExcel`.
    type ExcelExportParams = {
        fileName: string option
        sheetName: string option
        author: string option
        columnKeys: string array option
        onlySelected: bool option
        onlySelectedAllPages: bool option
        allColumns: bool option
        skipColumnHeaders: bool option
        skipColumnGroupHeaders: bool option
        skipRowGroups: bool option
        skipPinnedTop: bool option
        skipPinnedBottom: bool option
        prependContent: obj option
        appendContent: obj option
        processCellCallback: (obj -> string) option
        processGroupHeaderCallback: (obj -> string) option
        processRowGroupCallback: (obj -> string) option
        autoConvertFormulas: bool option
    }

    /// Master / Detail params. Bound on `Enterprise.AgGrid.masterDetailParams`.
    type MasterDetailParams<'row, 'detail> = {
        detailRowAutoHeight: bool option
        detailRowHeight: int option
        /// Typed inner-grid options (build from `AgGrid.*` / `Enterprise.AgGrid.*`).
        detailGridOptions: obj option
        /// `params.successCallback(detailRows)` supplies the detail rows.
        getDetailRowData: (obj -> unit) option
        keepDetailRows: bool option
        keepDetailRowsCount: int option
    }

    [<RequireQualifiedAccess>]
    type BuiltInStatusPanel =
        | TotalRowCount
        | FilteredRowCount
        | SelectedRowCount
        | TotalAndFilteredRowCount
        | Aggregation

        member this.PanelName =
            match this with
            | TotalRowCount -> "agTotalRowCountComponent"
            | FilteredRowCount -> "agFilteredRowCountComponent"
            | SelectedRowCount -> "agSelectedRowCountComponent"
            | TotalAndFilteredRowCount -> "agTotalAndFilteredRowCountComponent"
            | Aggregation -> "agAggregationComponent"

    /// One status-bar panel. Use `statusPanelBuiltIn` / `statusPanelCustom`
    /// to construct.
    type StatusPanelDef = {
        statusPanel: string
        align: string option
        key: string option
        statusPanelParams: obj option
    }

    [<RequireQualifiedAccess>]
    type BuiltInToolPanel =
        | Columns
        | Filters
        | Pivot

        member this.ToolPanelName =
            match this with
            | Columns -> "agColumnsToolPanel"
            | Filters -> "agFiltersToolPanel"
            | Pivot -> "agColumnsToolPanel"

    type ToolPanelDef = {
        id: string
        labelKey: string
        labelDefault: string
        iconKey: string
        toolPanel: string
        toolPanelParams: obj option
        minWidth: int option
        maxWidth: int option
        width: int option
    }

    type SideBarDef = {
        toolPanels: ToolPanelDef array
        defaultToolPanel: string option
        position: string option
        hiddenByDefault: bool option
    }

    /// Params for `IGridApi.createRangeChart` / `createPivotChart` /
    /// `createCrossFilterChart`.
    type IChartParams = {
        /// "groupedColumn" | "stackedColumn" | "line" | "pie" | "scatter" |
        /// "bubble" | "area" | "histogram" | "donut" | ...
        chartType: string
        /// cellRange = {| rowStartIndex; rowEndIndex; columns = string[] |}
        cellRange: obj option
        suppressChartRanges: bool option
        /// A DOM element to render the chart into (unlinked mode).
        chartContainer: obj option
        chartThemeName: string option
        chartThemeOverrides: obj option
        unlinkChart: bool option
        switchCategorySeries: bool option
    }

    /// Params for `IGridApi.addCellRange`.
    type CellRangeParams = {
        rowStartIndex: int option
        rowEndIndex: int option
        columnStart: string option
        columnEnd: string option
        columns: string array option
    }

    /// A custom aggregate function's params (`Enterprise.AgGrid.aggFuncs`).
    type IAggFuncParams<'value> = {
        values: 'value array
        rowNode: IRowNode<obj>
        colDef: obj
        column: IColumn
        api: obj
        context: obj
    }

    /// Server-Side transaction applied via `IGridApi.applyServerSideTransaction`.
    type ServerSideTransaction<'row> = {
        route: obj array option
        add: 'row array option
        addIndex: int option
        remove: 'row array option
        update: 'row array option
    }

    [<Erase>]
    type ColumnDef<'row> =
        static member inline filter(v: RowFilter) =
            columnDefProp<'row, 'value> ("filter" ==> v.FilterText)

        static member cellEditor(v: AgCellEditor) =
            columnDefProp<'row, 'value> ("cellEditor" ==> v.RichCellEditorText)

        static member inline pivot(v: bool) =
            columnDefProp<'row, 'value> ("pivot" ==> v)

        static member inline aggFunc(v: AggregateFunction) =
            columnDefProp<'row, 'value> ("aggFunc" ==> v.AggregateText)

        static member inline rowGroup(v: bool) =
            columnDefProp<'row, 'value> ("rowGroup" ==> v)

        static member inline suppressAggFuncInHeader(v: bool) =
            columnDefProp<'row, 'value> ("suppressAggFuncInHeader" ==> v)

        static member inline cellRendererParams(v: IGroupCellRendererParams<'row, 'value>) =
            columnDefProp<'row, 'value> ("cellRendererParams" ==> v)

        // ─── Phase 12e Enterprise filters ───────────────────────
        static member inline setFilterParams(v: SetFilterParams<'row, 'value>) =
            columnDefProp<'row, 'value> ("filterParams" ==> v)

        static member inline multiFilterParams(v: MultiFilterParams) =
            columnDefProp<'row, 'value> ("filterParams" ==> v)

    [<Erase>]
    type AgGrid<'row> =
        static member inline autoGroupColumnDef(values: IColumnDefProp<'row, 'value> seq) =
            agGridProp ("autoGroupColumnDef", values |> unbox<_ seq> |> createObj)

        static member inline getContextMenuItems(callback: int -> int -> MenuItem list) =
            agGridProp<'row> (
                "getContextMenuItems",
                fun x ->
                    let menuItems = callback x?node?rowIndex x?column?colId

                    [|
                        for item in menuItems do
                            match item with
                            | BuiltIn builtInItemName -> box builtInItemName.BuiltInMenuItemText
                            | Custom customMenuItem -> box customMenuItem
                    |]
            )

        static member inline getDataPath(v: 'row -> string array) = agGridProp<'row> ("getDataPath", v)

        static member inline groupDisplayType(v: RowGroupingDisplayType) =
            agGridProp<'row> ("groupDisplayType", v.RowGroupingDisplayTypeText)

        static member inline pivotMode(v: bool) = agGridProp<'row> ("pivotMode", v)

        static member inline rowGroupPanelShow(v: RowGroupPanelShow) =
            agGridProp<'row> ("rowGroupPanelShow", v.RowGroupPanelShowText)

        static member inline rowModelType(v: RowModelType) = agGridProp<'row> ("rowModelType", v)

        static member inline serverSideDataSource<'row>(v: ServerSideDataSource<'row>) =
            agGridProp<'row> ("serverSideDatasource", v)

        static member inline serverSideOnlyRefreshFilteredGroups<'row>(v: bool) =
            agGridProp<'row> ("serverSideOnlyRefreshFilteredGroups", v)

        static member inline treeData(v: bool) = agGridProp<'row> ("treeData", v)

        // ─── Phase 12e Enterprise grid options ──────────────────

        static member inline excelStyles(v: ExcelStyle seq) =
            agGridProp<'row> ("excelStyles", Seq.toArray v)

        static member inline masterDetail(v: bool) = agGridProp<'row> ("masterDetail", v)

        static member inline detailCellRendererParams(v: MasterDetailParams<'row, 'detail>) =
            agGridProp<'row> ("detailCellRendererParams", v)

        static member inline detailRowHeight(v: int) = agGridProp<'row> ("detailRowHeight", v)

        static member inline detailRowAutoHeight(v: bool) =
            agGridProp<'row> ("detailRowAutoHeight", v)

        static member inline statusBar(panels: StatusPanelDef seq) =
            agGridProp<'row> ("statusBar", {| statusPanels = Seq.toArray panels |})

        static member inline sideBar(v: SideBarDef) = agGridProp<'row> ("sideBar", v)

        /// Shorthand: default Columns + Filters tool panels.
        static member inline sideBar(v: bool) = agGridProp<'row> ("sideBar", v)

        static member inline enableCharts(v: bool) = agGridProp<'row> ("enableCharts", v)

        static member inline cellSelection(v: bool) = agGridProp<'row> ("cellSelection", v)

        static member inline chartThemes(v: string array) = agGridProp<'row> ("chartThemes", v)

        /// Register named custom aggregate functions referenced by
        /// `AggregateFunction.Custom name`.
        static member inline aggFuncs(v: Map<string, IAggFuncParams<'value> -> 'value>) =
            agGridProp<'row> ("aggFuncs", v |> Map.toList |> List.map (fun (k, f) -> k ==> f) |> createObj)

    // ─── Constructors for the Enterprise param records ──────────────

    /// Built-in status-bar panel.
    let statusPanelBuiltIn (panel: BuiltInStatusPanel) (align: string option) : StatusPanelDef = {
        statusPanel = panel.PanelName
        align = align
        key = None
        statusPanelParams = None
    }

    /// Built-in tool panel entry for a `SideBarDef`.
    let toolPanelBuiltIn (panel: BuiltInToolPanel) (labelKey: string) (labelDefault: string) : ToolPanelDef = {
        id =
            match panel with
            | BuiltInToolPanel.Filters -> "filters"
            | _ -> "columns"
        labelKey = labelKey
        labelDefault = labelDefault
        iconKey =
            match panel with
            | BuiltInToolPanel.Filters -> "filter"
            | _ -> "columns"
        toolPanel = panel.ToolPanelName
        toolPanelParams = None
        minWidth = None
        maxWidth = None
        width = None
    }

    // ─── IGridApi Enterprise API surface (Phase 12e) ────────────────
    // Extension members on the erased Community IGridApi; each dispatches
    // dynamically to the Enterprise-registered API method on the live grid.

    type IGridApi<'row> with
        /// Integrated-charts: create a range chart from a cell range.
        member inline this.createRangeChart(p: IChartParams) : obj = this?createRangeChart (p)
        member inline this.createPivotChart(p: IChartParams) : obj = this?createPivotChart (p)

        member inline this.createCrossFilterChart(p: IChartParams) : obj = this?createCrossFilterChart (p)

        /// Range (cell) selection.
        member inline this.addCellRange(p: CellRangeParams) : unit = this?addCellRange (p)
        member inline this.clearRangeSelection() : unit = this?clearRangeSelection ()

        /// Excel export.
        member inline this.exportDataAsExcel(p: ExcelExportParams) : unit = this?exportDataAsExcel (p)

        /// Server-Side Row Model.
        member inline this.setServerSideDatasource(ds: ServerSideDataSource<'row>) : unit =
            this?setServerSideDatasource (ds)

        member inline this.getInfiniteRowCount() : int = this?getInfiniteRowCount ()
        member inline this.getCacheBlockState() : obj = this?getCacheBlockState ()

        member inline this.forEachServerSideGroup(callback: IRowNode<'row> -> unit) : unit =
            this?forEachServerSideGroup (callback)

        member inline this.refreshServerSide(p: obj) : unit = this?refreshServerSide (p)

        member inline this.applyServerSideTransaction(tx: ServerSideTransaction<'row>) : obj =
            this?applyServerSideTransaction (tx)