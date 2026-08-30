// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Parquet.ParquetDataSource

open System
open System.IO
open System.Text
open System.Threading.Tasks
open Parquet
open Parquet.Schema
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.DataSources.Common
open DataManagementTypes

module CsvWire = ToolUp.DataSources.Common.Csv

// ─── ToolUp.DataSources.Parquet — the IDataSource leg ─────────────
//
// `IDataSource` companion for Parquet files — "we got a Parquet
// extract from the warehouse", the one file shape a consumer receives
// precisely because someone upstream cared about types.
//
// **This file is the INGESTION leg; `ParquetDatasetCodec.fs` beside it
// is the STORAGE leg.** They share a package because they share the
// vendor dependency and the format, and nothing else: the codec
// encodes ToolUp's own typed dataset vintages for external compute to
// read, while this connector reads Parquet somebody ELSE wrote and
// knows nothing about ToolUp. Neither calls the other.
//
// **No schema inference, ever.** Parquet carries its own schema, so
// `GetSchema` reads the file's footer metadata and reports what the
// writer declared — including nullability, which the CSV and Excel
// connectors can only guess at from a sample. `sample_rows` is
// therefore inert here, and the README says so.
//
// **Row groups are read one at a time.** The connector never holds
// more than one row group's columns, which is the memory bound
// Parquet's format exists to offer. The honest caveat is that
// `IDataSource.Query` returns `byte[]` and `IBlobStorage.Download`
// yields `byte[]`, so the file's bytes and the emitted CSV are both
// materialised whatever this loop does — the bound is on the DECODED
// column data, which is the part that expands.

/// Parsed, validated view of one Parquet source's `ConnectionScope`.
type ParquetSourceSettings = {
    /// Container / prefix / extension, shared with the other file
    /// connectors. `SampleRows` is unused — Parquet declares its own
    /// schema.
    File: Files.FileSourceSettings
}

/// The `DataSourceConfig.Kind` this connector answers to.
[<Literal>]
let Kind = "Parquet"

[<Literal>]
let private DefaultExtension = ".parquet"

/// Read and validate one call's `ConnectionScope`. Pure.
let readSettings (scope: Map<string, string>) : Result<ParquetSourceSettings, IngestionError> =
    Files.readSettings DefaultExtension scope
    |> Result.map (fun file -> { File = file })

/// Dotted path of a field, which is how a nested Parquet schema
/// flattens: a `struct { customer { id, name } }` presents as
/// `customer.id` / `customer.name`. For a flat schema the path is the
/// name, so one rule covers both.
let fieldPath (field: DataField) : string =
    match box field.Path with
    | null -> field.Name
    | path ->
        match path.ToString() with
        | null -> field.Name
        | "" -> field.Name
        | rendered -> rendered

/// Project a Parquet field's CLR type onto the SDK's coarse
/// `ColumnType`. The RAW declared type is preserved on
/// `ColumnInfo.DataType`, exactly as the warehouse connectors preserve
/// their vendor spelling.
let toColumnType (clrType: Type) : ColumnType =
    if isNull clrType then
        StringColumn
    else
        let underlying =
            match Nullable.GetUnderlyingType clrType with
            | null -> clrType
            | inner -> inner

        if underlying = typeof<bool> then
            BooleanColumn
        elif
            underlying = typeof<DateTime>
            || underlying = typeof<DateTimeOffset>
            || underlying = typeof<DateOnly>
            || underlying = typeof<TimeOnly>
            || underlying = typeof<TimeSpan>
        then
            DateColumn
        elif
            underlying = typeof<sbyte>
            || underlying = typeof<byte>
            || underlying = typeof<int16>
            || underlying = typeof<uint16>
            || underlying = typeof<int>
            || underlying = typeof<uint32>
            || underlying = typeof<int64>
            || underlying = typeof<uint64>
            || underlying = typeof<float32>
            || underlying = typeof<float>
            || underlying = typeof<decimal>
        then
            NumberColumn
        else
            // Includes `string`, `byte[]` (rendered base64 by the
            // shared emitter) and anything else the writer declared.
            StringColumn

