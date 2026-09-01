// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 344 — backward-compatibility re-export for the AG Grid binding.
///
/// The binding itself moved to the standalone `Feliz.AgGrid` package and
/// its module was renamed `ToolUp.Platform.AgGrid` -> `Feliz.AgGrid`. This
/// file keeps `open ToolUp.Platform.AgGrid` compiling for consumers that
/// have not yet moved (GP 11), by re-declaring the module name over the
/// new one: every public type is a type abbreviation and every public
/// value a forwarding binding, so a call site resolves to exactly the same
/// underlying declaration it did before.
///
/// ── The two things a re-export in F# cannot carry ────────────────────
///
/// F# has no `export *`, so this is written out by hand, and two shapes do
/// not survive the transcription. Both are named here rather than left for
/// a consumer to discover:
///
///   1. **Bare record-label construction.** A type abbreviation does not
///      bring the underlying record's LABELS into scope, so
///      `{ spacing = None; ... }` for a `ThemeParams` resolves only under
///      `open Feliz.AgGrid`. The documented and overwhelmingly common form
///      — `{ ThemeParams.empty with accentColor = Some "#59229D" }` — is
///      unaffected, because it names the type.
///   2. **Union-case PATTERNS.** A type abbreviation does not re-export the
///      cases, and the in-tree sample proved that unqualified cases really
///      are used (`Series.seriesKind Bar`, `Axis.position Bottom`), so each
///      case of every non-`[<RequireQualifiedAccess>]` union is re-exported
///      below as a VALUE. That restores construction exactly. It does not
///      restore matching: `match x with | Bottom -> …` binds a fresh
///      variable rather than testing the case — F# warns (FS0049) but
///      compiles. These are configuration enums passed to props, so a
///      consumer matching on one is unlikely; it is named because the
///      failure is quiet where every other one here is loud.
///
/// Consumers hitting either should `open Feliz.AgGrid` — which is where
/// they are going anyway. This shim is a migration aid and is scheduled for
/// retirement in a future minor, not a permanent second surface.
[<System.Obsolete "The AG Grid binding moved to the standalone Feliz.AgGrid package — `open Feliz.AgGrid` instead. This compat module is retired in a future minor.">]
module ToolUp.Platform.AgGrid

// The re-exported `ThemeClass` members are themselves [<Obsolete>] in the
// source module, and this module carries the migration [<Obsolete>] above,
// so forwarding them here would otherwise warn on every line.
#nowarn "44"

// ─── Module configuration + provider ─────────────────────────────

type AgGridModuleConfig = Feliz.AgGrid.AgGridModuleConfig

module AgGridModuleConfig =
    /// Community-only configuration — zero-config default. No license key required.
    let community = Feliz.AgGrid.AgGridModuleConfig.community

/// Wrap a React subtree in AgGridProvider, supplying AG Grid modules and
/// optional license key to all <AgGridReact> instances within the subtree.
let provider config children = Feliz.AgGrid.provider config children

let setGridModulesRegistered () =
    Feliz.AgGrid.setGridModulesRegistered ()

let ensureGridModulesRegistered () =
    Feliz.AgGrid.ensureGridModulesRegistered ()

let agGrid: obj = Feliz.AgGrid.agGrid

let MemoizedGrid reactProps = Feliz.AgGrid.MemoizedGrid reactProps

// ─── Grid api + row/column shapes ────────────────────────────────

type IRowNode<'row> = Feliz.AgGrid.IRowNode<'row>
type ICellRange = Feliz.AgGrid.ICellRange
type IColumn = Feliz.AgGrid.IColumn
type IGridApi<'row> = Feliz.AgGrid.IGridApi<'row>
type IFilterModelEntry = Feliz.AgGrid.IFilterModelEntry

module IFilterModelEntry =
    /// All-None starting point; set only the fields the filter needs.
    let empty = Feliz.AgGrid.IFilterModelEntry.empty

