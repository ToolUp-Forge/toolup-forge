// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.RedshiftDataSourceTests

open Expecto
open ToolUp.Platform
open ToolUp.DataSources.Redshift
open ToolUp.DataSources.Tests.Support
open ToolUp.DataSources.Tests.Support.TestFakes
open DataManagementTypes

// ─── ToolUp.DataSources.Redshift ──────────────────────────────────

let private read scope =
    RedshiftDataSource.readSettings (Map.ofList scope)

let private serverless = [
    "region", "us-east-1"
    "database", "analytics"
    "workgroup_name", "reporting-wg"
]

let private provisioned = [
    "region", "us-east-1"
    "database", "analytics"
    "cluster_identifier", "prod-cluster"
    "secret_arn", "arn:aws:secretsmanager:us-east-1:1:secret:redshift"
]

let private parsingTests =
    testList "readSettings" [
        test "a serverless workgroup needs no database credential key" {
            match read serverless with
            | Ok settings ->
                Expect.equal settings.WorkgroupName (Some "reporting-wg") "workgroup"
                Expect.equal settings.ClusterIdentifier None "no cluster"
                Expect.equal settings.Schema "public" "default schema"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "a provisioned cluster requires secret_arn or db_user" {
            // Without one of these the Data API rejects every call
            // with a message that does not say which key is missing —
            // which is exactly why the connector checks first.
            let bare = [ "region", "us-east-1"; "database", "analytics"; "cluster_identifier", "prod" ]

            match read bare with
            | Error(SchemaMismatch message) ->
                Expect.stringContains message "secret_arn" "names one route"
                Expect.stringContains message "db_user" "names the other"
            | other -> failtestf "expected SchemaMismatch; got %A" other

            Expect.isTrue (read provisioned |> Result.isOk) "secret_arn satisfies it"

            Expect.isTrue
                (read [ "region", "r"; "database", "d"; "cluster_identifier", "c"; "db_user", "u" ]
                 |> Result.isOk)
                "db_user satisfies it"
        }

        test "exactly one of cluster_identifier and workgroup_name is required" {
            match read [ "region", "r"; "database", "d" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "exactly one" "explains the rule"
            | other -> failtestf "expected SchemaMismatch; got %A" other

            match read (provisioned @ [ "workgroup_name", "wg" ]) with
            | Error(SchemaMismatch message) -> Expect.stringContains message "exactly one" "both is also wrong"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "poll knobs default and refuse non-positive overrides" {
            match read serverless with
            | Ok settings ->
                Expect.equal settings.PollIntervalMs 500 "default poll"
                Expect.equal settings.QueryTimeoutSeconds 300 "default timeout"
            | Error err -> failtestf "expected Ok; got %A" err

            for key in [ "poll_interval_ms"; "query_timeout_seconds" ] do
                match read (serverless @ [ key, "-1" ]) with
                | Error(SchemaMismatch message) -> Expect.stringContains message key "names the key"
                | other -> failtestf "expected SchemaMismatch for %s; got %A" key other
        }
    ]

let private credentialTests =
    testList "parseAwsCredentials" [
        test "an access-key pair becomes credentials, a half-filled blob does not" {
            Expect.isTrue
                (RedshiftDataSource.parseAwsCredentials "k" """{"accessKeyId":"AK","secretAccessKey":"SK"}"""
                 |> Result.isOk)
                "complete pair"

            match RedshiftDataSource.parseAwsCredentials "my-key" """{"secretAccessKey":"SK"}""" with
            | Error(SchemaMismatch message) -> Expect.stringContains message "my-key" "names the credential"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

let private schemaMappingTests =
    testList "schema mapping" [
        test "Redshift type names classify to the coarse ColumnType" {
            let cases = [
                "bool", BooleanColumn
                "boolean", BooleanColumn
                "int4", NumberColumn
                "int8", NumberColumn
                "numeric(18,2)", NumberColumn
                "double precision", NumberColumn
                "date", DateColumn
                "timestamptz", DateColumn
                // Redshift's own additions, none of which the ANSI
                // table would classify correctly on its own.
                "interval", DateColumn
                "super", StringColumn
                "hllsketch", StringColumn
                "varbyte", StringColumn
                "geometry", StringColumn
                "character varying(256)", StringColumn
            ]

            for native, expected in cases do
                Expect.equal (RedshiftDataSource.toColumnType native) expected $"'{native}'"
        }
    ]

let private kindTests =
    testList "Kind" [
        test "the connector answers to the documented Kind" {
            Expect.equal RedshiftDataSource.Kind "Redshift" "Kind constant"
            Expect.equal (RedshiftDataSource.createWithDefaultCredentials ()).Kind "Redshift" "instance Kind"
        }

        testCaseAsync "a misconfigured ConnectionScope fails before any AWS call"
        <| async {
            let source = RedshiftDataSource.createWithDefaultCredentials ()

            let ctx =
                config "rs-1" "Redshift" [ "region", "us-east-1" ] |> context "test-scope" None

            match! source.Connect ctx with
            | Error(SchemaMismatch message) -> Expect.stringContains message "database" "names the missing key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

/// Env-gated remote arm. Read-only against a pre-provisioned table.
let private remoteTests =
    RemoteDataSourceContract.tests
        "Redshift"
        [
            "TOOLUP_REDSHIFT_REGION"
            "TOOLUP_REDSHIFT_DATABASE"
            "TOOLUP_REDSHIFT_TABLE"
        ]
        (fun () ->
            let table = envOr "TOOLUP_REDSHIFT_TABLE" ""
            let schema = envOr "TOOLUP_REDSHIFT_SCHEMA" "public"

            let mk suffix (schema: string) =
                config $"redshift-remote-%s{suffix}" "Redshift" [
                    yield "region", envOr "TOOLUP_REDSHIFT_REGION" ""
                    yield "database", envOr "TOOLUP_REDSHIFT_DATABASE" ""
                    yield "schema", schema
                    match env "TOOLUP_REDSHIFT_CLUSTER_IDENTIFIER" with
                    | Some cluster -> yield "cluster_identifier", cluster
                    | None -> ()
                    match env "TOOLUP_REDSHIFT_WORKGROUP_NAME" with
                    | Some workgroup -> yield "workgroup_name", workgroup
                    | None -> ()
                    match env "TOOLUP_REDSHIFT_SECRET_ARN" with
                    | Some arn -> yield "secret_arn", arn
                    | None -> ()
                    match env "TOOLUP_REDSHIFT_DB_USER" with
                    | Some user -> yield "db_user", user
                    | None -> ()
                ]
                |> context "test-scope" (env "TOOLUP_REDSHIFT_CREDENTIAL_JSON")

            {
                Source = RedshiftDataSource.createWithDefaultCredentials ()
                Context = mk "main" schema
                IsolatedContext = mk "iso" (envOr "TOOLUP_REDSHIFT_ISOLATED_SCHEMA" "toolup_no_such_schema")
                Table = table
                SampleSql = envOr "TOOLUP_REDSHIFT_SAMPLE_SQL" $"SELECT * FROM %s{schema}.%s{table} LIMIT 5"
                MissingTableSql = $"SELECT * FROM %s{schema}.toolup_no_such_table_9c1f LIMIT 1"
            })

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Redshift" [ parsingTests; credentialTests; schemaMappingTests; kindTests; remoteTests ]