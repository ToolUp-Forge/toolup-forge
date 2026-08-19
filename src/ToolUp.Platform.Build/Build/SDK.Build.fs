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

// ─── Aggregating gate runner ─────────────────────────────────────────

/// Runs a set of INDEPENDENT gate legs and reports on all of them.
///
/// The problem this exists for is an INFORMATION one, not a strictness
/// one. A gate that stops at the first non-zero exit reports "red" and
/// nothing else: every leg after the failure never ran, so the log
/// cannot tell one broken project from twelve. A failure nobody in the
/// session caused then costs everyone sharing the gate their whole
/// signal rather than one project's worth of it, and the usual response
/// to that — stop running the gate — is worse than the failure was.
///
/// Deliberately NOT a lenience mechanism. `runAll` raises when any leg
/// failed, so the aggregate exit status is exactly what fail-fast would
/// have produced. Only the reporting differs: the gate now says what
/// else it found on the way.
///
/// `internal` so it does not enlarge the published `ToolUp.Platform.Build`
/// surface, and the Public-API approval baseline with it. The root
/// `Build.fs` compiles these files into its own assembly, so its targets
/// reach it regardless.
module internal Aggregate =

    /// One leg of an aggregating gate. `Run` returns the leg's exit code
    /// (0 = pass) and does its own tracing, so each caller keeps the
    /// per-leg log lines it already emitted.
    type Leg = { Name: string; Run: unit -> int }

    let leg name run = { Name = name; Run = run }

    /// Run every leg in order, summarise all of them, then raise if any
    /// failed.
    ///
    /// A leg body that THROWS is recorded as a failure rather than
    /// aborting the run. Without that, a caller that kept an
    /// `ensureExitCode`-shaped process, or that trips over a missing file
    /// before it launches one at all, reintroduces fail-fast through the
    /// back door — and silently, which is the shape this module exists
    /// to remove.
    ///
    /// The summary block's shape is load-bearing rather than decoration:
    /// CI reads it to tell a real pass from a vacuous one (`VerifyAll`
    /// exits 0 having run nothing when its pack list is empty), counting
    /// lines that match `PASS` / `FAIL` under the `<label> summary:`
    /// header. Those tokens must stay unique to the per-leg lines, which
    /// is why the headline below says "failed" and not "FAILED".
    let runAll (label: string) (noun: string) (legs: Leg list) : unit =
        let results = ResizeArray<string * int>()

        try
            for leg in legs do
                let exitCode =
                    try
                        leg.Run()
                    with ex ->
                        Trace.traceError (sprintf "%s: %s raised — %s" label leg.Name ex.Message)
                        1

                results.Add(leg.Name, exitCode)
        finally
            // `finally` so a genuinely fatal abort still reports what had
            // run by then; the catch above is what keeps the ordinary
            // failing case complete.
            Trace.tracefn ""
            Trace.tracefn "%s summary:" label

            for name, exitCode in results do
                let status =
                    if exitCode = 0 then
                        "PASS"
                    else
                        sprintf "FAIL (exit %d)" exitCode

                Trace.tracefn "  %s — %s" status name

        let failures = results |> Seq.filter (fun (_, c) -> c <> 0) |> Seq.toList

        if List.isEmpty failures then
            Trace.tracefn "%s: all %d %s(s) passed." label results.Count noun
        else
            let named =
                failures
                |> List.map (fun (n, c) -> sprintf "%s (exit %d)" n c)
                |> String.concat "; "

            let headline =
                sprintf "%s: %d of %d %s(s) failed — %s" label failures.Length results.Count noun named

            // Printed as well as raised, so it lands with the summary
            // block where a reader is already looking rather than only
            // inside FAKE's error block.
            Trace.tracefn "%s" headline
            failwith headline

// ─── Build configuration ───────────────────────────────────────────

open ToolUp.Platform

type BuildConfig = {
    ServerProject: string
    ClientProject: string
    Output: BuildOutput
    Port: int
    /// Expecto test packs run sequentially by the `VerifyAll` target.
    /// Default is empty — `VerifyAll` becomes a no-op for consumers
    /// that haven't opted in, preserving zero behaviour change for
    /// downstream `Build.fs` files that construct `BuildConfig` via
    /// `{ BuildConfig.defaults with … }`. Forge's own root `Build.fs`
    /// populates this list with the 4 in-tree Expecto runners.
    TestPacks: TestPack list
}

