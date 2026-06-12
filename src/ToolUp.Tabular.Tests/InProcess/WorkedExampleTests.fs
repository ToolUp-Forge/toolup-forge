// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Tabular.Tests.InProcess.WorkedExampleTests

open System
open Expecto
open ToolUp.Tabular
open ToolUp.Tabular.Tests.Fixtures

// ─── Phase 123 worked example — bulk product import ─────────────
//
// The end-to-end shape a consumer app's bulk-import endpoint
// follows: declare the schema, read the uploaded spreadsheet,
// bind rows onto a domain record via the binder seam, and render
// the structured error report for the operator. Doubles as the
// README's walkthrough — the assertions here keep that document
// honest.

/// The consumer's domain record — what the import actually wants.
type Product = {
    Sku: string
    Name: string
    Price: decimal
    Stock: int64
    Launched: DateTime option
    Status: string
}

/// Domain binder: `Map<string, TabularValue> -> Result<Product, _>`.
/// Cells arrive already typed — no re-parsing, just shape mapping.
let private bindProduct (row: Map<string, TabularValue>) : Result<Product, CellError list> =
    match row["Sku"], row["Name"], row["Price"], row["Status"] with
    | TabularValue.Text sku, TabularValue.Text name, TabularValue.Number price, TabularValue.Text status ->
        Ok {
            Sku = sku
            Name = name
            Price = price
            Stock =
                match row["Stock"] with
                | TabularValue.Integer stock -> stock
                | _ -> 0L
            Launched =
                match row["Launched"] with
                | TabularValue.Date date -> Some date
                | _ -> None
            Status = status
        }
    | _ ->
        // Unreachable for rows the reader bound (the schema
        // guarantees the types above) — but the binder seam keeps
        // the consumer in control, so the escape hatch is typed.
        Error []

/// Render the structured report the way an import UI would — one
/// line per problem, naming row, column, what was expected, and
/// what the file actually said.
let private renderReport (result: TabularReadResult<Product>) : string list =
    let cellLines =
        result.CellErrors
        |> List.map (fun e -> sprintf "Row %d, %s: expected %s, got '%s'" e.RowIndex e.Column e.Expected e.Actual)

    let rowLines =
        result.RowErrors
        |> List.map (fun e ->
            let detail =
                match e.Kind with
                | RowErrorKind.ArityMismatch(expected, actual) ->
                    sprintf "has %d columns where %d were declared" actual expected
                | RowErrorKind.UnparseableRow message -> message
                | RowErrorKind.MissingColumn column -> sprintf "required column '%s' is missing" column
                | RowErrorKind.ExtraColumn header -> sprintf "undeclared column '%s'" header
                | RowErrorKind.DuplicateHeader header -> sprintf "header '%s' appears twice" header

            sprintf "Row %d: %s" e.RowIndex detail)

    cellLines @ rowLines

let tests =
    testList "WorkedExample" [
        testCase "bulk import binds typed records and renders the error report"
        <| fun () ->
            // A mixed-validity upload: rows 2 + 5 are good, row 3
            // has a bad price + status, row 4 a bad SKU + stock.
            let result =
                TabularReader.readCsvBytesWith bindProduct productSchema CsvReadOptions.defaults (csvBytes mixedCsv)

            // The good rows arrive as domain records, not maps.
            Expect.equal (result.Rows |> List.map _.Sku) [ "AB-0001"; "GH-0004" ] "typed records for the clean rows"

            Expect.equal result.Rows[0].Price 9.99m "typed price"
            Expect.equal result.Rows[0].Launched (Some(DateTime(2024, 3, 1))) "typed date"

            // The report names every failing cell.
            let report = renderReport result
            Expect.equal report.Length 4 "four problems, four lines"

            Expect.isTrue
                (report
                 |> List.exists (fun line -> line.Contains "Row 3, Price" && line.Contains "not-a-price"))
                "price line names row, column and offending text"

            Expect.isTrue (report |> List.exists (fun line -> line.Contains "Row 4, Sku")) "sku line present"

        testCase "the same import shape works for XLSX uploads unchanged"
        <| fun () ->
            let bytes =
                xlsxBytes "Upload" [
                    productHeader
                    [ S "AB-0001"; I "Widget"; N 9.99; N 120.0; D serial20210101; S "Active" ]
                    [ S "broken"; I "Gadget"; S "free"; N 2.0; Gap; S "Active" ]
                ]

            let result =
                TabularReader.readXlsxBytesWith bindProduct productSchema XlsxReadOptions.defaults bytes

            Expect.equal (result.Rows |> List.map _.Sku) [ "AB-0001" ] "clean row binds"
            let report = renderReport result

            Expect.isTrue
                (report
                 |> List.exists (fun line -> line.Contains "Price" && line.Contains "free"))
                "same report shape from the XLSX leg"
    ]