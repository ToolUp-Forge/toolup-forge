// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Emission: `WorkbookModel` → `.xlsx`.
///
/// The model is validated first (`WorkbookModel.validate`), then
/// lowered to SpreadsheetML: string cells pool into a shared-string
/// table, number-format codes become a styles part, column widths
/// become `cols`, merged ranges become `mergeCells`. The finished
/// package is normalised so **two emits of the same model are
/// byte-identical** — see `Package.normalise` for the two facts
/// (per-entry ZIP timestamps and a randomly-minted package-root
/// relationship id) that otherwise make that false.
///
/// The read side is `ToolUp.Tabular`'s `Xlsx` module: a workbook
/// emitted here reopens there with its values and its date-vs-number
/// distinction intact.
module ToolUp.OpenXml.Spreadsheet.Emit

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Spreadsheet

/// Excel reserves number-format ids below 164 for its built-in
/// formats, so every format code the model carries is allocated an id
/// from 164 upward — in first-appearance order, which is what makes
/// the styles part a deterministic function of the model.
[<Literal>]
let private FirstCustomNumberFormatId = 164

// ─── Model walks ─────────────────────────────────────────────────

let private allCells (model: WorkbookModel) : CellModel seq = seq {
    for sheet in model.Sheets do
        for row in sheet.Rows do
            yield! row.Cells
}

/// The number-format code a cell is emitted under. A `Date` cell with
/// no declared format takes the date default — a date serial under
/// Excel's General format renders, and reads back, as a bare number.
/// A `Blank` cell emits no cell element, so it contributes no format
/// however it was annotated.
let private effectiveNumberFormat (cell: CellModel) : string option =
    match cell.Content with
    | CellContent.Blank -> None
    | CellContent.Date _ -> cell.NumberFormat |> Option.orElse (Some SpreadsheetDefaults.dateFormat)
    | CellContent.Text _
    | CellContent.Number _
    | CellContent.Boolean _ -> cell.NumberFormat

/// Distinct string values in first-appearance order, plus the total
/// number of cells referencing one (the shared-string table's `count`
/// against its `uniqueCount`).
let private collectSharedStrings (model: WorkbookModel) : Dictionary<string, int> * string list * int =
    let index = Dictionary<string, int>(StringComparer.Ordinal)
    let ordered = ResizeArray<string>()
    let mutable references = 0

    for cell in allCells model do
        match cell.Content with
        | CellContent.Text text ->
            references <- references + 1

            if not (index.ContainsKey text) then
                index[text] <- ordered.Count
                ordered.Add text
        | CellContent.Number _
        | CellContent.Boolean _
        | CellContent.Date _
        | CellContent.Blank -> ()

    index, List.ofSeq ordered, references

/// Distinct number-format codes in first-appearance order.
let private collectNumberFormats (model: WorkbookModel) : string list =
    let seen = HashSet<string>(StringComparer.Ordinal)
    let ordered = ResizeArray<string>()

    for cell in allCells model do
        match effectiveNumberFormat cell with
        | Some code ->
            if seen.Add code then
                ordered.Add code
        | None -> ()

    List.ofSeq ordered

// ─── Styles part ─────────────────────────────────────────────────

/// The minimal valid stylesheet, plus one `cellXf` per number-format
/// code. Excel requires the fonts / fills / borders / cellStyleXfs /
/// cellStyles scaffolding even when nothing varies, and requires the
/// second fill to be `gray125`; a stylesheet missing any of it is
/// reported as a repairable-file error on open.
///
/// `cellXfs` index 0 is the unformatted default, so a cell's style
/// index is `1 + <position of its format code>`.
let private buildStylesheet (formatCodes: string list) : Stylesheet =
    let stylesheet = Stylesheet()

    if not formatCodes.IsEmpty then
        let numberingFormats =
            NumberingFormats(Count = UInt32Value(uint32 formatCodes.Length))

        formatCodes
        |> List.iteri (fun position code ->
            numberingFormats.AppendChild(
                NumberingFormat(
                    NumberFormatId = UInt32Value(uint32 (FirstCustomNumberFormatId + position)),
                    FormatCode = StringValue code
                )
            )
            |> ignore)

        stylesheet.AppendChild numberingFormats |> ignore

    let fonts = Fonts(Count = UInt32Value 1u)
    let font = Font()
    font.AppendChild(FontSize(Val = DoubleValue 11.0)) |> ignore
    font.AppendChild(FontName(Val = StringValue "Calibri")) |> ignore
    fonts.AppendChild font |> ignore
    stylesheet.AppendChild fonts |> ignore

    let fills = Fills(Count = UInt32Value 2u)
    let emptyFill = Fill()

    emptyFill.AppendChild(PatternFill(PatternType = EnumValue PatternValues.None))
    |> ignore

    fills.AppendChild emptyFill |> ignore
    let gray125Fill = Fill()

    gray125Fill.AppendChild(PatternFill(PatternType = EnumValue PatternValues.Gray125))
    |> ignore

    fills.AppendChild gray125Fill |> ignore
    stylesheet.AppendChild fills |> ignore

    let borders = Borders(Count = UInt32Value 1u)
    borders.AppendChild(Border()) |> ignore
    stylesheet.AppendChild borders |> ignore

    let cellStyleFormats = CellStyleFormats(Count = UInt32Value 1u)

    cellStyleFormats.AppendChild(
        CellFormat(
            NumberFormatId = UInt32Value 0u,
            FontId = UInt32Value 0u,
            FillId = UInt32Value 0u,
            BorderId = UInt32Value 0u
        )
    )
    |> ignore

    stylesheet.AppendChild cellStyleFormats |> ignore

    let cellFormats = CellFormats(Count = UInt32Value(uint32 formatCodes.Length + 1u))

    cellFormats.AppendChild(
        CellFormat(
            NumberFormatId = UInt32Value 0u,
            FontId = UInt32Value 0u,
            FillId = UInt32Value 0u,
            BorderId = UInt32Value 0u,
            FormatId = UInt32Value 0u
        )
    )
    |> ignore

    formatCodes
    |> List.iteri (fun position _ ->
        cellFormats.AppendChild(
            CellFormat(
                NumberFormatId = UInt32Value(uint32 (FirstCustomNumberFormatId + position)),
                FontId = UInt32Value 0u,
                FillId = UInt32Value 0u,
                BorderId = UInt32Value 0u,
                FormatId = UInt32Value 0u,
                ApplyNumberFormat = BooleanValue true
            )
        )
        |> ignore)

    stylesheet.AppendChild cellFormats |> ignore

    let cellStyles = CellStyles(Count = UInt32Value 1u)

    cellStyles.AppendChild(
        CellStyle(Name = StringValue "Normal", FormatId = UInt32Value 0u, BuiltinId = UInt32Value 0u)
    )
    |> ignore

    stylesheet.AppendChild cellStyles |> ignore
    stylesheet