/// The native type name recorded on `ColumnInfo.DataType`. Parquet's
/// own logical types reach the reader as CLR types, so the CLR name is
/// the most specific true thing available — and unlike the inferred
/// names the CSV and Excel connectors record, it is not a guess.
let nativeName (field: DataField) : string =
    let clrName =
        match field.ClrType with
        | null -> "unknown"
        | clrType ->
            match Nullable.GetUnderlyingType clrType with
            | null -> clrType.Name
            | inner -> inner.Name

    if field.IsNullable then $"%s{clrName}?" else clrName

/// Build the `TableSchema` from a reader's footer metadata. Reads no
/// row group and decodes no value.
let schemaOf (table: string) (fields: DataField seq) : TableSchema = {
    TableName = table
    Columns =
        fields
        |> Seq.map (fun field -> {
            Name = fieldPath field
            DataType = nativeName field
            Nullable = field.IsNullable
        })
        |> List.ofSeq
}

// ─── Column decoding ──────────────────────────────────────────────
//
// Parquet.Net's reader is TYPED: there is no untyped "give me this
// column as an array" call, only `ReadAsync<'T>` overloads constrained
// to a non-nullable value type, plus non-generic overloads for the two
// reference types the format carries (`string`, `byte[]`). A
// schema-agnostic connector therefore dispatches on the field's
// declared CLR type once per column and boxes the result — the boxing
// is unavoidable, and it is also exactly what the shared CSV emitter
// consumes.

/// Read one non-nullable value column.
let private readStruct<'T when 'T: struct and 'T :> ValueType and 'T: (new: unit -> 'T)>
    (rowGroup: ParquetRowGroupReader)
    (field: DataField)
    (rowCount: int)
    : Task<obj[]> =
    task {
        let values = Array.zeroCreate<'T> rowCount
        do! rowGroup.ReadAsync<'T>(field, Memory<'T> values)
        return values |> Array.map box
    }

/// Read one nullable value column. A null cell boxes to `null`, which
/// the shared emitter renders as the empty field.
let private readNullableStruct<'T when 'T: struct and 'T :> ValueType and 'T: (new: unit -> 'T)>
    (rowGroup: ParquetRowGroupReader)
    (field: DataField)
    (rowCount: int)
    : Task<obj[]> =
    task {
        let values = Array.zeroCreate<Nullable<'T>> rowCount
        do! rowGroup.ReadAsync<'T>(field, Memory<Nullable<'T>> values)

        return
            values
            |> Array.map (fun value -> if value.HasValue then box value.Value else null)
    }

