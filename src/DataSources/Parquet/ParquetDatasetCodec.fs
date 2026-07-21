// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.DataSources.Parquet

open System
open System.IO
open System.Text.Json
open Parquet
open Parquet.Schema
open ToolUp.Platform
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 598 — ParquetDatasetCodec ────────────────────────────────────
//
// The Parquet companion implementation of the `IDatasetCodec` seam (Phase
// 448): encodes a typed dataset vintage as **native Parquet**, so a
// deployment composing it via `BlobDatasetStore.createWithCodec` hands
// external compute workers a blob any Python / R Parquet reader parses with
// no ToolUp code (plan D7). The BCL `JsonFrameDatasetCodec` stays the
// default composition (GP 13); this companion isolates the `Parquet.Net`
// vendor dependency behind the seam (GP 1) and is the seam's second
// implementation (GP 12).
//
// **Column mapping.** `Float` → double, `Int` → int64, `Bool` → bool,
// `Text` / `Categorical` → UTF-8 string, `Timestamp` → Parquet timestamp
// (micros, UTC-adjusted). Nullability maps to Parquet optional columns.
// `Text` vs `Categorical` (and the column `Role`s) are not distinguishable
// from the Parquet physical schema alone, so the full declared
// `DatasetSchema` travels in the file's custom key/value metadata under
// `toolup.dataset.schema`; `Decode` verifies the physical schema against it
// and refuses on any mismatch (a typed refusal the store lifts to
// `DatasetError.StorageFailure`).
//
// **Precision contract (GP 12 rule 6).** Parquet timestamps are instants:
// a `DatasetValue.Timestamp`'s UTC instant round-trips at microsecond
// precision; the original offset is not preserved (an offset-carrying value
// re-reads as the same instant at offset zero — `DateTimeOffset` equality,
// which compares instants, is preserved). Sub-microsecond ticks truncate.
// Documented in this companion's README.