// ─── Shared-string part ──────────────────────────────────────────

let private buildSharedStringTable (values: string list) (references: int) : SharedStringTable =
    let table =
        SharedStringTable(Count = UInt32Value(uint32 references), UniqueCount = UInt32Value(uint32 values.Length))

    for value in values do
        let item = SharedStringItem()

        item.AppendChild(Text(Text = value, Space = EnumValue SpaceProcessingModeValues.Preserve))
        |> ignore

        table.AppendChild item |> ignore

    table

// ─── Cell / row / worksheet emission ─────────────────────────────

/// Invariant, round-trippable rendering. .NET's default `double`
/// formatting is the shortest string that round-trips, which is both
/// exact and stable across runs — the two properties the emitted
/// bytes depend on.
let private numberText (value: float) : string =
    value.ToString(CultureInfo.InvariantCulture)

let private emitCell
    (sharedStrings: Dictionary<string, int>)
    (styleIndexOf: string option -> uint32 option)
    (rowIndex: int)
    (columnIndex: int)
    (cell: CellModel)
    : Cell option =
    let build (configure: Cell -> unit) =
        let element =
            Cell(CellReference = StringValue(WorkbookModel.cellReference rowIndex columnIndex))

        styleIndexOf (effectiveNumberFormat cell)
        |> Option.iter (fun styleIndex -> element.StyleIndex <- UInt32Value styleIndex)

        configure element
        Some element

    match cell.Content with
    | CellContent.Blank -> None
    | CellContent.Text text ->
        build (fun element ->
            element.DataType <- EnumValue CellValues.SharedString
            element.CellValue <- CellValue(string sharedStrings[text]))
    | CellContent.Number value ->
        // No `t` attribute: SpreadsheetML's default cell type is
        // number, and that is what Excel itself writes.
        build (fun element -> element.CellValue <- CellValue(numberText value))
    | CellContent.Boolean value ->
        build (fun element ->
            element.DataType <- EnumValue CellValues.Boolean
            element.CellValue <- CellValue(if value then "1" else "0"))
    | CellContent.Date value ->
        // Stored the way Excel stores dates — an OLE-automation serial
        // under a date-shaped number format, which is what makes the
        // reader classify it as a date rather than a number.
        build (fun element -> element.CellValue <- CellValue(numberText (value.ToOADate())))

