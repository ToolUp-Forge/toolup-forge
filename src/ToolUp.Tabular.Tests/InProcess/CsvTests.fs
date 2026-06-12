// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Tabular.Tests.InProcess.CsvTests

open System
open System.IO
open System.Text
open Expecto
open ToolUp.Tabular
open ToolUp.Tabular.Tests.Fixtures

let private readClean text =
    use stream = csvStream text
    TabularReader.readCsv productSchema CsvReadOptions.defaults stream

/// Counts bytes the consumer actually pulled — the streaming
/// proof reads a prefix of a large file and asserts most of it
/// was never touched.
type private CountingStream(inner: Stream) =
    inherit Stream()
    member val BytesRead = 0L with get, set
    override this.CanRead = inner.CanRead
    override this.CanSeek = false
    override this.CanWrite = false
    override this.Length = inner.Length

    override this.Position
        with get () = inner.Position
        and set _ = raise (NotSupportedException())

    override this.Flush() = inner.Flush()

    override this.Read(buffer, offset, count) =
        let read = inner.Read(buffer, offset, count)
        this.BytesRead <- this.BytesRead + int64 read
        read

    override this.Seek(_, _) = raise (NotSupportedException())
    override this.SetLength _ = raise (NotSupportedException())
    override this.Write(_, _, _) = raise (NotSupportedException())

