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
        ]
}

[<EntryPoint>]
let main args =
    init args
    registerTargets config

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