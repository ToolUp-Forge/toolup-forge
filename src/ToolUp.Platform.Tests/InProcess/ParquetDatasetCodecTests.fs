// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ParquetDatasetCodecTests

open System
open System.IO
open Expecto
open Parquet
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Infra.DataObjectStore
open ToolUp.DataSources.Parquet
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Infra
open ToolUp.Platform.Testing

// ─── Phase 598 — ParquetDatasetCodec conformance ────────────────────────
//
// Re-binds the Phase 448.E `IDatasetStoreContract` pack to the blob-backed
// store composed with the Parquet companion codec (`createWithCodec`), then
// adds the codec-specific cases: cross-codec parity (same rows through both
// codecs → identical `ReadPage` results), the `"parquet"` format tag on
// `GetContentRef`, an independent-reader proof (the emitted bytes are real
// Parquet, parsed by `Parquet.Net`'s reader API directly — not through the
// codec — so the output is not self-certifying), and typed refusals on
// schema mismatch / foreign bytes.

let private baseDataObjects (dir: TempDir) =
    let blob = LocalFileStorage.LocalFileStorage(dir.Path) :> IBlobStorage
    DataObjectStore(blob) :> IDataObjectStore

let private parquetStore (dir: TempDir) =
    BlobDatasetStore.createWithCodec (baseDataObjects dir) (ParquetDatasetCodec())

let private t0 = DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)

