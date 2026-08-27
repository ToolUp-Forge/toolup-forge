// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.BigQueryDataSourceTests

open Expecto
open ToolUp.Platform
open ToolUp.DataSources.BigQuery
open ToolUp.DataSources.Tests.Support
open ToolUp.DataSources.Tests.Support.TestFakes
open DataManagementTypes

// ─── ToolUp.DataSources.BigQuery ──────────────────────────────────

let private read scope =
    BigQueryDataSource.readSettings (Map.ofList scope)

let private baseScope = [ "project_id", "billing-project"; "dataset_id", "analytics" ]

let private parsingTests =
    testList "readSettings" [
        test "project_id and dataset_id are both required" {
            match read [ "dataset_id", "analytics" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "project_id" "names the key"
            | other -> failtestf "expected SchemaMismatch; got %A" other

            match read [ "project_id", "p" ] with
            | Error(SchemaMismatch message) -> Expect.stringContains message "dataset_id" "names the key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        test "defaults are the conservative ones" {
            match read baseScope with
            | Ok settings ->
                Expect.isFalse settings.UseLegacySql "Standard SQL by default"
                Expect.isFalse settings.UseDefaultCredentials "an explicit credential by default"
                Expect.equal settings.Location None "location inferred unless declared"
                Expect.equal settings.MaximumBytesBilled None "no ceiling unless declared"
            | Error err -> failtestf "expected Ok; got %A" err
        }

        test "maximum_bytes_billed parses, and a non-positive or unparseable value is refused" {
            match read (baseScope @ [ "maximum_bytes_billed", "10000000000" ]) with
            | Ok settings -> Expect.equal settings.MaximumBytesBilled (Some 10_000_000_000L) "parsed as int64"
            | Error err -> failtestf "expected Ok; got %A" err

            // BigQuery bills per byte scanned, so a mistyped ceiling
            // silently becoming "no ceiling" is the expensive failure
            // this refusal exists to prevent.
            for bad in [ "0"; "-1"; "10 GB"; "1e9" ] do
                match read (baseScope @ [ "maximum_bytes_billed", bad ]) with
                | Error(SchemaMismatch message) ->
                    Expect.stringContains message "maximum_bytes_billed" $"'{bad}' names the key"
                | other -> failtestf "expected SchemaMismatch for '%s'; got %A" bad other
        }

        test "the boolean knobs read every accepted spelling" {
            match read (baseScope @ [ "use_legacy_sql", "true"; "use_default_credentials", "1" ]) with
            | Ok settings ->
                Expect.isTrue settings.UseLegacySql "use_legacy_sql"
                Expect.isTrue settings.UseDefaultCredentials "use_default_credentials"
            | Error err -> failtestf "expected Ok; got %A" err
        }
    ]

let private schemaMappingTests =
    testList "schema mapping" [
        test "BigQuery type names classify to the coarse ColumnType" {
            let cases = [
                "BOOL", BooleanColumn
                "BOOLEAN", BooleanColumn
                "INT64", NumberColumn
                "FLOAT64", NumberColumn
                "NUMERIC", NumberColumn
                "BIGNUMERIC", NumberColumn
                "DATE", DateColumn
                "DATETIME", DateColumn
                "TIMESTAMP", DateColumn
                "TIME", DateColumn
                "STRING", StringColumn
                "BYTES", StringColumn
                "GEOGRAPHY", StringColumn
                "JSON", StringColumn
                "RECORD", StringColumn
                "STRUCT", StringColumn
                "ARRAY", StringColumn
                // INTERVAL is structured, not a point in time — it
                // renders in CSV as its text form.
                "INTERVAL", StringColumn
            ]

            for native, expected in cases do
                Expect.equal (BigQueryDataSource.toColumnType native) expected $"'{native}'"
        }

        test "field mode decides nullability — only REQUIRED is not nullable" {
            Expect.isFalse (BigQueryDataSource.nullableFromMode "REQUIRED") "REQUIRED"
            Expect.isFalse (BigQueryDataSource.nullableFromMode "required") "case-insensitive"
            Expect.isTrue (BigQueryDataSource.nullableFromMode "NULLABLE") "NULLABLE"
            Expect.isTrue (BigQueryDataSource.nullableFromMode "REPEATED") "REPEATED elements may be absent"
            // The API omits the mode for a plain nullable field.
            Expect.isTrue (BigQueryDataSource.nullableFromMode null) "an absent mode reads nullable"
        }
    ]

let private kindTests =
    testList "Kind" [
        test "the connector answers to the documented Kind" {
            Expect.equal BigQueryDataSource.Kind "BigQuery" "Kind constant"
            Expect.equal (BigQueryDataSource.createWithDefaultCredentials ()).Kind "BigQuery" "instance Kind"
        }
    ]

let private credentialTests =
    testList "credentials" [
        testCaseAsync "a malformed service-account blob fails as SchemaMismatch, not an auth error"
        <| async {
            // Distinguishing "the JSON you pasted is not a
            // service-account key" from "GCP rejected your key" is the
            // difference between a two-minute fix and an afternoon.
            let source = BigQueryDataSource.create (InMemorySecretStore())

            let ctx =
                config "bq-1" "BigQuery" baseScope |> context "test-scope" (Some "not json")

            match! source.Connect ctx with
            | Error(SchemaMismatch message) ->
                Expect.stringContains message "bigquery-credential" "names the credential key"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }

        testCaseAsync "no credential at all is CredentialMissing"
        <| async {
            let source = BigQueryDataSource.create (InMemorySecretStore())
            let ctx = config "bq-1" "BigQuery" baseScope |> context "test-scope" None

            match! source.Connect ctx with
            | Error(CredentialMissing key) -> Expect.equal key "bigquery-credential" "names the key"
            | other -> failtestf "expected CredentialMissing; got %A" other
        }

        testCaseAsync "a misconfigured ConnectionScope fails BEFORE any credential is touched"
        <| async {
            let store = InMemorySecretStore()
            let source = BigQueryDataSource.create store

            let ctx =
                config "bq-1" "BigQuery" [ "project_id", "p" ] |> context "test-scope" None

            match! source.Connect ctx with
            | Error(SchemaMismatch _) ->
                Expect.equal
                    (store.ReadCount("test-scope", "bigquery-credential"))
                    0
                    "configuration is validated before the secret store is consulted"
            | other -> failtestf "expected SchemaMismatch; got %A" other
        }
    ]

/// Env-gated remote arm. `TOOLUP_BIGQUERY_CREDENTIAL_JSON` is the
/// service-account key; nothing is created or written.
let private remoteTests =
    RemoteDataSourceContract.tests
        "BigQuery"
        [
            "TOOLUP_BIGQUERY_PROJECT_ID"
            "TOOLUP_BIGQUERY_DATASET_ID"
            "TOOLUP_BIGQUERY_TABLE"
            "TOOLUP_BIGQUERY_CREDENTIAL_JSON"
        ]
        (fun () ->
            let project = envOr "TOOLUP_BIGQUERY_PROJECT_ID" ""
            let dataset = envOr "TOOLUP_BIGQUERY_DATASET_ID" ""
            let table = envOr "TOOLUP_BIGQUERY_TABLE" ""
            let credential = env "TOOLUP_BIGQUERY_CREDENTIAL_JSON"

            let mk suffix (dataset: string) =
                config $"bq-remote-%s{suffix}" "BigQuery" [
                    yield "project_id", project
                    yield "dataset_id", dataset
                    // A conformance run must not be able to scan a
                    // production fact table by accident.
                    yield "maximum_bytes_billed", envOr "TOOLUP_BIGQUERY_MAX_BYTES" "1000000000"
                    match env "TOOLUP_BIGQUERY_LOCATION" with
                    | Some location -> yield "location", location
                    | None -> ()
                ]
                |> context "test-scope" credential

            {
                Source = BigQueryDataSource.createWithDefaultCredentials ()
                Context = mk "main" dataset
                IsolatedContext = mk "iso" (envOr "TOOLUP_BIGQUERY_ISOLATED_DATASET" "toolup_no_such_dataset")
                Table = table
                SampleSql =
                    envOr "TOOLUP_BIGQUERY_SAMPLE_SQL" $"SELECT * FROM `%s{project}.%s{dataset}.%s{table}` LIMIT 5"
                MissingTableSql = $"SELECT * FROM `%s{project}.%s{dataset}.toolup_no_such_table_9c1f` LIMIT 1"
            })

[<Tests>]
let tests =
    testList "ToolUp.DataSources.BigQuery" [ parsingTests; schemaMappingTests; kindTests; credentialTests; remoteTests ]