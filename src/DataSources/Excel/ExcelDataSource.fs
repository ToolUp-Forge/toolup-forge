// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Excel.ExcelDataSource

open System
open System.Globalization
open System.IO
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Tabular
open ToolUp.DataSources.Common
open DataManagementTypes

module CsvWire = ToolUp.DataSources.Common.Csv

// ─── ToolUp.DataSources.Excel ─────────────────────────────────────
//
// `IDataSource` companion for `.xlsx` / `.xlsm` workbooks — "the team
// drops a spreadsheet into a shared folder", which is an ingestion
// shape no warehouse connector covers.
//
// **Production-ready, and stateless between calls (portability rule
// 4).** Every method re-reads its settings, re-acquires the workbook
// through `IBlobStorage`, and closes it before returning. Nothing is
// cached: a workbook replaced between two runs is simply read again.
//
// **A "table" here is `<file>` or `<file>#<selector>`.** A workbook is
// not one table, so the connector's table names carry the selector
// with them. `ListTables` enumerates, for every workbook under the
// configured prefix, the workbook itself (its default sheet) plus one
// name per SHEET, per Excel **Table** (`ListObject`), and per **named
// range** — so both of the structures a spreadsheet author uses to say
// "this rectangle is the data" are addressable by the name they gave
// it, not by a cell range someone has to keep in step with the file.
//
// Selector resolution is deliberately ordered — Table, then named
// range, then sheet name, then `@index` — because a workbook may
// legitimately carry a Table and a sheet with the same name, and the
// Table is the narrower, more deliberate answer.
//
// **Cached values only; no formula evaluation, no Power Query
// refresh.** The connector reads what Excel wrote when the workbook
// was last saved. That is a property of the format rather than a
// limitation of this reader: an `.xlsx` stores each formula's cached
// result, and re-evaluating it would require a calculation engine this
// package deliberately does not carry. A workbook whose formulas have
// never been calculated reads as empty cells, and that is the honest
// answer.
//
// **The grid reader is `ToolUp.Tabular.Xlsx`** — shared strings,
// inline strings, number-format-aware date detection and the 1904
// date system are all fiddly, already solved there, and already
// contract-tested. This companion adds the workbook-level structure
// that a typed-row reader has no reason to expose: sheet, Table and
// defined-name enumeration, and range slicing.

/// Parsed, validated view of one Excel source's `ConnectionScope`.
type ExcelSourceSettings = {
    /// Container / prefix / extension / sample size, shared with the
    /// other file connectors.
    File: Files.FileSourceSettings
    /// Default selector applied when a table name carries none.
    /// `None` means "the first sheet in workbook order".
    DefaultSelector: string option
    /// Does the first row of the resolved range carry column names?
    HasHeader: bool
}

/// The `DataSourceConfig.Kind` this connector answers to.
[<Literal>]
let Kind = "Excel"

[<Literal>]
let private DefaultExtension = ".xlsx"

/// Separator between the workbook name and the in-workbook selector.
[<Literal>]
let SelectorSeparator = '#'

/// Read and validate one call's `ConnectionScope`. Pure.
let readSettings (scope: Map<string, string>) : Result<ExcelSourceSettings, IngestionError> =
    Files.readSettings DefaultExtension scope
    |> Result.bind (fun file ->
        ConnectionScope.optionalBool scope "has_header"
        |> Result.bind (fun hasHeader ->
            ConnectionScope.optionalInt scope "sheet_index"
            |> Result.bind (fun sheetIndex ->
                let sheetName = ConnectionScope.optional scope "sheet"

                match sheetName, sheetIndex with
                | Some _, Some _ ->
                    Error(
                        SchemaMismatch
                            "ConnectionScope carries both 'sheet' and 'sheet_index'; set one — they select the same thing two ways and a disagreement has no correct reading"
                    )
                | _, Some index when index < 0 ->
                    Error(SchemaMismatch $"ConnectionScope key 'sheet_index' must not be negative; got %d{index}")
                | _ ->
                    let defaultSelector =
                        match sheetName, sheetIndex with
                        | Some name, _ -> Some name
                        | None, Some index -> Some $"@%d{index}"
                        | None, None -> None

                    Ok {
                        File = file
                        DefaultSelector = defaultSelector
                        HasHeader = defaultArg hasHeader true
                    })))

/// Split a table name into its workbook part and optional selector.
let splitTableName (table: string) : string * string option =
    let value = if isNull table then "" else table

    match value.IndexOf SelectorSeparator with
    | -1 -> value, None
    | i ->
        let selector = value.Substring(i + 1)
        value.Substring(0, i), (if selector.Trim() = "" then None else Some(selector.Trim()))

