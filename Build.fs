module ToolUpApp.Build

open System.IO
open Fake.Core
open Fake.IO
open Fake.IO.Globbing.Operators
open ToolUp.Platform
open ToolUp.Platform.Build

let config = {
    BuildConfig.defaults with
        Port = 5000
        // The 4 in-tree Expecto runners — `dotnet run -- VerifyAll`
        // iterates these sequentially. AIProviders.Tests is env-gated;
        // arms with no API-key env var report Pending (not Failed), so
        // a fresh checkout is green without per-provider credentials.
        TestPacks = [
            TestPack.create "Platform" "src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj"
            TestPack.create "Forms" "src/ToolUp.Forms.Tests/ToolUp.Forms.Tests.fsproj"
            TestPack.create "Scheduling" "src/ToolUp.Scheduling.Tests/ToolUp.Scheduling.Tests.fsproj"
            TestPack.create "AIProviders" "src/ToolUp.AIProviders.Tests/ToolUp.AIProviders.Tests.fsproj"
            TestPack.create "Stripe" "src/ToolUp.Stripe.Tests/ToolUp.Stripe.Tests.fsproj"
            // Phase 182 — release SBOM gate + CycloneDX emission contract.
            TestPack.create "Build" "src/ToolUp.Platform.Build.Tests/ToolUp.Platform.Build.Tests.fsproj"
            // Phase 195 — compile-time auth/audit analyzer AST-path coverage
            // (offline FCS parse fixtures → Analyzer.analyzeParseTree). The
            // recognition-vs-runtime parity lives in the Platform pack.
            TestPack.create
                "RemotingAnalyzers"
                "src/ToolUp.Remoting.Analyzers.Tests/ToolUp.Remoting.Analyzers.Tests.fsproj"
            // Phase 167 — `toolup` CLI substrate (dispatch + docker-emit
            // token substitution). Pure-BCL host; no env gating.
            TestPack.create "Cli" "src/ToolUp.Cli.Tests/ToolUp.Cli.Tests.fsproj"
            // Phase 518 — ToolUp.Voice: Transcript model + error taxonomy
            // + the Whisper / AzureSpeech pure Wire surfaces. No env gating.
            TestPack.create "Voice" "src/ToolUp.Voice.Tests/ToolUp.Voice.Tests.fsproj"
            // Phase 12e — AICookbooks licensing-boundary: the Community AG
            // Chart prompt builder leaks zero Enterprise feature names + the
            // ~600-token bound + graceful no-op on a missing cookbook.
            TestPack.create "AICookbooks" "src/ToolUp.AICookbooks.Tests/ToolUp.AICookbooks.Tests.fsproj"
            // Phase 11.E.2 — ToolUp.Algorithms: the six-portability-rule
            // contract pack, the registry's duplicate-registration
            // failure path, the dispatcher's typed error surface, and
            // the AI tool edge. No env gating; no vendor dependency.
            TestPack.create "Algorithms" "src/ToolUp.Algorithms.Tests/ToolUp.Algorithms.Tests.fsproj"
            // Phase 11.E.3 — the Math.NET algorithm provider: the shared
            // contract packs bound against a real vendor implementation,
            // plus known-answer numerical fixtures for the hand-written
            // estimators (R-7 quantiles, OLS + categorical encoding,
            // each distribution's parameterisation, centred-vs-trailing
            // alignment). No env gating — the numerics run in-process.
            TestPack.create
                "AlgorithmProviders"
                "src/ToolUp.AlgorithmProviders.Tests/ToolUp.AlgorithmProviders.Tests.fsproj"
        ]
}

