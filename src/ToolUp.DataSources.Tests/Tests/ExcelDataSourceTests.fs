// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.ExcelDataSourceTests

open System.IO
open System.Text
open DocumentFormat.OpenXml
open DocumentFormat.OpenXml.Packaging
open DocumentFormat.OpenXml.Spreadsheet
open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Excel
open ToolUp.DataSources.Tests.Support

// ─── ToolUp.DataSources.Excel ─────────────────────────────────────
//
// Always-on. The fixtures are real `.xlsx` packages BUILT HERE with
// the same OpenXml SDK the connector reads through, then written into
// an in-process `IBlobStorage` — so the pack needs no committed binary
// fixture, no filesystem and no cleanup, and a fixture can never drift
// out of step with the assertion that reads it.

let private container (sourceId: string) = $"team-%s{sourceId}"

[<Literal>]
let private Prefix = "inbox/"

let private context (scope: (string * string) list) (sourceId: string) : DataSourceCallContext =
    TestFakes.config sourceId ExcelDataSource.Kind ([ "container", container sourceId; "prefix", Prefix ] @ scope)
    |> TestFakes.context "test-scope" None

// ─── Fixture workbooks ────────────────────────────────────────────

/// `A`, `B`, … `Z`, `AA`, … for a 0-based column index.
let private columnName (index: int) =
    let builder = StringBuilder()
    let mutable remaining = index
    let mutable go = true

    while go do
        builder.Insert(0, char (int 'A' + remaining % 26)) |> ignore
        remaining <- remaining / 26 - 1
        go <- remaining >= 0

    builder.ToString()

/// A cell carrying `text` with NO `t` attribute — the default, which
/// the reader interprets as a number when the text parses as one and
/// as text otherwise. That is exactly how a real export stores a mixed
/// grid, so the fixture exercises the reader's own disambiguation
/// rather than side-stepping it.
let private cellOf (reference: string) (text: string) =
    let cell = Cell()
    cell.CellReference <- StringValue reference
    cell.CellValue <- CellValue(text: string)
    cell

let private sheetDataOf (grid: string list list) =
    let sheetData = SheetData()

    grid
    |> List.iteri (fun rowIndex row ->
        let element = Row()
        element.RowIndex <- UInt32Value(uint32 (rowIndex + 1))

        row
        |> List.iteri (fun columnIndex value ->
            element.AppendChild(cellOf $"%s{columnName columnIndex}%d{rowIndex + 1}" value)
            |> ignore)

        sheetData.AppendChild element |> ignore)

    sheetData

/// Write the OPC package into `stream`. Split out so the document's
/// `use` scope ends before the caller reads the bytes back.
let private writePackage
    (stream: Stream)
    (sheets: (string * string list list) list)
    (tables: (string * string * string) list)
    (names: (string * string) list)
    =
    use document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook)
    let workbookPart = document.AddWorkbookPart()
    workbookPart.Workbook <- Workbook()
    let sheetElements = workbookPart.Workbook.AppendChild(Sheets())

    sheets
    |> List.iteri (fun index (name, grid) ->
        let worksheetPart = workbookPart.AddNewPart<WorksheetPart>()
        // `AppendChild`, never `Append` / the `IEnumerable` ctor: an
        // OpenXmlElement IS an `IEnumerable<OpenXmlElement>` over its own
        // children, so the collection overloads splice a built element's
        // CHILDREN in and then throw "already part of a tree".
        let worksheet = Worksheet()
        worksheet.AppendChild(sheetDataOf grid) |> ignore
        worksheetPart.Worksheet <- worksheet

        for sheet, tableName, reference in tables do
            if sheet = name then
                let tablePart = worksheetPart.AddNewPart<TableDefinitionPart>()
                let table = Table()
                table.Id <- UInt32Value(uint32 (index + 1))
                table.Name <- StringValue tableName
                table.DisplayName <- StringValue tableName
                table.Reference <- StringValue reference
                tablePart.Table <- table

        let sheetElement = Sheet()
        sheetElement.Id <- StringValue(workbookPart.GetIdOfPart worksheetPart)
        sheetElement.SheetId <- UInt32Value(uint32 (index + 1))
        sheetElement.Name <- StringValue name
        sheetElements.AppendChild sheetElement |> ignore)

    if not (List.isEmpty names) then
        let definedNames = DefinedNames()

        for name, reference in names do
            let definedName = DefinedName()
            definedName.Name <- StringValue name
            definedName.Text <- reference
            definedNames.AppendChild definedName |> ignore

        workbookPart.Workbook.AppendChild definedNames |> ignore

    workbookPart.Workbook.Save()

/// Build a workbook. Each entry of `sheets` is a tab name and its
/// grid; `tables` names an Excel Table (`sheet`, `name`, `A1 ref`) and
/// `names` a workbook-scoped defined name (`name`, `Sheet!$A$1:$C$3`).
let buildWorkbook
    (sheets: (string * string list list) list)
    (tables: (string * string * string) list)
    (names: (string * string) list)
    : byte[] =
    use stream = new MemoryStream()
    writePackage stream sheets tables names
    stream.ToArray()

