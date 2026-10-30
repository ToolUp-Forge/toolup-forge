// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup facts migrate`: the verb's own surface (what it forwards to the
/// composition root and how it reports), the startup-mode argv it relies on,
/// the task's grammar, and — against a live database when
/// `TOOLUP_TEST_POSTGRES` holds a connection string, the leg the
/// PostgresFactStore pack runs under — the task over a composed provider
/// whose blob backend is the local file store.
module ToolUp.Cli.Tests.FactsMigrateTests

open System
open System.IO
open Expecto
open Microsoft.Extensions.DependencyInjection
open Npgsql
open ToolUp.Cli
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Facts
open ToolUp.FactStores.Postgres

// ── The verb ──────────────────────────────────────────────────────────

let private parsed (args: string list) =
    match FactsMigrateCommand.parse args with
    | Ok opts -> opts
    | Error e -> failtestf "expected %A to parse, got: %s" args e

/// A runner that records what it was asked to start and, like the
/// composition root's task, writes `report` to the `--operator-task-out`
/// file it was handed.
let private fakeRunner (report: string option) (code: int option) (seen: ResizeArray<string * string list>) =
    fun (fileName: string) (args: string list) (_: int) ->
        seen.Add((fileName, args))

        match report, args |> List.skipWhile ((<>) "--operator-task-out") with
        | Some text, _ :: path :: _ -> File.WriteAllText(path, text)
        | _ -> ()

        code

let private verbTests =
    testList "the verb" [
        testCase "task arguments are forwarded verbatim; the root flags are the verb's own" (fun _ ->
            let opts =
                parsed [ "--dll"; "app/App.dll"; "--scope"; "team-a"; "--timeout"; "60"; "--dry-run" ]

            Expect.equal opts.Root (Choice2Of2 "app/App.dll") "the built root"
            Expect.equal opts.TimeoutSeconds 60 "the timeout"
            Expect.equal opts.TaskArgs [ "--scope"; "team-a"; "--dry-run" ] "the task's arguments, in order"

            let fileName, args = FactsMigrateCommand.invocation opts "out.txt"
            Expect.equal fileName "dotnet" "runs dotnet"

            Expect.equal
                args
                [
                    "app/App.dll"
                    "--operator-task-out"
                    "out.txt"
                    "--operator-task"
                    "facts-migrate"
                    "--scope"
                    "team-a"
                    "--dry-run"
                ]
                "the root in its operator-task startup mode, the task's arguments after the name")

        testCase "the default root is the project in the current directory, run without a launch profile" (fun _ ->
            let _, args = FactsMigrateCommand.invocation (parsed [ "--all-scopes" ]) "o"

            Expect.equal
                args
                [
                    "run"
                    "--project"
                    "."
                    "--no-launch-profile"
                    "--"
                    "--operator-task-out"
                    "o"
                    "--operator-task"
                    "facts-migrate"
                    "--all-scopes"
                ]
                "dotnet run --project .")

        testCase "a usage error is refused before any process starts" (fun _ ->
            Expect.isError (FactsMigrateCommand.parse [ "--dry-run" ]) "no scope named"
            Expect.isError (FactsMigrateCommand.parse [ "--all-scopes"; "--project"; "a"; "--dll"; "b" ]) "two roots"
            Expect.isError (FactsMigrateCommand.parse [ "--all-scopes"; "--timeout"; "0" ]) "a zero timeout"
            Expect.isError (FactsMigrateCommand.parse [ "--all-scopes"; "--dll" ]) "a flag with no value"

            let code =
                Dispatch.run [ FactsMigrateCommand.command ] [| "facts"; "migrate"; "--dry-run" |]

            Expect.equal code Dispatch.ExitUsage "exit 2")

        testCase "no connection string or storage credential is ever a flag of the verb" (fun _ ->
            let text = String.concat "\n" FactsMigrateCommand.help

            for flag in [ "--connection"; "--connection-string"; "--password"; "--source"; "--table" ] do
                Expect.isFalse (text.Contains flag) (sprintf "the help offers no %s" flag))

        testCase "the task's report is printed and its exit code returned, the temporary file removed" (fun _ ->
            let seen = ResizeArray()
            use out = new StringWriter()

            let code =
                FactsMigrateCommand.run
                    (fakeRunner (Some "team-a: VERIFICATION FAILED\n  f-1 differs\n") (Some 1) seen)
                    out
                    (parsed [ "--scope"; "team-a" ])

            Expect.equal code 1 "the task's exit code"
            Expect.stringContains (out.ToString()) "f-1 differs" "the report, naming the fact"
            let _, args = seen[0]
            let file = args |> List.skipWhile ((<>) "--operator-task-out") |> List.item 1
            Expect.isFalse (File.Exists file) "the report file is removed")

        testCase "a root that writes no report, or does not exit, fails rather than reading green" (fun _ ->
            use out = new StringWriter()
            let opts = parsed [ "--scope"; "team-a" ]

            Expect.equal
                (FactsMigrateCommand.run (fakeRunner None (Some 0) (ResizeArray())) out opts)
                Dispatch.ExitRuntimeError
                "exit 0 without a report is a failure"

            Expect.equal
                (FactsMigrateCommand.run (fakeRunner None None (ResizeArray())) out opts)
                Dispatch.ExitRuntimeError
                "a timeout is a failure"

            Expect.equal (out.ToString()) "" "nothing is printed as a report")
    ]

