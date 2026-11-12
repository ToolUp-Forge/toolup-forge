// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.SqlBindingTests

open System
open System.Data.Common
open System.Globalization
open System.Text
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.Platform.Tests.Contracts
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Tests.Support.TestFakes

// ─── ToolUp.DataSources.Sql — bound statements ────────────────────
//
// The scanner and the marker translation are pure, so they are tested per
// backend without a database. The binding laws of the data source contract
// pack then run against SQLite in process, always, and against each of the
// five server backends as an armed leg: Pending without its variable, never
// Failed. An armed leg needs a SCRATCH database: the laws' hostile-value law
// seeds one table (`kept`) and drops it again.

let private allBackends = [ PostgreSql; MySql; SqlServer; Sqlite; Oracle; ClickHouse ]

let private scanned backend text =
    match SqlBinding.scan backend text with
    | Ok pieces -> pieces
    | Error reason -> failtestf "%A: expected %s to scan; refused: %s" backend text reason

let private markers backend text =
    scanned backend text
    |> List.choose (fun piece ->
        match piece with
        | StatementPiece.Marker name -> Some name
        | StatementPiece.Verbatim _ -> None)

let private refusedScan backend text (needle: string) =
    match SqlBinding.scan backend text with
    | Error reason -> Expect.stringContains reason needle $"{backend}: {text} is refused for the right reason"
    | Ok pieces -> failtestf "%A: expected %s to be refused; scanned %A" backend text pieces

let private statementOf (text: string) (parameters: (string * BoundValue) list) =
    parameters
    |> List.fold (fun s (n, v) -> BoundStatement.bind n v s) (BoundStatement.ofText text)

let private translated backend text parameters =
    match SqlBinding.translate backend (statementOf text parameters) with
    | Ok command -> command
    | Error refusal -> failtestf "%A: expected %s to translate; got %A" backend text refusal

