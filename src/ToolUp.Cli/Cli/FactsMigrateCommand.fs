// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup facts migrate` — move a deployment's facts from its blob fact
/// store into its PostgreSQL fact store, check a copy, or move persisted
/// fact ids onto the canonical frame.
///
/// The migration needs Npgsql, the fact tier and the deployment's own
/// storage backend, secrets, event store and metric registry, none of
/// which this dependency-free host carries. So the verb does what
/// `toolup describe --json` does: it runs the deployment's composition
/// root in a startup mode that binds no listener —
/// `--operator-task facts-migrate`, the task the Postgres companion
/// contributes (`FactStoreMigrationCompose.withFactsMigrateTask`) — and
/// prints the report the task writes. The blob backend is whichever storage
/// companion the root composed and the connection string is the one it
/// resolves, so neither is ever a flag here, and neither is echoed.
module ToolUp.Cli.FactsMigrateCommand

open System
open System.IO
open ToolUp.Cli.Dispatch

/// The operator task the composition root runs.
[<Literal>]
let TaskName = "facts-migrate"

/// Where the composition root is, and what to hand its task.
type Options = {
    /// `dotnet run --project <path>` (`Choice1Of2`) or `dotnet <dll>`
    /// (`Choice2Of2`).
    Root: Choice<string, string>
    /// Seconds to wait for the root to exit.
    TimeoutSeconds: int
    /// The task's own arguments, forwarded verbatim: the task parses them,
    /// so there is one grammar.
    TaskArgs: string list
}

/// The composition root in the current directory, an hour's wait, no task
/// arguments.
let defaultOptions: Options = {
    Root = Choice1Of2 "."
    TimeoutSeconds = 3600
    TaskArgs = []
}

/// Parse the tokens after `facts migrate`. `--project`, `--dll` and
/// `--timeout` are this verb's; every other token is the task's.
let parse (args: string list) : Result<Options, string> =
    let rec go (roots: Choice<string, string> list) (opts: Options) (task: string list) (rest: string list) =
        match rest with
        | [] ->
            let opts = { opts with TaskArgs = List.rev task }

            match roots with
            | [] -> Ok opts
            | [ root ] -> Ok { opts with Root = root }
            | _ -> Error "name at most one of --project and --dll"
        | "--project" :: path :: tail -> go (Choice1Of2 path :: roots) opts task tail
        | "--dll" :: path :: tail -> go (Choice2Of2 path :: roots) opts task tail
        | "--timeout" :: seconds :: tail ->
            match Int32.TryParse seconds with
            | true, n when n > 0 && n <= Int32.MaxValue / 1000 -> go roots { opts with TimeoutSeconds = n } task tail
            | _ -> Error(sprintf "--timeout takes a positive number of seconds, not '%s'" seconds)
        | [ ("--project" | "--dll" | "--timeout") as flag ] -> Error(sprintf "%s needs a value" flag)
        | token :: tail -> go roots opts (token :: task) tail

    match go [] defaultOptions [] args with
    | Ok opts when not (opts.TaskArgs |> List.exists (fun a -> a = "--scope" || a = "--all-scopes")) ->
        Error "name a scope (--scope <id>) or pass --all-scopes"
    | result -> result

/// The command line that runs the composition root's `facts-migrate` task,
/// writing its report to `outFile`.
let invocation (opts: Options) (outFile: string) : string * string list =
    let taskArgs =
        [ "--operator-task-out"; outFile; "--operator-task"; TaskName ] @ opts.TaskArgs

    match opts.Root with
    | Choice2Of2 dll -> "dotnet", dll :: taskArgs
    | Choice1Of2 project -> "dotnet", [ "run"; "--project"; project; "--no-launch-profile"; "--" ] @ taskArgs

/// Run the task through `runner`, print its report to `out`, and return
/// the task's exit code.
let run (runner: DescribeCommand.ProcessRunner) (out: TextWriter) (opts: Options) : int =
    let outFile =
        Path.Combine(Path.GetTempPath(), sprintf "toolup-facts-migrate-%s.txt" (Guid.NewGuid().ToString "N"))

    let fileName, args = invocation opts outFile

    try
        match runner fileName args opts.TimeoutSeconds with
        | None ->
            eprintfn
                "facts migrate: the composition root did not exit within %d s. A migration that is still copying resumes after its last committed page when run again; an application on a ToolUp.Platform.Server that predates the --operator-task startup mode ignores the flag and starts serving."
                opts.TimeoutSeconds

            ExitRuntimeError
        | Some code when File.Exists outFile ->
            out.Write(File.ReadAllText outFile)
            out.Flush()
            code
        | Some code ->
            eprintfn
                "facts migrate: the composition root exited %d without a report. It may have failed before composing (see the output above), not compose the facts-migrate task (FactStoreMigrationCompose.withFactsMigrateTask), or predate the --operator-task startup mode."
                code

            if code = ExitOk then ExitRuntimeError else code
    finally
        if File.Exists outFile then
            File.Delete outFile

let help = [
    "Usage: toolup facts migrate [--project <path> | --dll <path>] [--timeout <seconds>]"
    "                            (--scope <id>... | --all-scopes) [--dry-run | --verify | --ids]"
    "                            [--max-scope-facts <n>] [--allow-unreadable-source]"
    ""
    "Move the deployment's facts from its BlobFactStore into its PostgresFactStore,"
    "verifying each scope, with one FactStoreMigrated audit record per scope."
    ""
    "Runs the composition root (default: the project in the current directory) in"
    "its --operator-task startup mode, which composes the application and exits"
    "without binding a listener. The root must compose"
    "PostgresFactStoreCompose.withPostgresFactStore and"
    "FactStoreMigrationCompose.withFactsMigrateTask. The source is the blob storage"
    "the root composes and the target the connection string it resolves: neither"
    "is a flag, and neither is printed. Stop every writer first."
    ""
    "  --scope <id>               A scope to migrate; repeatable."
    "  --all-scopes               Every scope the composition enumerates."
    "  --dry-run                  Read and check the source; write nothing."
    "  --verify                   Compare the existing copy with its source; write nothing."
    "  --ids                      Move persisted fact ids onto the canonical frame, in the"
    "                             blob store and the table (with --dry-run: plan only)."
    "  --max-scope-facts <n>      Raise the per-scope bound (300,000) for this run."
    "  --allow-unreadable-source  Migrate scopes holding unreadable blobs."
    "  --timeout <seconds>        How long to wait for the root (default 3600)."
    ""
    "Per scope it prints facts read, rows written, rows skipped as already present,"
    "the verification's verdict with every differing fact id, and the audit record."
    "A second run skips a scope already verified over the same source facts; an"
    "interrupted run resumes after its last committed page."
    "Exit code: 0 when every scope verified (or, with --dry-run, would migrate);"
    "1 when any scope differs, is refused, or failed to copy; 2 for a usage error."
]

let command = {
    Path = [ "facts"; "migrate" ]
    Summary = "Migrate the blob fact store into the Postgres fact store, or check a copy."
    Help = help
    Run =
        fun args ->
            match parse args with
            | Error e ->
                eprintfn "facts migrate: %s" e
                eprintfn "Run 'toolup facts migrate --help' for usage."
                ExitUsage
            | Ok opts -> run DescribeCommand.processRunner Console.Out opts
}