[<EntryPoint>]
let main args =
    init args
    registerTargets config

    // Phase 614 — the Fable-tier test gate as ONE invocation.
    //
    // `VerifyAll` (in ToolUp.Platform.Build) covers the twelve .NET
    // Expecto packs. The client tier's harness is a different shape —
    // Fable-transpiled F# run under Node's built-in `node:test` — and
    // was until now a four-step recipe every caller reproduced by hand
    // from `CLAUDE.md`. Two transcriptions of one recipe drift; a single
    // target cannot, so CI and a developer necessarily run the same
    // thing.
    //
    // Usage: `dotnet run --project Build.fsproj -- VerifyFable`
    //
    // The last step deliberately reads the TAP COUNTS, not just the exit
    // code. `node --test` exits 0 when it matched no test file at all,
    // so a harness that silently stopped emitting cases — a moved output
    // path, a Fable compile that wrote elsewhere, an entry point that
    // registered nothing — is indistinguishable from a green run by exit
    // status alone. That is the exact shape of the local `--filter`
    // incident that silently matched 0 Expecto tests. `fableCaseFloor` is
    // a LOWER BOUND, not the exact count: adding a case never needs a
    // companion edit here, and it fires only when the harness has
    // collapsed rather than shrunk.
    let fableCaseFloor = 100

    Target.create "VerifyFable" (fun _ ->
        let testDir = Path.getFullName "src/ToolUp.AI.Client.Tests"

        let onPath name =
            match ProcessUtils.tryFindFileOnPath name with
            | Some path -> path
            | None ->
                failwithf
                    "VerifyFable: `%s` was not found on PATH. The Fable-tier harness needs the .NET SDK and Node.js (>= 20)."
                    name

        let proc exe args =
            CreateProcess.fromRawCommand exe args
            |> CreateProcess.withWorkingDirectory testDir

        let runChecked exe args =
            proc exe args |> CreateProcess.ensureExitCode |> Proc.run |> ignore

        let npm = onPath "npm"
        let node = onPath "node"

        Trace.tracefn "▶ VerifyFable (1/4): dotnet tool restore"
        runChecked "dotnet" [ "tool"; "restore" ]

        // `ci`, not `install` — the lockfile is committed, so this is the
        // reproducible form and it is the same one CI runs.
        Trace.tracefn "▶ VerifyFable (2/4): npm ci"
        runChecked npm [ "ci"; "--no-fund"; "--no-audit" ]

        Trace.tracefn "▶ VerifyFable (3/4): dotnet fable -o output --noCache"
        runChecked "dotnet" [ "fable"; "-o"; "output"; "--noCache" ]

        Trace.tracefn "▶ VerifyFable (4/4): node --test output/Program.js"

        // `--test-reporter=tap` is pinned rather than inherited: node
        // picks `spec` on a TTY and `tap` otherwise, and the summary
        // counts parsed below only exist in the TAP form.
        let result =
            proc node [
                "--import"
                "./register-loader.mjs"
                "--test"
                "--test-reporter=tap"
                "output/Program.js"
            ]
            |> CreateProcess.redirectOutput
            |> Proc.run

        let output = result.Result.Output + result.Result.Error
        printfn "%s" output

        let tapCount (label: string) =
            let prefix = sprintf "# %s " label

            output.Split('\n')
            |> Array.map _.Trim()
            |> Array.tryPick (fun line ->
                if line.StartsWith prefix then
                    match System.Int32.TryParse(line.Substring prefix.Length) with
                    | true, n -> Some n
                    | _ -> None
                else
                    None)

        match tapCount "pass", tapCount "fail" with
        | Some passed, Some failed ->
            Trace.tracefn ""
            Trace.tracefn "VerifyFable summary: %d passed, %d failed (floor %d)." passed failed fableCaseFloor

            if failed > 0 then
                failwithf "VerifyFable: %d node:test case(s) failed." failed

            if passed < fableCaseFloor then
                failwithf
                    "VerifyFable: only %d case(s) ran, below the floor of %d. A run this small means the harness matched almost nothing — check the Fable output path and the entry point before lowering the floor."
                    passed
                    fableCaseFloor
        | _ ->
            failwith
                "VerifyFable: node:test printed no TAP `# pass` / `# fail` summary — the harness did not run. `node --test` exits 0 in that case, which is why the counts are checked and not just the exit status."

        if result.ExitCode <> 0 then
            failwithf "VerifyFable: node exited %d." result.ExitCode)

    // App-specific target: Azure deployment
    Target.create "Deploy-CD" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        dotnet [ "run"; "--project"; "deploy/azure/Deploy.fsproj"; "--"; "cd" ] "."
        |> Proc.run
        |> ignore)

    // Phase 72 — template-pack packaging.
    //
    // The standard Pack target (in ToolUp.Platform.Build) walks
    // `src/**/*.fsproj` and packs each SDK fsproj as a NuGet assembly
    // package. Templates live under `templates/` and need their own
    // packaging shape (`<PackageType>Template</PackageType>` + content-
    // only packing), so they don't fit the standard Pack glob.
    //
    // `PackTemplates` packs each template-pack csproj into
    // ../local-nuget-feed for consumers to `dotnet new install
    // <PackageId>` from the same feed they consume the SDK from.
    //
    // Single-template-per-package convention: each template ships as
    // its own NuGet (e.g. ToolUp.Templates.SAFER ships only
    // `toolup-safer`). Add new template-packs by extending the
    // `templatePackProjects` glob.
    Target.create "PackTemplates" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        let templatePackProjects = !!"templates/**/ToolUp.Templates.*.fsproj"

        for proj in templatePackProjects do
            Trace.tracefn "Packing template-pack %s..." proj

            dotnet [ "pack"; proj; "-c"; "Release"; "-o"; outputDir; "--nologo" ] "."
            |> Proc.run
            |> ignore)

    // Phase 4b dev convenience — wipe the local Platform Admin list
    // so the next `dotnet run` re-bootstraps from
    // `TOOLUP_INITIAL_PLATFORM_ADMIN` or `ServerConfig.AutoBootstrapDevAdmin`.
    // Saves the manual file-delete step when iterating on bootstrap.
    // Does NOT clear the rest of `data/` — KB content, files, teams,
    // etc. are preserved.
    //
    // Usage: `dotnet run -- ResetPlatformAdmins`
    //
    // The reset only applies to the reference deployment's
    // `LocalFileStorage` data path under `src/ToolUpApp-Server/data/`.
    // Cloud-storage deployments (Azure Blob, S3, GCS) are unaffected
    // — operators clear those via the cloud provider's tooling.
    Target.create "ResetPlatformAdmins" (fun _ ->
        let adminListPath =
            Path.Combine(__SOURCE_DIRECTORY__, "src", "ToolUpApp-Server", "data", "_platform", "platform-admins.json")

        if File.Exists adminListPath then
            File.Delete adminListPath
            Trace.tracefn "Removed %s" adminListPath
            Trace.tracefn "Next `dotnet run` will re-bootstrap the Platform Admin list."
        else
            Trace.tracefn "No admin list to remove (already empty / no LocalFileStorage data)."
            Trace.tracefn "Path checked: %s" adminListPath)

    // Phase 167 — pack the `toolup` CLI as a tool-manifest-installable
    // `dotnet tool` into the shared local feed. The standard `Pack`
    // target (in ToolUp.Platform.Build) already walks `src/**/*.fsproj`
    // and picks up ToolUp.Cli too — `PackCli` is the fast single-package
    // path for iterating on the CLI and then `dotnet tool install
    // --add-source ../../local-nuget-feed ToolUp.Cli`.
    //
    // Usage: `dotnet run -- PackCli`
    Target.create "PackCli" (fun _ ->
        let dotnet args dir =
            CreateProcess.fromRawCommand "dotnet" args
            |> CreateProcess.withWorkingDirectory dir
            |> CreateProcess.ensureExitCode

        let outputDir = Path.getFullName "../../local-nuget-feed"
        Directory.ensure outputDir

        dotnet
            [
                "pack"
                "src/ToolUp.Cli/ToolUp.Cli.fsproj"
                "-c"
                "Release"
                "-o"
                outputDir
                "--nologo"
            ]
            "."
        |> Proc.run
        |> ignore)

    execute args