let private buildWorksheet
    (sharedStrings: Dictionary<string, int>)
    (styleIndexOf: string option -> uint32 option)
    (sheet: SheetModel)
    : Worksheet =
    let worksheet = Worksheet()

    // Schema sequence: cols precedes sheetData, mergeCells follows it.
    if not sheet.ColumnWidths.IsEmpty then
        let columns = Columns()

        for width in sheet.ColumnWidths |> List.sortBy _.ColumnIndex do
            let reference = uint32 width.ColumnIndex + 1u

            columns.AppendChild(
                Column(
                    Min = UInt32Value reference,
                    Max = UInt32Value reference,
                    Width = DoubleValue width.Width,
                    CustomWidth = BooleanValue true
                )
            )
            |> ignore

        worksheet.AppendChild columns |> ignore

    let sheetData = SheetData()

    sheet.Rows
    |> List.iteri (fun rowIndex row ->
        let element = Row(RowIndex = UInt32Value(uint32 rowIndex + 1u))

        row.Cells
        |> List.iteri (fun columnIndex cell ->
            emitCell sharedStrings styleIndexOf rowIndex columnIndex cell
            |> Option.iter (fun emitted -> element.AppendChild emitted |> ignore))

        // A row of nothing but blanks carries no information; omitting
        // it is Excel's own behaviour and shifts nothing, since row
        // numbers come from the model's positions, not from the
        // emitted sequence.
        if element.HasChildren then
            sheetData.AppendChild element |> ignore)

    worksheet.AppendChild sheetData |> ignore

    if not sheet.MergedRanges.IsEmpty then
        let mergeCells = MergeCells(Count = UInt32Value(uint32 sheet.MergedRanges.Length))

        for range in sheet.MergedRanges do
            let reference =
                sprintf
                    "%s:%s"
                    (WorkbookModel.cellReference range.FirstRow range.FirstColumn)
                    (WorkbookModel.cellReference range.LastRow range.LastColumn)

            mergeCells.AppendChild(MergeCell(Reference = StringValue reference)) |> ignore

        worksheet.AppendChild mergeCells |> ignore

    worksheet

// ─── Package assembly ────────────────────────────────────────────

/// Write the workbook's parts onto the stream. Relationship ids are
/// assigned here, in model order — worksheets first, then the
/// shared-string and styles parts — so the emitted `.rels` XML is a
/// function of the model alone.
let private buildPackage (model: WorkbookModel) (stream: Stream) : unit =
    use doc = Package.create stream
    let workbook = Package.workbookPart doc

    let sharedStrings, sharedStringValues, sharedStringReferences =
        collectSharedStrings model

    let formatCodes = collectNumberFormats model

    let formatPositions = Dictionary<string, int>(StringComparer.Ordinal)

    formatCodes
    |> List.iteri (fun position code -> formatPositions[code] <- position)

    let styleIndexOf (format: string option) : uint32 option =
        match format with
        | None -> None
        | Some code ->
            match formatPositions.TryGetValue code with
            | true, position -> Some(uint32 position + 1u)
            | false, _ -> None

    let sheets = Sheets()

    model.Sheets
    |> List.iteri (fun position sheet ->
        let relationshipId = sprintf "rId%d" (position + 1)
        let part = Package.addWorksheetPart workbook relationshipId
        part.Worksheet <- buildWorksheet sharedStrings styleIndexOf sheet

        sheets.AppendChild(
            Sheet(
                Name = StringValue sheet.Name,
                SheetId = UInt32Value(uint32 position + 1u),
                Id = StringValue relationshipId
            )
        )
        |> ignore)

    // Both parts are always written, even when empty: a fixed part set
    // keeps the relationship-id sequence gapless and the emitted shape
    // uniform across models.
    let sharedStringPart =
        Package.addSharedStringTablePart workbook (sprintf "rId%d" (model.Sheets.Length + 1))

    sharedStringPart.SharedStringTable <- buildSharedStringTable sharedStringValues sharedStringReferences

    let stylesPart =
        Package.addStylesPart workbook (sprintf "rId%d" (model.Sheets.Length + 2))

    stylesPart.Stylesheet <- buildStylesheet formatCodes

    workbook.Workbook.AppendChild sheets |> ignore
    workbook.Workbook.Save()

// ─── Entry points ────────────────────────────────────────────────

/// Emit the model as `.xlsx` bytes, or every reason it could not be
/// emitted. This is the recoverable form — a sheet name Excel refuses
/// comes back as data, never as a truncated name in a file that opens.
let tryToBytes (model: WorkbookModel) : Result<byte[], WorkbookError list> =
    WorkbookModel.validate model
    |> Result.map (fun valid ->
        use stream = new MemoryStream()
        buildPackage valid stream
        Package.normalise (stream.ToArray()))

/// Emit the model as `.xlsx` bytes. Two emits of the same model are
/// byte-identical.
///
/// Raises `InvalidOperationException` naming every validation failure
/// when the model cannot be emitted; reach for `tryToBytes` where that
/// is a recoverable condition rather than a programming error.
let toBytes (model: WorkbookModel) : byte[] =
    match tryToBytes model with
    | Ok bytes -> bytes
    | Error errors ->
        let detail = errors |> List.map (fun error -> "  - " + WorkbookError.describe error)

        raise (
            InvalidOperationException(
                String.Join(Environment.NewLine, "The workbook model cannot be emitted:" :: detail)
            )
        )

/// Emit the model as an `.xlsx` package onto the stream, or every
/// reason it could not be emitted. The stream is written from its
/// current position and left open; the caller owns it.
let tryToStream (model: WorkbookModel) (stream: Stream) : Result<unit, WorkbookError list> =
    tryToBytes model
    |> Result.map (fun bytes -> stream.Write(bytes, 0, bytes.Length))

/// Emit the model as an `.xlsx` package onto the stream. The stream is
/// written from its current position and left open; the caller owns
/// it. Raises on an invalid model — see `toBytes`.
let toStream (model: WorkbookModel) (stream: Stream) : unit =
    let bytes = toBytes model
    stream.Write(bytes, 0, bytes.Length)