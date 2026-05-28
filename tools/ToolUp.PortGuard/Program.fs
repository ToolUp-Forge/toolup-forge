module ToolUp.PortGuard.Program

open System
open System.IO
open System.Text.RegularExpressions

// Scans a workspace root for ports already in use by adjacent ToolUp
// apps, then rejects requested port values that clash. Invoked before
// `dotnet new platformsdk-{solution,application}` so a clash errors out
// before any file is written.

type Args = {
    ServerPort: int option
    ClientPort: int option
    WorkspaceRoot: string
    Json: bool
}

let private defaultArgs () = {
    ServerPort = None
    ClientPort = None
    WorkspaceRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))
    Json = false
}

let private parseArgs (argv: string array) : Result<Args, string> =
    let rec loop (acc: Args) (xs: string list) =
        match xs with
        | [] -> Ok acc
        | "--server-port" :: v :: rest ->
            match Int32.TryParse v with
            | true, n -> loop { acc with ServerPort = Some n } rest
            | _ -> Error $"--server-port expects an integer, got '{v}'"
        | "--client-port" :: v :: rest ->
            match Int32.TryParse v with
            | true, n -> loop { acc with ClientPort = Some n } rest
            | _ -> Error $"--client-port expects an integer, got '{v}'"
        | "--workspace-root" :: v :: rest ->
            loop
                {
                    acc with
                        WorkspaceRoot = Path.GetFullPath v
                }
                rest
        | "--json" :: rest -> loop { acc with Json = true } rest
        | flag :: _ -> Error $"Unrecognised argument '{flag}'"

    loop (defaultArgs ()) (List.ofArray argv)

let private launchSettingsPortRegex =
    Regex(@"""applicationUrl""\s*:\s*""([^""]+)""", RegexOptions.Compiled)

let private composePortRegex =
    Regex(@"-\s*""?(?:\$\{[A-Z_]+:-)?(\d{2,5})(?:\}\s*)?:\s*\d{2,5}""?", RegexOptions.Compiled)

let private viteConfigPortRegex =
    Regex(@"\bport\s*:\s*(\d{2,5})", RegexOptions.Compiled)

let private extractPortsFromUrl (url: string) =
    let m = Regex.Match(url, @":(\d{2,5})\b")
    if m.Success then [ Int32.Parse m.Groups.[1].Value ] else []

let private scanLaunchSettings (path: string) =
    try
        let text = File.ReadAllText path

        launchSettingsPortRegex.Matches text
        |> Seq.cast<Match>
        |> Seq.collect (fun m -> extractPortsFromUrl m.Groups.[1].Value)
        |> Seq.toList
    with _ -> []

let private scanComposeYml (path: string) =
    try
        let text = File.ReadAllText path

        composePortRegex.Matches text
        |> Seq.cast<Match>
        |> Seq.choose (fun m ->
            match Int32.TryParse m.Groups.[1].Value with
            | true, n -> Some n
            | _ -> None)
        |> Seq.toList
    with _ -> []

let private scanViteConfig (path: string) =
    try
        let text = File.ReadAllText path

        viteConfigPortRegex.Matches text
        |> Seq.cast<Match>
        |> Seq.choose (fun m ->
            match Int32.TryParse m.Groups.[1].Value with
            | true, n -> Some n
            | _ -> None)
        |> Seq.toList
    with _ -> []

type PortUsage = { Port: int; Source: string }

