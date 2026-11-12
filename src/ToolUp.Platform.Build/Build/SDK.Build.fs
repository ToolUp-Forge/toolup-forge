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
            |> Array.Parallel.map Fake.Core.Proc.run

    /// Inherit the console, echo the command line, add nothing to the
    /// environment, never time out.
    let options: ProcOptions = {
        Environment = []
        Redirect = false
        TraceCommand = true
        Timeout = None
    }

    /// The FAKE process `execWith` starts, before redirection — for callers
    /// that compose FAKE's process model directly (`Parallel.run`).
    let create (o: ProcOptions) (exe: string) (args: string list) (dir: string) : CreateProcess<ProcessResult<unit>> =
        let c =
            o.Environment
            |> List.fold
                (fun c (name, value) -> CreateProcess.setEnvironmentVariable name value c)
                (CreateProcess.fromRawCommand exe args |> CreateProcess.withWorkingDirectory dir)

        let c =
            if o.TraceCommand then
                c
            else
                CreateProcess.disableTraceCommand c

        o.Timeout |> Option.fold (fun c limit -> CreateProcess.withTimeout limit c) c

    /// Run `exe args` in `dir` under `o`. Never raises for a non-zero exit:
    /// the code is in the result, and `ensure` turns it into a failure.
    let execWith (o: ProcOptions) (exe: string) (args: string list) (dir: string) : ProcResult =
        let c = create o exe args dir
        let clock = Diagnostics.Stopwatch.StartNew()

        let code, out, err =
            if o.Redirect then
                let r = c |> CreateProcess.redirectOutput |> Fake.Core.Proc.run
                r.ExitCode, r.Result.Output, r.Result.Error
            else
                (Fake.Core.Proc.run c).ExitCode, "", ""

        {
            Command = c.CommandLine
            ExitCode = code
            StdOut = out
            StdErr = err
            Elapsed = clock.Elapsed
        }

    /// Run `exe args` in `dir`, inheriting the console.
    let exec (exe: string) (args: string list) (dir: string) : ProcResult = execWith options exe args dir

    /// Run `exe args` in `dir`, capturing standard output and error.
    let redirected (exe: string) (args: string list) (dir: string) : ProcResult =
        execWith { options with Redirect = true } exe args dir

    /// The result unchanged when it exited 0; otherwise raise, naming the
    /// exit code and the command line.
    let ensure (result: ProcResult) : ProcResult =
        if result.ExitCode <> 0 then
            failwithf "Process exit code '%d' <> 0. Command Line: %s" result.ExitCode result.Command

        result

    /// The full path of `name` on PATH; raises with `missing name` when it
    /// is not there.
    let onPath (missing: string -> string) (name: string) : string =
        match ProcessUtils.tryFindFileOnPath name with
        | Some path -> path
        | None -> failwith (missing name)

/// `git`, run the one way a build driver reads it: captured and not echoed.
module Git =

    let private quiet = {
        Proc.options with
            Redirect = true
            TraceCommand = false
    }

    /// `git args` in `root`. The exit code is the caller's to read.
    let run (root: string) (args: string list) : ProcResult = Proc.execWith quiet "git" args root

    /// `git args` in `root`, killed and raising when it outlives `limit`.
    let within (limit: TimeSpan) (root: string) (args: string list) : ProcResult =
        Proc.execWith { quiet with Timeout = Some limit } "git" args root

    /// The standard output of `git args` in `root`; raises on a non-zero exit.
    let read (root: string) (args: string list) : string = (run root args |> Proc.ensure).StdOut

/// Splitting a process's output into lines.
module Lines =

    /// The non-blank lines of `text`, each trimmed, in order. CRLF and LF
    /// both end a line.
    let ofOutput (text: string) : string list =
        text.Replace("\r\n", "\n").Split('\n')
        |> Array.map _.Trim()
        |> Array.filter (String.IsNullOrWhiteSpace >> not)
        |> List.ofArray

/// Path spelling shared by the driver's reports.
[<RequireQualifiedAccess>]
module Paths =

    /// `path` with every backslash turned into a forward slash.
    let toSlash (path: string) : string = path.Replace('\\', '/')

/// The driver's command line, parsed once. `execute` and every target read
/// the same parse, so no target re-reads the process arguments for itself.
module Flags =

    /// The target `argv` requests: the first argument when it is not an
    /// option, else `Run`.
    let targetOf (argv: string seq) : string =
        match List.ofSeq argv with
        | t :: _ when not (t.StartsWith("--", StringComparison.Ordinal)) -> t
        | _ -> "Run"

    /// The options the SDK's own targets read: `AddHeaders` / `Codemod`
    /// take `--check`, `Codemod` takes `--phase <n>`, `execute` takes
    /// `--module <name>`.
    let sdk: FlagSpec = {
        Switches = [ "--check" ]
        Valued = [ "--module"; "--phase" ]
    }

    /// Two specs accepted together.
    let union (a: FlagSpec) (b: FlagSpec) : FlagSpec = {
        Switches = List.distinct (a.Switches @ b.Switches)
        Valued = List.distinct (a.Valued @ b.Valued)
    }

    let private walk (strict: bool) (spec: FlagSpec) (argv: string list) : Result<Flags, string> =
        let target = targetOf argv

        let valued (a: string) = List.contains a spec.Valued

        let known (a: string) =
            valued a || List.contains a spec.Switches

        let accepted () =
            spec.Switches @ (spec.Valued |> List.map (sprintf "%s <value>"))
            |> List.sort
            |> String.concat ", "

        let rec go args switches values positionals =
            match args with
            | [] ->
                Ok {
                    Target = target
                    Positionals = List.rev positionals
                    Switches = switches
                    Values = values
                }
            | (a: string) :: tail when a.StartsWith("--", StringComparison.Ordinal) ->
                match tail with
                | v :: rest when valued a -> go rest switches (Map.add a v values) positionals
                | [] when strict && valued a -> Error(sprintf "the option `%s` needs a value." a)
                | _ when strict && not (known a) ->
                    Error(sprintf "unknown option `%s`. The accepted options are: %s." a (accepted ()))
                | _ -> go tail (Set.add a switches) values positionals
            | a :: tail -> go tail switches values (a :: positionals)

        let rest =
            if List.tryHead argv = Some target then
                List.tail argv
            else
                argv

        go rest Set.empty Map.empty []

    /// Parse `argv` against `spec`. An option the spec does not declare is
    /// refused by name, as is a valued option with no value.
    let parse (spec: FlagSpec) (argv: string seq) : Result<Flags, string> = walk true spec (List.ofSeq argv)

    /// Parse `argv`, reading an undeclared `--name` (and a valued option
    /// with no value) as a switch. Never refuses. For the
    /// SDK's own targets inside a consumer's driver, whose other options the
    /// SDK cannot know.
    let lenient (spec: FlagSpec) (argv: string seq) : Flags =
        match walk false spec (List.ofSeq argv) with
        | Ok flags -> flags
        | Error why -> invalidOp why

    /// Whether the switch `name` was given.
    let has (name: string) (flags: Flags) : bool = flags.Switches.Contains name

    /// The value given for `name`, if any.
    let value (name: string) (flags: Flags) : string option = flags.Values.TryFind name

/// Scratch directories a gate builds outside the repository.
module Scratch =

    /// `<system temp>/<name>`: short, so Windows MAX_PATH stays away from
    /// fsc, and outside every repository, so no ancestor MSBuild or NuGet
    /// file leaks into what is built there.
    let root (name: string) : string =
        IO.Path.Combine(IO.Path.GetTempPath(), name)

/// The publication-boundary matching rule: one denylist token per line,
/// matched case-insensitively, word-bounded at an alphanumeric end, any run
/// of whitespace in a multi-word token matching any run, `re:` for a raw
/// regex. The token list itself is private and read at run time; only the
/// public canary is written here.
module Neutrality =

    // OSS-BOUNDARY-EXEMPT: the public canary itself, which the boundary check always enforces.
    /// The public-safe token always enforced, so a scan with no private list
    /// still proves it ran.
    [<Literal>]
    let Canary = "PRIVATE-VOCAB-CANARY"

    /// The untracked token file at a repository root.
    let tokenFileName = "neutrality-tokens.local.txt"

    /// The tokens in `lines`: trimmed, blank lines and `#` comments dropped.
    let parseTokens (lines: string seq) : string list =
        lines
        |> Seq.map _.Trim()
        |> Seq.filter (fun l -> l <> "" && not (l.StartsWith "#"))
        |> Seq.toList

    /// Whether the environment variable `variable` (a development knob the
    /// caller names, not a deployment setting) asks for an absent token
    /// source to fail rather than skip: `1` or `true`, in any case.
    let isRequired (variable: string) : bool =
        match Environment.GetEnvironmentVariable variable with
        | null -> false
        | v -> v.Trim() = "1" || v.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)

    /// The root of the primary checkout when `root` is a LINKED worktree:
    /// the parent of `git rev-parse --git-common-dir`, which every worktree
    /// of a clone shares. `None` for the primary checkout itself, outside
    /// git, for a bare layout, or when git cannot be run. Git answers both
    /// directories, so a short and a long spelling of one path never read
    /// as two checkouts.
    let primaryRoot (root: string) : string option =
        try
            let r =
                Git.within (TimeSpan.FromSeconds 15.) root [
                    "rev-parse"
                    "--path-format=absolute"
                    "--git-common-dir"
                    "--absolute-git-dir"
                ]

            let full (dir: string) =
                IO.Path.TrimEndingDirectorySeparator(IO.Path.GetFullPath(dir.Trim()))

            match r.ExitCode, r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries) with
            | 0, [| common; own |] ->
                let common = full common

                if
                    IO.Path.GetFileName common <> ".git"
                    || String.Equals(common, full own, StringComparison.OrdinalIgnoreCase)
                then
                    None
                else
                    Option.ofObj (IO.Path.GetDirectoryName common)
            | _ -> None
        with _ ->
            None

    /// The token files looked for, in order: `root`, then the primary
    /// checkout's root when `root` is a linked worktree.
    let tokenFiles (root: string) : string list =
        IO.Path.Combine(root, tokenFileName)
        :: (primaryRoot root
            |> Option.map (fun primary -> IO.Path.Combine(primary, tokenFileName))
            |> Option.toList)

    /// Every place the list is looked for, in order, as a reader names them:
    /// the token files, then the environment variable `variable`.
    let searched (root: string) (variable: string) : string list =
        tokenFiles root
        @ [ sprintf "the %s environment variable (semicolon-separated)" variable ]

    /// The private token list for the repository at `root`: the first token
    /// file `tokenFiles` names that exists, else the semicolon-separated
    /// environment variable `variable` (a development knob the caller names,
    /// not a deployment setting); `None` when none supplies a token.
    let externalTokens (root: string) (variable: string) : string list option =
        let tokens =
            match tokenFiles root |> List.tryFind IO.File.Exists with
            | Some file -> parseTokens (IO.File.ReadAllLines file)
            | None ->
                match Environment.GetEnvironmentVariable variable with
                | null
                | "" -> []
                | value -> parseTokens (value.Split ';')

        match tokens with
        | [] -> None
        | some -> Some some

    /// The regex one denylist entry compiles to.
    let toRegex (token: string) : Text.RegularExpressions.Regex =
        let options =
            Text.RegularExpressions.RegexOptions.IgnoreCase
            ||| Text.RegularExpressions.RegexOptions.CultureInvariant

        if token.StartsWith("re:", StringComparison.OrdinalIgnoreCase) then
            Text.RegularExpressions.Regex(token.Substring 3, options)
        else
            let isWord (c: char) = Char.IsLetterOrDigit c || c = '_'

            let body =
                token.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.map Text.RegularExpressions.Regex.Escape
                |> String.concat @"\s+"

            let edge (c: char option) =
                if c |> Option.exists isWord then @"\b" else ""

            Text.RegularExpressions.Regex(edge (Seq.tryHead token) + body + edge (Seq.tryLast token), options)

    /// The canary and the external tokens, each beside its regex.
    let rules (external: string list option) : (string * Text.RegularExpressions.Regex) list =
        Canary :: (external |> Option.defaultValue [])
        |> List.map (fun t -> t, toRegex t)