/// The decoder for one field's declared type, or `None` when the type
/// is outside the supported set. `None` is a refusal rather than a
/// silent `ToString()`: emitting a column the connector does not
/// understand would put values in the CSV that no consumer could parse
/// back, and the operator would have no way to tell.
let private decoderFor (field: DataField) : (ParquetRowGroupReader -> DataField -> int -> Task<obj[]>) option =
    let clrType =
        match field.ClrType with
        | null -> null
        | declared ->
            match Nullable.GetUnderlyingType declared with
            | null -> declared
            | underlying -> underlying

    if isNull clrType then
        None
    else
        let pick (plain: ParquetRowGroupReader -> DataField -> int -> Task<obj[]>) nullable =
            Some(if field.IsNullable then nullable else plain)

        if clrType = typeof<bool> then
            pick readStruct<bool> readNullableStruct<bool>
        elif clrType = typeof<sbyte> then
            pick readStruct<sbyte> readNullableStruct<sbyte>
        elif clrType = typeof<byte> then
            pick readStruct<byte> readNullableStruct<byte>
        elif clrType = typeof<int16> then
            pick readStruct<int16> readNullableStruct<int16>
        elif clrType = typeof<uint16> then
            pick readStruct<uint16> readNullableStruct<uint16>
        elif clrType = typeof<int> then
            pick readStruct<int> readNullableStruct<int>
        elif clrType = typeof<uint32> then
            pick readStruct<uint32> readNullableStruct<uint32>
        elif clrType = typeof<int64> then
            pick readStruct<int64> readNullableStruct<int64>
        elif clrType = typeof<uint64> then
            pick readStruct<uint64> readNullableStruct<uint64>
        elif clrType = typeof<float32> then
            pick readStruct<float32> readNullableStruct<float32>
        elif clrType = typeof<float> then
            pick readStruct<float> readNullableStruct<float>
        elif clrType = typeof<decimal> then
            pick readStruct<decimal> readNullableStruct<decimal>
        elif clrType = typeof<DateTime> then
            pick readStruct<DateTime> readNullableStruct<DateTime>
        elif clrType = typeof<DateTimeOffset> then
            pick readStruct<DateTimeOffset> readNullableStruct<DateTimeOffset>
        elif clrType = typeof<TimeSpan> then
            pick readStruct<TimeSpan> readNullableStruct<TimeSpan>
        elif clrType = typeof<DateOnly> then
            pick readStruct<DateOnly> readNullableStruct<DateOnly>
        elif clrType = typeof<TimeOnly> then
            pick readStruct<TimeOnly> readNullableStruct<TimeOnly>
        elif clrType = typeof<Guid> then
            pick readStruct<Guid> readNullableStruct<Guid>
        else
            None

/// Read one column of a row group as boxed cells.
///
/// A repeated field (Parquet `LIST` / `MAP`) has no rectangular
/// reading — its values arrive flattened behind repetition levels, so
/// aligning them into rows would mean inventing a flattening rule the
/// writer never declared, and every row after the first list would
/// silently carry another column's values. Nested STRUCTs are
/// unaffected: they flatten to dotted paths with one value per row,
/// which is why they are supported and lists are not. The refusal
/// arrives here as a decode failure and is reported by column name.
let private readColumn
    (rowGroup: ParquetRowGroupReader)
    (field: DataField)
    (rowCount: int)
    : Task<Result<obj[], IngestionError>> =
    task {
        let name = fieldPath field

        match field.ClrType with
        | clrType when clrType = typeof<string> ->
            let values = Array.zeroCreate<string> rowCount
            do! rowGroup.ReadAsync(field, Memory<string> values)
            return Ok(values |> Array.map box)
        | clrType when clrType = typeof<byte[]> ->
            let values = Array.zeroCreate<byte[]> rowCount
            do! rowGroup.ReadAsync(field, Memory<byte[]> values)
            return Ok(values |> Array.map box)
        | clrType ->
            match decoderFor field with
            | None ->
                let declared = if isNull clrType then "unknown" else clrType.Name

                return
                    Error(
                        SchemaMismatch
                            $"column '%s{name}' has Parquet type '%s{declared}', which this connector does not decode"
                    )
            | Some decode ->
                try
                    let! values = decode rowGroup field rowCount
                    return Ok values
                with ex ->
                    return
                        Error(
                            SchemaMismatch
                                $"column '%s{name}' could not be read as a rectangular column (%s{ex.Message}) — repeated fields (Parquet LIST / MAP) are not supported"
                        )
    }

