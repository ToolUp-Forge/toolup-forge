// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

// ─── The Feliz ratchet: a census that may only fall ──────────────────
//
// The move off Feliz is a programme of many phases, and its worst outcome
// is stalling half done with two UI idioms in the tree indefinitely. This
// module gives the programme a number and stops new Feliz arriving while
// it runs. Three counts, each kept per cell:
//
//   * `sources` — per project, the non-test `.fs` / `.fsx` files that
//     depend on the Feliz package: by an `open Feliz` line, or by a
//     qualified name (`Feliz.Html.div`, `Feliz.prop.className`,
//     `[<Feliz.ReactComponent>]`, `open type Feliz.prop`). A file counts
//     once however it reaches Feliz. Comments and string literals are not
//     code and never count, nor does a `namespace` / `module` declaration
//     line (a file naming itself), nor the namespaces of the other packages
//     that only share the prefix (`otherPackages`: the AG Grid and AG Charts
//     bindings and the SSR engine), which have their own exits;
//   * `tests` — the same count over test files (a path segment named
//     `tests`, or a project directory ending `.Tests` / holding `.Tests.`),
//     kept apart because a view's tests move WITH the view, so the count
//     may rise while a migration is in flight;
//   * `baselines` — per assembly, the lines of its committed public-API
//     baseline (`api-baselines/<assembly>.approved.txt`) that name
//     `ReactElement`, which is what a consumer's code is written against.
//
// A file's project is the nearest directory above it holding a `.fsproj`,
// named by its repository-relative path. The walk skips build output and
// tooling directories (`bin`, `obj`, `node_modules`, `fable_modules`,
// `output`, `artifacts`, any directory whose name starts with `.`) and the
// extracted documentation fragments under `docs-snippets/`, which are
// prose held by the documentation gates rather than code a project builds.
//
// The gate (`check`) holds the committed census against the tree, cell by
// cell, and is red in both directions: a cell that ROSE is the ratchet
// refusing new Feliz; a cell that FELL is a stale census, rewritten in the
// same change, so the committed number never drifts down by accident and
// every decrease is a reviewable line in a diff. The writer (`write`)
// refuses to raise a `sources` or `baselines` cell. The one way up is an
// ALLOWANCE: a dated entry, with its reason and the cells and amounts it
// admits, added to the committed file by hand (a reviewed act with an
// author). The next write admits rises up to those amounts and marks every
// unspent allowance spent, so it admits nothing again; the gate is red
// while an allowance sits unspent, so none can be staged for a later rise.
// A `tests` cell may be raised by the writer.
//
// FAKE-free and IO at the edges: `code`, `dependsOnFeliz`, `censusOf`,
// `render`, `parse` and `judge` are pure, `scan` / `check` / `write` read
// and write the tree.
[<RequireQualifiedAccess>]
module internal FelizRatchet =

    /// The committed census file, at the repository root.
    [<Literal>]
    let CensusFile = "feliz-census.json"

    /// The directory of public-API baselines, at the repository root.
    [<Literal>]
    let BaselineDir = "api-baselines"

    /// The suffix of a baseline file; the assembly is the name before it.
    [<Literal>]
    let BaselineSuffix = ".approved.txt"

    /// Which count a cell belongs to.
    type Class =
        | Sources
        | Tests
        | Baselines

    let className (c: Class) : string =
        match c with
        | Sources -> "sources"
        | Tests -> "tests"
        | Baselines -> "baselines"

    let classes = [ Sources; Tests; Baselines ]

    /// A dated, reasoned admission of a rise, held in the committed file.
    /// It admits each named `sources` / `baselines` cell rising by up to
    /// its amount, once: the write that uses it marks it spent.
    type Allowance = {
        Date: string
        Reason: string
        Sources: Map<string, int>
        Baselines: Map<string, int>
        Spent: bool
    }

    /// One census: per class, a cell per project (or assembly), present
    /// only when its count is above zero; and the allowances the committed
    /// file records (a census of the tree has none).
    type Census = {
        Sources: Map<string, int>
        Tests: Map<string, int>
        Baselines: Map<string, int>
        Allowances: Allowance list
    }

    let cells (c: Class) (census: Census) : Map<string, int> =
        match c with
        | Sources -> census.Sources
        | Tests -> census.Tests
        | Baselines -> census.Baselines

    let total (c: Class) (census: Census) : int = cells c census |> Map.values |> Seq.sum

    // ── What counts as a dependency on Feliz ──────────────────────────

    /// The first segment after `Feliz.` of the namespaces of OTHER packages
    /// that share the prefix: the forge's AG Grid and AG Charts bindings
    /// (and their runtime modules) and the server-side `Feliz.ViewEngine`.
    /// A qualified name or `open` under one of these is not a use of Feliz.
    let otherPackages =
        set [ "AgGrid"; "AgGridRuntime"; "AgCharts"; "AgChartRuntime"; "ViewEngine" ]

    /// The text with its comments (`//`, nested `(* *)`) and its string and
    /// character literals blanked to spaces, line breaks kept, so what is
    /// left is the code and every line keeps its number.
    let code (text: string) : string =
        let n = text.Length

        let at k =
            if k >= 0 && k < n then text.[k] else '\000'

        let sb = StringBuilder(n)

        let blank (a: int) (b: int) =
            for k in a .. b - 1 do
                let c = text.[k]
                sb.Append(if c = '\n' || c = '\r' then c else ' ') |> ignore

        let rec skipBlock (depth: int) (j: int) =
            if j >= n || depth = 0 then
                j
            elif at j = '(' && at (j + 1) = '*' then
                skipBlock (depth + 1) (j + 2)
            elif at j = '*' && at (j + 1) = ')' then
                skipBlock (depth - 1) (j + 2)
            else
                skipBlock depth (j + 1)

        let rec skipString (verbatim: bool) (j: int) =
            if j >= n then
                n
            elif not verbatim && at j = '\\' then
                skipString verbatim (j + 2)
            elif at j = '"' && verbatim && at (j + 1) = '"' then
                skipString verbatim (j + 2)
            elif at j = '"' then
                j + 1
            else
                skipString verbatim (j + 1)

        // A character literal's end, if one starts at `i` ('x', '\n', '\'',
        // 'A'); otherwise `None` — a type variable or a primed name.
        let charEnd (i: int) =
            if at (i + 1) = '\\' then
                [ i + 3 .. i + 11 ]
                |> List.tryFind (fun k -> at k = '\'')
                |> Option.map (fun k -> k + 1)
            elif at (i + 2) = '\'' && at (i + 1) <> '\n' && at (i + 1) <> '\'' then
                Some(i + 3)
            else
                None

        let rec go (i: int) =
            if i < n then
                let c = text.[i]

                if c = '/' && at (i + 1) = '/' then
                    let e =
                        match text.IndexOf('\n', i) with
                        | -1 -> n
                        | e -> e

                    blank i e
                    go e
                elif c = '(' && at (i + 1) = '*' && at (i + 2) <> ')' then
                    let e = min n (skipBlock 1 (i + 2))
                    blank i e
                    go e
                elif c = '"' && at (i + 1) = '"' && at (i + 2) = '"' then
                    let e =
                        match text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal) with
                        | -1 -> n
                        | e -> e + 3

                    blank i e
                    go e
                elif c = '"' then
                    let verbatim = at (i - 1) = '@' || (at (i - 1) = '$' && at (i - 2) = '@')
                    let e = min n (skipString verbatim (i + 1))
                    blank i e
                    go e
                elif c = '\'' then
                    match charEnd i with
                    | Some e when e <= n ->
                        blank i e
                        go e
                    | _ ->
                        sb.Append c |> ignore
                        go (i + 1)
                else
                    sb.Append c |> ignore
                    go (i + 1)

        go 0
        sb.ToString()

    let private openFelizLine =
        Regex(@"^[ \t]*open[ \t]+Feliz[ \t]*\r?$", RegexOptions.Multiline ||| RegexOptions.CultureInvariant)

    // A top-level `namespace` / `module` declaration: the file naming itself.
    let private declarationLine =
        Regex(
            @"^[ \t]*(namespace|module)[ \t]+((rec|public|internal|private)[ \t]+)*(global\.)?[\w.]+[ \t]*\r?$",
            RegexOptions.Multiline ||| RegexOptions.CultureInvariant
        )

    // `Feliz.<segment>` not preceded by another name (`ToolUp.Feliz.AgGrid`
    // is a package id, not a use), except through `global.`.
    let private qualifiedName =
        Regex(@"(?:(?<=\bglobal\.)|(?<![\w.'`]))Feliz\.([A-Za-z_]\w*)", RegexOptions.CultureInvariant)

    /// Whether a source file's text depends on Feliz: an `open Feliz` line,
    /// or a qualified `Feliz.` name outside `otherPackages`, in its code
    /// (comments, strings and declaration lines aside).
    let dependsOnFeliz (text: string) : bool =
        let code = declarationLine.Replace(code text, "")

        openFelizLine.IsMatch code
        || qualifiedName.Matches code
           |> Seq.exists (fun m -> not (otherPackages.Contains m.Groups.[1].Value))

    let private reactElement = Regex(@"\bReactElement\b", RegexOptions.CultureInvariant)

    /// The lines of a baseline's text that name `ReactElement`.
    let reactElementLines (text: string) : int =
        text.Split('\n') |> Array.filter reactElement.IsMatch |> Array.length

    let private segments (relPath: string) =
        relPath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)

    /// Whether a repository-relative source path is a test file.
    let isTest (relPath: string) : bool =
        segments relPath
        |> Array.exists (fun s ->
            s.Equals("tests", StringComparison.OrdinalIgnoreCase)
            || s.EndsWith(".Tests", StringComparison.Ordinal)
            || s.Contains(".Tests.", StringComparison.Ordinal))

    /// The cell a source file counts in: the nearest directory above it
    /// that `projectDirs` names (repository-relative, `/`-separated), else
    /// `(no project)/<its top directory>`.
    let projectOf (projectDirs: Set<string>) (relPath: string) : string =
        let parts = segments relPath
        let dirs = parts.[0 .. parts.Length - 2]

        let nearest =
            [ dirs.Length .. -1 .. 1 ]
            |> List.map (fun n -> String.Join("/", dirs.[0 .. n - 1]))
            |> List.tryFind projectDirs.Contains

        match nearest with
        | Some dir -> dir
        | None when dirs.Length = 0 -> "(no project)"
        | None -> sprintf "(no project)/%s" dirs.[0]

    let private countBy (pairs: string seq) : Map<string, int> = pairs |> Seq.countBy id |> Map.ofSeq

    /// The census of a tree given as data: its project directories, its
    /// source files (repository-relative path, text) and its baselines
    /// (assembly, text).
    let censusOf
        (projectDirs: Set<string>)
        (sources: (string * string) seq)
        (baselines: (string * string) seq)
        : Census =
        let felizFiles =
            sources |> Seq.filter (snd >> dependsOnFeliz) |> Seq.map fst |> List.ofSeq

        {
            Sources =
                felizFiles
                |> List.filter (isTest >> not)
                |> Seq.map (projectOf projectDirs)
                |> countBy
            Tests = felizFiles |> List.filter isTest |> Seq.map (projectOf projectDirs) |> countBy
            Baselines =
                baselines
                |> Seq.map (fun (assembly, text) -> assembly, reactElementLines text)
                |> Seq.filter (fun (_, n) -> n > 0)
                |> Map.ofSeq
            Allowances = []
        }

    // ── Rendering and parsing the committed file ─────────────────────

    let private header =
        "GENERATED by the FelizRatchet target (run it with --write, or the ratchet script with -Write); the gate in the Build pack holds it against the tree. sources: per project, non-test .fs/.fsx files that depend on Feliz, by an open Feliz line or a qualified Feliz. name (comments, strings, declaration lines and the other Feliz.* packages aside). tests: the same over test files, which may rise while their views move. baselines: per assembly, api-baselines lines naming ReactElement. A sources or baselines cell may only fall; the one way up is a dated allowance added here by hand, which the next write spends. Every change to a cell is this file rewritten in the same change."

    let private quote (s: string) = JsonSerializer.Serialize s

    let private inlineMap (m: Map<string, int>) =
        match Map.toList m with
        | [] -> "{}"
        | entries ->
            entries
            |> List.map (fun (cell, n) -> sprintf "%s: %d" (quote cell) n)
            |> String.concat ", "
            |> sprintf "{ %s }"

    /// The census as the committed file's text: sorted, LF line endings,
    /// a trailing newline, so equal censuses render to equal bytes.
    let render (census: Census) : string =
        let sb = StringBuilder()
        let line (s: string) = sb.Append(s).Append('\n') |> ignore

        line "{"
        line (sprintf "  \"kind\": \"felizCensus\",")
        line (sprintf "  \"_\": %s," (quote header))

        line (
            sprintf
                "  \"totals\": { \"sources\": %d, \"tests\": %d, \"baselines\": %d },"
                (total Sources census)
                (total Tests census)
                (total Baselines census)
        )

        for c in classes do
            match cells c census |> Map.toList with
            | [] -> line (sprintf "  %s: {}," (quote (className c)))
            | entries ->
                line (sprintf "  %s: {" (quote (className c)))

                entries
                |> List.iteri (fun j (cell, n) ->
                    let itemComma = if j = entries.Length - 1 then "" else ","
                    line (sprintf "    %s: %d%s" (quote cell) n itemComma))

                line "  },"

        match census.Allowances with
        | [] -> line "  \"allowances\": []"
        | allowances ->
            line "  \"allowances\": ["

            allowances
            |> List.iteri (fun j a ->
                line "    {"
                line (sprintf "      \"date\": %s," (quote a.Date))
                line (sprintf "      \"reason\": %s," (quote a.Reason))
                line (sprintf "      \"sources\": %s," (inlineMap a.Sources))
                line (sprintf "      \"baselines\": %s," (inlineMap a.Baselines))
                line (sprintf "      \"spent\": %b" a.Spent)
                line (if j = allowances.Length - 1 then "    }" else "    },"))

            line "  ]"

        line "}"
        sb.ToString()

    let private isoDate = Regex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)

    let private readCells (o: JsonElement) : Map<string, int> =
        o.EnumerateObject()
        |> Seq.map (fun p -> p.Name, p.Value.GetInt32())
        |> Map.ofSeq

    let private readAllowance (i: int) (e: JsonElement) : Result<Allowance, string> =
        let field (name: string) (kind: JsonValueKind) =
            match e.TryGetProperty name with
            | true, v when v.ValueKind = kind -> Some v
            | _ -> None

        let cellsOf name =
            field name JsonValueKind.Object |> Option.map readCells

        match
            field "date" JsonValueKind.String,
            field "reason" JsonValueKind.String,
            cellsOf "sources",
            cellsOf "baselines"
        with
        | Some date, Some reason, Some sources, Some baselines ->
            let spent =
                match e.TryGetProperty "spent" with
                | true, v when v.ValueKind = JsonValueKind.True -> Some true
                | true, v when v.ValueKind = JsonValueKind.False -> Some false
                | _ -> None

            let amounts = Seq.append (Map.values sources) (Map.values baselines) |> List.ofSeq

            if not (isoDate.IsMatch(date.GetString())) then
                Error(sprintf "allowance %d: `date` is not a YYYY-MM-DD date" i)
            elif String.IsNullOrWhiteSpace(reason.GetString()) then
                Error(sprintf "allowance %d: `reason` is empty" i)
            elif amounts.IsEmpty || amounts |> List.exists (fun n -> n <= 0) then
                Error(sprintf "allowance %d: it must admit at least one cell, each by a positive amount" i)
            else
                match spent with
                | None -> Error(sprintf "allowance %d: `spent` is not true or false" i)
                | Some spent ->
                    Ok {
                        Date = date.GetString()
                        Reason = reason.GetString()
                        Sources = sources
                        Baselines = baselines
                        Spent = spent
                    }
        | _ -> Error(sprintf "allowance %d needs `date`, `reason`, `sources` and `baselines`" i)

    /// Read a committed census, refusing a file that is not one.
    let parse (text: string) : Result<Census, string> =
        try
            use doc = JsonDocument.Parse text
            let root = doc.RootElement

            match root.TryGetProperty "kind" with
            | true, k when k.ValueKind = JsonValueKind.String && k.GetString() = "felizCensus" ->
                let read (c: Class) =
                    match root.TryGetProperty(className c) with
                    | true, o when o.ValueKind = JsonValueKind.Object -> Ok(readCells o)
                    | _ -> Error(sprintf "the census has no `%s` object" (className c))

                let allowances =
                    match root.TryGetProperty "allowances" with
                    | false, _ -> Ok []
                    | true, a when a.ValueKind = JsonValueKind.Array ->
                        a.EnumerateArray()
                        |> Seq.mapi readAllowance
                        |> List.ofSeq
                        |> List.fold
                            (fun acc r ->
                                match acc, r with
                                | Ok xs, Ok x -> Ok(xs @ [ x ])
                                | Error e, _
                                | _, Error e -> Error e)
                            (Ok [])
                    | true, _ -> Error "the census's `allowances` is not an array"

                match read Sources, read Tests, read Baselines, allowances with
                | Ok s, Ok t, Ok b, Ok a ->
                    Ok {
                        Sources = s
                        Tests = t
                        Baselines = b
                        Allowances = a
                    }
                | Error e, _, _, _
                | _, Error e, _, _
                | _, _, Error e, _
                | _, _, _, Error e -> Error e
            | _ -> Error "the file is not a Feliz census (`kind` is not \"felizCensus\")"
        with e ->
            Error(sprintf "the census does not parse: %s" e.Message)

    // ── The judgement ─────────────────────────────────────────────────

    /// One cell whose count differs between the committed census and the
    /// tree.
    type Drift = {
        Class: Class
        Cell: string
        Committed: int
        Current: int
    }

    /// Whether a drift raises a cell the ratchet holds (`sources` or
    /// `baselines`).
    let isRise (d: Drift) : bool =
        d.Current > d.Committed && d.Class <> Tests

    /// Every cell that differs, in class then cell order. Allowances play
    /// no part: the gate judges cells alone.
    let judge (committed: Census) (current: Census) : Drift list = [
        for c in classes do
            let was = cells c committed
            let now = cells c current

            let keys = Seq.append (Map.keys was) (Map.keys now) |> Seq.distinct |> Seq.sort

            for cell in keys do
                let a = was |> Map.tryFind cell |> Option.defaultValue 0
                let b = now |> Map.tryFind cell |> Option.defaultValue 0

                if a <> b then
                    {
                        Class = c
                        Cell = cell
                        Committed = a
                        Current = b
                    }
    ]

    /// How far the unspent allowances admit a drift's cell to rise.
    let admitted (allowances: Allowance list) (d: Drift) : int =
        allowances
        |> List.filter (fun a -> not a.Spent)
        |> List.sumBy (fun a ->
            match d.Class with
            | Sources -> a.Sources
            | Baselines -> a.Baselines
            | Tests -> Map.empty
            |> Map.tryFind d.Cell
            |> Option.defaultValue 0)

    /// One line naming a drift and what to do about it.
    let describe (d: Drift) : string =
        let where =
            sprintf "%s `%s`: committed %d, tree %d" (className d.Class) d.Cell d.Committed d.Current

        if isRise d then
            sprintf
                "%s — Feliz may only fall. Remove the new Feliz use (an `open Feliz` line, a qualified `Feliz.` name, or a new ReactElement surface) rather than raising the census."
                where
        elif d.Current > d.Committed then
            sprintf "%s — test files may rise, but the census is stale: rewrite it in this change." where
        else
            sprintf "%s — the count fell; rewrite the census in this change so the decrease is recorded." where

    let private describeUnspent (a: Allowance) =
        sprintf
            "the allowance dated %s is unspent: rewrite the census with --write to spend it on the rise it names, or remove it (an allowance is never held over for a later rise)."
            a.Date

    // ── The tree ──────────────────────────────────────────────────────

    let private skippedDirs =
        set [ "bin"; "obj"; "node_modules"; "fable_modules"; "output"; "artifacts" ]

    let private rel (root: string) (path: string) =
        Path.GetRelativePath(root, path).Replace('\\', '/')

    let rec private walk (root: string) (dir: string) : string seq = seq {
        for file in Directory.EnumerateFiles dir do
            yield file

        for sub in Directory.EnumerateDirectories dir do
            let name = Path.GetFileName sub

            if
                not (name.StartsWith ".")
                && not (skippedDirs.Contains name)
                && rel root sub <> "docs-snippets"
            then
                yield! walk root sub
    }

    /// The census of the tree at `root`.
    let scan (root: string) : Census =
        let root = Path.GetFullPath root
        let files = walk root root |> List.ofSeq

        let projectDirs =
            files
            |> List.filter (fun f -> f.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase))
            |> List.map (Path.GetDirectoryName >> rel root)
            |> Set.ofList

        let sources =
            files
            |> List.filter (fun f ->
                f.EndsWith(".fs", StringComparison.OrdinalIgnoreCase)
                || f.EndsWith(".fsx", StringComparison.OrdinalIgnoreCase))
            |> List.map (fun f -> rel root f, File.ReadAllText f)

        let baselineDir = Path.Combine(root, BaselineDir)

        let baselines =
            if Directory.Exists baselineDir then
                Directory.EnumerateFiles(baselineDir, "*" + BaselineSuffix)
                |> Seq.map (fun f ->
                    let name = Path.GetFileName f
                    name.Substring(0, name.Length - BaselineSuffix.Length), File.ReadAllText f)
                |> List.ofSeq
            else
                []

        censusOf projectDirs sources baselines

    let private censusPath (root: string) = Path.Combine(root, CensusFile)

    let private summary (verb: string) (census: Census) =
        sprintf
            "%s %d source file(s), %d test file(s), %d ReactElement baseline line(s)."
            verb
            (total Sources census)
            (total Tests census)
            (total Baselines census)

    /// Hold the committed census against the tree at `root`: `Ok` with a
    /// one-line summary, or `Error` with one line per finding.
    let check (root: string) : Result<string, string list> =
        let path = censusPath root

        if not (File.Exists path) then
            Error [ sprintf "%s is missing: write the census with --write." CensusFile ]
        else
            let text = File.ReadAllText(path).Replace("\r\n", "\n")

            match parse text with
            | Error why -> Error [ sprintf "%s: %s" CensusFile why ]
            | Ok committed ->
                let current = {
                    scan root with
                        Allowances = committed.Allowances
                }

                let unspent =
                    committed.Allowances
                    |> List.filter (fun a -> not a.Spent)
                    |> List.map describeUnspent

                match judge committed current |> List.map describe, unspent with
                | [], [] when text <> render current ->
                    Error [
                        sprintf
                            "%s: every cell matches, but the file is not the census's render (a hand-edited total or layout): rewrite it with --write."
                            CensusFile
                    ]
                | [], [] -> Ok(summary "Feliz census holds:" current)
                | drifts, unspent -> Error(drifts @ unspent)

    /// Write the tree's census at `root`. Refused, writing nothing, when it
    /// would raise a `sources` or `baselines` cell over the committed file
    /// beyond what its unspent allowances admit; a write marks every
    /// allowance spent.
    let write (root: string) : Result<string, string list> =
        let path = censusPath root
        let current = scan root

        let outcome =
            if File.Exists path then
                match parse (File.ReadAllText path) with
                | Error why ->
                    Error [
                        sprintf "%s: %s — delete it to seed a first census, deliberately." CensusFile why
                    ]
                | Ok committed ->
                    match
                        judge committed current
                        |> List.filter (fun d -> isRise d && d.Current - d.Committed > admitted committed.Allowances d)
                    with
                    | [] -> Ok(committed.Allowances |> List.map (fun a -> { a with Spent = true }))
                    | rises -> Error(rises |> List.map describe)
            else
                Ok []

        match outcome with
        | Error lines -> Error lines
        | Ok allowances ->
            File.WriteAllText(path, render { current with Allowances = allowances })
            Ok(summary (sprintf "wrote %s:" CensusFile) current)