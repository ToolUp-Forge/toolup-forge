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
    /// The Platform pack keeps only the lists named in the
    /// file `TOOLUP_TEST_LISTS` points at (written by `Select`); every
    /// other pack runs whole, and the Fable and Samples legs follow the
    /// diff. A fail-fast lane, never a citable one: a ship cites `full`.
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

/// The lists file: one top-level Platform list per line, by
/// its label or by the binding the pack's `Program.fs` registers. `Select`
/// writes it and the Platform pack reads it, so the format has one parser.
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
/// always empty and every manifest says `map: none`.
module Selection =

    open System.Text.RegularExpressions

    /// The VerifyAll pack whose lists the lane selects.
    [<Literal>]
    let PlatformPack = "Platform"

    [<Literal>]
    let PlatformTestRoot = "src/ToolUp.Platform.Tests/"

    /// The pack's registration. Deliberately NOT a forcing path: every
    /// phase that adds a list touches it, and the registration guard runs
    /// in every lane.
    [<Literal>]
    let PlatformProgram = "src/ToolUp.Platform.Tests/Program.fs"

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

    let sourceName (source: Source) =
        match source with
        | Source.Declared -> "declared"
        | Source.Diffed -> "diffed"
        | Source.Always -> "always"

    let private sourceOfName (raw: string) =
        match raw with
        | "declared" -> Some Source.Declared
        | "diffed" -> Some Source.Diffed
        | "always" -> Some Source.Always
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

    /// The selection over `inputs`.
    let select (inputs: Inputs) : Manifest =
        let paths =
            inputs.Paths |> List.map normalise |> List.filter ((<>) "") |> List.distinct

        let regs = registrations (inputs.Read PlatformProgram |> Option.defaultValue "")

        let definedBy (q: string) (r: Registration) =
            q = r.ModuleRef || q.EndsWith("." + r.ModuleRef, StringComparison.Ordinal)

        // A name given as a registered binding: the full name, or the
        // spelling `Program.fs` uses.
        let byBinding (name: string) =
            regs
            |> List.filter (fun r ->
                not r.Applied
                && (name = r.Text || name.EndsWith("." + r.Text, StringComparison.Ordinal)))

        let testSources =
            paths
            |> List.filter (fun p ->
                under PlatformTestRoot p
                && ext p = ".fs"
                && p <> PlatformProgram
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
                            |> List.choose (fun r ->
                                modules |> List.tryFind (fun q -> definedBy q r) |> Option.map (fun q -> r, q))

                        if matched.IsEmpty || matched |> List.exists (fun (r, _) -> r.Applied) then
                            hits, unpinned @ [ p ]
                        else
                            hits @ matched, unpinned)
                ([], [])

        let fired = [
            for p in paths do
                for rule in forcingRules do
                    if rule.Matches p then
                        yield { Rule = rule.Name; Path = p }
            for p in unpinned do
                yield { Rule = UnpinnedRule; Path = p }
        ]

        let packs = set inputs.Packs

        let declared =
            inputs.Declared
            |> List.map (fun d -> d.Trim())
            |> List.filter (fun d -> d <> "" && not (startsWith "#" d))
            |> List.distinct

        let split (d: string) =
            match d.IndexOf '/' with
            | i when i > 0 && packs.Contains(d.Substring(0, i)) -> d.Substring(0, i), d.Substring(i + 1)
            | _ -> PlatformPack, d

        let platformDeclared =
            declared
            |> List.choose (fun d ->
                match split d with
                | pack, name when pack = PlatformPack -> Some name
                | _ -> None)

        let covered =
            set [
                yield! platformDeclared |> List.collect byBinding
                yield! diffed |> List.map fst
                yield! alwaysOnLists |> List.collect byBinding
            ]

        let diffedNames = diffed |> List.map (fun (r, q) -> q + "." + r.Binding)

        let units =
            [
                for d in declared do
                    let pack, name = split d

                    yield {
                        Unit = pack + "/" + name
                        Source = Source.Declared
                    }
                for n in diffedNames do
                    yield {
                        Unit = PlatformPack + "/" + n
                        Source = Source.Diffed
                    }
                for b in alwaysOnLists do
                    yield {
                        Unit = PlatformPack + "/" + b
                        Source = Source.Always
                    }
                yield {
                    Unit = PlatformPack + "/" + RegistrationGuard
                    Source = Source.Always
                }
                for p in inputs.Packs do
                    if p <> PlatformPack then
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

        {
            Base = inputs.Base
            Head = inputs.Head
            Paths = paths
            Forced = forced
            Units = units
            UnresolvedDeclared = platformDeclared |> List.filter (byBinding >> List.isEmpty)
            Skipped =
                if forced then
                    []
                else
                    regs
                    |> List.filter (fun r -> not (covered.Contains r))
                    |> List.map (fun r -> if r.Applied then r.Text + " …" else r.Text)
            Checked =
                [ for r in forcingRules -> r.Name, r.Covers ]
                @ [ (UnpinnedRule, unpinnedCovers) ]
            Fired = fired
            ClientTierMoved = forced || List.exists clientTier paths
            Lists = platformDeclared @ diffedNames @ alwaysOnLists |> List.distinct
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
                    "  units: %d declared, %d diffed, %d always"
                    (bySource Source.Declared).Length
                    (bySource Source.Diffed).Length
                    (bySource Source.Always).Length
            for source in [ Source.Declared; Source.Diffed; Source.Always ] do
                for u in bySource source do
                    yield sprintf "    %-8s %s" (sourceName source) u.Unit
            for d in m.UnresolvedDeclared do
                yield
                    sprintf
                        "    declared %s is not a registered binding: the pack resolves it by label, and is red if it names no list"
                        d
            if not m.Forced then
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