module BuildConfig =
    let defaults = {
        ServerProject = "src/ToolUpApp-Server/ToolupApp-Server.fsproj"
        ClientProject = "src/ToolUpApp-Client/ToolupApp-Client.fsproj"
        Output = BuildOutput.defaults
        Port = 5000
        TestPacks = []
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

    Target.create "VerifyAll" (fun _ ->
        // Canonical "run every Expecto test pack" aggregator. Each pack
        // is a console runner (`<OutputType>Exe</OutputType>` per the
        // forge convention) invoked via `dotnet run --project <path>`;
        // `dotnet test` silently no-ops against them, hence this
        // target exists to give operators + CI a single call shape.
        //
        // Packs run sequentially so the per-pack output isn't
        // interleaved (the parallel-pretty-printer is reserved for
        // long-lived watch shapes). Each pack's stdout/stderr stream
        // straight through; the cumulative summary lands at the end.
        //
        // Failure semantics: EVERY pack runs, whatever the ones before
        // it did, and the target fails at the end naming each that did
        // not. See `Aggregate` above for why that is worth the minutes a
        // known-red pack costs — briefly, "the gate is red" says nothing
        // about the eleven packs that never ran.
        // Diagnostic pass-through: `TOOLUP_TEST_ARGS` (whitespace-split)
        // is appended to every pack invocation after `--`. Exists so CI
        // can run the suite with e.g. `--debug` (Expecto names each test
        // as it starts) when hunting a failure that only reproduces on a
        // runner — a killed run then names its last-started test in the
        // log. Empty/unset ⇒ byte-for-byte the previous invocation.
        let extraTestArgs =
            match System.Environment.GetEnvironmentVariable "TOOLUP_TEST_ARGS" with
            | null
            | "" -> []
            | v -> v.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

        match config.TestPacks with
        | [] ->
            Trace.tracefn
                "VerifyAll: BuildConfig.TestPacks is empty — nothing to run. Populate `TestPacks` in your `BuildConfig` to opt in."
        | packs ->
            packs
            |> List.map (fun pack ->
                Aggregate.leg pack.Name (fun () ->
                    Trace.tracefn "▶ VerifyAll: %s (%s)" pack.Name pack.Project

                    // Deliberately NOT the file-top `dotnet` shim: that
                    // decorates with `ensureExitCode`, which throws on a
                    // non-zero exit and would take every later pack with
                    // it. The invocation is otherwise identical.
                    let args =
                        match extraTestArgs with
                        | [] -> [ "run"; "--project"; pack.Project ]
                        | extra -> [ "run"; "--project"; pack.Project; "--" ] @ extra

                    let result =
                        CreateProcess.fromRawCommand "dotnet" args
                        |> CreateProcess.withWorkingDirectory "."
                        |> Proc.run

                    result.ExitCode))
            |> Aggregate.runAll "VerifyAll" "pack")

    Target.create "Pack" (fun _ ->
        // Pack each public-surface SDK fsproj into the local NuGet feed
        // at ../../local-nuget-feed (the single workspace-root shared feed — see
        // this repo's nuget.config header for the local-feed rationale).
        //
        // Per-fsproj iteration (rather than solution-level pack)
        // sidesteps the bootstrap chicken-and-egg: a consumer app's
        // fsprojs <PackageReference> the SDK packages from this very
        // feed, so a solution-level pack spanning both would try to
        // build the consumer before the packages exist. Each SDK fsproj's
        // transitive ProjectReference graph stays inside the SDK
        // boundary, so per-fsproj pack builds cleanly.
        let outputDir = Path.getFullName "../../local-nuget-feed"
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

        // Pack every project, then fail at the end — the `VerifyAll`
        // shape above, for the same reason. A packaging defect in one
        // project (a missing packed README, a bad PackagePath) is
        // independent of every other project, so aborting on the first
        // one hides the rest and turns a single fix-and-rerun cycle
        // into one round trip per defect. The target still fails, and
        // the summary names every project that did not pack.
        let results = ResizeArray<string * int>()

        // Per-project process WITHOUT the file-top `dotnet` shim's
        // `ensureExitCode` decorator, so a non-zero exit is captured
        // rather than thrown.
        let packProject proj =
            CreateProcess.fromRawCommand "dotnet" [ "pack"; proj; "-c"; "Release"; "-o"; outputDir; "--nologo" ]
            |> CreateProcess.withWorkingDirectory "."
            |> Proc.run

        try
            for proj in projects do
                Trace.tracefn "Packing %s..." proj
                let result = packProject proj
                results.Add(proj, result.ExitCode)
        finally
            let failed = results |> Seq.filter (fun (_, c) -> c <> 0) |> Seq.toList

            Trace.tracefn ""
            Trace.tracefn "Pack summary: %d packed, %d failed." (results.Count - failed.Length) failed.Length

            for proj, exitCode in failed do
                Trace.tracefn "  FAIL (exit %d) — %s" exitCode proj

        let failed = results |> Seq.filter (fun (_, c) -> c <> 0) |> Seq.toList

        if not failed.IsEmpty then
            failed
            |> List.map (fun (p, c) -> sprintf "%s (exit %d)" p c)
            |> String.concat "; "
            |> failwithf "Pack: %d project(s) failed to pack — %s" failed.Length)

    Target.create "Publish" (fun _ ->
        // Phase 11.C.3 (2026-05-28) / Phase 346 (2026-08-19) — publish
        // every public-surface SDK fsproj to nuget.org (the default
        // source since the 2026-08-19 cutover; the old ToolUp-Forge
        // GitHub Packages feed is frozen and no longer pushed to).
        //
        // Packs into a per-run `./artifacts/` directory (NOT the shared
        // `../local-nuget-feed/`) so the push never re-uploads stale
        // versions from prior local packs. Published feed versions are
        // immutable; once a `Package/Version` ships it cannot be
        // re-pushed, so a fresh per-run dir is the only safe shape.
        //
        // Feed source: configurable via `TOOLUP_PUBLISH_SOURCE`; defaults
        // to nuget.org. Key resolution is source-aware (see the token
        // block below): nuget.org reads `NUGET_API_KEY` (in CI, the
        // temp key minted by the trusted-publishing login step; locally,
        // a classic push-scoped api key); a GH Packages URL — reachable
        // only by explicit opt-in now — still reads `GITHUB_TOKEN` /
        // `GITHUB_PACKAGES_TOKEN`. Fails loud if the matching variable
        // is unset rather than producing an empty push.
        //
        // Symbol packages: the push loop filters to .nupkg; for
        // nuget.org, `dotnet nuget push` auto-detects the matching
        // .snupkg beside each .nupkg and pushes it to the symbol server —
        // no extra handling needed here. Symbol files remain in
        // `artifacts/` for local inspection either way.

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

        // Phase 182 — gated SBOM (+ optional provenance sidecar) emission.
        // Off unless TOOLUP_EMIT_SBOM is set (GP 11/13); CI's
        // publish-nuget.yml sets it on the release path. Emitted alongside
        // the nupkgs in `artifacts/` — the push loop below filters to
        // `*.nupkg`, so the `.cdx.json` SBOMs / `.sig` sidecars are never
        // pushed to the feed (they travel as separate CI artefacts +
        // GitHub build-provenance attestation). The signer hook is `None`
        // here — CI uses GitHub's native attestation; a deployment wiring
        // an IArtefactSigner passes it from its own Build.fs (see Sbom).
        let producedNupkgs =
            System.IO.Directory.GetFiles(artifactsDir, "*.nupkg")
            |> Array.filter (fun p -> not (p.EndsWith ".snupkg"))
            |> Array.sort
            |> Array.toList

        let sbomArtefacts =
            Sbom.emit
                System.Environment.GetEnvironmentVariable
                (fun () -> System.DateTimeOffset.UtcNow)
                (fun () -> "urn:uuid:" + System.Guid.NewGuid().ToString())
                None
                (Trace.tracefn "%s")
                producedNupkgs

        if not (List.isEmpty sbomArtefacts) then
            Trace.tracefn "SBOM: emitted %d artefact(s) into %s" (List.length sbomArtefacts) artifactsDir

        let source =
            match System.Environment.GetEnvironmentVariable "TOOLUP_PUBLISH_SOURCE" with
            | null
            | "" -> "https://api.nuget.org/v3/index.json"
            | v -> v

        // Key resolution is source-aware (Phase 346). The default
        // nuget.org source reads NUGET_API_KEY (CI: the trusted-
        // publishing temp key; local: an api.nuget.org key scoped to
        // push on the ToolUp.* glob). A non-nuget.org source — the
        // frozen GH Packages feed, reachable only by explicit opt-in —
        // reads GITHUB_TOKEN / GITHUB_PACKAGES_TOKEN. Fails loud when
        // the matching variable is unset rather than producing an
        // empty push.
        let isNuGetOrg =
            source.Contains("api.nuget.org", System.StringComparison.OrdinalIgnoreCase)

        let tokenNames =
            if isNuGetOrg then
                [ "NUGET_API_KEY" ]
            else
                [ "GITHUB_TOKEN"; "GITHUB_PACKAGES_TOKEN" ]

        let token =
            tokenNames
            |> List.tryPick (fun name ->
                match System.Environment.GetEnvironmentVariable name with
                | null
                | "" -> None
                | v -> Some v)
            |> Option.defaultWith (fun () ->
                if isNuGetOrg then
                    failwith
                        "No publish key in environment. TOOLUP_PUBLISH_SOURCE targets nuget.org — set NUGET_API_KEY (an api.nuget.org API key with push scope on the ToolUp.* glob)."
                else
                    failwith
                        "No publish token in environment. Set GITHUB_TOKEN (CI — Actions provides it when permissions: { packages: write } is declared on the workflow) or GITHUB_PACKAGES_TOKEN (local — a PAT with write:packages scope).")

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
        // Auto-discover the solution file at repo root. The Phase 11.A
        // shape hardcoded "ToolUpApplication.sln" which only worked for
        // the original consumer app — every other consumer (forge itself
        // and any downstream app) names its sln differently
        // and the target failed. One `.sln` at the working directory is
        // the contract; multiple is ambiguous, none is a usage error.
        let slnFile =
            match !!"*.sln" |> List.ofSeq with
            | [ single ] -> System.IO.Path.GetFileName single
            | [] -> failwithf "ThirdPartyNotices: no .sln found in %s" (System.IO.Directory.GetCurrentDirectory())
            | many ->
                failwithf
                    "ThirdPartyNotices: multiple .sln files found in %s (%s) — single-sln assumption violated"
                    (System.IO.Directory.GetCurrentDirectory())
                    (many |> List.map System.IO.Path.GetFileName |> String.concat ", ")

        Trace.tracefn "Running `dotnet list %s package --include-transitive --format json`..." slnFile

        let psi = System.Diagnostics.ProcessStartInfo()
        psi.FileName <- "dotnet"

        for arg in [ "list"; slnFile; "package"; "--include-transitive"; "--format"; "json" ] do
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
        // SPDX header for Client-tier source that the Fable packaging step
        // copies verbatim into consumer apps (Phase 11.A). The repo-root
        // LICENSE does not travel with the copied source tree, so each
        // redistributed .fs needs its own attribution notice — Apache-2.0
        // §4(c) and MIT each require attribution notices to survive
        // redistribution. The authoritative file set is derived from the
        // fsprojs that pack source under `fable/` (the same
        // `PackagePath="fable"` content-include the nupkgs use), never
        // hand-listed, so a newly-added packed-source project is covered
        // automatically and cannot drift.
        //
        // Per-directory license map. Default = forge-native Apache-2.0.
        // Forked-in subtrees get their upstream license: stamping
        // Apache-2.0 on an MIT-origin file would silently relicense it.
        // See NOTICE.md for the upstream attribution narrative.
        //
        //   Client/Elmish/                    Fable.Elmish fork (Apache-2.0)
        //   Client/Remoting/                  Fable.Remoting fork (MIT)
        //   Shared/Remoting/MsgPack/          Fable.Remoting MsgPack fork (MIT)
        //
        //   dotnet run -- AddHeaders           stamp missing headers in place
        //   dotnet run -- AddHeaders --check   report drift, exit 1, mutate nothing
        let forgeNativeHeader = [
            "// SPDX-License-Identifier: Apache-2.0"
            "// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)"
        ]

        let elmishForkHeader = [
            "// SPDX-License-Identifier: Apache-2.0"
            "// Copyright (c) Eugene Tolmachev and Fable.Elmish contributors"
            "// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)"
        ]

        let remotingForkHeader = [
            "// SPDX-License-Identifier: MIT"
            "// Copyright (c) Zaid Ajaj and Fable.Remoting contributors"
            "// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)"
        ]

        // Forward-slash path prefixes; first match wins. Project-relative
        // (callers normalize backslashes before matching).
        let licenseMap = [
            "Client/Elmish/", elmishForkHeader
            "Client/Remoting/", remotingForkHeader
            "Shared/Remoting/MsgPack/", remotingForkHeader
        ]

        let chooseHeader (path: string) =
            let normalized = path.Replace('\\', '/')

            licenseMap
            |> List.tryFind (fun (prefix, _) -> normalized.Contains(prefix))
            |> Option.map snd
            |> Option.defaultValue forgeNativeHeader

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
            // Marker is the SPDX line expected for this file's path — a
            // mismatched-SPDX file (e.g. Apache-2.0 stamp on an MIT-origin
            // file from a pre-license-map run) is reported as missing.
            let expectedMarker = chooseHeader path |> List.head
            use sr = new System.IO.StreamReader(path)
            let first = sr.ReadLine()
            not (isNull first) && first.TrimStart('﻿') = expectedMarker

        let missing = sourceFiles |> List.filter (hasHeader >> not)

        if checkOnly then
            if List.isEmpty missing then
                Trace.tracefn
                    "AddHeaders --check: all %d packed-source files carry the expected SPDX header."
                    sourceFiles.Length
            else
                for f in missing do
                    let expected = chooseHeader f |> List.head
                    Trace.traceErrorfn "  missing/wrong SPDX header (expected '%s'): %s" expected f

                failwithf
                    "AddHeaders --check: %d of %d packed-source files lack the expected SPDX header. Run `dotnet run -- AddHeaders`."
                    missing.Length
                    sourceFiles.Length
        else
            for f in missing do
                // Prepend header bytes only; the original body is left
                // byte-for-byte intact so existing line endings / encoding
                // are preserved and the diff is exactly the inserted lines.
                let headerLines = chooseHeader f
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