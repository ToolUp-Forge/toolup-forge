// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Xlsx.CellAddress

open System

// ─── Cell-address keys (Phase 23 — cell-address-map binding mode) ────
//
// Alongside `{{key}}` token templates, the Xlsx renderer accepts
// placeholder keys shaped as cell addresses — `"Sheet1!B7"` (quoted
// sheet names supported: `"'My Sheet'!B7"`) — which write their value
// directly into that cell without any template-side token markup. The
// use case is rendering against an existing workbook whose layout was
// authored visually.
//
// A key is a cell address when it parses here; anything else is an
// ordinary token key. Parsing is deliberately strict (a real A1-style
// reference), so ordinary keys containing `!` do not silently become
// cell writes.

/// A parsed `Sheet!A1`-style placeholder key.
type CellRef = {
    Sheet: string
    /// The A1-style cell reference (`"B7"`), uppercased.
    Cell: string
    /// 1-based row index parsed from the reference.
    RowIndex: uint32
    /// Column letters (`"B"`), uppercased.
    Column: string
}

/// 1-based column number for column letters (`"A"` → 1, `"AA"` → 27).
let columnNumber (letters: string) : int =
    letters
    |> Seq.fold (fun acc c -> acc * 26 + (int (Char.ToUpperInvariant c) - int 'A' + 1)) 0

let private isCellReference (candidate: string) : bool =
    let letters = candidate |> Seq.takeWhile Char.IsLetter |> Seq.length

    letters >= 1
    && letters <= 3
    && candidate.Length > letters
    && candidate |> Seq.skip letters |> Seq.forall Char.IsDigit

/// Parse a `Sheet!A1`-style key. `None` for anything that is not a
/// strict sheet-qualified A1 reference — those keys stay ordinary
/// placeholder tokens.
let tryParse (key: string) : CellRef option =
    match key.LastIndexOf '!' with
    | -1 -> None
    | bang when bang = 0 || bang = key.Length - 1 -> None
    | bang ->
        let rawSheet = key.Substring(0, bang)
        let rawCell = key.Substring(bang + 1).Trim()

        let sheet =
            if rawSheet.Length >= 2 && rawSheet.StartsWith "'" && rawSheet.EndsWith "'" then
                rawSheet.Substring(1, rawSheet.Length - 2).Replace("''", "'")
            else
                rawSheet

        if String.IsNullOrWhiteSpace sheet || not (isCellReference rawCell) then
            None
        else
            let cell = rawCell.ToUpperInvariant()
            let letters = cell |> Seq.takeWhile Char.IsLetter |> Seq.length

            Some {
                Sheet = sheet
                Cell = cell
                Column = cell.Substring(0, letters)
                RowIndex = uint32 (cell.Substring letters)
            }