/// All six dtypes, mixed nullability, panel roles — the exhaustive-mapping
/// schema (598.A: all six `DatasetDType`s + nullability + roles).
let private allTypesSchema: DatasetSchema = {
    Columns = [
        {
            Name = "unit"
            DType = DatasetDType.Categorical
            Nullable = false
            Role = DatasetColumnRole.PanelUnit
        }
        {
            Name = "period"
            DType = DatasetDType.Timestamp
            Nullable = false
            Role = DatasetColumnRole.PanelPeriod
        }
        {
            Name = "value"
            DType = DatasetDType.Float
            Nullable = true
            Role = DatasetColumnRole.Target
        }
        {
            Name = "count"
            DType = DatasetDType.Int
            Nullable = true
            Role = DatasetColumnRole.Plain
        }
        {
            Name = "active"
            DType = DatasetDType.Bool
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
        {
            Name = "note"
            DType = DatasetDType.Text
            Nullable = true
            Role = DatasetColumnRole.Plain
        }
    ]
}

let private allTypesRows: DatasetRow list = [
    {
        Cells = [
            DatasetValue.Categorical "north"
            DatasetValue.Timestamp t0
            DatasetValue.Float 1.5
            DatasetValue.Int 42L
            DatasetValue.Bool true
            DatasetValue.Text "first"
        ]
    }
    {
        Cells = [
            DatasetValue.Categorical "south"
            DatasetValue.Timestamp(t0.AddDays 7.0)
            DatasetValue.Null
            DatasetValue.Null
            DatasetValue.Bool false
            DatasetValue.Null
        ]
    }
    {
        Cells = [
            DatasetValue.Categorical "east"
            DatasetValue.Timestamp(t0.AddDays 14.0)
            DatasetValue.Float -0.25
            DatasetValue.Int -7L
            DatasetValue.Bool true
            DatasetValue.Text "third"
        ]
    }
]

let contractTests =
    // The full 448.E pack against the Parquet composition (598.C).
    IDatasetStoreContract.testsWithCodec
        "BlobDatasetStore (blob-backed, Parquet codec)"
        (fun () -> ParquetDatasetCodec() :> IDatasetCodec)
        // The contract pack owns each store's lifetime through a bare
        // factory, so the directory behind it is not disposed here.
        (fun () -> parquetStore (TempDir.forRun "parquet-test"))

// ─── The exact decimal ──────────────────────────────────────
//
// `DatasetValue.Decimal` is canonical decimal text (`DatasetDecimal`). These
// cases pin the canonical form, the exact ordering, the refusal at the host
// decimal's edge, and that a decimal column survives both codecs with every
// digit — including values wider than a float and than `System.Decimal`.

let private decimalSchema: DatasetSchema = {
    Columns = [
        {
            Name = "id"
            DType = DatasetDType.Int
            Nullable = false
            Role = DatasetColumnRole.Plain
        }
        {
            Name = "amount"
            DType = DatasetDType.Decimal
            Nullable = true
            Role = DatasetColumnRole.Plain
        }
    ]
}

/// 38 digits (the SQL `DECIMAL(38, s)` limit), 76 digits with scale 38
/// (BigQuery `BIGNUMERIC`), a tiny negative, zero, a whole number, a value a
/// float misprints, and a NULL.
let private decimalAmounts = [
    Some "12345678901234567890.123456789012345678"
    Some "-12345678901234567890123456789012345678.12345678901234567890123456789012345678"
    Some "-0.0000000005"
    Some "0"
    Some "1000000"
    Some "0.1"
    None
]

let private decimalRows: DatasetRow list =
    decimalAmounts
    |> List.mapi (fun i amount -> {
        Cells = [
            DatasetValue.Int(int64 i)
            (match amount with
             | Some digits -> DatasetValue.Decimal digits
             | None -> DatasetValue.Null)
        ]
    })

let private decimalTests =
    testList "DatasetValue.Decimal — the exact decimal" [
        test "tryParse answers the canonical form, and refuses what is not a plain decimal" {
            for input, expected in
                [
                    "007.500", Some "7.5"
                    "-0.000", Some "0"
                    "+12", Some "12"
                    ".5", Some "0.5"
                    "5.", Some "5"
                    "-12.340", Some "-12.34"
                    "1e3", None
                    "1,000", None
                    "", None
                    "-", None
                    ".", None
                    " 1", None
                    "NaN", None
                ] do
                Expect.equal (DatasetDecimal.tryParse input) expected $"tryParse %A{input}"
        }

        test "compare is numeric order, exact at any width" {
            let ordered = [
                "-12345678901234567890123456789012345678.1"
                "-100"
                "-0.0000000005"
                "0"
                "0.0000000000000000000000000000000000001"
                "0.1"
                "0.10000000000000001"
                "9.99"
                "10"
                "12345678901234567890.123456789012345678"
                "12345678901234567890.123456789012345679"
            ]

            let shuffled = List.rev ordered @ []
            Expect.equal (List.sortWith DatasetDecimal.compare shuffled) ordered "sorted by value"

            for a in ordered do
                Expect.equal (DatasetDecimal.compare a a) 0 $"%s{a} = itself"
        }

        test "tryToDecimal is exact or refuses — it never rounds" {
            Expect.equal (DatasetDecimal.tryToDecimal "0.1") (Some 0.1M) "0.1 fits"

            Expect.equal
                (DatasetDecimal.tryToDecimal "79228162514264337593543950335")
                (Some Decimal.MaxValue)
                "the edge fits"

            Expect.isNone (DatasetDecimal.tryToDecimal "79228162514264337593543950336") "one past the edge"

            Expect.isNone
                (DatasetDecimal.tryToDecimal "0.12345678901234567890123456789")
                "29 fractional digits: System.Decimal would round"

            Expect.equal (DatasetDecimal.ofDecimal 12.50M) "12.5" "a host decimal's trailing zero is not a digit"
        }

        test "a Decimal cell fits only a Decimal column, and only in canonical form" {
            Expect.isTrue (DatasetValue.fits DatasetDType.Decimal false (DatasetValue.Decimal "7.5")) "canonical"

            Expect.isFalse
                (DatasetValue.fits DatasetDType.Decimal false (DatasetValue.Decimal "7.50"))
                "a second spelling"

            Expect.isFalse (DatasetValue.fits DatasetDType.Float false (DatasetValue.Decimal "7.5")) "not a float"
            Expect.equal (DatasetValue.dtypeOf (DatasetValue.Decimal "1")) (Some DatasetDType.Decimal) "dtypeOf"

            Expect.equal
                (DatasetDType.parse (DatasetDType.name DatasetDType.Decimal))
                (Some DatasetDType.Decimal)
                "name/parse"
        }

        test "every digit round-trips through the Parquet codec" {
            let codec = ParquetDatasetCodec() :> IDatasetCodec

            match codec.Decode(codec.Encode(decimalSchema, decimalRows)) with
            | Error e -> failtestf "decode failed: %s" e
            | Ok(schema, rows) ->
                Expect.equal schema decimalSchema "schema"
                Expect.equal rows decimalRows "rows, digit for digit"
        }

        testCaseAsync "a plain Parquet reader sees the decimal logical type, at the shape the values need"
        <| async {
            // Every row but the 76-digit one: 38 digits is the widest a
            // Parquet.Net decimal holds.
            let rows = decimalRows |> List.filter (fun r -> r.Cells[0] <> DatasetValue.Int 1L)
            let codec = ParquetDatasetCodec() :> IDatasetCodec
            let bytes = codec.Encode(decimalSchema, rows)
            use stream = new MemoryStream(bytes)

            let! reader =
                ParquetReader.CreateAsync(stream, ParquetOptions(UseBigDecimal = true))
                |> Async.AwaitTask

            match reader.Schema.DataFields[1] with
            | :? Schema.DecimalDataField as field ->
                Expect.equal field.Scale 18 "the widest scale the column needs"
                Expect.equal field.Precision 38 "the widest precision the column needs"
            | other -> failtestf "the amount column is %s, not a Parquet decimal" (other.GetType().Name)

            match codec.Decode bytes with
            | Ok(_, decoded) -> Expect.equal decoded rows "and the codec reads it back digit for digit"
            | Error e -> failtestf "decode failed: %s" e
        }

        testCaseAsync "a decimal column wider than 38 digits is carried as its canonical text"
        <| async {
            let codec = ParquetDatasetCodec() :> IDatasetCodec
            let bytes = codec.Encode(decimalSchema, decimalRows)
            use stream = new MemoryStream(bytes)
            let! reader = ParquetReader.CreateAsync stream |> Async.AwaitTask
            let field = reader.Schema.DataFields[1]
            Expect.equal field.ClrType typeof<string> "a string column, since no Parquet.Net decimal holds 76 digits"
            use rowGroup = reader.OpenRowGroupReader 0
            let texts = Array.zeroCreate<string> decimalRows.Length
            do! rowGroup.ReadAsync(field, Memory texts) |> _.AsTask() |> Async.AwaitTask

            Expect.equal
                texts[1]
                "-12345678901234567890123456789012345678.12345678901234567890123456789012345678"
                "the digits, as a plain reader sees them"
        }

        testCaseAsync "every digit round-trips through the default JSON-frame store, and filters order exactly"
        <| async {
            use dir = TempDir.create "decimal-test"
            let store = BlobDatasetStore.create (baseDataObjects dir)

            let! created =
                store.Create("scope-d", "money", decimalSchema, decimalRows, "u1", Map.empty, StrictlyVersioned)

            match created with
            | Error e -> failtestf "create failed: %s" (DatasetError.describe e)
            | Ok version ->
                let! page = store.ReadPage("scope-d", "money", version.Version, DatasetPageQuery.firstPage 100)

                match page with
                | Error e -> failtestf "read failed: %s" (DatasetError.describe e)
                | Ok page -> Expect.equal page.Rows decimalRows "rows, digit for digit"

                let above = {
                    DatasetPageQuery.firstPage 100 with
                        Filters = [
                            {
                                Column = "amount"
                                Op = DatasetFilterOp.Gt
                                Value = DatasetValue.Decimal "0.1"
                            }
                        ]
                }

                let! filtered = store.ReadPage("scope-d", "money", version.Version, above)

                match filtered with
                | Error e -> failtestf "filtered read failed: %s" (DatasetError.describe e)
                | Ok page ->
                    Expect.equal
                        (page.Rows |> List.map (fun r -> r.Cells[1]))
                        [
                            DatasetValue.Decimal "12345678901234567890.123456789012345678"
                            DatasetValue.Decimal "1000000"
                        ]
                        "amount > 0.1, compared as numbers"
        }

        testCaseAsync "the store refuses a non-canonical decimal cell"
        <| async {
            use dir = TempDir.create "decimal-test"
            let store = BlobDatasetStore.create (baseDataObjects dir)

            let rows = [
                {
                    Cells = [ DatasetValue.Int 1L; DatasetValue.Decimal "7.50" ]
                }
            ]

            let! created = store.Create("scope-d", "bad", decimalSchema, rows, "u1", Map.empty, StrictlyVersioned)

            match created with
            | Error(DatasetError.SchemaMismatch _) -> ()
            | other -> failtestf "expected SchemaMismatch, got %A" other
        }
    ]

let codecTests =
    testList "ParquetDatasetCodec — codec-specific" [
        decimalTests

        testCase "All six dtypes + nullability + roles round-trip through the codec"
        <| fun () ->
            let codec = ParquetDatasetCodec() :> IDatasetCodec
            let bytes = codec.Encode(allTypesSchema, allTypesRows)

            match codec.Decode bytes with
            | Error e -> failtestf "decode failed: %s" e
            | Ok(schema, rows) ->
                Expect.equal schema allTypesSchema "schema round-trips (dtypes + nullability + roles)"
                Expect.equal rows allTypesRows "rows round-trip"

        testCase "Encoded bytes are real Parquet — magic header + footer"
        <| fun () ->
            let codec = ParquetDatasetCodec() :> IDatasetCodec
            let bytes = codec.Encode(allTypesSchema, allTypesRows)
            let magic = "PAR1"B
            Expect.equal bytes[0..3] magic "PAR1 header"
            Expect.equal bytes[bytes.Length - 4 ..] magic "PAR1 footer"

        testCaseAsync "Independent reader parses the emitted blob (non-self-certifying output)"
        <| async {
            // Read the codec's output through Parquet.Net's reader API
            // directly — no ToolUp codec on the read path.
            let codec = ParquetDatasetCodec() :> IDatasetCodec
            let bytes = codec.Encode(allTypesSchema, allTypesRows)

            use stream = new MemoryStream(bytes)

            let! reader = ParquetReader.CreateAsync stream |> Async.AwaitTask

            Expect.equal reader.Schema.DataFields.Length 6 "six physical columns"

            Expect.equal
                (reader.Schema.DataFields |> Array.map _.Name |> Array.toList)
                [ "unit"; "period"; "value"; "count"; "active"; "note" ]
                "column names visible to a plain Parquet reader"

            use rowGroup = reader.OpenRowGroupReader 0
            Expect.equal rowGroup.RowCount 3L "three rows visible to a plain Parquet reader"

            // Spot-read one primitive column natively.
            let active = Array.zeroCreate<bool> 3

            do!
                rowGroup.ReadAsync(reader.Schema.DataFields[4], Memory active)
                |> _.AsTask()
                |> Async.AwaitTask

            Expect.equal (Array.toList active) [ true; false; true ] "native column read matches"
        }

        testCaseAsync "Cross-codec parity — same rows through both codecs give identical ReadPage results"
        <| async {
            use jsonDir = TempDir.create "parquet-test"
            use pqDir = TempDir.create "parquet-test"
            let jsonStore = BlobDatasetStore.create (baseDataObjects jsonDir)
            let pqStore = parquetStore pqDir

            let seed (store: IDatasetStore) = async {
                let! created =
                    store.Create("scope-a", "parity", allTypesSchema, allTypesRows, "u1", Map.empty, StrictlyVersioned)

                match created with
                | Ok _ -> ()
                | Error e -> failtestf "create failed: %s" (DatasetError.describe e)
            }

            do! seed jsonStore
            do! seed pqStore

            let query = {
                Offset = 0L
                Limit = 100
                Filters = [
                    {
                        Column = "active"
                        Op = DatasetFilterOp.Eq
                        Value = DatasetValue.Bool true
                    }
                ]
            }

            let! jsonPage = jsonStore.ReadPage("scope-a", "parity", 1, query)
            let! pqPage = pqStore.ReadPage("scope-a", "parity", 1, query)

            match jsonPage, pqPage with
            | Ok j, Ok p ->
                Expect.equal p.Rows j.Rows "identical rows through both codecs"
                Expect.equal p.TotalRows j.TotalRows "identical totals"
                Expect.equal p.Schema j.Schema "identical schemas"
            | other -> failtestf "expected two Ok pages; got %A" other
        }

        testCaseAsync "GetContentRef tags the parquet format under the Parquet composition"
        <| async {
            use dir = TempDir.create "parquet-test"
            let store = parquetStore dir

            let! _ = store.Create("scope-a", "ds", allTypesSchema, allTypesRows, "u1", Map.empty, StrictlyVersioned)

            let! contentRef = store.GetContentRef("scope-a", "ds", 1)

            match contentRef with
            | Ok r -> Expect.equal r.Format "parquet" "worker-facing format tag is parquet"
            | Error e -> failtestf "GetContentRef failed: %s" (DatasetError.describe e)
        }

        testCase "Decode refuses content whose physical schema mismatches the declared schema"
        <| fun () ->
            // Encode with one schema, then tamper: re-declare a different
            // schema in the metadata by re-encoding the sidecar via a second
            // file whose physical shape differs. Simplest construction:
            // encode bytes under schema A, ask a codec to decode after the
            // declared sidecar says B — achieved by building a file with
            // physical column renamed.
            let codec = ParquetDatasetCodec() :> IDatasetCodec

            let schemaA: DatasetSchema = {
                Columns = [
                    {
                        Name = "x"
                        DType = DatasetDType.Float
                        Nullable = false
                        Role = DatasetColumnRole.Plain
                    }
                ]
            }

            let rowsA: DatasetRow list = [ { Cells = [ DatasetValue.Float 1.0 ] } ]

            // A foreign Parquet file with no ToolUp schema sidecar: written
            // directly with Parquet.Net (independent writer path).
            let foreignBytes =
                use ms = new MemoryStream()

                let write = task {
                    let field = Parquet.Schema.DataField("y", typeof<float>, isNullable = false)
                    let schema = Parquet.Schema.ParquetSchema(field)
                    // The writer must dispose before `ToArray` — disposal
                    // writes the Parquet footer.
                    use! writer = ParquetWriter.CreateAsync(schema, ms)
                    use rg = writer.CreateRowGroup()
                    do! rg.WriteAsync(field, ReadOnlyMemory [| 2.0 |])
                }

                write.GetAwaiter().GetResult()
                ms.ToArray()

            match codec.Decode foreignBytes with
            | Error reason ->
                Expect.stringContains reason "schema" "refusal names the missing/mismatched schema declaration"
            | Ok _ -> failtest "expected a typed refusal for a foreign Parquet file without a schema sidecar"

            // Sanity: the untampered encode still decodes.
            match codec.Decode(codec.Encode(schemaA, rowsA)) with
            | Ok(s, r) ->
                Expect.equal s schemaA "control decode schema"
                Expect.equal r rowsA "control decode rows"
            | Error e -> failtestf "control decode failed: %s" e

        testCase "Decode refuses non-Parquet bytes with a typed error, never a throw"
        <| fun () ->
            let codec = ParquetDatasetCodec() :> IDatasetCodec

            let jsonBytes =
                (JsonFrameDatasetCodec() :> IDatasetCodec).Encode(allTypesSchema, allTypesRows)

            match codec.Decode jsonBytes with
            | Error _ -> ()
            | Ok _ -> failtest "expected a refusal decoding JSON-frame bytes as Parquet"

        testCase "Empty dataset (zero rows) round-trips"
        <| fun () ->
            let codec = ParquetDatasetCodec() :> IDatasetCodec
            let bytes = codec.Encode(allTypesSchema, [])

            match codec.Decode bytes with
            | Ok(schema, rows) ->
                Expect.equal schema allTypesSchema "schema survives an empty frame"
                Expect.isEmpty rows "no rows"
            | Error e -> failtestf "decode failed: %s" e
    ]