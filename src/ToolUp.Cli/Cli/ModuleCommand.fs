// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup module add` / `toolup module remove` — turn the manual
/// post-scaffold registration step into a clean, *reversible* operation
/// (Phase 168). `add` scaffolds the four-file module (the Phase 11.B
/// template, embedded here so the tool is self-contained) and
/// append-only-registers it into the composition root + project files at
/// a `toolup:modules` marker, recording every edit in a per-module
/// ledger. `remove` replays the ledger in reverse — deleting exactly the
/// inserted lines and the scaffolded folder — so the tree returns
/// byte-for-byte to its pre-add state (a round-trip test pins this).
///
/// The transaction is the point: registration is *append-only* (a line
/// inserted after a marker, never an edit to an existing line) so it
/// merges cleanly under parallel development, and removal is *precise*
/// (driven by the recorded ledger) rather than best-effort.
module ToolUp.Cli.ModuleCommand

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Cli.Dispatch

/// Embedded template files (logical name → emitted file name). `MyModule`
/// in the template content + the project/props file names is substituted
/// with the new module's name.
let private templateFiles = [
    "module/SharedTypes.fs", "SharedTypes.fs"
    "module/Server.fs", "Server.fs"
    "module/ClientModel.fs", "ClientModel.fs"
    "module/ClientView.fs", "ClientView.fs"
    "module/MyModule.fsproj", "{{name}}.fsproj"
    "module/MyModule.Client.props", "{{name}}.Client.props"
]

/// The append-only registration marker. A line is inserted on the line
/// *after* the marker; the marker itself is never touched, so two `add`
/// runs against the same marker each append their own line and three-way-
/// merge cleanly.
[<Literal>]
let FsMarker = "// toolup:modules"

[<Literal>]
let MsBuildMarker = "<!-- toolup:modules -->"

let private isMsBuild (path: string) =
    let e = Path.GetExtension(path).ToLowerInvariant()
    e = ".fsproj" || e = ".csproj" || e = ".props" || e = ".targets"

// ── ledger ───────────────────────────────────────────────────────────

type private Insertion = { File: string; Line: string }

type private Ledger = {
    Name: string
    CreatedDir: string
    Insertions: Insertion list
}

let private ledgerPath (appRoot: string) (name: string) =
    Path.Combine(appRoot, ".toolup", "modules", name + ".json")

let private writeLedger (appRoot: string) (ledger: Ledger) =
    let doc = JsonObject()
    doc["name"] <- JsonValue.Create ledger.Name
    doc["createdDir"] <- JsonValue.Create ledger.CreatedDir
    let arr = JsonArray()

    for ins in ledger.Insertions do
        let o = JsonObject()
        o["file"] <- JsonValue.Create ins.File
        o["line"] <- JsonValue.Create ins.Line
        arr.Add o

    doc["insertions"] <- arr
    let path = ledgerPath appRoot ledger.Name
    Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
    File.WriteAllText(path, doc.ToJsonString(JsonSerializerOptions(WriteIndented = true)))

let private readLedger (appRoot: string) (name: string) : Result<Ledger, string> =
    let path = ledgerPath appRoot name

    if not (File.Exists path) then
        Error(sprintf "no ledger for module '%s' (expected %s) — was it added with `toolup module add`?" name path)
    else
        try
            let doc = JsonNode.Parse(File.ReadAllText path).AsObject()

            let insertions =
                doc["insertions"].AsArray()
                |> Seq.map (fun n ->
                    let o = n.AsObject()

                    {
                        File = o["file"].GetValue<string>()
                        Line = o["line"].GetValue<string>()
                    })
                |> List.ofSeq

            Ok {
                Name = doc["name"].GetValue<string>()
                CreatedDir = doc["createdDir"].GetValue<string>()
                Insertions = insertions
            }
        with ex ->
            Error(sprintf "module ledger '%s' is corrupt: %s" path ex.Message)

// ── registration (append-only, marker-anchored) ──────────────────────

