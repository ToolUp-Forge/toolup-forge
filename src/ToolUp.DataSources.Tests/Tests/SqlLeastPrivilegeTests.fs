// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.DataSources.Tests.Tests.SqlLeastPrivilegeTests

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Infra
open ToolUp.DataSources.Sql
open ToolUp.DataSources.Sql.SqlDataSource
open ToolUp.DataSources.Tests.Support.TestFakes

// ─── ToolUp.DataSources.Sql — the least-privilege probe ───────────
//
// The probe statements and the classification of what a catalogue answers
// are pure, so they are tested without a database: the statements are read
// as each backend reads them, and must only read. The validator then runs
// in process against sources that cannot be probed (SQLite, an unreachable
// server), always. Against each server backend it is an armed leg on the
// bound-statement legs' variables, whose scratch account can create tables,
// so the probe must name what it can do: Pending without one, never Failed.

let private servers = [ PostgreSql; MySql; SqlServer; Oracle; ClickHouse ]

let private probeOf (backend: SqlBackend) =
    match SqlDialect.privilegeProbe backend with
    | Ok probe -> probe
    | Error reason -> failtestf "expected %A to be probed; got %s" backend reason

// The words of a statement outside its string literals, uppercased.
let private codeWords (text: string) =
    Regex.Replace(text, "'(?:[^']|'')*'", " ").ToUpperInvariant()
    |> fun code -> Regex.Matches(code, "[A-Z_][A-Z_0-9]*")
    |> Seq.map _.Value
    |> Set.ofSeq

let private changing =
    set [
        "INSERT"
        "UPDATE"
        "DELETE"
        "MERGE"
        "CREATE"
        "ALTER"
        "DROP"
        "TRUNCATE"
        "GRANT"
        "REVOKE"
        "EXEC"
        "EXECUTE"
        "CALL"
        "SET"
        "COMMIT"
        "BEGIN"
        "DECLARE"
        "INTO"
    ]