// The SDK targets' own process shapes, over `Proc`.
let private run exe args dir =
    Proc.exec exe args dir |> Proc.ensure |> ignore

let private npmMissing name =
    $"{name} was not found in path. Please install it and make sure it's available from your path."

let private npm args dir =
    run (Proc.onPath npmMissing "npm") args dir

let private dotnetProcess args dir =
    Proc.create Proc.options "dotnet" args dir |> CreateProcess.ensureExitCode

let private runParallel processes =
    processes |> Proc.Parallel.run |> ignore

// ─── Phase 1096 — every test project runs or is excluded by name ────

/// Reconciles the repository's test projects against the packs `VerifyAll`
/// runs.
///
/// A test project nothing runs cannot go red: it builds on every push, so it
/// reads as covered, and its cases are dead weight that look like a gate.
/// Four such packs (about 84 cases) sat in forge's solution for months for
/// exactly that reason — nothing compared the pack list with the solution.
/// This is the `SampleGate.reconcile` shape lifted one level: every
/// test project (`*.Tests.fsproj`, or a `.Tests.` segment in the file name) is
/// a declared `TestPacks` row or a named exclusion with its reason, and a
/// declaration naming nothing is stale.
///
/// The candidate set is the solution's projects UNION the `*.Tests.fsproj`
/// files on disk under `src/`, `tests/` and `tools/`, because a project left
/// out of the solution is the more dormant of the two: it is not even built.
/// Names projects by path, never by what they test (GP 9).
module Gate =

    /// A test project the gate deliberately does not run as a pack, with the
    /// reason — the runner that does run it, or the input it needs that a
    /// default gate cannot supply. Recorded as data where a reader of a green
    /// run will look, not as a comment beside the pack list.
    type Exclusion = {
        /// Repo-relative path to the excluded `.fsproj`.
        Project: string
        Reason: string
    }

    /// Declare an exclusion: the repo-relative project and why it is not a pack.
    let exclude project reason = { Project = project; Reason = reason }

    /// The directories under the repository root scanned for `*.Tests.fsproj`.
    let scannedDirs = [ "src"; "tests"; "tools" ]

    let private normalise (path: string) =
        path.Replace('\\', '/').TrimStart('.', '/')

    let private key (path: string) = (normalise path).ToLowerInvariant()

    /// A test project's file name ends `.Tests.fsproj` or carries a `.Tests.`
    /// segment (`ToolUp.Platform.Tests.Infra.fsproj`, one pack of a family
    /// partitioned out of `ToolUp.Platform.Tests`).
    let private isTestProject (path: string) =
        let file = IO.Path.GetFileName(normalise path)

        file.EndsWith(".Tests.fsproj", StringComparison.OrdinalIgnoreCase)
        || (file.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            && file.Contains(".Tests.", StringComparison.OrdinalIgnoreCase))

    /// Every `.fsproj` a `.sln` or `.slnx` text names, forward-slashed.
    let solutionProjects (solutionText: string) : string list =
        let sln =
            Text.RegularExpressions.Regex.Matches(
                solutionText,
                @"Project\(""[^""]*""\)\s*=\s*""[^""]*""\s*,\s*""([^""]+\.fsproj)"""
            )

        let slnx =
            Text.RegularExpressions.Regex.Matches(solutionText, @"<Project\s+Path=""([^""]+\.fsproj)""")

        Seq.append sln slnx
        |> Seq.map (fun m -> normalise m.Groups[1].Value)
        |> Seq.distinctBy key
        |> List.ofSeq

    /// The reconciliation's findings, empty when clean. Pure: `solution` is the
    /// solution's projects (`None` when the repository has no single solution,
    /// which skips the in-solution check), `onDisk` the `*.Tests.fsproj` files
    /// found under `scannedDirs`.
    let findings
        (solution: string list option)
        (onDisk: string list)
        (packs: ToolUp.Platform.TestPack list)
        (excluded: Exclusion list)
        : string list =
        let packKeys = packs |> List.map (fun p -> key p.Project) |> Set.ofList
        let excludedKeys = excluded |> List.map (fun e -> key e.Project) |> Set.ofList
        let diskKeys = onDisk |> List.map key |> Set.ofList
        let solutionKeys = solution |> Option.defaultValue [] |> List.map key |> Set.ofList

        let candidates =
            (solution |> Option.defaultValue []) @ onDisk
            |> List.filter isTestProject
            |> List.distinctBy key
            |> List.sortBy key

        let unrun = [
            for project in candidates do
                let k = key project

                if not (packKeys.Contains k || excludedKeys.Contains k) then
                    let where =
                        if solutionKeys.Contains k then
                            "in the solution"
                        else
                            "on disk, not in the solution"

                    yield
                        sprintf
                            "`%s` (%s) is neither a TestPacks row nor a named exclusion: VerifyAll never runs it, so its cases cannot go red. Add a `TestPack.create` row, or a `Gate.exclude` naming the runner that does run it or the input it needs."
                            (normalise project)
                            where
        ]

        let notBuilt = [
            match solution with
            | None -> ()
            | Some _ ->
                for pack in packs do
                    if not (solutionKeys.Contains(key pack.Project)) then
                        yield
                            sprintf
                                "TestPacks row `%s` names `%s`, which is not in the solution: VerifyAll builds the solution once and launches each pack's built dll, so this pack is never built."
                                pack.Name
                                (normalise pack.Project)
        ]

        let stale = [
            for e in excluded do
                let k = key e.Project

                if not (diskKeys.Contains k || solutionKeys.Contains k) then
                    yield
                        sprintf
                            "exclusion `%s` names a project that does not exist: a stale exclusion hides nothing and reads as coverage. Remove it."
                            (normalise e.Project)

                if packKeys.Contains k then
                    yield
                        sprintf
                            "`%s` is both a TestPacks row and a named exclusion: it can be one or the other."
                            (normalise e.Project)
        ]

        unrun @ notBuilt @ stale

    /// Reconcile the repository at `root`: its single `.sln` / `.slnx` (when
    /// there is exactly one) and the `*.Tests.fsproj` under `scannedDirs`,
    /// against the declared packs and exclusions. `Error` carries every
    /// finding, never only the first.
    let reconcile
        (root: string)
        (packs: ToolUp.Platform.TestPack list)
        (excluded: Exclusion list)
        : Result<unit, string list> =
        let solution =
            match
                List.ofSeq (Seq.append (IO.Directory.GetFiles(root, "*.slnx")) (IO.Directory.GetFiles(root, "*.sln")))
            with
            | [ single ] -> Some(solutionProjects (IO.File.ReadAllText single))
            | _ -> None

        let skip (path: string) =
            let p = path.Replace('\\', '/')
            p.Contains "/bin/" || p.Contains "/obj/" || p.Contains "/node_modules/"

        let onDisk = [
            for dir in scannedDirs do
                let full = IO.Path.Combine(root, dir)

                if IO.Directory.Exists full then
                    for file in IO.Directory.EnumerateFiles(full, "*.Tests*.fsproj", IO.SearchOption.AllDirectories) do
                        let relative = IO.Path.GetRelativePath(root, file)

                        if not (skip ("/" + relative)) && isTestProject relative then
                            yield normalise relative
        ]

        match findings solution onDisk packs excluded with
        | [] -> Ok()
        | found -> Error found

    // ─── The manifest ───────────────────────────────────────────────

    /// A row as the fold sees it: the lane decision already taken by the
    /// driver that knows its lanes. `Selected` is whether this process's
    /// lane runs it; a row that runs only elsewhere (`Where` without
    /// `Local`) is registered too, so the fold can say what it did not run.
    type Registered = {
        Name: string
        Kind: LegKind
        Where: Set<Site>
        Reads: Input list
        Budget: TimeSpan option
        Floor: Floor option
        Selected: bool
    }

    let private registry = ResizeArray<Registered>()

    /// Register one row of the repository's gate manifest. `current` is the
    /// lane this process runs; the row is selected when its `Lanes` carry
    /// it. Registering a name twice is refused rather than run twice — a
    /// repeated registration is a build script that composed itself twice,
    /// and two `PASS` lines for one leg would double-count against a CI floor.
    let register (current: 'Lane) (leg: GateLeg<'Lane>) : unit =
        if registry |> Seq.exists (fun r -> r.Name = leg.Name) then
            failwithf "Gate.register: a leg named `%s` is already registered." leg.Name

        registry.Add {
            Name = leg.Name
            Kind = leg.Kind
            Where = leg.Where
            Reads = leg.Reads
            Budget = leg.Budget
            Floor = leg.Floor
            Selected = Set.contains current leg.Lanes
        }

    /// Every registered row, in registration order.
    let registered () : Registered list = List.ofSeq registry

    /// The `ExpectoPack` rows `VerifyAll` runs locally, as the `TestPack`
    /// view a consumer's `BuildConfig` carries.
    let packs (manifest: GateLeg<'Lane> list) : TestPack list = [
        for row in manifest do
            match row.Kind with
            | LegKind.ExpectoPack project when Set.contains Site.Local row.Where -> TestPack.create row.Name project
            | LegKind.ExpectoPack _
            | LegKind.Body _
            | LegKind.Script _ -> ()
    ]

    /// How many rows the named CI job runs — CI's floor on the count of
    /// `PASS` lines that job's summary block must carry.
    let ciFloor (job: string) (manifest: GateLeg<'Lane> list) : int =
        manifest
        |> List.filter (fun r -> Set.contains (Site.Ci job) r.Where)
        |> List.length

    /// The pack rows whose test sources read nothing: the `pure` lane.
    let pureRows (manifest: GateLeg<'Lane> list) : GateLeg<'Lane> list =
        manifest
        |> List.filter (fun r ->
            List.isEmpty r.Reads
            && (match r.Kind with
                | LegKind.ExpectoPack _ -> Set.contains Site.Local r.Where
                | LegKind.Body _
                | LegKind.Script _ -> false))

    /// The environment variables among `reads`.
    let envInputs (reads: Input list) : string list =
        reads
        |> List.choose (function
            | Input.Env name -> Some name
            | Input.Tree
            | Input.Git
            | Input.Node
            | Input.Browser
            | Input.Docker
            | Input.Network -> None)

    /// The variables cleared from `row`'s environment when it runs: every
    /// `Env` input some other row declares and this row does not, so a pack
    /// cannot read a credential it did not name. A row's own allowlist is
    /// `envInputs row.Reads`.
    let scrubbedFor (rows: Registered list) (row: Registered) : string list =
        let mine = envInputs row.Reads |> Set.ofList

        rows
        |> List.collect (fun r -> envInputs r.Reads)
        |> List.distinct
        |> List.filter (fun name -> not (mine.Contains name))

    let siteName (site: Site) =
        match site with
        | Site.Local -> "local"
        | Site.Ci job -> "ci:" + job
        | Site.Release -> "release"

    let inputName (input: Input) =
        match input with
        | Input.Tree -> "Tree"
        | Input.Git -> "Git"
        | Input.Node -> "Node"
        | Input.Browser -> "Browser"
        | Input.Docker -> "Docker"
        | Input.Network -> "Network"
        | Input.Env name -> "Env " + name

    /// A process exit code as a verdict: 0 passes, anything else fails
    /// naming the code — the shape every runner that reports only an exit
    /// status gets.
    let verdictOfExit (code: int) : Verdict =
        if code = 0 then
            Verdict.Pass None
        else
            Verdict.Fail(sprintf "exit %d" code)

    /// A body whose only verdict is to raise, as a verdict: a `Failure`
    /// (`failwith`) is the body's own red, named; any other exception is a
    /// harness fault and propagates.
    let verdictOf (body: unit -> unit) : Verdict =
        try
            body ()
            Verdict.Pass None
        with Failure reason ->
            Verdict.Fail reason

    /// Apply a row's floor to its verdict. `readHighWater` reads the mark a
    /// `HighWater` file holds. A `FromRegistry` floor is the leg's own
    /// script's to apply and passes through; a floor over a `Pass` that
    /// reported no count is a fault in the row, not a pass.
    let judge (name: string) (floor: Floor option) (readHighWater: string -> int) (verdict: Verdict) : Verdict =
        match floor, verdict with
        | None, v
        | Some(Floor.FromRegistry _), v
        | _, (Verdict.Fail _ as v)
        | _, (Verdict.Pending _ as v) -> v
        | Some _, Verdict.Pass None ->
            Verdict.Fail(
                sprintf
                    "%s carries a floor but reported no case count; a floor over an unknown count holds nothing."
                    name
            )
        | Some(Floor.AtLeast least), Verdict.Pass(Some cases) when cases < least ->
            Verdict.Fail(
                sprintf
                    "only %d case(s) ran, below the floor of %d. A run this small means the harness matched almost nothing — check its output path and entry point before lowering the floor."
                    cases
                    least
            )
        | Some(Floor.Exactly exact), Verdict.Pass(Some cases) when cases <> exact ->
            Verdict.Fail(
                sprintf
                    "%d case(s) ran; the floor is exactly %d. Adding or removing a case edits the row's floor in the same commit; fewer than declared almost always means a case was skipped. Do not lower the floor to make this green."
                    cases
                    exact
            )
        | Some(Floor.HighWater file), Verdict.Pass(Some cases) when cases < readHighWater file ->
            Verdict.Fail(
                sprintf
                    "only %d case(s) ran, below the recorded high-water mark of %d (%s). The mark may only fall by hand, in the same commit as the argued loss."
                    cases
                    (readHighWater file)
                    file
            )
        | Some _, (Verdict.Pass _ as v) -> v

    /// The leg token on a summary line, byte-for-byte what CI counts:
    /// `PASS` and `FAIL (…)` as they have always been, `PENDING (…)` for
    /// an arm whose named input was absent.
    let statusToken (verdict: Verdict) =
        let firstLine (s: string) = s.Replace("\r", "").Split('\n').[0]

        match verdict with
        | Verdict.Pass _ -> "PASS"
        | Verdict.Fail reason -> sprintf "FAIL (%s)" (firstLine reason)
        | Verdict.Pending reason -> sprintf "PENDING (%s)" (firstLine reason)

    let mutable private lastRunResults: (string * Verdict * TimeSpan) list = []

    /// What the last aggregate in this process ran: each leg's name, verdict
    /// and wall clock, for the admission record a driver writes.
    let lastRun () = lastRunResults

    let internal recordRun (results: (string * Verdict * TimeSpan) list) = lastRunResults <- results

// ─── Build census ────────────────────────────────────────────────────

/// The per-project build census: what each solution project
/// costs to build, how big it is and where it sits in the reference graph,
/// written to `build-census.json` so a split or a gate budget follows
/// measured cost rather than folder size.
///
/// The reader takes MSBuild's `-clp:PerformanceSummary` text rather than a
/// binary log, so the package takes no MSBuild reader dependency. That text
/// is only per-project when the build runs with `-graph`: in a plain
/// solution build a project's time includes every second it spent blocked
/// on an unbuilt reference, which on this tree put most leaves near the
/// whole build's wall clock. `buildArgs` is therefore the one command whose
/// summary `readSummary` is valid for.
module Census =

    /// The census file, at the repository root.
    let fileName = "build-census.json"

    /// The `dotnet` arguments of the census build: the whole solution,
    /// rebuilt from scratch as a graph build, the summary written to
    /// `logFile` and nothing else to the console.
    let buildArgs (solution: string) (logFile: string) : string list = [
        "build"
        solution
        "-graph"
        "--no-incremental"
        "-nologo"
        "-v:q"
        sprintf "-flp:PerformanceSummary;Verbosity=quiet;LogFile=%s" logFile
    ]

    let private row =
        Text.RegularExpressions.Regex(@"^\s*(\d+) ms\s+(\S.*?\.\w+proj)\s+\d+ calls\s*$")

    let private key (path: string) =
        path.Replace('\\', '/').ToLowerInvariant()

    /// The project rows under `heading`, summed per project. A section ends
    /// at a blank line or the next summary heading; its target rows name no
    /// project file and are skipped.
    let private section (heading: string) (lines: string list) : (string * float) list =
        match lines |> List.skipWhile (fun l -> l.Trim() <> heading) with
        | [] -> []
        | _ :: body ->
            body
            |> List.takeWhile (fun l -> l.Trim() <> "" && not (l.TrimEnd().EndsWith "Summary:"))
            |> List.choose (fun l ->
                let m = row.Match l

                if m.Success then
                    Some(m.Groups[2].Value, float m.Groups[1].Value / 1000.0)
                else
                    None)
            |> List.groupBy (fst >> key)
            |> List.map (fun (_, rows) -> fst rows.Head, List.sumBy snd rows)

    /// Every project the `Project Performance Summary` names, with its
    /// evaluation time from the `Project Evaluation Performance Summary`.
    /// Text with no summary reads as no projects.
    let readSummary (text: string) : ProjectCost list =
        let lines = text.Replace("\r\n", "\n").Split('\n') |> List.ofArray

        let evaluation =
            section "Project Evaluation Performance Summary:" lines
            |> List.map (fun (p, s) -> key p, s)
            |> Map.ofList

        section "Project Performance Summary:" lines
        |> List.map (fun (project, seconds) -> {
            Project = project
            CompileSeconds = seconds
            EvaluationSeconds = evaluation.TryFind(key project) |> Option.defaultValue 0.0
        })

    let private includes (item: string) (projectXml: string) : string list =
        Xml.Linq.XDocument.Parse(projectXml).Descendants()
        |> Seq.filter (fun e -> e.Name.LocalName = item)
        |> Seq.choose (fun e -> e.Attribute(Xml.Linq.XName.Get "Include") |> Option.ofObj)
        |> Seq.map _.Value
        |> List.ofSeq

    /// The `Include` of every `ProjectReference` in a project file's text.
    let references (projectXml: string) : string list = includes "ProjectReference" projectXml

    /// The `Include` of every `Compile` item in a project file's text that
    /// names one file: no wildcard, no property.
    let compileItems (projectXml: string) : string list =
        includes "Compile" projectXml
        |> List.filter (fun i -> not (i.Contains "*" || i.Contains "$("))

    /// The census rows. `projects` is each solution project's repo-relative
    /// path with the repo-relative projects it references and its line
    /// count; `costs` are matched to it through their path relative to
    /// `root`. A project the summary does not name costs 0. Fan-in and
    /// fan-out count solution projects only. Most expensive first, then by
    /// path.
    let assemble
        (root: string)
        (costs: ProjectCost list)
        (projects: (string * string list * int) list)
        : CensusEntry list =
        let rootKey = (key root).TrimEnd('/') + "/"

        // A string prefix rather than `Path.GetRelativePath`, so a summary
        // written on one OS reads the same on another.
        let relativeKey (path: string) =
            let k = key path

            if k.StartsWith(rootKey, StringComparison.Ordinal) then
                k.Substring rootKey.Length
            else
                k

        let costOf = costs |> List.map (fun c -> relativeKey c.Project, c) |> Map.ofList

        let inSolution = projects |> List.map (fun (p, _, _) -> key p) |> Set.ofList

        let referencesOf =
            projects
            |> List.map (fun (p, refs, _) ->
                key p, refs |> List.map key |> List.filter inSolution.Contains |> Set.ofList)
            |> Map.ofList

        projects
        |> List.map (fun (project, _, lines) ->
            let k = key project
            let cost = costOf.TryFind k

            {
                Project = Paths.toSlash project
                CompileSeconds = cost |> Option.map _.CompileSeconds |> Option.defaultValue 0.0
                EvaluationSeconds = cost |> Option.map _.EvaluationSeconds |> Option.defaultValue 0.0
                Lines = lines
                FanIn = referencesOf |> Map.filter (fun _ refs -> refs.Contains k) |> Map.count
                FanOut = referencesOf[k].Count
            })
        |> List.sortBy (fun e -> -e.CompileSeconds, e.Project)

    /// Each solution project of the repository at `root` with its
    /// references (repo-relative) and the lines in its `Compile` items, the
    /// input `assemble` takes. A missing source file counts 0 lines.
    let projectGraph (root: string) (solutionProjects: string list) : (string * string list * int) list =
        solutionProjects
        |> List.map (fun project ->
            let path = IO.Path.Combine(root, project)
            let dir = IO.Path.GetDirectoryName path
            let xml = IO.File.ReadAllText path

            let local (inc: string) =
                IO.Path.Combine(dir, inc.Replace('\\', '/'))

            let relative (inc: string) =
                IO.Path.GetRelativePath(root, IO.Path.GetFullPath(local inc)) |> Paths.toSlash

            let lines =
                compileItems xml
                |> List.sumBy (fun inc ->
                    let file = local inc

                    if IO.File.Exists file then
                        Seq.length (IO.File.ReadLines file)
                    else
                        0)

            Paths.toSlash project, references xml |> List.map relative, lines)

    /// This machine as the census records it: OS, architecture and logical
    /// processor count. Never the host name — the file is committed, and a
    /// host name identifies a private machine without saying anything a
    /// reader of the figures can use.
    let machine () : string =
        sprintf
            "%s %s, %d logical processors"
            Runtime.InteropServices.RuntimeInformation.OSDescription
            (string Runtime.InteropServices.RuntimeInformation.OSArchitecture)
            System.Environment.ProcessorCount

    let private round1 (x: float) = Math.Round(x, 1)

    /// The census as `build-census.json` text: indented, rows in the order
    /// given, seconds to one decimal place, LF line ends.
    let render (census: BuildCensus) : string =
        use stream = new IO.MemoryStream()

        do
            use w =
                new Text.Json.Utf8JsonWriter(
                    stream,
                    Text.Json.JsonWriterOptions(
                        Indented = true,
                        Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    )
                )

            w.WriteStartObject()
            w.WriteString("treeSha", census.TreeSha)
            w.WriteString("machine", census.Machine)
            w.WriteString("measuredAt", census.MeasuredAt)
            w.WriteNumber("wallSeconds", round1 census.WallSeconds)
            w.WriteNumber("totalCompileSeconds", round1 (census.Projects |> List.sumBy _.CompileSeconds))
            w.WriteString("command", census.Command)
            w.WriteStartArray("projects")

            for e in census.Projects do
                w.WriteStartObject()
                w.WriteString("project", e.Project)
                w.WriteNumber("compileSeconds", round1 e.CompileSeconds)
                w.WriteNumber("evaluationSeconds", round1 e.EvaluationSeconds)
                w.WriteNumber("lines", e.Lines)
                w.WriteNumber("fanIn", e.FanIn)
                w.WriteNumber("fanOut", e.FanOut)
                w.WriteEndObject()

            w.WriteEndArray()
            w.WriteEndObject()

        Text.Encoding.UTF8.GetString(stream.ToArray()).Replace("\r\n", "\n") + "\n"

    /// The project paths a `build-census.json` text names, or why it names
    /// none.
    let projectSet (censusJson: string) : Result<string list, string> =
        try
            use doc = Text.Json.JsonDocument.Parse censusJson

            match doc.RootElement.TryGetProperty "projects" with
            | true, projects when projects.ValueKind = Text.Json.JsonValueKind.Array ->
                projects.EnumerateArray()
                |> Seq.map (fun p -> p.GetProperty("project").GetString())
                |> List.ofSeq
                |> Ok
            | _ -> Error "it has no `projects` array"
        with ex ->
            Error(sprintf "it does not parse (%s)" ex.Message)

    /// The `--check` verdict: findings, empty when the census names exactly
    /// the solution's projects. Timings are not compared — they move machine
    /// to machine; a project added or removed is what makes the file stale.
    let check (censusJson: string) (solutionText: string) : string list =
        match projectSet censusJson with
        | Error why -> [ sprintf "%s cannot be read: %s." fileName why ]
        | Ok named ->
            let current = Gate.solutionProjects solutionText
            let keys (ps: string list) = ps |> List.map key |> Set.ofList
            let missing = Set.difference (keys current) (keys named)
            let gone = Set.difference (keys named) (keys current)

            [
                for p in current do
                    if missing.Contains(key p) then
                        yield sprintf "%s is in the solution but not in %s." p fileName
                for p in named do
                    if gone.Contains(key p) then
                        yield sprintf "%s is in %s but no longer in the solution." p fileName
            ]

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

    /// One leg of an aggregating gate. `Run` returns the leg's `Verdict`
    /// and does its own tracing, so each caller keeps the per-leg log
    /// lines it already emitted.
    type Leg = { Name: string; Run: unit -> Verdict }

    /// A leg whose body answers with an exit code (0 = pass): the shape
    /// every child process gives.
    let leg name (run: unit -> int) = {
        Name = name
        Run = fun () -> Gate.verdictOfExit (run ())
    }

    /// A leg whose body answers with its verdict.
    let legOf name (run: unit -> Verdict) = { Name = name; Run = run }

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
    /// is why the headline below says "failed" and not "FAILED" — and why
    /// a `Pending` leg prints `PENDING`, which matches neither.
    let runAll (label: string) (noun: string) (legs: Leg list) : unit =
        let results = ResizeArray<string * Verdict * TimeSpan>()

        try
            for leg in legs do
                let clock = Diagnostics.Stopwatch.StartNew()

                let verdict =
                    try
                        leg.Run()
                    with ex ->
                        Trace.traceError (sprintf "%s: %s raised — %s" label leg.Name ex.Message)
                        Verdict.Fail("raised: " + ex.Message)

                results.Add(leg.Name, verdict, clock.Elapsed)
        finally
            // `finally` so a genuinely fatal abort still reports what had
            // run by then; the catch above is what keeps the ordinary
            // failing case complete.
            Gate.recordRun (List.ofSeq results)
            Trace.tracefn ""
            Trace.tracefn "%s summary:" label

            for name, verdict, _ in results do
                Trace.tracefn "  %s — %s" (Gate.statusToken verdict) name

        let failures =
            results
            |> Seq.choose (fun (n, v, _) ->
                match v with
                | Verdict.Fail reason -> Some(n, reason)
                | Verdict.Pass _
                | Verdict.Pending _ -> None)
            |> Seq.toList

        if List.isEmpty failures then
            Trace.tracefn "%s: all %d %s(s) passed." label results.Count noun
        else
            let named =
                failures
                |> List.map (fun (n, reason) -> sprintf "%s (%s)" n (reason.Replace("\r", "").Split('\n').[0]))
                |> String.concat "; "

            let headline =
                sprintf "%s: %d of %d %s(s) failed — %s" label failures.Length results.Count noun named

            // Printed as well as raised, so it lands with the summary
            // block where a reader is already looking rather than only
            // inside FAKE's error block.
            Trace.tracefn "%s" headline
            failwith headline

// ─── Template-gate package closure ───────────────────────────────────

/// Phase 754 — the stranger's-package guard.
///
/// The template gates (`VerifyTemplates`,
/// `VerifyPackagedModuleTemplate`) supply the SDK packages the gated
/// scaffolds reference by packing them from source at a throwaway
/// version into a scratch feed. Each entry was a bare package-id
/// STRING, and the project to pack was derived from it by
/// `sprintf "src/%s/%s.fsproj"` — so the id and the producing project
/// were the same fact spelled once, and an id this repo does not
/// produce could not be expressed at all.
///
/// That coupling broke the moment a package's `PackageId` stopped
/// matching its directory (this phase, moving the AG Grid / AG Charts
/// bindings to `ToolUp.Feliz.*` while their projects stay at
/// `src/Feliz.*/`), and the failure it was implicitly guarding against
/// is not hypothetical: Phase 307 found `Feliz.AgGrid` / `Feliz.AgCharts`
/// missing from the closure, and because BOTH ids exist on nuget.org as
/// unrelated packages by another author, restore did not fail — it
/// silently served a STRANGER'S package, and only the NU1603 escalation
/// turned that into an error.
///
/// So the id and the project are now declared separately, and the two
/// are RECONCILED against what the pack actually produced: after the
/// pack loop, every declared id must appear in the scratch feed as
/// `<id>.<version>.nupkg`. An id no project in this repo produces has
/// no such file, and the gate says so by name instead of proceeding to
/// a restore that will quietly resolve it from nuget.org.
///
/// Pure so it is provable in both directions from the Build test pack
/// (`TemplateGateTests`) — a guard that has never been seen to fail is
/// not known to work.
module TemplateGate =

    /// One package the template gate must supply: the id consumers
    /// resolve, and the in-repo project that produces it. They are not
    /// the same string in general, and assuming they were is the defect
    /// this type exists to remove.
    type GatePackage = {
        /// The `PackageId` the packed project emits — the name a
        /// consumer's `PackageReference` resolves, and the name NuGet
        /// caches under.
        Id: string
        /// Repo-relative path to the producing `.fsproj`.
        Project: string
    }

    /// Declare a gate package. `id` is the emitted `PackageId`; `project`
    /// the repo-relative `.fsproj` that emits it.
    let package id project = { Id = id; Project = project }

    /// Declared ids for which the pack produced no `<id>.<version>.nupkg`,
    /// in declaration order.
    ///
    /// `producedFiles` may be full paths or bare file names — only the
    /// file name is read. Matching is case-insensitive because NuGet
    /// package ids are, and a case-only difference between the
    /// declaration and the emitted `PackageId` would otherwise be
    /// reported as a stranger's package on a case-sensitive filesystem
    /// and pass on Windows.
    let unproducedIds (version: string) (declared: GatePackage list) (producedFiles: string seq) : string list =
        let produced =
            producedFiles
            |> Seq.map (fun path -> IO.Path.GetFileName(path: string))
            |> Set.ofSeq
            |> Set.map (fun name -> name.ToLowerInvariant())

        declared
        |> List.filter (fun p -> not (produced.Contains((sprintf "%s.%s.nupkg" p.Id version).ToLowerInvariant())))
        |> List.map _.Id

    /// The failure message for `unproducedIds`. Names the ids, the class,
    /// and the two ways the declaration can be wrong — because the
    /// remedy differs: a typo is fixed in the list, a genuinely-absent
    /// package means the closure names something the repo does not own.
    let report (gate: string) (version: string) (unproduced: string list) =
        sprintf
            "%s: %d declared gate package id(s) were NOT produced by this repo's own pack at %s: %s. This is the stranger's-package class (Phase 307): the scratch feed cannot serve the id, so restore falls through to nuget.org and may resolve an UNRELATED package of the same name published by someone else. Either the id is misspelled in the gate closure, or its `Project` does not emit that `PackageId` — check `<PackageId>` in the declared project. Never silence this by removing the id: the packages the closure names are the ones the scaffolds actually resolve."
            gate
            (List.length unproduced)
            version
            (String.concat ", " unproduced)

    // ─── The private packages folder ─────────────────────────────────
    //
    // A template gate restores its scaffolds against a THROWAWAY package
    // version, so a stale extracted copy from an earlier run must never be
    // what restore resolves. Guessing the machine's global-packages folder
    // and evicting that version there is not enough: a `globalPackagesFolder`
    // in any nuget.config on NuGet's search path overrides both
    // `NUGET_PACKAGES` and the default, so the eviction can no-op on a folder
    // NuGet is not reading, and the gate goes green over the previous run's
    // package. And even the right shared folder is shared by every worktree
    // on the machine, so two gates running at once evict each other
    // mid-restore. So each run gets its OWN packages folder under the calling
    // repository's `obj/` (per-worktree by construction), and the gate points
    // `NUGET_PACKAGES` at it for every child it spawns. There is nothing
    // ambient to get wrong, and no other checkout can see the folder.

    /// The private packages folder a template gate points `NUGET_PACKAGES`
    /// at. Pure: a function of the repository root only.
    let privatePackagesFolder (repoRoot: string) : string =
        IO.Path.Combine(repoRoot, "obj", "template-gate-packages")

    /// What `evict` removed, and where — the gate prints both, so a reader
    /// can tell a cold run (nothing removed) from one that needed it.
    type EvictionReport = {
        /// The private packages folder evicted.
        Folder: string
        /// The package ids (top-level subfolder names) present before the
        /// evict and now gone, sorted. NuGet lays a global-packages folder out
        /// as `<folder>/<id>/<version>/`, so a top-level name IS a package id.
        Removed: string list
    }

    /// Wipe the private packages folder and leave it present but empty. Safe
    /// to delete wholesale: nothing but the gate's own restores writes there.
    let evict (folder: string) : EvictionReport =
        let removed =
            if IO.Directory.Exists folder then
                IO.Directory.EnumerateDirectories folder
                |> Seq.map IO.Path.GetFileName
                |> Seq.sort
                |> Seq.toList
            else
                []

        if IO.Directory.Exists folder then
            IO.Directory.Delete(folder, true)

        IO.Directory.CreateDirectory folder |> ignore
        { Folder = folder; Removed = removed }

    // ─── The gate as data ────────────────────────────────────────────

    /// The version every gate packs its closure at and every scaffold is
    /// told to resolve. Packing at the tree's own version would be a
    /// same-version repack, and NuGet would serve whatever was extracted
    /// last rather than this run's source.
    let version = "0.0.0-templategate"

    /// Which tree a `Neutral` check scans.
    type NeutralScope =
        /// The template's own files in the repository.
        | TemplateFiles
        /// The repository the template scaffolded.
        | ScaffoldedTree

    /// One check a template gate makes beyond building.
    type Check =
        /// The template's vendored copy of an SDK file equals the region of
        /// the source before the marker line: a copy drifts silently by
        /// construction, and a template ships it as conformant.
        | VendoredPack of source: string * vendored: string * marker: string
        /// Every `ToolUp.*` package in the restore graph resolved at the gate
        /// version — the NU1603 escalation read off `project.assets.json`.
        /// `Some path` reads that one scaffold-relative file; `None` reads
        /// every one under the scaffold.
        | StrayPins of assets: string option
        /// The scaffold's packed nupkg carries every `required` entry and no
        /// `forbidden` one.
        | NupkgLayout of required: string list * forbidden: string list
        /// No publication-boundary token in the scope. The private token list
        /// is the repository's token file, else the environment variable
        /// `tokensVariable` names.
        | Neutral of scope: NeutralScope * tokensVariable: string

    /// One step of an instantiated scaffold's own pipeline, run in the
    /// scaffold directory.
    type Step =
        /// `dotnet <args>`, which must exit 0.
        | Dotnet of args: string list
        /// A check, at this point in the pipeline.
        | Verify of Check

    /// A template the gate instantiates outside the repository with
    /// `dotnet new`, then runs.
    type Instantiation = {
        /// The repo-relative template directory.
        Template: string
        /// The template's `dotnet new` short name.
        ShortName: string
        /// The `-n` name, and the scaffold's directory under the scratch root.
        Name: string
        /// Extra `dotnet new` arguments, after `-o`.
        Args: string list
        /// The scratch root's name under the system temp directory.
        Scratch: string
        /// The scaffold's pipeline, in order.
        Steps: Step list
    }

    /// What the gate builds.
    type Scaffold =
        /// Root-inheriting template projects, each built where it lies.
        | InPlace of projects: string list
        /// A whole-repository template, scaffolded and run.
        | Instantiate of Instantiation

    /// One template gate: everything that differs between the gates, as data.
    type TemplateGateSpec = {
        /// The target name, which prefixes every line the gate prints.
        Name: string
        /// The packages packed at `version` into the feed.
        Closure: GatePackage list
        /// The repo-relative scratch feed directory, one per gate.
        Feed: string
        /// Checks run before anything is packed.
        Checks: Check list
        Scaffold: Scaffold
        /// The closing line on success.
        Done: string
    }

    /// The `dotnet build` arguments for one in-place template project:
    /// built at the gate version, the scratch feed ADDED to the sources
    /// (the templates still need nuget.org), and NU1603 escalated so a
    /// closure missing a package fails by name rather than resolving an
    /// older one.
    let inPlaceBuildArgs (feedDir: string) (project: string) : string list = [
        "build"
        project
        sprintf "-p:ToolUpSdkVersion=%s" version
        sprintf "-p:RestoreAdditionalProjectSources=%s" feedDir
        "-warnaserror:NU1603"
        "--nologo"
    ]

    /// The `dotnet new` arguments that scaffold `i` into `scaffoldDir`
    /// against the gate version and the scratch feed (forward-slashed: it
    /// lands in an F# string and in nuget.config).
    let instantiateArgs (i: Instantiation) (scaffoldDir: string) (feedDir: string) : string list =
        [ "new"; i.ShortName; "-n"; i.Name; "-o"; scaffoldDir ]
        @ i.Args
        @ [ "--sdk-version"; version; "--feed"; Paths.toSlash feedDir ]

    /// The drift finding for a vendored copy, `None` when it matches.
    let vendoredFinding
        (gate: string)
        (source: string, vendored: string, marker: string)
        (sourceText: string)
        (vendoredText: string)
        : string option =
        let normalise (s: string) =
            s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd()

        let markerIndex = sourceText.IndexOf(marker, StringComparison.Ordinal)

        if markerIndex < 0 then
            Some(
                sprintf
                    "%s: could not find the laws/self-test boundary marker %s in %s. The vendored-copy drift check cannot run; re-establish the marker or update this target."
                    gate
                    marker
                    source
            )
        elif normalise (sourceText.Substring(0, markerIndex)) <> normalise vendoredText then
            Some(
                sprintf
                    "%s: the vendored conformance pack has drifted from the SDK's.%sRe-copy the laws region:%s  the first %d characters of %s (everything before `%s`) into %s."
                    gate
                    Environment.NewLine
                    Environment.NewLine
                    markerIndex
                    source
                    marker
                    vendored
            )
        else
            None

    /// The `ToolUp.*` packages a restore graph resolved at a version other
    /// than the gate's, distinct, in first-seen order.
    let strayPins (assetsTexts: string seq) : (string * string) list =
        assetsTexts
        |> Seq.collect (fun text ->
            Text.RegularExpressions.Regex.Matches(text, "\"(ToolUp\\.[A-Za-z.]+)/([^\"]+)\"")
            |> Seq.map (fun m -> m.Groups[1].Value, m.Groups[2].Value))
        |> Seq.filter (fun (_, v) -> v <> version)
        |> Seq.distinct
        |> List.ofSeq

    /// What is wrong with a packed nupkg's entries, empty when nothing is.
    let layoutFindings (required: string list) (forbidden: string list) (entries: Set<string>) : string list = [
        match required |> List.filter (fun r -> not (entries.Contains r)) with
        | [] -> ()
        | missing ->
            sprintf
                "the packed nupkg is missing %s. A consumer's Fable build resolves the client tier from these paths."
                (String.concat ", " missing)
        for f in forbidden do
            if entries.Contains f then
                sprintf "%s is packed — declared server-only, so it must never reach a consumer's Fable compile." f
    ]

    /// Every boundary-token hit in `files` (path beside its lines), as
    /// `path:line  'token'`. `masked` are literal values the gate itself
    /// substituted (the scratch feed's machine path), removed from each line
    /// before matching: they are this machine's, not the template's.
    let neutralHits
        (rules: (string * Text.RegularExpressions.Regex) list)
        (masked: string list)
        (files: (string * string[]) seq)
        : string list =
        files
        |> Seq.collect (fun (path, lines) ->
            lines
            |> Seq.mapi (fun i line -> i + 1, masked |> List.fold (fun (l: string) m -> l.Replace(m, "")) line)
            |> Seq.collect (fun (n, line) ->
                rules
                |> Seq.filter (fun (_, rx) -> rx.IsMatch line)
                |> Seq.map (fun (token, _) -> sprintf "%s:%d  '%s'" path n token)))
        |> List.ofSeq

    /// The text files under `root` a `Neutral` check reads, relative path
    /// beside lines; build output and installed packages skipped.
    let private scannedFiles (root: string) : (string * string[]) seq =
        let skipped = [ "bin"; "obj"; "node_modules"; "output" ]

        IO.Directory.EnumerateFiles(root, "*", IO.SearchOption.AllDirectories)
        |> Seq.filter (fun path ->
            let segments = IO.Path.GetRelativePath(root, path).Split([| '\\'; '/' |])
            not (segments |> Array.exists (fun s -> List.contains s skipped)))
        |> Seq.map (fun path -> IO.Path.GetRelativePath(root, path), IO.File.ReadAllLines path)

    // ─── The gate, run ───────────────────────────────────────────────

    /// Run one template gate in the current directory (the repository root):
    /// the pre-checks, a scratch feed packed with the closure at `version`
    /// under a private packages folder, the closure reconciled against what
    /// was packed, then the scaffold — each in-place project built and
    /// aggregated, or the template instantiated outside the repository and
    /// its pipeline run. Raises on the first failure, except that in-place
    /// projects are independent and all reported.
    let run (spec: TemplateGateSpec) : unit =
        let root = Fake.IO.Path.getFullName "."
        let feedDir = Fake.IO.Path.getFullName spec.Feed

        let ensure (exe: string) (args: string list) (dir: string) =
            let r = Proc.exec exe args dir

            if r.ExitCode <> 0 then
                failwithf "%s: `%s %s` exited %d (in %s)" spec.Name exe (String.concat " " args) r.ExitCode dir

        let check (scaffoldDir: string) (c: Check) =
            match c with
            | VendoredPack(source, vendored, marker) ->
                match
                    vendoredFinding
                        spec.Name
                        (source, vendored, marker)
                        (IO.File.ReadAllText source)
                        (IO.File.ReadAllText vendored)
                with
                | Some finding -> failwith finding
                | None -> Trace.tracefn "▶ %s: vendored conformance pack matches %s" spec.Name source
            | Neutral(scope, tokensVariable) ->
                let rules = Neutrality.rules (Neutrality.externalTokens root tokensVariable)
                let fullList = rules.Length > 1

                let label, masked, dir =
                    match scope with
                    | TemplateFiles ->
                        let template =
                            match spec.Scaffold with
                            | Instantiate i -> i.Template
                            | InPlace _ ->
                                failwithf "%s: a TemplateFiles check needs an instantiated template." spec.Name

                        "the template's files", [], Fake.IO.Path.getFullName template
                    | ScaffoldedTree -> "the scaffolded repository", [ Paths.toSlash feedDir ], scaffoldDir

                match neutralHits rules masked (scannedFiles dir) with
                | [] ->
                    Trace.tracefn
                        "▶ %s: %s carries no banned vocabulary (%s)"
                        spec.Name
                        label
                        (if fullList then
                             sprintf "%d tokens" rules.Length
                         else
                             "NEUTRALITY TOKEN SOURCE ABSENT: only the public canary was enforced")
                | hits ->
                    failwithf
                        "%s: the publication boundary is broken in %s, %d reference(s) to private vocabulary:%s%s%sThis template is Apache-2.0 public; neutralise the wording."
                        spec.Name
                        label
                        hits.Length
                        Environment.NewLine
                        (hits |> List.truncate 40 |> String.concat Environment.NewLine)
                        Environment.NewLine
            | StrayPins assets ->
                let files =
                    match assets with
                    | Some relative ->
                        let file = IO.Path.Combine(scaffoldDir, relative)

                        if not (IO.File.Exists file) then
                            failwithf "%s: no restore graph at %s — the scaffold did not restore." spec.Name file

                        [ file ]
                    | None ->
                        IO.Directory.EnumerateFiles(scaffoldDir, "project.assets.json", IO.SearchOption.AllDirectories)
                        |> List.ofSeq

                match strayPins (files |> Seq.map IO.File.ReadAllText) with
                | [] -> ()
                | pins ->
                    failwithf
                        "%s: %d ToolUp.* package(s) resolved at a version other than %s — the gate closure is incomplete, so the scaffold built against a MIX of current source and a released SDK: %s. Add the named package(s) to the gate's `Closure`."
                        spec.Name
                        (List.length pins)
                        version
                        (pins |> List.map (fun (p, v) -> sprintf "%s@%s" p v) |> String.concat ", ")
            | NupkgLayout(required, forbidden) ->
                let name =
                    match spec.Scaffold with
                    | Instantiate i -> i.Name
                    | InPlace _ -> failwithf "%s: a NupkgLayout check needs an instantiated template." spec.Name

                let nupkg =
                    IO.Directory.GetFiles(feedDir, name + ".*.nupkg")
                    |> Array.filter (fun p -> not (p.EndsWith(".snupkg", StringComparison.OrdinalIgnoreCase)))
                    |> Array.sort
                    |> Array.tryLast

                match nupkg with
                | None -> failwithf "%s: the scaffold's Pack produced no %s nupkg in %s." spec.Name name feedDir
                | Some path ->
                    use archive = IO.Compression.ZipFile.OpenRead path

                    let entries =
                        archive.Entries |> Seq.map (fun e -> Paths.toSlash e.FullName) |> Set.ofSeq

                    match layoutFindings required forbidden entries with
                    | [] ->
                        Trace.tracefn "▶ %s: %s carries a conformant fable/ layout" spec.Name (IO.Path.GetFileName path)
                    | findings -> failwithf "%s: %s" spec.Name (String.concat " " findings)

        for c in spec.Checks do
            check root c

        Shell.deleteDir feedDir
        Directory.ensure feedDir

        let packages = privatePackagesFolder root
        let eviction = evict packages

        Trace.tracefn
            "▶ %s: private packages folder %s (evicted %d prior entr%s: %s)"
            spec.Name
            eviction.Folder
            eviction.Removed.Length
            (if eviction.Removed.Length = 1 then "y" else "ies")
            (String.concat ", " eviction.Removed)

        Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages)

        // Fail-fast, unlike the in-place builds below: every build reads
        // these packages, so a failed pack makes the rest meaningless.
        for pkg in spec.Closure do
            if not (IO.File.Exists(Fake.IO.Path.getFullName pkg.Project)) then
                failwithf
                    "%s: gate package %s declares project %s, which does not exist. Fix the `Project` in the gate's `Closure`."
                    spec.Name
                    pkg.Id
                    pkg.Project

            Trace.tracefn "▶ %s: packing %s (%s) @ %s" spec.Name pkg.Id pkg.Project version

            ensure
                "dotnet"
                [
                    "pack"
                    pkg.Project
                    sprintf "-p:Version=%s" version
                    "-o"
                    feedDir
                    "--nologo"
                ]
                "."

        match unproducedIds version spec.Closure (IO.Directory.EnumerateFiles(feedDir, "*.nupkg")) with
        | [] -> ()
        | unproduced -> failwith (report spec.Name version unproduced)

        match spec.Scaffold with
        | InPlace projects ->
            // Each project is what a separate `dotnet new` produces, so a
            // broken one costs its own signal and not the others'.
            projects
            |> List.map (fun proj ->
                Aggregate.leg proj (fun () ->
                    Trace.tracefn "▶ %s: building %s" spec.Name proj

                    // A consumer's fresh copy has no obj/ or bin/, and NuGet
                    // replays a leftover project.assets.json's warnings
                    // rather than resolving again.
                    let projDir = IO.Path.GetDirectoryName(Fake.IO.Path.getFullName proj)
                    Shell.deleteDir (IO.Path.Combine(projDir, "obj"))
                    Shell.deleteDir (IO.Path.Combine(projDir, "bin"))
                    (Proc.exec "dotnet" (inPlaceBuildArgs feedDir proj) ".").ExitCode))
            |> Aggregate.runAll spec.Name "template project"
        | Instantiate i ->
            let scratchRoot = Scratch.root i.Scratch
            let scaffoldDir = IO.Path.Combine(scratchRoot, i.Name)
            Shell.deleteDir scratchRoot
            Directory.ensure scaffoldDir

            let templatePath = Fake.IO.Path.getFullName i.Template

            // A machine may carry an earlier install of the same identity,
            // and `install` refuses rather than replacing it.
            Proc.exec "dotnet" [ "new"; "uninstall"; templatePath ] "." |> ignore
            ensure "dotnet" [ "new"; "install"; templatePath ] "."

            try
                ensure "dotnet" (instantiateArgs i scaffoldDir feedDir) "."

                for step in i.Steps do
                    match step with
                    | Dotnet args -> ensure "dotnet" args scaffoldDir
                    | Verify c -> check scaffoldDir c
            finally
                Proc.exec "dotnet" [ "new"; "uninstall"; templatePath ] "." |> ignore

        if spec.Done <> "" then
            Trace.tracefn "▶ %s: %s" spec.Name spec.Done

// ─── Phase 803 — extra legs a repository composes into `VerifyAll` ──

/// A leg `VerifyAll` runs AFTER its Expecto packs, registered by the
/// consuming repository's own build script.
///
/// **Why a registry and not a `BuildConfig` field.** `BuildConfig` is a
/// published record: a field added to it retypes the compiler-generated
/// constructor, reddens the package's Public-API baseline and moves every
/// consumer's `Build.fs` — the cost `TestPackHost` above records for the
/// solution path, and the same one here. A registration function grows
/// the surface by one additive token instead, which is what a repository
/// with ONE extra leg (this one has a Fable-tier harness) should cost the
/// package. It is the model FAKE itself uses — `Target.create` is a
/// process-wide registry consulted when the target runs — so a leg is
/// registered at script top level, before or after `registerTargets`,
/// and read when `VerifyAll` executes. The module-level state is the
/// deliberate, documented exception to the no-mutable rule, for the same
/// reason FAKE's is: a build script is one process, one run.
///
/// **What a leg is.** A name and a body returning an exit code — the
/// `Aggregate.leg` shape, kept behind a function because that module is
/// `internal`. A body that throws is recorded as a failure by the
/// aggregate, never fail-fast. The leg appears in the `VerifyAll
/// summary:` block as one more `PASS` / `FAIL` line, which is a CI
/// contract: a repository whose CI counts those lines against a floor
/// renders the floor from its gate manifest (forge's `EXPECTED_PACKS`),
/// and `Gate.register` is where the leg becomes a row of it.
///
/// **What it is NOT.** A pack: `TestPacks` are Expecto runners built
/// once and launched as dlls, and the build-once step, the
/// `TOOLUP_TEST_ARGS` pass-through and the lane filter are theirs. A leg
/// owns its own toolchain — which is the point; the Fable harness needs
/// Node, `npm ci` and a transpile no Expecto pack has.
[<RequireQualifiedAccess>]
module VerifyLeg =

    /// Register a leg by name: a `Body` row of the gate manifest that runs
    /// locally in every lane and reads the tree, through `Gate.register`,
    /// which keeps the refusal of a repeated name.
    let register (name: string) (run: unit -> int) : unit =
        Gate.register () {
            Name = name
            Kind = LegKind.Body(fun () -> Gate.verdictOfExit (run ()))
            Lanes = set [ () ]
            Where = set [ Site.Local ]
            Reads = [ Input.Tree ]
            Budget = None
            Floor = None
        }

    /// The `Body` rows registered so far, in registration order, as the
    /// exit-code legs they were registered as (a non-zero code for a
    /// failed or pending verdict).
    let registeredLegs () : (string * (unit -> int)) list = [
        for row in Gate.registered () do
            match row.Kind with
            | LegKind.Body run ->
                row.Name,
                fun () ->
                    match run () with
                    | Verdict.Pass _ -> 0
                    | Verdict.Fail _
                    | Verdict.Pending _ -> 1
            | LegKind.ExpectoPack _
            | LegKind.Script _ -> ()
    ]

// ─── Build configuration ───────────────────────────────────────────

open ToolUp.Platform

/// How `VerifyAll` reaches a test pack (Phase 731).
///
/// It used to be `dotnet run --project <fsproj>` per pack — a build and a
/// launch in one call. That launch path intermittently WEDGES before the
/// suite starts: ~0% CPU, no output, indistinguishable from a slow pack
/// until someone gives up waiting on it. Invoking the pack's already-built
/// test dll instead has no such step, and did not reproduce the hang once
/// across a campaign that hit it repeatedly the other way.
///
/// So the target builds ONCE up front, then launches dlls. The build is
/// not an optimisation but a prerequisite — `dotnet <dll>` does not build,
/// so a fresh checkout would otherwise have nothing to launch. It also
/// makes the Public-API approval gate's precondition ("run `dotnet build
/// ToolUp.Forge.sln` first") true of the canonical invocation rather than
/// only of the CI job that happens to wrap it.
///
/// `internal` for the same reason `Aggregate` is, and here it is load-
/// bearing rather than tidy: `BuildConfig` and `TestPack` are published
/// types of `ToolUp.Platform.Build`, so a field carrying (say) the
/// solution path would retype a public record — reddening the package's
/// Public-API baseline and moving every consumer's `Build.fs` — to record
/// something the target can discover for itself.
module internal TestPackHost =

    /// The one solution in the working directory, if there is exactly one.
    ///
    /// Ambiguity resolves to `None` on purpose: two solutions mean the
    /// target cannot know which one covers the packs, and building the
    /// wrong one is worse than building the projects individually.
    let solutionInWorkingDir () : string option =
        match !!"*.slnx" ++ "*.sln" |> List.ofSeq with
        | [ single ] -> Some single
        | _ -> None

    /// Build everything the packs need, once, before any of them runs.
    /// Raises on failure — a pack cannot run against a tree that did not
    /// compile, so there is nothing for the aggregate to report about.
    let buildOnce (packs: TestPack list) =
        let build (what: string) (args: string list) =
            Trace.tracefn "▶ VerifyAll: building %s" what

            let result = Proc.exec "dotnet" args "."

            if result.ExitCode <> 0 then
                failwithf
                    "VerifyAll: building %s failed (exit %d). No pack ran — fix the build first; the per-pack summary below would be about a tree that does not compile."
                    what
                    result.ExitCode

        match solutionInWorkingDir () with
        | Some sln -> build sln [ "build"; sln; "--nologo" ]
        | None ->
            // No single solution to lean on — build each pack's own
            // project. Slower and narrower (it misses cross-project
            // breakage a solution build catches), but it is the honest
            // fallback for a consumer repo laid out differently.
            for pack in packs do
                build pack.Project [ "build"; pack.Project; "--nologo" ]

    /// The pack's built assembly, asked of MSBuild rather than guessed.
    ///
    /// Deliberately not `<projDir>/bin/<config>/<tfm>/<name>.dll`: that
    /// convention holds today and is exactly the kind of assumption that
    /// breaks silently when a project sets `AssemblyName`, an output path,
    /// or a second target framework. `TargetPath` is the property MSBuild
    /// itself resolves the answer from.
    let resolveTargetPath (project: string) : Result<string, string> =
        let result =
            Proc.redirected "dotnet" [ "msbuild"; project; "-getProperty:TargetPath"; "-nologo" ] "."

        if result.ExitCode <> 0 then
            Error(sprintf "could not resolve TargetPath for %s (dotnet msbuild exit %d)" project result.ExitCode)
        else
            let path = result.StdOut.Trim()

            if String.IsNullOrWhiteSpace path then
                Error(sprintf "dotnet msbuild reported an empty TargetPath for %s" project)
            elif not (IO.File.Exists path) then
                Error(sprintf "%s resolves to %s, which does not exist — was the build skipped?" project path)
            else
                Ok path

    /// Expecto's JUnit summary carries no `tests` attribute; the cases are
    /// its `<testcase` elements.
    let private junitCase = Text.RegularExpressions.Regex(@"<testcase\b")

    /// Run one pack and return its verdict. Trace what is happening, never
    /// throw for an ordinary failure, and hand back the verdict so the
    /// summary can name it. The case count is read from the JUnit summary
    /// Expecto writes (`--junit-summary`), a structured record, never from
    /// its coloured console line; a pack that wrote none passes with no
    /// count. `scrub` names the variables cleared from the child's
    /// environment (`Gate.scrubbedFor`), so the pack reads only the inputs
    /// its row declares.
    let runPack (extraArgs: string list) (scrub: string list) (pack: TestPack) : Verdict =
        Trace.tracefn "▶ VerifyAll: %s (%s)" pack.Name pack.Project

        match resolveTargetPath pack.Project with
        | Error message ->
            Trace.traceError (sprintf "VerifyAll: %s — %s" pack.Name message)
            Verdict.Fail message
        | Ok dll ->
            let summaryDir = Scratch.root "toolup-verifyall"
            IO.Directory.CreateDirectory summaryDir |> ignore
            let summary = IO.Path.Combine(summaryDir, pack.Name + ".junit.xml")
            IO.File.Delete summary

            // The working directory is the caller's, which is what
            // `dotnet run --project` also handed the child (measured, not
            // assumed — `dotnet run` does NOT move the child into the
            // project directory). Any pack resolving a path relative to
            // cwd therefore sees exactly what it saw before.
            let result =
                Proc.execWith
                    {
                        Proc.options with
                            Environment = scrub |> List.map (fun name -> name, "")
                    }
                    "dotnet"
                    (dll :: extraArgs @ [ "--junit-summary"; summary ])
                    "."

            let cases =
                if IO.File.Exists summary then
                    Some(junitCase.Matches(IO.File.ReadAllText summary).Count)
                else
                    None

            match Gate.verdictOfExit result.ExitCode with
            | Verdict.Pass _ -> Verdict.Pass cases
            | other -> other

// ─── Resilient package push (Phase 1016) ─────────────────────────────

/// The `Publish` target's push loop, with the network taken out of it.
///
/// One slow or cancelled upload used to abort the whole release: the
/// push client's 300 s default timeout, or one cancelled symbol-package
/// upload, failed the target with the release half served, and the
/// only remedy was a manual re-run. Here each package's push (which
/// includes its `.snupkg`, auto-detected by `dotnet nuget push`) runs
/// under a longer `--timeout` and is retried with backoff before the
/// target gives up. When it does give up, it names every package still
/// unpushed, which is what a re-run will push.
///
/// Retrying is safe because every push passes `--skip-duplicate`: a
/// retry whose package was already accepted is skipped by the server
/// rather than refused, so it moves on to whatever was left (in the
/// common case, the symbol package).
///
/// The budget is a `RetryPolicy` record (portability rule 3): the
/// transport tier's own retry type, reused here, not a second shape.
/// `Timeout` is the per-push `--timeout`.
///
/// `internal` for the reason `TestPackHost` is: the unit is reached by
/// the Build test pack through `InternalsVisibleTo` and by the root
/// `Build.fs`, which compiles this file into its own assembly. It does
/// not enlarge the published surface of `ToolUp.Platform.Build`.
module internal NugetPush =

    open ToolUp.Platform.Transport

    /// Seconds per `dotnet nuget push` (the `--timeout` value).
    [<Literal>]
    let TimeoutVariable = "TOOLUP_PUBLISH_PUSH_TIMEOUT_SECONDS"

    /// Retries after a package's first failed push (0 = no retry).
    [<Literal>]
    let RetriesVariable = "TOOLUP_PUBLISH_PUSH_RETRIES"

    /// The first push plus three retries, waiting 15 s, 30 s and 60 s
    /// between them, each push allowed 900 s. One package's worst case
    /// is therefore 4 x 900 s + 105 s, just under 62 minutes.
    let defaultPolicy: RetryPolicy = {
        MaxAttempts = 4
        InitialBackoff = TimeSpan.FromSeconds 15.0
        MaxBackoff = TimeSpan.FromSeconds 120.0
        Timeout = Some(TimeSpan.FromSeconds 900.0)
        RateLimitFloor = None
    }

    let private parseBounded (name: string) (lo: int) (hi: int) (raw: string) : Result<int, string> =
        match Int32.TryParse(raw.Trim()) with
        | true, v when v >= lo && v <= hi -> Ok v
        | _ -> Error(sprintf "%s must be a whole number from %d to %d (got '%s')" name lo hi raw)

    /// The default policy with the two environment overrides applied.
    /// A malformed value is an `Error`, never a silent fall-back to the
    /// default: the release path should fail before it packs, not
    /// publish under a budget nobody chose.
    let policyFromEnv (getEnv: string -> string) : Result<RetryPolicy, string> =
        let read name lo hi =
            match getEnv name with
            | null
            | "" -> Ok None
            | raw -> parseBounded name lo hi raw |> Result.map Some

        match read TimeoutVariable 1 7200, read RetriesVariable 0 10 with
        | Error e, _
        | _, Error e -> Error e
        | Ok timeout, Ok retries ->
            Ok {
                defaultPolicy with
                    Timeout =
                        timeout
                        |> Option.map (float >> TimeSpan.FromSeconds)
                        |> Option.orElse defaultPolicy.Timeout
                    MaxAttempts =
                        retries
                        |> Option.map (fun r -> r + 1)
                        |> Option.defaultValue defaultPolicy.MaxAttempts
            }

    /// The `dotnet` argument list for one package's push.
    let pushArgs (policy: RetryPolicy) (source: string) (apiKey: string) (nupkg: string) : string list = [
        "nuget"
        "push"
        nupkg
        "--source"
        source
        "--api-key"
        apiKey
        "--skip-duplicate"
        match policy.Timeout with
        | Some t ->
            "--timeout"
            string (int (Math.Ceiling t.TotalSeconds))
        | None -> ()
    ]

    /// What the loop achieved. `Failed` is the package whose last
    /// attempt failed, with that attempt's exit code; `Unpushed` is that
    /// package followed by every package the loop never reached, in
    /// push order. Both are empty on a clean run.
    type PushOutcome = {
        Pushed: string list
        Failed: (string * int) option
        Unpushed: string list
    }

    /// Push `packages` in order. `push` runs one package's push and
    /// returns its exit code (0 = accepted or skipped as a duplicate);
    /// an exception from it counts as a failed attempt. Stops at the
    /// first package that fails on every attempt the policy allows.
    let pushAll
        (policy: RetryPolicy)
        (sleep: TimeSpan -> unit)
        (log: string -> unit)
        (push: string -> int)
        (packages: string list)
        : PushOutcome =
        let attempts = max 1 policy.MaxAttempts
        let name (p: string) = IO.Path.GetFileName p

        let attempt (pkg: string) (n: int) =
            try
                push pkg
            with ex ->
                log (sprintf "Push of %s threw on attempt %d of %d: %s" (name pkg) n attempts ex.Message)
                -1

        let rec tryPush (pkg: string) (n: int) : Result<unit, int> =
            if n > 1 then
                sleep (RetryPolicy.delayFor policy n)

            match attempt pkg n with
            | 0 ->
                if n > 1 then
                    log (sprintf "Pushed %s on attempt %d of %d (retry succeeded)" (name pkg) n attempts)

                Ok()
            | code when n < attempts ->
                log (
                    sprintf
                        "Push of %s failed (exit %d) on attempt %d of %d; retrying in %.0f s"
                        (name pkg)
                        code
                        n
                        attempts
                        (RetryPolicy.delayFor policy (n + 1)).TotalSeconds
                )

                tryPush pkg (n + 1)
            | code ->
                log (sprintf "Push of %s failed (exit %d) on its last attempt (%d of %d)" (name pkg) code n attempts)
                Error code

        let rec loop pushed remaining =
            match remaining with
            | [] -> {
                Pushed = List.rev pushed
                Failed = None
                Unpushed = []
              }
            | pkg :: rest ->
                match tryPush pkg 1 with
                | Ok() -> loop (pkg :: pushed) rest
                | Error code -> {
                    Pushed = List.rev pushed
                    Failed = Some(pkg, code)
                    Unpushed = remaining
                  }

        loop [] packages

    /// The failure message for an outcome with a `Failed` package: what
    /// failed, after how many attempts, and the full unpushed set.
    let remainderReport (policy: RetryPolicy) (outcome: PushOutcome) : string =
        let name (p: string) = IO.Path.GetFileName p

        match outcome.Failed with
        | None -> ""
        | Some(pkg, code) ->
            sprintf
                "Publish: %s failed on all %d attempt(s) (last exit %d). %d package(s) were pushed; %d are left unpushed, and a re-run of the publish pushes exactly these (the pushed ones are skipped by --skip-duplicate): %s"
                (name pkg)
                (max 1 policy.MaxAttempts)
                code
                outcome.Pushed.Length
                outcome.Unpushed.Length
                (outcome.Unpushed |> List.map name |> String.concat ", ")

/// Phase 1108 - the `KillStaleProcesses` target's stop, scoped to one project.
///
/// A leftover `esbuild` can hold file locks inside the project's client
/// `node_modules` and fail the next `npm ci`, but `esbuild` is also what Vite
/// runs under the hood, so stopping every `esbuild` by name stops other
/// projects' dev servers on a shared machine. This stops only the processes
/// whose main module lies under the given `node_modules` directory. A process
/// whose path cannot be read (access denied, already exited) is left alone and
/// reported through `log`.
///
/// `internal` for the reason `NugetPush` is: reached by the Build test pack
/// through `InternalsVisibleTo`, not part of the published surface.
module internal StaleEsbuild =

    open System.Diagnostics

    /// True when `path` lies strictly under `root`. Windows paths compare
    /// case-insensitively; elsewhere they compare ordinally.
    let isUnder (root: string) (path: string) =
        let comparison =
            if OperatingSystem.IsWindows() then
                StringComparison.OrdinalIgnoreCase
            else
                StringComparison.Ordinal

        let sep = string IO.Path.DirectorySeparatorChar
        let rootFull = IO.Path.GetFullPath root
        let rootDir = if rootFull.EndsWith sep then rootFull else rootFull + sep
        (IO.Path.GetFullPath path).StartsWith(rootDir, comparison)

    /// Stop every `esbuild` process whose executable lies under `nodeModules`;
    /// returns the number stopped.
    let stopUnder (nodeModules: string) (log: string -> unit) : int =
        let mutable stopped = 0

        for p in Diagnostics.Process.GetProcessesByName "esbuild" do
            use p = p

            let path =
                try
                    Some p.MainModule.FileName
                with e ->
                    log
                        $"KillStaleProcesses: left esbuild pid %d{p.Id} alone: its path could not be read (%s{e.Message})"

                    None

            match path with
            | Some path when isUnder nodeModules path ->
                try
                    p.Kill()
                    p.WaitForExit 5000 |> ignore
                    stopped <- stopped + 1
                    log $"KillStaleProcesses: stopped esbuild pid %d{p.Id} (%s{path})"
                with e ->
                    log $"KillStaleProcesses: could not stop esbuild pid %d{p.Id} (%s{e.Message})"
            | _ -> ()

        stopped

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
    /// populates this list with its in-tree Expecto runners, reconciled
    /// against the solution by `Gate.reconcile`.
    TestPacks: TestPack list
}