let private scannerTests =
    testList "the marker scanner" [
        test "a marker outside a literal is found on every backend, and a repeat names it again" {
            for backend in allBackends do
                Expect.equal
                    (markers backend "SELECT a FROM t WHERE x = @p AND y > @q OR x = @p")
                    [ "p"; "q"; "p" ]
                    $"{backend} finds every marker"
        }

        test "a marker inside a string literal, a quoted identifier or a comment is left alone" {
            for backend in allBackends do
                let text = "SELECT '@a', \"@b\", 'it''s @c' -- @d\n, @real /* @e */ FROM t"

                Expect.equal (markers backend text) [ "real" ] $"{backend} reads only the real marker"
        }

        test "each backend's own quoting is skipped" {
            let cases = [
                MySql, "SELECT `@a` FROM t WHERE x = @p"
                ClickHouse, "SELECT `@a` FROM t WHERE x = @p"
                Sqlite, "SELECT `@a`, [@b] FROM t WHERE x = @p"
                SqlServer, "SELECT [@a]]@b] FROM t WHERE x = @p"
                PostgreSql, "SELECT $$ @a ; @b $$, $tag$ @c $tag$ FROM t WHERE x = @p"
                PostgreSql, "SELECT E'\\' @a' FROM t WHERE x = @p"
                ClickHouse, "SELECT '\\' @a' FROM t WHERE x = @p"
                MySql, "SELECT 1 # @a ; DROP TABLE t\n, @p"
                ClickHouse, "SELECT 1 # @a\n, @p"
            ]

            for backend, text in cases do
                Expect.equal (markers backend text) [ "p" ] $"{backend}: {text}"
        }

        test "nested block comments are read as the backend reads them" {
            // PostgreSQL and SQL Server nest; the others close at the first */.
            for backend in [ PostgreSql; SqlServer ] do
                Expect.equal (markers backend "SELECT /* a /* b */ @x */ @p") [ "p" ] $"{backend} nests"

            for backend in [ MySql; Sqlite; Oracle; ClickHouse ] do
                Expect.equal (markers backend "SELECT /* a /* b */ @x, @p") [ "x"; "p" ] $"{backend} does not nest"
        }

        test "MySQL reads -- as a comment only before whitespace, and runs the body of /*! */" {
            Expect.equal (markers MySql "SELECT 1--@p") [ "p" ] "1--@p is arithmetic on MySQL"
            Expect.equal (markers MySql "SELECT 1 /*! + @p */") [ "p" ] "an executable comment is code"
            Expect.equal (markers PostgreSql "SELECT 1--@p") [] "PostgreSQL reads -- as a comment"
        }

        test "a system variable is never a marker" {
            Expect.equal (markers SqlServer "SELECT @@ROWCOUNT, @p") [ "p" ] "@@ROWCOUNT"
            Expect.equal (markers MySql "SELECT @@session.sql_mode, @p") [ "p" ] "@@session"
        }

        test "verbatim text and markers rebuild the statement exactly" {
            for backend in allBackends do
                let text = "SELECT 'a;b', @p -- tail ; x\n"

                let rebuilt =
                    scanned backend text
                    |> List.map (fun piece ->
                        match piece with
                        | StatementPiece.Verbatim run -> run
                        | StatementPiece.Marker name -> "@" + name)
                    |> String.concat ""

                Expect.equal rebuilt text $"{backend} loses nothing"
        }

        test "a second statement is refused; a trailing separator and one inside a literal are not" {
            for backend in allBackends do
                refusedScan backend "SELECT @p; DROP TABLE t" "more than one statement"
                refusedScan backend "SELECT 1;SELECT 2" "more than one statement"
                Expect.equal (markers backend "SELECT @p;") [ "p" ] $"{backend} admits a trailing separator"
                Expect.equal (markers backend "SELECT @p ; -- done\n") [ "p" ] $"{backend}: a comment after it"
                Expect.equal (markers backend "SELECT ';', @p") [ "p" ] $"{backend}: a separator in a literal"
        }

        test "a text ending inside a literal, an identifier or a comment is refused" {
            for backend in allBackends do
                refusedScan backend "SELECT 'open, @p" "ends inside"
                refusedScan backend "SELECT \"open, @p" "ends inside"
                refusedScan backend "SELECT 1 /* open @p" "ends inside"

            refusedScan PostgreSql "SELECT $tag$ open" "ends inside"
        }

        test "a backslash a session setting reads two ways is refused, not guessed at" {
            // MySQL (`sql_mode`) and PostgreSQL (`standard_conforming_strings`).
            refusedScan MySql "SELECT 'a\\' ; DROP TABLE t; '' , @p" "read two ways"
            refusedScan MySql "SELECT \"a\\\" , @p" "read two ways"
            refusedScan PostgreSql "SELECT 'a\\' , @p" "read two ways"

            // Read one way only: an E-string, and the backends whose reading is fixed.
            Expect.equal (markers PostgreSql "SELECT E'a\\'', @p") [ "p" ] "an E-string escapes"

            for backend in [ SqlServer; Sqlite; Oracle ] do
                Expect.equal (markers backend "SELECT 'a\\', @p") [ "p" ] $"{backend} never escapes"
        }

        test "a marker in the backend's own syntax is refused" {
            refusedScan PostgreSql "SELECT $1" "positional"
            refusedScan Oracle "SELECT :x FROM DUAL" "':name'"
            refusedScan Sqlite "SELECT :x" "':name'"
            refusedScan Sqlite "SELECT $x" "'$name'"
            refusedScan Sqlite "SELECT ?" "positional"
            refusedScan MySql "SELECT ?" "positional"
            // `?` is an operator on PostgreSQL, and `::` a cast.
            Expect.equal (markers PostgreSql "SELECT j ? 'k', @p::text") [ "p" ] "PostgreSQL operators"
        }
    ]

