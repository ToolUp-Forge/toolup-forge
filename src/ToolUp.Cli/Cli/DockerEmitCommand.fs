// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// `toolup docker emit` — re-emit the maintained Docker host artefacts
/// (`Dockerfile` + `.dockerignore` + `healthcheck.sh` + `compose.yml`)
/// at a solution root, substituting the project/image/port tokens. This
/// closes the Phase 16b tail: the migration doc deferred the command to
/// "once the CLI substrate lands", pointing operators at
/// `dotnet new platformsdk-docker --force` in the interim. The four
/// template files are the same ones the `platformsdk-docker` template
/// ships — embedded into the tool assembly so there is one source of
/// truth (see Templating.readEmbedded).
module ToolUp.Cli.DockerEmitCommand

open System.IO
open ToolUp.Cli.Dispatch

/// Embedded-resource logical names (must match the `<EmbeddedResource
/// LogicalName=...>` items in ToolUp.Cli.fsproj) paired with the file
/// name each emits and whether it carries `{{token}}` substitutions.
/// `healthcheck.sh` is byte-for-byte verbatim — it has no tokens.
let private artefacts = [
    "docker/Dockerfile.template", "Dockerfile", true
    "docker/dockerignore.template", ".dockerignore", false
    "docker/compose.yml.template", "compose.yml", true
    "docker/healthcheck.sh", "healthcheck.sh", false
]

type Options = {
    ServerProject: string option
    ServerDll: string option
    ImageName: string option
    HostPort: string
    OutputDir: string
    Force: bool
}

let private defaults = {
    ServerProject = None
    ServerDll = None
    ImageName = None
    HostPort = "8080"
    OutputDir = "."
    Force = false
}

/// Parse the residual args after `docker emit` into an `Options`. Pure:
/// no IO, no validation of values beyond shape — `Error` carries a
/// human-readable usage message.
let rec private parse (opts: Options) (args: string list) : Result<Options, string> =
    match args with
    | [] -> Ok opts
    | "--server-project" :: v :: rest -> parse { opts with ServerProject = Some v } rest
    | "--server-dll" :: v :: rest -> parse { opts with ServerDll = Some v } rest
    | "--image-name" :: v :: rest -> parse { opts with ImageName = Some v } rest
    | "--host-port" :: v :: rest -> parse { opts with HostPort = v } rest
    | "--output-dir" :: v :: rest -> parse { opts with OutputDir = v } rest
    | "--force" :: rest -> parse { opts with Force = true } rest
    | ("--server-project" | "--server-dll" | "--image-name" | "--host-port" | "--output-dir") :: [] ->
        Error(sprintf "missing value for %s" (List.head args))
    | unknown :: _ -> Error(sprintf "unrecognised argument: %s" unknown)

/// Validate a parsed `Options` and produce the token bindings. Pure —
/// surfaces every required-field / type error as a usage message.
let bindings (opts: Options) : Result<(string * string) list, string> =
    let required name =
        function
        | Some v -> Ok v
        | None -> Error(sprintf "--%s is required" name)

    match required "server-project" opts.ServerProject with
    | Error e -> Error e
    | Ok serverProject ->
        match required "server-dll" opts.ServerDll with
        | Error e -> Error e
        | Ok serverDll ->
            match required "image-name" opts.ImageName with
            | Error e -> Error e
            | Ok imageName ->
                match System.Int32.TryParse opts.HostPort with
                | false, _ -> Error(sprintf "--host-port must be an integer (got '%s')" opts.HostPort)
                | true, _ ->
                    Ok [
                        "server-project", serverProject
                        "server-dll", serverDll
                        "image-name", imageName
                        "host-port", opts.HostPort
                    ]

let private helpText = [
    "Usage: toolup docker emit --server-project <dir> --server-dll <name> --image-name <name>"
    "                          [--host-port <port>] [--output-dir <dir>] [--force]"
    ""
    "Re-emits Dockerfile, .dockerignore, healthcheck.sh and compose.yml at the output"
    "directory (default: current directory), substituting the deployment's tokens."
    ""
    "Options:"
    "  --server-project <dir>   Server project directory under src/, e.g. MyApp-Server. (required)"
    "  --server-dll <name>      Server assembly name without .dll, e.g. MyApp-Server. (required)"
    "  --image-name <name>      Container image name (lowercase), e.g. myapp. (required)"
    "  --host-port <port>       Host-side port compose publishes (container-side is 5000). Default 8080."
    "  --output-dir <dir>       Directory to write the four files into. Default '.'."
    "  --force                  Overwrite existing files instead of refusing."
    ""
    "Redis stays commented in compose.yml (the 'none' notification-channel default);"
    "uncomment it by hand for multi-silo deployments."
]

let private usageError (message: string) =
    eprintfn "toolup docker emit: %s" message
    eprintfn ""
    helpText |> List.iter (eprintfn "%s")
    ExitUsage

let private runWith (opts: Options) : int =
    match bindings opts with
    | Error message -> usageError message
    | Ok tokens ->
        Directory.CreateDirectory opts.OutputDir |> ignore

        // Pre-check every target before writing any — an emit is
        // all-or-nothing, so a clash on file 3 never leaves files 1–2
        // half-overwritten.
        let targets =
            artefacts
            |> List.map (fun (logical, name, subst) -> logical, Path.Combine(opts.OutputDir, name), name, subst)

        let clashes =
            if opts.Force then
                []
            else
                targets |> List.filter (fun (_, path, _, _) -> File.Exists path)

        match clashes with
        | _ :: _ ->
            eprintfn "toolup docker emit: refusing to overwrite existing file(s) (pass --force to overwrite):"

            for (_, _, name, _) in clashes do
                eprintfn "  %s" name

            ExitRuntimeError
        | [] ->
            for (logical, path, name, subst) in targets do
                let raw = Templating.readEmbedded logical
                let content = if subst then Templating.substitute tokens raw else raw
                File.WriteAllText(path, content)
                printfn "wrote %s" name

            ExitOk

let command = {
    Path = [ "docker"; "emit" ]
    Summary = "Emit Dockerfile + compose + healthcheck for a deployment."
    Help = helpText
    Run =
        fun args ->
            match parse defaults args with
            | Error message -> usageError message
            | Ok opts -> runWith opts
}