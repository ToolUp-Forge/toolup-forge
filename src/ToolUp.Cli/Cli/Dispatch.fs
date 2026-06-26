// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The `toolup` command host. A deliberately tiny, dependency-free
/// (pure-BCL + FSharp.Core) subcommand dispatcher: a registry of
/// `Command` records, longest-path-prefix resolution, `--help`
/// rendering, and an exit-code discipline. Subcommands (Phase 166
/// `stamp`, Phase 168 `module add/remove`, the Phase 16b `docker emit`
/// tail) are appended to the registry in `Program.fs` — they never edit
/// the dispatcher or each other (GP 1 isolation at the command seam).
module ToolUp.Cli.Dispatch

// ─── Exit-code discipline ──────────────────────────────────────────
//
// 0 — success. 1 — the command ran but failed at runtime (e.g. refusing
// to overwrite a file without --force). 2 — the invocation was wrong
// (unknown / incomplete command, bad / missing arguments). Subcommands
// return these from their `Run`; the host returns them from `run`.

[<Literal>]
let ExitOk = 0

[<Literal>]
let ExitRuntimeError = 1

[<Literal>]
let ExitUsage = 2

/// A registered CLI command.
///
/// `Path` is the sequence of literal tokens that selects the command —
/// `[ "version" ]` for `toolup version`, `[ "docker"; "emit" ]` for
/// `toolup docker emit`. `Run` receives the argument tokens that follow
/// the matched path and returns a process exit code. `Help` is the body
/// printed for `toolup <path> --help`.
type Command = {
    Path: string list
    Summary: string
    Help: string list
    Run: string list -> int
}

[<Literal>]
let private ProgramName = "toolup"

/// `-h` / `--help` / `help` in any position request help rather than
/// execution.
let isHelpToken (token: string) =
    match token with
    | "-h"
    | "--help"
    | "help" -> true
    | _ -> false

/// `path` matches the head of `args` (every path token equals the
/// corresponding leading arg token).
let private isPrefixOf (path: string list) (args: string list) =
    let n = List.length path
    n <= List.length args && (args |> List.truncate n) = path

/// Resolve `args` to the registered command with the longest matching
/// path, returning the command plus the residual args after its path.
/// A tie on length is impossible — two commands cannot share a path.
let resolve (commands: Command list) (args: string list) : (Command * string list) option =
    commands
    |> List.filter (fun c -> isPrefixOf c.Path args)
    |> List.sortByDescending (fun c -> List.length c.Path)
    |> List.tryHead
    |> Option.map (fun c -> c, args |> List.skip (List.length c.Path))

/// Commands whose path strictly extends `args` — i.e. `args` names a
/// group (`toolup docker`) rather than a leaf command. Used to print
/// group help instead of an "unknown command" error.
let subcommandsOf (commands: Command list) (args: string list) =
    commands
    |> List.filter (fun c -> List.length c.Path > List.length args && isPrefixOf args c.Path)

let private pathLabel (path: string list) = String.concat " " path

let private renderTopLevel (commands: Command list) = [
    sprintf "%s — ToolUp Platform SDK admin CLI" ProgramName
    ""
    sprintf "Usage: %s <command> [options]" ProgramName
    ""
    "Commands:"
    yield!
        commands
        |> List.sortBy (fun c -> pathLabel c.Path)
        |> List.map (fun c -> sprintf "  %-22s %s" (pathLabel c.Path) c.Summary)
    ""
    sprintf "Run `%s <command> --help` for command-specific options." ProgramName
]

let private renderGroup (group: string list) (subs: Command list) = [
    sprintf "Usage: %s %s <subcommand> [options]" ProgramName (pathLabel group)
    ""
    "Subcommands:"
    yield!
        subs
        |> List.sortBy (fun c -> pathLabel c.Path)
        |> List.map (fun c -> sprintf "  %-22s %s" (pathLabel c.Path) c.Summary)
]

let private renderCommand (cmd: Command) = [
    sprintf "%s %s — %s" ProgramName (pathLabel cmd.Path) cmd.Summary
    ""
    yield! cmd.Help
]

let private printLines (lines: string list) = lines |> List.iter (printfn "%s")

/// Dispatch `argv` against the registry. The single entry point the
/// host process calls; returns the exit code to surface.
let run (commands: Command list) (argv: string[]) : int =
    let args = List.ofArray argv
    let helpRequested = args |> List.exists isHelpToken
    let effective = args |> List.filter (isHelpToken >> not)

    match effective with
    | [] ->
        // No command (or only help tokens) — top-level help is the
        // intended output, exit 0.
        printLines (renderTopLevel commands)
        ExitOk
    | _ ->
        match resolve commands effective with
        | Some(cmd, rest) ->
            if helpRequested then
                printLines (renderCommand cmd)
                ExitOk
            else
                cmd.Run rest
        | None ->
            match subcommandsOf commands effective with
            | [] ->
                eprintfn "%s: unknown command '%s'" ProgramName (pathLabel effective)
                eprintfn ""
                renderTopLevel commands |> List.iter (eprintfn "%s")
                ExitUsage
            | subs ->
                // `effective` names a group with no leaf of its own.
                if helpRequested then
                    printLines (renderGroup effective subs)
                    ExitOk
                else
                    eprintfn "%s: incomplete command '%s'" ProgramName (pathLabel effective)
                    eprintfn ""
                    renderGroup effective subs |> List.iter (eprintfn "%s")
                    ExitUsage