module GridApiRegistry =
    /// Publish a grid's `IGridApi` under a key.
    let publish (key: string) (api: IGridApi<'row>) =
        Feliz.AgGrid.GridApiRegistry.publish key api

    /// Look up a previously-published grid api.
    let tryGet (key: string) = Feliz.AgGrid.GridApiRegistry.tryGet key

    /// Enumerate the currently-registered keys.
    let listKeys () =
        Feliz.AgGrid.GridApiRegistry.listKeys ()

    /// Remove a key from the registry.
    let remove (key: string) = Feliz.AgGrid.GridApiRegistry.remove key

// ─── Column-def / grid-prop carriers ─────────────────────────────

type IColumnDefProp<'row, 'value> = Feliz.AgGrid.IColumnDefProp<'row, 'value>
type IRowHeightParameters<'row> = Feliz.AgGrid.IRowHeightParameters<'row>

let columnDefProp<'row, 'value> = Feliz.AgGrid.columnDefProp<'row, 'value>

type IColumnDef<'row> = Feliz.AgGrid.IColumnDef<'row>

let columnDef<'row> = Feliz.AgGrid.columnDef<'row>

// ─── Callback / event parameter shapes ───────────────────────────

[<AutoOpen>]
module CallbackParams =
    type IValueChangedParams<'row, 'value> = Feliz.AgGrid.CallbackParams.IValueChangedParams<'row, 'value>
    type IValueParams<'row, 'value> = Feliz.AgGrid.CallbackParams.IValueParams<'row, 'value>
    type ICellFocusedEvent<'row> = Feliz.AgGrid.CallbackParams.ICellFocusedEvent<'row>
    type IGridReadyEvent<'row> = Feliz.AgGrid.CallbackParams.IGridReadyEvent<'row>
    type IGetRowIdParams<'row> = Feliz.AgGrid.CallbackParams.IGetRowIdParams<'row>
    type ICellRendererParams<'row, 'value> = Feliz.AgGrid.CallbackParams.ICellRendererParams<'row, 'value>
    type IPasteEvent<'row> = Feliz.AgGrid.CallbackParams.IPasteEvent<'row>
    type IProcessDataFromClipboardParams<'row> = Feliz.AgGrid.CallbackParams.IProcessDataFromClipboardParams<'row>
    type TooltipLocation = Feliz.AgGrid.CallbackParams.TooltipLocation
    type ITooltipParams<'row, 'value> = Feliz.AgGrid.CallbackParams.ITooltipParams<'row, 'value>

    // Case re-exports — see the union-case note in the file header.
    let AdvancedFilter = TooltipLocation.AdvancedFilter
    let Cell = TooltipLocation.Cell
    let ColumnToolPanelColumn = TooltipLocation.ColumnToolPanelColumn
    let ColumnToolPanelColumnGroup = TooltipLocation.ColumnToolPanelColumnGroup
    let FilterToolPanelColumnGroup = TooltipLocation.FilterToolPanelColumnGroup
    let FullWidthRow = TooltipLocation.FullWidthRow
    let Header = TooltipLocation.Header
    let HeaderGroup = TooltipLocation.HeaderGroup
    let Menu = TooltipLocation.Menu
    let PivotColumnsList = TooltipLocation.PivotColumnsList
    let RowGroupColumnsList = TooltipLocation.RowGroupColumnsList
    let SetFilterValue = TooltipLocation.SetFilterValue
    let ValueColumnsList = TooltipLocation.ValueColumnsList

[<AutoOpen>]
module GridEvents =
    type ICellEvent<'row, 'value> = Feliz.AgGrid.GridEvents.ICellEvent<'row, 'value>
    type IRowEvent<'row> = Feliz.AgGrid.GridEvents.IRowEvent<'row>
    type IColumnEvent<'row> = Feliz.AgGrid.GridEvents.IColumnEvent<'row>
    type ISortChangedEvent<'row> = Feliz.AgGrid.GridEvents.ISortChangedEvent<'row>
    type IGridDisplayEvent<'row> = Feliz.AgGrid.GridEvents.IGridDisplayEvent<'row>

// ─── Configuration enums ─────────────────────────────────────────

type RowSelection = Feliz.AgGrid.RowSelection
type RowFilter = Feliz.AgGrid.RowFilter
type CellDataType = Feliz.AgGrid.CellDataType
type AgCellEditor = Feliz.AgGrid.AgCellEditor
type DOMLayout = Feliz.AgGrid.DOMLayout
type ColumnType = Feliz.AgGrid.ColumnType
type SortDirection = Feliz.AgGrid.SortDirection

// Case re-exports for the unions that are NOT [<RequireQualifiedAccess>] —
// see the union-case note in the file header. `RowFilter` / `CellDataType` /
// `AgCellEditor` are qualified-access by declaration, so the abbreviation
// above already carries them.
let Single = RowSelection.Single
let Multiple = RowSelection.Multiple
let Normal = DOMLayout.Normal
let AutoHeight = DOMLayout.AutoHeight
let Print = DOMLayout.Print
let RightAligned = ColumnType.RightAligned
let NumericColumn = ColumnType.NumericColumn
let Asc = SortDirection.Asc
let Desc = SortDirection.Desc

// ─── Theming ─────────────────────────────────────────────────────

/// Legacy string theme classes. Deprecated in favour of the Theming API.
[<System.Obsolete "Prefer the Theming API: AgGrid.theme (Theme.themeQuartz |> Theme.withParams ...)">]
module ThemeClass =
    let Alpine = Feliz.AgGrid.ThemeClass.Alpine
    let AlpineDark = Feliz.AgGrid.ThemeClass.AlpineDark
    let Balham = Feliz.AgGrid.ThemeClass.Balham
    let BalhamDark = Feliz.AgGrid.ThemeClass.BalhamDark
    let Material = Feliz.AgGrid.ThemeClass.Material

type ThemeParams = Feliz.AgGrid.ThemeParams

module ThemeParams =
    let empty = Feliz.AgGrid.ThemeParams.empty

module Theme =
    let themeQuartz: obj = Feliz.AgGrid.Theme.themeQuartz
    let themeBalham: obj = Feliz.AgGrid.Theme.themeBalham
    let themeMaterial: obj = Feliz.AgGrid.Theme.themeMaterial
    let themeAlpine: obj = Feliz.AgGrid.Theme.themeAlpine
    let colorSchemeDark: obj = Feliz.AgGrid.Theme.colorSchemeDark
    let colorSchemeLight: obj = Feliz.AgGrid.Theme.colorSchemeLight
    let colorSchemeDarkBlue: obj = Feliz.AgGrid.Theme.colorSchemeDarkBlue
    let iconSetMaterial: obj = Feliz.AgGrid.Theme.iconSetMaterial
    let iconSetQuartz: obj = Feliz.AgGrid.Theme.iconSetQuartz
    let iconSetAlpine: obj = Feliz.AgGrid.Theme.iconSetAlpine
    let withParams (p: ThemeParams) (theme: obj) = Feliz.AgGrid.Theme.withParams p theme
    let withPart (part: obj) (theme: obj) = Feliz.AgGrid.Theme.withPart part theme

    let withoutPart (feature: string) (theme: obj) =
        Feliz.AgGrid.Theme.withoutPart feature theme

    let withCustomCss (raw: obj) (theme: obj) =
        Feliz.AgGrid.Theme.withCustomCss raw theme

// ─── Export / locale / filter parameter records ──────────────────

type CsvExportParams = Feliz.AgGrid.CsvExportParams

module CsvExportParams =
    let empty = Feliz.AgGrid.CsvExportParams.empty

type LocaleText = Feliz.AgGrid.LocaleText

module LocaleText =
    let empty = Feliz.AgGrid.LocaleText.empty

type LocaleTextDictionary = Feliz.AgGrid.LocaleTextDictionary

type TextFilterParams = Feliz.AgGrid.TextFilterParams

module TextFilterParams =
    let empty = Feliz.AgGrid.TextFilterParams.empty

type NumberFilterParams = Feliz.AgGrid.NumberFilterParams

module NumberFilterParams =
    let empty = Feliz.AgGrid.NumberFilterParams.empty

type DateFilterParams = Feliz.AgGrid.DateFilterParams

module DateFilterParams =
    let empty = Feliz.AgGrid.DateFilterParams.empty

// ─── Cell renderer helpers ───────────────────────────────────────

let openClosed (isOpen: bool) = Feliz.AgGrid.openClosed isOpen

let CellRendererComponent<'row, 'value>
    (render: ICellRendererParams<'row, 'value> -> Fable.React.ReactElement, p: ICellRendererParams<'row, 'value>)
    =
    Feliz.AgGrid.CellRendererComponent<'row, 'value>(render, p)

// ─── The grid + column builders ──────────────────────────────────

type ColumnDef<'row> = Feliz.AgGrid.ColumnDef<'row>
type IColumnGroupDefProp<'row> = Feliz.AgGrid.IColumnGroupDefProp<'row>
type ColumnGroup<'row> = Feliz.AgGrid.ColumnGroup<'row>
type IAgGridProp<'row> = Feliz.AgGrid.IAgGridProp<'row>

let columnGroupDefProp<'row> = Feliz.AgGrid.columnGroupDefProp<'row>

let agGridProp<'row> (x: obj) = Feliz.AgGrid.agGridProp<'row> x

type AgGrid<'row> = Feliz.AgGrid.AgGrid<'row>