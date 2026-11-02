// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The gate lane, read from `TOOLUP_TEST_LANE` once per
/// process.
///
/// The full gate is tens of minutes of wall-clock — every pack plus the
/// legs, the `Samples` leg among them — which a dispatched
/// worker has to spend before it can know whether its own change compiles
/// green. A lane lets it spend minutes on its own work first and the full
/// gate ONCE, on the folded tree — the citable run. No figure is restated
/// here: `VerifyAll` prints the measured one at the end of every run, and
/// `dotnet run -- GateTimings` folds the recorded runs.
///
/// **The lane is an environment variable, not a `verify.ps1` switch, and
/// that is deliberate.** `verify.ps1` is a hard-denied path on the forge
/// roadmap side (`RM-FOOTPRINT-FORBIDDEN` — the gate script sits beneath
/// approval), so a `-Lane` parameter would move the recorded gate-script
/// hash and could not be authored at all. The variable rides INSIDE the
/// recorded `--gate-cmd` string instead
/// (`$env:TOOLUP_TEST_LANE='fast'; pwsh ./verify.ps1`), so the evidence
/// stays self-describing and the gate file's hash never moves.
///
/// **Why this file lives under `ToolUp.Platform.Tests/Support/` and is
/// source-linked into `Build.fsproj`.** There are two readers of the
/// variable — the `VerifyAll` driver (which selects PACKS) and the
/// Platform pack's own entry point (which selects LISTS) — and they sit
/// in assemblies that cannot reference each other. Two independent
/// parsers of one variable drift, and a lane that means different things
/// to the driver and the pack is worse than no lane, so there is exactly
/// one parser and `Build.fsproj` links it. The namespace is
/// `ToolUp.Forge` (forge's own build hygiene, as `SdkManifest.fs` is)
/// rather than the tests namespace it is filed under. It takes no
/// dependency beyond `System` for the same reason: `Build.fsproj` has no
/// Expecto, and the Expecto-shaped half lives in `TestLaneFilter.fs`
/// beside it, which only the pack compiles.
///
/// The `affected` lane adds, and with it two more things that
/// must be read the same way on both sides of that assembly boundary: the
/// lists file `Select` writes and the pack reads (`Lists`), and the
/// selection itself, which `Select` computes and `VerifyAll` summarises
/// (`Selection`). They live here for the same one-parser reason, and the
/// Build pack source-links this file to pin them.
module ToolUp.Forge.TestLane

open System

/// Which lane this process is running.
[<RequireQualifiedAccess>]
type Lane =
    /// Only the suites DECLARED to touch no filesystem, process, socket
    /// or environment variable. Seconds. Membership is opt-IN at both
    /// levels — an unaudited pack or list is never in this lane — so the
    /// lane can under-claim but never over-claim.
    | Pure
    /// Everything except the measured slow set. The pre-merge lane.
    | Fast
    /// Each pack of the Platform family keeps only the lists named for it
    /// in the file `TOOLUP_TEST_LISTS` points at (written by `Select`); every
    /// other pack runs whole unless the graph source skipped it, and the
    /// Fable and Samples legs follow the diff. A fail-fast lane, never a
    /// citable one: a ship cites `full`.
    | Affected
    /// The whole suite. The default, and the only lane a ship may cite.
    | Full

/// The variable this module reads. Named once so a caller reporting it
/// (a warning, a `--help` line, a recorded gate command) cannot misspell
/// it independently of the reader.
[<Literal>]
let VariableName = "TOOLUP_TEST_LANE"

/// The spelling of a lane in the variable and in operator prose.
let name (lane: Lane) =
    match lane with
    | Lane.Pure -> "pure"
    | Lane.Fast -> "fast"
    | Lane.Affected -> "affected"
    | Lane.Full -> "full"

/// Every accepted spelling, for an error message that tells the reader
/// what to type instead of only that they were wrong.
let accepted = [ Lane.Pure; Lane.Fast; Lane.Affected; Lane.Full ] |> List.map name

/// Parse one variable value. Unset / empty is `Full` — the default lane
/// is the pre-phase behaviour, so a machine that has never heard of this
/// variable runs exactly the gate it ran before.
///
/// An UNRECOGNISED value is an `Error`, and the caller resolves it to
/// `Full` with a warning rather than to a reduced lane. That direction is
/// load-bearing: a typo resolving to `full` costs minutes, whereas a typo
/// resolving to `fast` would report a green over a suite the operator did
/// not choose — and the operator would read it as the full run they
/// asked for.
let parse (raw: string) : Result<Lane, string> =
    match (if isNull raw then "" else raw.Trim().ToLowerInvariant()) with
    | "" -> Ok Lane.Full
    | "pure" -> Ok Lane.Pure
    | "fast" -> Ok Lane.Fast
    | "affected" -> Ok Lane.Affected
    | "full" -> Ok Lane.Full
    | other -> Error other

/// The lane this process runs, read once. Module-level, so the variable
/// is read exactly once however many times a caller asks — a lane that
/// could change mid-run would make the pack's own count of what it
/// excluded a lie.
let current: Lane =
    match parse (Environment.GetEnvironmentVariable VariableName) with
    | Ok lane -> lane
    | Error other ->
        eprintfn
            "WARNING: %s=%s is not a lane; running the FULL suite. Accepted: %s."
            VariableName
            other
            (String.Join(", ", accepted))

        Lane.Full

/// One line naming the lane and what it left out, printed ONLY when the
/// lane is not `full`. The default lane must leave the gate's output
/// exactly as it was, so an unset variable prints nothing at all — the
/// full gate is byte-for-byte the run it was before this phase.
let announce (whatWasExcluded: string) =
    if current <> Lane.Full then
        printfn "%s=%s — %s" VariableName (name current) whatWasExcluded

/// The variable naming the `affected` lane's lists file.
[<Literal>]
let ListsVariableName = "TOOLUP_TEST_LISTS"