/// Insert `line` on the line after the first `marker` occurrence in
/// `text`, preserving the file's newline style. Returns `None` when the
/// marker is absent (the caller reports it rather than guessing where to
/// register).
let insertAfterMarker (marker: string) (line: string) (text: string) : string option =
    let newline = if text.Contains "\r\n" then "\r\n" else "\n"
    let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList

    let rec splice acc =
        function
        | [] -> None
        | (l: string) :: rest when l.Contains marker -> Some(List.rev (line :: l :: acc) @ rest)
        | l :: rest -> splice (l :: acc) rest

    splice [] lines |> Option.map (String.concat newline)

/// Remove the first exact-match `line` from `text` (the reverse of
/// `insertAfterMarker`). Byte-identical: only the inserted line +its
/// newline is dropped.
let removeLine (line: string) (text: string) : string =
    let newline = if text.Contains "\r\n" then "\r\n" else "\n"
    let lines = text.Replace("\r\n", "\n").Split('\n') |> Array.toList

    let rec drop acc =
        function
        | [] -> List.rev acc
        | (l: string) :: rest when l = line -> List.rev acc @ rest // drop first match only
        | l :: rest -> drop (l :: acc) rest

    drop [] lines |> String.concat newline

// ── option model ─────────────────────────────────────────────────────

type Options = {
    Name: string option
    AppRoot: string
    ModulesDir: string
    Register: string list
}

let private defaults = {
    Name = None
    AppRoot = "."
    ModulesDir = "Modules"
    Register = []
}

let rec private parse (opts: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] -> Ok opts
    | "--name" :: v :: rest -> parse { opts with Name = Some v } rest
    | "--app-root" :: v :: rest -> parse { opts with AppRoot = v } rest
    | "--modules-dir" :: v :: rest -> parse { opts with ModulesDir = v } rest
    | "--register" :: v :: rest ->
        parse
            {
                opts with
                    Register = opts.Register @ [ v ]
            }
            rest
    | ("--name" | "--app-root" | "--modules-dir" | "--register") :: [] ->
        Error(sprintf "missing value for %s" (List.head args))
    | unknown :: _ -> Error(sprintf "unrecognised argument: %s" unknown)

let private addHelp = [
    "Usage: toolup module add --name <Name> --app-root <dir> [--modules-dir <sub>] [--register <file>]..."
    ""
    "Scaffolds the four-file module under <app-root>/<modules-dir>/<Name>/ and append-only-"
    "registers it into each --register file at a 'toolup:modules' marker, recording every edit"
    "in a ledger so `toolup module remove` reverses it byte-for-byte."
    ""
    "Marker: '<!-- toolup:modules -->' in .fsproj/.props (gets a <ProjectReference>),"
    "        '// toolup:modules' in .fs files (gets a ClientView.register() call)."
    ""
    "Options:"
    "  --name <Name>        Module name (required)."
    "  --app-root <dir>     Application root the module is scaffolded under. Default '.'."
    "  --modules-dir <sub>  Sub-directory for modules under the app root. Default 'Modules'."
    "  --register <file>    A composition-root / project file to register the module into (repeatable)."
]

let private removeHelp = [
    "Usage: toolup module remove --name <Name> --app-root <dir>"
    ""
    "Reverses a prior `toolup module add`: deletes exactly the recorded registration lines and"
    "the scaffolded module folder, restoring the tree byte-for-byte. Reads the per-module ledger"
    "written by `add` (under <app-root>/.toolup/modules/)."
    ""
    "Options:"
    "  --name <Name>        Module name (required)."
    "  --app-root <dir>     Application root used at add time. Default '.'."
]

let private usageError (help: string list) (label: string) (message: string) =
    eprintfn "toolup module %s: %s" label message
    eprintfn ""
    help |> List.iter (eprintfn "%s")
    ExitUsage

