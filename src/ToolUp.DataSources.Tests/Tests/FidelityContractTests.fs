// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.FidelityContractTests

open System
open System.Collections.Concurrent
open System.Data
open System.Globalization
open System.IO
open Expecto
open Microsoft.Data.Sqlite
open Parquet
open Parquet.Schema
open ToolUp.Platform
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.DataSourceFidelityContract
open ToolUp.DataSources.Common
open ToolUp.DataSources.Csv
open ToolUp.DataSources.Excel
open ToolUp.DataSources.Parquet
open ToolUp.DataSources.Redshift
open ToolUp.DataSources.Snowflake
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Synapse
open ToolUp.DataSources.Tests.Support
open DataManagementTypes

module CsvWire = ToolUp.DataSources.Common.Csv

type private DataApiField = Amazon.RedshiftDataAPIService.Model.Field

// ─── Phase 836 — the fidelity pack, bound to the shipped connectors ─
//
// Every connector this project ships is either BOUND to
// `DataSourceFidelityContract` here or listed as `unbound` with the
// reason — never quietly absent. Three binding shapes, strongest first:
//
//   • LIVE, LOCAL. The connector's own `IDataSource` runs against a real
//     backend in-process: `SqlDataSource` against SQLite (an in-memory
//     database seeded with DDL), and the three file connectors against
//     files written in their own formats into an in-memory `IBlobStorage`.
//     Nothing is stubbed between the pack and the connector.
//   • RECORDED. For a warehouse with no local or emulated backend, the
//     result set the vendor returns is recorded here and pushed through
//     the connector's OWN render path — `RedshiftDataSource.renderField`
//     over Data API `Field`s, `Csv.ofReader` over an ADO reader for the
//     Synapse and Snowflake connectors (the exact call their `Query`
//     makes) — with the catalogue's native type names classified by the
//     connector's own `toColumnType`. What is recorded is the vendor's
//     side of the wire; what is tested is the connector's.
//   • UNBOUND, with a reason, where the render path is not reachable
//     without the vendor.
//
// The `SqlDataSource` binding covers all six of its backends' `Query`
// path — every backend renders through the same `Csv.ofReader` — while
// each backend's catalogue SQL and type classification stay unit-tested
// in `SqlDataSourceTests`.

// ─── Classifiers for the schema a file connector infers ───────────

/// The file connectors record an inferred type as `number (inferred)`,
/// `date (inferred)`, … (`LocalFileSupport.TypeProbe.nativeName`).
let private classifyInferred (dataType: string) : ColumnType =
    match dataType.Split(' ').[0] with
    | "number" -> NumberColumn
    | "date" -> DateColumn
    | "boolean" -> BooleanColumn
    | _ -> StringColumn

/// Parquet records its CLR type name (`Int32`, `Double?`), which the
/// connector's own `toColumnType` projects once it is a `Type` again.
let private classifyParquet (dataType: string) : ColumnType =
    let clrName = dataType.TrimEnd('?')

    match Type.GetType $"System.%s{clrName}" with
    | null -> StringColumn
    | clrType -> ParquetDataSource.toColumnType clrType

// ─── Live: SqlDataSource against SQLite ───────────────────────────

/// In-memory shared-cache databases live exactly as long as one
/// connection to them is open, so each seeded database keeps its seeding
/// connection here for the life of the run.
let private keepAlive = ConcurrentBag<SqliteConnection>()

let private sqliteTarget () : FidelityTarget =
    let database = Guid.NewGuid().ToString "N"

    let connectionString = $"Data Source=fidelity-%s{database};Mode=Memory;Cache=Shared"

    let connection = new SqliteConnection(connectionString)
    connection.Open()
    keepAlive.Add connection

    let execute (sql: string) (parameters: (string * obj) list) =
        use command = connection.CreateCommand()
        command.CommandText <- sql

        for name, value in parameters do
            command.Parameters.AddWithValue(name, value) |> ignore

        command.ExecuteNonQuery() |> ignore

    execute "CREATE TABLE fidelity (id INTEGER NOT NULL, label TEXT, amount REAL, observed_on DATE)" []

    let valueOf (convert: string -> obj) (cell: Cell) : obj =
        match cell with
        | Cell.Null -> box DBNull.Value
        | Cell.Text text -> convert text

    let number (text: string) =
        box (Double.Parse(text, CultureInfo.InvariantCulture))

    for row in Fixture.standard.Rows do
        match row.Cells with
        | [ id; label; amount; observedOn ] ->
            execute "INSERT INTO fidelity VALUES ($id, $label, $amount, $observed)" [
                "$id", valueOf (fun t -> box (Int64.Parse(t, CultureInfo.InvariantCulture))) id
                "$label", valueOf box label
                "$amount", valueOf number amount
                "$observed", valueOf box observedOn
            ]
        | other -> failwithf "the fixture row has %d cells; this binding seeds four" other.Length

    {
        Source = SqlDataSource.createWithoutSecrets ()
        Context =
            TestFakes.config "fidelity" SqlDataSource.Kind [
                "backend", "sqlite"
                "connection_string", connectionString
            ]
            |> TestFakes.context "test-scope" None
        Table = Fixture.standard.Table
        Statement = Fixture.standard.Table
        Classify = SqlDialect.toColumnType Sqlite
        Schema = SchemaEvidence.Declared
        EmptyString = EmptyStringSupport.Distinguishes
    }

