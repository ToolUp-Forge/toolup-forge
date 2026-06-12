// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Tabular.Tests.InProcess.XlsxTests

open System
open System.Text
open Expecto
open ToolUp.Tabular
open ToolUp.Tabular.Tests.Fixtures

let private cleanWorkbook () =
    xlsxBytes "Products" [
        productHeader
        [ S "AB-0001"; I "Widget"; N 9.99; N 120.0; D serial20210101; S "Active" ]
        [ S "CD-0002"; S "Gadget"; N 1250.5; N 4.0; Gap; S "Preorder" ]
    ]

let tests =
    testList "Xlsx" [
        testCase "clean workbook binds shared strings, inline strings, numbers, dates"
        <| fun () ->
            let result =
                TabularReader.readXlsxBytes productSchema XlsxReadOptions.defaults (cleanWorkbook ())

            Expect.isEmpty result.CellErrors "no cell errors"
            Expect.isEmpty result.RowErrors "no row errors"
            Expect.equal result.Rows.Length 2 "two rows"

            let first = result.Rows[0]
            Expect.equal first["Sku"] (TabularValue.Text "AB-0001") "shared string"
            Expect.equal first["Name"] (TabularValue.Text "Widget") "inline string"
            Expect.equal first["Price"] (TabularValue.Number 9.99m) "number"
            Expect.equal first["Stock"] (TabularValue.Integer 120L) "integer"
            Expect.equal first["Launched"] (TabularValue.Date(DateTime(2021, 1, 1))) "date serial via style"

            let second = result.Rows[1]
            Expect.equal second["Launched"] TabularValue.Empty "sparse gap binds Empty"

        testCase "date-styled serial converts; plain number under a Date column also converts"
        <| fun () ->
            let schema =
                TableSchema.make [ ColumnSchema.make "When" ColumnType.Date |> ColumnSchema.required ]

            let bytes =
                xlsxBytes "S" [ [ S "When" ]; [ D serial20210101 ]; [ N serial20210101 ] ]

            let result = TabularReader.readXlsxBytes schema XlsxReadOptions.defaults bytes
            Expect.equal result.Rows.Length 2 "both rows bind"

            for row in result.Rows do
                Expect.equal row["When"] (TabularValue.Date(DateTime(2021, 1, 1))) "serial converts"

        testCase "boolean cells bind"
        <| fun () ->
            let schema =
                TableSchema.make [ ColumnSchema.make "Flag" ColumnType.Bool |> ColumnSchema.required ]

            let bytes = xlsxBytes "S" [ [ S "Flag" ]; [ B true ]; [ B false ] ]
            let result = TabularReader.readXlsxBytes schema XlsxReadOptions.defaults bytes

            Expect.equal
                (result.Rows |> List.map (fun row -> row["Flag"]))
                [ TabularValue.Bool true; TabularValue.Bool false ]
                "both spellings"

        testCase "sheet selection by name is case-insensitive"
        <| fun () ->
            let result =
                TabularReader.readXlsxBytes
                    productSchema
                    {
                        XlsxReadOptions.defaults with
                            Sheet = SheetSelection.Name "products"
                    }
                    (cleanWorkbook ())

            Expect.equal result.Rows.Length 2 "rows bind via named sheet"

        testCase "missing sheet reports a file-level row error, no exception"
        <| fun () ->
            let result =
                TabularReader.readXlsxBytes
                    productSchema
                    {
                        XlsxReadOptions.defaults with
                            Sheet = SheetSelection.Name "Nope"
                    }
                    (cleanWorkbook ())

            Expect.isEmpty result.Rows "no rows"
            Expect.equal result.RowErrors.Length 1 "one structural error"
            Expect.equal result.RowErrors[0].RowIndex 0 "file-level errors carry row 0"

            match result.RowErrors[0].Kind with
            | RowErrorKind.UnparseableRow message ->
                Expect.stringContains message "Nope" "names the requested sheet"
                Expect.stringContains message "Products" "names the available sheets"
            | other -> failtestf "expected UnparseableRow, got %A" other

        testCase "garbage bytes report instead of throwing"
        <| fun () ->
            let result =
                TabularReader.readXlsxBytes
                    productSchema
                    XlsxReadOptions.defaults
                    (Encoding.UTF8.GetBytes "this is not a zip archive")

            Expect.isEmpty result.Rows "no rows"
            Expect.equal result.RowErrors.Length 1 "one structural error"

            match result.RowErrors[0].Kind with
            | RowErrorKind.UnparseableRow message ->
                Expect.stringContains message "not a readable XLSX workbook" "fatal message"
            | other -> failtestf "expected UnparseableRow, got %A" other

        testCase "mixed-validity workbook reports failing cells with raw text"
        <| fun () ->
            let bytes =
                xlsxBytes "Products" [
                    productHeader
                    [ S "AB-0001"; S "Widget"; N 9.99; N 120.0; D serial20210101; S "Active" ]
                    [ S "no good"; S "Gadget"; S "free"; N 2.5; D serial20210101; S "Maybe" ]
                ]

            let result =
                TabularReader.readXlsxBytes productSchema XlsxReadOptions.defaults bytes

            Expect.equal result.Rows.Length 1 "clean row binds"
            Expect.equal result.TotalRows 2 "two data rows seen"

            let columns = result.CellErrors |> List.map _.Column |> List.sort
            Expect.equal columns [ "Price"; "Sku"; "Status"; "Stock" ] "four failing cells"

            let price = result.CellErrors |> List.find (fun e -> e.Column = "Price")
            Expect.equal price.Actual "free" "raw text carried"
            Expect.equal price.RowIndex 3 "sheet row number"

        testCase "required cell missing in a sparse row is reported"
        <| fun () ->
            let bytes =
                xlsxBytes "Products" [
                    productHeader
                    [ Gap; S "Widget"; N 9.99; N 1.0; D serial20210101; S "Active" ]
                ]

            let result =
                TabularReader.readXlsxBytes productSchema XlsxReadOptions.defaults bytes

            Expect.isEmpty result.Rows "row excluded"

            let sku = result.CellErrors |> List.find (fun e -> e.Column = "Sku")

            Expect.equal sku.Violation (Some ConstraintViolation.RequiredValueMissing) "required-missing violation"
    ]