// ── The startup mode and the task's grammar ──────────────────────────

let private grammarTests =
    testList "the startup mode and the task's grammar" [
        testCase "--operator-task selects the mode and hands the task everything after its name" (fun _ ->
            let argv = [
                "app.dll"
                "--operator-task-out"
                "o.txt"
                "--operator-task"
                "facts-migrate"
                "--scope"
                "a"
            ]

            Expect.equal (StartupModes.detect argv) StartupModes.RunOperatorTask "the mode"
            Expect.equal (StartupModes.operatorTask argv) (Some("facts-migrate", [ "--scope"; "a" ])) "name and args"
            Expect.equal (StartupModes.operatorTaskOut argv) (Some "o.txt") "the report file"
            Expect.equal (StartupModes.detect [ "app.dll" ]) StartupModes.NormalBoot "absent: a normal boot"
            Expect.equal (StartupModes.operatorTask [ "app.dll"; "--operator-task" ]) None "no name")

        testCase "the task parses each mode and refuses a contradictory one" (fun _ ->
            let mode args =
                FactsMigrateTask.parse args |> Result.map _.Mode

            Expect.equal (mode [ "--scope"; "a" ]) (Ok FactsMigrateMode.Migrate) "migrate"
            Expect.equal (mode [ "--scope"; "a"; "--dry-run" ]) (Ok FactsMigrateMode.DryRun) "dry run"
            Expect.equal (mode [ "--all-scopes"; "--verify" ]) (Ok FactsMigrateMode.Verify) "verify"
            Expect.equal (mode [ "--all-scopes"; "--ids"; "--dry-run" ]) (Ok(FactsMigrateMode.Ids true)) "ids, planned"
            Expect.isError (mode [ "--scope"; "a"; "--verify"; "--dry-run" ]) "verify and dry run"
            Expect.isError (mode [ "--scope"; "a"; "--all-scopes" ]) "named and all"
            Expect.isError (mode [ "--scope" ]) "a scope flag with no id"
            Expect.isError (mode [ "--scope"; "a"; "--max-scope-facts"; "0" ]) "an invalid bound"
            Expect.isError (mode [ "--scope"; "a"; "--table"; "t" ]) "no table flag: the table is the composition's"

            match FactsMigrateTask.parse [ "--scope"; "a"; "--scope"; "b"; "--max-scope-facts"; "5" ] with
            | Ok r ->
                Expect.equal r.Scopes (FactsMigrateScopes.Named [ "a"; "b" ]) "scopes in order"
                Expect.equal r.Options.MaxScopeFacts 5 "the raised bound"
            | Error e -> failtest e)

        testCase "composing the task registers it for the startup mode" (fun _ ->
            let app = ServerApp.empty |> FactStoreMigrationCompose.withFactsMigrateTask

            let services = ServiceCollection() :> IServiceCollection

            let registered =
                match ServerApp.serviceConfig app with
                | Some register -> register services
                | None -> services

            use provider = registered.BuildServiceProvider()

            let names =
                provider.GetServices<StartupModes.OperatorTask>()
                |> Seq.map _.Name
                |> List.ofSeq

            Expect.equal names [ FactsMigrateTask.Name ] "one task, by name")
    ]

// ── The task, live ────────────────────────────────────────────────────

[<Literal>]
let private Table = "toolup_facts_cli_migrate"

