// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Tabular.Tests.InProcess.XlsmTests

open System
open System.IO
open System.IO.Compression
open System.Text
open Expecto
open ToolUp.Tabular
open ToolUp.Tabular.Tests.Fixtures

// ─── Phase 639 — macro-enabled workbooks read as plain workbooks ─
//
// The whole leg is a claim of EQUIVALENCE, so the tests are written
// as equivalence: build the same grid twice, once as `.xlsx` and
// once as `.xlsm` (macro part and all), and assert the two reads
// are indistinguishable — same rows, same cells, same errors, same
// counts. A test that only asserted "the xlsm read produced two
// rows" would still pass if the macro flavour silently dropped a
// column, which is precisely the drift this leg must not have.
//
// The last two cases guard the guard: they prove the two fixtures
// genuinely DIFFER as packages (macro part present, content type
// declared macro-enabled), so the parity above is a real result and
// not two identical files agreeing with each other.

/// The rows both flavours are built from — one of every cell shape
/// the reader handles (shared string, inline string, number, styled
/// date serial, boolean, sparse gap).
let private gridRows = [
    productHeader
    [ S "AB-0001"; I "Widget"; N 9.99; N 120.0; D serial20210101; S "Active" ]
    [ S "CD-0002"; S "Gadget"; N 1250.5; N 4.0; Gap; S "Preorder" ]
]

/// A grid where two cells fail validation, so the equivalence claim
/// covers the ERROR path as well as the happy path.
let private mixedRows = [
    productHeader
    [ S "AB-0001"; S "Widget"; N 9.99; N 120.0; D serial20210101; S "Active" ]
    [ S "no good"; S "Gadget"; S "free"; N 2.5; D serial20210101; S "Maybe" ]
]

let private readRows (bytes: byte[]) =
    use stream = new MemoryStream(bytes)

    Xlsx.readRows (SheetSelection.Index 0) stream |> Seq.toList

/// Part names inside the OPC (zip) container, without opening any
/// of them. Used only to characterise the fixtures.
let private partNames (bytes: byte[]) =
    use stream = new MemoryStream(bytes)
    use archive = new ZipArchive(stream, ZipArchiveMode.Read)

    archive.Entries |> Seq.map _.FullName |> Seq.toList

let private contentTypesXml (bytes: byte[]) =
    use stream = new MemoryStream(bytes)
    use archive = new ZipArchive(stream, ZipArchiveMode.Read)
    let entry = archive.GetEntry "[Content_Types].xml"
    use entryStream = entry.Open()
    use reader = new StreamReader(entryStream, Encoding.UTF8)

    reader.ReadToEnd()

