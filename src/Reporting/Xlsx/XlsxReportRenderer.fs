// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Reporting.Xlsx.XlsxReportRenderer

open System
open System.Globalization
open System.IO
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet
open ToolUp.Reporting
open ToolUp.Reporting.PlaceholderSubstitution

// ─── XLSX renderer (Phase 23 sub-companion) ──────────────────────────
//
// Two binding modes, one renderer:
//
//   1. **Token templates** — `{{key}}` tokens in cell text (shared-
//      string or inline-string cells) substitute via the shared
//      `PlaceholderSubstitution` machinery. A cell whose entire text is
//      a single token bound to an unhinted `Number` value becomes a
//      NATIVE numeric cell (so downstream formulas can consume it);
//      hinted numbers and dates render as formatted text per the hint.
//   2. **Cell-address map** — placeholder keys shaped `"Sheet1!B7"`
//      (see `CellAddress.tryParse`) write their value directly into the
//      named cell, no template-side markup required. The target cell's
//      style (its `s` attribute, hence its number-format string) is
//      left untouched, so the written value displays per the template's
//      own formatting. Dates written this way land as OADate serial
//      numbers — the template cell's date format renders them.
//
// Formula cells are never substituted into, and the workbook is marked
// for full recalculation on open so formulas referencing filled cells
// re-evaluate.

let private name = "XlsxReportRenderer"

/// Short-circuit signal for typed render errors discovered mid-walk.
exception private RenderFailed of RenderError

let private sharedStringText (workbookPart: WorkbookPart) (index: int) : string option =
    match workbookPart.SharedStringTablePart with
    | null -> None
    | sstPart ->
        match sstPart.SharedStringTable with
        | null -> None
        | sst -> sst.Elements<SharedStringItem>() |> Seq.tryItem index |> Option.map _.InnerText

/// The cell's current text content, when it has a textual shape this
/// renderer substitutes into. Formula cells and empty cells yield
/// `None`.
let private cellText (workbookPart: WorkbookPart) (cell: Cell) : string option =
    if not (isNull (box cell.CellFormula)) then
        None
    elif
        not (isNull (box cell.DataType))
        && cell.DataType.Value = CellValues.SharedString
    then
        match cell.CellValue with
        | null -> None
        | v ->
            match Int32.TryParse(v.Text, NumberStyles.Integer, CultureInfo.InvariantCulture) with
            | true, index -> sharedStringText workbookPart index
            | _ -> None
    elif
        not (isNull (box cell.DataType))
        && cell.DataType.Value = CellValues.InlineString
    then
        match cell.InlineString with
        | null -> None
        | inline' -> Some inline'.InnerText
    elif not (isNull (box cell.DataType)) && cell.DataType.Value = CellValues.String then
        cell.CellValue |> Option.ofObj |> Option.map _.Text
    else
        None

let private writeInlineString (cell: Cell) (value: string) =
    cell.RemoveAllChildren()
    cell.DataType <- EnumValue CellValues.InlineString
    // Explicit AppendChild: an OpenXmlElement passed to a composite
    // element's constructor can bind the IEnumerable overload and be
    // enumerated as its CHILDREN rather than appended itself.
    let inlineString = InlineString()

    inlineString.AppendChild(Spreadsheet.Text(Text = value, Space = SpaceProcessingModeValues.Preserve))
    |> ignore

    cell.InlineString <- inlineString

let private writeNumber (cell: Cell) (value: double) =
    cell.RemoveAllChildren()
    cell.DataType <- null
    cell.CellValue <- CellValue(value.ToString("R", CultureInfo.InvariantCulture))

/// The token-only cell shape: the trimmed text is exactly one
/// `{{key}}` token.
let private wholeCellToken (text: string) : string option =
    let trimmed = text.Trim()

    if
        trimmed.StartsWith "{{"
        && trimmed.EndsWith "}}"
        && trimmed.IndexOf("}}", 2) = trimmed.Length - 2
    then
        Some(trimmed.Substring(2, trimmed.Length - 4).Trim())
    else
        None

/// Insert-in-order helpers — SpreadsheetML requires rows ordered by
/// index and cells ordered by column within a row.
let private ensureRow (sheetData: SheetData) (rowIndex: uint32) : Row =
    let existing =
        sheetData.Elements<Row>()
        |> Seq.tryFind (fun r -> not (isNull (box r.RowIndex)) && r.RowIndex.Value = rowIndex)

    match existing with
    | Some row -> row
    | None ->
        let row = Row(RowIndex = UInt32Value rowIndex)

        let successor =
            sheetData.Elements<Row>()
            |> Seq.tryFind (fun r -> not (isNull (box r.RowIndex)) && r.RowIndex.Value > rowIndex)

        match successor with
        | Some s -> sheetData.InsertBefore(row, s) |> ignore
        | None -> sheetData.AppendChild row |> ignore

        row

let private ensureCell (row: Row) (cellRef: CellAddress.CellRef) : Cell =
    let reference = cellRef.Cell

    let existing =
        row.Elements<Cell>()
        |> Seq.tryFind (fun c ->
            not (isNull (box c.CellReference))
            && String.Equals(c.CellReference.Value, reference, StringComparison.OrdinalIgnoreCase))

    match existing with
    | Some cell -> cell
    | None ->
        let cell = Cell(CellReference = StringValue reference)

        let successor =
            row.Elements<Cell>()
            |> Seq.tryFind (fun c ->
                match Option.ofObj (box c.CellReference) with
                | None -> false
                | Some _ ->
                    let letters =
                        c.CellReference.Value |> Seq.takeWhile Char.IsLetter |> Seq.toArray |> String

                    CellAddress.columnNumber letters > CellAddress.columnNumber cellRef.Column)

        match successor with
        | Some s -> row.InsertBefore(cell, s) |> ignore
        | None -> row.AppendChild cell |> ignore

        cell