let private target (scope: (string * string) list) (sheetName: string) () =
    let storage = FakeBlobStorage.InMemoryBlobStorage()

    {
        LocalFileDataSourceContract.Source = ExcelDataSource.create storage
        LocalFileDataSourceContract.Seed =
            fun sourceId table header rows ->
                storage.Put(
                    container sourceId,
                    $"%s{Prefix}%s{table}.xlsx",
                    buildWorkbook [ sheetName, (header :: rows) ] [] []
                )
        LocalFileDataSourceContract.Context = context scope
        LocalFileDataSourceContract.Address = id
    }

/// The Phase 10d worked-example workbook: two sheets, one Excel Table
/// over a sub-rectangle of the second, and one named range.
let private richWorkbook () =
    buildWorkbook
        [
            "Summary", [ [ "note" ]; [ "not the data" ] ]
            "Sales",
            [
                [ "region"; "units"; "active" ]
                [ "north"; "12"; "true" ]
                [ "south"; "7"; "false" ]
            ]
        ] [ "Sales", "SalesTable", "A1:C3" ] [ "SalesRange", "Sales!$A$1:$C$2" ]

let private richSource () =
    let storage = FakeBlobStorage.InMemoryBlobStorage()
    storage.Put(container "src", $"%s{Prefix}book.xlsx", richWorkbook ())
    ExcelDataSource.create storage