/// Compose a table name from a workbook and a selector.
let composeTableName (workbook: string) (selector: string) : string =
    $"%s{workbook}%c{SelectorSeparator}%s{selector}"

// ─── A1 range references ──────────────────────────────────────────

/// A rectangle inside one sheet, in 0-based row / column coordinates.
/// `LastRow`/`LastColumn` are inclusive.
type CellRange = {
    /// Sheet the rectangle lives on. `None` when the reference
    /// carried no sheet qualifier and the caller supplies it (an
    /// Excel Table's `ref` is relative to its owning worksheet).
    Sheet: string option
    FirstRow: int
    LastRow: int
    FirstColumn: int
    LastColumn: int
}

/// Decode an `A1`-style column reference to a 0-based index.
let private columnIndexOf (reference: string) : int option = Xlsx.columnIndexOfReference reference

/// Decode the row number of an `A1`-style cell reference to a 0-based
/// index.
let private rowIndexOf (reference: string) : int option =
    let digits = String(reference |> Seq.filter Char.IsDigit |> Seq.toArray)

    match Int32.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture) with
    | true, value when value >= 1 -> Some(value - 1)
    | _ -> None

/// Parse an A1 range reference — `Sheet1!$A$1:$D$20`,
/// `'Sales Data'!A1:D20`, or a bare `A1:D20`. A single-cell reference
/// (`B7`) is a one-by-one rectangle.
///
/// Excel's `#REF!` and multi-area references (`A1:B2,D1:E2`) are
/// refused rather than partially honoured: reading the first area of a
/// multi-area name would produce a plausible table that silently omits
/// the rest.
let parseRange (reference: string) : Result<CellRange, string> =
    let raw = (if isNull reference then "" else reference).Trim()

    if raw = "" then
        Error "range reference is empty"
    elif raw.Contains ',' then
        Error $"multi-area reference '%s{raw}' is not supported — name a single rectangle"
    elif raw.Contains "#REF" then
        Error $"reference '%s{raw}' is broken (#REF!) in the workbook itself"
    else
        let sheet, cells =
            match raw.LastIndexOf '!' with
            | -1 -> None, raw
            | i ->
                let qualifier = raw.Substring(0, i).Trim()

                let unquoted =
                    if qualifier.StartsWith '\'' && qualifier.EndsWith '\'' && qualifier.Length >= 2 then
                        qualifier.Substring(1, qualifier.Length - 2).Replace("''", "'")
                    else
                        qualifier

                (if unquoted = "" then None else Some unquoted), raw.Substring(i + 1).Trim()

        let cells = cells.Replace("$", "")

        let corners =
            match cells.Split(':') with
            | [| single |] -> Some(single, single)
            | [| first; last |] -> Some(first, last)
            | _ -> None

        match corners with
        | None -> Error $"'%s{raw}' is not an A1 range reference"
        | Some(first, last) ->
            match columnIndexOf first, rowIndexOf first, columnIndexOf last, rowIndexOf last with
            | Some firstColumn, Some firstRow, Some lastColumn, Some lastRow ->
                Ok {
                    Sheet = sheet
                    FirstRow = min firstRow lastRow
                    LastRow = max firstRow lastRow
                    FirstColumn = min firstColumn lastColumn
                    LastColumn = max firstColumn lastColumn
                }
            | _ -> Error $"'%s{raw}' is not an A1 range reference"

// ─── Workbook structure ───────────────────────────────────────────

/// One addressable region inside a workbook, as `ListTables` reports
/// it and `Query` resolves it.
type WorkbookRegion = {
    /// Selector an operator writes after the `#`.
    Selector: string
    /// Sheet the region reads from.
    Sheet: string
    /// Rectangle within that sheet; `None` reads the whole sheet.
    Range: CellRange option
}

let private sheetsOf (workbookPart: WorkbookPart) : (string * string) list =
    match workbookPart.Workbook with
    | null -> []
    | workbook ->
        match workbook.Sheets with
        | null -> []
        | sheets ->
            sheets.Elements<Sheet>()
            |> Seq.choose (fun sheet ->
                match sheet.Name, sheet.Id with
                | null, _
                | _, null -> None
                | name, id when isNull name.Value || isNull id.Value -> None
                | name, id -> Some(name.Value, id.Value))
            |> List.ofSeq