module private ParquetMapping =

    /// The custom key/value metadata key carrying the declared
    /// `DatasetSchema` JSON (the same `FableConverters` STJ wire the SDK
    /// uses for non-Remoting JSON).
    [<Literal>]
    let SchemaMetadataKey = "toolup.dataset.schema"

    let jsonOptions = FableConverters.create ()

    /// The Parquet field for one declared dataset column.
    let toParquetField (col: DatasetColumn) : Field =
        match col.DType with
        | DatasetDType.Float -> DataField(col.Name, typeof<float>, isNullable = col.Nullable) :> Field
        | DatasetDType.Int -> DataField(col.Name, typeof<int64>, isNullable = col.Nullable) :> Field
        | DatasetDType.Bool -> DataField(col.Name, typeof<bool>, isNullable = col.Nullable) :> Field
        | DatasetDType.Text
        | DatasetDType.Categorical ->
            // Parquet.Net strings are always physically optional; declared
            // non-nullability is enforced by the codec on decode, not by the
            // physical schema.
            DataField(col.Name, typeof<string>, isNullable = true) :> Field
        | DatasetDType.Timestamp ->
            DateTimeDataField(col.Name, DateTimeFormat.DateAndTimeMicros, isNullable = col.Nullable) :> Field

    /// The CLR value type a declared dtype maps to in the physical schema.
    let expectedClrType (dtype: DatasetDType) : Type =
        match dtype with
        | DatasetDType.Float -> typeof<float>
        | DatasetDType.Int -> typeof<int64>
        | DatasetDType.Bool -> typeof<bool>
        | DatasetDType.Text
        | DatasetDType.Categorical -> typeof<string>
        | DatasetDType.Timestamp -> typeof<DateTime>

    /// Cell extraction failure — raised inside `Encode` (whose seam
    /// contract is "throws only on caller error"; the store validates rows
    /// before encoding, so this is defensive).
    let private badCell (col: DatasetColumn) (v: DatasetValue) =
        failwith $"cell for column '{col.Name}' does not fit its declared dtype: %A{v}"

    let private cellsOf (colIndex: int) (rows: DatasetRow list) =
        rows |> List.map (fun r -> List.item colIndex r.Cells)

    /// Write one declared column's cells into the row group with the
    /// dtype-appropriate `WriteAsync` overload.
    let writeColumn
        (rowGroup: ParquetRowGroupWriter)
        (field: DataField)
        (col: DatasetColumn)
        (colIndex: int)
        (rows: DatasetRow list)
        =
        task {
            let cells = cellsOf colIndex rows

            match col.DType with
            | DatasetDType.Float ->
                if col.Nullable then
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Float f -> Nullable f
                            | DatasetValue.Null -> Nullable()
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
                else
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Float f -> f
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
            | DatasetDType.Int ->
                if col.Nullable then
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Int i -> Nullable i
                            | DatasetValue.Null -> Nullable()
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
                else
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Int i -> i
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
            | DatasetDType.Bool ->
                if col.Nullable then
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Bool b -> Nullable b
                            | DatasetValue.Null -> Nullable()
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
                else
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Bool b -> b
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
            | DatasetDType.Text ->
                let arr: string[] =
                    cells
                    |> List.map (fun v ->
                        match v with
                        | DatasetValue.Text s -> s
                        | DatasetValue.Null when col.Nullable -> null
                        | other -> badCell col other)
                    |> List.toArray

                do! rowGroup.WriteAsync(field, (arr :> System.Collections.Generic.IReadOnlyCollection<string>))
            | DatasetDType.Categorical ->
                let arr: string[] =
                    cells
                    |> List.map (fun v ->
                        match v with
                        | DatasetValue.Categorical s -> s
                        | DatasetValue.Null when col.Nullable -> null
                        | other -> badCell col other)
                    |> List.toArray

                do! rowGroup.WriteAsync(field, (arr :> System.Collections.Generic.IReadOnlyCollection<string>))
            | DatasetDType.Timestamp ->
                if col.Nullable then
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Timestamp t -> Nullable t.UtcDateTime
                            | DatasetValue.Null -> Nullable()
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
                else
                    let arr =
                        cells
                        |> List.map (fun v ->
                            match v with
                            | DatasetValue.Timestamp t -> t.UtcDateTime
                            | other -> badCell col other)
                        |> List.toArray

                    do! rowGroup.WriteAsync(field, ReadOnlyMemory arr)
        }

    /// A decoded UTC timestamp cell.
    let private timestampValue (dt: DateTime) =
        DatasetValue.Timestamp(DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)))

    /// Read one declared column's cells from the row group with the
    /// dtype-appropriate `ReadAsync` overload. `Error` on a null cell in a
    /// declared-non-nullable column.
    let readColumn
        (rowGroup: ParquetRowGroupReader)
        (field: DataField)
        (col: DatasetColumn)
        (rowCount: int)
        : Threading.Tasks.Task<Result<DatasetValue[], string>> =
        task {
            let nullRefused () =
                Error $"column '{col.Name}' is declared non-nullable but the content has a null cell"

            match col.DType with
            | DatasetDType.Float ->
                if col.Nullable then
                    let arr = Array.zeroCreate<Nullable<float>> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)

                    return
                        Ok(
                            arr
                            |> Array.map (fun v ->
                                if v.HasValue then
                                    DatasetValue.Float v.Value
                                else
                                    DatasetValue.Null)
                        )
                else
                    let arr = Array.zeroCreate<float> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)
                    return Ok(arr |> Array.map DatasetValue.Float)
            | DatasetDType.Int ->
                if col.Nullable then
                    let arr = Array.zeroCreate<Nullable<int64>> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)

                    return
                        Ok(
                            arr
                            |> Array.map (fun v ->
                                if v.HasValue then
                                    DatasetValue.Int v.Value
                                else
                                    DatasetValue.Null)
                        )
                else
                    let arr = Array.zeroCreate<int64> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)
                    return Ok(arr |> Array.map DatasetValue.Int)
            | DatasetDType.Bool ->
                if col.Nullable then
                    let arr = Array.zeroCreate<Nullable<bool>> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)

                    return
                        Ok(
                            arr
                            |> Array.map (fun v ->
                                if v.HasValue then
                                    DatasetValue.Bool v.Value
                                else
                                    DatasetValue.Null)
                        )
                else
                    let arr = Array.zeroCreate<bool> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)
                    return Ok(arr |> Array.map DatasetValue.Bool)
            | DatasetDType.Text ->
                let arr = Array.zeroCreate<string> rowCount
                do! rowGroup.ReadAsync(field, Memory arr)

                if not col.Nullable && arr |> Array.exists isNull then
                    return nullRefused ()
                else
                    return
                        Ok(
                            arr
                            |> Array.map (fun s -> if isNull s then DatasetValue.Null else DatasetValue.Text s)
                        )
            | DatasetDType.Categorical ->
                let arr = Array.zeroCreate<string> rowCount
                do! rowGroup.ReadAsync(field, Memory arr)

                if not col.Nullable && arr |> Array.exists isNull then
                    return nullRefused ()
                else
                    return
                        Ok(
                            arr
                            |> Array.map (fun s ->
                                if isNull s then
                                    DatasetValue.Null
                                else
                                    DatasetValue.Categorical s)
                        )
            | DatasetDType.Timestamp ->
                if col.Nullable then
                    let arr = Array.zeroCreate<Nullable<DateTime>> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)

                    return
                        Ok(
                            arr
                            |> Array.map (fun v ->
                                if v.HasValue then
                                    timestampValue v.Value
                                else
                                    DatasetValue.Null)
                        )
                else
                    let arr = Array.zeroCreate<DateTime> rowCount
                    do! rowGroup.ReadAsync(field, Memory arr)
                    return Ok(arr |> Array.map timestampValue)
        }

    /// Verify the physical Parquet schema matches the declared dataset
    /// schema: same column count, names, and per-dtype physical type, in
    /// order. Returns the first mismatch reason.
    let verifyPhysicalSchema (declared: DatasetSchema) (physical: DataField[]) : Result<unit, string> =
        if physical.Length <> List.length declared.Columns then
            Error
                $"content has {physical.Length} columns; the declared schema has {List.length declared.Columns} — schema mismatch"
        else
            List.zip declared.Columns (List.ofArray physical)
            |> List.tryPick (fun (col, field) ->
                if field.Name <> col.Name then
                    Some $"content column '{field.Name}' does not match declared column '{col.Name}'"
                elif field.ClrType <> expectedClrType col.DType then
                    Some
                        $"content column '{col.Name}' has physical type {field.ClrType.Name}; the declared dtype is {DatasetDType.name col.DType}"
                else
                    None)
            |> function
                | Some reason -> Error reason
                | None -> Ok()