// ─── Live: the file connectors ────────────────────────────────────

let private fileContext (kind: string) (prefix: string) (scope: (string * string) list) =
    TestFakes.config "fidelity" kind ([ "container", "team-fidelity"; "prefix", prefix ] @ scope)
    |> TestFakes.context "test-scope" None

/// Why a delimited file or a workbook binds with `CannotState`.
let private blankIsAllAFileSays =
    "a blank cell in an uploaded file is all the file says, so the connector reads every blank as absent (Csv.absentIfEmpty)"

let private csvTarget () : FidelityTarget =
    let storage = FakeBlobStorage.InMemoryBlobStorage()

    let lines =
        (Fixture.header Fixture.standard |> String.concat ",")
        :: (Fixture.standard.Rows
            |> List.map (fun row -> row.Cells |> List.map renderFaithful |> String.concat ","))

    let bytes =
        lines
        |> List.map (fun line -> line + "\r\n")
        |> String.concat ""
        |> Text.Encoding.UTF8.GetBytes

    storage.Put("team-fidelity", $"exports/%s{Fixture.standard.Table}.csv", bytes)

    {
        Source = CsvDataSource.create storage
        Context = fileContext CsvDataSource.Kind "exports/" []
        Table = Fixture.standard.Table
        Statement = Fixture.standard.Table
        Classify = classifyInferred
        Schema = SchemaEvidence.Inferred
        EmptyString = EmptyStringSupport.CannotState blankIsAllAFileSays
    }

let private excelTarget () : FidelityTarget =
    let storage = FakeBlobStorage.InMemoryBlobStorage()

    let grid =
        Fixture.header Fixture.standard
        :: (Fixture.standard.Rows
            |> List.map (fun row ->
                row.Cells
                |> List.map (fun cell ->
                    match cell with
                    | Cell.Null -> ""
                    | Cell.Text text -> text)))

    storage.Put(
        "team-fidelity",
        $"inbox/%s{Fixture.standard.Table}.xlsx",
        ExcelDataSourceTests.buildWorkbook [ "Data", grid ] [] []
    )

    {
        Source = ExcelDataSource.create storage
        Context = fileContext ExcelDataSource.Kind "inbox/" []
        Table = Fixture.standard.Table
        Statement = Fixture.standard.Table
        Classify = classifyInferred
        Schema = SchemaEvidence.Inferred
        EmptyString = EmptyStringSupport.CannotState blankIsAllAFileSays
    }

