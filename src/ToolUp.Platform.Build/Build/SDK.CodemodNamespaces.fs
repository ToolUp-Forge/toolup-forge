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
/// every part — inserting them beside the file's `open ToolUp.Platform`,
/// or above its own opens when it has none, and inside a nested module
/// that carries its own opens. A second run finds every open in place and
/// changes nothing. It also reports the opens that look dead after the
/// move (`likelyDeadOpens`) and deletes none of them.
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

    /// `lines` with everything that is not code blanked to spaces: comments
    /// (a block comment nests, and spans lines), and string and character
    /// literals of every spelling, across lines where they span them. An
    /// interpolated string's holes are code and stay. A position in a masked
    /// line is the same position in the line it masks, so a match found in
    /// the mask can be applied to the source.
    let private maskNonCode (lines: string[]) : string[] =
        let text = String.Join("\n", lines)
        let n = text.Length
        let masked = text.ToCharArray()

        let blank (from: int) (until: int) =
            for k in from .. (min until n) - 1 do
                if masked[k] <> '\n' then
                    masked[k] <- ' '

        let startsAt (i: int) (s: string) =
            String.CompareOrdinal(text, i, s, 0, s.Length) = 0

        let isIdentifierChar (c: char) = Char.IsLetterOrDigit c || c = '_'

        // The length of a string opener at `i` (its `$` and `@` prefixes and
        // its quotes), or 0 when no string opens there.
        let openerAt (i: int) =
            let mutable j = i

            while j < n && (text[j] = '$' || text[j] = '@') do
                j <- j + 1

            if j < n && text[j] = '"' then
                j - i + (if startsAt j "\"\"\"" then 3 else 1)
            else
                0

        // The index past a character literal at `i`, or `i` when the quote
        // is a type variable's or an identifier's.
        let charLiteralEnd (i: int) =
            if i > 0 && isIdentifierChar (text[i - 1]) then
                i
            elif i + 2 < n && text[i + 1] <> '\\' && text[i + 1] <> '\n' && text[i + 2] = '\'' then
                i + 3
            elif i + 1 < n && text[i + 1] = '\\' then
                let close = text.IndexOf('\'', min n (i + 3))
                if close >= 0 && close - i <= 10 then close + 1 else i
            else
                i

        // Blanks the string opening at `i`, walks its holes as code, and
        // returns the index past it. An unterminated one runs to the end.
        let rec stringEnd (i: int) (openerLength: int) : int =
            let opener = text.Substring(i, openerLength)
            let triple = opener.EndsWith "\"\"\""
            let dollars = opener |> Seq.filter ((=) '$') |> Seq.length
            let verbatim = opener.Contains('@')
            let holeOpen = String('{', max 1 dollars)
            let mutable k = i + openerLength
            let mutable segment = i
            let mutable closed = false

            while not closed && k < n do
                if dollars = 1 && (startsAt k "{{" || startsAt k "}}") then
                    k <- k + 2
                elif dollars > 0 && startsAt k holeOpen then
                    blank segment (k + holeOpen.Length)
                    k <- codeEnd (k + holeOpen.Length) true
                    segment <- k - 1
                elif triple then
                    if startsAt k "\"\"\"" then
                        k <- k + 3
                        closed <- true
                    else
                        k <- k + 1
                elif verbatim && startsAt k "\"\"" then
                    k <- k + 2
                elif not verbatim && text[k] = '\\' then
                    k <- k + 2
                elif text[k] = '"' then
                    k <- k + 1
                    closed <- true
                else
                    k <- k + 1

            blank segment k
            min k n

        // Blanks the block comment opening at `i` and returns the index past
        // it. A string inside a comment is lexed, as the compiler does.
        and commentEnd (i: int) : int =
            let mutable k = i + 2
            let mutable depth = 1

            while depth > 0 && k < n do
                if startsAt k "*)" then
                    depth <- depth - 1
                    k <- k + 2
                elif startsAt k "(*" && not (startsAt k "(*)") then
                    depth <- depth + 1
                    k <- k + 2
                else
                    match (if text[k] = '$' then 0 else openerAt k) with
                    | 0 -> k <- k + 1
                    | length -> k <- stringEnd k length

            blank i k
            min k n

        // Walks code from `i`, blanking what is not code. In a hole it
        // returns the index past the hole's closing brace.
        and codeEnd (i: int) (inHole: bool) : int =
            let mutable k = i
            let mutable depth = 0
            let mutable closed = false

            while not closed && k < n do
                let c = text[k]

                if startsAt k "//" then
                    let eol = text.IndexOf('\n', k)
                    let stop = if eol < 0 then n else eol
                    blank k stop
                    k <- stop
                elif startsAt k "(*" && not (startsAt k "(*)") then
                    k <- commentEnd k
                elif c = '\'' then
                    match charLiteralEnd k with
                    | stop when stop > k ->
                        blank k stop
                        k <- stop
                    | _ -> k <- k + 1
                elif inHole && c = '{' then
                    depth <- depth + 1
                    k <- k + 1
                elif inHole && c = '}' then
                    k <- k + 1

                    if depth = 0 then closed <- true else depth <- depth - 1
                else
                    match openerAt k with
                    | 0 -> k <- k + 1
                    | length -> k <- stringEnd k length

            k

        codeEnd 0 false |> ignore
        String(masked).Split('\n')

    // A top-level `module` may carry its attributes on its own line, and any
    // segment of the path may be double-backticked.
    let private headerRx =
        Regex(
            @"^(?:\[<[^\]]*>\]\s*)*(?<kw>namespace|module)\s+(?<mods>(?:rec\s+|internal\s+|private\s+|public\s+)*)(?<path>(?:[A-Za-z_]\w*|``[^`]+``)(?:\.(?:[A-Za-z_]\w*|``[^`]+``))*)\s*$",
            RegexOptions.CultureInvariant
        )

    let private autoOpenRx =
        Regex(@"\[<\s*AutoOpen\s*>\]", RegexOptions.CultureInvariant)

    /// The first header line of `lines`, if the file has one, read from its
    /// code alone. A `module X =` with a body is a nested module, not a
    /// header, and does not match.
    let header (lines: string[]) : Header option =
        let code = maskNonCode lines

        let rec go i =
            if i >= code.Length then
                None
            else
                let m = headerRx.Match code[i]

                if m.Success then
                    let autoOpen =
                        [ max 0 (i - 3) .. i ] |> List.exists (fun j -> autoOpenRx.IsMatch code[j])

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
        /// is a Core module too): the old `open` stays. A moved MODULE is
        /// opened beside it only when one of its members is named, through
        /// its `Member` rows, since the bare name resolves to the half that
        /// stayed. A moved TYPE (`EgressPolicy`, a module in Core) has no such
        /// rows, and the bare name is the use.
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

    /// The namespace-level modules of a `namespace` file, each with what its
    /// body declares, read as if the module were a file of its own.
    let private nestedModules (lines: string[]) (h: Header) : (string * string[] * Header) list = [
        for i in h.Line + 1 .. lines.Length - 1 do
            let m = nestedModuleRx.Match lines[i]

            if m.Success && m.Groups["indent"].Value = "" then
                let body =
                    lines
                    |> Array.skip (i + 1)
                    |> Array.takeWhile (fun l -> String.IsNullOrWhiteSpace l || indentOf l > 0)
                    |> Array.map (fun l -> if l.Length >= 4 then l.Substring 4 else "")

                m.Groups["name"].Value,
                body,
                {
                    h with
                        Keyword = "module"
                        AutoOpen = true
                        Line = -1
                }
    ]

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

                            let nested = nestedModules lines h

                            for kind, name in declarations lines (Some h) do
                                match kind with
                                | EntryKind.Type when staying.Contains(oldFull + "." + name) ->
                                    entries.Add(entry EntryKind.Shared oldNs name newNs)

                                    for moduleName, body, asFile in nested do
                                        if moduleName = name then
                                            for _, declared in declarations body (Some asFile) do
                                                entries.Add(
                                                    entry
                                                        EntryKind.Member
                                                        (oldFull + "." + name)
                                                        declared
                                                        (newFull + "." + name)
                                                )
                                | _ -> entries.Add(entry kind oldNs name newNs)

        List.ofSeq files, entries |> Seq.distinct |> List.ofSeq

    /// What an `open` of a path reaches, for the paths the name table cannot
    /// answer: everything `ToolUp.Platform` reaches (the table records only what
    /// moved), each moved module's members, and the union cases of every other
    /// path outside the tier, the only thing asked of those. One `(path, name)`
    /// per line of `reachResourceName`.
    let scanReach (repoRoot: string) : (string * string) list =
        let tierProjects = parts |> List.map _.Project |> Set.ofList

        let notTheSdk (project: string) =
            let name = Path.GetFileName project

            not (tierProjects.Contains name)
            && name <> "ToolUp.Platform.Build"
            && not (name.EndsWith ".Tests")

        let membersOf (lines: string[]) (h: Header) =
            let asNamespace = declarations lines (Some { h with Keyword = "namespace" })

            let asModule =
                declarations
                    lines
                    (Some {
                        h with
                            Keyword = "module"
                            AutoOpen = true
                    })

            asNamespace @ asModule |> List.map snd

        let outsideTier = [
            for project in
                Directory.GetDirectories(Path.Combine(repoRoot, "src"))
                |> Array.filter notTheSdk do
                let fsproj = Path.Combine(project, Path.GetFileName project + ".fsproj")

                if File.Exists fsproj then
                    for file in compileItems fsproj do
                        if File.Exists file then
                            let lines = readLines file

                            match header lines with
                            | None -> ()
                            | Some h ->
                                // A path under the platform is also reached
                                // through its first segment.
                                if h.Path.StartsWith(platform + ".") then
                                    platform, h.Path.Substring(platform.Length + 1).Split('.')[0]

                                let rowsAt (path: string) (asRead: Header) = [
                                    for kind, name in declarations lines (Some asRead) do
                                        if path = platform || kind = EntryKind.Case then
                                            path, name
                                ]

                                if h.Keyword = "namespace" then
                                    yield! rowsAt h.Path h
                                else
                                    yield! rowsAt h.Path { h with Keyword = "namespace" }

                                    if h.AutoOpen then
                                        yield! rowsAt (namespaceOf h) h
        ]

        let movedModules = [
            for part in parts do
                for file in compileItems (projectPath repoRoot part) do
                    let lines = readLines file

                    match header lines with
                    | Some h when h.Keyword = "module" ->
                        for name in membersOf lines h do
                            h.Path, name
                    | Some h ->
                        for moduleName, body, asFile in nestedModules lines h do
                            for name in membersOf body asFile do
                                h.Path + "." + moduleName, name
                    | None -> ()
        ]

        outsideTier @ movedModules |> List.distinct

    // ── The table on disk ─────────────────────────────────────────────

    /// The embedded resource's logical name in `ToolUp.Platform.Build`.
    let resourceName = "codemod-1198-namespaces.txt"

    /// The logical name of the reach table (`scanReach`). Unlike the name
    /// table it is not held to a fresh scan: it answers only what a tree below
    /// 0.39.0 could name, so a name added to Core since then cannot make it wrong.
    let reachResourceName = "codemod-1198-reach.txt"

    /// Renders the reach table, sorted, LF throughout.
    let renderReach (rows: (string * string) list) : string =
        let sb = StringBuilder()

        sb.Append "# Phase 1198 — what an open of a path reaches. Generated by\n"
        |> ignore

        sb.Append "# `dotnet run --project Build.fsproj -- SubsystemNamespaces --write-table`; do not edit.\n"
        |> ignore

        sb.Append "# path name\n" |> ignore

        for path, name in
            rows
            |> List.distinct
            |> List.sortWith (fun (a, b) (c, d) ->
                match String.CompareOrdinal(a, c) with
                | 0 -> String.CompareOrdinal(b, d)
                | o -> o) do
            sb.Append(path).Append(' ').Append(name).Append('\n') |> ignore

        sb.ToString()

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
        /// Path → the names an `open` of it reaches, for the paths that are
        /// known; a path absent here is never called dead.
        Reaches: Map<string, Set<string>>
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

        // A shared name with member rows is a module, and only its members
        // say the moved half is meant.
        let sharedModules =
            entries
            |> List.filter (fun e -> e.Kind = EntryKind.Member)
            |> List.map _.OldNamespace
            |> Set.ofList

        let mentions =
            entries
            |> List.choose (fun e ->
                match e.Kind with
                | EntryKind.Namespace -> None
                | EntryKind.Split -> None
                | EntryKind.Shared when sharedModules.Contains(join e.OldNamespace e.Name) -> None
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

        let reaches =
            entries
            |> List.choose (fun e ->
                match e.Kind with
                | EntryKind.Namespace
                | EntryKind.Split -> None
                | _ -> Some(e.NewNamespace, e.Name))
            |> List.groupBy fst
            |> List.map (fun (path, xs) -> path, xs |> List.map snd |> Set.ofList)
            |> Map.ofList

        {
            Moves = moves
            Mentions = mentions
            Splits = splits
            Reaches = reaches
        }

    /// The reach table shipped inside this assembly.
    let private embeddedReach: Lazy<(string * string) list> =
        lazy
            (let asm = System.Reflection.Assembly.GetExecutingAssembly()

             use stream = asm.GetManifestResourceStream reachResourceName

             if isNull stream then
                 failwithf "SubsystemNamespaces: embedded resource %s is missing from %s" reachResourceName asm.FullName

             use reader = new StreamReader(stream, Encoding.UTF8)

             [
                 for raw in reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries) do
                     let line = raw.Trim()

                     if not (line.StartsWith "#") then
                         match line.Split ' ' with
                         | [| path; name |] -> path, name
                         | _ -> failwithf "SubsystemNamespaces: malformed reach line: %s" line
             ])

    /// The index over the embedded tables.
    let embeddedIndex: Lazy<Index> =
        lazy
            (let idx = index embedded.Value

             let reaches =
                 embeddedReach.Value
                 |> List.fold
                     (fun (m: Map<string, Set<string>>) (path, name) ->
                         m.Add(path, (m.TryFind path |> Option.defaultValue Set.empty).Add name))
                     idx.Reaches

             { idx with Reaches = reaches })

    let private lineSplitter = Regex(@"(\r\n|\n|\r)", RegexOptions.CultureInvariant)

    let private openRx =
        Regex(@"^(?<indent>\s*)open\s+(?<type>type\s+)?(?<path>[A-Za-z_][\w.]*)\s*$", RegexOptions.CultureInvariant)

    let private qualifiedRx =
        Regex(@"(?<![\w.'`])ToolUp\.Platform(?:\.[A-Z]\w*)+", RegexOptions.CultureInvariant)

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

    let private typeHeadRx =
        Regex(
            @"^\s*(?:type|and)\s+(?:\[<[^\]]*>\]\s*)*(?:rec\s+|internal\s+|private\s+|public\s+)*(?<name>[A-Za-z_]\w*)[^=]*=\s*(?:internal\s+|private\s+|public\s+)?(?<rest>.*)$",
            RegexOptions.CultureInvariant
        )

    let private exceptionHeadRx =
        Regex(@"^\s*exception\s+(?<name>[A-Z]\w*)", RegexOptions.CultureInvariant)

    let private firstCaseRx =
        Regex(@"^(?<name>[A-Z]\w*)\s*(?:of\b|\|)", RegexOptions.CultureInvariant)

    let private activePatternRx =
        Regex(@"^\s*let\s[^=]*\(\|(?<ap>[^)]*)\|\)", RegexOptions.CultureInvariant)

    let private recordFieldRx =
        Regex(@"(?:^|[{;])\s*(?:mutable\s+)?(?<name>[A-Z]\w*)\s*:", RegexOptions.CultureInvariant)

    /// The types, union cases, record fields, exceptions and active patterns
    /// `lines` declare. A union case is read only under its `type`, so a match
    /// arm's `| Required` is not mistaken for one.
    let declaredNames (lines: string[]) : Set<string> =
        let code = maskNonCode lines
        let names = Collections.Generic.HashSet<string>()
        let mutable inUnion = false

        for line in code do
            let head = typeHeadRx.Match line

            if head.Success then
                names.Add head.Groups["name"].Value |> ignore
                let rest = head.Groups["rest"].Value.Trim()

                for field in recordFieldRx.Matches rest do
                    names.Add field.Groups["name"].Value |> ignore

                let first = firstCaseRx.Match rest

                if first.Success then
                    names.Add first.Groups["name"].Value |> ignore

                for c in inlineCaseRx.Matches rest do
                    names.Add c.Groups["name"].Value |> ignore

                inUnion <- rest = "" || rest.StartsWith "|" || first.Success
            else
                let t = line.Trim()

                if inUnion && t.StartsWith "|" then
                    let c = inlineCaseRx.Match t

                    if c.Success then
                        names.Add c.Groups["name"].Value |> ignore
                elif
                    inUnion
                    && t <> ""
                    && not (t.StartsWith "[<")
                    && t <> "private"
                    && t <> "internal"
                then
                    inUnion <- false

                let e = exceptionHeadRx.Match line

                if e.Success then
                    names.Add e.Groups["name"].Value |> ignore

                for field in recordFieldRx.Matches line do
                    names.Add field.Groups["name"].Value |> ignore

                let ap = activePatternRx.Match line

                if ap.Success then
                    for n in ap.Groups["ap"].Value.Split('|', StringSplitOptions.RemoveEmptyEntries) do
                        names.Add(n.Trim()) |> ignore

        Set.ofSeq names

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
        /// The names the consumer's own tree declares. A table `case` row of
        /// one of these derives nothing: a case name in a file says nothing
        /// about whose union it is, and the tree already has its own.
        TreeDeclared: Set<string>
    }

    /// A consumer file: everything is open to it.
    let consumerReach = {
        ReachesServer = true
        LevelCap = None
        Allowed = None
        TreeDeclared = Set.empty
    }

    let private isSkippable (l: string) =
        let t = l.TrimStart()

        String.IsNullOrWhiteSpace l || t.StartsWith "#" || t.StartsWith "[<"

    /// The `open` lines of the block starting at `first`.
    let private leadingOpens (code: string[]) (first: int) : int list =
        let rec go i acc =
            if i >= code.Length then List.rev acc
            elif openRx.IsMatch code[i] then go (i + 1) (i :: acc)
            elif isSkippable code[i] then go (i + 1) acc
            else List.rev acc

        go first []

    let private openPathAt (code: string[]) (i: int) =
        let m = openRx.Match code[i]

        if m.Success && m.Groups["type"].Value = "" then
            Some m.Groups["path"].Value
        else
            None

    /// An outermost nested module that carries its own opens: an open there
    /// covers the module's body and nothing else.
    type private ModuleScope = {
        /// The lines of the module's body.
        Body: int list
        /// The module's own `open` lines.
        Opens: int list
    }

    let private moduleScopes (code: string[]) (headerLine: int option) : ModuleScope list = [
        for i in 0 .. code.Length - 1 do
            let m = nestedModuleRx.Match code[i]

            if m.Success && m.Groups["indent"].Value = "" && Some i <> headerLine then
                let stop =
                    seq { i + 1 .. code.Length - 1 }
                    |> Seq.tryFind (fun j -> not (isSkippable code[j]) && indentOf code[j] = 0)
                    |> Option.defaultValue code.Length

                match leadingOpens code (i + 1) |> List.filter (fun o -> o < stop) with
                | [] -> ()
                | opens -> {
                    Body = [ i + 1 .. stop - 1 ]
                    Opens = opens
                  }
    ]

    let private localNames (code: string[]) =
        code
        |> Array.choose (fun l ->
            let m = localDeclRx.Match l
            if m.Success then Some m.Groups["name"].Value else None)
        |> Set.ofArray

    /// Names that resolve with or without a platform open, whichever union or
    /// module of the platform also spells them: a use of one keeps nothing alive.
    let private ambientNames =
        set [
            "Array"
            "Async"
            "DateTime"
            "DateTimeOffset"
            "Error"
            "Guid"
            "List"
            "Map"
            "None"
            "Ok"
            "Option"
            "Result"
            "Seq"
            "Set"
            "Some"
            "String"
            "System"
            "Task"
            "TimeSpan"
        ]

    /// Whether nothing on `body` names anything an open of `path` reaches. A
    /// path the tables do not know is never dead. A dotted mention counts its
    /// qualifier: `x.Foo` says nothing about an open reaching `Foo`.
    let private isDeadOpen
        (idx: Index)
        (code: string[])
        (headerLine: int option)
        (local: Set<string>)
        (body: int seq)
        (path: string)
        =
        match idx.Reaches.TryFind path with
        | None -> false
        | Some names ->
            body
            |> Seq.filter (fun i -> Some i <> headerLine && not (openRx.IsMatch code[i]))
            |> Seq.collect (fun i -> mentionRx.Matches code[i])
            |> Seq.forall (fun m ->
                let qualifier = m.Groups["qualifier"]

                let used =
                    if qualifier.Success then
                        qualifier.Value
                    else
                        m.Groups["name"].Value

                local.Contains used || ambientNames.Contains used || not (names.Contains used))

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

        // Every pass reads the code alone: what a string holds or a comment
        // says is neither an open, nor a path, nor a use of a name.
        let code = maskNonCode lines

        // 1. `open` lines of a moved path.
        for i in 0 .. lines.Length - 1 do
            let m = openRx.Match code[i]

            if m.Success then
                let path = m.Groups["path"]

                match moved idx.Moves path.Value with
                | Some target ->
                    let after =
                        lines[i].Substring(0, path.Index)
                        + target
                        + lines[i].Substring(path.Index + path.Length)

                    rewrites.Add(("1198-open", i, lines[i], after))
                    out[contentIndex i] <- after
                | None -> ()

        // 2. Qualified paths, outside headers and opens.
        for i in 0 .. lines.Length - 1 do
            let line = out[contentIndex i]

            if
                Some i <> headerLine
                && not (openRx.IsMatch code[i])
                && qualifiedRx.IsMatch code[i]
            then
                let after =
                    qualifiedRx.Replace(
                        line,
                        MatchEvaluator(fun m ->
                            if code[i][m.Index] <> m.Value[0] then
                                m.Value
                            else
                                moved idx.Moves m.Value |> Option.defaultValue m.Value)
                    )

                if after <> line then
                    rewrites.Add(("1198-qualified", i, line, after))
                    out[contentIndex i] <- after

        // 3. Derived opens.
        let current = maskNonCode [| for i in 0..2 .. out.Length - 1 -> out[i] |]

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

        let local = localNames current

        // A `case` row says nothing about whose union a name is, so one the
        // file or its tree declares for itself derives nothing, and neither
        // does one the file already resolves through an open outside the tier
        // (`Scalar` in a file opening `ToolUp.Facts`). `ToolUp.Platform` does
        // not count: a server case won over a Core case of the same spelling.
        let ownDeclared = Set.union reach.TreeDeclared (declaredNames lines)

        let resolvedElsewhere =
            current
            |> Array.choose (fun l ->
                let m = openRx.Match l
                if m.Success then Some m.Groups["path"].Value else None)
            |> Array.filter (fun path -> path <> platform && (levelOf path).IsNone)
            |> Array.choose idx.Reaches.TryFind
            |> Set.unionMany

        // `Existing = n` opening a line or following `{`, `{|` or `;` is a record
        // label, whichever record: it names no case.
        let isRecordLabel (line: string) (m: Match) =
            let before = line.Substring(0, m.Index).TrimEnd()
            let after = line.Substring(m.Index + m.Length).TrimStart()

            (before = ""
             || before.EndsWith "{"
             || before.EndsWith "{|"
             || before.EndsWith ";")
            && after.StartsWith "="
            && not (after.StartsWith "==")

        let targetsOf (name: string) =
            idx.Mentions.TryFind name
            |> Option.defaultValue []
            |> List.filter (fun (kind, _, _) ->
                not (
                    kind = EntryKind.Case
                    && (ownDeclared.Contains name || resolvedElsewhere.Contains name)
                ))

        // A shared module's member is usually a common word (`create`, `token`),
        // and undotted it is the module's only under an open of the old path.
        let countsBare (ns: string) (declaredIn: string) =
            let lastSegment = declaredIn.Substring(declaredIn.LastIndexOf '.' + 1)
            not (ns.EndsWith("." + lastSegment)) || opened.Contains declaredIn

        let collectMentions (mentioned: Collections.Generic.HashSet<string>) (body: int seq) =
            for i in body do
                let line = current[i]

                if Some i <> headerLine && not (openRx.IsMatch line) then
                    for m in mentionRx.Matches line do
                        let name = m.Groups["name"].Value
                        let qualifier = m.Groups["qualifier"]

                        // The qualifier is itself a bare mention: in a dotted mention of the form Qualifier.member, the qualifier half.
                        if qualifier.Success && not (local.Contains qualifier.Value) then
                            for _, ns, declaredIn in targetsOf qualifier.Value do
                                if countsBare ns declaredIn then
                                    mentioned.Add ns |> ignore

                        if not (local.Contains name) then
                            for kind, ns, declaredIn in targetsOf name do
                                if not (kind = EntryKind.Case && isRecordLabel line m) then
                                    // Unqualified, or qualified by the declaring
                                    // namespace's own last segment: a Core module
                                    // whose nested module moved is still reached
                                    // through the Core module's name.
                                    if not qualifier.Success then
                                        if countsBare ns declaredIn then
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

        let neededFrom (mentioned: string seq) (alreadyOpen: Set<string>) =
            mentioned
            |> Seq.filter (fun ns -> not (isOwn own ns) && ns <> ownPath && not (alreadyOpen.Contains ns))
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

        let scopes = moduleScopes current headerLine
        let inScopes = scopes |> List.collect _.Body |> Set.ofList
        let mentioned = Collections.Generic.HashSet<string>()

        // A file now declared in a part's namespace sees nothing of
        // `ToolUp.Platform` until it opens it.
        if own <> platform && own.StartsWith(platform + ".") && (levelOf own).IsSome then
            mentioned.Add platform |> ignore

        // A file whose namespace moved out of one other sources still
        // declare keeps seeing that half.
        match idx.Splits.TryFind own with
        | Some old -> mentioned.Add old |> ignore
        | None -> ()

        collectMentions mentioned (seq { 0 .. current.Length - 1 } |> Seq.filter (inScopes.Contains >> not))
        let needed = neededFrom mentioned opened

        let terminator = if out.Length > 1 then out[1] else Environment.NewLine

        let openLines (indent: string) (names: string list) =
            names |> List.map (fun ns -> sprintf "%sopen %s" indent ns)

        let record (at: int) (after: string) =
            rewrites.Add(("1198-derived-open", at, out[contentIndex at], after))
            out[contentIndex at] <- after

        /// Appends `open` lines after content line `at`.
        let insertAfter (at: int) (blankBefore: bool) (indent: string) (names: string list) =
            if not names.IsEmpty then
                let inserted = openLines indent names

                let block =
                    (if blankBefore then "" :: inserted else inserted) |> String.concat terminator

                record at (out[contentIndex at] + terminator + block)

        /// Puts `open` lines above content line `at`.
        let insertBefore (at: int) (indent: string) (names: string list) =
            if not names.IsEmpty then
                let block = openLines indent names |> String.concat terminator
                record at (block + terminator + out[contentIndex at])

        // Where the derived opens go decides what shadows what, and the
        // rule restores the resolution a file had while everything shared
        // `ToolUp.Platform`: a later open shadows an earlier one, a server
        // name won over a Core name of the same spelling (the server half
        // was the later contribution to the merged namespace), and a module
        // a file opened itself won over both. So the part opens take the
        // place of the file's `open ToolUp.Platform`, straight after it, with
        // the file's other opens still below them; a file with no such open
        // takes them above its own opens; and a tier file that lacks it takes
        // it straight after its header with the part opens right after it.
        // Where the two halves genuinely collided (a record with the same
        // labels on both sides) the codemod cannot know which was meant, and
        // the site takes a type annotation by hand — the migration doc lists
        // the shapes.
        let platformOpen, partOpens = needed |> List.partition ((=) platform)

        let indentAt (i: int) =
            (openRx.Match current[i]).Groups["indent"].Value

        let firstLine = (headerLine |> Option.defaultValue -1) + 1
        let fileOpens = leadingOpens current firstLine

        let platformLine (candidates: int list) =
            candidates |> List.tryFind (fun i -> openPathAt current i = Some platform)

        match platformOpen, platformLine fileOpens, fileOpens, headerLine with
        | _ :: _, _, _, Some hl -> insertAfter hl true "" (platformOpen @ partOpens)
        | _ :: _, _, _, None -> insertBefore 0 "" (platformOpen @ partOpens)
        | [], Some i, _, _ -> insertAfter i false (indentAt i) partOpens
        | [], None, first :: _, _ -> insertBefore first (indentAt first) partOpens
        | [], None, [], Some hl -> insertAfter hl true "" partOpens
        | [], None, [], None -> insertBefore 0 "" partOpens

        // A nested module with opens of its own takes what its body needs
        // there, not at namespace level. Its `open ToolUp.Platform` gives way
        // to them when its body reaches nothing through it, the one open this
        // pass removes; everywhere else a dead open is reported, not deleted.
        for scope in scopes do
            let inModule = Collections.Generic.HashSet<string>()
            collectMentions inModule scope.Body

            let alreadyOpen =
                scope.Opens
                |> List.choose (openPathAt current)
                |> Set.ofList
                |> Set.union opened
                |> Set.union (Set.ofList needed)

            match neededFrom inModule alreadyOpen with
            | [] -> ()
            | names ->
                let indent = indentAt scope.Opens.Head

                match platformLine scope.Opens with
                | Some p when isDeadOpen idx current headerLine local scope.Body platform ->
                    let path = (openRx.Match current[p]).Groups["path"]
                    let line = out[contentIndex p]

                    let replaced =
                        line.Substring(0, path.Index)
                        + names.Head
                        + line.Substring(path.Index + path.Length)

                    let rest = openLines indent names.Tail
                    record p (String.concat terminator (replaced :: rest))
                | Some p -> insertAfter p false indent names
                | None -> insertBefore scope.Opens.Head indent names

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

    /// The opens in a rewritten file the move looks to have killed: an open of
    /// `ToolUp.Platform` or of a path that moved, where nothing in the code it
    /// covers names anything it reaches. Each as `(1-based line, line text)`. A
    /// candidate only, and nothing here deletes one: the tables hold no record
    /// fields and no extension members, so only the compiler can confirm it.
    let likelyDeadOpens (idx: Index) (reach: FileReach) (text: string) : (int * string) list =
        if not reach.ReachesServer then
            []
        else
            let segments = lineSplitter.Split text
            let lines = [| for i in 0..2 .. segments.Length - 1 -> segments[i] |]
            let code = maskNonCode lines
            let headerLine = header lines |> Option.map _.Line
            let local = localNames code
            let scopes = moduleScopes code headerLine
            let scoped = scopes |> List.collect _.Opens |> Set.ofList
            let first = (headerLine |> Option.defaultValue -1) + 1
            let movedTo = idx.Moves |> List.map (fun (_, target, _) -> target) |> Set.ofList

            let candidates (opens: int list) (body: int seq) = [
                for i in opens do
                    match openPathAt code i with
                    | Some path when
                        (path = platform || movedTo.Contains path)
                        && isDeadOpen idx code headerLine local body path
                        ->
                        i + 1, lines[i].Trim()
                    | _ -> ()
            ]

            let fileLevel =
                candidates
                    (leadingOpens code first |> List.filter (scoped.Contains >> not))
                    (seq { 0 .. code.Length - 1 })

            fileLevel @ (scopes |> List.collect (fun s -> candidates s.Opens s.Body))

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
            /// What the tree's own sources declare, the tier's left out.
            Declared: Set<string>
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

            // Every source under the root, compiled or not: a name a project
            // declares is the tree's whichever project a file here belongs to.
            let declared =
                let rec sources (dir: string) = seq {
                    for f in Directory.EnumerateFiles dir do
                        if f.EndsWith ".fs" || f.EndsWith ".fsx" then
                            yield f

                    for d in Directory.EnumerateDirectories dir do
                        if not (pruned.Contains(Path.GetFileName d)) then
                            yield! sources d
                }

                sources (Path.GetFullPath root)
                |> Seq.filter (tierSources.Contains >> not)
                |> Seq.map (readLines >> declaredNames)
                |> Set.unionMany

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
                Declared = declared
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
                TreeDeclared = scope.Declared
            }