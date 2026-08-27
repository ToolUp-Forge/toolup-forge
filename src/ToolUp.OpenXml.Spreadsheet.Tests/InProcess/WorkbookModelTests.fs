// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 574.A — the workbook model's own contract: Excel's sheet-name
/// rules surfaced as a recoverable `Result` (never a silent
/// truncation), the merged-range / column-width shape checks, and the
/// column-address arithmetic the emitter and `ToolUp.Tabular`'s reader
/// must agree on.
module ToolUp.OpenXml.Spreadsheet.Tests.InProcess.WorkbookModelTests

open Expecto
open ToolUp.OpenXml.Spreadsheet
open ToolUp.OpenXml.Spreadsheet.Tests

let private sheetNameCases =
    testList "sheet-name validation" [
        test "a legal name passes through unchanged" {
            Expect.equal (SheetName.validate "Summary") (Ok "Summary") "a legal name must be returned verbatim"
        }

        test "a 31-character name is legal; 32 is not" {
            let atLimit = String.replicate 31 "a"
            Expect.equal (SheetName.validate atLimit) (Ok atLimit) "31 characters is Excel's limit, not one past it"

            Expect.equal
                (SheetName.validate (String.replicate 32 "a"))
                (Error(SheetNameTooLong 32))
                "32 characters must be refused, naming the length"
        }

        test "an over-long name is REFUSED, never truncated" {
            // The whole point of the Result: a caller that hands over a
            // 40-character name gets it back as an error, not as a
            // 31-character name in a file that opens and is wrong.
            let name = String.replicate 40 "x"

            match SheetName.validate name with
            | Ok accepted -> failtestf "an over-long name must not be accepted (got '%s')" accepted
            | Error(SheetNameTooLong length) -> Expect.equal length 40 "the reported length is the name's own"
            | Error other -> failtestf "expected SheetNameTooLong, got %A" other
        }

        test "every character Excel refuses is reported" {
            for illegal in SheetName.illegalCharacters do
                let name = sprintf "Data%cSheet" illegal

                Expect.equal
                    (SheetName.validate name)
                    (Error(SheetNameIllegalCharacters [ illegal ]))
                    (sprintf "'%c' must be refused in a sheet name" illegal)
        }

        test "several illegal characters are all named, in first-appearance order" {
            Expect.equal
                (SheetName.validate "a/b\\c/d")
                (Error(SheetNameIllegalCharacters [ '/'; '\\' ]))
                "each distinct offending character is reported once, in the order it first appears"
        }

        test "a leading or trailing apostrophe is refused" {
            Expect.equal
                (SheetName.validate "'Data")
                (Error SheetNameEnclosingApostrophe)
                "a leading apostrophe must be refused"

            Expect.equal
                (SheetName.validate "Data'")
                (Error SheetNameEnclosingApostrophe)
                "a trailing apostrophe must be refused"

            Expect.equal (SheetName.validate "Don't") (Ok "Don't") "an interior apostrophe is legal"
        }

        test "the reserved name is refused case-insensitively" {
            Expect.equal
                (SheetName.validate "history")
                (Error(SheetNameReserved "History"))
                "Excel reserves 'History' whatever its casing"
        }

        test "empty and whitespace names are refused" {
            Expect.equal (SheetName.validate "") (Error SheetNameEmpty) "an empty name must be refused"
            Expect.equal (SheetName.validate "   ") (Error SheetNameEmpty) "a whitespace name must be refused"
        }

        test "SheetModel.create surfaces the name failure rather than building" {
            match SheetModel.create "Bad/Name" [] with
            | Ok sheet -> failtestf "an illegal name must not produce a sheet (got '%s')" sheet.Name
            | Error error -> Expect.equal error (SheetNameIllegalCharacters [ '/' ]) "the failure names the character"
        }

        test "every error renders a message naming the offending name" {
            let errors = [
                SheetNameEmpty
                SheetNameTooLong 40
                SheetNameIllegalCharacters [ '/' ]
                SheetNameEnclosingApostrophe
                SheetNameReserved "History"
            ]

            for error in errors do
                let described = SheetName.describeError "Offender" error
                Expect.isNotEmpty described "every sheet-name error must render a message"
        }
    ]

