// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.ParquetDataSourceTests

open System
open System.IO
open Expecto
open Parquet
open Parquet.Schema
open ToolUp.Platform
open ToolUp.DataSources.Parquet
open ToolUp.DataSources.Tests.Support
open DataManagementTypes

// ─── ToolUp.DataSources.Parquet (the IDataSource leg) ─────────────
//
// Always-on. Fixtures are real Parquet files WRITTEN HERE with
// Parquet.Net and read back through the connector, so nothing is
// committed as a binary and the writer's schema and the reader's
// expectations cannot drift apart.

let private container (sourceId: string) = $"team-%s{sourceId}"

[<Literal>]
let private Prefix = "extracts/"

let private context (sourceId: string) : DataSourceCallContext =
    TestFakes.config sourceId ParquetDataSource.Kind [ "container", container sourceId; "prefix", Prefix ]
    |> TestFakes.context "test-scope" None

// ─── Fixture files ────────────────────────────────────────────────

/// One fixture column: the field to declare, and how to write its
/// values into a row group. Parquet.Net's writer is typed, so the
/// second half cannot be generic over the first.
type private FixtureColumn = DataField * (ParquetRowGroupWriter -> DataField -> Threading.Tasks.Task)

/// Write a Parquet file from typed columns.
let private writeParquet (columns: FixtureColumn list) : byte[] =
    use stream = new MemoryStream()

    let schema =
        ParquetSchema(columns |> List.map (fun (field, _) -> field :> Field) |> List.toArray)

    let write = task {
        use! writer = ParquetWriter.CreateAsync(schema, stream)
        use rowGroup = writer.CreateRowGroup()

        for field, emit in columns do
            do! emit rowGroup field
    }

    write.GetAwaiter().GetResult()
    stream.ToArray()

/// A string column, which is what the contract fixture needs: the
/// contract seeds text and asserts it survives the round trip, so any
/// typed column would be asserting the connector's rendering rather
/// than its reading.
let private stringColumn (name: string) (values: string list) : FixtureColumn =
    let field = DataField(name, typeof<string>, isNullable = true)

    field, fun (rowGroup: ParquetRowGroupWriter) (field: DataField) -> rowGroup.WriteAsync(field, List.toArray values)

/// A non-nullable value column.
let private valueColumn<'T when 'T: struct and 'T :> ValueType and 'T: (new: unit -> 'T)>
    (name: string)
    (values: 'T list)
    : FixtureColumn =
    let field = DataField(name, typeof<'T>, isNullable = false)

    field,
    fun (rowGroup: ParquetRowGroupWriter) (field: DataField) ->
        rowGroup.WriteAsync<'T>(field, ReadOnlyMemory<'T>(List.toArray values))

let private target () =
    let storage = FakeBlobStorage.InMemoryBlobStorage()

    {
        LocalFileDataSourceContract.Source = ParquetDataSource.create storage
        LocalFileDataSourceContract.Seed =
            fun sourceId table header rows ->
                let columns =
                    header
                    |> List.mapi (fun index name -> stringColumn name (rows |> List.map (fun row -> row[index])))

                storage.Put(container sourceId, $"%s{Prefix}%s{table}.parquet", writeParquet columns)
        LocalFileDataSourceContract.Context = context
        LocalFileDataSourceContract.Address = id
    }

let tests =
    testList "ParquetDataSource" [

        LocalFileDataSourceContract.tests "Parquet" target

        testList "readSettings" [
            test "container is required" {
                match ParquetDataSource.readSettings Map.empty with
                | Error(SchemaMismatch message) -> Expect.stringContains message "container" "names the missing key"
                | other -> failtestf "Expected SchemaMismatch naming 'container'; got %A" other
            }

            test "the default extension is .parquet" {
                match ParquetDataSource.readSettings (Map.ofList [ "container", "c" ]) with
                | Ok settings -> Expect.equal settings.File.Extension ".parquet" "default"
                | Error err -> failtestf "readSettings failed: %A" err
            }
        ]

        testList "type mapping" [
            test "CLR types project onto the coarse ColumnType" {
                Expect.equal (ParquetDataSource.toColumnType typeof<bool>) BooleanColumn "bool"
                Expect.equal (ParquetDataSource.toColumnType typeof<int64>) NumberColumn "int64"
                Expect.equal (ParquetDataSource.toColumnType typeof<decimal>) NumberColumn "decimal"
                Expect.equal (ParquetDataSource.toColumnType typeof<DateTime>) DateColumn "DateTime"
                Expect.equal (ParquetDataSource.toColumnType typeof<string>) StringColumn "string"
                Expect.equal (ParquetDataSource.toColumnType typeof<byte[]>) StringColumn "byte[] renders base64"
            }

            test "a nullable CLR type projects as its underlying type" {
                Expect.equal (ParquetDataSource.toColumnType typeof<Nullable<int>>) NumberColumn "int?"
            }
        ]

        testList "declared schema" [
            testCaseAsync "GetSchema reads the file's own metadata rather than inferring"
            <| async {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = ParquetDataSource.create storage

                let bytes =
                    writeParquet [ valueColumn<int64> "id" [ 1L; 2L ]; stringColumn "label" [ "alpha"; "beta" ] ]

                storage.Put(container "src", $"%s{Prefix}typed.parquet", bytes)

                match! source.GetSchema(context "src", "typed") with
                | Ok schema ->
                    Expect.sequenceEqual (schema.Columns |> List.map _.Name) [ "id"; "label" ] "columns"

                    // The CSV and Excel connectors can only report a
                    // guess here; Parquet declares it, and the native
                    // name says so by carrying no "(inferred)" marker.
                    let id = schema.Columns |> List.find (fun c -> c.Name = "id")
                    Expect.equal id.Nullable false "the writer declared id non-nullable"
                    Expect.isFalse (id.DataType.Contains "inferred") "the type is declared, not inferred"

                    let label = schema.Columns |> List.find (fun c -> c.Name = "label")
                    Expect.equal label.Nullable true "the writer declared label nullable"
                | Error err -> failtestf "GetSchema failed: %A" err
            }

            testCaseAsync "typed columns render invariant-culture in the emitted CSV"
            <| async {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = ParquetDataSource.create storage

                let bytes = writeParquet [ valueColumn<double> "amount" [ 1.5; 2.25 ] ]
                storage.Put(container "src", $"%s{Prefix}amounts.parquet", bytes)

                match! source.Query(context "src", "amounts") with
                | Ok payload ->
                    let parsed = LocalFileDataSourceContract.parseCsv payload
                    Expect.sequenceEqual (List.head parsed) [ "amount" ] "header"

                    // A comma-decimal host would render `1,5` here and
                    // silently split the field in two.
                    Expect.sequenceEqual (List.tail parsed) [ [ "1.5" ]; [ "2.25" ] ] "invariant decimal point"
                | Error err -> failtestf "Query failed: %A" err
            }

            testCaseAsync "a blob that is not a Parquet file is refused, not misread"
            <| async {
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let source = ParquetDataSource.create storage
                storage.Put(container "src", $"%s{Prefix}bogus.parquet", Text.Encoding.UTF8.GetBytes "not parquet")

                match! source.Query(context "src", "bogus") with
                | Error(SchemaMismatch message) -> Expect.stringContains message "Parquet" "names the format"
                | other -> failtestf "Expected SchemaMismatch; got %A" other
            }
        ]

        test "Kind is the documented discriminator" {
            Expect.equal ParquetDataSource.Kind "Parquet" "DataSourceConfig.Kind"
        }
    ]