let private translationTests =
    testList "marker translation" [
        test "each backend's marker form" {
            let expectations = [
                SqlServer, "SELECT @a, @b, @a"
                MySql, "SELECT @a, @b, @a"
                Sqlite, "SELECT @a, @b, @a"
                PostgreSql, "SELECT $1, $2, $1"
                Oracle, "SELECT :b1, :b2, :b1"
                ClickHouse, "SELECT {a:Int64}, {b:String}, {a:Int64}"
            ]

            for backend, expected in expectations do
                let command =
                    translated backend "SELECT @a, @b, @a" [ "a", BoundValue.Int 1L; "b", BoundValue.Text "x" ]

                Expect.equal command.Text expected $"{backend} form"

                Expect.equal
                    (command.Bindings |> List.map _.Value)
                    [ BoundValue.Int 1L; BoundValue.Text "x" ]
                    "once each"
        }

        test "the provider names, positional where the marker is" {
            let names backend =
                (translated backend "SELECT @a, @b" [ "a", BoundValue.Int 1L; "b", BoundValue.Int 2L ]).Bindings
                |> List.map _.Name

            Expect.equal (names PostgreSql) [ None; None ] "positional"
            Expect.equal (names SqlServer) [ Some "@a"; Some "@b" ] "named"
            Expect.equal (names Oracle) [ Some "b1"; Some "b2" ] "prefixed"
            Expect.equal (names ClickHouse) [ Some "a"; Some "b" ] "typed"
        }

        test "an exact decimal never passes through a host decimal" {
            let wide = "1234567890123456789012345678.9012345678"

            let pg = translated PostgreSql "SELECT @d" [ "d", BoundValue.Decimal wide ]
            Expect.equal pg.Text "SELECT CAST($1 AS numeric)" "PostgreSQL casts the digits server-side"

            let ch = translated ClickHouse "SELECT @d" [ "d", BoundValue.Decimal wide ]
            Expect.equal ch.Text "SELECT {d:Decimal(38, 10)}" "ClickHouse names the exact width"

            let chNull =
                translated ClickHouse "SELECT @d" [ "d", BoundValue.Null DatasetDType.Int ]

            Expect.equal chNull.Text "SELECT {d:Nullable(Int64)}" "a typed null is Nullable"
        }

        test "a decimal beyond what the backend carries exactly is refused before the provider" {
            let digits39 = String('9', 39)

            for backend in [ SqlServer; Oracle ] do
                match SqlBinding.translate backend (statementOf "SELECT @d" [ "d", BoundValue.Decimal digits39 ]) with
                | Error(BoundStatementError.Failed(SchemaMismatch message)) ->
                    Expect.stringContains message "'d'" $"{backend} names the parameter"
                | other -> failtestf "%A: expected the 39-digit decimal refused; got %A" backend other

            // 38 digits fit SQL Server; a tiny fraction has one significant digit on Oracle.
            translated SqlServer "SELECT @d" [ "d", BoundValue.Decimal(String('9', 38)) ]
            |> ignore

            translated Oracle "SELECT @d FROM DUAL" [ "d", BoundValue.Decimal("0." + String('0', 60) + "1") ]
            |> ignore
        }

        test "a marker no parameter supplies, and a parameter the text does not use, are refused by name" {
            for backend in allBackends do
                match SqlBinding.translate backend (statementOf "SELECT @a, @b" [ "a", BoundValue.Int 1L ]) with
                | Error(BoundStatementError.Refused(BindingRefusal.MissingParameter "b")) -> ()
                | other -> failtestf "%A: expected MissingParameter b; got %A" backend other

                match
                    SqlBinding.translate
                        backend
                        (statementOf "SELECT @a" [ "a", BoundValue.Int 1L; "b", BoundValue.Int 2L ])
                with
                | Error(BoundStatementError.Refused(BindingRefusal.UnusedParameter "b")) -> ()
                | other -> failtestf "%A: expected UnusedParameter b; got %A" backend other

                // A marker-shaped sequence inside a literal is not a use.
                match SqlBinding.translate backend (statementOf "SELECT '@a'" [ "a", BoundValue.Int 1L ]) with
                | Error(BoundStatementError.Refused(BindingRefusal.UnusedParameter "a")) -> ()
                | other -> failtestf "%A: expected UnusedParameter a; got %A" backend other
        }

        test "the kinds each backend binds" {
            let all = Set.ofList BoundValue.kinds

            for backend in [ PostgreSql; SqlServer; Sqlite; ClickHouse ] do
                Expect.equal (SqlBinding.bindableKinds backend) all $"{backend} binds every kind"

            Expect.equal (SqlBinding.bindableKinds MySql) (all.Remove DatasetDType.Decimal) "MySQL refuses the decimal"
            Expect.equal (SqlBinding.bindableKinds Oracle) (all.Remove DatasetDType.Bool) "Oracle refuses the boolean"
        }
    ]

// ─── The binding laws, against a real database ────────────────────

