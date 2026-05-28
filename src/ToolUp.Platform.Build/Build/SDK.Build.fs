module ToolUp.Platform.Build

open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open System

// ─── Process helpers ───────────────────────────────────────────────

module Proc =
    module Parallel =
        let locker = obj ()

        let colors = [|
            ConsoleColor.Blue
            ConsoleColor.Yellow
            ConsoleColor.Magenta
            ConsoleColor.Cyan
            ConsoleColor.DarkBlue
            ConsoleColor.DarkYellow
            ConsoleColor.DarkMagenta
            ConsoleColor.DarkCyan
        |]

        let print color (colored: string) (line: string) =
            lock locker (fun () ->
                let currentColor = Console.ForegroundColor
                Console.ForegroundColor <- color
                Console.Write colored
                Console.ForegroundColor <- currentColor
                Console.WriteLine line)

        let onStdout index name (line: string) =
            let color = colors[index % colors.Length]

            if isNull line then
                print color $"{name}: --- END ---" ""
            else if String.isNotNullOrEmpty line then
                print color $"{name}: " line

        let onStderr name (line: string) =
            let color = ConsoleColor.Red

            if isNull line |> not then
                print color $"{name}: " line

        let redirect (index, (name, createProcess)) =
            createProcess
            |> CreateProcess.redirectOutputIfNotRedirected
            |> CreateProcess.withOutputEvents (onStdout index name) (onStderr name)

        let printStarting indexed =
            for (index, (name, c: CreateProcess<_>)) in indexed do
                let color = colors[index % colors.Length]
                let wd = c.WorkingDirectory |> Option.defaultValue ""
                let exe = c.Command.Executable
                let args = c.Command.Arguments.ToStartInfo
                print color $"{name}: {wd}> {exe} {args}" ""

        let run cs =
            cs
            |> Seq.toArray
            |> Array.indexed
            |> fun x ->
                printStarting x
                x
            |> Array.map redirect
            |> Array.Parallel.map Proc.run

let private createProcess exe args dir =
    CreateProcess.fromRawCommand exe args
    |> CreateProcess.withWorkingDirectory dir
    |> CreateProcess.ensureExitCode

let private dotnet args dir = createProcess "dotnet" args dir
let private docker args dir = createProcess "docker" args dir

let private createProcessFromPath processName args dir =
    let path =
        match ProcessUtils.tryFindFileOnPath processName with
        | Some path -> path
        | None ->
            $"{processName} was not found in path. Please install it and make sure it's available from your path."
            |> failwith

    createProcess path args dir

let private npm args dir = createProcessFromPath "npm" args dir
let private run proc arg dir = proc arg dir |> Proc.run |> ignore

let private runParallel processes =
    processes |> Proc.Parallel.run |> ignore

// ─── Build configuration ───────────────────────────────────────────

open ToolUp.Platform

type BuildConfig = {
    ServerProject: string
    ClientProject: string
    Output: BuildOutput
    Port: int
}

module BuildConfig =
    let defaults = {
        ServerProject = "src/ToolUpApp-Server/ToolupApp-Server.fsproj"
        ClientProject = "src/ToolUpApp-Client/ToolupApp-Client.fsproj"
        Output = BuildOutput.defaults
        Port = 5000
    }

// ─── FAKE pipeline ─────────────────────────────────────────────────

/// Initialise the FAKE execution context
let init (argv: string[]) =
    let execContext = Context.FakeExecutionContext.Create false "build.fsx" []
    Context.setExecutionContext (Context.RuntimeContext.Fake execContext)

