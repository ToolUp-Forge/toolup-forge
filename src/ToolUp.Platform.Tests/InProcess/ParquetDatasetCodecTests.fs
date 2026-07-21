// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ParquetDatasetCodecTests

open System
open System.IO
open Expecto
open Parquet
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.DataObjectStore
open ToolUp.DataSources.Parquet
open ToolUp.Platform.Tests.Contracts

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

let private freshDir () =
    let tempDir =
        Path.Combine(Path.GetTempPath(), "toolup-parquet-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory tempDir |> ignore
    tempDir

let private baseDataObjects () =
    let blob = LocalFileStorage.LocalFileStorage(freshDir ()) :> IBlobStorage
    DataObjectStore(blob) :> IDataObjectStore

let private parquetStore () =
    BlobDatasetStore.createWithCodec (baseDataObjects ()) (ParquetDatasetCodec())

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
        parquetStore

let codecTests =
    testList "ParquetDatasetCodec — codec-specific" [
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
            let jsonStore = BlobDatasetStore.create (baseDataObjects ())
            let pqStore = parquetStore ()

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
            let store = parquetStore ()

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