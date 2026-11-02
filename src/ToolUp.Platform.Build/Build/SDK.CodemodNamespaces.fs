// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

/// Phase 1198 — the server tier's namespaces name the part a file is
/// compiled into, and the consumer codemod that follows the move.
///
/// Phase 1193 cut `ToolUp.Platform.Server` into six part assemblies and
/// an umbrella without moving a namespace, so every one of the tier's
/// files still declared `namespace ToolUp.Platform` and a reader could
/// not tell a file's part from its source. This phase gives each part a
/// namespace: `ToolUp.Platform.Infra`, `.Config`, `.Scope`, `.Jobs`,
/// `.Audit`; the umbrella's `ToolUp.Platform.Compose`, with its five
/// file-management sources under `ToolUp.Platform.Files`; and the
/// Remoting part keeps the remoting library's `ToolUp.Remoting.Server`
/// (`ToolUp.Platform.Remoting` is a module of the client tier). A file's
/// part is read from the part project files, never from its folder, and
/// a file placed in a part other than the one its name says takes the
/// namespace of the part it is PLACED in, so a namespace never spans two
/// assemblies and the opens a consumer carries say which assemblies it
/// reaches.
///
/// The codemod half is table-driven. The table is scanned from the
/// tier's sources (`scan`), rendered to one text file that ships inside
/// `ToolUp.Platform.Build` as an embedded resource (`render` / `parse`),
/// and read by the rewrite pass (`rewrite`), which does three things to
/// a consumer file: rewrites an `open` of a path that moved, rewrites a
/// qualified `ToolUp.Platform.X` path that moved, and DERIVES the opens
/// the file needs from the symbols it mentions — never a blanket list of
/// every part — inserting them after the file's last `open`. A second
/// run finds every open in place and changes nothing.
module SubsystemNamespaces =

    // ── The parts and their namespaces ────────────────────────────────

    /// One part assembly of the server tier: its project (assembly) name
    /// and the namespace root its files take.
    type Part = {
        /// The project file name without extension — the assembly name.
        Project: string
        /// The namespace root the part's files declare.
        Root: string
    }

    /// The seven projects in the umbrella's dependency order, last the
    /// umbrella itself. The Remoting part is the remoting library's
    /// server half and keeps that library's namespace.
    let parts: Part list = [
        {
            Project = "ToolUp.Platform.Server.Remoting"
            Root = "ToolUp.Remoting.Server"
        }
        {
            Project = "ToolUp.Platform.Server.Infra"
            Root = "ToolUp.Platform.Infra"
        }
        {
            Project = "ToolUp.Platform.Server.Config"
            Root = "ToolUp.Platform.Config"
        }
        {
            Project = "ToolUp.Platform.Server.Scope"
            Root = "ToolUp.Platform.Scope"
        }
        {
            Project = "ToolUp.Platform.Server.Jobs"
            Root = "ToolUp.Platform.Jobs"
        }
        {
            Project = "ToolUp.Platform.Server.Audit"
            Root = "ToolUp.Platform.Audit"
        }
        {
            Project = "ToolUp.Platform.Server"
            Root = "ToolUp.Platform.Compose"
        }
    ]

    /// The umbrella's file-management sources take this namespace rather
    /// than the composition root's: Phase 1193 merged the five-file Files
    /// run into the umbrella, under the fifteen-file floor for a part.
    let filesRoot = "ToolUp.Platform.Files"

    /// The five sources, by file name (they sit under `Server/Files/` and
    /// `Server/`; the project file, not the folder, is what places them).
    let filesSources =
        set [
            "FileNameSanitiser.fs"
            "ProcessedDataCodec.fs"
            "FileManagement.fs"
            "SessionFileStoreInstanceValidator.fs"
            "ColumnMappingApiHandler.fs"
        ]

    /// Every namespace root a server file may declare after the move, in
    /// the order the derived opens are emitted (the parts' dependency
    /// order, then the umbrella's two).
    let roots: string list = (parts |> List.map _.Root) @ [ filesRoot ]

    let private platform = "ToolUp.Platform"
    let private remotingServer = "ToolUp.Remoting.Server"

    /// The declaration path a file's header takes once the file is placed
    /// in the part whose namespace root is `root`. `path` is the header's
    /// path as declared today (`""` for a file with no header);
    /// idempotent on a path already under `root`.
    let placed (root: string) (path: string) : string =
        if path = root || path.StartsWith(root + ".") then
            path
        elif root = remotingServer then
            // The remoting library's own namespaces stay; only a
            // platform-family header in the part moves, under
            // `ToolUp.Remoting.Server.Http` — its own sub-namespace, so a
            // consumer opening it for the body binder is not handed the
            // whole remoting server surface (whose `AuditEvent` shadows
            // Core's). A dotless internal module (the vendored TypeShape)
            // is not the platform's and stays where it is.
            if path = platform then
                root + ".Http"
            elif path.StartsWith(platform + ".") then
                root + ".Http" + path.Substring platform.Length
            else
                path
        elif path = "" then
            root
        elif path = platform then
            root
        elif path.StartsWith(platform + ".") then
            root + path.Substring platform.Length
        elif path.StartsWith "ToolUp.Remoting" then
            // A platform-family file declared in the remoting library's
            // namespace (one Jobs file did) takes its part's root.
            root
        elif not (path.Contains ".") then
            root + "." + path
        else
            path

    /// The six modules the tier declared with no namespace at all before
    /// the move (`module EncryptedSecretStore`): once placed under a part
    /// their old path is the bare name, which nothing else can tell from
    /// `ToolUp.Platform.<Name>`.
    let formerlyGlobal =
        set [
            "EncryptedSecretStore"
            "FileSecretStore"
            "HeaderAuthProvider"
            "StaticJwtAuthProvider"
            "LocalFileStorage"
            "EnvironmentSecretStore"
        ]

    /// The inverse of `placed`: the path a header declared BEFORE the move,
    /// read off a header that has already moved, so a scan of the tree
    /// after the move still records where each name came from (the table
    /// the consumer codemod rewrites old paths by). Identity on a path not
    /// under `root`, so a scan before the move reads the same table.
    let unplaced (root: string) (path: string) : string =
        let under (prefix: string) =
            if path.StartsWith(prefix + ".") then
                Some(path.Substring(prefix.Length + 1))
            else
                None

        if root = remotingServer then
            match under (root + ".Http") with
            | Some rest -> platform + "." + rest
            | None -> if path = root + ".Http" then platform else path
        elif path = root then
            platform
        else
            match under root with
            | Some rest when not (rest.Contains ".") && formerlyGlobal.Contains rest -> rest
            | Some rest -> platform + "." + rest
            | None -> path

    // ── Headers and declarations ──────────────────────────────────────

    /// A file's first `namespace` / `module` declaration.
    type Header = {
        /// `namespace` or `module`.
        Keyword: string
        /// The modifiers between the keyword and the path, as written
        /// (`rec `, `internal `), possibly empty.
        Modifiers: string
        /// The dotted path.
        Path: string
        /// 0-based index of the header line in the file.
        Line: int
        /// Whether `[<AutoOpen>]` precedes the header.
        AutoOpen: bool
    }

    let private headerRx =
        Regex(
            @"^(?<kw>namespace|module)\s+(?<mods>(?:rec\s+|internal\s+|private\s+|public\s+)*)(?<path>[A-Za-z_][\w.]*)\s*$",
            RegexOptions.CultureInvariant
        )

    let private autoOpenRx =
        Regex(@"\[<\s*AutoOpen\s*>\]", RegexOptions.CultureInvariant)

    /// The first header line of `lines`, if the file has one. A `module X =`
    /// with a body is a nested module, not a header, and does not match.
    let header (lines: string[]) : Header option =
        let rec go i =
            if i >= lines.Length then
                None
            else
                let m = headerRx.Match lines[i]

                if m.Success && not (lines[i].TrimStart().StartsWith "//") then
                    let autoOpen =
                        [ max 0 (i - 3) .. i - 1 ] |> List.exists (fun j -> autoOpenRx.IsMatch lines[j])

                    Some {
                        Keyword = m.Groups["kw"].Value
                        Modifiers = m.Groups["mods"].Value
                        Path = m.Groups["path"].Value
                        Line = i
                        AutoOpen = autoOpen
                    }
                else
                    go (i + 1)

        go 0

    /// The namespace a header's declarations sit in: the path itself for
    /// `namespace`, the path less its last segment for a top-level module
    /// (`""` for a dotless one).
    let namespaceOf (h: Header) : string =
        match h.Keyword with
        | "namespace" -> h.Path
        | _ ->
            match h.Path.LastIndexOf '.' with
            | -1 -> ""
            | i -> h.Path.Substring(0, i)

    /// What kind of name a table entry records.
    [<RequireQualifiedAccess>]
    type EntryKind =
        /// A namespace-level type, module or exception, or a top-level
        /// module: reachable unqualified once its namespace is open, and
        /// addressable as `<namespace>.<name>`.
        | Type
        /// A namespace-level type or module whose old path a source
        /// outside the tier still declares (`ToolUp.Platform.BlobStorage`
        /// is a Core module too): the old `open` stays, and the moved
        /// half is opened beside it when one of its members is named.
        | Shared
        /// A value, function, active pattern or type an `[<AutoOpen>]`
        /// module exposes, or a member of a shared module's moved half.
        | Member
        /// A union case of a namespace-level union. Record fields are
        /// deliberately not recorded: a field name is shared with Core's
        /// records far too often (`Decision`, `Name`, `Kind`) for a bare
        /// `Field = …` to say which record is being built.
        | Case
        /// A namespace that moved whole: every path under it moves too.
        | Namespace
        /// A namespace the tier's files left but that other sources still
        /// declare (`ToolUp.Platform.Metrics` is Core's too): a tier file
        /// now under the new namespace opens the old one to keep seeing
        /// the half that stayed.
        | Split

    /// One line of the table: a name, where it was, where it is.
    type Entry = {
        Kind: EntryKind
        /// The namespace (or, for `Namespace`, the full path) before the move.
        OldNamespace: string
        /// The unqualified name (`"*"` for a `Namespace` entry).
        Name: string
        /// The namespace (or path) after the move.
        NewNamespace: string
    }

    /// One file of the tier, as the map in the migration doc lists it.
    type FileMove = {
        /// The part project the file is compiled by.
        Project: string
        /// Path relative to the repository root, forward-slashed.
        Path: string
        /// The header as declared today (`""` for none).
        OldHeader: string
        /// The header after the move.
        NewHeader: string
    }

    let private declRx =
        Regex(
            @"^(?<indent>\s*)(?<kw>type|and|exception)\s+(?:\[<[^\]]*>\]\s*)*(?<vis>internal\s+|private\s+|public\s+)?(?<name>[A-Za-z_]\w*)",
            RegexOptions.CultureInvariant
        )

    let private nestedModuleRx =
        Regex(
            @"^(?<indent>\s*)module\s+(?<vis>(?:rec\s+|internal\s+|private\s+|public\s+)*)(?<name>[A-Za-z_]\w*)\s*=",
            RegexOptions.CultureInvariant
        )

    let private letRx =
        Regex(
            @"^(?<indent>\s*)let\s+(?<mods>(?:inline\s+|rec\s+|mutable\s+|internal\s+|private\s+)*)(?:\(\|(?<ap>[^)]*)\|\)|(?<name>[a-zA-Z_]\w*))",
            RegexOptions.CultureInvariant
        )

    let private caseRx =
        Regex(@"^\s{4}\|\s*(?:\[<[^\]]*>\]\s*)*(?<name>[A-Z]\w*)", RegexOptions.CultureInvariant)

    let private inlineCaseRx =
        Regex(@"\|\s*(?<name>[A-Z]\w*)", RegexOptions.CultureInvariant)

    let private indentOf (line: string) = line.Length - line.TrimStart().Length

    /// The names a file declares at the level a consumer reaches through
    /// its namespace, each with its kind, given the header. `entriesOf`
    /// reads the file's text only; the caller supplies where the names go.
    let declarations (lines: string[]) (h: Header option) : (EntryKind * string) list =
        let out = ResizeArray<EntryKind * string>()
        let add kind name = out.Add((kind, name))

        let activePatternNames (ap: string) =
            ap.Split('|', StringSplitOptions.RemoveEmptyEntries)
            |> Array.map _.Trim()
            |> Array.filter (fun n -> n <> "_" && n <> "")
            |> List.ofArray

        let memberOf (kind: EntryKind) (line: string) =
            let d = declRx.Match line

            if d.Success then
                if not (d.Groups["vis"].Value.TrimEnd() = "private") then
                    add kind d.Groups["name"].Value
            else
                let l = letRx.Match line

                if l.Success && not (l.Groups["mods"].Value.Contains "private") then
                    if l.Groups["ap"].Success then
                        for n in activePatternNames l.Groups["ap"].Value do
                            add kind n
                    else
                        add kind l.Groups["name"].Value

        match h with
        | None -> ()
        | Some h when h.Keyword = "module" ->
            // A top-level module: the module is the entry; its body is
            // reached through it, or directly when it is AutoOpen.
            if h.AutoOpen then
                for i in h.Line + 1 .. lines.Length - 1 do
                    let line = lines[i]

                    if line.Length > 0 && indentOf line = 0 then
                        memberOf EntryKind.Member line
        | Some h ->
            let start = h.Line + 1
            // Scan state over the lines after the header.
            let mutable context = "" // "", "union", "record", "autoopen"
            let mutable autoOpenPending = false

            for i in start .. lines.Length - 1 do
                let line = lines[i]

                if String.IsNullOrWhiteSpace line then
                    ()
                elif indentOf line = 0 then
                    let trimmed = line.TrimStart()

                    if trimmed.StartsWith "[<" then
                        if autoOpenRx.IsMatch trimmed then
                            autoOpenPending <- true
                    elif trimmed.StartsWith "//" || trimmed.StartsWith "#" || trimmed.StartsWith "open " then
                        ()
                    else
                        let nm = nestedModuleRx.Match line

                        if nm.Success && nm.Groups["indent"].Value = "" then
                            let vis = nm.Groups["vis"].Value

                            if not (vis.Contains "private") then
                                add EntryKind.Type nm.Groups["name"].Value

                            context <-
                                (if autoOpenPending && not (vis.Contains "private") then
                                     "autoopen"
                                 else
                                     "")
                        else
                            let d = declRx.Match line

                            if d.Success && d.Groups["indent"].Value = "" then
                                let isPrivate = d.Groups["vis"].Value.TrimEnd() = "private"

                                if not isPrivate then
                                    add EntryKind.Type d.Groups["name"].Value

                                let body = line.Substring(d.Index + d.Length)

                                context <-
                                    if isPrivate || body.Contains "{" then
                                        // A record (its fields are not recorded) or a private type.
                                        ""
                                    else
                                        for c in inlineCaseRx.Matches body do
                                            add EntryKind.Case c.Groups["name"].Value

                                        "union"
                            else
                                context <- ""

                        autoOpenPending <- false
                elif context = "autoopen" then
                    if indentOf line = 4 then
                        memberOf EntryKind.Member line
                elif context = "union" then
                    let c = caseRx.Match line

                    if c.Success then
                        add EntryKind.Case c.Groups["name"].Value
                    elif indentOf line = 4 && not (line.TrimStart().StartsWith "|") then
                        // Members follow the cases: no more cases to read.
                        if not (line.TrimStart().StartsWith "//") then
                            context <- ""

        out |> List.ofSeq |> List.distinct

    // ── Scanning the tier ─────────────────────────────────────────────

    let private compileIncludeRx =
        Regex(@"<Compile\s+Include=""(?<path>[^""]+)""", RegexOptions.CultureInvariant)

    /// The `Compile Include` paths of a project file, resolved to absolute
    /// paths, in compile order.
    let compileItems (projectPath: string) : string list =
        let dir = Path.GetDirectoryName projectPath

        [
            for m in compileIncludeRx.Matches(File.ReadAllText projectPath) do
                Path.GetFullPath(Path.Combine(dir, m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)))
        ]

    let private toRelative (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    let private readLines (path: string) =
        File.ReadAllText(path).Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.None)

    /// The project file of `part` under `repoRoot`.
    let projectPath (repoRoot: string) (part: Part) =
        Path.Combine(repoRoot, "src", part.Project, part.Project + ".fsproj")

    /// The namespace root a file compiled by `part` takes.
    let rootFor (part: Part) (path: string) =
        if
            part.Project = "ToolUp.Platform.Server"
            && filesSources.Contains(Path.GetFileName path)
        then
            filesRoot
        else
            part.Root

    /// The declaration paths Core and the projects it references (the
    /// transport tier below it) declare — what every part and every
    /// consumer sees without a companion. A tier path equal to one of them
    /// is `Shared`, not moved; a tier file leaving one of them keeps
    /// opening it. A path only a companion or the client tier declares
    /// counts for nothing here: a part cannot reach it, and a consumer of
    /// the companion opens the companion's own module.
    let coreDeclared (repoRoot: string) : Set<string> =
        let core =
            Path.Combine(repoRoot, "src", "ToolUp.Platform.Core", "ToolUp.Platform.Core.fsproj")

        let referenced = [
            for m in Regex.Matches(File.ReadAllText core, @"<ProjectReference\s+Include=""(?<path>[^""]+)""") do
                Path.GetFullPath(
                    Path.Combine(
                        Path.GetDirectoryName core,
                        m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar)
                    )
                )
        ]

        core :: referenced
        |> List.collect compileItems
        |> List.collect (fun f ->
            let lines = readLines f

            match header lines with
            | None -> []
            | Some h ->
                // The namespace, the module, and every namespace-level name
                // the file declares: a tier module named like a Core type
                // (`module ServerConfig`, extending Core's record) is shared
                // with it, not moved away from it.
                let ns = namespaceOf h

                h.Path
                :: ns
                :: (declarations lines (Some h)
                    |> List.choose (fun (kind, name) ->
                        match kind with
                        | EntryKind.Type -> Some(if ns = "" then name else ns + "." + name)
                        | _ -> None)))
        |> List.filter (fun p -> p <> "")
        |> Set.ofList

    /// Scans the tier under `repoRoot`: every file's move and every table
    /// entry, from the part project files and the sources they compile.
    /// Reads the sources as they are, before or after the move — the
    /// placement rule is idempotent.
    let scan (repoRoot: string) : FileMove list * Entry list =
        // What stays: every path Core declares, and every tier path the
        // placement rule leaves where it is (the remoting library's
        // namespaces in the Remoting part).
        let staying =
            let tierStays = [
                for part in parts do
                    for path in compileItems (projectPath repoRoot part) do
                        let root = rootFor part path

                        match header (readLines path) with
                        | Some h when placed root (unplaced root h.Path) = unplaced root h.Path ->
                            yield h.Path
                            yield namespaceOf h
                        | _ -> ()
            ]

            Set.union (coreDeclared repoRoot) (Set.ofList tierStays) |> Set.remove ""


        let files = ResizeArray<FileMove>()
        let entries = ResizeArray<Entry>()

        for part in parts do
            for path in compileItems (projectPath repoRoot part) do
                let lines = readLines path
                let h = header lines
                let root = rootFor part path

                let oldHeader, newHeader, oldNs, newNs =
                    match h with
                    | None -> "", "", "", root
                    | Some h ->
                        let oldPath = unplaced root h.Path
                        let newPath = placed root oldPath

                        let render p =
                            sprintf "%s %s%s" h.Keyword h.Modifiers p

                        let oldNs = namespaceOf { h with Path = oldPath }

                        let newNs =
                            match h.Keyword with
                            | "namespace" -> newPath
                            | _ ->
                                match newPath.LastIndexOf '.' with
                                | -1 -> ""
                                | i -> newPath.Substring(0, i)

                        render oldPath, render newPath, oldNs, newNs

                files.Add {
                    Project = part.Project
                    Path = toRelative repoRoot path
                    OldHeader = oldHeader
                    NewHeader = newHeader
                }

                match h with
                | None -> ()
                | Some h ->
                    let entry kind oldNs name newNs = {
                        Kind = kind
                        OldNamespace = oldNs
                        Name = name
                        NewNamespace = newNs
                    }

                    // The file's namespace moved, and other sources still
                    // declare the old one: the file keeps seeing that half
                    // by opening it.
                    if oldNs <> newNs && oldNs.StartsWith(platform + ".") && staying.Contains oldNs then
                        entries.Add(entry EntryKind.Split oldNs "*" newNs)

                    if h.Keyword = "module" then
                        let oldFull = unplaced root h.Path
                        let name = oldFull.Substring(oldFull.LastIndexOf '.' + 1)
                        let newFull = placed root oldFull

                        if oldFull <> newFull then
                            if staying.Contains oldFull then
                                entries.Add(entry EntryKind.Shared oldNs name newNs)

                                for _, declared in declarations lines (Some { h with AutoOpen = true }) do
                                    entries.Add(entry EntryKind.Member oldFull declared newFull)
                            else
                                entries.Add(entry EntryKind.Type oldNs name newNs)

                                if h.AutoOpen then
                                    for kind, declared in declarations lines (Some h) do
                                        entries.Add(entry kind oldNs declared newNs)
                    else
                        let oldFull = unplaced root h.Path
                        let newFull = placed root oldFull

                        if oldFull <> newFull then
                            if not (staying.Contains oldFull) then
                                entries.Add(entry EntryKind.Namespace oldFull "*" newFull)

                            for kind, name in declarations lines (Some h) do
                                match kind with
                                | EntryKind.Type when staying.Contains(oldFull + "." + name) ->
                                    entries.Add(entry EntryKind.Shared oldNs name newNs)
                                | _ -> entries.Add(entry kind oldNs name newNs)

        List.ofSeq files, entries |> Seq.distinct |> List.ofSeq

    // ── The table on disk ─────────────────────────────────────────────

    /// The embedded resource's logical name in `ToolUp.Platform.Build`.
    let resourceName = "codemod-1198-namespaces.txt"

    let private kindName kind =
        match kind with
        | EntryKind.Type -> "type"
        | EntryKind.Shared -> "shared"
        | EntryKind.Member -> "member"
        | EntryKind.Case -> "case"
        | EntryKind.Namespace -> "namespace"
        | EntryKind.Split -> "split"

    let private kindOf name =
        match name with
        | "type" -> Some EntryKind.Type
        | "shared" -> Some EntryKind.Shared
        | "member" -> Some EntryKind.Member
        | "case" -> Some EntryKind.Case
        | "namespace" -> Some EntryKind.Namespace
        | "split" -> Some EntryKind.Split
        | _ -> None

    /// Renders the table: a header comment, then one line per entry,
    /// `kind old-namespace name new-namespace`, sorted so a regeneration
    /// over unchanged sources is byte-identical.
    let render (entries: Entry list) : string =
        let sb = StringBuilder()

        // LF throughout, whatever the platform: the file is committed, and
        // the drift check compares it to a fresh rendering byte for byte.
        sb.Append "# Phase 1198 — the server tier's names and the namespace each took. Generated by\n"
        |> ignore

        sb.Append "# `dotnet run --project Build.fsproj -- SubsystemNamespaces --write-table`; do not edit.\n"
        |> ignore

        sb.Append "# kind old-namespace name new-namespace\n" |> ignore

        let ordered =
            entries
            |> List.distinct
            |> List.sortWith (fun a b ->
                let k = compare (kindName a.Kind) (kindName b.Kind)

                if k <> 0 then
                    k
                else
                    let o = String.CompareOrdinal(a.OldNamespace, b.OldNamespace)

                    if o <> 0 then
                        o
                    else
                        let n = String.CompareOrdinal(a.Name, b.Name)

                        if n <> 0 then
                            n
                        else
                            String.CompareOrdinal(a.NewNamespace, b.NewNamespace))

        for e in ordered do
            sb
                .Append(kindName e.Kind)
                .Append(' ')
                .Append(if e.OldNamespace = "" then "-" else e.OldNamespace)
                .Append(' ')
            |> ignore

            sb.Append(e.Name).Append(' ').Append(e.NewNamespace).Append('\n') |> ignore

        sb.ToString()

    /// Parses a rendered table. A malformed line is an error naming it.
    let parse (text: string) : Entry list = [
        for raw in text.Split([| "\r\n"; "\n"; "\r" |], StringSplitOptions.RemoveEmptyEntries) do
            let line = raw.Trim()

            if line <> "" && not (line.StartsWith "#") then
                match line.Split(' ', StringSplitOptions.RemoveEmptyEntries) with
                | [| kind; oldNs; name; newNs |] ->
                    match kindOf kind with
                    | Some k -> {
                        Kind = k
                        OldNamespace = (if oldNs = "-" then "" else oldNs)
                        Name = name
                        NewNamespace = newNs
                      }
                    | None -> failwithf "SubsystemNamespaces: unknown entry kind in table line: %s" line
                | _ -> failwithf "SubsystemNamespaces: malformed table line: %s" line
    ]

    /// The table shipped inside this assembly, parsed once.
    let embedded: Lazy<Entry list> =
        lazy
            (let asm = System.Reflection.Assembly.GetExecutingAssembly()

             use stream = asm.GetManifestResourceStream resourceName

             if isNull stream then
                 failwithf "SubsystemNamespaces: embedded resource %s is missing from %s" resourceName asm.FullName

             use reader = new StreamReader(stream, Encoding.UTF8)
             parse (reader.ReadToEnd()))

    // ── The rewrite ───────────────────────────────────────────────────

    /// The table indexed for the rewrite.
    type Index = {
        /// Old full path → new full path, longest old path first, with
        /// whether the old path matches a prefix: a moved (non-shared)
        /// type or module moves everything under it; a wholly-moved
        /// namespace matches only exactly, because a companion may still
        /// declare a module under the old name.
        Moves: (string * string * bool) list
        /// Name → the namespaces to open when a file mentions it, each
        /// with its kind and the namespace the name was declared in (a
        /// mention qualified by that namespace's last segment,
        /// `Auth.IdentitySanitiser`, counts; any other qualifier does not).
        Mentions: Map<string, (EntryKind * string * string) list>
        /// New namespace → the old one other sources still declare.
        Splits: Map<string, string>
    }

    /// Builds the index over a table.
    let index (entries: Entry list) : Index =
        let join ns name =
            if ns = "" then name else ns + "." + name

        let moves =
            entries
            |> List.choose (fun e ->
                match e.Kind with
                | EntryKind.Namespace -> Some(e.OldNamespace, e.NewNamespace, false)
                | EntryKind.Type -> Some(join e.OldNamespace e.Name, join e.NewNamespace e.Name, true)
                // A union case written fully qualified (`ToolUp.Platform.SomeCase x`)
                // moves with its union; a case has nothing under it.
                | EntryKind.Case -> Some(join e.OldNamespace e.Name, join e.NewNamespace e.Name, false)
                | _ -> None)
            |> List.distinct
            |> List.sortByDescending (fun (o, _, _) -> o.Length)

        let mentions =
            entries
            |> List.choose (fun e ->
                match e.Kind with
                | EntryKind.Namespace -> None
                | EntryKind.Split -> None
                | k -> Some(e.Name, (k, e.NewNamespace, e.OldNamespace)))
            |> List.groupBy fst
            |> List.map (fun (name, xs) -> name, xs |> List.map snd |> List.distinct)
            |> Map.ofList

        let splits =
            entries
            |> List.choose (fun e ->
                match e.Kind with
                | EntryKind.Split -> Some(e.NewNamespace, e.OldNamespace)
                | _ -> None)
            |> Map.ofList

        {
            Moves = moves
            Mentions = mentions
            Splits = splits
        }

    /// The index over the embedded table.
    let embeddedIndex: Lazy<Index> = lazy (index embedded.Value)

    let private lineSplitter = Regex(@"(\r\n|\n|\r)", RegexOptions.CultureInvariant)

    let private openRx =
        Regex(@"^(?<indent>\s*)open\s+(?<type>type\s+)?(?<path>[A-Za-z_][\w.]*)\s*$", RegexOptions.CultureInvariant)

    let private qualifiedRx =
        Regex(@"(?<![\w.'`])ToolUp\.Platform(?:\.[A-Z]\w*)+", RegexOptions.CultureInvariant)

    /// A file extension or assembly-name tail after which a qualified
    /// match is a path or a package id, never an F# path.
    let private notAPath =
        Regex(
            @"^(?:[\\/""']|\.(?:fsproj|props|targets|dll|nupkg|csproj|md|json|Remoting|Infra|Config|Scope|Jobs|Audit|Tests)\b)",
            RegexOptions.CultureInvariant
        )

    let private isComment (line: string) =
        let t = line.TrimStart()
        t.StartsWith "//" || t.StartsWith "(*"

    /// Whether position `i` of `line` falls inside a string literal, read
    /// as an odd number of unescaped quotes before it. A triple-quoted
    /// string that spans lines is not seen; the path guard above covers
    /// the project paths those carry.
    let private inString (line: string) (i: int) =
        let mutable quotes = 0
        let mutable j = 0

        while j < i do
            if line[j] = '"' && (j = 0 || line[j - 1] <> '\\') then
                quotes <- quotes + 1

            j <- j + 1

        quotes % 2 = 1

    /// The new path for `path` when a move's old path is it or a
    /// dotted prefix of it.
    let private moved (moves: (string * string * bool) list) (path: string) =
        moves
        |> List.tryPick (fun (o, n, prefix) ->
            if path = o then
                Some n
            elif prefix && path.StartsWith(o + ".") then
                Some(n + path.Substring o.Length)
            else
                None)

    /// A name, optionally qualified by ONE segment (`Auth.IdentitySanitiser`).
    let private mentionRx =
        Regex(@"(?<![\w.'])(?:(?<qualifier>[A-Za-z_]\w*)\.)?(?<name>[A-Za-z_]\w*)\b", RegexOptions.CultureInvariant)

    /// A name the file declares itself — a type, a case, a binding, a
    /// module — resolves locally and opens nothing.
    let private localDeclRx =
        Regex(
            @"^(?:\s*(?:type|and|exception|module)\s+(?:\[<[^\]]*>\]\s*)*(?:rec\s+|internal\s+|private\s+)*|\s*let\s+(?:inline\s+|rec\s+|mutable\s+|private\s+)*|\s{4}\|\s*(?!.*->))(?<name>[A-Z]\w*)",
            RegexOptions.CultureInvariant
        )

    /// F# opens no enclosing namespace on its own: a declaration in `own`
    /// sees `own` and nothing above it (which is why every tier file now
    /// opens `ToolUp.Platform` explicitly).
    let private isOwn (own: string) (ns: string) = own = ns

    /// The order derived opens are emitted in: `ToolUp.Platform` first,
    /// then the parts in dependency order, then any other namespace by name.
    let private openOrder (ns: string) =
        if ns = platform then
            -1, ns
        else
            match roots |> List.tryFindIndex (fun r -> ns = r || ns.StartsWith(r + ".")) with
            | Some i -> i, ns
            | None -> roots.Length, ns

    /// The assembly level a namespace belongs to, in the umbrella's order
    /// (`Files` is the umbrella, so it shares `Compose`'s level; the
    /// remoting library's namespaces are the Remoting part's); `None` for
    /// a namespace outside the tier.
    let private levelOf (ns: string) =
        if ns.StartsWith "ToolUp.Remoting" then
            Some 0
        else
            match roots |> List.tryFindIndex (fun r -> ns = r || ns.StartsWith(r + ".")) with
            | Some i when roots[i] = filesRoot -> Some(roots.Length - 2)
            | other -> other

    /// Whether a file's own namespace is one of the tier's.
    let private isTierFile (lines: string[]) =
        match header lines with
        | Some h -> (levelOf (namespaceOf h)).IsSome
        | None -> false

    /// What a file's compiling projects let it open (see `ProjectScope`).
    type FileReach = {
        /// Whether every project compiling the file reaches the server tier.
        ReachesServer: bool
        /// The lowest part level among the part projects compiling the
        /// file: an open of a later part would name an assembly that
        /// project does not reference. `None` outside the tier.
        LevelCap: int option
        /// For a project that compiles tier SOURCES against Core rather
        /// than referencing the parts (a test pack over a few server
        /// files): the tier namespaces those sources declare, the only
        /// ones its files can open. `None` means no such restriction.
        Allowed: Set<string> option
    }

    /// A consumer file: everything is open to it.
    let consumerReach = {
        ReachesServer = true
        LevelCap = None
        Allowed = None
    }

    let private rewriteAll
        (idx: Index)
        (reach: FileReach)
        (segments: string[])
        : string[] * (string * int * string * string) list =
        let cap = reach.LevelCap
        let out = Array.copy segments
        let rewrites = ResizeArray<string * int * string * string>()
        let lines = [| for i in 0..2 .. segments.Length - 1 -> segments[i] |]
        let h = header lines
        let headerLine = h |> Option.map _.Line
        let own = h |> Option.map namespaceOf |> Option.defaultValue ""
        // A top-level module never opens itself.
        let ownPath = h |> Option.map _.Path |> Option.defaultValue ""

        let contentIndex (lineIndex: int) = lineIndex * 2

        // 1. `open` lines of a moved path.
        for i in 0 .. lines.Length - 1 do
            let m = openRx.Match lines[i]

            if m.Success then
                match moved idx.Moves m.Groups["path"].Value with
                | Some target ->
                    let after =
                        sprintf "%sopen %s%s" m.Groups["indent"].Value m.Groups["type"].Value target

                    rewrites.Add(("1198-open", i, lines[i], after))
                    out[contentIndex i] <- after
                | None -> ()

        // 2. Qualified paths, outside headers, opens, comments and strings.
        for i in 0 .. lines.Length - 1 do
            let line = out[contentIndex i]

            if
                Some i <> headerLine
                && not (openRx.IsMatch line)
                && not (isComment line)
                && qualifiedRx.IsMatch line
            then
                let after =
                    qualifiedRx.Replace(
                        line,
                        MatchEvaluator(fun m ->
                            let tail = line.Substring(m.Index + m.Length)

                            if inString line m.Index || notAPath.IsMatch tail then
                                m.Value
                            else
                                let comment = line.IndexOf "//"

                                if comment >= 0 && comment < m.Index && not (inString line comment) then
                                    m.Value
                                else
                                    match moved idx.Moves m.Value with
                                    | Some target -> target
                                    | None -> m.Value)
                    )

                if after <> line then
                    rewrites.Add(("1198-qualified", i, line, after))
                    out[contentIndex i] <- after

        // 3. Derived opens.
        let current = [| for i in 0..2 .. out.Length - 1 -> out[i] |]

        // What the file already opens at the top level. An indented open
        // belongs to a nested module and covers nothing outside it.
        let opened =
            current
            |> Array.choose (fun l ->
                let m = openRx.Match l

                if m.Success && m.Groups["type"].Value = "" && m.Groups["indent"].Value = "" then
                    Some m.Groups["path"].Value
                else
                    None)
            |> Set.ofArray

        let mentioned = Collections.Generic.HashSet<string>()

        let local =
            current
            |> Array.choose (fun l ->
                let m = localDeclRx.Match l
                if m.Success then Some m.Groups["name"].Value else None)
            |> Set.ofArray

        // A file now declared in a part's namespace sees nothing of
        // `ToolUp.Platform` until it opens it.
        if own <> platform && own.StartsWith(platform + ".") && (levelOf own).IsSome then
            mentioned.Add platform |> ignore

        // A file whose namespace moved out of one other sources still
        // declare keeps seeing that half.
        match idx.Splits.TryFind own with
        | Some old -> mentioned.Add old |> ignore
        | None -> ()

        for i in 0 .. current.Length - 1 do
            let line = current[i]

            if Some i <> headerLine && not (openRx.IsMatch line) && not (isComment line) then
                let code =
                    match line.IndexOf "//" with
                    | -1 -> line
                    | c when inString line c -> line
                    | c -> line.Substring(0, c)

                for m in mentionRx.Matches code do
                    let name = m.Groups["name"].Value
                    let qualifier = m.Groups["qualifier"]

                    // The qualifier is itself a bare mention: in a dotted mention of the form Qualifier.member, the qualifier half.
                    if qualifier.Success && not (local.Contains qualifier.Value) then
                        match idx.Mentions.TryFind qualifier.Value with
                        | None -> ()
                        | Some targets ->
                            for _, ns, _ in targets do
                                mentioned.Add ns |> ignore

                    if not (local.Contains name) then
                        match idx.Mentions.TryFind name with
                        | None -> ()
                        | Some targets ->
                            for _, ns, declaredIn in targets do
                                // Unqualified, or qualified by the declaring
                                // namespace's own last segment: a Core module
                                // whose nested module moved is still reached
                                // through the Core module's name.
                                if not qualifier.Success then
                                    mentioned.Add ns |> ignore
                                elif declaredIn = qualifier.Value || declaredIn.EndsWith("." + qualifier.Value) then
                                    // `Auth.IdentitySanitiser` resolves through the
                                    // namespace ABOVE `…Config.Auth`, where `Auth`
                                    // is a fragment the Core module's name merges with.
                                    let suffix = "." + qualifier.Value

                                    if ns.EndsWith suffix then
                                        mentioned.Add(ns.Substring(0, ns.Length - suffix.Length)) |> ignore
                                    else
                                        mentioned.Add ns |> ignore

        let needed =
            mentioned
            |> Seq.filter (fun ns -> not (isOwn own ns) && ns <> ownPath && not (opened.Contains ns))
            // A project compiling tier sources against Core can open only the
            // namespaces those sources declare.
            |> Seq.filter (fun ns ->
                match reach.Allowed with
                | Some allowed -> ns = platform || allowed.Contains ns
                | None -> true)
            // A tier file sees only the parts before its own: an open of a
            // later part names an assembly the project does not reference.
            // The cap is the lower of what its namespace says and what the
            // project compiling it says (a global module in the Remoting
            // part has no namespace to read a level from).
            |> Seq.filter (fun ns ->
                let mine =
                    match levelOf own, cap with
                    | Some a, Some b -> Some(min a b)
                    | Some a, None -> Some a
                    | None, b -> b

                match mine, levelOf ns with
                | Some mine, Some theirs -> theirs <= mine
                | _ -> true)
            |> Seq.sortBy openOrder
            |> List.ofSeq

        let terminator = if out.Length > 1 then out[1] else Environment.NewLine

        let isSkippable (l: string) =
            let t = l.TrimStart()

            String.IsNullOrWhiteSpace l
            || t.StartsWith "//"
            || t.StartsWith "#"
            || t.StartsWith "[<"
            || t.StartsWith "(*"
            || t.StartsWith "*)"

        // The last `open` of the file's leading block, if it has one.
        let lastOpen =
            let firstLine = (headerLine |> Option.defaultValue -1) + 1
            let mutable last = None
            let mutable i = firstLine
            let mutable stop = false

            while not stop && i < current.Length do
                let l = current[i]

                if openRx.IsMatch l then
                    last <- Some i
                elif not (isSkippable l) then
                    stop <- true

                i <- i + 1

            last

        /// Appends `open` lines after content line `at` (`-1`: the top).
        let insertAfter (at: int) (blankBefore: bool) (indent: string) (names: string list) =
            match names with
            | [] -> ()
            | names ->
                let inserted = names |> List.map (fun ns -> sprintf "%sopen %s" indent ns)

                let block =
                    (if blankBefore then "" :: inserted else inserted) |> String.concat terminator

                if at < 0 then
                    let before = out[0]
                    let after = block + terminator + before
                    rewrites.Add(("1198-derived-open", 0, before, after))
                    out[0] <- after
                else
                    let before = out[contentIndex at]
                    let after = before + terminator + block
                    rewrites.Add(("1198-derived-open", at, before, after))
                    out[contentIndex at] <- after

        // Where the derived opens go decides what shadows what, and the
        // rule restores the resolution a file had while everything shared
        // `ToolUp.Platform`: a server name won over a Core name of the same
        // spelling (the server half was the later contribution to the
        // merged namespace), and a module a file opened itself won over
        // both. So a tier file that lacks `open ToolUp.Platform` takes it
        // straight after its header, the part opens right after it, and
        // both before the file's own opens; a file that already opens
        // `ToolUp.Platform`, and every consumer, takes the part opens after
        // its last open. Where the two halves genuinely collided (a record
        // with the same labels on both sides) the codemod cannot know which
        // was meant, and the site takes a type annotation by hand — the
        // migration doc lists the shapes.
        let platformOpen, partOpens = needed |> List.partition ((=) platform)

        let indentAt (i: int) =
            (openRx.Match current[i]).Groups["indent"].Value

        match platformOpen, lastOpen, headerLine with
        | _ :: _, _, Some hl -> insertAfter hl true "" (platformOpen @ partOpens)
        | _ :: _, _, None -> insertAfter -1 false "" (platformOpen @ partOpens)
        | [], Some i, _ -> insertAfter i false (indentAt i) partOpens
        | [], None, Some hl -> insertAfter hl true "" partOpens
        | [], None, None -> insertAfter -1 false "" partOpens

        out, List.ofSeq rewrites

    /// Rewrites one file's segments (content / terminator alternating, as
    /// `Codemod` splits them) and returns the segments with the rewrites,
    /// each as `(rule, 0-based content index, before, after)`. A file
    /// whose projects do not all reach the server tier is left alone
    /// unless it is a tier file itself: a tier file compiled by such a
    /// project (a test pack compiling a few server sources against Core)
    /// still needs `ToolUp.Platform` opened.
    let rewrite
        (idx: Index)
        (reach: FileReach)
        (segments: string[])
        : string[] * (string * int * string * string) list =
        let lines = [| for i in 0..2 .. segments.Length - 1 -> segments[i] |]

        if reach.ReachesServer || isTierFile lines then
            rewriteAll idx reach segments
        else
            segments, []

    /// Rewrites a whole text (any line terminators, re-emitted verbatim)
    /// of a consumer file.
    let rewriteText (idx: Index) (text: string) : string * (string * int * string * string) list =
        let segments, rewrites = rewrite idx consumerReach (lineSplitter.Split text)
        String.concat "" segments, rewrites

    // ── The declarations of the tier itself ───────────────────────────

    /// Rewrites each tier file's header to the one the map says, in place
    /// under `repoRoot`; returns the files written. Idempotent.
    let applyDeclarations (repoRoot: string) (moves: FileMove list) : string list = [
        for f in moves do
            if f.OldHeader <> f.NewHeader && f.OldHeader <> "" then
                let path = Path.Combine(repoRoot, f.Path)
                let text = File.ReadAllText path
                let bytes = File.ReadAllBytes path

                let bom =
                    bytes.Length >= 3 && bytes[0] = 0xEFuy && bytes[1] = 0xBBuy && bytes[2] = 0xBFuy

                let lines = lineSplitter.Split text

                match header [| for i in 0..2 .. lines.Length - 1 -> lines[i] |] with
                | Some h when lines[h.Line * 2].TrimEnd() = f.OldHeader ->
                    lines[h.Line * 2] <- f.NewHeader
                    File.WriteAllText(path, String.concat "" lines, UTF8Encoding bom)
                    path
                | _ -> ()
    ]

    /// The migration doc's map: one Markdown table row per file.
    let renderMap (moves: FileMove list) : string =
        let sb = StringBuilder()
        sb.AppendLine "| Part | File | Declared today | Declared after |" |> ignore
        sb.AppendLine "|---|---|---|---|" |> ignore

        for f in moves do
            let shortProject =
                f.Project.Replace("ToolUp.Platform.Server.", "").Replace("ToolUp.Platform.Server", "umbrella")

            let path = f.Path.Replace("src/ToolUp.Platform.Server/Server/", "")

            sb.AppendLine(
                sprintf
                    "| %s | `%s` | `%s` | `%s` |"
                    shortProject
                    path
                    (if f.OldHeader = "" then "—" else f.OldHeader)
                    (if f.NewHeader = "" then "—" else f.NewHeader)
            )
            |> ignore

        sb.ToString()

    // ── Which projects the pass applies to ────────────────────────────

    /// The projects of a consumer tree that reach the server tier — the
    /// only ones whose files take derived opens. A file in a project that
    /// never references the tier (a Fable client, a Core-only library)
    /// could mention a moved name by coincidence, and an open of a part
    /// it does not reference would not compile.
    module ProjectScope =

        let private projectRefRx =
            Regex(@"<ProjectReference\s+Include=""(?<path>[^""]+)""", RegexOptions.CultureInvariant)

        let private packageRefRx =
            Regex(@"<PackageReference\s+Include=""(?<id>[^""]+)""", RegexOptions.CultureInvariant)

        /// A source-injection consumer: `<Import Project="…ToolUp.Platform.Server….props" />`.
        let private serverPropsImportRx =
            Regex(@"<Import\s+Project=""[^""]*ToolUp\.Platform\.Server[^""]*""", RegexOptions.CultureInvariant)

        let private partProjects = parts |> List.map _.Project |> Set.ofList

        /// Package ids that say nothing about the server tier: a project
        /// referencing only these (or none) is out of scope.
        let private tierNeutral =
            set [
                "ToolUp.Platform.Core"
                "ToolUp.Platform.Client"
                "ToolUp.Platform.UI"
                "ToolUp.Platform.Build"
                "ToolUp.Platform.Transport"
                "ToolUp.Elmish"
            ]

        let private pruned =
            set [ "bin"; "obj"; "node_modules"; ".git"; ".fable"; ".vs"; ".idea"; "packages" ]

        /// The decision over a tree: every project directory under `root`
        /// with whether its project reaches the server tier, and every
        /// compiled file with whether EVERY project compiling it does — a
        /// source linked into a Fable project as well as a server one
        /// takes no open the Fable compile cannot resolve.
        type Scope = {
            /// Project directory (absolute, no trailing separator) → in scope.
            Directories: Map<string, bool>
            /// Compiled file (absolute) → every compiling project is in scope.
            Files: Map<string, bool>
            /// Compiled file (absolute) → the lowest part level among the
            /// part projects compiling it; absent outside the tier.
            Levels: Map<string, int>
            /// Compiled file (absolute) → the tier namespaces a project that
            /// compiles tier sources against Core (and compiles this file)
            /// declares through them; absent when no such project does.
            Allowed: Map<string, Set<string>>
        }

        /// Reads every project under `root` and decides each.
        let ofTree (root: string) : Scope =
            let rec visit (dir: string) = seq {
                for f in Directory.EnumerateFiles(dir, "*.fsproj") do
                    yield f

                for d in Directory.EnumerateDirectories dir do
                    if not (pruned.Contains(Path.GetFileName d)) then
                        yield! visit d
            }

            let projects = visit (Path.GetFullPath root) |> List.ofSeq

            let references =
                projects
                |> List.map (fun p ->
                    let text = File.ReadAllText p
                    let dir = Path.GetDirectoryName p

                    let projectRefs = [
                        for m in projectRefRx.Matches text ->
                            Path.GetFullPath(
                                Path.Combine(dir, m.Groups["path"].Value.Replace('\\', Path.DirectorySeparatorChar))
                            )
                    ]

                    let packageRefs = [ for m in packageRefRx.Matches text -> m.Groups["id"].Value ]
                    let importsServerProps = serverPropsImportRx.IsMatch text
                    Path.GetFullPath p, (projectRefs, packageRefs, importsServerProps))
                |> Map.ofList

            let decided = Collections.Generic.Dictionary<string, bool>()

            // The part projects in the tree, by their index in the
            // umbrella's order, and the sources they compile.
            let partLevels =
                projects
                |> List.choose (fun p ->
                    parts
                    |> List.tryFindIndex (fun part -> part.Project = Path.GetFileNameWithoutExtension p)
                    |> Option.map (fun i -> Path.GetFullPath p, i))

            let tierSources =
                partLevels |> List.collect (fun (p, _) -> compileItems p) |> Set.ofList

            let rec inScope (visiting: Set<string>) (project: string) =
                match decided.TryGetValue project with
                | true, v -> v
                | _ ->
                    let name = Path.GetFileNameWithoutExtension project

                    let v =
                        if partProjects.Contains name then
                            true
                        // A project compiling a tier source (a test pack over
                        // a few server files and Core) reaches the tier's names
                        // through those sources.
                        elif compileItems project |> List.exists tierSources.Contains then
                            true
                        else
                            match references.TryFind project with
                            | None ->
                                // A referenced project outside the walked
                                // tree: decided by its name alone.
                                name.StartsWith "ToolUp." && not (tierNeutral.Contains name)
                            | Some(projectRefs, packageRefs, importsServerProps) ->
                                let isFable = packageRefs |> List.contains "Fable.Core"

                                // The server tier by name — a package, an imported
                                // `.Server.props`, or a project reaching it — puts a
                                // project in scope whatever else it compiles (a
                                // single-project module template carries Fable.Core
                                // beside its server half). Any other ToolUp package
                                // counts only for a project that is not a Fable one.
                                packageRefs
                                |> List.exists (fun id -> id.StartsWith "ToolUp.Platform.Server" || id = "ToolUp.Sdk")
                                || importsServerProps
                                || projectRefs
                                   |> List.exists (fun r ->
                                       not (visiting.Contains r) && inScope (visiting.Add project) r)
                                || (not isFable
                                    && packageRefs
                                       |> List.exists (fun id ->
                                           id.StartsWith "ToolUp." && not (tierNeutral.Contains id)))

                    decided[project] <- v
                    v

            let decisions =
                projects |> List.map (fun p -> Path.GetFullPath p, inScope Set.empty p)

            // The namespace a tier source declares once placed, read from
            // the first part project that compiles it.
            let placedNamespace =
                let byFile =
                    partLevels
                    |> List.collect (fun (p, level) -> compileItems p |> List.map (fun f -> f, (p, level)))
                    |> List.groupBy fst
                    |> List.map (fun (f, xs) -> f, xs |> List.minBy (snd >> snd) |> snd |> fst)
                    |> Map.ofList

                fun (file: string) ->
                    match byFile.TryFind file with
                    | None -> None
                    | Some projectPath ->
                        let part =
                            parts
                            |> List.find (fun part -> part.Project = Path.GetFileNameWithoutExtension projectPath)

                        header (readLines file)
                        |> Option.map (fun h ->
                            let newPath = placed (rootFor part file) h.Path

                            match h.Keyword with
                            | "namespace" -> newPath
                            | _ ->
                                match newPath.LastIndexOf '.' with
                                | -1 -> ""
                                | i -> newPath.Substring(0, i))

            // Projects that compile tier sources without being a part: each
            // can open exactly the tier namespaces its sources declare.
            let sourceCompilers =
                decisions
                |> List.choose (fun (p, _) ->
                    if partLevels |> List.exists (fun (part, _) -> part = p) then
                        None
                    else
                        let compiled = compileItems p |> List.filter tierSources.Contains

                        if compiled.IsEmpty then
                            None
                        else
                            let allowed = compiled |> List.choose placedNamespace |> Set.ofList
                            Some(p, allowed))

            {
                Directories =
                    decisions
                    |> List.map (fun (p, v) -> Path.GetDirectoryName p, v)
                    |> List.groupBy fst
                    |> List.map (fun (d, xs) -> d, xs |> List.exists snd)
                    |> Map.ofList
                Files =
                    decisions
                    |> List.collect (fun (p, v) -> compileItems p |> List.map (fun f -> f, v))
                    |> List.groupBy fst
                    |> List.map (fun (f, xs) -> f, xs |> List.forall snd)
                    |> Map.ofList
                Levels =
                    partLevels
                    |> List.collect (fun (p, level) -> compileItems p |> List.map (fun f -> f, level))
                    |> List.groupBy fst
                    |> List.map (fun (f, xs) -> f, xs |> List.map snd |> List.min)
                    |> Map.ofList
                Allowed =
                    sourceCompilers
                    |> List.collect (fun (p, allowed) -> compileItems p |> List.map (fun f -> f, allowed))
                    |> List.groupBy fst
                    |> List.map (fun (f, xs) -> f, xs |> List.map snd |> Set.intersectMany)
                    |> Map.ofList
            }

        /// Whether a file is in scope: every project that compiles it
        /// decides when any does; else the nearest project directory above
        /// it; a file under no project (a script) is in scope.
        let includes (scope: Scope) (path: string) : bool =
            let full = Path.GetFullPath path

            match scope.Files.TryFind full with
            | Some v -> v
            | None ->
                let rec up (dir: string) =
                    if String.IsNullOrEmpty dir then
                        true
                    else
                        match scope.Directories.TryFind dir with
                        | Some v -> v
                        | None -> up (Path.GetDirectoryName dir)

                up (Path.GetDirectoryName full)

        /// What the pass may open in a file: `includes` and the file's
        /// part level, if a part project compiles it.
        let reachOf (scope: Scope) (path: string) : FileReach =
            let full = Path.GetFullPath path

            {
                ReachesServer = includes scope path
                LevelCap = scope.Levels.TryFind full
                Allowed = scope.Allowed.TryFind full
            }