let private discoverPorts (root: string) : PortUsage list =
    if Directory.Exists root |> not then
        []
    else
        let walkFiles () = seq {
            let skip (segment: string) =
                let s = segment.ToLowerInvariant()

                s = "bin"
                || s = "obj"
                || s = "node_modules"
                || s = ".git"
                || s = "_template-test"
                || s = "templates"

            let rec walk (dir: string) = seq {
                let dirName = Path.GetFileName dir

                if skip dirName |> not then
                    yield!
                        try
                            Directory.GetFiles dir
                        with _ -> [||]

                    yield!
                        try
                            Directory.GetDirectories dir |> Seq.collect walk
                        with _ ->
                            Seq.empty
            }

            yield! walk root
        }

        let files = walkFiles () |> Seq.toList

        let launchSettings =
            files
            |> List.filter (fun f ->
                Path.GetFileName(f).Equals("launchSettings.json", StringComparison.OrdinalIgnoreCase))

        let composeFiles =
            files
            |> List.filter (fun f ->
                let name = Path.GetFileName(f).ToLowerInvariant()
                name = "compose.yml" || name = "docker-compose.yml")

        let viteConfigs =
            files
            |> List.filter (fun f ->
                let name = Path.GetFileName(f).ToLowerInvariant()
                name = "vite.config.mts" || name = "vite.config.ts" || name = "vite.config.js")

        let fromLaunch =
            launchSettings
            |> List.collect (fun p -> scanLaunchSettings p |> List.map (fun port -> { Port = port; Source = p }))

        let fromCompose =
            composeFiles
            |> List.collect (fun p -> scanComposeYml p |> List.map (fun port -> { Port = port; Source = p }))

        let fromVite =
            viteConfigs
            |> List.collect (fun p -> scanViteConfig p |> List.map (fun port -> { Port = port; Source = p }))

        fromLaunch @ fromCompose @ fromVite

let private printHelp () =
    printfn "ToolUp.PortGuard — port-clash detector for the platformsdk-solution / platformsdk-application templates."
    printfn ""
    printfn "Usage:"

    printfn
        "  dotnet run --project tools/ToolUp.PortGuard -- --server-port <int> [--client-port <int>] [--workspace-root <path>] [--json]"

    printfn ""
    printfn "Exit codes:"
    printfn "  0 — no clash; requested ports are free."
    printfn "  1 — clash detected; details printed to stderr."
    printfn "  2 — argument error."
    printfn ""

    printfn
        "The scanner walks the workspace root (default: two levels above this tool) for launchSettings.json, compose.yml, and vite.config files and collects every port literal it finds. Requested ports are rejected on exact match."

[<EntryPoint>]
let main argv =
    if Array.exists (fun a -> a = "--help" || a = "-h") argv then
        printHelp ()
        0
    else
        match parseArgs argv with
        | Error msg ->
            eprintfn "ToolUp.PortGuard: %s" msg
            eprintfn "Run with --help for usage."
            2
        | Ok args ->
            let requested =
                [
                    args.ServerPort |> Option.map (fun p -> "server", p)
                    args.ClientPort |> Option.map (fun p -> "client-vite", p)
                ]
                |> List.choose id

            if List.isEmpty requested then
                eprintfn "ToolUp.PortGuard: at least one of --server-port / --client-port is required."
                2
            else
                let usages = discoverPorts args.WorkspaceRoot

                let clashes =
                    requested
                    |> List.collect (fun (role, port) ->
                        usages
                        |> List.filter (fun u -> u.Port = port)
                        |> List.map (fun u -> role, port, u.Source))

                if List.isEmpty clashes then
                    if args.Json then
                        printfn
                            """{"status":"ok","workspaceRoot":"%s","portsChecked":%d}"""
                            (args.WorkspaceRoot.Replace("\\", "\\\\"))
                            (List.length requested)
                    else
                        printfn
                            "ToolUp.PortGuard: OK — no clashes for %s in %s"
                            (requested |> List.map (fun (r, p) -> sprintf "%s=%d" r p) |> String.concat ", ")
                            args.WorkspaceRoot

                    0
                else
                    if args.Json then
                        let entries =
                            clashes
                            |> List.map (fun (role, port, src) ->
                                sprintf
                                    """{"role":"%s","port":%d,"source":"%s"}"""
                                    role
                                    port
                                    (src.Replace("\\", "\\\\")))
                            |> String.concat ","

                        eprintfn """{"status":"clash","clashes":[%s]}""" entries
                    else
                        eprintfn "ToolUp.PortGuard: PORT CLASH DETECTED"

                        for (role, port, src) in clashes do
                            eprintfn "  --%s-port=%d clashes with %s" role port src

                        eprintfn ""

                        eprintfn
                            "Pick a free 10-port band that doesn't overlap any existing reservation in this workspace, then retry."

                    1