/// Excel **Tables** (`ListObject`s), by the name the author gave them.
/// A Table's `ref` is relative to the worksheet that owns it, so the
/// sheet comes from the part rather than the reference.
let private tablesOf (workbookPart: WorkbookPart) (sheets: (string * string) list) : WorkbookRegion list =
    sheets
    |> List.collect (fun (sheetName, partId) ->
        match workbookPart.GetPartById partId with
        | :? WorksheetPart as worksheetPart ->
            worksheetPart.TableDefinitionParts
            |> Seq.choose (fun definitionPart ->
                match definitionPart.Table with
                | null -> None
                | table ->
                    let name =
                        match table.Name, table.DisplayName with
                        | null, null -> None
                        | name, _ when not (isNull name) && not (isNull name.Value) -> Some name.Value
                        | _, display when not (isNull display) && not (isNull display.Value) -> Some display.Value
                        | _ -> None

                    match name, table.Reference with
                    | Some name, reference when not (isNull reference) && not (isNull reference.Value) ->
                        match parseRange reference.Value with
                        | Ok range ->
                            Some {
                                Selector = name
                                Sheet = sheetName
                                Range = Some { range with Sheet = Some sheetName }
                            }
                        | Error _ -> None
                    | _ -> None)
            |> List.ofSeq
        | _ -> [])

/// Workbook-scoped **named ranges**. Sheet-local names are skipped:
/// two sheets may legitimately carry the same local name, so the
/// selector would be ambiguous, and a `ListTables` entry that
/// sometimes addresses a different rectangle is worse than one that is
/// absent.
let private namedRangesOf (workbookPart: WorkbookPart) : WorkbookRegion list =
    match workbookPart.Workbook with
    | null -> []
    | workbook ->
        match workbook.DefinedNames with
        | null -> []
        | names ->
            names.Elements<DefinedName>()
            |> Seq.choose (fun definedName ->
                if not (isNull definedName.LocalSheetId) then
                    None
                else
                    match definedName.Name with
                    | null -> None
                    | name when isNull name.Value -> None
                    | name ->
                        match parseRange definedName.Text with
                        | Ok range ->
                            match range.Sheet with
                            | Some sheet ->
                                Some {
                                    Selector = name.Value
                                    Sheet = sheet
                                    Range = Some range
                                }
                            | None -> None
                        | Error _ -> None)
            |> List.ofSeq

/// Every region a workbook exposes, in the order `ListTables` reports
/// them: Tables, then named ranges, then sheets.
let private regionsOf (workbookPart: WorkbookPart) : WorkbookRegion list =
    let sheets = sheetsOf workbookPart

    let sheetRegions =
        sheets
        |> List.map (fun (name, _) -> {
            Selector = name
            Sheet = name
            Range = None
        })

    tablesOf workbookPart sheets @ namedRangesOf workbookPart @ sheetRegions

/// Resolve a selector against a workbook. Order is Table → named
/// range → sheet name → `@index`; `None` takes the configured default
/// selector, else the first sheet.
let private resolveRegion
    (workbookPart: WorkbookPart)
    (defaultSelector: string option)
    (selector: string option)
    : Result<WorkbookRegion, IngestionError> =
    let sheets = sheetsOf workbookPart
    let regions = regionsOf workbookPart

    let available =
        regions |> List.map _.Selector |> List.distinct |> String.concat ", "

    let byIndex (raw: string) =
        match Int32.TryParse(raw.TrimStart('@'), NumberStyles.Integer, CultureInfo.InvariantCulture) with
        | true, index when index >= 0 && index < sheets.Length ->
            let name = fst sheets[index]

            Some {
                Selector = name
                Sheet = name
                Range = None
            }
        | _ -> None

    let resolve (raw: string) =
        match
            regions
            |> List.tryFind (fun region -> region.Selector.Equals(raw, StringComparison.OrdinalIgnoreCase))
        with
        | Some region -> Some region
        | None when raw.StartsWith '@' -> byIndex raw
        | None -> None

    match selector |> Option.orElse defaultSelector with
    | Some raw ->
        match resolve raw with
        | Some region -> Ok region
        | None ->
            Error(
                SchemaMismatch
                    $"selector '%s{raw}' matches no table, named range or sheet in the workbook; available: %s{available}"
            )
    | None ->
        match sheets with
        | [] -> Error(SchemaMismatch "workbook contains no sheets")
        | (name, _) :: _ ->
            Ok {
                Selector = name
                Sheet = name
                Range = None
            }

// ─── Grid reading ─────────────────────────────────────────────────