/// One database the laws run against: its backend, the connection scope the
/// connector reads, and a raw connection for seeding.
type private Target = {
    Backend: SqlBackend
    Scope: (string * string) list
    Open: unit -> DbConnection
}

/// The connector with every call's `ConnectionScope` pointed at `target`,
/// declaring the kinds its backend binds. The contract pack's call context
/// carries no scope of its own.
let private boundTo (target: Target) (inner: IDataSource) : IDataSource =
    let rebind (ctx: DataSourceCallContext) = {
        ctx with
            Config = {
                ctx.Config with
                    Kind = inner.Kind
                    ConnectionScope = Map.ofList target.Scope
            }
    }

    let binds =
        { new IBindsStatements with
            member _.BindableKinds = SqlBinding.bindableKinds target.Backend

            member _.QueryBound(ctx, statement) =
                inner.Capabilities.Binds.Value.QueryBound(rebind ctx, statement)
        }

    { new IDataSource with
        member _.Kind = inner.Kind

        member _.Capabilities = {
            inner.Capabilities with
                Binds = Some binds
        }

        member _.Connect ctx = inner.Connect(rebind ctx)
        member _.ListTables ctx = inner.ListTables(rebind ctx)
        member _.GetSchema(ctx, table) = inner.GetSchema(rebind ctx, table)
        member _.Query(ctx, sql) = inner.Query(rebind ctx, sql)
    }

let private quoteName (backend: SqlBackend) (name: string) =
    match backend with
    | SqlServer -> $"[%s{name}]"
    | MySql
    | ClickHouse -> $"`%s{name}`"
    | PostgreSql
    | Oracle
    | Sqlite -> $"\"%s{name}\""

let private textType (backend: SqlBackend) =
    match backend with
    | PostgreSql
    | MySql
    | Sqlite -> "TEXT"
    | SqlServer -> "NVARCHAR(4000)"
    | Oracle -> "NVARCHAR2(2000)"
    | ClickHouse -> "String"

let private execute (connection: DbConnection) (sql: string) =
    use command = connection.CreateCommand()
    command.CommandText <- sql
    command.ExecuteNonQuery() |> ignore

/// Seed a table from the contract's CSV bytes: a header and rows of plain
/// cells, every column text. Test fixture text only, so its cells are written
/// as literals.
let private seeder (target: Target) : IDataSourceContract.Seeder =
    fun _ table bytes ->
        let lines =
            (Encoding.UTF8.GetString bytes).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray

        let header = (List.head lines).Split ','
        let quoted = quoteName target.Backend
        use connection = target.Open()
        execute connection $"DROP TABLE IF EXISTS %s{quoted table}"

        let columns =
            header |> Array.map (fun c -> $"%s{quoted c} %s{textType target.Backend}")

        let engine =
            if target.Backend = ClickHouse then
                " ENGINE = Memory"
            else
                ""

        execute connection $"""CREATE TABLE %s{quoted table} (%s{String.Join(", ", columns)})%s{engine}"""

        for row in List.tail lines do
            let cells =
                row.Split ',' |> Array.map (fun cell -> "'" + cell.Replace("'", "''") + "'")

            execute connection $"""INSERT INTO %s{quoted table} VALUES (%s{String.Join(", ", cells)})"""

/// How the laws speak `backend`: a select of the parameters, and a cell read
/// back as the kind it was bound as.
let private harness (backend: SqlBackend) : IDataSourceContract.BindingHarness =
    let select (names: string list) =
        let columns = names |> List.map BoundStatement.marker |> String.concat ", "

        if backend = Oracle then
            $"SELECT %s{columns} FROM DUAL"
        else
            $"SELECT %s{columns}"

    let read (kind: DatasetDType) (cell: string) =
        if kind = DatasetDType.Bool then
            // SQLite and MySQL hand a boolean back as an integer.
            match cell.ToLowerInvariant() with
            | "1"
            | "true" -> Some(BoundValue.Bool true)
            | "0"
            | "false" -> Some(BoundValue.Bool false)
            | _ -> None
        elif kind = DatasetDType.Timestamp then
            // A backend with no offset hands back the UTC instant it was bound as.
            match DateTimeOffset.TryParse(cell, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) with
            | true, value -> Some(BoundValue.Timestamp value)
            | false, _ -> None
        else
            IDataSourceContract.BindingHarness.invariantRead kind cell

    {
        Select = select
        Lookup = None
        Row = IDataSourceContract.BindingHarness.csvRow
        Read = read
    }