/// Parquet implementation of the Phase 448 `IDatasetCodec` seam
/// (`Format = "parquet"`). Stateless and pure with respect to its inputs
/// (GP 12 rule 4). Compose via `BlobDatasetStore.createWithCodec`.
type ParquetDatasetCodec() =

    interface IDatasetCodec with
        member _.Format = "parquet"

        member _.Encode(schema: DatasetSchema, rows: DatasetRow list) : byte[] =
            let fields = schema.Columns |> List.map ParquetMapping.toParquetField
            let parquetSchema = ParquetSchema(fields |> List.toArray)

            use stream = new MemoryStream()

            let write = task {
                use! writer = ParquetWriter.CreateAsync(parquetSchema, stream)

                writer.CustomMetadata <-
                    System.Collections.Generic.Dictionary(
                        dict [
                            ParquetMapping.SchemaMetadataKey,
                            JsonSerializer.Serialize(schema, ParquetMapping.jsonOptions)
                        ]
                    )

                use rowGroup = writer.CreateRowGroup()

                for i, col in List.indexed schema.Columns do
                    do! ParquetMapping.writeColumn rowGroup parquetSchema.DataFields[i] col i rows
            }

            write.GetAwaiter().GetResult()
            stream.ToArray()

        member _.Decode(content: byte[]) : Result<DatasetSchema * DatasetRow list, string> =
            try
                let read = task {
                    use stream = new MemoryStream(content)
                    use! reader = ParquetReader.CreateAsync(stream)

                    // The declared schema travels in the custom metadata; its
                    // absence means the bytes are not a ToolUp-tagged Parquet
                    // frame.
                    match reader.CustomMetadata.TryGetValue ParquetMapping.SchemaMetadataKey with
                    | false, _ -> return Error "parquet content carries no declared dataset schema metadata"
                    | true, json ->
                        let declared =
                            JsonSerializer.Deserialize<DatasetSchema>(json, ParquetMapping.jsonOptions)

                        match ParquetMapping.verifyPhysicalSchema declared reader.Schema.DataFields with
                        | Error reason -> return Error reason
                        | Ok() ->
                            let rows = ResizeArray<DatasetRow>()
                            let mutable failure = None

                            for groupIndex in 0 .. reader.RowGroupCount - 1 do
                                if Option.isNone failure then
                                    use rowGroup = reader.OpenRowGroupReader groupIndex
                                    let rowCount = int rowGroup.RowCount

                                    let columns =
                                        Array.zeroCreate<Result<DatasetValue[], string>> (List.length declared.Columns)

                                    for c, col in List.indexed declared.Columns do
                                        let! decoded =
                                            ParquetMapping.readColumn rowGroup reader.Schema.DataFields[c] col rowCount

                                        columns[c] <- decoded

                                    match
                                        columns
                                        |> Array.tryPick (fun r ->
                                            match r with
                                            | Error e -> Some e
                                            | Ok _ -> None)
                                    with
                                    | Some e -> failure <- Some e
                                    | None ->
                                        let decoded =
                                            columns
                                            |> Array.map (fun r ->
                                                match r with
                                                | Ok arr -> arr
                                                | Error _ -> [||])

                                        for r in 0 .. rowCount - 1 do
                                            rows.Add {
                                                Cells = decoded |> Array.toList |> List.map (fun c -> c[r])
                                            }

                            match failure with
                            | Some reason -> return Error reason
                            | None -> return Ok(declared, List.ofSeq rows)
                }

                read.GetAwaiter().GetResult()
            with ex ->
                Error ex.Message