let tests =
    testList "ExcelDataSource" [

        // The contract, twice: on the default (first-sheet) selector,
        // and against a workbook whose data sheet is NOT first, so a
        // connector that ignored `sheet` would pass the first run and
        // fail the second.
        LocalFileDataSourceContract.tests "Excel (first sheet)" (target [] "Data")
        LocalFileDataSourceContract.tests "Excel (named sheet)" (target [ "sheet", "Figures" ] "Figures")

        testList "readSettings" [
            test "container is required" {
                match ExcelDataSource.readSettings Map.empty with
                | Error(SchemaMismatch message) -> Expect.stringContains message "container" "names the missing key"
                | other -> failtestf "Expected SchemaMismatch naming 'container'; got %A" other
            }

            test "sheet and sheet_index together are refused" {
                match
                    ExcelDataSource.readSettings (Map.ofList [ "container", "c"; "sheet", "Sales"; "sheet_index", "1" ])
                with
                | Error(SchemaMismatch message) -> Expect.stringContains message "sheet_index" "names both keys"
                | other -> failtestf "Expected SchemaMismatch; got %A" other
            }

            test "a negative sheet_index is refused" {
                match ExcelDataSource.readSettings (Map.ofList [ "container", "c"; "sheet_index", "-1" ]) with
                | Error(SchemaMismatch message) -> Expect.stringContains message "sheet_index" "names the key"
                | other -> failtestf "Expected SchemaMismatch; got %A" other
            }

            test "the default extension is .xlsx and is overridable" {
                match ExcelDataSource.readSettings (Map.ofList [ "container", "c" ]) with
                | Ok settings -> Expect.equal settings.File.Extension ".xlsx" "default"
                | Error err -> failtestf "readSettings failed: %A" err

                match ExcelDataSource.readSettings (Map.ofList [ "container", "c"; "extension", "xlsm" ]) with
                | Ok settings -> Expect.equal settings.File.Extension ".xlsm" "a bare suffix is dot-prefixed"
                | Error err -> failtestf "readSettings failed: %A" err
            }
        ]

        testList "A1 range references" [
            test "a sheet-qualified absolute reference parses" {
                match ExcelDataSource.parseRange "Sales!$B$2:$D$10" with
                | Ok range ->
                    Expect.equal range.Sheet (Some "Sales") "sheet"
                    Expect.equal range.FirstColumn 1 "B is column 1"
                    Expect.equal range.FirstRow 1 "row 2 is index 1"
                    Expect.equal range.LastColumn 3 "D is column 3"
                    Expect.equal range.LastRow 9 "row 10 is index 9"
                | Error message -> failtestf "parseRange failed: %s" message
            }

            test "a quoted sheet name with spaces parses" {
                match ExcelDataSource.parseRange "'Sales Data'!A1:B2" with
                | Ok range -> Expect.equal range.Sheet (Some "Sales Data") "sheet"
                | Error message -> failtestf "parseRange failed: %s" message
            }

            test "a single cell is a one-by-one rectangle" {
                match ExcelDataSource.parseRange "B7" with
                | Ok range ->
                    Expect.equal (range.FirstRow, range.LastRow) (6, 6) "rows"
                    Expect.equal (range.FirstColumn, range.LastColumn) (1, 1) "columns"
                | Error message -> failtestf "parseRange failed: %s" message
            }

            test "a multi-area reference is refused, not half-honoured" {
                // Reading the first area would produce a plausible table
                // that silently omits the rest.
                match ExcelDataSource.parseRange "A1:B2,D1:E2" with
                | Error message -> Expect.stringContains message "multi-area" "says why"
                | Ok range -> failtestf "Expected a refusal; got %A" range
            }

            test "a broken #REF! reference is refused" {
                match ExcelDataSource.parseRange "#REF!$A$1" with
                | Error message -> Expect.stringContains message "REF" "names the defect"
                | Ok range -> failtestf "Expected a refusal; got %A" range
            }
        ]

        testList "workbook structure" [
            testCaseAsync "ListTables reports the workbook, its sheets, its Tables and its named ranges"
            <| async {
                match! (richSource ()).ListTables(context [] "src") with
                | Ok tables ->
                    let expected = [ "book"; "book#Summary"; "book#Sales"; "book#SalesTable"; "book#SalesRange" ]

                    for name in expected do
                        Expect.contains tables name $"%s{name} is addressable"
                | Error err -> failtestf "ListTables failed: %A" err
            }

            testCaseAsync "an Excel Table is addressed by the name its author gave it"
            <| async {
                match! (richSource ()).Query(context [] "src", "book#SalesTable") with
                | Ok bytes ->
                    let parsed = LocalFileDataSourceContract.parseCsv bytes
                    Expect.sequenceEqual (List.head parsed) [ "region"; "units"; "active" ] "header"
                    Expect.equal (List.length parsed) 3 "the Table's three rows, header included"
                | Error err -> failtestf "Query failed: %A" err
            }

            testCaseAsync "a named range is clipped to its declared rectangle"
            <| async {
                // `SalesRange` is `Sales!$A$1:$C$2` — the header plus ONE
                // data row, where the sheet carries two. A connector that
                // resolved the name to its sheet and ignored the
                // rectangle would return both and still look right.
                match! (richSource ()).Query(context [] "src", "book#SalesRange") with
                | Ok bytes ->
                    let parsed = LocalFileDataSourceContract.parseCsv bytes
                    Expect.equal (List.length parsed) 2 "header plus exactly one data row"
                    Expect.sequenceEqual (List.item 1 parsed) [ "north"; "12"; "true" ] "the first data row"
                | Error err -> failtestf "Query failed: %A" err
            }

            testCaseAsync "a bare workbook name reads the first sheet"
            <| async {
                match! (richSource ()).Query(context [] "src", "book") with
                | Ok bytes ->
                    let parsed = LocalFileDataSourceContract.parseCsv bytes
                    Expect.sequenceEqual (List.head parsed) [ "note" ] "Summary is the first sheet"
                | Error err -> failtestf "Query failed: %A" err
            }

            testCaseAsync "the configured default sheet applies when a name carries no selector"
            <| async {
                match! (richSource ()).Query(context [ "sheet", "Sales" ] "src", "book") with
                | Ok bytes ->
                    let parsed = LocalFileDataSourceContract.parseCsv bytes
                    Expect.sequenceEqual (List.head parsed) [ "region"; "units"; "active" ] "Sales, not Summary"
                | Error err -> failtestf "Query failed: %A" err
            }

            testCaseAsync "sheet_index selects positionally"
            <| async {
                match! (richSource ()).Query(context [ "sheet_index", "1" ] "src", "book") with
                | Ok bytes ->
                    let parsed = LocalFileDataSourceContract.parseCsv bytes
                    Expect.sequenceEqual (List.head parsed) [ "region"; "units"; "active" ] "the second sheet"
                | Error err -> failtestf "Query failed: %A" err
            }

            testCaseAsync "an unknown selector names what the workbook does carry"
            <| async {
                match! (richSource ()).Query(context [] "src", "book#Nope") with
                | Error(SchemaMismatch message) ->
                    Expect.stringContains message "SalesTable" "the failure lists the available selectors"
                | other -> failtestf "Expected SchemaMismatch listing the selectors; got %A" other
            }

            testCaseAsync "GetSchema over a Table infers its columns"
            <| async {
                match! (richSource ()).GetSchema(context [] "src", "book#SalesTable") with
                | Ok schema ->
                    Expect.equal schema.TableName "book#SalesTable" "TableName echoes the request"

                    Expect.sequenceEqual (schema.Columns |> List.map _.Name) [ "region"; "units"; "active" ] "columns"
                | Error err -> failtestf "GetSchema failed: %A" err
            }
        ]

        testList "table names" [
            test "a name splits into workbook and selector" {
                Expect.equal (ExcelDataSource.splitTableName "book#Sheet1") ("book", Some "Sheet1") "with selector"
                Expect.equal (ExcelDataSource.splitTableName "book") ("book", None) "without"
                Expect.equal (ExcelDataSource.splitTableName "book#") ("book", None) "an empty selector is absent"
            }

            test "compose is the inverse of split" {
                let composed = ExcelDataSource.composeTableName "book" "Sales"
                Expect.equal (ExcelDataSource.splitTableName composed) ("book", Some "Sales") "round trip"
            }
        ]

        test "Kind is the documented discriminator" {
            Expect.equal ExcelDataSource.Kind "Excel" "DataSourceConfig.Kind"
        }
    ]