/// A Parquet file with the fixture's declared types and nullability:
/// Parquet states both in its own schema, so this binding holds the
/// connector's schema to the source's.
let private parquetBytes () : byte[] =
    let column (index: int) =
        Fixture.standard.Rows |> List.map (fun row -> row.Cells[index])

    let parse (convert: string -> 'T) (cell: Cell) : Nullable<'T> =
        match cell with
        | Cell.Null -> Nullable()
        | Cell.Text text -> Nullable(convert text)

    let invariant = CultureInfo.InvariantCulture

    let idField = DataField("id", typeof<int>, isNullable = false)
    let labelField = DataField("label", typeof<string>, isNullable = true)
    let amountField = DataField("amount", typeof<double>, isNullable = true)
    let observedField = DataField("observed_on", typeof<DateTime>, isNullable = true)

    let ids =
        column 0
        |> List.map (fun cell -> Int32.Parse(Fixture.textOrNull cell, invariant))
        |> List.toArray

    let labels = column 1 |> List.map Fixture.textOrNull |> List.toArray

    let amounts =
        column 2
        |> List.map (parse (fun t -> Double.Parse(t, invariant)))
        |> List.toArray

    let observed =
        column 3
        |> List.map (
            parse (fun t ->
                DateTime.ParseExact(
                    t,
                    "yyyy-MM-dd",
                    invariant,
                    DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
                ))
        )
        |> List.toArray

    use stream = new MemoryStream()

    let schema =
        ParquetSchema(idField :> Field, labelField :> Field, amountField :> Field, observedField :> Field)

    let write = task {
        use! writer = ParquetWriter.CreateAsync(schema, stream)
        use rowGroup = writer.CreateRowGroup()
        do! rowGroup.WriteAsync<int>(idField, ReadOnlyMemory<int> ids)
        do! rowGroup.WriteAsync(labelField, labels)
        do! rowGroup.WriteAsync<double>(amountField, ReadOnlyMemory<Nullable<double>> amounts)
        do! rowGroup.WriteAsync<DateTime>(observedField, ReadOnlyMemory<Nullable<DateTime>> observed)
    }

    write.GetAwaiter().GetResult()
    stream.ToArray()

let private parquetTarget () : FidelityTarget =
    let storage = FakeBlobStorage.InMemoryBlobStorage()
    storage.Put("team-fidelity", $"extracts/%s{Fixture.standard.Table}.parquet", parquetBytes ())

    {
        Source = ParquetDataSource.create storage
        Context = fileContext ParquetDataSource.Kind "extracts/" []
        Table = Fixture.standard.Table
        Statement = Fixture.standard.Table
        Classify = classifyParquet
        Schema = SchemaEvidence.Declared
        EmptyString = EmptyStringSupport.Distinguishes
    }

// ─── Recorded: a vendor result set through the connector's renderer ─

/// A connector-shaped source over a RECORDED result set: its schema is
/// the vendor catalogue's answer, its payload is produced on each `Query`
/// by the connector's own render path, and it declares whatever the real
/// connector declares.
type private RecordedSource(kind: string, declares: IDataSource, schema: TableSchema, render: unit -> byte[]) =
    let missing (table: string) =
        Error(SchemaMismatch $"%s{kind} (recorded): no table '%s{table}'")

    interface IDataSource with
        member _.Kind = kind
        member _.Connect _ = async { return Ok() }
        member _.ListTables _ = async { return Ok [ schema.TableName ] }

        member _.GetSchema(_, table) = async {
            return
                if table = schema.TableName then
                    Ok schema
                else
                    missing table
        }

        member _.Query(_, sql) = async {
            return
                if sql = schema.TableName then
                    Ok(render ())
                else
                    missing sql
        }

    interface IDeclaresPayloadFormat with
        member _.PayloadFormat = PayloadFormat.declaredBy declares

/// The vendor catalogue's answer for the fixture table, in its own type
/// names: `(id, label, amount, observed_on)`, only `id` NOT NULL.
let private recordedSchema (nativeTypes: string list) : TableSchema =
    Fixture.standard.Columns
    |> List.map2 (fun native (column: FixtureColumn) -> TypeMap.column column.Name native column.Nullable) nativeTypes
    |> TypeMap.schema Fixture.standard.Table

let private recordedTarget
    (kind: string)
    (declares: IDataSource)
    (nativeTypes: string list)
    (classify: string -> ColumnType)
    (render: unit -> byte[])
    : FidelityTarget =
    {
        Source = RecordedSource(kind, declares, recordedSchema nativeTypes, render) :> IDataSource
        Context = TestFakes.config "fidelity" kind [] |> TestFakes.context "test-scope" None
        Table = Fixture.standard.Table
        Statement = Fixture.standard.Table
        Classify = classify
        Schema = SchemaEvidence.Declared
        EmptyString = EmptyStringSupport.Distinguishes
    }

/// The fixture as a Redshift Data API `GetStatementResult` would return
/// it: a NULL is the `isNull` arm, text the `stringValue` arm, an integer
/// the `longValue` arm, a float the `doubleValue` arm, and a date text.
let private redshiftRecords () : DataApiField list list =
    let field (setValue: DataApiField -> unit) =
        let field = DataApiField()
        setValue field
        field

    let cellField (index: int) (cell: Cell) =
        match index, cell with
        | _, Cell.Null -> field (fun f -> f.IsNull <- true)
        | 0, Cell.Text text -> field (fun f -> f.LongValue <- Int64.Parse(text, CultureInfo.InvariantCulture))
        | 2, Cell.Text text -> field (fun f -> f.DoubleValue <- Double.Parse(text, CultureInfo.InvariantCulture))
        | _, Cell.Text text -> field (fun f -> f.StringValue <- text)

    Fixture.standard.Rows |> List.map (fun row -> row.Cells |> List.mapi cellField)

let private redshiftTarget () : FidelityTarget =
    recordedTarget
        RedshiftDataSource.Kind
        (RedshiftDataSource.createWithDefaultCredentials ())
        [ "integer"; "character varying(4096)"; "double precision"; "date" ]
        RedshiftDataSource.toColumnType
        (fun () ->
            // Exactly `readResults`: the Data API records through
            // `renderField`, written by the shared RFC 4180 writer.
            CsvWire.toBytes
                (Fixture.header Fixture.standard)
                (redshiftRecords ()
                 |> Seq.map (List.map RedshiftDataSource.renderField >> Seq.ofList)))

/// The fixture as an ADO provider's reader returns it — typed values,
/// `DBNull` for NULL — so `Csv.ofReader` (the exact call the ADO-shaped
/// connectors' `Query` makes) renders it.
let private adoPayload () : byte[] =
    use table = new DataTable()
    table.Columns.Add("id", typeof<int64>) |> ignore
    table.Columns.Add("label", typeof<string>) |> ignore
    table.Columns.Add("amount", typeof<double>) |> ignore
    table.Columns.Add("observed_on", typeof<DateTime>) |> ignore

    let invariant = CultureInfo.InvariantCulture

    let valueOf (index: int) (cell: Cell) : obj =
        match index, cell with
        | _, Cell.Null -> box DBNull.Value
        | 0, Cell.Text text -> box (Int64.Parse(text, invariant))
        | 2, Cell.Text text -> box (Double.Parse(text, invariant))
        | 3, Cell.Text text -> box (DateTime.ParseExact(text, "yyyy-MM-dd", invariant))
        | _, Cell.Text text -> box text

    for row in Fixture.standard.Rows do
        table.Rows.Add(row.Cells |> List.mapi valueOf |> List.toArray) |> ignore

    use reader = table.CreateDataReader()
    CsvWire.ofReader reader |> Async.RunSynchronously

let private synapseTarget () : FidelityTarget =
    recordedTarget
        SynapseDataSource.Kind
        (SynapseDataSource.createWithDefaultCredentials ())
        [ "int"; "nvarchar(4000)"; "float"; "date" ]
        SynapseCatalogue.toColumnType
        adoPayload

let private snowflakeTarget () : FidelityTarget =
    recordedTarget
        SnowflakeDataSource.Kind
        (SnowflakeDataSource.createWithKeyFile ())
        [ "NUMBER(38,0)"; "TEXT"; "FLOAT"; "DATE" ]
        SnowflakeCatalogue.toColumnType
        adoPayload

// ─── The bindings ─────────────────────────────────────────────────

let tests =
    testList "IDataSource fidelity bindings" [
        // Live, local.
        DataSourceFidelityContract.tests "SqlDataSource (sqlite, live)" sqliteTarget
        DataSourceFidelityContract.tests "CsvDataSource (live)" csvTarget
        DataSourceFidelityContract.tests "ExcelDataSource (live)" excelTarget
        DataSourceFidelityContract.tests "ParquetDataSource (live)" parquetTarget

        // Recorded vendor result sets through the connector's own renderer.
        DataSourceFidelityContract.tests "RedshiftDataSource (recorded Data API result)" redshiftTarget
        DataSourceFidelityContract.tests "SynapseDataSource (recorded ADO result)" synapseTarget
        DataSourceFidelityContract.tests "SnowflakeDataSource (recorded ADO result)" snowflakeTarget

        // Not bound — each says why.
        DataSourceFidelityContract.unbound
            "BigQueryDataSource"
            "Query renders BigQueryRow values inside the connector's private implementation, a BigQueryRow cannot be constructed without the service, and no emulator is composed here; reachability is covered by the env-gated RemoteDataSourceContract arm"
        DataSourceFidelityContract.unbound
            "AthenaDataSource"
            "its Datum-to-cell mapping is private to the connector's result pager and there is no local Athena; reachability is covered by the env-gated RemoteDataSourceContract arm"
        DataSourceFidelityContract.unbound
            "GoogleAnalyticsDataSource"
            "it declares Json and emits the GA4 report shape, and the pack decodes cells from Csv payloads only; its payload-format declaration is bound by IDeclaresPayloadFormatContract in ToolUp.Platform.Tests"
        DataSourceFidelityContract.unbound
            "InMemoryDataSource"
            "a byte store: Query returns exactly the bytes it was seeded with and GetSchema publishes no columns, so a binding would test the seed rather than the connector"
    ]