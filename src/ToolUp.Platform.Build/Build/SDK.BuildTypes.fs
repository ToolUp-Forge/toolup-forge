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

/// What one child process did: the command line as run, its
/// exit code, what it printed (empty unless the run was redirected) and how
/// long it took. Every runner in a build driver returns this one record, so a
/// gate leg's verdict, output and budget are read off it rather than scraped.
type ProcResult = {
    /// The command line as the process was started, for reports.
    Command: string
    ExitCode: int
    /// Captured standard output; empty when the run inherited the console.
    StdOut: string
    /// Captured standard error; empty when the run inherited the console.
    StdErr: string
    Elapsed: System.TimeSpan
}

/// How a child process is run. `Proc.options` is the default:
/// inherit the console, echo the command line, no extra environment.
type ProcOptions = {
    /// Variables set on the child's environment, in order.
    Environment: (string * string) list
    /// Capture standard output and error into the result instead of
    /// inheriting the console.
    Redirect: bool
    /// Echo the command line before it runs. Off for high-volume readers
    /// (one `git show` per baseline), whose echo would bury the report.
    TraceCommand: bool
    /// Kill the child and raise when it outlives this.
    Timeout: System.TimeSpan option
}

/// The options a driver accepts. A switch takes no value; a
/// valued option takes the next argument. Anything else spelled `--name` is
/// refused by name, so a mistyped flag cannot be silently ignored.
type FlagSpec = {
    Switches: string list
    Valued: string list
}

/// A driver's command line, parsed once.
type Flags = {
    /// The first argument when it is not an option, else `Run` — the rule
    /// `execute` has always applied.
    Target: string
    /// The non-option arguments after the target, in order.
    Positionals: string list
    /// The switches given.
    Switches: Set<string>
    /// The valued options given; the last occurrence wins.
    Values: Map<string, string>
}

/// One project's cost in a solution build, read off MSBuild's
/// `-clp:PerformanceSummary` text. The census build runs with `-graph`, so a
/// project starts only after every project it references has finished: its
/// time is its own work (dominated by `Fsc`), never time spent blocked on a
/// reference. See `docs/migrations/1199-build-census.md`.
type ProjectCost = {
    /// The project file as MSBuild printed it (a full path).
    Project: string
    /// The project's own build time, in seconds, summed over its calls.
    CompileSeconds: float
    /// Seconds MSBuild spent evaluating the project file, summed over its
    /// evaluations; 0 when the summary has no evaluation row for it.
    EvaluationSeconds: float
}

/// One row of `build-census.json`: a solution project's measured cost,
/// its size and its place in the reference graph.
type CensusEntry = {
    /// Repo-relative path of the `.fsproj`, forward-slashed.
    Project: string
    /// The project's own build seconds, as in `ProjectCost`.
    CompileSeconds: float
    /// The project file's evaluation seconds, as in `ProjectCost`.
    EvaluationSeconds: float
    /// Lines in the project's `Compile` items.
    Lines: int
    /// Solution projects that reference this one.
    FanIn: int
    /// Solution projects this one references.
    FanOut: int
}

/// The whole census: one machine's measurement of one tree, dated.
type BuildCensus = {
    /// `git rev-parse HEAD` when the census was measured.
    TreeSha: string
    /// The machine that measured it, as its OS, architecture and processor
    /// count — never its host name, which names a private machine.
    Machine: string
    /// When it was measured, UTC, ISO 8601.
    MeasuredAt: string
    /// Wall-clock seconds of the census build.
    WallSeconds: float
    /// The build command, as run.
    Command: string
    /// Every solution project, most expensive first.
    Projects: CensusEntry list
}