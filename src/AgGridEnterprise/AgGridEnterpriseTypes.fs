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

        member this.AggregateText = (sprintf "%O" this).ToLower()

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