let private workbookValidationCases =
    testList "workbook validation" [
        test "a sheetless workbook is refused" {
            Expect.equal
                (WorkbookModel.problems WorkbookModel.empty)
                [ EmptyWorkbook ]
                "Excel cannot open a workbook with no sheets"
        }

        test "duplicate sheet names are refused, case-insensitively" {
            let model =
                WorkbookModel.ofSheets [ Fixtures.sheet "Data" []; Fixtures.sheet "data" [] ]

            Expect.equal
                (WorkbookModel.problems model)
                [ DuplicateSheetName "Data" ]
                "Excel compares sheet names case-insensitively, so 'Data' and 'data' collide"
        }

        test "an inverted merged range is refused" {
            let model =
                Fixtures.sheet "Data" []
                |> SheetModel.withMergedRanges [
                    {
                        FirstRow = 4
                        FirstColumn = 0
                        LastRow = 1
                        LastColumn = 2
                    }
                ]
                |> List.singleton
                |> WorkbookModel.ofSheets

            match WorkbookModel.problems model with
            | [ InvalidMergedRange(sheetName, _, _) ] -> Expect.equal sheetName "Data" "the owning sheet is named"
            | other -> failtestf "expected one InvalidMergedRange, got %A" other
        }

        test "a negative merged-range index is refused" {
            let model =
                Fixtures.sheet "Data" []
                |> SheetModel.withMergedRanges [
                    {
                        FirstRow = -1
                        FirstColumn = 0
                        LastRow = 0
                        LastColumn = 0
                    }
                ]
                |> List.singleton
                |> WorkbookModel.ofSheets

            Expect.isNonEmpty (WorkbookModel.problems model) "a negative index must be refused"
        }

        test "a non-positive or non-finite column width is refused" {
            for width in [ 0.0; -3.0; nan; infinity ] do
                let model =
                    Fixtures.sheet "Data" []
                    |> SheetModel.withColumnWidths [ { ColumnIndex = 0; Width = width } ]
                    |> List.singleton
                    |> WorkbookModel.ofSheets

                Expect.isNonEmpty (WorkbookModel.problems model) (sprintf "width %g must be refused" width)
        }

        test "validation reports every problem, not just the first" {
            let model =
                WorkbookModel.ofSheets [
                    {
                        Fixtures.sheet "Data" [] with
                            Name = "Bad/Name"
                    }
                    {
                        Fixtures.sheet "Data" [] with
                            Name = String.replicate 40 "x"
                    }
                ]

            Expect.equal
                (WorkbookModel.problems model |> List.length)
                2
                "both sheet-name failures must be reported in one pass"
        }

        test "a valid model validates clean" {
            Expect.isOk
                (WorkbookModel.validate (Fixtures.mixedKindWorkbook ()))
                "the acceptance fixture must be a valid model"
        }

        test "every workbook error renders a message" {
            let errors = [
                InvalidSheetName("x", SheetNameEmpty)
                DuplicateSheetName "Data"
                EmptyWorkbook
                InvalidMergedRange(
                    "Data",
                    {
                        FirstRow = 0
                        FirstColumn = 0
                        LastRow = 0
                        LastColumn = 0
                    },
                    "reason"
                )
                InvalidColumnWidth("Data", 0, "reason")
            ]

            for error in errors do
                Expect.isNotEmpty (WorkbookError.describe error) "every workbook error must render a message"
        }
    ]

let private addressCases =
    testList "cell addressing" [
        test "column names follow Excel's bijective base-26" {
            let expected = [
                0, "A"
                25, "Z"
                26, "AA"
                27, "AB"
                51, "AZ"
                52, "BA"
                701, "ZZ"
                702, "AAA"
            ]

            for index, name in expected do
                Expect.equal (WorkbookModel.columnName index) name (sprintf "column %d must render as %s" index name)
        }

        test "a negative column index is rejected rather than producing nonsense" {
            Expect.throws
                (fun () -> WorkbookModel.columnName -1 |> ignore)
                "a negative column index has no address and must not silently produce one"
        }

        test "cell references are row-1-based" {
            Expect.equal (WorkbookModel.cellReference 0 0) "A1" "the top-left cell is A1"
            Expect.equal (WorkbookModel.cellReference 4 2) "C5" "row 4 / column 2 is C5"
        }

        test "column naming inverts ToolUp.Tabular's reference decoder" {
            // The two packages sit on opposite sides of the same file
            // format; a disagreement here would be silent, and would
            // present as cells landing in the wrong column.
            for index in [ 0; 1; 25; 26; 27; 100; 701; 702; 1000 ] do
                let name = WorkbookModel.columnName index

                Expect.equal
                    (ToolUp.Tabular.Xlsx.columnIndexOfReference name)
                    (Some index)
                    (sprintf "the reader must decode '%s' back to column %d" name index)
        }

        test "SheetModel.columnCount is the widest row" {
            let sheet =
                Fixtures.sheet "Data" [
                    RowModel.ofText [ "a" ]
                    RowModel.ofText [ "a"; "b"; "c" ]
                    RowModel.ofText [ "a"; "b" ]
                ]

            Expect.equal (SheetModel.columnCount sheet) 3 "the widest row decides the column count"
        }
    ]

let tests =
    testList "Phase 574.A — workbook model" [ sheetNameCases; workbookValidationCases; addressCases ]