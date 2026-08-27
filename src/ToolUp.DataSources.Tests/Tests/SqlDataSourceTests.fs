// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.SqlDataSourceTests

open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Tests.Support
open ToolUp.DataSources.Tests.Support.TestFakes
open DataManagementTypes

// ─── ToolUp.DataSources.Sql ───────────────────────────────────────
//
// The pure half — backend parsing, catalogue SQL for all three
// dialect shapes, connection-string composition, statement resolution
// and type classification — runs always. The vendor round-trip is the
// env-gated `RemoteDataSourceContract` arm at the bottom.

let private settings backend connectionString scope =
    SqlDataSource.readSettings (Map.ofList (("backend", backend) :: ("connection_string", connectionString) :: scope))

let private parsingTests =
    testList "readSettings" [
        test "accepts every documented backend alias" {
            let expected = [
                "postgres", PostgreSql
                "postgresql", PostgreSql
                "npgsql", PostgreSql
                "redshift-wire", PostgreSql
                "mysql", MySql
                "mariadb", MySql
                "sqlserver", SqlServer
                "mssql", SqlServer
                "azuresql", SqlServer
                "sqlite", Sqlite
                "oracle", Oracle
                "clickhouse", ClickHouse
            ]

            for alias, backend in expected do
                Expect.equal (SqlDialect.parseBackend alias) (Ok backend) $"'{alias}'"
                Expect.equal (SqlDialect.parseBackend (alias.ToUpperInvariant())) (Ok backend) $"'{alias}' upper-cased"
        }

        test "an unknown backend failure names every accepted spelling" {
            match SqlDialect.parseBackend "cassandra" with
            | Error(SchemaMismatch message) ->
                for alias in SqlDialect.acceptedBackends do
                    Expect.stringContains message alias $"names '{alias}'"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "backend and connection_string are both required" {
            match SqlDataSource.readSettings (Map.ofList [ "connection_string", "Host=x" ]) with
            | Error(SchemaMismatch message) -> Expect.stringContains message "backend" "names the missing key"
            | other -> failtestf "expected SchemaMismatch; got %A" other

            match SqlDataSource.readSettings (Map.ofList [ "backend", "postgres" ]) with
            | Error(SchemaMismatch message) -> Expect.stringContains message "connection_string" "names the missing key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "an absent schema takes the backend's default" {
            let schemaOf backend =
                settings backend "cs" [] |> Result.map _.Schema

            Expect.equal (schemaOf "postgres") (Ok(Some "public")) "postgres defaults to public"
            Expect.equal (schemaOf "sqlserver") (Ok(Some "dbo")) "sqlserver defaults to dbo"
            // These four self-scope in their catalogue query, so a
            // default would be a guess rather than a convenience.
            Expect.equal (schemaOf "mysql") (Ok None) "mysql self-scopes to DATABASE()"
            Expect.equal (schemaOf "oracle") (Ok None) "oracle self-scopes to USER"
            Expect.equal (schemaOf "sqlite") (Ok None) "sqlite has no schema catalogue"
            Expect.equal (schemaOf "clickhouse") (Ok None) "clickhouse excludes its system schemas"
        }

        test "a declared schema overrides the default" {
            Expect.equal
                (settings "postgres" "cs" [ "schema", "analytics" ] |> Result.map _.Schema)
                (Ok(Some "analytics"))
                "declared schema wins"
        }

        test "a non-positive command timeout is refused" {
            match settings "postgres" "cs" [ "command_timeout_seconds", "0" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "command_timeout_seconds" "names the key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

let private connectionStringTests =
    testList "composeConnectionString" [
        test "folds a resolved password into a base connection string" {
            let composed =
                SqlDataSource.composeConnectionString "Host=db;Username=app" (Some "s3cret")

            // Read the result back through ADO rather than matching
            // substrings: `DbConnectionStringBuilder` normalises
            // keyword CASE on the way out (`Host=` becomes `host=`),
            // which is invisible to every provider — ADO keyword
            // matching is case-insensitive — but would make a
            // substring assertion here quietly wrong.
            let round = System.Data.Common.DbConnectionStringBuilder()
            round.ConnectionString <- composed
            Expect.equal (string round["Password"]) "s3cret" "the password is present"
            Expect.equal (string round["Host"]) "db" "the base keys survive"
            Expect.equal (string round["Username"]) "app" "and so do the rest"
        }

        test "never overwrites a password the operator already supplied" {
            let baseString = "Host=db;Username=app;Password=original"
            let composed = SqlDataSource.composeConnectionString baseString (Some "from-store")
            Expect.equal composed baseString "returned byte-identical"
        }

        test "no password resolved leaves the string untouched" {
            // Integrated security, a managed identity, and a local
            // SQLite file all legitimately need no credential.
            Expect.equal
                (SqlDataSource.composeConnectionString "Data Source=app.db" None)
                "Data Source=app.db"
                "unchanged"
        }

        test "a password containing a semicolon survives the builder's quoting" {
            let composed = SqlDataSource.composeConnectionString "Host=db" (Some "pa;ss")
            let round = System.Data.Common.DbConnectionStringBuilder()
            round.ConnectionString <- composed
            Expect.equal (string round["Password"]) "pa;ss" "round-trips through ADO quoting"
        }
    ]

let private catalogueTests =
    testList "catalogue SQL" [
        test "the four ANSI backends scope by table_schema" {
            for backend in [ PostgreSql; MySql; SqlServer; ClickHouse ] do
                match SqlDialect.listTablesSql backend (Some "analytics") with
                | Ok sql ->
                    Expect.stringContains sql "information_schema.tables" $"{backend} uses information_schema"
                    Expect.stringContains sql "'analytics'" $"{backend} scopes to the schema"
                    Expect.stringContains sql "ORDER BY" $"{backend} orders deterministically"
                | Error err -> failtestf "%A: expected Ok; got %A" backend err
        }

        test "an unscoped ANSI listing excludes the system catalogues" {
            let exclusions = [ PostgreSql, "pg_catalog"; SqlServer, "sys"; ClickHouse, "system" ]

            for backend, excluded in exclusions do
                match SqlDialect.listTablesSql backend None with
                | Ok sql -> Expect.stringContains sql excluded $"{backend} excludes {excluded}"
                | Error err -> failtestf "%A: expected Ok; got %A" backend err

            match SqlDialect.listTablesSql MySql None with
            | Ok sql -> Expect.stringContains sql "DATABASE()" "mysql self-scopes to the connected database"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "Oracle uses ALL_TABLES with an upper-cased owner" {
            match SqlDialect.listTablesSql Oracle (Some "hr") with
            | Ok sql ->
                Expect.stringContains sql "ALL_TABLES" "Oracle catalogue view"
                // Oracle folds unquoted identifiers to upper case and
                // STORES them that way, so comparing against 'hr'
                // would match nothing.
                Expect.stringContains sql "'HR'" "owner is upper-cased for the comparison"
            | Error err -> failtestf "expected Ok; got %A" err

            match SqlDialect.listTablesSql Oracle None with
            | Ok sql -> Expect.stringContains sql "OWNER = USER" "unscoped Oracle listing is the connected user's"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "SQLite uses sqlite_master and refuses a declared schema" {
            match SqlDialect.listTablesSql Sqlite None with
            | Ok sql ->
                Expect.stringContains sql "sqlite_master" "SQLite catalogue"
                Expect.stringContains sql "sqlite_%" "internal tables excluded"
            | Error err -> failtestf "expected Ok; got %A" err

            // Silently ignoring a declared schema would leave an
            // operator believing a scope was applied.
            match SqlDialect.listTablesSql Sqlite (Some "main") with
            | Error(SchemaMismatch message) -> Expect.stringContains message "sqlite" "explains why"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "columnsSql returns three projections in ordinal order for every backend" {
            let expectations = [
                PostgreSql, Some "public", [ "column_name"; "data_type"; "is_nullable"; "ordinal_position" ]
                MySql, None, [ "column_name"; "DATABASE()"; "ordinal_position" ]
                SqlServer, Some "dbo", [ "column_name"; "ordinal_position" ]
                ClickHouse, None, [ "column_name"; "ordinal_position" ]
                Oracle, Some "hr", [ "COLUMN_NAME"; "DATA_TYPE"; "NULLABLE"; "COLUMN_ID" ]
                Sqlite, None, [ "pragma_table_info"; "notnull"; "cid" ]
            ]

            for backend, schema, needles in expectations do
                match SqlDialect.columnsSql backend schema "orders" with
                | Ok sql ->
                    for needle in needles do
                        Expect.stringContains sql needle $"{backend} projects {needle}"
                | Error err -> failtestf "%A: expected Ok; got %A" backend err
        }

        test "an unsafe table or schema name is REFUSED, never escaped into the query" {
            // The connectors interpolate identifiers because the six
            // backends share no parameter marker; validation is what
            // makes that safe, so it is asserted per backend rather
            // than sampled.
            for backend in [ PostgreSql; MySql; SqlServer; Sqlite; Oracle; ClickHouse ] do
                match SqlDialect.columnsSql backend None "orders; DROP TABLE users" with
                | Error(SchemaMismatch _) -> ()
                | other -> failtestf "%A: expected the injection-shaped table name to be refused; got %A" backend other

            for backend in [ PostgreSql; MySql; SqlServer; ClickHouse; Oracle ] do
                match SqlDialect.listTablesSql backend (Some "a' OR '1'='1") with
                | Error(SchemaMismatch _) -> ()
                | other -> failtestf "%A: expected the injection-shaped schema to be refused; got %A" backend other
        }

        test "selectAllSql quotes with each backend's own delimiter" {
            let expectations = [
                PostgreSql, "\"public\".\"orders\""
                Oracle, "\"public\".\"orders\""
                Sqlite, "\"public\".\"orders\""
                SqlServer, "[public].[orders]"
                MySql, "`public`.`orders`"
                ClickHouse, "`public`.`orders`"
            ]

            for backend, expected in expectations do
                match SqlDialect.selectAllSql backend (Some "public") "orders" with
                | Ok sql -> Expect.stringContains sql expected $"{backend} qualification"
                | Error err -> failtestf "%A: expected Ok; got %A" backend err
        }
    ]

let private nullabilityTests =
    testList "nullableFromToken" [
        test "ANSI YES/NO and Oracle Y/N read the obvious way" {
            for backend in [ PostgreSql; MySql; SqlServer; ClickHouse; Oracle ] do
                Expect.isTrue (SqlDialect.nullableFromToken backend "YES") $"{backend} YES"
                Expect.isTrue (SqlDialect.nullableFromToken backend "Y") $"{backend} Y"
                Expect.isTrue (SqlDialect.nullableFromToken backend "1") $"{backend} 1 (ClickHouse UInt8)"
                Expect.isFalse (SqlDialect.nullableFromToken backend "NO") $"{backend} NO"
                Expect.isFalse (SqlDialect.nullableFromToken backend "0") $"{backend} 0"
        }

        test "SQLite's notnull token is INVERTED" {
            // pragma_table_info reports `notnull`, so 1 means NOT
            // nullable — the opposite sense from every other backend
            // here, and the easiest thing in this file to get wrong.
            Expect.isFalse (SqlDialect.nullableFromToken Sqlite "1") "notnull = 1 means NOT nullable"
            Expect.isTrue (SqlDialect.nullableFromToken Sqlite "0") "notnull = 0 means nullable"
        }
    ]

let private typeMappingTests =
    testList "toColumnType" [
        test "Postgres spellings the ANSI table alone would miss" {
            Expect.equal
                (SqlDialect.toColumnType PostgreSql "interval")
                DateColumn
                "interval carries no ANSI date token"

            Expect.equal (SqlDialect.toColumnType PostgreSql "oid") NumberColumn "oid is an integer"
            Expect.equal (SqlDialect.toColumnType PostgreSql "jsonb") StringColumn "jsonb renders as text"
            Expect.equal (SqlDialect.toColumnType PostgreSql "boolean") BooleanColumn "boolean"
            Expect.equal (SqlDialect.toColumnType PostgreSql "timestamptz") DateColumn "timestamptz"
            Expect.equal (SqlDialect.toColumnType PostgreSql "numeric(38,9)") NumberColumn "parameterised numeric"
        }

        test "T-SQL `timestamp` is a ROW VERSION, not a date" {
            // The single most misleading type name in the family: the
            // ANSI table sees the token `timestamp` and would classify
            // it DateColumn.
            Expect.equal (SqlDialect.toColumnType SqlServer "timestamp") StringColumn "T-SQL timestamp"
            Expect.equal (SqlDialect.toColumnType SqlServer "rowversion") StringColumn "rowversion"
            Expect.equal (SqlDialect.toColumnType SqlServer "datetime2") DateColumn "datetime2 IS a date"
            Expect.equal (SqlDialect.toColumnType SqlServer "uniqueidentifier") StringColumn "guid renders as text"
        }

        test "ClickHouse wrappers are peeled before classification" {
            // Stripping at the first '(' — what the shared normaliser
            // does — would classify every nullable ClickHouse column
            // as its wrapper name.
            Expect.equal (SqlDialect.toColumnType ClickHouse "Nullable(DateTime64(3))") DateColumn "Nullable peeled"

            Expect.equal
                (SqlDialect.toColumnType ClickHouse "LowCardinality(Nullable(String))")
                StringColumn
                "two wrappers peeled"

            Expect.equal (SqlDialect.toColumnType ClickHouse "Nullable(UInt64)") NumberColumn "numeric under a wrapper"

            Expect.equal
                (SqlDialect.toColumnType ClickHouse "SimpleAggregateFunction(sum, UInt64)")
                NumberColumn
                "the TYPE is the LAST argument of SimpleAggregateFunction"

            // Array is deliberately NOT peeled: an array of numbers is
            // not a number, and renders in CSV as text.
            Expect.equal (SqlDialect.toColumnType ClickHouse "Array(UInt8)") StringColumn "Array is not transparent"
        }

        test "Oracle and MySQL binary families render as text" {
            Expect.equal (SqlDialect.toColumnType Oracle "CLOB") StringColumn "CLOB"
            Expect.equal (SqlDialect.toColumnType Oracle "NUMBER(10,2)") NumberColumn "NUMBER"
            Expect.equal (SqlDialect.toColumnType MySql "longblob") StringColumn "longblob"
            Expect.equal (SqlDialect.toColumnType MySql "year") DateColumn "year is a date"
        }
    ]

let private statementTests =
    testList "resolveStatement" [
        test "a bare identifier expands to SELECT *" {
            match settings "postgres" "cs" [] with
            | Ok settings ->
                match SqlDataSource.resolveStatement settings "orders" with
                | Ok sql -> Expect.stringContains sql "SELECT * FROM \"public\".\"orders\"" "expanded"
                | Error err -> failtestf "expected Ok; got %A" err
            | Error err -> failtestf "settings: %A" err
        }

        test "an actual statement passes through verbatim" {
            match settings "postgres" "cs" [] with
            | Ok settings ->
                let statement =
                    "SELECT id, total FROM orders WHERE created_at > now() - interval '1 day'"

                Expect.equal (SqlDataSource.resolveStatement settings statement) (Ok statement) "untouched"
            | Error err -> failtestf "settings: %A" err
        }
    ]

let private kindTests =
    testList "Kind" [
        test "the connector answers to the documented Kind" {
            Expect.equal SqlDataSource.Kind "Sql" "Kind constant"
            Expect.equal (SqlDataSource.createWithoutSecrets ()).Kind "Sql" "instance Kind"
        }

        test "createWithKind lets a deployment route by backend name instead" {
            Expect.equal (SqlDataSource.createWithKind "Postgres" None).Kind "Postgres" "custom Kind"
        }
    ]

/// Env-gated remote arm. Needs a reachable database and the name of a
/// pre-provisioned READABLE table; nothing is created or written.
let private remoteTests =
    RemoteDataSourceContract.tests
        "Sql"
        [ "TOOLUP_SQL_BACKEND"; "TOOLUP_SQL_CONNECTION_STRING"; "TOOLUP_SQL_TABLE" ]
        (fun () ->
            let backend = envOr "TOOLUP_SQL_BACKEND" ""
            let connectionString = envOr "TOOLUP_SQL_CONNECTION_STRING" ""
            let table = envOr "TOOLUP_SQL_TABLE" ""
            let schema = env "TOOLUP_SQL_SCHEMA"

            let scopeOf (schema: string option) = [
                yield "backend", backend
                yield "connection_string", connectionString
                match schema with
                | Some value -> yield "schema", value
                | None -> ()
            ]

            let mk suffix schema =
                config $"sql-remote-%s{suffix}" "Sql" (scopeOf schema)
                |> context "test-scope" (env "TOOLUP_SQL_PASSWORD")

            {
                Source = SqlDataSource.createWithoutSecrets ()
                Context = mk "main" schema
                // SQLite has no schema catalogue, so its isolated
                // context cannot be "a different schema" — the arm
                // simply reuses the main scope there and the scoping
                // assertion degrades to "the table is not duplicated".
                IsolatedContext =
                    if backend.ToLowerInvariant() = "sqlite" then
                        mk "iso" None
                    else
                        mk "iso" (Some "toolup_no_such_schema")
                Table = table
                SampleSql = envOr "TOOLUP_SQL_SAMPLE_SQL" $"SELECT * FROM %s{table}"
                MissingTableSql = "SELECT * FROM toolup_no_such_table_9c1f"
            })

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Sql" [
        parsingTests
        connectionStringTests
        catalogueTests
        nullabilityTests
        typeMappingTests
        statementTests
        kindTests
        remoteTests
    ]