let tests =
    testList "Xlsm" [
        testCase "macro-enabled workbook yields the identical raw grid"
        <| fun () ->
            let asXlsx = readRows (xlsxBytes "Products" gridRows)
            let asXlsm = readRows (xlsmBytes "Products" gridRows)

            // Sanity floor: an empty-vs-empty comparison would pass
            // vacuously, so pin that rows were actually read.
            Expect.equal asXlsx.Length 3 "three raw rows read from the xlsx"
            Expect.equal asXlsm asXlsx "xlsm raw grid is identical to the xlsx grid"

        testCase "macro-enabled workbook binds identically through TabularReader"
        <| fun () ->
            let fromXlsx =
                TabularReader.readXlsxBytes productSchema XlsxReadOptions.defaults (xlsxBytes "Products" gridRows)

            let fromXlsm =
                TabularReader.readXlsxBytes productSchema XlsxReadOptions.defaults (xlsmBytes "Products" gridRows)

            Expect.equal fromXlsx.Rows.Length 2 "two bound rows from the xlsx"
            Expect.isEmpty fromXlsm.CellErrors "no cell errors"
            Expect.isEmpty fromXlsm.RowErrors "no row errors"
            Expect.equal fromXlsm.Rows fromXlsx.Rows "bound rows identical"
            Expect.equal fromXlsm.TotalRows fromXlsx.TotalRows "row count identical"
            Expect.equal fromXlsm.Truncated fromXlsx.Truncated "truncation flag identical"

            // Spot-check the typed values so a mutual failure of both
            // legs cannot masquerade as agreement.
            let first = fromXlsm.Rows[0]
            Expect.equal first["Sku"] (TabularValue.Text "AB-0001") "shared string"
            Expect.equal first["Name"] (TabularValue.Text "Widget") "inline string"
            Expect.equal first["Price"] (TabularValue.Number 9.99m) "number"
            Expect.equal first["Stock"] (TabularValue.Integer 120L) "integer"
            Expect.equal first["Launched"] (TabularValue.Date(DateTime(2021, 1, 1))) "date serial via style"
            // Extracted, not chained: `rows[1]["Launched"]` on one line
            // is the documented Fantomas indexer-ambiguity trap — the
            // formatter inserts a space and it re-parses as a list
            // application (CLAUDE.md, "Fantomas pitfall").
            let second = fromXlsm.Rows[1]
            Expect.equal second["Launched"] TabularValue.Empty "sparse gap binds Empty"

        testCase "per-cell error reporting is identical across the two flavours"
        <| fun () ->
            let fromXlsx =
                TabularReader.readXlsxBytes productSchema XlsxReadOptions.defaults (xlsxBytes "Products" mixedRows)

            let fromXlsm =
                TabularReader.readXlsxBytes productSchema XlsxReadOptions.defaults (xlsmBytes "Products" mixedRows)

            Expect.isNonEmpty fromXlsx.CellErrors "the mixed fixture does fail cells"
            Expect.equal fromXlsm.CellErrors fromXlsx.CellErrors "cell errors identical (row, column, expected, actual)"
            Expect.equal fromXlsm.RowErrors fromXlsx.RowErrors "row errors identical"
            Expect.equal fromXlsm.Rows fromXlsx.Rows "surviving rows identical"

        testCase "sheet selection by name works on a macro-enabled workbook"
        <| fun () ->
            let options = {
                XlsxReadOptions.defaults with
                    Sheet = SheetSelection.Name "Products"
            }

            let result =
                TabularReader.readXlsxBytes productSchema options (xlsmBytes "Products" gridRows)

            Expect.isEmpty result.RowErrors "named sheet resolves"
            Expect.equal result.Rows.Length 2 "two rows"

        testCase "streaming surface is identical across the two flavours"
        <| fun () ->
            let stream (bytes: byte[]) =
                use source = new MemoryStream(bytes)

                TabularReader.streamXlsx productSchema XlsxReadOptions.defaults source
                |> Seq.toList

            let fromXlsx = stream (xlsxBytes "Products" mixedRows)
            let fromXlsm = stream (xlsmBytes "Products" mixedRows)

            Expect.isNonEmpty fromXlsx "outcomes were produced"
            Expect.equal fromXlsm fromXlsx "streamed outcomes identical"

        // ── The fixtures really are different packages ──────────────

        testCase "the xlsm fixture carries a vbaProject part the xlsx does not"
        <| fun () ->
            let xlsmParts = partNames (xlsmBytes "Products" gridRows)
            let xlsxParts = partNames (xlsxBytes "Products" gridRows)

            Expect.isTrue
                (xlsmParts
                 |> List.exists (fun name -> name.EndsWith("vbaProject.bin", StringComparison.Ordinal)))
                "the macro-enabled package carries a vbaProject.bin part"

            Expect.isFalse
                (xlsxParts
                 |> List.exists (fun name -> name.EndsWith("vbaProject.bin", StringComparison.Ordinal)))
                "the plain package does not"

        testCase "the xlsm fixture declares the macro-enabled workbook content type"
        <| fun () ->
            let xlsmTypes = contentTypesXml (xlsmBytes "Products" gridRows)
            let xlsxTypes = contentTypesXml (xlsxBytes "Products" gridRows)

            Expect.stringContains
                xlsmTypes
                "application/vnd.ms-excel.sheet.macroEnabled.main+xml"
                "macro-enabled workbook part content type"

            Expect.stringContains
                xlsxTypes
                "spreadsheetml.sheet.main+xml"
                "the plain fixture declares the ordinary workbook part content type"
    ]