let tests =
    testList "Csv" [
        testCase "clean fixture binds every row with typed values"
        <| fun () ->
            let result = readClean cleanCsv
            Expect.equal result.Rows.Length 3 "three data rows"
            Expect.isEmpty result.CellErrors "no cell errors"
            Expect.isEmpty result.RowErrors "no row errors"
            Expect.equal result.TotalRows 3 "total rows"
            Expect.isFalse result.Truncated "not truncated"

            let first = result.Rows[0]
            Expect.equal first["Sku"] (TabularValue.Text "AB-0001") "sku"
            Expect.equal first["Price"] (TabularValue.Number 9.99m) "price"
            Expect.equal first["Stock"] (TabularValue.Integer 120L) "stock"
            Expect.equal first["Launched"] (TabularValue.Date(DateTime(2024, 3, 1))) "launched"
            Expect.equal first["Status"] (TabularValue.Text "Active") "choice binds canonical value"

            let second = result.Rows[1]
            Expect.equal second["Name"] (TabularValue.Text "Gadget, large") "quoted embedded delimiter"

            let third = result.Rows[2]
            Expect.equal third["Stock"] TabularValue.Empty "optional empty binds Empty"
            Expect.equal third["Launched"] TabularValue.Empty "optional empty date binds Empty"

        testCase "rows are total maps over schema columns"
        <| fun () ->
            let result = readClean cleanCsv

            for row in result.Rows do
                Expect.equal row.Count 6 "every schema column has a key"

        testCase "mixed-validity fixture reports every failing cell and keeps good rows"
        <| fun () ->
            let result = readClean mixedCsv
            Expect.equal result.Rows.Length 2 "two clean rows bind"
            Expect.equal result.TotalRows 4 "four data rows seen"

            let errorAt row column =
                result.CellErrors
                |> List.tryFind (fun e -> e.RowIndex = row && e.Column = column)

            let priceError = errorAt 3 "Price"
            Expect.isSome priceError "row 3 Price reported"
            Expect.equal priceError.Value.Actual "not-a-price" "actual carries raw text"
            Expect.isNone priceError.Value.Violation "type-parse failure has no constraint violation"
            Expect.stringContains priceError.Value.Expected "number" "expected wording names the type"

            Expect.isSome (errorAt 3 "Status") "row 3 Status reported"
            Expect.isSome (errorAt 4 "Sku") "row 4 Sku pattern reported"
            Expect.isSome (errorAt 4 "Stock") "row 4 Stock reported"
            Expect.equal result.CellErrors.Length 4 "exactly the four failing cells"

        testCase "embedded newline inside quotes stays one record"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status\n"
                + "AB-0001,\"Line one\nline two\",1.00,1,2024-01-01,Active\n"

            let result = readClean text
            Expect.equal result.Rows.Length 1 "one row"
            let row = result.Rows[0]
            Expect.equal row["Name"] (TabularValue.Text "Line one\nline two") "newline preserved"

        testCase "escaped quotes bind literally"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status\n"
                + "AB-0001,\"The \"\"big\"\" one\",1.00,1,2024-01-01,Active\n"

            let result = readClean text
            let row = result.Rows[0]
            Expect.equal row["Name"] (TabularValue.Text "The \"big\" one") "quotes unescaped"

        testCase "configurable delimiter parses semicolon files"
        <| fun () ->
            let text =
                "Sku;Name;Price;Stock;Launched;Status\n"
                + "AB-0001;Widget;9.99;120;2024-03-01;Active\n"

            use stream = csvStream text

            let result =
                TabularReader.readCsv
                    productSchema
                    {
                        CsvReadOptions.defaults with
                            Delimiter = ';'
                    }
                    stream

            Expect.equal result.Rows.Length 1 "one row"
            let row = result.Rows[0]
            Expect.equal row["Price"] (TabularValue.Number 9.99m) "price"

        testCase "UTF-8 BOM is consumed, not bound into the first header"
        <| fun () ->
            let bytes = Array.append (UTF8Encoding(true).GetPreamble()) (csvBytes cleanCsv)
            use stream = new MemoryStream(bytes)
            let result = TabularReader.readCsv productSchema CsvReadOptions.defaults stream
            Expect.equal result.Rows.Length 3 "all rows bind"
            Expect.isEmpty result.RowErrors "header matched despite BOM"

        testCase "UTF-16 BOM decodes via byte-order-mark detection"
        <| fun () ->
            let bytes =
                Array.append (Encoding.Unicode.GetPreamble()) (Encoding.Unicode.GetBytes cleanCsv)

            use stream = new MemoryStream(bytes)
            let result = TabularReader.readCsv productSchema CsvReadOptions.defaults stream
            Expect.equal result.Rows.Length 3 "all rows bind"

        testCase "malformed quoting fails that record only"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status\n"
                + "AB-0001,\"Widget\"x,1.00,1,2024-01-01,Active\n"
                + "CD-0002,Gadget,2.00,2,2024-01-02,Active\n"

            let result = readClean text
            Expect.equal result.Rows.Length 1 "the following record still parses"

            let structural =
                result.RowErrors
                |> List.tryFind (fun e ->
                    match e.Kind with
                    | RowErrorKind.UnparseableRow _ -> true
                    | _ -> false)

            Expect.isSome structural "malformed record reported"
            Expect.equal structural.Value.RowIndex 2 "at the offending record"

        testCase "unterminated quote at EOF reports instead of throwing"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status\n"
                + "AB-0001,\"never closed,1.00,1,2024-01-01,Active\n"

            let result = readClean text
            Expect.isEmpty result.Rows "no rows bind"

            Expect.isTrue
                (result.RowErrors
                 |> List.exists (fun e ->
                     match e.Kind with
                     | RowErrorKind.UnparseableRow message -> message.Contains "unterminated"
                     | _ -> false))
                "unterminated quote reported"

        testCase "empty file yields an empty result, no errors"
        <| fun () ->
            let result = readClean ""
            Expect.isEmpty result.Rows "no rows"
            Expect.isEmpty result.CellErrors "no cell errors"
            Expect.isEmpty result.RowErrors "no row errors"
            Expect.equal result.TotalRows 0 "no rows seen"

        testCase "blank lines are skipped, not reported"
        <| fun () ->
            let text =
                "Sku,Name,Price,Stock,Launched,Status\n\n"
                + "AB-0001,Widget,1.00,1,2024-01-01,Active\n\n"

            let result = readClean text
            Expect.equal result.Rows.Length 1 "one data row"
            Expect.isEmpty result.CellErrors "blank lines produce no errors"

        testCase "streaming enumeration does not read the whole file"
        <| fun () ->
            // 100k data rows, ~5 MB. Taking the first 10 outcomes
            // must leave the overwhelming majority of the file
            // unread (Phase 123 large-file acceptance criterion).
            let sb = StringBuilder()
            sb.AppendLine "Sku,Name,Price,Stock,Launched,Status" |> ignore

            for i in 1..100_000 do
                sb.AppendLine(sprintf "AB-%04d,Widget %d,9.99,%d,2024-03-01,Active" (i % 10000) i i)
                |> ignore

            let bytes = csvBytes (sb.ToString())
            use inner = new MemoryStream(bytes)
            use counting = new CountingStream(inner)

            let outcomes =
                TabularReader.streamCsv productSchema CsvReadOptions.defaults counting
                |> Seq.take 10
                |> List.ofSeq

            Expect.equal outcomes.Length 10 "ten outcomes taken"

            Expect.isTrue
                (counting.BytesRead < int64 bytes.Length / 10L)
                (sprintf "read %d of %d bytes — should be a small prefix" counting.BytesRead bytes.Length)
    ]