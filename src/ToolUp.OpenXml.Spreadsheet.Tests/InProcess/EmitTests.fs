// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 574.B — emission: the parts the model lowers to (shared
/// strings, styles carrying the number formats, `cols`, `mergeCells`),
/// the refusal path for a model Excel would not open, and the
/// determinism property the phase's acceptance criteria name.
module ToolUp.OpenXml.Spreadsheet.Tests.InProcess.EmitTests

open System
open System.IO
open System.IO.Compression
open Expecto
open ToolUp.OpenXml.Spreadsheet
open ToolUp.OpenXml.Spreadsheet.Tests

let private entryNames (bytes: byte[]) : string list =
    use stream = new MemoryStream(bytes)
    use archive = new ZipArchive(stream, ZipArchiveMode.Read)
    archive.Entries |> Seq.map _.FullName |> List.ofSeq

let private determinismCases =
    testList "determinism" [
        test "two emits of the same model are byte-identical" {
            // The acceptance criterion, and the one that fails silently
            // without help: the SDK stamps each ZIP entry with the
            // current time and mints the package-root relationship id
            // from a fresh GUID, so an un-normalised emit differs run to
            // run while agreeing in every readable respect.
            let model = Fixtures.mixedKindWorkbook ()
            let first = Emit.toBytes model
            Threading.Thread.Sleep 1100
            let second = Emit.toBytes model

            Expect.equal second first "two emits of one model must produce identical bytes"
        }

        test "the emitted package carries no wall-clock entry timestamp" {
            // The falsifier for the case above: prove the mechanism,
            // not just the agreement. A run-to-run comparison inside one
            // fast test could pass on a same-second coincidence; the
            // fixed timestamp is checkable directly.
            use stream = new MemoryStream(Emit.toBytes (Fixtures.mixedKindWorkbook ()))
            use archive = new ZipArchive(stream, ZipArchiveMode.Read)

            for entry in archive.Entries do
                Expect.equal
                    entry.LastWriteTime.UtcDateTime
                    (DateTime(1980, 1, 1, 0, 0, 0, DateTimeKind.Utc))
                    (sprintf "entry '%s' must carry the fixed timestamp, not the wall clock" entry.FullName)
        }

        test "the package-root relationship id is stable, not a fresh GUID" {
            let readRootRels (bytes: byte[]) =
                use stream = new MemoryStream(bytes)
                use archive = new ZipArchive(stream, ZipArchiveMode.Read)
                use entry = (archive.GetEntry "_rels/.rels").Open()
                use reader = new StreamReader(entry)
                reader.ReadToEnd()

            let model = Fixtures.mixedKindWorkbook ()
            let first = readRootRels (Emit.toBytes model)
            let second = readRootRels (Emit.toBytes model)

            Expect.equal second first "the root relationships part must not vary between emits"
            Expect.stringContains first "rId1" "the root relationship id must be the normalised form"
        }

        test "models differing only in a cell value emit different bytes" {
            // Determinism must not have been bought by emitting a
            // constant. A guard that cannot fail is not a guard.
            let build value =
                Fixtures.sheet "Data" [ RowModel.ofCells [ CellModel.number value ] ]
                |> List.singleton
                |> WorkbookModel.ofSheets
                |> Emit.toBytes

            Expect.notEqual (build 2.0) (build 1.0) "a different model must produce different bytes"
        }

        test "entries are written in a stable ordinal order, content types first" {
            let names = entryNames (Emit.toBytes (Fixtures.mixedKindWorkbook ()))

            Expect.equal
                (List.head names)
                "[Content_Types].xml"
                "the content-type map is conventionally the first entry"

            Expect.equal
                names
                (names |> List.sortWith (fun a b -> String.CompareOrdinal(a, b)))
                "entries must be written in ordinal name order"
        }
    ]