module BuildConfig =
    let defaults = {
        ServerProject = sprintf "src/%s/ToolupApp-Server.fsproj" PackScope.appServerDir
        ClientProject = sprintf "src/%s/ToolupApp-Client.fsproj" PackScope.appClientDir
        Output = BuildOutput.defaults
        Port = 5000
        TestPacks = []
    }

// ─── FAKE pipeline ─────────────────────────────────────────────────

/// The arguments `init` was given. The SDK's own targets read their options
/// from this one parse rather than from the process command line.
let mutable private invocation: string[] = [||]

let private invocationFlags () = Flags.lenient Flags.sdk invocation

/// Initialise the FAKE execution context
let init (argv: string[]) =
    invocation <- argv
    let execContext = Context.FakeExecutionContext.Create false "build.fsx" []
    Context.setExecutionContext (Context.RuntimeContext.Fake execContext)

/// Register all standard FAKE targets
let registerTargets (config: BuildConfig) =
    let serverPath = Path.getFullName (IO.Path.GetDirectoryName config.ServerProject)
    let clientPath = Path.getFullName (IO.Path.GetDirectoryName config.ClientProject)
    let deployPath = Path.getFullName config.Output.ServerPublishPath

    Target.create "Clean" (fun _ ->
        Shell.cleanDir deployPath
        run "dotnet" [ "fable"; "clean"; "--yes" ] clientPath)

    Target.create "KillStaleProcesses" (fun _ ->
        // Stop stale esbuild processes that may hold file locks in THIS project's
        // node_modules and so fail npm ci. Scoped by executable path (Phase 1108):
        // another project's Vite dev server runs an esbuild too, and is left alone.
        let nodeModules = IO.Path.Combine(clientPath, "node_modules")
        Trace.tracefn "Stopping stale esbuild processes under %s..." nodeModules
        StaleEsbuild.stopUnder nodeModules (Trace.tracefn "%s") |> ignore)

    Target.create "RestoreClientDependencies" (fun _ -> npm [ "ci"; "--include=optional" ] clientPath)

    Target.create "Build" (fun _ -> run "dotnet" [ "build" ] serverPath)

    Target.create "Bundle" (fun _ ->
        run "dotnet" [ "build"; config.ServerProject; "-c"; "Release" ] "."

        [
            "server", dotnetProcess [ "publish"; "-c"; "Release"; "-o"; deployPath ] serverPath
            "client",
            dotnetProcess
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
            dotnetProcess [ "watch"; "run"; "--no-restore" ] serverPath
            |> CreateProcess.setEnvironmentVariable "SERVER_PORT" (string config.Port)
            |> applyModuleFilter

        [
            "server", serverProcess
            "client",
            dotnetProcess
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

    Target.create "Docker" (fun _ -> run "docker" [ "compose"; "up"; "-d" ] ".")

    Target.create "Format" (fun _ -> run "dotnet" [ "fantomas"; "." ] ".")

    Target.create "VerifyAll" (fun _ ->
        // Canonical "run every Expecto test pack" aggregator. Each pack
        // is a console runner (`<OutputType>Exe</OutputType>` per the
        // forge convention); `dotnet test` silently no-ops against them,
        // hence this target exists to give operators + CI a single call
        // shape.
        //
        // Phase 731: the target BUILDS ONCE and then invokes each pack's
        // built dll directly, rather than shelling `dotnet run --project`
        // per pack. See `TestPackHost` above for why (briefly: the
        // `dotnet run` launch path intermittently hangs before the suite
        // starts, and a hang is far more expensive than a failure because
        // nothing about it says which it is).
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
        // is appended to every pack invocation. (Before Phase 731 it went
        // after the `--` that separated `dotnet run`'s own arguments from
        // the app's; invoking the dll directly, every argument is already
        // the app's, so the separator has nothing left to separate and the
        // pack sees the identical argv.) Exists so CI
        // can run the suite with e.g. `--debug` (Expecto names each test
        // as it starts) when hunting a failure that only reproduces on a
        // runner — a killed run then names its last-started test in the
        // log. Empty/unset ⇒ byte-for-byte the previous invocation.
        let extraTestArgs =
            match System.Environment.GetEnvironmentVariable "TOOLUP_TEST_ARGS" with
            | null
            | "" -> []
            | v -> v.Split(' ', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

        // The gate manifest's rows, in registration order: a repository
        // that registers rows (`Gate.register`) runs its selected `Local`
        // ones; a `TestPacks` entry no row names is a pack row with the
        // default reads, so a consumer's `BuildConfig` alone is a manifest.
        // Each row is one line in the summary block.
        let rows = Gate.registered ()

        let packRows =
            rows
            |> List.choose (fun r ->
                match r.Kind with
                | LegKind.ExpectoPack project -> Some(project, r)
                | LegKind.Body _
                | LegKind.Script _ -> None)
            |> Map.ofList

        let unregisteredPacks: Gate.Registered list = [
            for pack in config.TestPacks do
                if not (packRows.ContainsKey pack.Project) then
                    ({
                        Name = pack.Name
                        Kind = LegKind.ExpectoPack pack.Project
                        Where = set [ Site.Local ]
                        Reads = [ Input.Tree ]
                        Budget = None
                        Floor = None
                        Selected = true
                    }
                    : Gate.Registered)
        ]

        let allRows = unregisteredPacks @ rows

        let toRun =
            allRows |> List.filter (fun r -> r.Selected && Set.contains Site.Local r.Where)

        let elsewhere =
            allRows |> List.filter (fun r -> not (Set.contains Site.Local r.Where))

        let packs = [
            for r in toRun do
                match r.Kind with
                | LegKind.ExpectoPack project -> TestPack.create r.Name project
                | LegKind.Body _
                | LegKind.Script _ -> ()
        ]

        let readHighWater (file: string) = int ((IO.File.ReadAllText file).Trim())

        let clock = Diagnostics.Stopwatch.StartNew()

        match toRun with
        | [] ->
            Trace.tracefn
                "VerifyAll: BuildConfig.TestPacks is empty — nothing to run. Populate `TestPacks` in your `BuildConfig` to opt in."
        | _ ->
            // Outside the aggregate on purpose. The aggregate's whole
            // value is "every leg ran, here is what each did"; a failed
            // build means no leg CAN run, so folding it in as a leg would
            // produce a summary of thirteen packs that never started plus
            // one that never was one. It also keeps the summary's PASS /
            // FAIL line count exactly one-per-pack, which CI counts
            // against its EXPECTED_PACKS floor.
            if not (List.isEmpty packs) then
                TestPackHost.buildOnce packs

            let legs =
                toRun
                |> List.map (fun row ->
                    Aggregate.legOf row.Name (fun () ->
                        let verdict =
                            match row.Kind with
                            | LegKind.ExpectoPack project ->
                                TestPackHost.runPack
                                    extraTestArgs
                                    (Gate.scrubbedFor allRows row)
                                    (TestPack.create row.Name project)
                            | LegKind.Body run -> run ()
                            | LegKind.Script path ->
                                Gate.verdictOfExit (Proc.exec "pwsh" [ "-NoProfile"; "-File"; path ] ".").ExitCode

                        Gate.judge row.Name row.Floor readHighWater verdict))

            try
                Aggregate.runAll "VerifyAll" "pack" legs
            finally
                // The trailer: the rows this run did not run because they
                // run elsewhere, each with the inputs that put it there —
                // what a green here does not cover — and the one measured
                // duration, printed once, where `GateTimings` folds it from.
                if not (List.isEmpty elsewhere) then
                    Trace.tracefn "VerifyAll did not run %d row(s) that run elsewhere:" elsewhere.Length

                    for row in elsewhere do
                        Trace.tracefn
                            "  %s — %s; reads %s"
                            row.Name
                            (match row.Where |> Set.toList |> List.map Gate.siteName with
                             | [] -> "operator-run"
                             | sites -> String.concat ", " sites)
                            (match row.Reads with
                             | [] -> "nothing"
                             | reads -> reads |> List.map Gate.inputName |> String.concat ", ")

                let elapsed = clock.Elapsed

                Trace.tracefn
                    "VerifyAll: %d row(s) in %d:%02d wall clock on this run."
                    toRun.Length
                    (int elapsed.TotalMinutes)
                    elapsed.Seconds)

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
            PackScope.excludedDirs
            |> List.fold (fun g dir -> g -- sprintf "src/%s/**/*.fsproj" dir) !!"src/**/*.fsproj"

        // Pack every project, then fail at the end — the `VerifyAll`
        // shape above, for the same reason. A packaging defect in one
        // project (a missing packed README, a bad PackagePath) is
        // independent of every other project, so aborting on the first
        // one hides the rest and turns a single fix-and-rerun cycle
        // into one round trip per defect. The target still fails, and
        // the summary names every project that did not pack.
        let results = ResizeArray<string * int>()

        // Unchecked, so a non-zero exit is captured rather than thrown.
        let packProject proj =
            Proc.exec "dotnet" [ "pack"; proj; "-c"; "Release"; "-o"; outputDir; "--nologo" ] "."

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
        //
        // Phase 1016: each push runs under a longer `--timeout` and is
        // retried with backoff (`NugetPush` above). The budget is read
        // before anything is packed, so a malformed override fails in
        // seconds rather than after the pack.
        let pushPolicy =
            match NugetPush.policyFromEnv System.Environment.GetEnvironmentVariable with
            | Ok p -> p
            | Error e -> failwithf "Publish: %s" e

        let artifactsDir = Path.getFullName "./artifacts"
        Shell.cleanDir artifactsDir

        let projects =
            PackScope.excludedDirs
            |> List.fold (fun g dir -> g -- sprintf "src/%s/**/*.fsproj" dir) !!"src/**/*.fsproj"

        for proj in projects do
            Trace.tracefn "Packing %s for publish..." proj

            run "dotnet" [ "pack"; proj; "-c"; "Release"; "-o"; artifactsDir; "--nologo" ] "."

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

        Trace.tracefn
            "Pushing %d package(s) to %s (timeout %s per push, up to %d attempt(s) each)..."
            producedNupkgs.Length
            source
            (ToolUp.Platform.Transport.RetryPolicy.timeoutDescription pushPolicy)
            pushPolicy.MaxAttempts

        let pushOne (nupkg: string) =
            Trace.tracefn "Pushing %s..." (System.IO.Path.GetFileName nupkg)

            let result =
                Proc.exec "dotnet" (NugetPush.pushArgs pushPolicy source token nupkg) "."

            result.ExitCode

        let outcome =
            NugetPush.pushAll
                pushPolicy
                (fun delay -> System.Threading.Thread.Sleep delay)
                (Trace.tracefn "%s")
                pushOne
                producedNupkgs

        match outcome.Failed with
        | Some _ -> failwith (NugetPush.remainderReport pushPolicy outcome)
        | None -> Trace.tracefn "Publish: pushed all %d package(s)." outcome.Pushed.Length)

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

        let listed =
            Proc.execWith
                {
                    Proc.options with
                        Redirect = true
                        TraceCommand = false
                }
                "dotnet"
                [ "list"; slnFile; "package"; "--include-transitive"; "--format"; "json" ]
                "."

        if listed.ExitCode <> 0 then
            failwithf "dotnet list package failed (exit %d): %s" listed.ExitCode listed.StdErr

        let stdout = listed.StdOut

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
        //   Client/Remoting/                  Fable.Remoting fork (MIT), except the
        //                                     generated PlatformClientProxies.fs
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
            // Generated by ToolUp.Remoting.Generator (Phase 853) — forge-native
            // code that lives beside the fork, so it keeps the Apache-2.0 header.
            "Client/Remoting/PlatformClientProxies.fs", forgeNativeHeader
            "Client/Remoting/", remotingForkHeader
            "Shared/Remoting/MsgPack/", remotingForkHeader
        ]

        let chooseHeader (path: string) =
            let normalized = path.Replace('\\', '/')

            licenseMap
            |> List.tryFind (fun (prefix, _) -> normalized.Contains(prefix))
            |> Option.map snd
            |> Option.defaultValue forgeNativeHeader

        let checkOnly = Flags.has "--check" (invocationFlags ())

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

    // Phase 183 — the consumer codemod for the Epoch-1 0.x breaking
    // renames (Phase 73 `Fable.Remoting.*` / `Elmish` namespaces, Phase 66
    // `PlatformMode` → Surfaces, Phase 11.C.5 package-id + interface
    // renames). The deciding half is `Codemod` in SDK.Codemod.fs — rules
    // as data, per-file rewrite, walk, renderings — and is FAKE-free and
    // process-free so the golden-file pack decides every rule; this
    // target is the shell around it: argument parsing, the writes, and
    // the Fantomas pass the formatting mandate requires of generated F#.
    //
    //   dotnet run -- Codemod <dir>            rewrite in place; report the
    //                                          sites left for human review;
    //                                          then `dotnet fantomas` the
    //                                          rewritten .fs / .fsx files
    //   dotnet run -- Codemod <dir> --check    print the diff + the review
    //                                          list, write nothing; exit 1
    //                                          while rewrites are pending
    //                                          (the AddHeaders --check shape)
    //
    // Idempotent: a second run over migrated source plans zero rewrites
    // and runs no formatter. The review list never fails the run — it is
    // the report of what the codemod deliberately did not guess at.
    // Opt-in: a consumer who never runs it is byte-for-byte unaffected
    // (GP 13); the SDK runtime surface is untouched.
    Target.create "Codemod" (fun _ ->
        let flags = invocationFlags ()
        let checkOnly = Flags.has "--check" flags

        // The directory is the first non-option argument after the target.
        let root = List.tryHead flags.Positionals

        // `--phase 877` applies one migration's rules only — what a consumer
        // adopting one release wants, and what the SDK's own tree needs
        // when the rename it carries lands (every other rule would then
        // rewrite the names the SDK itself still defines).
        let only =
            match Flags.value "--phase" flags with
            | None -> []
            | Some phase ->
                match Codemod.migrationOfPhase phase with
                | Some m -> [ m ]
                | None -> failwithf "Codemod: no migration is labelled Phase %s" phase

        match root with
        | None -> failwith "Codemod: usage — dotnet run -- Codemod <consumer-source-dir> [--check] [--phase <n>]"
        | Some dir ->
            let plan = Codemod.planFor only (System.IO.Path.GetFullPath dir)
            Trace.tracefn "%s" (Codemod.renderSummary plan)
            Trace.tracefn "%s" (Codemod.renderFindings plan)

            if checkOnly then
                Trace.tracefn "%s" (Codemod.renderDiff plan)

                match plan.ChangedFiles with
                | [] -> ()
                | changed ->
                    failwithf
                        "Codemod --check: %d file(s) still carry pre-rename source (diff above). Re-run without --check to apply."
                        changed.Length
            else
                let written = Codemod.apply plan

                for path in written do
                    Trace.tracefn "  rewrote %s" path

                let sources =
                    written
                    |> List.filter (fun p -> Codemod.classify p = Some CodemodFileClass.Source)

                match sources with
                | [] -> Trace.tracefn "Codemod: no .fs / .fsx rewritten — nothing to format."
                | files ->
                    // The formatting mandate applies to generated F# too:
                    // the consumer's diff must be format-clean, so the
                    // rewritten sources go through the consumer's own
                    // Fantomas (the tool manifest resolves from the cwd,
                    // which is the consumer's Build project). A missing
                    // tool is named rather than swallowed — the files ARE
                    // rewritten at this point, and the remedy is one
                    // command.
                    try
                        run "dotnet" ("fantomas" :: files) "."
                    with e ->
                        failwithf
                            "Codemod: %d file(s) were rewritten but `dotnet fantomas` failed (%s). Run `dotnet tool restore` then `dotnet fantomas <the rewritten files>` to finish the format pass."
                            files.Length
                            e.Message)

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
        let flags = Flags.lenient Flags.sdk argv

        // `--module` is propagated as an environment variable.
        match Flags.value "--module" flags with
        | Some m -> Environment.SetEnvironmentVariable("TOOLUP_MODULE", m)
        | None -> ()

        Target.runOrDefault flags.Target

        0
    with e ->
        printfn "%A" e
        1