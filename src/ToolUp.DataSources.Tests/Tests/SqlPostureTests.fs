// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.SqlPostureTests

open System
open System.Data.Common
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.DataSources.Common
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Tests.Support.TestFakes

// ─── ToolUp.DataSources.Sql — statement policy and read-only intent ──
//
// The posture keys, the read-only connection strings and the preflight
// lines are pure, so they are tested without a database. The policy and
// the read-only refusal then run against SQLite in process, always, and
// the read-only refusal against each server backend as an armed leg on
// the bound-statement legs' variables: Pending without one, never Failed.
// An armed leg needs a SCRATCH database: it creates one table
// (`toolup_posture`) and drops it again.

let private scopeOf (pairs: (string * string) list) = Map.ofList pairs

let private postureOf pairs =
    match SourcePosture.read (scopeOf pairs) with
    | Ok posture -> posture
    | Error err -> failtestf "expected the posture to read; got %A" err

let private schemaMismatch (label: string) (result: Result<'T, IngestionError>) =
    match result with
    | Error(SchemaMismatch message) -> message
    | other -> failtestf "%s: expected SchemaMismatch; got %A" label other

let private postureTests =
    testList "SourcePosture.read" [
        test "neither key set is today's posture" {
            Expect.equal (postureOf [ "backend", "sqlite" ]) SourcePosture.unset "raw text admitted, writable"
            Expect.isTrue (SourcePosture.admitsRawAndMayWrite SourcePosture.unset) "the open posture"
        }

        test "each key is read in its own spelling, case-insensitively" {
            let posture = postureOf [ "statement_policy", "Bound-Only"; "read_only", "TRUE" ]
            Expect.equal posture.StatementPolicy StatementPolicy.BoundOnly "bound-only"
            Expect.isTrue posture.ReadOnly "read-only"
            Expect.isFalse (SourcePosture.admitsRawAndMayWrite posture) "closed"
            Expect.equal (SourcePosture.describe posture) "statement_policy=bound-only, read_only=true" "one line"
        }

        test "either key alone closes the posture" {
            Expect.isFalse
                (SourcePosture.admitsRawAndMayWrite (postureOf [ "statement_policy", "bound-only" ]))
                "bound-only"

            Expect.isFalse (SourcePosture.admitsRawAndMayWrite (postureOf [ "read_only", "true" ])) "read-only"
        }

        test "a value outside the accepted set is refused by name, never read as the default" {
            let policy =
                SourcePosture.read (scopeOf [ "statement_policy", "bound" ])
                |> schemaMismatch "policy"

            Expect.stringContains policy "statement_policy" "names the key"
            Expect.stringContains policy "bound-only" "names the accepted values"

            let readOnly =
                SourcePosture.read (scopeOf [ "read_only", "sometimes" ])
                |> schemaMismatch "read_only"

            Expect.stringContains readOnly "read_only" "names the key"
        }
    ]

let private keyValue (connectionString: string) (key: string) =
    let builder = DbConnectionStringBuilder()
    builder.ConnectionString <- connectionString

    match builder.TryGetValue key with
    | true, value -> Some(string value)
    | false, _ -> None

let private readOnlyString backend connectionString =
    match SqlDataSource.readOnlyConnectionString backend connectionString with
    | Ok value -> value
    | Error err -> failtestf "expected a read-only connection string; got %A" err

let private connectionStringTests =
    testList "readOnlyConnectionString" [
        test "PostgreSQL opens every transaction read-only, keeping the operator's own options" {
            let plain = readOnlyString PostgreSql "Host=db;Username=app"
            Expect.equal (keyValue plain "Options") (Some "-c default_transaction_read_only=on") "the startup option"

            let kept = readOnlyString PostgreSql "Host=db;Options=-c search_path=analytics"

            Expect.equal
                (keyValue kept "Options")
                (Some "-c search_path=analytics -c default_transaction_read_only=on")
                "appended, last so it wins"
        }

        test "SQL Server declares its read-only intent, whichever spelling the operator used" {
            let value = readOnlyString SqlServer "Server=db;Application Intent=ReadWrite"
            Expect.equal (keyValue value "ApplicationIntent") (Some "ReadOnly") "the intent"
            Expect.isNone (keyValue value "Application Intent") "the other spelling is gone"
        }

        test "SQLite opens the file read-only" {
            let value = readOnlyString Sqlite "Data Source=app.db;Mode=ReadWriteCreate"
            Expect.equal (keyValue value "Mode") (Some "ReadOnly") "the mode"
        }

        test "a SQLite in-memory database is refused by name rather than opened writable" {
            let message =
                SqlDataSource.readOnlyConnectionString Sqlite "Data Source=x;Mode=Memory;Cache=Shared"
                |> schemaMismatch "memory"

            Expect.stringContains message "read_only" "names the key"
            Expect.stringContains message "Mode=Memory" "names why"
        }

        test "ClickHouse carries the readonly setting" {
            let value = readOnlyString ClickHouse "Host=db;Database=analytics"
            Expect.equal (keyValue value "set_readonly") (Some "2") "the setting"
        }

        test "MySQL and Oracle are unchanged: their calls run in a read-only transaction" {
            for backend in [ MySql; Oracle ] do
                Expect.equal (readOnlyString backend "Server=db;User Id=app") "Server=db;User Id=app" $"%A{backend}"
        }
    ]

let private execute (connection: DbConnection) (sql: string) =
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

let private scalar (connection: DbConnection) (sql: string) =
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteScalar() |> string

let private statementOf (text: string) (parameters: (string * BoundValue) list) =
    parameters
    |> List.fold (fun s (n, v) -> BoundStatement.bind n v s) (BoundStatement.ofText text)

let private sourceFor (scope: (string * string) list) =
    let ctx = config "posture" "Sql" scope |> context "test-scope" None
    SqlDataSource.createWithoutSecrets (), ctx

/// A private in-memory SQLite database, alive while its keeper is open.
let private sqliteMemory () =
    let connectionString =
        $"Data Source=toolup-sql-posture-{Guid.NewGuid():N};Mode=Memory;Cache=Shared"

    let keeper = new Microsoft.Data.Sqlite.SqliteConnection(connectionString)
    keeper.Open()
    connectionString, keeper

let private policyTests =
    testList "statement_policy, SQLite in process" [
        testCaseAsync "with neither key set, statement text runs as it always has"
        <| async {
            let connectionString, keeper = sqliteMemory ()
            use _ = keeper

            let source, ctx =
                sourceFor [ "backend", "sqlite"; "connection_string", connectionString ]

            match! source.Query(ctx, "SELECT 1 AS one") with
            | Ok bytes -> Expect.stringContains (Text.Encoding.UTF8.GetString bytes) "one" "it ran"
            | Error err -> failtestf "expected the statement to run; got %A" err
        }

        testCaseAsync "bound-only refuses statement text by name, and nothing reaches the database"
        <| async {
            let connectionString, keeper = sqliteMemory ()
            use _ = keeper

            let source, ctx =
                sourceFor [
                    "backend", "sqlite"
                    "connection_string", connectionString
                    "statement_policy", "bound-only"
                ]

            let! refused = source.Query(ctx, "CREATE TABLE smuggled (k TEXT)")
            let message = refused |> schemaMismatch "Query"
            Expect.stringContains message "statement_policy=bound-only" "names the policy"
            Expect.stringContains message "'posture'" "names the source"

            Expect.equal
                (scalar keeper "SELECT count(*) FROM sqlite_master WHERE name = 'smuggled'")
                "0"
                "the statement never ran"

            let! described = source.GetSchema(ctx, "SELECT 1 AS one")
            Expect.stringContains (described |> schemaMismatch "GetSchema") "bound-only" "no text is described"
        }

        testCaseAsync "bound-only keeps the bare table name, and runs a bound statement"
        <| async {
            let connectionString, keeper = sqliteMemory ()
            use _ = keeper
            execute keeper "CREATE TABLE orders (id INTEGER NOT NULL, total REAL)"
            execute keeper "INSERT INTO orders VALUES (1, 9.5)"

            let source, ctx =
                sourceFor [
                    "backend", "sqlite"
                    "connection_string", connectionString
                    "statement_policy", "bound-only"
                ]

            match! source.Query(ctx, "orders") with
            | Ok bytes -> Expect.stringContains (Text.Encoding.UTF8.GetString bytes) "9.5" "the table expanded"
            | Error err -> failtestf "expected the bare name to run; got %A" err

            match! source.GetSchema(ctx, "orders") with
            | Ok schema -> Expect.equal (schema.Columns |> List.map _.Name) [ "id"; "total" ] "the catalogue"
            | Error err -> failtestf "GetSchema: %A" err

            match!
                BoundQuery.run
                    source
                    ctx
                    (statementOf "SELECT total FROM orders WHERE id = @id" [ "id", BoundValue.Int 1L ])
            with
            | Ok bytes -> Expect.stringContains (Text.Encoding.UTF8.GetString bytes) "9.5" "the bound statement ran"
            | Error err -> failtestf "expected the bound statement to run; got %A" err
        }
    ]

/// A SQLite database file seeded with one row, deleted after `body`.
let private withSqliteFile (body: string -> Async<unit>) = async {
    let path =
        Path.Combine(Path.GetTempPath(), $"toolup-sql-posture-{Guid.NewGuid():N}.db")

    let connectionString = $"Data Source=%s{path}"

    try
        do
            use writer = new Microsoft.Data.Sqlite.SqliteConnection(connectionString)
            writer.Open()
            execute writer "CREATE TABLE orders (id INTEGER NOT NULL, total REAL)"
            execute writer "INSERT INTO orders VALUES (1, 9.5)"

        do! body connectionString
    finally
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools()

        try
            File.Delete path
        with _ ->
            ()
}

let private rowCount (connectionString: string) =
    use reader = new Microsoft.Data.Sqlite.SqliteConnection(connectionString)
    reader.Open()
    scalar reader "SELECT count(*) FROM orders"

let private readOnlyTests =
    testList "read_only, SQLite in process" [
        testCaseAsync "a read-only source reads, and a write is refused by the database as a named error"
        <| withSqliteFile (fun connectionString -> async {
            let source, ctx =
                sourceFor [
                    "backend", "sqlite"
                    "connection_string", connectionString
                    "read_only", "true"
                ]

            match! source.Query(ctx, "orders") with
            | Ok bytes -> Expect.stringContains (Text.Encoding.UTF8.GetString bytes) "9.5" "it reads"
            | Error err -> failtestf "expected the read to run; got %A" err

            let! refused = source.Query(ctx, "INSERT INTO orders VALUES (2, 1.0)")
            let message = refused |> schemaMismatch "the write"
            Expect.stringContains message "refused a write" "named as a write refusal"
            Expect.stringContains message "read_only=true" "names the declared intent"
            Expect.stringContains message "'posture'" "names the source"

            let! bound =
                BoundQuery.run source ctx (statementOf "DELETE FROM orders WHERE id = @id" [ "id", BoundValue.Int 1L ])

            match bound with
            | Error(BoundStatementError.Failed(SchemaMismatch message)) ->
                Expect.stringContains message "refused a write" "the bound form is refused the same way"
            | other -> failtestf "expected the bound write refused; got %A" other

            Expect.equal (rowCount connectionString) "1" "nothing was written"
        })

        testCaseAsync "without read_only the same write runs, so the refusal is the key's doing"
        <| withSqliteFile (fun connectionString -> async {
            let source, ctx =
                sourceFor [ "backend", "sqlite"; "connection_string", connectionString ]

            match! source.Query(ctx, "INSERT INTO orders VALUES (2, 1.0)") with
            | Ok _ -> Expect.equal (rowCount connectionString) "2" "written"
            | Error err -> failtestf "expected the write to run; got %A" err
        })

        testCaseAsync "a backend that cannot honour the key refuses it at connect, by name"
        <| async {
            let connectionString, keeper = sqliteMemory ()
            use _ = keeper

            let source, ctx =
                sourceFor [
                    "backend", "sqlite"
                    "connection_string", connectionString
                    "read_only", "true"
                ]

            let! connected = source.Connect ctx
            Expect.stringContains (connected |> schemaMismatch "Connect") "read_only" "never silently ignored"
        }
    ]

let private preflightTests =
    let sql id scope =
        config id "Sql" (("backend", "postgres") :: ("connection_string", "Host=db") :: scope)

    let run (sources: DataSourceConfig list) =
        let reported = ResizeArray<string>()

        let validator =
            SqlDataSource.SourcePosturePreflight(sources, reported.Add) :> ConfigValidation.IConfigValidator

        validator.Validate() |> Async.RunSynchronously, List.ofSeq reported

    testList "SourcePosturePreflight" [
        test "a deployment with no sources reports nothing and passes" {
            let result, reported = run []
            Expect.equal result ConfigValidation.ValidationResult.Ok "passes"
            Expect.isEmpty reported "says nothing"
        }

        test "one line per source, and a warning naming each source that admits raw text and may write" {
            let sources = [
                sql "open" []
                sql "bound" [ "statement_policy", "bound-only" ]
                sql "reader" [ "read_only", "true" ]
            ]

            Expect.equal
                (SqlDataSource.SourcePosturePreflight.lines sources)
                [
                    "source 'open': statement_policy=raw-allowed, read_only=false (admits raw statement text and may write)"
                    "source 'bound': statement_policy=bound-only, read_only=false"
                    "source 'reader': statement_policy=raw-allowed, read_only=true"
                ]
                "one line each, policy and intent"

            let result, reported = run sources

            match result with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "1 of 3" "counts"
                Expect.stringContains message "'open'" "names the open source"
                Expect.isFalse (message.Contains "'bound'") "not the closed ones"
            | other -> failtestf "expected a warning; got %A" other

            match reported with
            | [ report ] ->
                for id in [ "open"; "bound"; "reader" ] do
                    Expect.stringContains report $"source '%s{id}'" "every source is reported"
            | other -> failtestf "expected one report; got %A" other
        }

        test "every source closed reports its lines and passes" {
            let result, reported = run [ sql "bound" [ "statement_policy", "bound-only" ] ]
            Expect.equal result ConfigValidation.ValidationResult.Ok "passes"
            Expect.equal reported.Length 1 "still reported"
        }

        test "an unreadable posture is reported and warned about, never read as the default" {
            let sources = [ sql "typo" [ "read_only", "perhaps" ] ]
            Expect.stringContains (SqlDataSource.SourcePosturePreflight.lines sources |> List.head) "unreadable" "said"

            match fst (run sources) with
            | ConfigValidation.ValidationResult.Warning message -> Expect.stringContains message "'typo'" "named"
            | other -> failtestf "expected a warning; got %A" other
        }

        test "it runs even when SkipPreflight is set: it reads only what it was given" {
            let validator = SqlDataSource.SourcePosturePreflight([], ignore) :> obj
            Expect.isTrue (validator :? ConfigValidation.IStructuralClassValidator) "structural class"
        }
    ]

// ─── The armed server legs ────────────────────────────────────────

let private serverLegs =
    let legs = [
        PostgreSql, "postgres", "TOOLUP_SQL_BIND_POSTGRES"
        MySql, "mysql", "TOOLUP_SQL_BIND_MYSQL"
        SqlServer, "sqlserver", "TOOLUP_SQL_BIND_SQLSERVER"
        Oracle, "oracle", "TOOLUP_SQL_BIND_ORACLE"
        ClickHouse, "clickhouse", "TOOLUP_SQL_BIND_CLICKHOUSE"
    ]

    let openFor (backend: SqlBackend) (connectionString: string) : DbConnection =
        let connection: DbConnection =
            match backend with
            | PostgreSql -> new Npgsql.NpgsqlConnection(connectionString)
            | MySql -> new MySqlConnector.MySqlConnection(connectionString)
            | SqlServer -> new Microsoft.Data.SqlClient.SqlConnection(connectionString)
            | Oracle -> new Oracle.ManagedDataAccess.Client.OracleConnection(connectionString)
            | ClickHouse -> new ClickHouse.Client.ADO.ClickHouseConnection(connectionString)
            | Sqlite -> new Microsoft.Data.Sqlite.SqliteConnection(connectionString)

        connection.Open()
        connection

    let createTable (backend: SqlBackend) =
        match backend with
        | ClickHouse -> "CREATE TABLE toolup_posture (k Int64) ENGINE = Memory"
        | _ -> "CREATE TABLE toolup_posture (k INTEGER)"

    let dropTable (connection: DbConnection) =
        try
            execute connection "DROP TABLE toolup_posture"
        with _ ->
            ()

    testList "read_only, server backends (armed legs)" [
        for backend, name, variable in legs do
            match env variable with
            | None -> ptestCase $"%s{name}: set %s{variable} to a scratch database's connection string" ignore
            | Some connectionString ->
                testCaseAsync $"%s{name}: a write is refused by the database, or the key at connect"
                <| async {
                    use writer = openFor backend connectionString
                    dropTable writer
                    execute writer (createTable backend)

                    try
                        let source, ctx =
                            sourceFor [ "backend", name; "connection_string", connectionString; "read_only", "true" ]

                        match! source.Connect ctx with
                        | Error(SchemaMismatch message) ->
                            // SQL Server on a writable primary: no read-only session exists.
                            Expect.equal backend SqlServer "only SQL Server refuses at connect"
                            Expect.stringContains message "read_only" "refused by name"
                        | Error err -> failtestf "expected a connect or a named refusal; got %A" err
                        | Ok() ->
                            let! refused = source.Query(ctx, "INSERT INTO toolup_posture VALUES (1)")
                            Expect.stringContains (refused |> schemaMismatch "the write") "refused a write" "named"

                            Expect.equal (scalar writer "SELECT count(*) FROM toolup_posture") "0" "nothing was written"
                    finally
                        dropTable writer
                }
    ]

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Sql — source posture" [
        postureTests
        connectionStringTests
        policyTests
        readOnlyTests
        preflightTests
        serverLegs
    ]