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

// ─── Phase 837 — native passthrough fixtures ──────────────────────

/// A nullable value column; `None` writes a null through the definition
/// level, which is the fact CSV cannot carry.
let private nullableColumn<'T when 'T: struct and 'T :> ValueType and 'T: (new: unit -> 'T)>
    (name: string)
    (values: 'T option list)
    : FixtureColumn =
    let field = DataField(name, typeof<Nullable<'T>>, isNullable = true)

    field,
    fun (rowGroup: ParquetRowGroupWriter) (field: DataField) ->
        let cells =
            values
            |> List.map (function
                | Some v -> Nullable v
                | None -> Nullable())
            |> List.toArray

        rowGroup.WriteAsync<'T>(field, ReadOnlyMemory<Nullable<'T>>(cells))

/// A nullable string column carrying a real NULL and a real empty string.
let private nullableStrings (name: string) (values: string list) : FixtureColumn =
    let field = DataField(name, typeof<string>, isNullable = true)

    field, fun (rowGroup: ParquetRowGroupWriter) (field: DataField) -> rowGroup.WriteAsync(field, List.toArray values)

let private typedFixture () =
    writeParquet [
        valueColumn<int64> "id" [ 1L; 2L; 3L ]
        nullableColumn<int64> "quantity" [ Some 10L; None; Some 0L ]
        nullableColumn<double> "amount" [ Some 1.5; Some 2.25; None ]
        nullableColumn<bool> "active" [ Some true; None; Some false ]
        nullableStrings "label" [ "alpha"; null; "" ]
    ]

let private ingestScope = "scope-837"

type private IngestRig = {
    Ingestor: IDataIngestor
    Store: IDataObjectStore
    Storage: FakeBlobStorage.InMemoryBlobStorage
}

/// The shipped `DataIngestor` over in-memory stores, with the Parquet
/// connector configured as source `sourceId` and `readers` composed.
let private ingestRig (sourceId: string) (readers: IPayloadReader list) (connector: IDataSource option) =
    let storage = FakeBlobStorage.InMemoryBlobStorage()
    let blobs = storage :> ToolUp.Platform.BlobStorage.IBlobStorage
    let store = DataObjectStore.DataObjectStore(blobs) :> IDataObjectStore
    let configs = DataSourceConfigStore.create blobs

    configs.Save(
        ingestScope,
        TestFakes.config sourceId ParquetDataSource.Kind [ "container", container sourceId; "prefix", Prefix ]
    )
    |> Async.RunSynchronously

    let logger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn _ = ()
            member _.Error(_, _) = ()
        }

    let secrets =
        { new ToolUp.Platform.Secrets.ISecretStore with
            member _.GetSecret(_, _) = async { return None }
            member _.SetSecret(_, _, _) = async { return Ok() }
            member _.DeleteSecret(_, _) = async { return Ok() }
            member _.ListKeys _ = async { return [] }
        }

    let source =
        connector |> Option.defaultWith (fun () -> ParquetDataSource.create storage)

    {
        Ingestor =
            DataIngestor.createWithReaders
                configs
                secrets
                store
                (InMemoryEventStore.InMemoryEventStore() :> IEventStore)
                [ source ]
                blobs
                logger
                readers
        Store = store
        Storage = storage
    }

/// Run one ingestion and read the stored payload back.
let private ingest (rig: IngestRig) (sourceId: string) (table: string) = async {
    match! rig.Ingestor.RunIngestion(ingestScope, sourceId, table) with
    | Ok {
             Status = IngestionStatus.Succeeded
             ResultObjectId = Some objectId
         } ->
        match! rig.Store.Get(ingestScope, objectId) with
        | Ok stored -> return Ok stored
        | Error e -> return failtestf "stored payload unreadable: %A" e
    | Ok run -> return Error run
    | Error e -> return failtestf "RunIngestion errored: %A" e
}

let private nativeTests =
    testList "native passthrough (Phase 837)" [

        testCaseAsync "with the reader composed, the payload is the file's own bytes and reads back typed, nulls intact"
        <| async {
            let rig = ingestRig "src" [ ParquetDataSource.payloadReader () ] None
            let file = typedFixture ()
            rig.Storage.Put(container "src", $"%s{Prefix}orders.parquet", file)

            match! ingest rig "src" "orders" with
            | Error run -> failtestf "ingestion did not succeed: %A" run
            | Ok(payload, bytes) ->
                Expect.equal bytes file "stored UNCHANGED — no parse, no conversion"

                Expect.equal
                    (IngestedPayload.contentFormat payload)
                    (Some ParquetDataSource.NativeFormat)
                    "content-format records the native format"

                match PayloadReader.read [ ParquetDataSource.payloadReader () ] payload bytes with
                | Error e -> failtestf "reader refused: %s" (PayloadReader.describe e)
                | Ok(schema, rows) ->
                    let shape = schema.Columns |> List.map (fun c -> c.Name, c.DType, c.Nullable)

                    Expect.sequenceEqual
                        shape
                        [
                            "id", DatasetDType.Int, false
                            "quantity", DatasetDType.Int, true
                            "amount", DatasetDType.Float, true
                            "active", DatasetDType.Bool, true
                            "label", DatasetDType.Text, true
                        ]
                        "types and nullability come from the footer, not from the values"

                    Expect.sequenceEqual
                        (rows |> List.map _.Cells)
                        [
                            [
                                DatasetValue.Int 1L
                                DatasetValue.Int 10L
                                DatasetValue.Float 1.5
                                DatasetValue.Bool true
                                DatasetValue.Text "alpha"
                            ]
                            [
                                DatasetValue.Int 2L
                                DatasetValue.Null
                                DatasetValue.Float 2.25
                                DatasetValue.Null
                                DatasetValue.Null
                            ]
                            [
                                DatasetValue.Int 3L
                                DatasetValue.Int 0L
                                DatasetValue.Null
                                DatasetValue.Bool false
                                DatasetValue.Text ""
                            ]
                        ]
                        "NULL and the empty string stay distinct; zero is not null"

                    // Every cell fits its declared column — nothing was
                    // re-inferred, so nothing disagrees with the schema.
                    for row in rows do
                        for column, cell in List.zip schema.Columns row.Cells do
                            Expect.isTrue
                                (DatasetValue.fits column.DType column.Nullable cell)
                                $"cell %A{cell} fits column %s{column.Name}"
        }

        testCaseAsync "with no reader composed, the same source falls back to CSV exactly as before"
        <| async {
            let rig = ingestRig "src" [] None
            let file = typedFixture ()
            rig.Storage.Put(container "src", $"%s{Prefix}orders.parquet", file)

            match! ingest rig "src" "orders" with
            | Error run -> failtestf "ingestion did not succeed: %A" run
            | Ok(payload, bytes) ->
                Expect.equal (IngestedPayload.contentFormat payload) (Some PayloadFormat.Csv) "content-format is csv"

                let source = ParquetDataSource.create rig.Storage

                match! source.Query(context "src", "orders") with
                | Ok csv -> Expect.equal bytes csv "byte-for-byte the connector's CSV Query"
                | Error err -> failtestf "Query failed: %A" err
        }

        testCaseAsync "a payload whose declared format has no composed reader is refused by name"
        <| async {
            let rig = ingestRig "src" [ ParquetDataSource.payloadReader () ] None
            rig.Storage.Put(container "src", $"%s{Prefix}orders.parquet", typedFixture ())

            match! ingest rig "src" "orders" with
            | Error run -> failtestf "ingestion did not succeed: %A" run
            | Ok(payload, bytes) ->
                match PayloadReader.read [] payload bytes with
                | Error(PayloadReadError.NoReaderComposed format) ->
                    Expect.equal format "parquet" "the refusal names the format"
                | other -> failtestf "Expected NoReaderComposed \"parquet\"; got %A" other
        }

        testCaseAsync "a connector DECLARING a native format is accepted once its reader is composed, refused without"
        <| async {
            let declaringParquet (storage: FakeBlobStorage.InMemoryBlobStorage) =
                let inner = ParquetDataSource.create storage
                let native = inner :?> IEmitsNativePayload

                { new IDataSource with
                    member _.Kind = inner.Kind
                    member _.Connect ctx = inner.Connect ctx
                    member _.ListTables ctx = inner.ListTables ctx
                    member _.GetSchema(ctx, t) = inner.GetSchema(ctx, t)
                    member _.Query(ctx, sql) = native.QueryNative(ctx, sql)
                  interface IDeclaresPayloadFormat with
                      member _.PayloadFormat = ParquetDataSource.NativeFormat
                }

            let seededRig readers =
                let storage = FakeBlobStorage.InMemoryBlobStorage()
                let rig = ingestRig "src" readers (Some(declaringParquet storage))
                // The connector reads the storage it was built over.
                storage.Put(container "src", $"%s{Prefix}orders.parquet", typedFixture ())
                rig

            match! ingest (seededRig [ ParquetDataSource.payloadReader () ]) "src" "orders" with
            | Ok(payload, _) ->
                Expect.equal (IngestedPayload.contentFormat payload) (Some ParquetDataSource.NativeFormat) "stored"
            | Error run -> failtestf "expected acceptance with the reader composed; got %A" run

            match! (seededRig []).Ingestor.RunIngestion(ingestScope, "src", "orders") with
            | Error(SchemaMismatch message) -> Expect.stringContains message "'parquet'" "the refusal names the format"
            | other -> failtestf "a declared format with no composed reader must be refused; got %A" other
        }

        testCaseAsync "QueryNative refuses a blob that is not Parquet rather than passing it through"
        <| async {
            let storage = FakeBlobStorage.InMemoryBlobStorage()
            let native = ParquetDataSource.create storage :?> IEmitsNativePayload
            storage.Put(container "src", $"%s{Prefix}bogus.parquet", Text.Encoding.UTF8.GetBytes "not parquet")

            match! native.QueryNative(context "src", "bogus") with
            | Error(SchemaMismatch message) -> Expect.stringContains message "Parquet" "names the format"
            | other -> failtestf "Expected SchemaMismatch; got %A" other
        }

        test "the platform tiers carry no Parquet dependency (GP 1)" {
            let parquetReferences (asm: Reflection.Assembly) =
                asm.GetReferencedAssemblies()
                |> Array.choose (fun name -> Option.ofObj name.Name)
                |> Array.filter (fun name -> name = "Parquet" || name.StartsWith "Parquet.")
                |> List.ofArray

            // The probe fails closed: the companion itself DOES reference
            // the vendor assembly, so a detector that matched nothing
            // would be caught here rather than passing vacuously.
            Expect.isNonEmpty
                (parquetReferences typeof<ParquetDataSource.ParquetPayloadReader>.Assembly)
                "the probe sees the companion's Parquet reference"

            for tier in [ typeof<DatasetSchema>.Assembly; typeof<IPayloadReader>.Assembly ] do
                Expect.isEmpty
                    (parquetReferences tier)
                    $"%s{tier.GetName().Name} must not reference Parquet — the reader lives in the companion"
        }
    ]

let tests =
    testList "ParquetDataSource" [

        nativeTests

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