/// Decode the whole file to RFC 4180 CSV, one row group at a time.
/// Only one row group's decoded columns are ever resident.
let private toCsv (reader: ParquetReader) : Task<Result<byte[], IngestionError>> = task {
    let fields = reader.Schema.DataFields
    let header = fields |> Array.map fieldPath

    let builder = StringBuilder()
    builder.Append(CsvWire.renderRow header).Append("\r\n") |> ignore

    let mutable failure: IngestionError option = None

    for groupIndex in 0 .. reader.RowGroupCount - 1 do
        if failure.IsNone then
            use rowGroup = reader.OpenRowGroupReader groupIndex
            let rowCount = int rowGroup.RowCount
            let columns = Array.zeroCreate<obj[]> fields.Length

            for i in 0 .. fields.Length - 1 do
                if failure.IsNone then
                    let! decoded = readColumn rowGroup fields[i] rowCount

                    match decoded with
                    | Error err -> failure <- Some err
                    | Ok values -> columns[i] <- values

            if failure.IsNone then
                for rowIndex in 0 .. rowCount - 1 do
                    let cells =
                        columns |> Array.map (fun column -> CsvWire.renderValue column[rowIndex])

                    builder.Append(CsvWire.renderRow cells).Append("\r\n") |> ignore

    match failure with
    | Some err -> return Error err
    | None -> return Ok(Encoding.UTF8.GetBytes(builder.ToString()))
}

type private ParquetDataSourceImpl(storage: IBlobStorage) =

    let withSettings
        (ctx: DataSourceCallContext)
        (context: string)
        (body: ParquetSourceSettings -> Async<Result<'T, IngestionError>>)
        : Async<Result<'T, IngestionError>> =
        Errors.guard context (fun () -> async {
            match readSettings ctx.Config.ConnectionScope with
            | Error err -> return Error err
            | Ok settings -> return! body settings
        })

    /// Acquire a file and open a reader over it.
    ///
    /// The reader scope is a `task` rather than an `async` because
    /// `ParquetReader` is `IAsyncDisposable` and NOT `IDisposable` —
    /// only `use!` in a task expression disposes it correctly, which
    /// is the same shape `ParquetDatasetCodec` beside this file uses.
    let withReader
        (context: string)
        (settings: ParquetSourceSettings)
        (table: string)
        (body: ParquetReader -> Task<Result<'T, IngestionError>>)
        : Async<Result<'T, IngestionError>> =
        async {
            match! Files.download storage context settings.File table with
            | Error err -> return Error err
            | Ok bytes ->
                let read = task {
                    try
                        use stream = new MemoryStream(bytes, writable = false)
                        use! reader = ParquetReader.CreateAsync stream
                        return! body reader
                    with ex ->
                        return Error(SchemaMismatch $"%s{context}: not a readable Parquet file: %s{ex.Message}")
                }

                return! read |> Async.AwaitTask
        }

    interface IDataSource with
        member _.Kind = Kind

        member _.Connect(ctx) =
            withSettings ctx "Parquet Connect" (fun settings -> async {
                match! Files.listTables storage "Parquet Connect" settings.File with
                | Error err -> return Error err
                | Ok [] ->
                    return
                        Error(
                            SourceUnreachable
                                $"Parquet Connect: no '%s{settings.File.Extension}' files under '%s{settings.File.Container}/%s{settings.File.Prefix}'"
                        )
                | Ok _ -> return Ok()
            })

        member _.ListTables(ctx) =
            withSettings ctx "Parquet ListTables" (fun settings ->
                Files.listTables storage "Parquet ListTables" settings.File)

        member _.GetSchema(ctx, table) =
            withSettings ctx "Parquet GetSchema" (fun settings ->
                withReader "Parquet GetSchema" settings table (fun reader ->
                    Task.FromResult(Ok(schemaOf table reader.Schema.DataFields))))

        member _.Query(ctx, sql) =
            withSettings ctx "Parquet Query" (fun settings -> withReader "Parquet Query" settings sql toCsv)

/// Build the connector over the deployment's blob storage.
///
/// `storage` is whatever the deployment composed — `LocalFileStorage`
/// for extracts on disk, or any cloud companion. The connector never
/// touches `System.IO` itself, so the same `DataSourceConfig` works
/// against either without an edit.
let create (storage: IBlobStorage) : IDataSource =
    ParquetDataSourceImpl(storage) :> IDataSource