/// Render one cell as the text the CSV emitter and the type probe
/// both see. Invariant culture throughout — a connector running on a
/// comma-decimal host must not silently corrupt every number.
let renderCell (cell: Xlsx.XlsxCell) : string =
    match cell.Content with
    | Xlsx.CellContent.Text text -> if isNull text then "" else text
    | Xlsx.CellContent.Number value -> value.ToString("R", CultureInfo.InvariantCulture)
    | Xlsx.CellContent.Date value -> value.ToString("O", CultureInfo.InvariantCulture)
    | Xlsx.CellContent.Bool value -> if value then "true" else "false"
    // An error cell (`#N/A`, `#DIV/0!`) carries its token forward
    // rather than becoming a blank. A blank would read as a missing
    // value, and "missing" and "the spreadsheet's own formula failed"
    // are not the same fact about the data.
    | Xlsx.CellContent.CellError _ -> (if isNull cell.RawText then "" else cell.RawText)

/// Project one row's sparse cells onto a dense field list over the
/// column window. Sparse rows keep their true positions, so a blank
/// cell in the middle of a row stays a blank field rather than
/// shifting every value after it one column left.
let private denseRow (firstColumn: int) (lastColumn: int) (row: Xlsx.XlsxRow) : string list =
    let width = lastColumn - firstColumn + 1
    let cells = Array.create width ""

    for cell in row.Cells do
        if cell.ColumnIndex >= firstColumn && cell.ColumnIndex <= lastColumn then
            cells[cell.ColumnIndex - firstColumn] <- renderCell cell

    List.ofArray cells

/// Read the resolved region as a header row plus body rows.
///
/// `limit` bounds the BODY rows read — the type probe takes a sample,
/// `Query` takes everything.
let private readRegion
    (settings: ExcelSourceSettings)
    (region: WorkbookRegion)
    (bytes: byte[])
    (limit: int option)
    : Result<string list * string list list, IngestionError> =
    use stream = new MemoryStream(bytes, writable = false)

    let firstRow, lastRow, firstColumn, lastColumn =
        match region.Range with
        | Some range -> range.FirstRow, range.LastRow, range.FirstColumn, range.LastColumn
        | None -> 0, Int32.MaxValue, 0, Int32.MaxValue

    let mutable failure: string option = None
    let mutable header: string list option = None
    let rows = ResizeArray<string list>()
    let mutable go = true

    use enumerator =
        (Xlsx.readRows (SheetSelection.Name region.Sheet) stream).GetEnumerator()

    while go && enumerator.MoveNext() do
        match enumerator.Current with
        | Error message ->
            failure <- Some message
            go <- false
        | Ok row ->
            // `XlsxRow.RowIndex` is the sheet's own 1-based number.
            let index = row.RowIndex - 1

            if index >= firstRow && index <= lastRow then
                // An unbounded region has no declared right edge, so
                // the window is whatever the widest row reached.
                let effectiveLast =
                    if lastColumn = Int32.MaxValue then
                        row.Cells |> Array.fold (fun acc cell -> max acc cell.ColumnIndex) firstColumn
                    else
                        lastColumn

                let fields = denseRow firstColumn effectiveLast row

                match header with
                | None when settings.HasHeader -> header <- Some(fields |> List.map _.Trim())
                | None ->
                    header <- Some [ for i in 1 .. fields.Length -> $"column_%d{i}" ]
                    rows.Add fields
                | Some _ -> rows.Add fields

                match limit with
                | Some n when rows.Count >= n -> go <- false
                | _ -> ()

    match failure with
    | Some message -> Error(SchemaMismatch $"Excel read failed on sheet '%s{region.Sheet}': %s{message}")
    | None ->
        match header with
        | None -> Ok([], [])
        | Some header ->
            // Rows past the header may be wider than it when the
            // region is unbounded. Pad the header so no column is
            // silently dropped.
            let widest = rows |> Seq.fold (fun acc row -> max acc row.Length) header.Length

            let header =
                if widest <= header.Length then
                    header
                else
                    header @ [ for i in header.Length + 1 .. widest -> $"column_%d{i}" ]

            Ok(header, List.ofSeq rows)