/// Register all standard FAKE targets
let registerTargets (config: BuildConfig) =
    let serverPath = Path.getFullName (IO.Path.GetDirectoryName config.ServerProject)
    let clientPath = Path.getFullName (IO.Path.GetDirectoryName config.ClientProject)
    let deployPath = Path.getFullName config.Output.ServerPublishPath

    Target.create "Clean" (fun _ ->
        Shell.cleanDir deployPath
        run dotnet [ "fable"; "clean"; "--yes" ] clientPath)

    Target.create "KillStaleProcesses" (fun _ ->
        // Kill stale esbuild processes that may hold file locks in node_modules,
        // which prevents npm ci from completing. Only kills esbuild (project-specific),
        // not node (which could be running unrelated apps).
        try
            Trace.tracefn "Killing stale esbuild processes..."
            Process.killAllByName "esbuild"
        with _ ->
            ())

    Target.create "RestoreClientDependencies" (fun _ -> run npm [ "ci"; "--include=optional" ] clientPath)

    Target.create "Build" (fun _ -> run dotnet [ "build" ] serverPath)

    Target.create "Bundle" (fun _ ->
        run dotnet [ "build"; config.ServerProject; "-c"; "Release" ] "."

        [
            "server", dotnet [ "publish"; "-c"; "Release"; "-o"; deployPath ] serverPath
            "client",
            dotnet
                [
                    "fable"
                    "-o"
                    "output"
                    "-s"
                    "--run"
                    "npx"
                    "vite"
                    "build"
                    "--emptyOutDir"
                ]
                clientPath
        ]
        |> runParallel)

    Target.create "Run" (fun _ ->
        let applyModuleFilter (p: CreateProcess<_>) =
            match Environment.GetEnvironmentVariable "TOOLUP_MODULE" with
            | null
            | "" -> p
            | m -> CreateProcess.setEnvironmentVariable "TOOLUP_MODULE" m p

        let serverProcess =
            dotnet [ "watch"; "run"; "--no-restore" ] serverPath
            |> CreateProcess.setEnvironmentVariable "SERVER_PORT" (string config.Port)
            |> applyModuleFilter

        [
            "server", serverProcess
            "client",
            dotnet
                [
                    "fable"
                    "watch"
                    "-o"
                    "output"
                    "-s"
                    "-c"
                    "Debug"
                    "--run"
                    "npx"
                    "vite"
                ]
                clientPath
            |> applyModuleFilter
        ]
        |> runParallel)

    Target.create "Docker" (fun _ -> run docker [ "compose"; "up"; "-d" ] ".")

    Target.create "Format" (fun _ -> run dotnet [ "fantomas"; "." ] ".")

    Target.create "Pack" (fun _ ->
        // Pack each public-surface SDK fsproj into the local NuGet feed
        // at ../local-nuget-feed (a sibling folder of this repo — see
        // this repo's nuget.config header for the local-feed rationale).
        //
        // Per-fsproj iteration (rather than solution-level pack)
        // sidesteps the bootstrap chicken-and-egg: a consumer app's
        // fsprojs <PackageReference> the SDK packages from this very
        // feed, so a solution-level pack spanning both would try to
        // build the consumer before the packages exist. Each SDK fsproj's
        // transitive ProjectReference graph stays inside the SDK
        // boundary, so per-fsproj pack builds cleanly.
        let outputDir = Path.getFullName "../local-nuget-feed"
        Directory.ensure outputDir

        let projects =
            !!"src/**/*.fsproj"
            -- "src/ToolUpApp-Server/**/*.fsproj"
            -- "src/ToolUpApp-Client/**/*.fsproj"
            -- "src/Modules/**/*.fsproj"
            -- "src/TestHarness/**/*.fsproj"
            -- "src/ToolUp.Algorithms/**/*.fsproj"
            -- "src/ToolUp.Platform.Tests/**/*.fsproj"
            -- "src/ToolUp.Forms.Tests/**/*.fsproj"
            -- "src/ToolUp.Scheduling.Tests/**/*.fsproj"
            -- "src/ToolUp.RAG.Evaluation/**/*.fsproj"
            -- "src/ToolUp.RAG.Benchmarks/**/*.fsproj"

        for proj in projects do
            Trace.tracefn "Packing %s..." proj

            run dotnet [ "pack"; proj; "-c"; "Release"; "-o"; outputDir; "--nologo" ] ".")

    Target.create "Publish" (fun _ ->
        // Phase 11.C.3 (2026-05-28) — publish every public-surface SDK
        // fsproj to the toolup-forge GitHub Packages cloud feed.
        //
        // Packs into a per-run `./artifacts/` directory (NOT the shared
        // `../local-nuget-feed/`) so the push never re-uploads stale
        // versions from prior local packs. GitHub Packages versions are
        // immutable; once a `Package/Version` ships it cannot be
        // re-pushed, so a fresh per-run dir is the only safe shape.
        //
        // Authentication: reads `GITHUB_TOKEN` (CI — Actions provides it
        // when `permissions: { packages: write }` is declared on the
        // workflow) or `GITHUB_PACKAGES_TOKEN` (local — a PAT with
        // `write:packages` scope) from the environment. Fails loud if
        // neither is set rather than producing an empty push.
        //
        // Feed source: configurable via `TOOLUP_PUBLISH_SOURCE`; defaults
        // to the toolup-forge org cloud feed.
        //
        // Symbol packages: GitHub Packages NuGet does not accept .snupkg
        // the way nuget.org does, so we push .nupkg only. Symbol files
        // remain in `artifacts/` for local inspection.

        let artifactsDir = Path.getFullName "./artifacts"
        Shell.cleanDir artifactsDir

        let projects =
            !!"src/**/*.fsproj"
            -- "src/ToolUpApp-Server/**/*.fsproj"
            -- "src/ToolUpApp-Client/**/*.fsproj"
            -- "src/Modules/**/*.fsproj"
            -- "src/TestHarness/**/*.fsproj"
            -- "src/ToolUp.Algorithms/**/*.fsproj"
            -- "src/ToolUp.Platform.Tests/**/*.fsproj"
            -- "src/ToolUp.Forms.Tests/**/*.fsproj"
            -- "src/ToolUp.Scheduling.Tests/**/*.fsproj"
            -- "src/ToolUp.RAG.Evaluation/**/*.fsproj"
            -- "src/ToolUp.RAG.Benchmarks/**/*.fsproj"

        for proj in projects do
            Trace.tracefn "Packing %s for publish..." proj

            run dotnet [ "pack"; proj; "-c"; "Release"; "-o"; artifactsDir; "--nologo" ] "."

        let token =
            [ "GITHUB_TOKEN"; "GITHUB_PACKAGES_TOKEN" ]
            |> List.tryPick (fun name ->
                match System.Environment.GetEnvironmentVariable name with
                | null
                | "" -> None
                | v -> Some v)
            |> Option.defaultWith (fun () ->
                failwith
                    "No publish token in environment. Set GITHUB_TOKEN (CI — Actions provides it when permissions: { packages: write } is declared on the workflow) or GITHUB_PACKAGES_TOKEN (local — a PAT with write:packages scope).")

        let source =
            match System.Environment.GetEnvironmentVariable "TOOLUP_PUBLISH_SOURCE" with
            | null
            | "" -> "https://nuget.pkg.github.com/ToolUp-Forge/index.json"
            | v -> v

        Trace.tracefn "Pushing artifacts to %s..." source

        // Push .nupkg only — .snupkg files in the same directory are
        // skipped (GitHub Packages NuGet rejects them).
        let nupkgs =
            System.IO.Directory.GetFiles(artifactsDir, "*.nupkg")
            |> Array.filter (fun p -> not (p.EndsWith(".snupkg")))
            |> Array.sort

        for nupkg in nupkgs do
            Trace.tracefn "Pushing %s..." (System.IO.Path.GetFileName nupkg)

            run
                dotnet
                [
                    "nuget"
                    "push"
                    nupkg
                    "--source"
                    source
                    "--api-key"
                    token
                    "--skip-duplicate"
                ]
                ".")

    Target.create "ThirdPartyNotices" (fun _ ->
        // Walks the MSBuild PackageReference graph via `dotnet list package
        // --include-transitive --format json`, queries NuGet flat-container
        // for each package's licence metadata, and emits THIRD_PARTY_NOTICES.md
        // at repo root. Phase 11.C.1 rewrite — replaces the Phase 11.A
        // paket.lock walker now that CPM owns dependency resolution.
        Trace.tracefn "Running `dotnet list package --include-transitive --format json`..."

        let psi = System.Diagnostics.ProcessStartInfo()
        psi.FileName <- "dotnet"

        for arg in
            [
                "list"
                "ToolUpApplication.sln"
                "package"
                "--include-transitive"
                "--format"
                "json"
            ] do
            psi.ArgumentList.Add(arg)

        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false

        use proc = System.Diagnostics.Process.Start(psi)
        let stdout = proc.StandardOutput.ReadToEnd()
        let stderr = proc.StandardError.ReadToEnd()
        proc.WaitForExit()

        if proc.ExitCode <> 0 then
            failwithf "dotnet list package failed (exit %d): %s" proc.ExitCode stderr

        // Parse the JSON output. Schema:
        // { "projects": [ { "frameworks": [ { "topLevelPackages": [...], "transitivePackages": [...] } ] } ] }
        // Each package entry: { "id": "...", "resolvedVersion": "..." } (transitives use resolvedVersion;
        // top-level may also include requestedVersion).
        let json = System.Text.Json.JsonDocument.Parse(stdout)
        let root = json.RootElement

        let collectPackages () = seq {
            for project in root.GetProperty("projects").EnumerateArray() do
                let mutable hasFrameworks = false

                let mutable frameworksValue = Unchecked.defaultof<System.Text.Json.JsonElement>

                if project.TryGetProperty("frameworks", &frameworksValue) then
                    hasFrameworks <- true

                if hasFrameworks then
                    for framework in frameworksValue.EnumerateArray() do
                        for kind in [ "topLevelPackages"; "transitivePackages" ] do
                            let mutable bucket = Unchecked.defaultof<System.Text.Json.JsonElement>

                            if framework.TryGetProperty(kind, &bucket) then
                                for pkg in bucket.EnumerateArray() do
                                    let id = pkg.GetProperty("id").GetString()

                                    let version = pkg.GetProperty("resolvedVersion").GetString()

                                    id, version
        }

        let packages =
            collectPackages ()
            |> Seq.distinct
            |> Seq.sortBy (fun (id, _) -> id.ToLowerInvariant())
            |> List.ofSeq

        Trace.tracefn "Found %d unique packages. Fetching licence metadata..." packages.Length

        use http = new System.Net.Http.HttpClient()
        http.Timeout <- System.TimeSpan.FromSeconds 30.0

        http.DefaultRequestHeaders.UserAgent.ParseAdd "ToolUp-ThirdPartyNotices/1.0"

        // NuGet flat-container API requires the normalised SemVer-3 form
        // (major.minor.patch). paket.lock strips trailing zero components, so
        // "8.2" must become "8.2.0" and "8" must become "8.0.0". Pre-release
        // and metadata suffixes (e.g. "1.0-alpha", "1.0.0+sha") attach to the
        // padded base.
        let normaliseVersion (version: string) =
            let suffixIdx =
                let dashIdx = version.IndexOf '-'
                let plusIdx = version.IndexOf '+'

                match dashIdx, plusIdx with
                | -1, -1 -> -1
                | -1, p -> p
                | d, -1 -> d
                | d, p -> min d p

            let baseVersion, suffix =
                if suffixIdx < 0 then
                    version, ""
                else
                    version.Substring(0, suffixIdx), version.Substring(suffixIdx)

            let parts = baseVersion.Split '.'

            let padded =
                match parts.Length with
                | 1 -> sprintf "%s.0.0" parts[0]
                | 2 -> sprintf "%s.%s.0" parts[0] parts[1]
                | _ -> baseVersion

            padded + suffix

        let fetchNuspec (pkgId: string) (version: string) =
            let lowerId = pkgId.ToLowerInvariant()
            let normalisedVersion = normaliseVersion version

            let url =
                sprintf "https://api.nuget.org/v3-flatcontainer/%s/%s/%s.nuspec" lowerId normalisedVersion lowerId

            try
                let nuspecXml = http.GetStringAsync(url).GetAwaiter().GetResult()
                let doc = System.Xml.Linq.XDocument.Parse(nuspecXml)
                let ns = doc.Root.Name.Namespace
                let metadata = doc.Root.Element(ns + "metadata")

                if isNull metadata then
                    None
                else
                    let licElem = metadata.Element(ns + "license")
                    let licUrlElem = metadata.Element(ns + "licenseUrl")
                    let projElem = metadata.Element(ns + "projectUrl")

                    let projectUrl = if isNull projElem then "" else projElem.Value.Trim()

                    if not (isNull licElem) then
                        let typeAttr = licElem.Attribute(System.Xml.Linq.XName.Get "type")

                        let licType = if isNull typeAttr then "expression" else typeAttr.Value

                        Some(licElem.Value.Trim(), licType, projectUrl)
                    elif not (isNull licUrlElem) then
                        Some(licUrlElem.Value.Trim(), "url", projectUrl)
                    else
                        Some("(licence not declared)", "missing", projectUrl)
            with ex ->
                Trace.traceErrorfn "  fetch failed for %s %s: %s" pkgId version ex.Message
                Some(sprintf "(fetch error: %s)" ex.Message, "error", "")

        let mutable progress = 0

        let results =
            packages
            |> List.map (fun (pkgId, version) ->
                progress <- progress + 1

                if progress % 10 = 0 then
                    Trace.tracefn "  [%d/%d]" progress packages.Length

                pkgId, version, fetchNuspec pkgId version)

        let sb = System.Text.StringBuilder()
        sb.AppendLine "# Third-Party Notices" |> ignore
        sb.AppendLine() |> ignore

        sb.AppendLine "Regenerated by `dotnet run -- ThirdPartyNotices`. This file lists every"
        |> ignore

        sb.AppendLine "NuGet dependency the ToolUp SDK consumes (direct and transitive) along with"
        |> ignore

        sb.AppendLine "its declared licence as published on nuget.org. The curated headline list"
        |> ignore

        sb.AppendLine "of major direct dependencies lives in [`NOTICE.md`](NOTICE.md)."
        |> ignore

        sb.AppendLine() |> ignore

        sb.AppendLine(sprintf "Last regenerated: %s." (System.DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'")))
        |> ignore

        sb.AppendLine() |> ignore

        sb.AppendLine "Entries showing `(licence not declared)` are upstream packages whose NuGet"
        |> ignore

        sb.AppendLine "metadata omits a licence — check the package's repository or README directly."
        |> ignore

        sb.AppendLine() |> ignore
        sb.AppendLine "## Packages" |> ignore
        sb.AppendLine() |> ignore
        sb.AppendLine "| Package | Version | Licence | Source |" |> ignore
        sb.AppendLine "|---|---|---|---|" |> ignore

        for (pkgId, version, licInfo) in results do
            let licText, licType, projectUrl =
                match licInfo with
                | Some(text, t, url) -> text, t, url
                | None -> "(metadata missing)", "missing", ""

            let licDisplay =
                match licType with
                | "expression" -> sprintf "`%s`" licText
                | "file" -> sprintf "Embedded file: `%s`" licText
                | "url" -> sprintf "[link](%s)" licText
                | _ -> licText

            let sourceLink =
                if System.String.IsNullOrWhiteSpace projectUrl then
                    sprintf "https://www.nuget.org/packages/%s/%s" pkgId version
                else
                    projectUrl

            sb.AppendLine(sprintf "| `%s` | `%s` | %s | [link](%s) |" pkgId version licDisplay sourceLink)
            |> ignore

        sb.AppendLine() |> ignore
        sb.AppendLine(sprintf "Total: %d packages." packages.Length) |> ignore

        System.IO.File.WriteAllText("THIRD_PARTY_NOTICES.md", sb.ToString())
        Trace.tracefn "Wrote THIRD_PARTY_NOTICES.md (%d packages)." packages.Length)

    Target.create "AddHeaders" (fun _ ->
        // Apache-2.0 SPDX header for Client-tier source that the Fable
        // packaging step copies verbatim into consumer apps (Phase 11.A).
        // The repo-root LICENSE does not travel with the copied source
        // tree, so each redistributed .fs needs its own attribution
        // notice — Apache-2.0 §4(c) requires attribution notices to
        // survive redistribution. The authoritative file set is derived
        // from the fsprojs that pack source under `fable/` (the same
        // `PackagePath="fable"` content-include the nupkgs use), never
        // hand-listed, so a newly-added packed-source project is covered
        // automatically and cannot drift.
        //
        //   dotnet run -- AddHeaders           stamp missing headers in place
        //   dotnet run -- AddHeaders --check   report drift, exit 1, mutate nothing
        let headerLines = [
            "// SPDX-License-Identifier: Apache-2.0"
            "// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)"
        ]

        let spdxMarker = List.head headerLines
        let checkOnly = Environment.GetCommandLineArgs() |> Array.contains "--check"

        let packsFableSource (proj: string) =
            (System.IO.File.ReadAllText proj).Contains "PackagePath=\"fable"

        let sourceFiles =
            !!"src/**/*.fsproj"
            |> Seq.filter packsFableSource
            |> Seq.collect (fun proj ->
                let dir = (System.IO.Path.GetDirectoryName proj).Replace('\\', '/')

                !!(sprintf "%s/**/*.fs" dir)
                -- (sprintf "%s/**/obj/**" dir)
                -- (sprintf "%s/**/bin/**" dir))
            |> Seq.distinct
            |> Seq.sort
            |> List.ofSeq

        let hasHeader (path: string) =
            // StreamReader strips a UTF-8 BOM (detectEncodingFromByteOrderMarks);
            // the extra TrimStart is belt-and-braces for an in-line BOM.
            use sr = new System.IO.StreamReader(path)
            let first = sr.ReadLine()
            not (isNull first) && first.TrimStart('﻿') = spdxMarker

        let missing = sourceFiles |> List.filter (hasHeader >> not)

        if checkOnly then
            if List.isEmpty missing then
                Trace.tracefn
                    "AddHeaders --check: all %d packed-source files carry the SPDX header."
                    sourceFiles.Length
            else
                for f in missing do
                    Trace.traceErrorfn "  missing SPDX header: %s" f

                failwithf
                    "AddHeaders --check: %d of %d packed-source files lack the Apache-2.0 SPDX header. Run `dotnet run -- AddHeaders`."
                    missing.Length
                    sourceFiles.Length
        else
            for f in missing do
                // Prepend header bytes only; the original body is left
                // byte-for-byte intact so existing line endings / encoding
                // are preserved and the diff is exactly the inserted lines.
                let bytes = System.IO.File.ReadAllBytes f

                let hasBom =
                    bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy

                let body = if hasBom then bytes[3..] else bytes

                // Match the file's own newline so the header doesn't
                // introduce a mixed-ending file.
                let newline =
                    let lfIdx = System.Array.IndexOf(body, 0x0Auy)

                    if lfIdx > 0 && body[lfIdx - 1] = 0x0Duy then
                        "\r\n"
                    else
                        "\n"

                let headerText = (String.concat newline headerLines) + newline + newline

                let headerBytes = System.Text.Encoding.UTF8.GetBytes headerText

                let finalBytes =
                    Array.concat [ (if hasBom then [| 0xEFuy; 0xBBuy; 0xBFuy |] else [||]); headerBytes; body ]

                System.IO.File.WriteAllBytes(f, finalBytes)
                Trace.tracefn "  stamped %s" f

            Trace.tracefn
                "AddHeaders: stamped %d file(s); %d already compliant."
                missing.Length
                (sourceFiles.Length - missing.Length))

    // Wire dependencies
    let (==>) a b = Fake.Core.TargetOperators.(==>) a b

    "Clean" ==> "KillStaleProcesses" ==> "RestoreClientDependencies" ==> "Bundle"
    |> ignore

    "Clean"
    ==> "KillStaleProcesses"
    ==> "RestoreClientDependencies"
    ==> "Build"
    ==> "Run"
    |> ignore

    "Clean" ==> "KillStaleProcesses" ==> "RestoreClientDependencies" ==> "Docker"
    |> ignore

/// Execute the FAKE pipeline
let execute (argv: string[]) =
    try
        let argList = argv |> Array.toList

        // Parse --module flag and propagate as environment variable
        let rec findModule =
            function
            | "--module" :: name :: _ -> Some name
            | _ :: rest -> findModule rest
            | [] -> None

        match findModule argList with
        | Some m -> Environment.SetEnvironmentVariable("TOOLUP_MODULE", m)
        | None -> ()

        let target =
            match argList with
            | t :: _ when not (t.StartsWith "--") -> t
            | _ -> "Run"

        Target.runOrDefault target

        0
    with e ->
        printfn "%A" e
        1