/// The lists file: one top-level list of the Platform family per line, by
/// its label or by the binding a pack's `Program.fs` registers, qualified
/// `<row>/` for every member but the residue. `Select` writes it and each
/// member reads its share, so the format has one parser.
///
/// Two lines are not names. `# manifest: <path>` points at the selection
/// manifest the file was written beside, so `VerifyAll` can say what the
/// selection was; other `#` lines are comments. A line holding only `*`
/// keeps every list — `Select` writes it when a forcing path fired, so the
/// pack runs whole under the same lane rather than through a second
/// mechanism. A file naming nothing at all is refused by its reader: a
/// selection of nothing is the silent empty run this lane must never be.
module Lists =

    [<Literal>]
    let ManifestHeader = "# manifest: "

    [<Literal>]
    let Everything = "*"

    /// A parsed lists file.
    type File = {
        /// The manifest the file was written beside, when it says so.
        Manifest: string option
        /// `*` was given: keep every list.
        Everything: bool
        /// The names, in file order, without duplicates.
        Names: string list
    }

    let parse (text: string) : File =
        let all = text.Replace("\r\n", "\n").Split('\n') |> Array.map (fun l -> l.Trim())

        let manifest =
            all
            |> Array.tryPick (fun l ->
                if l.StartsWith(ManifestHeader.Trim(), StringComparison.Ordinal) then
                    Some(l.Substring(ManifestHeader.Trim().Length).Trim())
                else
                    None)

        let entries =
            all
            |> Array.filter (fun l -> l <> "" && not (l.StartsWith("#", StringComparison.Ordinal)))
            |> List.ofArray

        {
            Manifest = manifest
            Everything = List.contains Everything entries
            Names = entries |> List.filter ((<>) Everything) |> List.distinct
        }

    let render (manifest: string option) (everything: bool) (names: string list) : string =
        let header = [
            yield "# The Platform lists the affected lane keeps, one per line."
            match manifest with
            | Some m -> yield ManifestHeader + m
            | None -> ()
        ]

        let body = if everything then [ Everything ] else names
        String.Join("\n", header @ body) + "\n"

    /// The residue pack's row: the umbrella's pack, which held every list
    /// before the Platform pack was partitioned by server part.
    [<Literal>]
    let ResidueRow = "Platform"

    /// A name qualified by the Platform-family row that holds it:
    /// `Platform/<list>` or `Platform.<Part>/<list>`.
    let private qualified =
        Text.RegularExpressions.Regex(@"^(Platform(?:\.[A-Za-z]+)?)/(.+)$")

    /// The share of `file` the Platform-family pack `row` reads. A name
    /// qualified `<row>/<list>` belongs to that row, and `<row>/*` keeps the
    /// whole pack; an unqualified name belongs to the residue, where every
    /// name lived before the partition, so a lists file written then still
    /// means what it meant. A pack the file names nothing of keeps nothing
    /// and runs its registration guard alone.
    let forPack (row: string) (file: File) : File =
        if file.Everything then
            file
        else
            let mine = [
                for n in file.Names do
                    let m = qualified.Match n

                    if m.Success then
                        if m.Groups[1].Value = row then
                            yield m.Groups[2].Value
                    elif row = ResidueRow then
                        yield n
            ]

            {
                file with
                    Everything = List.contains Everything mine
                    Names = mine |> List.filter ((<>) Everything) |> List.distinct
            }

    /// The file `TOOLUP_TEST_LISTS` names, read and parsed. An unset
    /// variable, a missing file and a file naming nothing are each an
    /// `Error` saying which: the lane has no meaning without its lists.
    let read () : Result<string * File, string> =
        match Environment.GetEnvironmentVariable ListsVariableName with
        | null
        | "" ->
            Error(
                sprintf
                    "%s=affected needs %s=<file> (write one with `dotnet run --project Build.fsproj -- Select`)."
                    VariableName
                    ListsVariableName
            )
        | path when not (IO.File.Exists path) -> Error(sprintf "%s=%s: no such file." ListsVariableName path)
        | path ->
            let file = parse (IO.File.ReadAllText path)

            if file.Everything || not file.Names.IsEmpty then
                Ok(path, file)
            else
                Error(
                    sprintf
                        "%s=%s names no list — a selection of nothing is refused, never run as an empty green."
                        ListsVariableName
                        path
                )