/// Open a workbook's bytes and apply `body` to its `WorkbookPart`.
let private withWorkbook
    (context: string)
    (bytes: byte[])
    (body: WorkbookPart -> Result<'T, IngestionError>)
    : Result<'T, IngestionError> =
    try
        use stream = new MemoryStream(bytes, writable = false)
        use document = SpreadsheetDocument.Open(stream, false)

        match document.WorkbookPart with
        | null -> Error(SchemaMismatch $"%s{context}: the package carries no workbook part")
        | workbookPart -> body workbookPart
    with ex ->
        Error(SchemaMismatch $"%s{context}: not a readable XLSX/XLSM workbook: %s{ex.Message}")

type private ExcelDataSourceImpl(storage: IBlobStorage) =

    let withSettings
        (ctx: DataSourceCallContext)
        (context: string)
        (body: ExcelSourceSettings -> Async<Result<'T, IngestionError>>)
        : Async<Result<'T, IngestionError>> =
        Errors.guard context (fun () -> async {
            match readSettings ctx.Config.ConnectionScope with
            | Error err -> return Error err
            | Ok settings -> return! body settings
        })

    /// Acquire a workbook and resolve one `<file>#<selector>` name to
    /// its bytes plus the region it addresses.
    let resolveTable
        (context: string)
        (settings: ExcelSourceSettings)
        (table: string)
        : Async<Result<byte[] * WorkbookRegion, IngestionError>> =
        async {
            let workbook, selector = splitTableName table

            match! Files.download storage context settings.File workbook with
            | Error err -> return Error err
            | Ok bytes ->
                return
                    withWorkbook context bytes (fun workbookPart ->
                        resolveRegion workbookPart settings.DefaultSelector selector
                        |> Result.map (fun region -> bytes, region))
        }

    interface IDataSource with
        member _.Kind = Kind

        member _.Connect(ctx) =
            withSettings ctx "Excel Connect" (fun settings -> async {
                match! Files.listTables storage "Excel Connect" settings.File with
                | Error err -> return Error err
                | Ok [] ->
                    return
                        Error(
                            SourceUnreachable
                                $"Excel Connect: no '%s{settings.File.Extension}' workbooks under '%s{settings.File.Container}/%s{settings.File.Prefix}'"
                        )
                | Ok _ -> return Ok()
            })

        member _.ListTables(ctx) =
            withSettings ctx "Excel ListTables" (fun settings -> async {
                match! Files.listTables storage "Excel ListTables" settings.File with
                | Error err -> return Error err
                | Ok workbooks ->
                    // Every workbook is opened. That is the cost of
                    // reporting a name an operator can actually pass
                    // to `Query`; a listing of bare filenames would
                    // hide every Table and named range the file
                    // carries. The README says so, and `Tables` on
                    // the config constrains the set when a prefix is
                    // large.
                    let names = ResizeArray<string>()
                    let mutable failure: IngestionError option = None

                    for workbook in workbooks do
                        if failure.IsNone then
                            match! Files.download storage "Excel ListTables" settings.File workbook with
                            | Error err -> failure <- Some err
                            | Ok bytes ->
                                names.Add workbook

                                // A workbook that will not open is
                                // reported as its bare name rather
                                // than failing the whole listing —
                                // one unreadable file in a prefix
                                // must not hide the readable ones.
                                match withWorkbook "Excel ListTables" bytes (fun part -> Ok(regionsOf part)) with
                                | Error _ -> ()
                                | Ok regions ->
                                    for region in regions do
                                        names.Add(composeTableName workbook region.Selector)

                    match failure with
                    | Some err -> return Error err
                    | None -> return Ok(names |> List.ofSeq |> List.distinct)
            })

        member _.GetSchema(ctx, table) =
            withSettings ctx "Excel GetSchema" (fun settings -> async {
                match! resolveTable "Excel GetSchema" settings table with
                | Error err -> return Error err
                | Ok(bytes, region) ->
                    match readRegion settings region bytes (Some settings.File.SampleRows) with
                    | Error err -> return Error err
                    | Ok(header, rows) -> return Ok(TypeProbe.schemaOf table header rows)
            })

        member _.Query(ctx, sql) =
            withSettings ctx "Excel Query" (fun settings -> async {
                match! resolveTable "Excel Query" settings sql with
                | Error err -> return Error err
                | Ok(bytes, region) ->
                    match readRegion settings region bytes None with
                    | Error err -> return Error err
                    | Ok(header, rows) -> return Ok(CsvWire.toBytes header (rows |> Seq.map Seq.ofList))
            })

/// Build the connector over the deployment's blob storage.
///
/// `storage` is whatever the deployment composed — `LocalFileStorage`
/// for workbooks on disk, or any cloud companion. The connector never
/// touches `System.IO` itself, so the same `DataSourceConfig` works
/// against either without an edit.
let create (storage: IBlobStorage) : IDataSource =
    ExcelDataSourceImpl(storage) :> IDataSource