let private statementTests =
    testList "the probe statements" [
        test "on every server backend, each statement is one statement that only reads" {
            for backend in servers do
                let probe = probeOf backend

                for label, statement in [ "account", probe.Account; "privileges", probe.Privileges ] do
                    let text = statement.Text
                    let context = $"%A{backend} %s{label}: %s{text}"

                    match SqlBinding.scan backend text with
                    | Ok pieces ->
                        Expect.isFalse
                            (pieces
                             |> List.exists (function
                                 | StatementPiece.Marker _ -> true
                                 | StatementPiece.Verbatim _ -> false))
                            $"%s{context}: binds nothing"
                    | Error reason -> failtestf "%s: the backend's own reading refused it: %s" context reason

                    Expect.isTrue
                        (text.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                        $"%s{context}: a SELECT"

                    Expect.isEmpty
                        (Set.intersect (codeWords text) changing)
                        $"%s{context}: no word that changes anything"

                    Expect.isOk (SqlBinding.translate backend statement) $"%s{context}: translates"
        }

        test "the reading of a statement is not fooled by a change word inside a literal" {
            Expect.isEmpty
                (Set.intersect (codeWords "SELECT 'INSERT' FROM t WHERE x = 'it''s DROP'") changing)
                "literals are not code"

            Expect.contains (codeWords "SELECT 1 INTO t") "INTO" "code is read"
        }

        test "sqlite is not probed, and says why" {
            match SqlDialect.privilegeProbe Sqlite with
            | Error reason -> Expect.stringContains reason "no accounts" "the reason"
            | Ok probe -> failtestf "expected sqlite unprobed; got %A" probe
        }
    ]

let private classificationTests =
    let surplus backend held =
        SqlDialect.surplusPrivileges backend held

    testList "what a catalogue answers, classified" [
        test "a read-only account's catalogue answer holds nothing surplus" {
            Expect.isEmpty (surplus PostgreSql []) "postgres reports only surplus rows"
            Expect.isEmpty (surplus MySql [ "SELECT"; "USAGE"; "SHOW VIEW" ]) "mysql"

            Expect.isEmpty
                (surplus SqlServer [
                    "CONNECT"
                    "SELECT"
                    "VIEW DEFINITION"
                    "VIEW ANY COLUMN ENCRYPTION KEY DEFINITION"
                    "VIEW ANY COLUMN MASTER KEY DEFINITION"
                    "SHOWPLAN"
                ])
                "sqlserver"

            Expect.isEmpty
                (surplus Oracle [ "CREATE SESSION"; "SELECT"; "SELECT ANY TABLE"; "CONNECT"; "READ" ])
                "oracle: CREATE SESSION is what connecting needs"

            Expect.isEmpty (surplus ClickHouse [ "SELECT"; "SHOW TABLES"; "SHOW COLUMNS" ]) "clickhouse"
        }

        test "every privilege that writes, alters schema, grants or administers is surplus" {
            let all backend held =
                Expect.equal (surplus backend held) (List.sort held) $"%A{backend}: every one named"

            all PostgreSql [
                "SUPERUSER"
                "CREATEROLE"
                "CREATEDB"
                "BYPASSRLS"
                "CREATE ON DATABASE"
                "CREATE ON SCHEMA"
                "INSERT"
                "UPDATE"
                "DELETE"
                "TRUNCATE"
                "TRIGGER"
                "OWNER"
            ]

            all MySql [
                "INSERT"
                "CREATE TEMPORARY TABLES"
                "ALTER ROUTINE"
                "SELECT WITH GRANT OPTION"
                "SUPER"
                "FILE"
                "SYSTEM_VARIABLES_ADMIN"
            ]

            all SqlServer [
                "INSERT"
                "ALTER ANY SCHEMA"
                "CREATE TABLE"
                "CONTROL"
                "TAKE OWNERSHIP"
                "IMPERSONATE"
            ]

            all Oracle [
                "CREATE TABLE"
                "INSERT ANY TABLE"
                "DROP ANY TABLE"
                "GRANT ANY ROLE"
                "DBA"
                "OWNER"
            ]

            all ClickHouse [
                "ALL"
                "INSERT"
                "ALTER UPDATE"
                "DROP TABLE"
                "ACCESS MANAGEMENT"
                "SYSTEM SHUTDOWN"
            ]
        }

        test "a mixed answer keeps the surplus once each, in the database's spelling, ordered" {
            Expect.equal
                (surplus MySql [ "SELECT"; " INSERT "; "Delete"; "INSERT"; "USAGE" ])
                [ "Delete"; "INSERT" ]
                "distinct, trimmed, sorted"
        }
    ]

let private lineTests =
    testList "the report line" [
        test "it names the source, the account and each surplus privilege" {
            Expect.equal
                (LeastPrivilegeProbe.line "orders" (PrivilegeReading.Probed("reporter", [ "DELETE"; "INSERT" ])))
                "source 'orders': account 'reporter' holds what a read-only source does not need: DELETE, INSERT"
                "surplus"

            Expect.equal
                (LeastPrivilegeProbe.line "orders" (PrivilegeReading.Probed("reader", [])))
                "source 'orders': account 'reader' holds no privilege beyond reading"
                "clean"

            Expect.equal
                (LeastPrivilegeProbe.line "orders" (PrivilegeReading.NotProbed "no catalogue"))
                "source 'orders': not probed: no catalogue"
                "unprobed"
        }

        test "an unprobed source warns, never reads as clean" {
            match LeastPrivilegeProbe.result [ "orders", PrivilegeReading.NotProbed "no catalogue" ] with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.stringContains message "could not be probed" "says so"
                Expect.stringContains message "'orders'" "names it"
            | other -> failtestf "expected a warning; got %A" other

            Expect.equal
                (LeastPrivilegeProbe.result [ "orders", PrivilegeReading.Probed("reader", []) ])
                ConfigValidation.ValidationResult.Ok
                "a clean account passes"
        }
    ]

let private validatorTests =
    let sql id scope =
        config id "Sql" scope |> context "test-scope" None

    let run limit (sources: DataSourceCallContext list) =
        let reported = ResizeArray<string>()

        let result =
            LeastPrivilegeProbe.validate limit None reported.Add sources
            |> Async.RunSynchronously

        result, List.ofSeq reported

    testList "LeastPrivilegeProbe" [
        test "a deployment with no sources reports nothing and passes" {
            let result, reported = run LeastPrivilegeProbe.deadline []
            Expect.equal result ConfigValidation.ValidationResult.Ok "passes"
            Expect.isEmpty reported "says nothing"
        }

        test "a source not declared read-only is skipped with a line saying so, and opens no connection" {
            let result, reported =
                run LeastPrivilegeProbe.deadline [
                    sql "open" [ "backend", "postgres"; "connection_string", "Host=10.255.255.1;Timeout=15" ]
                ]

            Expect.equal result ConfigValidation.ValidationResult.Ok "passes"

            match reported with
            | [ report ] ->
                Expect.stringContains report "source 'open': skipped: it does not declare read_only=true" "said"
            | other -> failtestf "expected one report; got %A" other
        }

        test "a read-only sqlite source is reported not probed, with the reason, and warned about" {
            let file =
                Path.Combine(Path.GetTempPath(), $"toolup-least-privilege-{Guid.NewGuid():N}.db")

            try
                let result, reported =
                    run LeastPrivilegeProbe.deadline [
                        sql "local" [
                            "backend", "sqlite"
                            "connection_string", $"Data Source=%s{file}"
                            "read_only", "true"
                        ]
                    ]

                match result with
                | ConfigValidation.ValidationResult.Warning message -> Expect.stringContains message "'local'" "named"
                | other -> failtestf "expected a warning; got %A" other

                Expect.stringContains
                    (List.exactlyOne reported)
                    "source 'local': not probed: sqlite has no accounts"
                    "said"
            finally
                File.Delete file
        }

        test "a server that does not answer is not probed within the limit, and the report carries no secret" {
            let connectionString =
                "Host=10.255.255.1;Username=reporter;Password=hunter2-in-the-string;Timeout=15"

            let ctx =
                config "slow" "Sql" [
                    "backend", "postgres"
                    "connection_string", connectionString
                    "read_only", "true"
                ]
                |> context "test-scope" (Some "s3cr3t-credential")

            let clock = Stopwatch.StartNew()
            let result, reported = run (TimeSpan.FromSeconds 0.5) [ ctx ]
            clock.Stop()

            Expect.isLessThan clock.Elapsed (TimeSpan.FromSeconds 5.0) "bounded by the limit, not the connect timeout"
            let report = List.exactlyOne reported
            Expect.stringContains report "source 'slow': not probed" "never clean"

            for secret in [ connectionString; "hunter2-in-the-string"; "s3cr3t-credential" ] do
                Expect.isFalse (report.Contains secret) $"the report does not carry %s{secret}"

            match result with
            | ConfigValidation.ValidationResult.Warning message ->
                Expect.isFalse (message.Contains "hunter2") "nor does the warning"
            | other -> failtestf "expected a warning; got %A" other
        }

        test "it is an external probe: SkipPreflight bypasses it, and it finishes inside the aggregator's ceiling" {
            let validator =
                SqlDataSource.LeastPrivilegeProbe([], None, ignore) :> ConfigValidation.IConfigValidator

            Expect.isFalse (box validator :? ConfigValidation.IStructuralClassValidator) "not structural"
            Expect.isFalse (box validator :? ConfigValidation.ISecurityClassValidator) "not security"
            Expect.isGreaterThan validator.Timeout LeastPrivilegeProbe.deadline "the per-source limit fits inside"
            Expect.isLessThanOrEqual validator.Timeout (TimeSpan.FromSeconds 10.0) "never past the ceiling"
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

    testList "least-privilege probe, server backends (armed legs)" [
        for _, name, variable in legs do
            match env variable with
            | None -> ptestCase $"%s{name}: set %s{variable} to a scratch database's connection string" ignore
            | Some connectionString ->
                testCaseAsync $"%s{name}: the scratch account, which can create a table, is named with what it can do"
                <| async {
                    let ctx =
                        config "scratch" "Sql" [ "backend", name; "connection_string", connectionString ]
                        |> context "test-scope" None

                    match! LeastPrivilegeProbe.probe None ctx with
                    | PrivilegeReading.Probed(account, surplus) ->
                        Expect.isNotEmpty account "the account as the database names it"
                        Expect.isNonEmpty surplus "an account that can create a table holds surplus privileges"
                    | PrivilegeReading.NotProbed reason ->
                        failtestf "expected the scratch account probed; got %s" reason
                }
    ]

[<Tests>]
let tests =
    testList "ToolUp.DataSources.Sql — least-privilege probe" [
        statementTests
        classificationTests
        lineTests
        validatorTests
        serverLegs
    ]