/// What `Select` computes: the affected lane's unit set over a
/// diff, by source, with the forcing paths that turn it into the full lane
/// and the leg decisions; and the manifest it writes in the shape of the
/// selected-lane gate record a gate runner can cite. Pure over the paths
/// and a file reader, so the Build pack decides every rule without git.
///
/// There is no coverage map yet, so the derived source is
/// always empty and every manifest says `map: none`. The Platform pack is
/// partitioned by server part, so the declared and diffed sources read every
/// family member's registration, and the graph source (`--source graph`)
/// selects whole packs by the project graph instead.
module Selection =

    open System.Text.RegularExpressions

    /// The residue pack of the Platform family: the umbrella's pack, which
    /// held every list before the partition.
    [<Literal>]
    let PlatformPack = "Platform"

    [<Literal>]
    let PlatformTestRoot = "src/ToolUp.Platform.Tests/"

    /// The residue's registration. Deliberately NOT a forcing path, nor is
    /// any family member's: every phase that adds a list touches one, and the
    /// registration guard runs in every lane.
    [<Literal>]
    let PlatformProgram = "src/ToolUp.Platform.Tests/Program.fs"

    /// The members of the Platform family among `packs` (row, project): the
    /// projects named `ToolUp.Platform.Tests` or `ToolUp.Platform.Tests.<Part>`,
    /// each with the `Program.fs` beside its project file that registers its
    /// lists.
    let familyOf (packs: (string * string) list) : (string * string) list =
        packs
        |> List.choose (fun (row, project) ->
            let p = project.Replace('\\', '/')
            let file = IO.Path.GetFileNameWithoutExtension p

            if
                file = "ToolUp.Platform.Tests"
                || file.StartsWith("ToolUp.Platform.Tests.", StringComparison.Ordinal)
            then
                Some(row, p.Substring(0, p.LastIndexOf '/' + 1) + "Program.fs")
            else
                None)

    /// The registration guard's own list. It is appended beside the
    /// registered lists rather than registered, so it runs in every lane
    /// without being named in a lists file.
    [<Literal>]
    let RegistrationGuard = "test-registration guard (Phase 722)"

    /// The Platform lists every selection keeps, by registered binding.
    let alwaysOnLists = [ "ToolUp.Platform.Tests.InProcess.PublicApiApprovalTests.tests" ]

    /// Which source admitted a unit, in the selected-lane record's vocabulary.
    [<RequireQualifiedAccess>]
    type Source =
        | Declared
        | Diffed
        | Always
        /// A whole pack whose project closure meets the diff.
        | Graph

    let sourceName (source: Source) =
        match source with
        | Source.Declared -> "declared"
        | Source.Diffed -> "diffed"
        | Source.Always -> "always"
        | Source.Graph -> "graph"

    let private sourceOfName (raw: string) =
        match raw with
        | "declared" -> Some Source.Declared
        | "diffed" -> Some Source.Diffed
        | "always" -> Some Source.Always
        | "graph" -> Some Source.Graph
        | _ -> None

    type SelectedUnit = { Unit: string; Source: Source }

    /// A path rule that turns the selection into the full lane.
    type Rule = {
        Name: string
        Covers: string
        Matches: string -> bool
    }

    type Firing = { Rule: string; Path: string }

    /// One entry of the Platform pack's registered list, as `Program.fs`
    /// spells it.
    type Registration = {
        /// The entry's head, e.g. `LocalFileStorageTests.tests`.
        Text: string
        ModuleRef: string
        Binding: string
        /// Called with arguments (a contract pack instantiated per
        /// implementation): a fresh value per call, so no binding names it.
        Applied: bool
    }

    /// What the graph source is given: each pack row with its project file,
    /// and the case count each pack's last recorded run reported.
    type GraphInputs = {
        PackProjects: (string * string) list
        Cases: Map<string, int>
    }

    /// What the graph source selected.
    type GraphSelection = {
        /// The packs whose project closure meets a changed path, with their
        /// last recorded case count.
        Selected: (string * int option) list
        /// The packs that run nothing: no changed path is in their closure and
        /// no declared or always-on unit names them.
        Skipped: (string * int option) list
        /// Changed paths no project owns: they select no pack.
        Unowned: string list
    }

    /// What `Select` was asked.
    type Inputs = {
        Base: string option
        Head: string option
        /// Repository-relative, forward slashes.
        Paths: string list
        /// The declared units, one per line: `<pack>/<list>`, or a bare
        /// Platform list name.
        Declared: string list
        /// The VerifyAll pack names.
        Packs: string list
        /// The Platform family's members (row, its `Program.fs`); empty means
        /// the residue alone.
        Family: (string * string) list
        /// `Some` selects whole packs by the project graph (`--source graph`).
        Graph: GraphInputs option
        /// A repository-relative path to its text, `None` when absent.
        Read: string -> string option
        ListsFile: string
        Command: string
    }

    /// The selection manifest.
    type Manifest = {
        Base: string option
        Head: string option
        Paths: string list
        /// A forcing path fired: the run is the full lane.
        Forced: bool
        Units: SelectedUnit list
        /// Declared Platform names `Select` could not match to a
        /// registration by binding (a label, or a typo). The pack resolves
        /// them by label at run time and is red on one matching nothing.
        UnresolvedDeclared: string list
        Skipped: string list
        Checked: (string * string) list
        Fired: Firing list
        ClientTierMoved: bool
        /// The graph source's selection, when it was the source asked for.
        Graph: GraphSelection option
        /// What the lists file names (`*` aside).
        Lists: string list
        ListsFile: string
        Command: string
    }

    /// Repository-relative, forward slashes, no leading `./`.
    let normalise (path: string) =
        let p = path.Trim().Replace('\\', '/')

        if p.StartsWith("./", StringComparison.Ordinal) then
            p.Substring 2
        else
            p

    let private under (prefix: string) (path: string) =
        path.StartsWith(prefix, StringComparison.Ordinal)

    let private ext (path: string) =
        IO.Path.GetExtension(path).ToLowerInvariant()

    let private fileName (path: string) = IO.Path.GetFileName(path)

    /// The forcing paths, each a named rule so the manifest can say which
    /// fired. Each is pinned in both directions in `AffectedLaneTests`.
    let forcingRules: Rule list = [
        {
            Name = "msbuild-import"
            Covers = "any *.props or *.targets — an MSBuild import reaches every project"
            Matches = fun p -> ext p = ".props" || ext p = ".targets"
        }
        {
            Name = "solution"
            Covers = "a solution file (*.sln, *.slnx)"
            Matches = fun p -> ext p = ".sln" || ext p = ".slnx"
        }
        {
            Name = "build-script"
            Covers = "Build.fs, Build.fsproj and the repo-root sources the build script compiles, verify.ps1"
            Matches =
                fun p ->
                    not (p.Contains "/")
                    && (List.contains (ext p) [ ".fs"; ".fsx"; ".fsproj" ] || p = "verify.ps1")
        }
        {
            Name = "toolchain"
            Covers = "global.json, nuget.config"
            Matches =
                fun p ->
                    let f = (fileName p).ToLowerInvariant()
                    f = "global.json" || f = "nuget.config"
        }
        {
            Name = "composition-root"
            Covers = "the server composition root: ToolUp.Platform.Server's Compose/, ServerApp.fs, SDK.Server.fs"
            Matches =
                fun p ->
                    under "src/ToolUp.Platform.Server/Server/Compose/" p
                    || p = "src/ToolUp.Platform.Server/Server/ServerApp.fs"
                    || p = "src/ToolUp.Platform.Server/Server/SDK.Server.fs"
        }
        {
            Name = "server-client-config"
            Covers = "ServerConfig (SDK.Shared.fs, Shared/Config/) and ClientConfig (SDK.ClientTypes.fs)"
            Matches =
                fun p ->
                    p = "src/ToolUp.Platform.Core/Shared/SDK.Shared.fs"
                    || under "src/ToolUp.Platform.Core/Shared/Config/" p
                    || p = "src/ToolUp.Platform.Client/Client/SDK.ClientTypes.fs"
        }
        {
            Name = "module-registry"
            Covers = "the DI and module registries: a module's *Compose.fs, the client's RegisteredModules.fs"
            Matches =
                fun p ->
                    (under "src/" p && (fileName p).EndsWith("Compose.fs", StringComparison.Ordinal))
                    || p = "src/ToolUp.Platform.Client/Client/RegisteredModules.fs"
        }
        {
            Name = "test-support"
            Covers =
                "the test support tree: the Platform pack's Support/, ToolUp.Platform.Testing, a non-source file in the Platform pack"
            Matches =
                fun p ->
                    under (PlatformTestRoot + "Support/") p
                    || under "src/ToolUp.Platform.Testing/" p
                    || (under PlatformTestRoot p && ext p <> ".fs" && ext p <> ".fsproj")
        }
        {
            Name = "test-project"
            Covers = "a test project file (*.Tests.fsproj)"
            Matches = fun p -> ext p = ".fsproj" && (fileName p).Contains ".Tests"
        }
    ]

    /// Not a path rule: a Platform test file whose lists cannot be named.
    [<Literal>]
    let UnpinnedRule = "unpinned-test-source"

    let private unpinnedCovers =
        "a Platform test file whose lists cannot be named — registered only through an applied (argument-taking) entry, or defining no registered list (a helper other lists share)"

    let private projectSuffixesWithNoClientTier = [
        ".Server"
        ".Benchmarks"
        ".Build"
        ".Analyzers"
        ".Generator"
        ".Cli"
    ]

    /// Could this path change what Fable compiles? Conservative in the
    /// direction that costs minutes: anything under `samples/`, and any
    /// `src/` project not named as server-, build- or .NET-test-only.
    let clientTier (path: string) =
        if ext path = ".md" then
            false
        elif under "samples/" path then
            true
        elif under "src/" path then
            let project = path.Split('/')[1]
            let tests = project.EndsWith(".Tests", StringComparison.Ordinal)
            let fableTests = tests && (project.Contains "Fable" || project.Contains "Client")

            let serverOnly =
                projectSuffixesWithNoClientTier
                |> List.exists (fun s -> project.EndsWith(s, StringComparison.Ordinal))

            not ((tests && not fableTests) || serverOnly)
        else
            false

    let private stripComment (line: string) =
        match line.IndexOf("//", StringComparison.Ordinal) with
        | -1 -> line
        | i -> line.Substring(0, i)

    let private indentOf (line: string) = line.Length - line.TrimStart().Length

    let private startsWith (prefix: string) (s: string) =
        s.StartsWith(prefix, StringComparison.Ordinal)

    let private entryHead =
        Regex(@"^\s*([A-Z][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_']*)+)\s*(.*)$")

    let private inlineList =
        Regex(@"\btestList\s+""[^""]*""\s*\[\s*([^\[\]]+?)\s*\]\s*\)?$")

    /// The entries of `let private registeredTests = testList … [ … ]` in
    /// the pack's `Program.fs`, at the item level of each list (an entry
    /// inside a nested `testList` counts; an argument line does not).
    let registrations (programText: string) : Registration list =
        match programText.IndexOf("let private registeredTests", StringComparison.Ordinal) with
        | -1 -> []
        | start ->
            let rest = programText.Substring start

            let stop =
                match rest.IndexOf("\nlet ", 1, StringComparison.Ordinal) with
                | -1 -> rest.Length
                | i -> i

            let lines =
                rest.Substring(0, stop).Split('\n')
                |> Array.map (fun l -> (stripComment l).TrimEnd('\r', ' ', '\t'))

            let continues i ind =
                match lines |> Array.skip (i + 1) |> Array.tryFind (fun l -> l.Trim() <> "") with
                | Some n -> indentOf n > ind && not (startsWith "]" (n.Trim()))
                | None -> false

            let found = ResizeArray<Registration>()
            // The item indent of each open list, innermost first.
            let mutable openLists: int list = []
            let mutable pending = false

            for i in 0 .. lines.Length - 1 do
                let line = lines[i]
                let t = line.Trim()

                if t <> "" then
                    let ind = indentOf line
                    let closesEmpty = pending && startsWith "]" t

                    if pending && not closesEmpty then
                        openLists <- ind :: openLists

                    pending <- false

                    if startsWith "]" t then
                        if not closesEmpty && not openLists.IsEmpty then
                            openLists <- List.tail openLists
                    else
                        match openLists with
                        | itemIndent :: _ when ind = itemIndent ->
                            let m = entryHead.Match line

                            if m.Success then
                                let text = m.Groups[1].Value
                                let dot = text.LastIndexOf '.'

                                found.Add {
                                    Text = text
                                    ModuleRef = text.Substring(0, dot)
                                    Binding = text.Substring(dot + 1)
                                    Applied = m.Groups[2].Value <> "" || continues i ind
                                }
                        | _ -> ()

                    if t.EndsWith("[", StringComparison.Ordinal) then
                        pending <- true
                    else
                        // A list the formatter kept on one line: `testList "…" [ A.b; C.d ]`.
                        let inline' = inlineList.Match t

                        if inline'.Success then
                            for item in inline'.Groups[1].Value.Split(';') do
                                let m = entryHead.Match item

                                if m.Success && m.Groups[2].Value = "" then
                                    let text = m.Groups[1].Value
                                    let dot = text.LastIndexOf '.'

                                    found.Add {
                                        Text = text
                                        ModuleRef = text.Substring(0, dot)
                                        Binding = text.Substring(dot + 1)
                                        Applied = false
                                    }

            List.ofSeq found

    let private topModule = Regex(@"^module\s+(?:rec\s+)?([A-Za-z_][\w.]*)\s*$")

    let private namespaceDecl = Regex(@"^namespace\s+(?:rec\s+)?([A-Za-z_][\w.]*)\s*$")

    let private nestedModule =
        Regex(@"^(\s*)module\s+(?:rec\s+)?(?:(?:private|internal|public)\s+)?([A-Za-z_]\w*)\s*=")

    /// The fully-qualified modules a source file defines.
    let modulesDefinedIn (text: string) : string list =
        let found = ResizeArray<string>()
        let mutable outer: string option = None
        let mutable nested: (int * string) list = []

        for raw in text.Split('\n') do
            let line = raw.TrimEnd('\r')
            let top = topModule.Match line
            let ns = namespaceDecl.Match line
            let inner = nestedModule.Match line

            if top.Success then
                outer <- Some top.Groups[1].Value
                nested <- []
                found.Add top.Groups[1].Value
            elif ns.Success then
                outer <- Some ns.Groups[1].Value
                nested <- []
            elif inner.Success then
                let ind = inner.Groups[1].Value.Length
                nested <- nested |> List.filter (fun (i, _) -> i < ind)

                let parent =
                    match nested with
                    | (_, q) :: _ -> Some q
                    | [] -> outer

                let q =
                    match parent with
                    | Some p -> p + "." + inner.Groups[2].Value
                    | None -> inner.Groups[2].Value

                nested <- (ind, q) :: nested
                found.Add q

        List.ofSeq found

    // ─── The graph source ───────────────────────────────────────────────

    /// A project file as the graph source reads it: the files its items
    /// name, the directories its wildcard items cover, and the project
    /// files it references. Repository-relative, forward slashes.
    type ProjectFile = {
        Path: string
        /// The project file's directory, with a trailing `/`.
        Dir: string
        Items: Set<string>
        ItemDirs: string list
        References: string list
    }

    let private itemPattern =
        Regex(@"<(Compile|None|Content|EmbeddedResource|ProjectReference)\b[^>]*?\bInclude=""([^""]+)""")

    /// `relative` resolved against the repository-relative directory `dir`
    /// (`..` and `.` segments folded).
    let resolvePath (dir: string) (relative: string) : string =
        let stack = Collections.Generic.List<string>()

        for part in (dir + relative.Replace('\\', '/')).Split('/') do
            match part with
            | ""
            | "." -> ()
            | ".." ->
                if stack.Count > 0 then
                    stack.RemoveAt(stack.Count - 1)
            | s -> stack.Add s

        String.Join("/", stack)

    /// The project file at `path`, `None` when it cannot be read. An item
    /// spelled with an MSBuild property is not followed: the forcing rules
    /// cover the imports that define one.
    let projectFile (read: string -> string option) (path: string) : ProjectFile option =
        read path
        |> Option.map (fun text ->
            let dir =
                match path.LastIndexOf '/' with
                | -1 -> ""
                | i -> path.Substring(0, i + 1)

            let items = [
                for m in itemPattern.Matches text do
                    let value = m.Groups[2].Value

                    if not (value.Contains "$(") then
                        yield m.Groups[1].Value, value
            ]

            let isWild (s: string) = s.Contains "*" || s.Contains "%("
            let files = items |> List.filter (fun (kind, _) -> kind <> "ProjectReference")

            {
                Path = path
                Dir = dir
                Items =
                    files
                    |> List.filter (snd >> isWild >> not)
                    |> List.map (snd >> resolvePath dir)
                    |> set
                ItemDirs =
                    files
                    |> List.filter (snd >> isWild)
                    |> List.map (fun (_, glob) ->
                        let g = glob.Replace('\\', '/')
                        let first = g.IndexOfAny [| '*'; '%' |]
                        let cut = g.LastIndexOf('/', first)
                        let baseDir = if cut < 0 then "" else g.Substring(0, cut + 1)
                        (resolvePath dir baseDir).TrimEnd('/') + "/")
                References =
                    items
                    |> List.filter (fun (kind, _) -> kind = "ProjectReference")
                    |> List.map (snd >> resolvePath dir)
            })

    /// Every project reachable from `roots` through project references.
    let projectGraph (read: string -> string option) (roots: string list) : Map<string, ProjectFile> =
        let rec walk (seen: Map<string, ProjectFile>) (pending: string list) =
            match pending with
            | [] -> seen
            | p :: rest when seen.ContainsKey p -> walk seen rest
            | p :: rest ->
                match projectFile read p with
                | Some f -> walk (Map.add p f seen) (f.References @ rest)
                | None -> walk seen rest

        walk Map.empty roots

    /// `project` and every project it reaches through references.
    let closureOf (graph: Map<string, ProjectFile>) (project: string) : Set<string> =
        let rec walk (seen: Set<string>) (pending: string list) =
            match pending with
            | [] -> seen
            | p :: rest when seen.Contains p -> walk seen rest
            | p :: rest ->
                let refs =
                    match graph.TryFind p with
                    | Some f -> f.References
                    | None -> []

                walk (Set.add p seen) (refs @ rest)

        walk Set.empty [ project ]

    /// The projects a changed path belongs to: those whose items name it;
    /// failing that, those whose wildcard items cover it; failing that, the
    /// project whose directory holds it most closely. A source compiled from
    /// another project's directory (a server part's file under the umbrella's
    /// folder, a test file a part's pack compiles from the residue's) is the
    /// compiling project's, not the folder's.
    let ownersOf (graph: Map<string, ProjectFile>) (path: string) : Set<string> =
        let files = graph |> Map.toList |> List.map snd

        match files |> List.filter (fun f -> f.Items.Contains path) with
        | _ :: _ as named -> named |> List.map _.Path |> set
        | [] ->
            match
                files
                |> List.filter (fun f -> f.ItemDirs |> List.exists (fun d -> under d path))
            with
            | _ :: _ as covered -> covered |> List.map _.Path |> set
            | [] ->
                files
                |> List.filter (fun f -> f.Dir <> "" && under f.Dir path)
                |> List.sortByDescending _.Dir.Length
                |> List.tryHead
                |> Option.map (fun f -> Set.singleton f.Path)
                |> Option.defaultValue Set.empty

    /// The forcing rules the project graph cannot see past: an import, a
    /// solution, the build script, the toolchain. The graph source applies
    /// only these; every other rule is a path the graph already places.
    let graphBlindRules =
        set [ "msbuild-import"; "solution"; "build-script"; "toolchain" ]

    /// The selection over `inputs`.
    let select (inputs: Inputs) : Manifest =
        let paths =
            inputs.Paths |> List.map normalise |> List.filter ((<>) "") |> List.distinct

        let family =
            if inputs.Family.IsEmpty then
                [ PlatformPack, PlatformProgram ]
            else
                inputs.Family

        let familyRows = family |> List.map fst |> set
        let familyPrograms = family |> List.map snd |> set

        // Every family member's registration, each entry with its row.
        let regs =
            family
            |> List.collect (fun (row, program) ->
                registrations (inputs.Read program |> Option.defaultValue "")
                |> List.map (fun r -> row, r))

        let unitOf (row: string) (name: string) = row + "/" + name

        // The lists-file spelling: the residue's names unqualified, as before
        // the partition; every other member's `<row>/<name>`.
        let listOf (row: string) (name: string) =
            if row = PlatformPack then name else row + "/" + name

        let definedBy (q: string) (r: Registration) =
            q = r.ModuleRef || q.EndsWith("." + r.ModuleRef, StringComparison.Ordinal)

        // A name given as a registered binding: the full name, or the
        // spelling `Program.fs` uses.
        let matchesBinding (name: string) (r: Registration) =
            not r.Applied
            && (name = r.Text || name.EndsWith("." + r.Text, StringComparison.Ordinal))

        let byBinding (name: string) =
            regs |> List.filter (snd >> matchesBinding name)

        let byBindingIn (row: string) (name: string) =
            regs |> List.filter (fun (r, reg) -> r = row && matchesBinding name reg)

        let testSources =
            paths
            |> List.filter (fun p ->
                under PlatformTestRoot p
                && ext p = ".fs"
                && not (familyPrograms.Contains p)
                && not (under (PlatformTestRoot + "Support/") p))

        let diffed, unpinned =
            testSources
            |> List.fold
                (fun (hits, unpinned) p ->
                    match inputs.Read p with
                    // Deleted: its lists left the registration with it.
                    | None -> hits, unpinned
                    | Some text ->
                        let modules = modulesDefinedIn text

                        let matched =
                            regs
                            |> List.choose (fun (row, r) ->
                                modules
                                |> List.tryFind (fun q -> definedBy q r)
                                |> Option.map (fun q -> (row, r), q))

                        if matched.IsEmpty || matched |> List.exists (fun ((_, r), _) -> r.Applied) then
                            hits, unpinned @ [ p ]
                        else
                            hits @ matched, unpinned)
                ([], [])

        let graphMode = inputs.Graph.IsSome

        let fired = [
            for p in paths do
                for rule in forcingRules do
                    if rule.Matches p && (not graphMode || graphBlindRules.Contains rule.Name) then
                        yield { Rule = rule.Name; Path = p }
            if not graphMode then
                for p in unpinned do
                    yield { Rule = UnpinnedRule; Path = p }
        ]

        let packs = set inputs.Packs

        let declared =
            inputs.Declared
            |> List.map (fun d -> d.Trim())
            |> List.filter (fun d -> d <> "" && not (startsWith "#" d))
            |> List.distinct

        // `<pack>/<name>` names its pack; an unqualified name is the family
        // member that registers it, or the residue, which resolves a label.
        let split (d: string) =
            match d.IndexOf '/' with
            | i when
                i > 0
                && (packs.Contains(d.Substring(0, i)) || familyRows.Contains(d.Substring(0, i)))
                ->
                d.Substring(0, i), d.Substring(i + 1)
            | _ ->
                match byBinding d |> List.map fst |> List.distinct with
                | [ row ] -> row, d
                | _ -> PlatformPack, d

        let familyDeclared =
            declared
            |> List.choose (fun d ->
                match split d with
                | row, name when familyRows.Contains row -> Some(row, name)
                | _ -> None)

        // The always-on lists, each in the member that registers it.
        let alwaysOn =
            alwaysOnLists
            |> List.map (fun b ->
                match byBinding b |> List.map fst |> List.distinct with
                | [ row ] -> row, b
                | _ -> PlatformPack, b)

        let covered =
            set [
                yield! familyDeclared |> List.collect (fun (row, name) -> byBindingIn row name)
                yield! diffed |> List.map fst
                yield! alwaysOn |> List.collect (fun (row, b) -> byBindingIn row b)
            ]

        let diffedUnits = diffed |> List.map (fun ((row, r), q) -> row, q + "." + r.Binding)

        // The graph source: the packs whose project closure meets a changed
        // path run whole; a pack no source names runs nothing.
        let graph =
            inputs.Graph
            |> Option.map (fun g ->
                let projects = projectGraph inputs.Read (g.PackProjects |> List.map snd)

                let owned =
                    paths
                    |> List.filter (fun p ->
                        not (
                            forcingRules
                            |> List.exists (fun r -> graphBlindRules.Contains r.Name && r.Matches p)
                        ))
                    |> List.map (fun p -> p, ownersOf projects p)

                let touched = owned |> List.collect (snd >> Set.toList) |> set

                let named =
                    set [ yield! declared |> List.map (split >> fst); yield! alwaysOn |> List.map fst ]

                let cases (pack: string) = g.Cases.TryFind pack

                let reaches (_, project) =
                    not (Set.isEmpty (Set.intersect (closureOf projects project) touched))

                let selected, rest = g.PackProjects |> List.partition reaches

                {
                    Selected = selected |> List.map (fun (pack, _) -> pack, cases pack)
                    Skipped =
                        rest
                        |> List.filter (fun (pack, _) -> not (named.Contains pack))
                        |> List.map (fun (pack, _) -> pack, cases pack)
                    Unowned = owned |> List.filter (snd >> Set.isEmpty) |> List.map fst
                })

        let skippedPacks =
            match graph with
            | Some g -> g.Skipped |> List.map fst |> set
            | None -> Set.empty

        let units =
            [
                for d in declared do
                    let pack, name = split d

                    yield {
                        Unit = unitOf pack name
                        Source = Source.Declared
                    }
                for row, name in diffedUnits do
                    yield {
                        Unit = unitOf row name
                        Source = Source.Diffed
                    }
                match graph with
                | Some g ->
                    for pack, _ in g.Selected do
                        yield {
                            Unit = pack + "/*"
                            Source = Source.Graph
                        }
                | None -> ()
                for row, b in alwaysOn do
                    yield {
                        Unit = unitOf row b
                        Source = Source.Always
                    }
                for row, _ in family do
                    if not (skippedPacks.Contains row) then
                        yield {
                            Unit = unitOf row RegistrationGuard
                            Source = Source.Always
                        }
                if not graphMode then
                    for p in inputs.Packs do
                        if not (familyRows.Contains p) then
                            yield {
                                Unit = p + "/*"
                                Source = Source.Always
                            }
                yield {
                    Unit = "leg/Analyzers"
                    Source = Source.Always
                }
            ]
            |> List.distinctBy _.Unit

        let forced = not fired.IsEmpty

        let graphLists =
            match graph with
            | Some g ->
                g.Selected
                |> List.filter (fun (pack, _) -> familyRows.Contains pack)
                |> List.map (fun (pack, _) -> pack + "/*")
            | None -> []

        {
            Base = inputs.Base
            Head = inputs.Head
            Paths = paths
            Forced = forced
            Units = units
            UnresolvedDeclared =
                familyDeclared
                |> List.filter (fun (row, name) -> (byBindingIn row name).IsEmpty)
                |> List.map (fun (row, name) -> listOf row name)
            Skipped =
                if forced then
                    []
                else
                    match graph with
                    | Some g -> g.Skipped |> List.map (fun (pack, _) -> pack + "/*")
                    | None ->
                        regs
                        |> List.filter (fun r -> not (covered.Contains r))
                        |> List.map (fun (row, r) -> listOf row (if r.Applied then r.Text + " …" else r.Text))
            Checked =
                [ for r in forcingRules -> r.Name, r.Covers ]
                @ [ (UnpinnedRule, unpinnedCovers) ]
            Fired = fired
            ClientTierMoved = forced || List.exists clientTier paths
            Graph = graph
            Lists =
                (familyDeclared |> List.map (fun (row, name) -> listOf row name))
                @ (diffedUnits |> List.map (fun (row, name) -> listOf row name))
                @ graphLists
                @ (alwaysOn |> List.map (fun (row, b) -> listOf row b))
                |> List.distinct
            ListsFile = inputs.ListsFile
            Command = inputs.Command
        }

    let toJson (m: Manifest) : string =
        use stream = new IO.MemoryStream()

        let options =
            Text.Json.JsonWriterOptions(
                Indented = true,
                Encoder = Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            )

        do
            use w = new Text.Json.Utf8JsonWriter(stream, options)

            let strings (name: string) (values: string list) =
                w.WriteStartArray name
                values |> List.iter (fun (v: string) -> w.WriteStringValue v)
                w.WriteEndArray()

            let optional (name: string) (value: string option) =
                match value with
                | Some v -> w.WriteString(name, v)
                | None -> w.WriteNull name

            let pairs (name: string) (a: string) (b: string) (values: (string * string) list) =
                w.WriteStartArray name

                for x, y in values do
                    w.WriteStartObject()
                    w.WriteString(a, x)
                    w.WriteString(b, y)
                    w.WriteEndObject()

                w.WriteEndArray()

            w.WriteStartObject()
            w.WriteString("kind", "selectionManifest")
            optional "base" m.Base
            optional "head" m.Head
            strings "paths" m.Paths
            w.WriteString("lane", (if m.Forced then "full" else "affected"))
            pairs "units" "unit" "source" [ for u in m.Units -> u.Unit, sourceName u.Source ]
            strings "derived" []
            strings "unresolvedDeclared" m.UnresolvedDeclared
            strings "skipped" m.Skipped
            w.WriteBoolean("skippedExact", m.UnresolvedDeclared.IsEmpty)
            w.WriteStartObject "forcing"
            pairs "checked" "rule" "covers" m.Checked
            pairs "fired" "rule" "path" [ for f in m.Fired -> f.Rule, f.Path ]
            w.WriteEndObject()
            w.WriteStartObject "legs"
            w.WriteBoolean("fable", m.ClientTierMoved)
            w.WriteBoolean("samplesBuild", true)
            w.WriteBoolean("samplesTranspile", m.ClientTierMoved)
            w.WriteBoolean("analyzers", true)
            w.WriteEndObject()
            w.WriteString("map", "none")
            w.WriteString("source", (if m.Graph.IsSome then "graph" else "declared"))

            match m.Graph with
            | None -> w.WriteNull "graph"
            | Some g ->
                let packs (name: string) (values: (string * int option) list) =
                    w.WriteStartArray name

                    for pack, cases in values do
                        w.WriteStartObject()
                        w.WriteString("pack", pack)

                        match cases with
                        | Some c -> w.WriteNumber("cases", c)
                        | None -> w.WriteNull "cases"

                        w.WriteEndObject()

                    w.WriteEndArray()

                w.WriteStartObject "graph"
                packs "selected" g.Selected
                packs "skipped" g.Skipped
                strings "unowned" g.Unowned
                w.WriteEndObject()

            strings "lists" m.Lists
            w.WriteString("listsFile", m.ListsFile)
            w.WriteString("command", m.Command)
            w.WriteEndObject()

        Text.Encoding.UTF8.GetString(stream.ToArray()) + "\n"

    let ofJson (text: string) : Result<Manifest, string> =
        try
            use doc = Text.Json.JsonDocument.Parse text
            let root = doc.RootElement
            let prop (name: string) = root.GetProperty name
            let field (e: Text.Json.JsonElement) (name: string) = e.GetProperty(name).GetString()

            let optional (name: string) =
                match prop name with
                | e when e.ValueKind = Text.Json.JsonValueKind.Null -> None
                | e -> Some(e.GetString())

            let strings (e: Text.Json.JsonElement) = [ for x in e.EnumerateArray() -> x.GetString() ]

            let pairs (e: Text.Json.JsonElement) (a: string) (b: string) = [
                for x in e.EnumerateArray() -> field x a, field x b
            ]

            if field root "kind" <> "selectionManifest" then
                Error "not a selection manifest (its kind is not `selectionManifest`)"
            else
                let forcing = prop "forcing"

                let units = [
                    for unitName, source in pairs (prop "units") "unit" "source" ->
                        match sourceOfName source with
                        | Some s -> { Unit = unitName; Source = s }
                        | None -> failwithf "unit %s has an unknown source `%s`" unitName source
                ]

                Ok {
                    Base = optional "base"
                    Head = optional "head"
                    Paths = strings (prop "paths")
                    Forced = field root "lane" = "full"
                    Units = units
                    UnresolvedDeclared = strings (prop "unresolvedDeclared")
                    Skipped = strings (prop "skipped")
                    Checked = pairs (forcing.GetProperty "checked") "rule" "covers"
                    Fired = [
                        for rule, path in pairs (forcing.GetProperty "fired") "rule" "path" ->
                            { Rule = rule; Path = path }
                    ]
                    ClientTierMoved = prop("legs").GetProperty("fable").GetBoolean()
                    Graph =
                        match root.TryGetProperty "graph" with
                        | true, g when g.ValueKind = Text.Json.JsonValueKind.Object ->
                            let packs (name: string) = [
                                for x in g.GetProperty(name).EnumerateArray() ->
                                    let cases = x.GetProperty "cases"

                                    field x "pack",
                                    (if cases.ValueKind = Text.Json.JsonValueKind.Null then
                                         None
                                     else
                                         Some(cases.GetInt32()))
                            ]

                            Some {
                                Selected = packs "selected"
                                Skipped = packs "skipped"
                                Unowned = strings (g.GetProperty "unowned")
                            }
                        | _ -> None
                    Lists = strings (prop "lists")
                    ListsFile = field root "listsFile"
                    Command = field root "command"
                }
        with ex ->
            Error(sprintf "unreadable selection manifest: %s" ex.Message)

    /// The manifest as lines for a log: what ran by source, what was
    /// skipped, which forcing rule fired, the legs, and `map: none` — so a
    /// selected run's output cannot be mistaken for a full one.
    let summaryLines (m: Manifest) : string list =
        let bySource source =
            m.Units |> List.filter (fun u -> u.Source = source)

        let legNote =
            if m.ClientTierMoved then
                "Fable leg runs; Samples builds every sample and transpiles the Fable ones"
            else
                "Fable leg skipped and Samples builds every sample without transpiling (no client-tier path moved)"

        [
            yield "Selection manifest — map: none (no coverage map, so the derived source is empty)"
            match m.Base, m.Head with
            | Some b, Some h -> yield sprintf "  diff: %s..%s, %d path(s)" b h m.Paths.Length
            | _ -> yield sprintf "  paths: %d given (a prediction, not a diff)" m.Paths.Length
            if m.Forced then
                yield "  lane: FULL — a forcing path fired, so nothing is skipped:"

                for f in m.Fired do
                    yield sprintf "    %s: %s" f.Rule f.Path
            else
                yield sprintf "  lane: affected — %d forcing rule(s) checked, none fired" m.Checked.Length
            yield
                sprintf
                    "  units: %d declared, %d diffed, %d graph, %d always"
                    (bySource Source.Declared).Length
                    (bySource Source.Diffed).Length
                    (bySource Source.Graph).Length
                    (bySource Source.Always).Length
            for source in [ Source.Declared; Source.Diffed; Source.Graph; Source.Always ] do
                for u in bySource source do
                    yield sprintf "    %-8s %s" (sourceName source) u.Unit
            for d in m.UnresolvedDeclared do
                yield
                    sprintf
                        "    declared %s is not a registered binding: the pack resolves it by label, and is red if it names no list"
                        d
            match m.Graph with
            | Some g ->
                let count (cases: int option) =
                    match cases with
                    | Some c -> sprintf "%d case(s)" c
                    | None -> "cases unknown"

                yield
                    sprintf
                        "  source: graph — %d pack(s) whose project closure meets the diff run whole, %d run nothing"
                        g.Selected.Length
                        g.Skipped.Length

                for pack, cases in g.Selected do
                    yield sprintf "    selected %s (%s)" pack (count cases)

                for pack, cases in g.Skipped do
                    yield sprintf "    skipped  %s (%s)" pack (count cases)

                for p in g.Unowned do
                    yield sprintf "    unowned  %s (no project owns it, so it selects no pack)" p
            | None -> ()
            if not m.Forced && m.Graph.IsNone then
                yield
                    sprintf
                        "  skipped: %d registered Platform list(s)%s"
                        m.Skipped.Length
                        (if m.UnresolvedDeclared.IsEmpty then
                             ""
                         else
                             " (an upper bound: a declared label may keep one, and the pack prints the exact account)")

                for s in m.Skipped do
                    yield "    skipped  " + s
            yield "  legs: " + legNote + "; Analyzers always"
        ]

/// What `VerifyAll` reads under the `affected` lane.
type AffectedPlan = {
    ListsPath: string
    Lists: Lists.File
    /// The manifest the lists file names, when it names one.
    Manifest: Result<Selection.Manifest, string> option
}

/// Read the lists file and, when it names one, the manifest beside it.
let readAffectedPlan () : Result<AffectedPlan, string> =
    Lists.read ()
    |> Result.map (fun (path, lists) -> {
        ListsPath = path
        Lists = lists
        Manifest =
            lists.Manifest
            |> Option.map (fun m ->
                if IO.File.Exists m then
                    Selection.ofJson (IO.File.ReadAllText m)
                else
                    Error(sprintf "the manifest %s is not on disk" m))
    })