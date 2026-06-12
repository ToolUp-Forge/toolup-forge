// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Tabular

open System
open System.IO
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet

// ─── Phase 123 — XLSX leg (DocumentFormat.OpenXml) ──────────────
//
// Row-streaming reads over `OpenXmlReader` — one `Row` element is
// materialised at a time, never the worksheet DOM, so a 100k-row
// sheet costs one row of memory (plus the shared-string table,
// which is a workbook-level lookup and is necessarily resident —
// see the README memory contract). Shared strings, inline
// strings, and number-format-aware cell reads are all handled
// here; date-vs-number disambiguation uses the cell's style
// (numFmtId), since real-world XLSX stores dates as styled
// numeric serials, not as typed date cells.
//
// The vendor dependency stays in this companion (GP 1) — nothing
// in ToolUp.Platform.* references OpenXml.

/// Low-level XLSX row reader. Most consumers want
/// `TabularReader.readXlsx` (schema validation + typed binding);
/// this module is public for consumers that need raw rows.
module Xlsx =

    /// What a cell carried, with enough provenance for typed
    /// validation: text stays text, numbers keep their raw double
    /// plus whether the cell's number format is date-shaped.
    [<RequireQualifiedAccess>]
    type CellContent =
        /// Shared-string / inline-string / formula-string cell.
        | Text of string
        /// Numeric cell whose style is not date-shaped.
        | Number of value: float
        /// Date cell — either a numeric serial under a date-shaped
        /// numFmtId (built-in 14–22 / 27–36 / 45–47 / 50–58 /
        /// 71–81, or a custom format containing date tokens) or a
        /// typed ISO-8601 date cell. Serial conversion happens
        /// here because the workbook's date system (1900 vs 1904)
        /// is only known at the workbook level.
        | Date of DateTime
        /// Boolean cell.
        | Bool of bool
        /// Error cell (`#DIV/0!`, `#N/A`, …) — carried as data so
        /// the validation report can name it; never thrown.
        | CellError of message: string

    /// One cell: 0-based column position (decoded from the cell
    /// reference, so sparse rows keep their true positions), the
    /// raw stored text (for error reports), and the typed content.
    type XlsxCell = {
        ColumnIndex: int
        RawText: string
        Content: CellContent
    }

    /// One row: the sheet's own 1-based row number (gaps in the
    /// sheet keep their numbering) and the cells present.
    type XlsxRow = { RowIndex: int; Cells: XlsxCell[] }

    /// Decode the column letters of an `A1`-style cell reference
    /// to a 0-based column index (`A`→0, `Z`→25, `AA`→26).
    let columnIndexOfReference (reference: string) : int option =
        let mutable acc = 0
        let mutable sawLetter = false
        let mutable i = 0
        let mutable bad = false

        while not bad && i < reference.Length && Char.IsLetter reference[i] do
            let c = Char.ToUpperInvariant reference[i]

            if c < 'A' || c > 'Z' then
                bad <- true
            else
                acc <- acc * 26 + (int c - int 'A' + 1)
                sawLetter <- true
                i <- i + 1

        if bad || not sawLetter then None else Some(acc - 1)

    /// Built-in numFmtIds Excel renders as dates / times.
    let private builtInDateFormatIds =
        Set.ofList [
            yield! [ 14..22 ] // date / date-time
            yield! [ 27..36 ] // East-Asian calendar dates
            yield! [ 45..47 ] // elapsed time
            yield! [ 50..58 ] // East-Asian calendar dates
            yield! [ 71..81 ] // Thai calendar dates
        ]

    /// Heuristic for custom format codes: date-shaped when the
    /// code contains y/m/d/h/s tokens outside quoted literals,
    /// `[...]` colour/locale sections, and `\`-escapes. (`m` is
    /// both month and minute — either way it's a date/time
    /// format, which is all the validator needs to know.)
    let private isDateFormatCode (code: string) =
        let mutable inQuote = false
        let mutable inBracket = false
        let mutable escaped = false
        let mutable isDate = false

        for c in code do
            if escaped then
                escaped <- false
            elif inQuote then
                if c = '"' then
                    inQuote <- false
            elif inBracket then
                if c = ']' then
                    inBracket <- false
            else
                match c with
                | '"' -> inQuote <- true
                | '[' -> inBracket <- true
                | '\\' -> escaped <- true
                | 'y'
                | 'Y'
                | 'm'
                | 'M'
                | 'd'
                | 'D'
                | 'h'
                | 'H'
                | 's'
                | 'S' -> isDate <- true
                | _ -> ()

        isDate

    /// Build the styleIndex → is-date-format lookup from the
    /// workbook's stylesheet. Returns a total function (absent
    /// stylesheet / out-of-range indexes → not a date).
    let private buildDateStyleLookup (workbookPart: WorkbookPart) : uint32 -> bool =
        match workbookPart.WorkbookStylesPart with
        | null -> fun _ -> false
        | stylesPart ->
            match stylesPart.Stylesheet with
            | null -> fun _ -> false
            | stylesheet ->
                let customDateFormatIds =
                    match stylesheet.NumberingFormats with
                    | null -> Set.empty
                    | formats ->
                        formats.Elements<NumberingFormat>()
                        |> Seq.choose (fun f ->
                            match f.NumberFormatId, f.FormatCode with
                            | null, _
                            | _, null -> None
                            | id, code when code.Value <> null && isDateFormatCode code.Value -> Some(uint32 id.Value)
                            | _ -> None)
                        |> Set.ofSeq

                let formatIdByStyleIndex =
                    match stylesheet.CellFormats with
                    | null -> [||]
                    | cellFormats ->
                        cellFormats.Elements<CellFormat>()
                        |> Seq.map (fun cf ->
                            match cf.NumberFormatId with
                            | null -> 0u
                            | id -> uint32 id.Value)
                        |> Seq.toArray

                fun styleIndex ->
                    if int styleIndex >= formatIdByStyleIndex.Length then
                        false
                    else
                        let formatId = formatIdByStyleIndex[int styleIndex]

                        builtInDateFormatIds.Contains(int formatId)
                        || customDateFormatIds.Contains formatId

    /// Materialise the shared-string table into an array lookup.
    /// This is the one deliberately-resident structure of the
    /// XLSX leg (documented in the README memory contract): it is
    /// a workbook-level dictionary every row may reference, so
    /// row-streaming cannot avoid it.
    let private loadSharedStrings (workbookPart: WorkbookPart) : string[] =
        match workbookPart.SharedStringTablePart with
        | null -> [||]
        | part ->
            match part.SharedStringTable with
            | null -> [||]
            | table -> table.Elements<SharedStringItem>() |> Seq.map _.InnerText |> Seq.toArray

    /// Convert a date serial to a `DateTime` under the workbook's
    /// date system. `None` when the serial is outside the
    /// convertible range (the cell then reports as an invalid
    /// date against the column type rather than throwing).
    let private dateOfSerial (date1904: bool) (serial: float) : DateTime option =
        try
            if date1904 then
                Some(DateTime(1904, 1, 1).AddDays serial)
            else
                Some(DateTime.FromOADate serial)
        with _ ->
            None

    let private convertCell
        (sharedStrings: string[])
        (isDateStyle: uint32 -> bool)
        (date1904: bool)
        (cell: Cell)
        : XlsxCell option =
        let columnIndex =
            match cell.CellReference with
            | null -> None
            | reference when isNull reference.Value -> None
            | reference -> columnIndexOfReference reference.Value

        match columnIndex with
        | None -> None
        | Some columnIndex ->
            let rawText =
                match cell.CellValue with
                | null -> ""
                | value -> value.Text

            let dataType =
                match cell.DataType with
                | null -> CellValues.Number
                | dataType -> dataType.Value

            let content =
                if dataType = CellValues.SharedString then
                    match Int32.TryParse rawText with
                    | true, i when i >= 0 && i < sharedStrings.Length -> CellContent.Text sharedStrings[i]
                    | _ -> CellContent.Text ""
                elif dataType = CellValues.InlineString then
                    let text =
                        match cell.InlineString with
                        | null -> ""
                        | inline' -> inline'.InnerText

                    CellContent.Text text
                elif dataType = CellValues.String then
                    CellContent.Text rawText
                elif dataType = CellValues.Boolean then
                    CellContent.Bool(rawText = "1" || rawText.Equals("true", StringComparison.OrdinalIgnoreCase))
                elif dataType = CellValues.Date then
                    // Rare typed-date cell (ISO 8601 text).
                    match
                        DateTime.TryParse(
                            rawText,
                            Globalization.CultureInfo.InvariantCulture,
                            Globalization.DateTimeStyles.None
                        )
                    with
                    | true, parsed -> CellContent.Date parsed
                    | false, _ -> CellContent.Text rawText
                elif dataType = CellValues.Error then
                    CellContent.CellError rawText
                else
                    // CellValues.Number — the default when no
                    // DataType attribute is present.
                    match Double.TryParse(rawText, Globalization.CultureInfo.InvariantCulture) with
                    | true, value ->
                        let dateStyle =
                            match cell.StyleIndex with
                            | null -> false
                            | styleIndex -> isDateStyle (uint32 styleIndex.Value)

                        if dateStyle then
                            match dateOfSerial date1904 value with
                            | Some date -> CellContent.Date date
                            | None -> CellContent.Number value
                        else
                            CellContent.Number value
                    | false, _ ->
                        // Numeric cell whose stored text isn't a
                        // number — corrupt; surface as text so the
                        // validator reports it against the column
                        // type instead of dropping the cell.
                        CellContent.Text rawText

            let resolvedRawText =
                match content with
                | CellContent.Text text -> text
                | _ -> rawText

            Some {
                ColumnIndex = columnIndex
                RawText = resolvedRawText
                Content = content
            }

    /// Resolve a `SheetSelection` to its `WorksheetPart`.
    let private resolveSheet (workbookPart: WorkbookPart) (selection: SheetSelection) : Result<WorksheetPart, string> =
        let sheets =
            match workbookPart.Workbook with
            | null -> []
            | workbook ->
                match workbook.Sheets with
                | null -> []
                | sheets -> sheets.Elements<Sheet>() |> List.ofSeq

        let chosen =
            match selection with
            | SheetSelection.Index i -> if i >= 0 && i < sheets.Length then Some sheets[i] else None
            | SheetSelection.Name name ->
                sheets
                |> List.tryFind (fun s ->
                    match s.Name with
                    | null -> false
                    | sheetName ->
                        not (isNull sheetName.Value)
                        && sheetName.Value.Equals(name, StringComparison.OrdinalIgnoreCase))

        match chosen with
        | None ->
            let available =
                sheets
                |> List.choose (fun s ->
                    match s.Name with
                    | null -> None
                    | name -> Option.ofObj name.Value)
                |> String.concat ", "

            let requested =
                match selection with
                | SheetSelection.Index i -> sprintf "index %d" i
                | SheetSelection.Name name -> sprintf "name '%s'" name

            Error(sprintf "sheet not found (requested %s; workbook has: %s)" requested available)
        | Some sheet ->
            match sheet.Id with
            | null -> Error "sheet has no relationship id — workbook is malformed"
            | id when isNull id.Value -> Error "sheet has no relationship id — workbook is malformed"
            | id ->
                match workbookPart.GetPartById id.Value with
                | :? WorksheetPart as worksheetPart -> Ok worksheetPart
                | _ -> Error "sheet relationship does not resolve to a worksheet part"

    [<RequireQualifiedAccess>]
    type private Step =
        | Finished
        | Skip
        | RowRead of XlsxRow

    /// Stream the selected sheet's rows. Lazy and row-at-a-time:
    /// the workbook is opened when enumeration starts and closed
    /// when it completes (or is disposed early); only one `Row`
    /// element is held at a time. A fatal problem — not a
    /// workbook, sheet missing, corrupt part mid-read — yields a
    /// single `Error message` and ends the sequence; data-shaped
    /// problems never throw (Phase 123 acceptance criterion).
    ///
    /// The 1904 date system (`workbookPr date1904`) is honoured:
    /// serials from such workbooks are shifted so the same
    /// wall-clock date round-trips. Serials below 61 in the 1900
    /// system inherit the historical Excel leap-year-1900
    /// off-by-one; dates from 1900-03-01 onward are exact.
    let readRows (selection: SheetSelection) (stream: Stream) : seq<Result<XlsxRow, string>> = seq {
        let opened =
            try
                Ok(SpreadsheetDocument.Open(stream, false))
            with ex ->
                Error(sprintf "not a readable XLSX workbook: %s" ex.Message)

        match opened with
        | Error message -> yield Error message
        | Ok document ->
            use document = document

            match document.WorkbookPart with
            | null -> yield Error "not a readable XLSX workbook: no workbook part"
            | workbookPart ->
                match resolveSheet workbookPart selection with
                | Error message -> yield Error message
                | Ok worksheetPart ->
                    let sharedStrings = loadSharedStrings workbookPart
                    let isDateStyle = buildDateStyleLookup workbookPart

                    let date1904 =
                        match workbookPart.Workbook with
                        | null -> false
                        | workbook ->
                            match workbook.WorkbookProperties with
                            | null -> false
                            | properties ->
                                match properties.Date1904 with
                                | null -> false
                                | date1904 -> date1904.Value

                    use reader = OpenXmlReader.Create worksheetPart
                    let mutable rowCounter = 0
                    let mutable go = true

                    while go do
                        // F# cannot yield inside try/with, so
                        // each step is computed under the guard
                        // and yielded outside it.
                        let step =
                            try
                                if not (reader.Read()) then
                                    Ok Step.Finished
                                elif reader.ElementType = typeof<Row> && reader.IsStartElement then
                                    let row = reader.LoadCurrentElement() :?> Row
                                    rowCounter <- rowCounter + 1

                                    let rowIndex =
                                        match row.RowIndex with
                                        | null -> rowCounter
                                        | index -> int index.Value

                                    let cells =
                                        row.Elements<Cell>()
                                        |> Seq.choose (convertCell sharedStrings isDateStyle date1904)
                                        |> Seq.toArray

                                    rowCounter <- max rowCounter rowIndex
                                    Ok(Step.RowRead { RowIndex = rowIndex; Cells = cells })
                                else
                                    Ok Step.Skip
                            with ex ->
                                Error(sprintf "failed reading worksheet row: %s" ex.Message)

                        match step with
                        | Ok Step.Finished -> go <- false
                        | Ok Step.Skip -> ()
                        | Ok(Step.RowRead row) -> yield Ok row
                        | Error message ->
                            yield Error message
                            go <- false
    }