// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

[<AutoOpen>]
module ToolUp.Platform.AgGridEnterprise

// Phase 15d.5 — Enterprise inline static members extracted from the
// Community grid binding (`module Feliz.AgGrid`) via cross-file type
// augmentation on the [<Erase>] grid types. Members here gate AG Grid
// Enterprise features:
//   - Range Selection / Fill Handle / Clipboard API (~9 members)
//   - Aggregation (`suppressAggFuncInHeader`)
//   - Undo/Redo for cell editing (2 members)
//   - Row Grouping (`enableRowGroup`, `rowGroup` on ColumnDef)
//
// `[<AutoOpen>]` under `namespace ToolUp.Platform` makes the augmented
// members visible to any caller that has `open ToolUp.Platform` in
// scope — no consumer source edits. Phase 344 moved the file out of
// ToolUp.Platform.Client into this opt-in package (GP 2), which is what
// makes the Enterprise-gated members opt-in rather than always present;
// the module NAME is deliberately unchanged so a consumer that already
// referenced the companion sees no source break.
//
// Module name is `AgGridEnterprise` (sibling to `AgGrid`) rather than
// `AgGrid.Enterprise` because F# rejects `module X` and `module X.Sub`
// across files of the same assembly (FS0247: "A namespace and a module
// named 'X' both occur in two parts of this assembly"). The sibling
// shape preserves the same import-free experience for consumers.
//
// Pattern B verified spike-green: Fable inlines augmented members at
// the call site identically to original-definition members; no
// `AgGridEnterprise.js` artefact is emitted (members are erased), and
// consumer JS imports remain unchanged.

open Fable.Core.JsInterop
open Feliz.AgGrid

#nowarn "1182"

// ─── ColumnDef<'row> Enterprise members (Row Grouping) ───────────

type ColumnDef<'row> with
    static member inline enableRowGroup<'value>(v: bool) =
        columnDefProp<'row, 'value> ("enableRowGroup" ==> v)

    static member inline rowGroup<'value>(v: bool) =
        columnDefProp<'row, 'value> ("rowGroup" ==> v)

// ─── AgGrid<'row> Enterprise members ──────────────────────────────

type AgGrid<'row> with
    // Clipboard API
    static member inline copyHeadersToClipboard(v: bool) =
        agGridProp<'row> ("copyHeadersToClipboard" ==> v)

    static member inline suppressClipboardApi(v: bool) =
        agGridProp<'row> ("suppressClipboardApi", v)

    static member inline suppressCopyRowsToClipboard(v: bool) =
        agGridProp<'row> ("suppressCopyRowsToClipboard", v)

    static member inline suppressCopySingleCellRanges(v: bool) =
        agGridProp<'row> ("suppressCopySingleCellRanges", v)

    static member inline processDataFromClipboard(callback: IProcessDataFromClipboardParams<'row> -> string[][]) =
        agGridProp<'row> ("processDataFromClipboard", callback)

    static member inline processDataFromClipboard(callback: string[][] -> string[][]) =
        agGridProp<'row> ("processDataFromClipboard", (fun x -> callback x?data))

    // Range Selection
    static member inline enableRangeHandle(v: bool) =
        agGridProp<'row> ("enableRangeHandle", v)

    static member inline enableRangeSelection(v: bool) =
        agGridProp<'row> ("enableRangeSelection", v)

    static member inline onRangeSelectionChanged callback =
        agGridProp<'row> (
            "onRangeSelectionChanged",
            fun x ->
                let selectedRange = x?api?getCellRanges ()?at 0
                let startRow = selectedRange?startRow?rowIndex
                let startColumn = selectedRange?columns?at 0?colId
                let endRow = selectedRange?endRow?rowIndex
                let endColumn = selectedRange?columns?at (selectedRange?columns?length - 1)?colId

                callback startRow startColumn endRow endColumn
        )

    static member inline suppressMultiRangeSelection(v: bool) =
        agGridProp<'row> ("suppressMultiRangeSelection", v)

    // Fill Handle (depends on Range Selection)
    static member inline enableFillHandle(v: bool) =
        agGridProp<'row> ("enableFillHandle", v)

    // Aggregation
    static member inline suppressAggFuncInHeader(v: bool) =
        agGridProp<'row> ("suppressAggFuncInHeader", v)

    // Undo/Redo for cell editing
    static member inline undoRedoCellEditing(v: bool) =
        agGridProp<'row> ("undoRedoCellEditing", v)

    static member inline undoRedoCellEditingLimit(v: int) =
        agGridProp<'row> ("undoRedoCellEditingLimit", v)