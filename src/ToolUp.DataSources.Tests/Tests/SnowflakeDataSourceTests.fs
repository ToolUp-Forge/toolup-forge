// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.SnowflakeDataSourceTests

open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Snowflake
open ToolUp.DataSources.Tests.Support
open ToolUp.DataSources.Tests.Support.TestFakes
open DataManagementTypes

// ─── ToolUp.DataSources.Snowflake ─────────────────────────────────

let private read scope =
    SnowflakeDataSource.readSettings (Map.ofList scope)

let private baseScope = [
    "account", "xy12345.eu-west-1"
    "user", "svc_ingest"
    "database", "ANALYTICS"
]

let private parsingTests =
    testList "readSettings" [
        test "account, user and database are all required" {
            for missing in [ "account"; "user"; "database" ] do
                let scope = baseScope |> List.filter (fun (k, _) -> k <> missing)

                match read scope with
                | Error(SchemaMismatch message) -> Expect.stringContains message missing "names the missing key"
                | other -> failtestf "expected SchemaMismatch for %s; got %A" missing other
        }

        test "database is required because INFORMATION_SCHEMA is per-database in Snowflake" {
            match read [ "account", "a"; "user", "u" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "database" "names the key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "defaults are password auth, PUBLIC schema, case-folding on" {
            match read baseScope with
            | Ok settings ->
                Expect.equal settings.Auth PasswordAuth "default auth"
                Expect.equal settings.Schema "PUBLIC" "default schema"
                Expect.isFalse settings.CaseSensitiveIdentifiers "folding on by default"
                Expect.equal settings.Warehouse None "no warehouse unless declared"
                Expect.equal settings.Role None "no role unless declared"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "the authenticator aliases parse and an unknown one names both accepted values" {
            for alias, expected in
                [
                    "password", PasswordAuth
                    "snowflake", PasswordAuth
                    "snowflake_jwt", KeyPairAuth
                    "keypair", KeyPairAuth
                    "key-pair", KeyPairAuth
                ] do
                Expect.equal (SnowflakeDataSource.parseAuth (Some alias)) (Ok expected) $"'{alias}'"

            match SnowflakeDataSource.parseAuth (Some "oauth") with
            | Error(SchemaMismatch message) ->
                Expect.stringContains message "password" "names password"
                Expect.stringContains message "snowflake_jwt" "names snowflake_jwt"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "per-warehouse / per-role routing is ConnectionScope data, not connector state" {
            match read (baseScope @ [ "warehouse", "WH_SMALL"; "role", "ANALYST" ]) with
            | Ok settings ->
                Expect.equal settings.Warehouse (Some "WH_SMALL") "warehouse"
                Expect.equal settings.Role (Some "ANALYST") "role"
            | Error err -> failtestf "expected Ok; got %A" err
        }
    ]

let private connectionStringTests =
    testList "composeConnectionString" [
        test "password auth emits the routing keys plus the password" {
            match read (baseScope @ [ "warehouse", "WH"; "role", "R" ]) with
            | Ok settings ->
                match SnowflakeDataSource.composeConnectionString settings (Some "s3cret") with
                | Ok composed ->
                    for needle in
                        [
                            "account=xy12345.eu-west-1"
                            "user=svc_ingest"
                            "db=ANALYTICS"
                            "schema=PUBLIC"
                            "warehouse=WH"
                            "role=R"
                            "password=s3cret"
                        ] do
                        Expect.stringContains composed needle $"carries {needle}"
                | Error err -> failtestf "expected Ok; got %A" err
            | Error err -> failtestf "settings: %A" err
        }

        test "key-pair auth prefers a private_key_file over an inline PEM" {
            match
                read (
                    baseScope
                    @ [ "authenticator", "snowflake_jwt"; "private_key_file", "/secrets/rsa_key.p8" ]
                )
            with
            | Ok settings ->
                match SnowflakeDataSource.composeConnectionString settings (Some "-----BEGIN PRIVATE KEY-----") with
                | Ok composed ->
                    Expect.stringContains composed "authenticator=snowflake_jwt" "jwt authenticator"
                    Expect.stringContains composed "private_key_file=/secrets/rsa_key.p8" "file path"

                    Expect.isFalse
                        (composed.Contains "BEGIN PRIVATE KEY")
                        "the inline PEM is not used when a file is named"
                | Error err -> failtestf "expected Ok; got %A" err
            | Error err -> failtestf "settings: %A" err
        }

        test "a value containing ';' is REFUSED rather than silently truncated" {
            // The driver parses a plain `key=value;` list. Emitting a
            // password with a semicolon in it would be mis-split and
            // present as an authentication failure nobody could
            // explain from the message.
            match read baseScope with
            | Ok settings ->
                match SnowflakeDataSource.composeConnectionString settings (Some "pa;ss") with
                | Error(SchemaMismatch message) -> Expect.stringContains message "password" "names the offending key"
                | other -> failtestf "expected SchemaMismatch; got %A" other
            | Error err -> failtestf "settings: %A" err
        }
    ]

let private catalogueTests =
    testList "SnowflakeCatalogue" [
        test "identifiers are UPPER-CASED for the catalogue comparison by default" {
            // Snowflake folds unquoted identifiers to upper case and
            // STORES them that way, so comparing against 'orders'
            // matches nothing.
            match SnowflakeCatalogue.listTablesSql false "analytics" with
            | Ok sql -> Expect.stringContains sql "'ANALYTICS'" "folded"
            | Error err -> failtestf "expected Ok; got %A" err

            match SnowflakeCatalogue.listTablesSql true "analytics" with
            | Ok sql -> Expect.stringContains sql "'analytics'" "case_sensitive_identifiers preserves the case"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "columnsSql projects name, type and nullability in ordinal order" {
            match SnowflakeCatalogue.columnsSql false "public" "orders" with
            | Ok sql ->
                for needle in
                    [
                        "COLUMN_NAME"
                        "DATA_TYPE"
                        "IS_NULLABLE"
                        "'PUBLIC'"
                        "'ORDERS'"
                        "ORDINAL_POSITION"
                    ] do
                    Expect.stringContains sql needle $"projects {needle}"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "an injection-shaped schema or table is REFUSED" {
            match SnowflakeCatalogue.columnsSql false "public" "orders'; DROP TABLE users --" with
            | Error(SchemaMismatch _) -> ()
            | other -> failtestf "expected the table to be refused; got %A" other

            match SnowflakeCatalogue.listTablesSql false "a' OR '1'='1" with
            | Error(SchemaMismatch _) -> ()
            | other -> failtestf "expected the schema to be refused; got %A" other
        }

        test "Snowflake type names classify to the coarse ColumnType" {
            let cases = [
                "BOOLEAN", BooleanColumn
                "NUMBER(38,0)", NumberColumn
                "FLOAT", NumberColumn
                "DATE", DateColumn
                "TIME", DateColumn
                "TIMESTAMP_NTZ", DateColumn
                "TIMESTAMP_LTZ", DateColumn
                "TEXT", StringColumn
                "VARCHAR(16777216)", StringColumn
                // The semi-structured family renders as its JSON text.
                "VARIANT", StringColumn
                "OBJECT", StringColumn
                "ARRAY", StringColumn
                "GEOGRAPHY", StringColumn
                "BINARY", StringColumn
            ]

            for native, expected in cases do
                Expect.equal (SnowflakeCatalogue.toColumnType native) expected $"'{native}'"
        }

        test "nullability reads INFORMATION_SCHEMA's YES/NO" {
            Expect.isTrue (SnowflakeCatalogue.nullableFromToken "YES") "YES"
            Expect.isFalse (SnowflakeCatalogue.nullableFromToken "NO") "NO"
        }
    ]

let private statementTests =
    testList "resolveStatement" [
        test "a bare identifier expands with folded, quoted parts" {
            match read baseScope with
            | Ok settings ->
                Expect.equal
                    (SnowflakeDataSource.resolveStatement settings "orders")
                    (Ok "SELECT * FROM \"PUBLIC\".\"ORDERS\"")
                    "expanded and folded"

                let statement = "SELECT * FROM analytics.public.orders LIMIT 10"
                Expect.equal (SnowflakeDataSource.resolveStatement settings statement) (Ok statement) "verbatim"
            | Error err -> failtestf "settings: %A" err
        }
    ]

let private kindTests =
    testList "Kind" [
        test "the connector answers to the documented Kind" {
            Expect.equal SnowflakeDataSource.Kind "Snowflake" "Kind constant"
            Expect.equal (SnowflakeDataSource.createWithKeyFile ()).Kind "Snowflake" "instance Kind"
        }

        testCaseAsync "password auth with no credential is CredentialMissing"
        <| async {
            let source = SnowflakeDataSource.create (InMemorySecretStore())
            let ctx = config "sf-1" "Snowflake" baseScope |> context "test-scope" None

            match! source.Connect ctx with
            | Error(CredentialMissing key) -> Expect.equal key "snowflake-credential" "names the key"
            | other -> failtestf "expected CredentialMissing; got %A" other
        }

        testCaseAsync "key-pair auth with a private_key_file needs no stored secret"
        <| async {
            // It must get PAST credential resolution and fail on the
            // connection attempt instead — anything else means a
            // key-file deployment could never start.
            let source = SnowflakeDataSource.createWithKeyFile ()

            let ctx =
                config
                    "sf-2"
                    "Snowflake"
                    (baseScope
                     @ [ "authenticator", "snowflake_jwt"; "private_key_file", "/no/such/key.p8" ])
                |> context "test-scope" None

            match! source.Connect ctx with
            | Error(CredentialMissing _) -> failtest "key-file auth must not demand an ISecretStore credential"
            | Error _ -> ()
            | Ok() -> failtest "a nonexistent key file cannot produce a live connection"
        }
    ]

/// Env-gated remote arm. Read-only against a pre-provisioned table.
let private remoteTests =
    RemoteDataSourceContract.tests
        "Snowflake"
        [
            "TOOLUP_SNOWFLAKE_ACCOUNT"
            "TOOLUP_SNOWFLAKE_USER"
            "TOOLUP_SNOWFLAKE_DATABASE"
            "TOOLUP_SNOWFLAKE_TABLE"
        ]
        (fun () ->
            let table = envOr "TOOLUP_SNOWFLAKE_TABLE" ""
            let schema = envOr "TOOLUP_SNOWFLAKE_SCHEMA" "PUBLIC"
            let database = envOr "TOOLUP_SNOWFLAKE_DATABASE" ""

            let mk suffix (schema: string) =
                config $"snowflake-remote-%s{suffix}" "Snowflake" [
                    yield "account", envOr "TOOLUP_SNOWFLAKE_ACCOUNT" ""
                    yield "user", envOr "TOOLUP_SNOWFLAKE_USER" ""
                    yield "database", database
                    yield "schema", schema
                    yield "authenticator", envOr "TOOLUP_SNOWFLAKE_AUTHENTICATOR" "password"
                    match env "TOOLUP_SNOWFLAKE_WAREHOUSE" with
                    | Some warehouse -> yield "warehouse", warehouse
                    | None -> ()
                    match env "TOOLUP_SNOWFLAKE_ROLE" with
                    | Some role -> yield "role", role
                    | None -> ()
                    match env "TOOLUP_SNOWFLAKE_PRIVATE_KEY_FILE" with
                    | Some file -> yield "private_key_file", file
                    | None -> ()
                ]
                |> context "test-scope" (env "TOOLUP_SNOWFLAKE_CREDENTIAL")

            {
                Source = SnowflakeDataSource.createWithKeyFile ()
                Context = mk "main" schema
                IsolatedContext = mk "iso" (envOr "TOOLUP_SNOWFLAKE_ISOLATED_SCHEMA" "TOOLUP_NO_SUCH_SCHEMA")
                Table = table
                SampleSql =
                    envOr "TOOLUP_SNOWFLAKE_SAMPLE_SQL" $"SELECT * FROM %s{database}.%s{schema}.%s{table} LIMIT 5"
                MissingTableSql = $"SELECT * FROM %s{database}.%s{schema}.TOOLUP_NO_SUCH_TABLE_9C1F LIMIT 1"
            })

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Snowflake" [
        parsingTests
        connectionStringTests
        catalogueTests
        statementTests
        kindTests
        remoteTests
    ]