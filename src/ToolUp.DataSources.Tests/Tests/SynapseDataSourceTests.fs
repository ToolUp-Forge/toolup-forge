// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.SynapseDataSourceTests

open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Synapse
open ToolUp.DataSources.Tests.Support
open ToolUp.DataSources.Tests.Support.TestFakes
open DataManagementTypes

// ─── ToolUp.DataSources.Synapse ───────────────────────────────────

let private read scope =
    SynapseDataSource.readSettings (Map.ofList scope)

let private baseScope = [
    "server", "myws-ondemand.sql.azuresynapse.net"
    "database", "analytics"
    "user", "svc_ingest"
]

let private parsingTests =
    testList "readSettings" [
        test "server and database are required" {
            match read [ "database", "d" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "server" "names the key"
            | other -> failtestf "expected SchemaMismatch; got %A" other

            match read [ "server", "s" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "database" "names the key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "auth defaults to SQL login, which then requires a user" {
            match read baseScope with
            | Ok settings ->
                Expect.equal settings.Auth SqlLogin "default auth"
                Expect.equal settings.Port 1433 "default TDS port"
                Expect.equal settings.Schema "dbo" "default schema"
                // Synapse SERVERLESS pools cold-start, so a short
                // connect timeout is a false negative waiting to
                // happen.
                Expect.equal settings.ConnectTimeoutSeconds 30 "default connect timeout"
            | Error err -> failtestf "expected Ok; got %A" err

            match read [ "server", "s"; "database", "d" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "user" "sql auth needs a user"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "the AAD modes need no user" {
            for auth, expected in
                [
                    "aad-token", AadToken
                    "aad-default", AadDefault
                    "managed-identity", AadDefault
                ] do
                match read [ "server", "s"; "database", "d"; "auth", auth ] with
                | Ok settings -> Expect.equal settings.Auth expected $"'{auth}'"
                | Error err -> failtestf "expected Ok for %s; got %A" auth err
        }

        test "an unknown auth mode names the three accepted ones" {
            match SynapseDataSource.parseAuth (Some "kerberos") with
            | Error(SchemaMismatch message) ->
                Expect.stringContains message "sql" "names sql"
                Expect.stringContains message "aad-token" "names aad-token"
                Expect.stringContains message "aad-default" "names aad-default"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

let private connectionStringTests =
    testList "composeConnectionString" [
        test "SQL login carries the user and the resolved password" {
            match read baseScope with
            | Ok settings ->
                let composed = SynapseDataSource.composeConnectionString settings (Some "s3cret")
                Expect.stringContains composed "myws-ondemand.sql.azuresynapse.net,1433" "server and port"
                Expect.stringContains composed "svc_ingest" "user"
                Expect.stringContains composed "s3cret" "password"
                // Stating Encrypt means a future provider default
                // cannot silently downgrade the transport.
                Expect.stringContains composed "Encrypt=True" "TLS is stated, not assumed"
            | Error err -> failtestf "settings: %A" err
        }

        test "the AAD modes put no credential in the connection string" {
            // Under aad-token the credential is an access token set on
            // the connection object; leaking it into the connection
            // string would put a bearer token into every ADO
            // diagnostic that echoes one.
            for auth in [ "aad-token"; "aad-default" ] do
                match read [ "server", "s"; "database", "d"; "auth", auth ] with
                | Ok settings ->
                    let composed = SynapseDataSource.composeConnectionString settings (Some "a-token")
                    Expect.isFalse (composed.Contains "a-token") $"{auth} keeps the credential out of the string"
                | Error err -> failtestf "expected Ok for %s; got %A" auth err
        }

        test "a declared port overrides 1433" {
            match read (baseScope @ [ "port", "1444" ]) with
            | Ok settings ->
                Expect.stringContains (SynapseDataSource.composeConnectionString settings None) ",1444" "port"
            | Error err -> failtestf "settings: %A" err
        }
    ]

let private catalogueTests =
    testList "SynapseCatalogue" [
        test "listTablesSql scopes to the schema and orders deterministically" {
            match SynapseCatalogue.listTablesSql "dbo" with
            | Ok sql ->
                Expect.stringContains sql "INFORMATION_SCHEMA.TABLES" "catalogue view"
                Expect.stringContains sql "'dbo'" "scoped"
                Expect.stringContains sql "ORDER BY" "ordered"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "columnsSql projects name, type and nullability in ordinal order" {
            match SynapseCatalogue.columnsSql "dbo" "orders" with
            | Ok sql ->
                for needle in
                    [
                        "COLUMN_NAME"
                        "DATA_TYPE"
                        "IS_NULLABLE"
                        "'dbo'"
                        "'orders'"
                        "ORDINAL_POSITION"
                    ] do
                    Expect.stringContains sql needle $"projects {needle}"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "an injection-shaped schema or table is REFUSED, never escaped in" {
            match SynapseCatalogue.columnsSql "dbo" "orders'; DROP TABLE users --" with
            | Error(SchemaMismatch _) -> ()
            | other -> failtestf "expected the table name to be refused; got %A" other

            match SynapseCatalogue.listTablesSql "a' OR '1'='1" with
            | Error(SchemaMismatch _) -> ()
            | other -> failtestf "expected the schema to be refused; got %A" other
        }

        test "selectAllSql brackets both parts" {
            match SynapseCatalogue.selectAllSql "dbo" "orders" with
            | Ok sql -> Expect.equal sql "SELECT * FROM [dbo].[orders]" "bracketed"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "nullability reads INFORMATION_SCHEMA's YES/NO" {
            Expect.isTrue (SynapseCatalogue.nullableFromToken "YES") "YES"
            Expect.isTrue (SynapseCatalogue.nullableFromToken "yes") "case-insensitive"
            Expect.isFalse (SynapseCatalogue.nullableFromToken "NO") "NO"
            Expect.isFalse (SynapseCatalogue.nullableFromToken null) "null reads NOT nullable"
        }

        test "T-SQL type names classify, including the row-version trap" {
            let cases = [
                "bit", BooleanColumn
                "int", NumberColumn
                "bigint", NumberColumn
                "decimal(18,2)", NumberColumn
                "money", NumberColumn
                "date", DateColumn
                "datetime2", DateColumn
                "datetimeoffset", DateColumn
                "nvarchar(max)", StringColumn
                "uniqueidentifier", StringColumn
                "varbinary(max)", StringColumn
                // `timestamp` in T-SQL is a ROW VERSION, not a date.
                "timestamp", StringColumn
                "rowversion", StringColumn
            ]

            for native, expected in cases do
                Expect.equal (SynapseCatalogue.toColumnType native) expected $"'{native}'"
        }
    ]

let private statementTests =
    testList "resolveStatement" [
        test "a bare identifier expands, a statement passes through" {
            match read baseScope with
            | Ok settings ->
                Expect.equal
                    (SynapseDataSource.resolveStatement settings "orders")
                    (Ok "SELECT * FROM [dbo].[orders]")
                    "expanded"

                let statement = "SELECT TOP 10 * FROM dbo.orders ORDER BY id DESC"
                Expect.equal (SynapseDataSource.resolveStatement settings statement) (Ok statement) "verbatim"
            | Error err -> failtestf "settings: %A" err
        }
    ]

let private kindTests =
    testList "Kind" [
        test "the connector answers to the documented Kind" {
            Expect.equal SynapseDataSource.Kind "Synapse" "Kind constant"
            Expect.equal (SynapseDataSource.createWithDefaultCredentials ()).Kind "Synapse" "instance Kind"
        }

        testCaseAsync "SQL auth with no credential is CredentialMissing, not a connection attempt"
        <| async {
            let source = SynapseDataSource.create (InMemorySecretStore())
            let ctx = config "syn-1" "Synapse" baseScope |> context "test-scope" None

            match! source.Connect ctx with
            | Error(CredentialMissing key) -> Expect.equal key "synapse-credential" "names the key"
            | other -> failtestf "expected CredentialMissing; got %A" other
        }
    ]

/// Env-gated remote arm. Read-only against a pre-provisioned table.
let private remoteTests =
    RemoteDataSourceContract.tests
        "Synapse"
        [ "TOOLUP_SYNAPSE_SERVER"; "TOOLUP_SYNAPSE_DATABASE"; "TOOLUP_SYNAPSE_TABLE" ]
        (fun () ->
            let table = envOr "TOOLUP_SYNAPSE_TABLE" ""
            let schema = envOr "TOOLUP_SYNAPSE_SCHEMA" "dbo"

            let mk suffix (schema: string) =
                config $"synapse-remote-%s{suffix}" "Synapse" [
                    yield "server", envOr "TOOLUP_SYNAPSE_SERVER" ""
                    yield "database", envOr "TOOLUP_SYNAPSE_DATABASE" ""
                    yield "schema", schema
                    yield "auth", envOr "TOOLUP_SYNAPSE_AUTH" "aad-default"
                    match env "TOOLUP_SYNAPSE_USER" with
                    | Some user -> yield "user", user
                    | None -> ()
                ]
                |> context "test-scope" (env "TOOLUP_SYNAPSE_CREDENTIAL")

            {
                Source = SynapseDataSource.createWithDefaultCredentials ()
                Context = mk "main" schema
                IsolatedContext = mk "iso" (envOr "TOOLUP_SYNAPSE_ISOLATED_SCHEMA" "toolup_no_such_schema")
                Table = table
                SampleSql = envOr "TOOLUP_SYNAPSE_SAMPLE_SQL" $"SELECT TOP 5 * FROM [%s{schema}].[%s{table}]"
                MissingTableSql = $"SELECT TOP 1 * FROM [%s{schema}].[toolup_no_such_table_9c1f]"
            })

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Synapse" [
        parsingTests
        connectionStringTests
        catalogueTests
        statementTests
        kindTests
        remoteTests
    ]