let private partCases =
    testList "emitted parts" [
        test "the package carries the expected part set" {
            let names = entryNames (Emit.toBytes (Fixtures.mixedKindWorkbook ()))

            for expected in
                [
                    "[Content_Types].xml"
                    "_rels/.rels"
                    "xl/workbook.xml"
                    "xl/_rels/workbook.xml.rels"
                    "xl/worksheets/sheet1.xml"
                    "xl/sharedStrings.xml"
                    "xl/styles.xml"
                ] do
                Expect.contains names expected (sprintf "the package must carry %s" expected)
        }

        test "string cells pool into the shared-string table, once per distinct value" {
            let model =
                Fixtures.sheet "Data" [ RowModel.ofText [ "alpha"; "beta" ]; RowModel.ofText [ "alpha"; "gamma" ] ]
                |> List.singleton
                |> WorkbookModel.ofSheets

            Expect.equal
                (Fixtures.sharedStrings (Emit.toBytes model))
                [ "alpha"; "beta"; "gamma" ]
                "distinct values only, in first-appearance order"
        }

        test "number formats reach the styles part under custom ids from 164" {
            let codes =
                Fixtures.numberFormatCodes (Emit.toBytes (Fixtures.mixedKindWorkbook ()))

            Expect.equal
                codes
                // Row 3 is text / 0.00 / date, so the date default is
                // seen before row 4's thousands format — first
                // appearance is a cell-order walk, not a per-row one.
                [ 164u, "0.00"; 165u, SpreadsheetDefaults.dateFormat; 166u, "#,##0" ]
                "format codes are allocated ids from 164 in first-appearance order"
        }

        test "a workbook with no formats still emits a valid styles part" {
            let bytes =
                Fixtures.sheet "Data" [ RowModel.ofText [ "plain" ] ]
                |> List.singleton
                |> WorkbookModel.ofSheets
                |> Emit.toBytes

            Expect.contains (entryNames bytes) "xl/styles.xml" "the styles part is always written"
            Expect.isEmpty (Fixtures.numberFormatCodes bytes) "a format-free workbook allocates no custom formats"
        }

        test "merged ranges lower to A1-style mergeCell references" {
            Expect.equal
                (Fixtures.mergedReferences (Emit.toBytes (Fixtures.mixedKindWorkbook ())) "Summary")
                [ "A1:C1" ]
                "the merged header range must reach the worksheet"
        }

        test "column widths lower to 1-based col declarations" {
            Expect.equal
                (Fixtures.columnDeclarations (Emit.toBytes (Fixtures.mixedKindWorkbook ())) "Summary")
                [ 1u, 1u, 24.0; 3u, 3u, 14.5 ]
                "widths keep their columns, converted from the model's zero-based index"
        }

        test "sheets keep their names and tab order" {
            let model =
                WorkbookModel.ofSheets [
                    Fixtures.sheet "First" [ RowModel.ofText [ "a" ] ]
                    Fixtures.sheet "Second" [ RowModel.ofText [ "b" ] ]
                    Fixtures.sheet "Third" [ RowModel.ofText [ "c" ] ]
                ]

            let bytes = Emit.toBytes model
            Expect.equal (Fixtures.sheetNames bytes) [ "First"; "Second"; "Third" ] "tab order is model order"

            for entry in
                [
                    "xl/worksheets/sheet1.xml"
                    "xl/worksheets/sheet2.xml"
                    "xl/worksheets/sheet3.xml"
                ] do
                Expect.contains (entryNames bytes) entry (sprintf "the package must carry %s" entry)
        }
    ]

let private refusalCases =
    testList "refusal" [
        test "tryToBytes returns the validation failures rather than a file" {
            let model =
                WorkbookModel.ofSheets [
                    {
                        Fixtures.sheet "Data" [] with
                            Name = "Bad/Name"
                    }
                ]

            match Emit.tryToBytes model with
            | Ok _ -> failtest "an invalid model must not emit"
            | Error errors ->
                Expect.equal
                    errors
                    [ InvalidSheetName("Bad/Name", SheetNameIllegalCharacters [ '/' ]) ]
                    "the failure is returned as data"
        }

        test "toBytes raises, naming every failure" {
            let raised =
                try
                    Emit.toBytes WorkbookModel.empty |> ignore
                    None
                with :? InvalidOperationException as ex ->
                    Some ex

            match raised with
            | None -> failtest "an invalid model must not emit silently"
            | Some ex -> Expect.stringContains ex.Message "no sheets" "the message must name what is wrong"
        }

        test "toStream writes the same bytes toBytes produces" {
            let model = Fixtures.mixedKindWorkbook ()
            use stream = new MemoryStream()
            Emit.toStream model stream
            Expect.equal (stream.ToArray()) (Emit.toBytes model) "the stream and byte entry points must agree"
        }

        test "tryToStream reports failures without writing" {
            use stream = new MemoryStream()

            match Emit.tryToStream WorkbookModel.empty stream with
            | Ok() -> failtest "an invalid model must not be written"
            | Error errors -> Expect.equal errors [ EmptyWorkbook ] "the failure is returned as data"

            Expect.equal stream.Length 0L "nothing may be written for a refused model"
        }
    ]

let private validityCases =
    // "Reopens cleanly in Excel" is the acceptance criterion and cannot
    // be run in CI. The SDK's own schema validator is the closest
    // machine-checkable proxy: it is the same part / element / attribute
    // ruleset Excel's repair pass applies, and a workbook that validates
    // clean is one Excel opens without a repair prompt. Run over the
    // WHOLE package, so the styles scaffolding Excel requires but never
    // varies (fonts / fills / borders / cellStyleXfs) is checked too.
    let validate (bytes: byte[]) =
        use stream = new MemoryStream(bytes)
        use document = Package.openRead stream

        DocumentFormat.OpenXml.Validation
            .OpenXmlValidator(DocumentFormat.OpenXml.FileFormatVersions.Office2019)
            .Validate
            document
        |> Seq.map (fun error -> sprintf "%A: %s" error.ErrorType error.Description)
        |> List.ofSeq

    testList "schema validity" [
        test "the mixed-kind workbook validates clean" {
            Expect.isEmpty
                (validate (Emit.toBytes (Fixtures.mixedKindWorkbook ())))
                "an emitted workbook must carry no schema violations"
        }

        test "a format-free single-cell workbook validates clean" {
            let bytes =
                Fixtures.sheet "Data" [ RowModel.ofText [ "only" ] ]
                |> List.singleton
                |> WorkbookModel.ofSheets
                |> Emit.toBytes

            Expect.isEmpty (validate bytes) "the minimal workbook must validate too"
        }

        test "a multi-sheet workbook validates clean" {
            let bytes =
                WorkbookModel.ofSheets [
                    Fixtures.sheet "First" [ RowModel.ofCells [ CellModel.number 1.0 ] ]
                    Fixtures.sheet "Second" [ RowModel.ofCells [ CellModel.boolean false ] ]
                ]
                |> Emit.toBytes

            Expect.isEmpty (validate bytes) "several worksheet parts must validate together"
        }
    ]

let tests =
    testList "Phase 574.B — SpreadsheetML emission" [ determinismCases; partCases; refusalCases; validityCases ]