/// Mark the workbook for full recalculation on open, so formulas
/// referencing filled cells re-evaluate in the consumer's spreadsheet
/// application.
let private forceRecalculation (workbookPart: WorkbookPart) =
    let workbook = workbookPart.Workbook

    let calcProps =
        match workbook.CalculationProperties with
        | null ->
            let props = CalculationProperties()
            workbook.CalculationProperties <- props
            props
        | props -> props

    calcProps.ForceFullCalculation <- BooleanValue true
    calcProps.FullCalculationOnLoad <- BooleanValue true

let create () : IReportRenderer =
    { new IReportRenderer with
        member _.SupportedFormats = [ Xlsx ]
        member _.Name = name

        member _.Render(template, values) = async {
            match validate template.Placeholders values with
            | Error e -> return Error e
            | Ok() ->
                try
                    use stream = new MemoryStream()
                    stream.Write(template.Body, 0, template.Body.Length)
                    stream.Position <- 0L

                    // Partition: address-shaped keys are direct cell
                    // writes; everything else participates in token
                    // substitution.
                    let cellWrites, tokenValues =
                        values
                        |> Map.toList
                        |> List.partition (fun (key, _) -> (CellAddress.tryParse key).IsSome)

                    let tokenValueMap = Map.ofList tokenValues

                    let renderKey (key: string) =
                        match
                            template.Placeholders |> List.tryFind (fun p -> p.Key = key), tokenValueMap.TryFind key
                        with
                        | Some schema, Some value ->
                            match schema.Kind, value with
                            | Image _, _ -> $"[image: {key} (not supported by {name})]"
                            | Table cols, TableValue rows ->
                                // Compact text fallback — one line per row,
                                // cells tab-joined. Sheet-shaped table fills
                                // belong to the cell-address mode.
                                let header = cols |> List.map _.DisplayName |> String.concat "\t"

                                let lines =
                                    rows
                                    |> List.map (fun row ->
                                        cols
                                        |> List.map (fun c ->
                                            row.TryFind c.Key
                                            |> Option.map (renderScalar c.Kind)
                                            |> Option.defaultValue "")
                                        |> String.concat "\t")

                                header :: lines |> String.concat "\n"
                            | _ -> renderScalar schema.Kind value
                        | _ -> $"{{{{{key}}}}}"

                    do
                        use doc = SpreadsheetDocument.Open(stream, true)

                        let workbookPart =
                            match doc.WorkbookPart with
                            | null -> raise (RenderFailed(RendererFailure(name, "workbook part missing")))
                            | part -> part

                        // ── Pass 1: token substitution over textual cells ──
                        for worksheetPart in workbookPart.WorksheetParts do
                            for cell in worksheetPart.Worksheet.Descendants<Cell>() do
                                match cellText workbookPart cell with
                                | Some text when text.Contains "{{" ->
                                    let handledAsNumber =
                                        match wholeCellToken text with
                                        | Some key ->
                                            match
                                                template.Placeholders |> List.tryFind (fun p -> p.Key = key),
                                                tokenValueMap.TryFind key
                                            with
                                            | Some { Kind = Number None }, Some(NumberValue n) ->
                                                writeNumber cell n
                                                true
                                            | _ -> false
                                        | None -> false

                                    if not handledAsNumber then
                                        let substituted = substituteText renderKey text

                                        if substituted <> text then
                                            writeInlineString cell substituted
                                | _ -> ()

                        // ── Pass 2: cell-address-map writes ───────────────
                        for key, value in cellWrites do
                            let cellRef = (CellAddress.tryParse key).Value

                            let sheet =
                                workbookPart.Workbook.Descendants<Sheet>()
                                |> Seq.tryFind (fun s ->
                                    not (isNull (box s.Name))
                                    && String.Equals(s.Name.Value, cellRef.Sheet, StringComparison.OrdinalIgnoreCase))

                            match sheet with
                            | None ->
                                raise (
                                    RenderFailed(
                                        RendererFailure(
                                            name,
                                            $"sheet '{cellRef.Sheet}' not found for cell write '{key}'"
                                        )
                                    )
                                )
                            | Some sheet ->
                                let worksheetPart = workbookPart.GetPartById sheet.Id.Value :?> WorksheetPart

                                let sheetData =
                                    match worksheetPart.Worksheet.GetFirstChild<SheetData>() with
                                    | null ->
                                        raise (
                                            RenderFailed(
                                                RendererFailure(name, $"sheet '{cellRef.Sheet}' has no sheet data")
                                            )
                                        )
                                    | data -> data

                                let row = ensureRow sheetData cellRef.RowIndex
                                let cell = ensureCell row cellRef

                                // The cell's StyleIndex is deliberately left
                                // untouched — the template's number-format
                                // string keeps governing how the written
                                // value displays.
                                match value with
                                | NumberValue n -> writeNumber cell n
                                | TextValue s -> writeInlineString cell s
                                | DateValue d -> writeNumber cell (d.DateTime.ToOADate())
                                | ImageValue _ -> raise (RenderFailed(UnsupportedPlaceholderKind(name, key, "Image")))
                                | TableValue _ -> raise (RenderFailed(UnsupportedPlaceholderKind(name, key, "Table")))
                                | NarrativeValue _ ->
                                    raise (RenderFailed(UnsupportedPlaceholderKind(name, key, "Narrative")))

                        if not (List.isEmpty cellWrites) || not (List.isEmpty tokenValues) then
                            forceRecalculation workbookPart

                    return Ok(stream.ToArray())
                with
                | RenderFailed error -> return Error error
                | ex -> return Error(RendererFailure(name, $"template could not be processed as .xlsx: {ex.Message}"))
        }
    }