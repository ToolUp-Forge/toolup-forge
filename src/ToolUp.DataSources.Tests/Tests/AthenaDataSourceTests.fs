// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.AthenaDataSourceTests

open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Athena
open ToolUp.DataSources.Tests.Support
open ToolUp.DataSources.Tests.Support.TestFakes
open DataManagementTypes

// ─── ToolUp.DataSources.Athena ────────────────────────────────────

let private read scope =
    AthenaDataSource.readSettings (Map.ofList scope)

let private baseScope = [
    "region", "eu-west-2"
    "database", "analytics"
    "output_location", "s3://staging-bucket/athena/"
]

let private parsingTests =
    testList "readSettings" [
        test "region and database are required" {
            let cases = [ "region", [ "database", "d" ]; "database", [ "region", "r" ] ]

            for missing, present in cases do
                match read present with
                | Error(SchemaMismatch message) -> Expect.stringContains message missing "names the missing key"
                | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "the catalogue defaults to AwsDataCatalog and the poll knobs to sane values" {
            match read baseScope with
            | Ok settings ->
                Expect.equal settings.Catalog "AwsDataCatalog" "default catalogue"
                Expect.equal settings.PollIntervalMs 500 "default poll interval"
                Expect.equal settings.QueryTimeoutSeconds 300 "default timeout"
                Expect.equal settings.WorkGroup None "no workgroup unless declared"
                Expect.equal settings.OutputLocation (Some "s3://staging-bucket/athena/") "output location"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "non-positive poll or timeout values are refused" {
            // A zero poll interval is a busy loop against a billed
            // API; a zero timeout cancels every query immediately.
            for key in [ "poll_interval_ms"; "query_timeout_seconds" ] do
                match read (baseScope @ [ key, "0" ]) with
                | Error(SchemaMismatch message) -> Expect.stringContains message key "names the key"
                | other -> failtestf "expected SchemaMismatch for %s; got %A" key other
        }

        test "output_location is optional — a workgroup may enforce its own" {
            match read [ "region", "r"; "database", "d"; "workgroup", "reporting" ] with
            | Ok settings ->
                Expect.equal settings.OutputLocation None "absent"
                Expect.equal settings.WorkGroup (Some "reporting") "workgroup"
            | Error err -> failtestf "expected Ok; got %A" err
        }
    ]

let private credentialTests =
    testList "parseAwsCredentials" [
        test "an access-key pair becomes basic credentials" {
            match AthenaDataSource.parseAwsCredentials "k" """{"accessKeyId":"AK","secretAccessKey":"SK"}""" with
            | Ok credentials -> Expect.isNotNull (box credentials) "credentials built"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "a session token is honoured under any of its spellings" {
            for token in [ "sessionToken"; "aws_session_token"; "session_token" ] do
                let json =
                    $"""{{"aws_access_key_id":"AK","aws_secret_access_key":"SK","{token}":"ST"}}"""

                match AthenaDataSource.parseAwsCredentials "k" json with
                | Ok credentials -> Expect.isNotNull (box credentials) $"{token} accepted"
                | Error err -> failtestf "expected Ok for %s; got %A" token err
        }

        test "a half-filled blob is refused, and the message says how to opt out entirely" {
            match AthenaDataSource.parseAwsCredentials "my-key" """{"accessKeyId":"AK"}""" with
            | Error(SchemaMismatch message) ->
                Expect.stringContains message "my-key" "names the credential"
                Expect.stringContains message "default credential chain" "explains the alternative"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

let private schemaMappingTests =
    testList "schema mapping" [
        test "Hive type names classify to the coarse ColumnType" {
            let cases = [
                "boolean", BooleanColumn
                "tinyint", NumberColumn
                "smallint", NumberColumn
                "int", NumberColumn
                "bigint", NumberColumn
                "double", NumberColumn
                "decimal(18,2)", NumberColumn
                "date", DateColumn
                "timestamp", DateColumn
                "string", StringColumn
                "varchar(64)", StringColumn
                "binary", StringColumn
                "array<int>", StringColumn
                "map<string,int>", StringColumn
                "struct<a:int>", StringColumn
            ]

            for native, expected in cases do
                Expect.equal (AthenaDataSource.toColumnType native) expected $"'{native}'"
        }
    ]

let private kindTests =
    testList "Kind" [
        test "the connector answers to the documented Kind" {
            Expect.equal AthenaDataSource.Kind "Athena" "Kind constant"
            Expect.equal (AthenaDataSource.createWithDefaultCredentials ()).Kind "Athena" "instance Kind"
        }

        testCaseAsync "a misconfigured ConnectionScope fails before any AWS call"
        <| async {
            let source = AthenaDataSource.createWithDefaultCredentials ()

            let ctx =
                config "ath-1" "Athena" [ "region", "eu-west-2" ] |> context "test-scope" None

            match! source.Connect ctx with
            | Error(SchemaMismatch message) -> Expect.stringContains message "database" "names the missing key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

/// Env-gated remote arm. Read-only: it starts SELECT queries against a
/// pre-provisioned table and stages their results to the configured
/// output location.
let private remoteTests =
    RemoteDataSourceContract.tests
        "Athena"
        [
            "TOOLUP_ATHENA_REGION"
            "TOOLUP_ATHENA_DATABASE"
            "TOOLUP_ATHENA_TABLE"
            "TOOLUP_ATHENA_OUTPUT_LOCATION"
        ]
        (fun () ->
            let database = envOr "TOOLUP_ATHENA_DATABASE" ""
            let table = envOr "TOOLUP_ATHENA_TABLE" ""

            let mk suffix (database: string) =
                config $"athena-remote-%s{suffix}" "Athena" [
                    yield "region", envOr "TOOLUP_ATHENA_REGION" ""
                    yield "database", database
                    yield "output_location", envOr "TOOLUP_ATHENA_OUTPUT_LOCATION" ""
                    match env "TOOLUP_ATHENA_WORKGROUP" with
                    | Some workgroup -> yield "workgroup", workgroup
                    | None -> ()
                ]
                |> context "test-scope" (env "TOOLUP_ATHENA_CREDENTIAL_JSON")

            {
                Source = AthenaDataSource.createWithDefaultCredentials ()
                Context = mk "main" database
                IsolatedContext = mk "iso" (envOr "TOOLUP_ATHENA_ISOLATED_DATABASE" "toolup_no_such_database")
                Table = table
                SampleSql = envOr "TOOLUP_ATHENA_SAMPLE_SQL" $"SELECT * FROM \"%s{database}\".\"%s{table}\" LIMIT 5"
                MissingTableSql = $"SELECT * FROM \"%s{database}\".\"toolup_no_such_table_9c1f\" LIMIT 1"
            })

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Athena" [ parsingTests; credentialTests; schemaMappingTests; kindTests; remoteTests ]