let private lawsFor (name: string) (target: Target) =
    testList name [
        IDataSourceContract.capabilityLaws name (fun () -> SqlDataSource.createWithoutSecrets ())
        IDataSourceContract.bindingLaws name (harness target.Backend) (fun () ->
            boundTo target (SqlDataSource.createWithoutSecrets ()), seeder target)
    ]

/// A private in-memory SQLite database, alive while its keeper connection is
/// open: every connection the connector opens names the same one.
let private sqliteTarget () : Target =
    let connectionString =
        $"Data Source=toolup-sql-bind-{Guid.NewGuid():N};Mode=Memory;Cache=Shared"

    let keeper = new Microsoft.Data.Sqlite.SqliteConnection(connectionString)
    keeper.Open()

    {
        Backend = Sqlite
        Scope = [ "backend", "sqlite"; "connection_string", connectionString ]
        Open =
            fun () ->
                GC.KeepAlive keeper
                let connection = new Microsoft.Data.Sqlite.SqliteConnection(connectionString)
                connection.Open()
                connection :> DbConnection
    }

let private sqliteTests =
    let target = sqliteTarget ()

    let source = boundTo target (SqlDataSource.createWithoutSecrets ())

    let ctx = config "sql-bind" "Sql" [] |> context "test-scope" None

    testList "SQLite, in process" [
        lawsFor "SqlDataSource (sqlite)" target

        testCaseAsync "the bound catalogue lists and describes a table"
        <| async {
            use connection = target.Open()
            execute connection "DROP TABLE IF EXISTS orders"
            execute connection "CREATE TABLE orders (id INTEGER NOT NULL, total REAL)"

            match! source.ListTables ctx with
            | Ok tables -> Expect.contains tables "orders" "the table is listed"
            | Error err -> failtestf "ListTables: %A" err

            match! source.GetSchema(ctx, "orders") with
            | Ok schema ->
                Expect.equal (schema.Columns |> List.map _.Name) [ "id"; "total" ] "the columns, bound by name"
            | Error err -> failtestf "GetSchema: %A" err
        }

        testCaseAsync "a second statement never reaches the provider"
        <| async {
            use connection = target.Open()
            execute connection "DROP TABLE IF EXISTS survivor"
            execute connection "CREATE TABLE survivor (k TEXT)"

            let statement =
                statementOf "SELECT @p; DROP TABLE survivor" [ "p", BoundValue.Int 1L ]

            match! BoundQuery.run source ctx statement with
            | Error(BoundStatementError.Failed(SchemaMismatch message)) ->
                Expect.stringContains message "one statement per call" "the refusal says why"
            | other -> failtestf "expected the two-statement text refused; got %A" other

            match! source.ListTables ctx with
            | Ok tables -> Expect.contains tables "survivor" "the second statement did not run"
            | Error err -> failtestf "ListTables: %A" err
        }

        testCaseAsync "the statement's timeout is the command's"
        <| async {
            let statement = {
                statementOf "SELECT @p" [ "p", BoundValue.Int 7L ] with
                    Timeout = Some(TimeSpan.FromSeconds 5.0)
            }

            match! BoundQuery.run source ctx statement with
            | Ok bytes -> Expect.equal (IDataSourceContract.BindingHarness.csvRow bytes) (Ok [ Some "7" ]) "it ran"
            | Error err -> failtestf "expected the statement to run; got %A" err
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

    let openFor (backend: SqlBackend) (connectionString: string) () : DbConnection =
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

    testList "server backends (armed legs)" [
        for backend, name, variable in legs do
            match env variable with
            | None -> ptestCase $"%s{name}: set %s{variable} to a scratch database's connection string" ignore
            | Some connectionString ->
                lawsFor $"SqlDataSource (%s{name})" {
                    Backend = backend
                    Scope = [ "backend", name; "connection_string", connectionString ]
                    Open = openFor backend connectionString
                }
    ]

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Sql — bound statements" [ scannerTests; translationTests; sqliteTests; serverLegs ]