let private q2: TemporalExtent = {
    From = DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)
    To = DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
    Label = Some "Q2-2026"
}

let private draft (member': string) (inputHash: string) (value: decimal) : FactDraft = {
    Subject = {
        Hierarchy = "geography"
        Path = [ member' ]
    }
    Metric = MetricRef "revenue"
    Value = Scalar value
    Period = q2
    Method = Computed("rollup", "1", "p0")
    Evidence = {
        ResultRef = None
        InputHashes = [ inputHash ]
        TriggerRef = None
    }
    Confidence = None
    Disclosure = Disclosure.Surfaceable
}

/// A composed provider like the one the startup mode builds: the local
/// file blob backend, an event store, and the Postgres store as the
/// composed `IFactStore`.
let private composed (conn: string) =
    let root =
        Path.Combine(Path.GetTempPath(), "toolup-facts-migrate-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory root |> ignore
    let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
    let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore

    let target =
        PostgresFactStore.create
            conn
            {
                PostgresFactStoreOptions.defaults with
                    Table = Table
            }
            events
            None
            (fun () -> DateTime.UtcNow)

    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore
    services.AddSingleton<IEventStore>(events) |> ignore
    services.AddSingleton<IFactStore>(target :> IFactStore) |> ignore
    services.BuildServiceProvider(), storage, events, root

let private seed (storage: IBlobStorage) (events: IEventStore) (scope: string) = async {
    let source = BlobFactStore(storage, events, None, (fun () -> DateTime.UtcNow))

    for round in 1..2 do
        let drafts = [
            for i in 1..12 -> draft (sprintf "m%02d" i) (sprintf "r%02d-%d" i round) (decimal (i * round))
        ]

        match! (source :> IFactStore).AssertBatch(scope, drafts) with
        | Ok _ -> ()
        | Error e -> failtestf "seed refused: %s" e

    return source
}

/// Without a composed Postgres store the task still runs, against the
/// local file blob backend: `--ids` moves the blob store's ids, and every
/// copying mode says what the composition is missing.
let private blobOnlyTests =
    testCaseAsync "without a composed Postgres store only --ids runs, over the blob store"
    <| async {
        let root =
            Path.Combine(Path.GetTempPath(), "toolup-facts-migrate-" + Guid.NewGuid().ToString "N")

        Directory.CreateDirectory root |> ignore

        try
            let storage = LocalFileStorage.LocalFileStorage(root) :> IBlobStorage
            let events = InMemoryEventStore.InMemoryEventStore() :> IEventStore
            let! source = seed storage events ScopeEnumeration.PlatformContainer
            let services = ServiceCollection()
            services.AddSingleton<IBlobStorage>(storage) |> ignore
            services.AddSingleton<IEventStore>(events) |> ignore
            services.AddSingleton<IFactStore>(source :> IFactStore) |> ignore
            use provider = services.BuildServiceProvider()

            let! text, code = FactsMigrateTask.runComposed provider [ "--all-scopes"; "--ids"; "--dry-run" ]
            Expect.equal code 0 text

            Expect.stringContains
                text
                (sprintf "blob %s: 24 facts read, would re-key 0" ScopeEnumeration.PlatformContainer)
                "the well-known containers are enumerated, and freshly asserted ids are current"

            Expect.stringContains
                text
                (sprintf "blob %s: 0 facts read" ScopeEnumeration.DeploymentContainer)
                "both of them"

            Expect.stringContains text "the blob store only" "it says the table leg did not run"

            let! refused, refusedCode = FactsMigrateTask.runComposed provider [ "--scope"; "team-a" ]
            Expect.equal refusedCode 2 refused
            Expect.stringContains refused "not a PostgresFactStore" "it names what is missing"

            let! usage, usageCode = FactsMigrateTask.runComposed provider [ "--dry-run" ]
            Expect.equal usageCode 2 usage
            Expect.stringContains usage "--all-scopes" "a usage error carries the usage"
        finally
            Directory.Delete(root, true)
    }

let private liveTests (conn: string) =
    let scalar (sql: string) (scope: string) = async {
        use dataSource = NpgsqlDataSource.Create conn
        use cmd = dataSource.CreateCommand(sql.Replace("{table}", Table))
        cmd.Parameters.AddWithValue("scope", scope) |> ignore
        let! v = cmd.ExecuteScalarAsync() |> Async.AwaitTask
        return Convert.ToInt64 v
    }

    let run (provider: IServiceProvider) (args: string list) =
        FactsMigrateTask.runComposed provider args

    testList "the task over a composed provider (live)" [
        testCaseAsync
            "a seeded local-file scope migrates clean; a dry run first writes nothing; a second run is a no-op"
        <| async {
            let provider, storage, events, root = composed conn

            try
                let scope = "team-" + Guid.NewGuid().ToString "N"
                let! _ = seed storage events scope

                let! dryText, dryCode = run provider [ "--scope"; scope; "--dry-run" ]
                Expect.equal dryCode 0 dryText
                Expect.stringContains dryText "writing nothing" "the dry run says so"
                Expect.stringContains dryText "would migrate" "and what it would do"
                let! rows = scalar "SELECT count(*) FROM {table} WHERE scope = @scope" scope
                Expect.equal rows 0L "the dry run leaves the target empty"

                let! text, code = run provider [ "--scope"; scope ]
                Expect.equal code 0 text
                Expect.stringContains text (scope + ": verified") "the scope verified"
                Expect.stringContains text "24 source facts" "facts read"
                Expect.stringContains text "24 inserted" "rows written"
                Expect.stringContains text "0 skipped as already present" "rows skipped"
                Expect.stringContains text "0 differences" "the differential's verdict"
                Expect.stringContains text "audit record " "the audit record's id"
                let! rows = scalar "SELECT count(*) FROM {table} WHERE scope = @scope" scope
                Expect.equal rows 24L "every fact is a row"

                let! again, againCode = run provider [ "--scope"; scope ]
                Expect.equal againCode 0 again
                Expect.stringContains again (scope + ": already verified") "a verified scope is skipped"
                Expect.stringContains again "0 inserted" "and nothing is written"
                Expect.isFalse (again.Contains "audit record ") "nor audited"
            finally
                (provider :> IDisposable).Dispose()
                Directory.Delete(root, true)
        }

        testCaseAsync "a target row altered after the copy fails the check, naming the fact"
        <| async {
            let provider, storage, events, root = composed conn

            try
                let scope = "team-" + Guid.NewGuid().ToString "N"
                let! source = seed storage events scope
                let! _, code = run provider [ "--scope"; scope ]
                Expect.equal code 0 "the copy verifies"

                let! export = source.ExportScope scope
                let altered = export.Facts.Head.FactId
                use dataSource = NpgsqlDataSource.Create conn

                use cmd =
                    dataSource.CreateCommand(
                        sprintf
                            "UPDATE %s SET as_of_ticks = as_of_ticks + 1 WHERE scope = @scope AND fact_id = @id"
                            Table
                    )

                cmd.Parameters.AddWithValue("scope", scope) |> ignore
                cmd.Parameters.AddWithValue("id", altered) |> ignore
                let! _ = cmd.ExecuteNonQueryAsync() |> Async.AwaitTask

                let! text, verifyCode = run provider [ "--scope"; scope; "--verify" ]
                Expect.equal verifyCode 1 "a differing scope exits non-zero"
                Expect.stringContains text "VERIFICATION FAILED" "the verdict"
                Expect.stringContains text altered "the altered fact is named"
            finally
                (provider :> IDisposable).Dispose()
                Directory.Delete(root, true)
        }

        testCaseAsync "--all-scopes lists the composition's scopes; --ids over a current scope moves nothing"
        <| async {
            let provider, storage, events, root = composed conn

            try
                let! _ = seed storage events ScopeEnumeration.PlatformContainer
                let! text, code = run provider [ "--all-scopes"; "--ids"; "--dry-run" ]
                Expect.equal code 0 text

                Expect.stringContains
                    text
                    (sprintf "blob %s: 24 facts read, would re-key 0" ScopeEnumeration.PlatformContainer)
                    "the well-known container is enumerated and its ids are current"

                Expect.stringContains text "postgres " "the table leg runs when Postgres is composed"
            finally
                (provider :> IDisposable).Dispose()
                Directory.Delete(root, true)
        }
    ]

[<Tests>]
let tests =
    testList "toolup facts migrate" [
        verbTests
        grammarTests
        blobOnlyTests
        match Environment.GetEnvironmentVariable "TOOLUP_TEST_POSTGRES" with
        | null
        | "" ->
            testList "the task over a composed provider (live)" [
                ptestCase "skipped — TOOLUP_TEST_POSTGRES not set" <| fun _ -> ()
            ]
        | conn -> liveTests conn
    ]