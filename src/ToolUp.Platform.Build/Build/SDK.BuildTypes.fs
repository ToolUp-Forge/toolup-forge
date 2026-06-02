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
/// `Program.fs` (`<OutputType>Exe</OutputType>`) — invoked via
/// `dotnet run --project <path>` since `dotnet test` silently no-ops
/// against Expecto console runners. See forge `CLAUDE.md` "Build
/// pipeline" for the canonical operator-side invocation.
type TestPack = {
    /// Display name surfaced in `VerifyAll`'s per-pack summary line.
    Name: string
    /// Path to the Expecto runner fsproj, relative to the repo root.
    Project: string
}

module TestPack =
    let create name project = { Name = name; Project = project }