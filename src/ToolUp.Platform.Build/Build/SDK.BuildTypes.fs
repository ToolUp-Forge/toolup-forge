namespace ToolUp.Platform

/// Documents the standard artefact output paths
type BuildOutput = {
    ServerPublishPath: string
    ClientBundlePath: string
}

module BuildOutput =
    let defaults = {
        ServerPublishPath = "deploy"
        ClientBundlePath = "deploy/public"
    }

/// Documents a single Expecto test pack the `VerifyAll` FAKE target
/// iterates over. The path is the fsproj that hosts an Expecto
/// `Program.fs` (`<OutputType>Exe</OutputType>`). `VerifyAll` builds the
/// solution once and then launches each pack's built dll directly
/// (Phase 731) — never `dotnet test`, which silently no-ops against
/// Expecto console runners. See forge `CLAUDE.md` "Build pipeline" for
/// the canonical operator-side invocation.
type TestPack = {
    /// Display name surfaced in `VerifyAll`'s per-pack summary line.
    Name: string
    /// Path to the Expecto runner fsproj, relative to the repo root.
    Project: string
}

module TestPack =
    let create name project = { Name = name; Project = project }

/// Phase 1096 — the directories under `src/` that the `Pack` and `Publish`
/// targets never pack, declared once. They were spelled three times (the
/// two globs and the SDK manifest reconciler), and a copy that drifts packs
/// or manifests a project the others skip. The app-era names match nothing
/// in an SDK-only tree; they stay because a consumer that vendors the
/// reference app keeps it out of its own pack this way.
module PackScope =

    /// The reference app's server project directory.
    let appServerDir = "ToolUpApp-Server"

    /// The reference app's client project directory.
    let appClientDir = "ToolUpApp-Client"

    /// Directory names under `src/` excluded from `Pack` / `Publish`, matched
    /// as a whole path segment.
    let excludedDirs = [
        appServerDir
        appClientDir
        "Modules"
        "TestHarness"
        "ToolUp.Platform.Tests"
        "ToolUp.Forms.Tests"
        "ToolUp.Scheduling.Tests"
        "ToolUp.RAG.Evaluation"
        "ToolUp.RAG.Benchmarks"
    ]

    /// Whether a project path lies under an excluded directory segment.
    let isExcluded (projectPath: string) =
        let normalised = projectPath.Replace('\\', '/')

        excludedDirs |> List.exists (fun seg -> normalised.Contains(sprintf "/%s/" seg))