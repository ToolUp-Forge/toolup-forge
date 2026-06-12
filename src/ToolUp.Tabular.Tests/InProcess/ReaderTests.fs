// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Tabular.Tests.InProcess.ReaderTests

open Expecto
open ToolUp.Tabular
open ToolUp.Tabular.Tests.Fixtures

let private read schema text =
    use stream = csvStream text
    TabularReader.readCsv schema CsvReadOptions.defaults stream

let tests =
    testList "Reader" [
        testCase "missing declared column under Reject aborts with MissingColumn, zero rows"
        <| fun () ->
            let text = "Sku,Name,Price\nAB-0001,Widget,1.00\n"
            let result = read productSchema text
            Expect.isEmpty result.Rows "no rows processed"
            Expect.equal result.TotalRows 0 "no data rows visited"

            let missing =
                result.RowErrors
                |> List.choose (fun e ->
                    match e.Kind with
                    | RowErrorKind.MissingColumn column -> Some column
                    | _ -> None)
                |> List.sort

            Expect.equal missing [ "Launched"; "Status"; "Stock" ] "every absent column named"

        testCase "missing column under TreatAsEmpty binds present columns"
        <| fun () ->
            let schema = {
                productSchema with
                    MissingColumns = MissingColumnPolicy.TreatAsEmpty
            }

            let text = "Sku,Name,Price\nAB-0001,Widget,1.00\n"
            let result = read schema text
            Expect.equal result.Rows.Length 1 "row binds"
            let row = result.Rows[0]
            Expect.equal row["Stock"] TabularValue.Empty "absent optional column is Empty"
            Expect.isEmpty result.RowErrors "no structural errors"

        testCase "extra file column is ignored by default"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status,Comment\n"
                + "AB-0001,Widget,1.00,1,2024-01-01,Active,nice\n"

            let result = read productSchema text
            Expect.equal result.Rows.Length 1 "row binds"
            Expect.isEmpty result.RowErrors "extra column tolerated"
            Expect.equal result.Rows[0].Count 6 "undeclared column not bound"

        testCase "extra file column under Reject is reported and rows still bind"
        <| fun () ->
            let schema = {
                productSchema with
                    ExtraColumns = ExtraColumnPolicy.Reject
            }

            let text =
                "Sku,Name,Price,Stock,Launched,Status,Comment\n"
                + "AB-0001,Widget,1.00,1,2024-01-01,Active,nice\n"

            let result = read schema text

            let extras =
                result.RowErrors
                |> List.choose (fun e ->
                    match e.Kind with
                    | RowErrorKind.ExtraColumn header -> Some header
                    | _ -> None)

            Expect.equal extras [ "Comment" ] "extra header named"
            // The data row carries a cell under the extra header
            // too — width 7 against the declared 7-header row is
            // consistent, so the row itself still binds.
            Expect.equal result.Rows.Length 1 "row binds"

        testCase "duplicate header aborts with DuplicateHeader"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status,Sku\nAB-0001,Widget,1.00,1,2024-01-01,Active,dup\n"

            let result = read productSchema text
            Expect.isEmpty result.Rows "no rows"

            Expect.isTrue
                (result.RowErrors
                 |> List.exists (fun e ->
                     match e.Kind with
                     | RowErrorKind.DuplicateHeader header -> header = "Sku"
                     | _ -> false))
                "duplicate named"

        testCase "header matching is case-insensitive and trims"
        <| fun () ->
            let text =
                " sku , NAME ,Price,Stock,Launched,Status\nAB-0001,Widget,1.00,1,2024-01-01,Active\n"

            let result = read productSchema text
            Expect.equal result.Rows.Length 1 "row binds"

        testCase "Header override matches the file text, binds under the canonical name"
        <| fun () ->
            let schema =
                TableSchema.make [
                    ColumnSchema.make "UnitPrice" ColumnType.Number
                    |> ColumnSchema.required
                    |> ColumnSchema.withHeader "Unit Price (GBP)"
                ]

            let text = "Unit Price (GBP)\n12.50\n"
            let result = read schema text
            let row = result.Rows[0]
            Expect.equal row["UnitPrice"] (TabularValue.Number 12.5m) "bound under canonical name"

        testCase "NoHeaderRow binds positionally from the first row"
        <| fun () ->
            let schema = {
                productSchema with
                    HeaderRow = HeaderRowPolicy.NoHeaderRow
            }

            let text = "AB-0001,Widget,1.00,1,2024-01-01,Active\n"
            let result = read schema text
            Expect.equal result.Rows.Length 1 "first row is data"
            let row = result.Rows[0]
            Expect.equal row["Sku"] (TabularValue.Text "AB-0001") "positional binding"

        testCase "over-long row under Reject reports ArityMismatch"
        <| fun () ->
            let schema = {
                productSchema with
                    HeaderRow = HeaderRowPolicy.NoHeaderRow
                    ExtraColumns = ExtraColumnPolicy.Reject
            }

            let text = "AB-0001,Widget,1.00,1,2024-01-01,Active,surplus\n"
            let result = read schema text
            Expect.isEmpty result.Rows "row excluded"

            match result.RowErrors with
            | [ {
                    Kind = RowErrorKind.ArityMismatch(expected, actual)
                } ] ->
                Expect.equal expected 6 "declared width"
                Expect.equal actual 7 "row width"
            | other -> failtestf "expected one ArityMismatch, got %A" other

        testCase "short row pads with Empty instead of erroring"
        <| fun () ->
            let text = "Sku,Name,Price,Stock,Launched,Status\nAB-0001,Widget,1.00\n"
            let result = read productSchema text
            Expect.equal result.Rows.Length 1 "row binds"
            let row = result.Rows[0]
            Expect.equal row["Status"] TabularValue.Empty "trailing absent cells are Empty"

        testCase "constraint violations carry the specific violation"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status\n"
                + "AB-0001,Widget,-5,1,2024-01-01,Active\n"
                + "CD-0002,Gadget,20000,1,2024-01-01,Active\n"
                + "EF-9999,Sprocket,1.00,-3,2024-01-01,Active\n"

            let result = read productSchema text

            let violations =
                result.CellErrors |> List.map (fun e -> e.RowIndex, e.Column, e.Violation)

            Expect.contains violations (2, "Price", Some(ConstraintViolation.BelowMinimum 0m)) "below minimum"

            Expect.contains violations (3, "Price", Some(ConstraintViolation.AboveMaximum 10000m)) "above maximum"

            Expect.contains violations (4, "Stock", Some(ConstraintViolation.BelowMinimum 0m)) "integer range checked"

        testCase "pattern and max-length violations report"
        <| fun () ->
            let schema =
                TableSchema.make [
                    ColumnSchema.make "Code" ColumnType.Text
                    |> ColumnSchema.withConstraints {
                        ColumnConstraints.none with
                            Pattern = Some "[a-z]+"
                            MaxLength = Some 5
                    }
                ]

            let result = read schema "Code\nABC\nabcdefgh\nok\n"
            Expect.equal result.Rows.Length 1 "only 'ok' binds"

            let kinds = result.CellErrors |> List.map _.Violation

            Expect.contains kinds (Some(ConstraintViolation.PatternMismatch "[a-z]+")) "pattern"
            Expect.contains kinds (Some(ConstraintViolation.TooLong(5, 8))) "max length"

        testCase "invalid Pattern regex throws eagerly as a schema-authoring error"
        <| fun () ->
            let schema =
                TableSchema.make [
                    ColumnSchema.make "Code" ColumnType.Text
                    |> ColumnSchema.withConstraints {
                        ColumnConstraints.none with
                            Pattern = Some "([unclosed"
                    }
                ]

            Expect.throwsT<System.ArgumentException>
                (fun () ->
                    use stream = csvStream "Code\nx\n"
                    TabularReader.readCsv schema CsvReadOptions.defaults stream |> ignore)
                "invalid regex is a programmer error, thrown before rows are read"

        testCase "error cap truncates the read"
        <| fun () ->
            let rows =
                Seq.init 50 (fun i -> sprintf "bad sku %d,Widget,not-a-price,many,2024-01-01,Nope" i)
                |> String.concat "\n"

            let text = "Sku,Name,Price,Stock,Launched,Status\n" + rows + "\n"
            use stream = csvStream text

            let result =
                TabularReader.readCsv
                    productSchema
                    {
                        CsvReadOptions.defaults with
                            MaxErrors = Some 10
                    }
                    stream

            Expect.isTrue result.Truncated "cap fired"
            Expect.isTrue (result.CellErrors.Length >= 10) "at least the cap was collected"
            Expect.isTrue (result.TotalRows < 50) "enumeration stopped early"

        testCase "binder seam maps bound rows to domain records and stamps RowIndex on binder errors"
        <| fun () ->
            let binder (row: Map<string, TabularValue>) =
                match row["Sku"], row["Price"] with
                | TabularValue.Text sku, TabularValue.Number price when price < 100m -> Ok(sku, price)
                | TabularValue.Text _, TabularValue.Number _ ->
                    Error [
                        {
                            RowIndex = 0 // reader stamps the real index
                            Column = "Price"
                            Expected = "a price under 100"
                            Actual = "(domain rule)"
                            Violation = None
                        }
                    ]
                | _ -> Error []

            use stream = csvStream cleanCsv

            let result =
                TabularReader.readCsvWith binder productSchema CsvReadOptions.defaults stream

            Expect.equal result.Rows.Length 2 "two rows pass the domain rule"
            Expect.equal result.Rows[0] ("AB-0001", 9.99m) "typed tuple out"

            match result.CellErrors with
            | [ error ] ->
                Expect.equal error.RowIndex 3 "reader stamped the failing row's index"
                Expect.equal error.Expected "a price under 100" "binder error carried"
            | other -> failtestf "expected one binder error, got %A" other

        testCase "GP 13 — companion references no ToolUp.Platform assembly (strip-imports proof)"
        <| fun () ->
            // The companion is additive by construction: nothing in
            // ToolUp.Platform.* references it, and it references
            // nothing in ToolUp.Platform.* — so removing the
            // companion reference from a consumer cannot break the
            // platform build, and a deployment that doesn't compose
            // it pays nothing.
            let references =
                typeof<TabularValue>.Assembly.GetReferencedAssemblies() |> Array.map _.Name

            Expect.isFalse
                (references
                 |> Array.exists (fun name -> name <> null && name.StartsWith "ToolUp."))
                (sprintf "no ToolUp.* references, got: %A" references)

        testCase "CSV leg dependency surface is BCL-only"
        <| fun () ->
            // The companion's only third-party reference is the
            // OpenXml reader for the XLSX leg (Phase 123 acceptance
            // criterion: the CSV leg carries zero vendor deps).
            let nonBcl =
                typeof<TabularValue>.Assembly.GetReferencedAssemblies()
                |> Array.map _.Name
                |> Array.filter (fun name ->
                    name <> null
                    && not (name.StartsWith "System")
                    && not (name.StartsWith "Microsoft")
                    && name <> "FSharp.Core"
                    && name <> "netstandard"
                    && name <> "mscorlib")

            Expect.isTrue
                (nonBcl |> Array.forall (fun name -> name.StartsWith "DocumentFormat.OpenXml"))
                (sprintf "only OpenXml expected outside the BCL, got: %A" nonBcl)
    ]