/// The registration line for a given target file + module name.
let private registrationLine (modulesDir: string) (name: string) (file: string) : string =
    if isMsBuild file then
        // Path from the register file's directory to the module fsproj.
        let fromDir = Path.GetDirectoryName(Path.GetFullPath file)

        let moduleProj =
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName file, "..", modulesDir, name, name + ".fsproj"))

        let rel = Path.GetRelativePath(fromDir, moduleProj).Replace('/', '\\')
        sprintf "    <ProjectReference Include=\"%s\" />" rel
    else
        sprintf "    %s.ClientView.register()" name

let private runAdd (opts: Options) : int =
    match opts.Name with
    | None -> usageError addHelp "add" "--name is required"
    | Some name ->
        let moduleDir = Path.Combine(opts.AppRoot, opts.ModulesDir, name)

        if Directory.Exists moduleDir then
            eprintfn "toolup module add: module directory already exists: %s" moduleDir
            ExitRuntimeError
        else
            // Pre-check every register target has its marker before writing
            // anything — the add is all-or-nothing.
            let missingMarker =
                opts.Register
                |> List.filter (fun f ->
                    not (File.Exists f)
                    || (let marker = if isMsBuild f then MsBuildMarker else FsMarker
                        not ((File.ReadAllText f).Contains marker)))

            match missingMarker with
            | _ :: _ ->
                eprintfn "toolup module add: these --register files are missing or lack the marker:"

                for f in missingMarker do
                    eprintfn "  %s" f

                ExitRuntimeError
            | [] ->
                // 1. Scaffold.
                Directory.CreateDirectory moduleDir |> ignore

                for logical, target in templateFiles do
                    // The template uses the bare `MyModule` sourceName (dotnet-
                    // new style), not a {{token}} — substitute it directly.
                    let content = (Templating.readEmbedded logical).Replace("MyModule", name)
                    let fileName = target.Replace("{{name}}", name)
                    File.WriteAllText(Path.Combine(moduleDir, fileName), content)

                // 2. Register (append-only at each marker), recording each.
                let insertions =
                    opts.Register
                    |> List.map (fun file ->
                        let marker = if isMsBuild file then MsBuildMarker else FsMarker
                        let line = registrationLine opts.ModulesDir name file
                        let text = File.ReadAllText file

                        match insertAfterMarker marker line text with
                        | Some updated ->
                            File.WriteAllText(file, updated)
                            { File = file; Line = line }
                        | None -> failwithf "marker vanished from %s" file)

                // 3. Ledger.
                writeLedger opts.AppRoot {
                    Name = name
                    CreatedDir = moduleDir
                    Insertions = insertions
                }

                printfn
                    "added module %s (%d file(s) scaffolded, %d registration(s))"
                    name
                    (List.length templateFiles)
                    (List.length insertions)

                ExitOk

let private runRemove (opts: Options) : int =
    match opts.Name with
    | None -> usageError removeHelp "remove" "--name is required"
    | Some name ->
        match readLedger opts.AppRoot name with
        | Error e ->
            eprintfn "toolup module remove: %s" e
            ExitRuntimeError
        | Ok ledger ->
            // 1. Reverse each recorded registration (exact line removal).
            for ins in ledger.Insertions do
                if File.Exists ins.File then
                    File.WriteAllText(ins.File, removeLine ins.Line (File.ReadAllText ins.File))

            // 2. Delete the scaffolded folder.
            if Directory.Exists ledger.CreatedDir then
                Directory.Delete(ledger.CreatedDir, true)

            // 3. Drop the ledger entry.
            File.Delete(ledgerPath opts.AppRoot name)
            printfn "removed module %s (%d registration(s) reversed)" name (List.length ledger.Insertions)
            ExitOk

let addCommand = {
    Path = [ "module"; "add" ]
    Summary = "Scaffold + transactionally register a module."
    Help = addHelp
    Run =
        fun args ->
            match parse defaults args with
            | Error m -> usageError addHelp "add" m
            | Ok opts -> runAdd opts
}

let removeCommand = {
    Path = [ "module"; "remove" ]
    Summary = "Reverse a `module add` byte-for-byte."
    Help = removeHelp
    Run =
        fun args ->
            match parse defaults args with
            | Error m -> usageError removeHelp "remove" m
            | Ok opts -> runRemove opts
}