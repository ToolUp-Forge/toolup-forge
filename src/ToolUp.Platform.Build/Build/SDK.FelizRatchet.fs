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
//     carry an `open Feliz` line (the React DSL itself; `open Feliz.AgGrid`,
//     `open Feliz.ViewEngine` and the other `Feliz.*` namespaces are not
//     counted — they are separate packages with their own exits);
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
// refuses to raise a `sources` or `baselines` cell: the only way up is a
// hand edit of the committed file, which is a reviewed act with an author.
// A `tests` cell may be raised by the writer.
//
// FAKE-free and IO at the edges: `censusOf`, `render`, `parse` and `judge`
// are pure, `scan` / `check` / `write` read and write the tree.
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

    /// One census: per class, a cell per project (or assembly). A cell is
    /// present only when its count is above zero.
    type Census = {
        Sources: Map<string, int>
        Tests: Map<string, int>
        Baselines: Map<string, int>
    }

    let cells (c: Class) (census: Census) : Map<string, int> =
        match c with
        | Sources -> census.Sources
        | Tests -> census.Tests
        | Baselines -> census.Baselines

    let total (c: Class) (census: Census) : int = cells c census |> Map.values |> Seq.sum

    let private openFelizLine =
        Regex(@"^[ \t]*open[ \t]+Feliz[ \t]*(//.*)?\r?$", RegexOptions.Multiline ||| RegexOptions.CultureInvariant)

    let private reactElement = Regex(@"\bReactElement\b", RegexOptions.CultureInvariant)

    /// Whether a source file's text carries an `open Feliz` line.
    let opensFeliz (text: string) : bool = openFelizLine.IsMatch text

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
            sources |> Seq.filter (snd >> opensFeliz) |> Seq.map fst |> List.ofSeq

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
        }

    // ── Rendering and parsing the committed file ─────────────────────

    let private header =
        "GENERATED by the FelizRatchet target (run it with --write, or the ratchet script with -Write); the gate in the Build pack holds it against the tree. sources: per project, non-test .fs/.fsx files with an open Feliz line. tests: the same over test files, which may rise while their views move. baselines: per assembly, api-baselines lines naming ReactElement. A sources or baselines cell may only fall; every change to a cell is this file rewritten in the same change."

    let private quote (s: string) = JsonSerializer.Serialize s

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

        classes
        |> List.iteri (fun i c ->
            let entries = cells c census |> Map.toList
            let comma = if i = classes.Length - 1 then "" else ","

            match entries with
            | [] -> line (sprintf "  %s: {}%s" (quote (className c)) comma)
            | _ ->
                line (sprintf "  %s: {" (quote (className c)))

                entries
                |> List.iteri (fun j (cell, n) ->
                    let itemComma = if j = entries.Length - 1 then "" else ","
                    line (sprintf "    %s: %d%s" (quote cell) n itemComma))

                line (sprintf "  }%s" comma))

        line "}"
        sb.ToString()

    /// Read a committed census, refusing a file that is not one.
    let parse (text: string) : Result<Census, string> =
        try
            use doc = JsonDocument.Parse text
            let root = doc.RootElement

            match root.TryGetProperty "kind" with
            | true, k when k.ValueKind = JsonValueKind.String && k.GetString() = "felizCensus" ->
                let read (c: Class) =
                    match root.TryGetProperty(className c) with
                    | true, o when o.ValueKind = JsonValueKind.Object ->
                        o.EnumerateObject()
                        |> Seq.map (fun p -> p.Name, p.Value.GetInt32())
                        |> Map.ofSeq
                        |> Ok
                    | _ -> Error(sprintf "the census has no `%s` object" (className c))

                match read Sources, read Tests, read Baselines with
                | Ok s, Ok t, Ok b ->
                    Ok {
                        Sources = s
                        Tests = t
                        Baselines = b
                    }
                | Error e, _, _
                | _, Error e, _
                | _, _, Error e -> Error e
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

    /// Every cell that differs, in class then cell order.
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

    /// One line naming a drift and what to do about it.
    let describe (d: Drift) : string =
        let where =
            sprintf "%s `%s`: committed %d, tree %d" (className d.Class) d.Cell d.Committed d.Current

        if isRise d then
            sprintf
                "%s — Feliz may only fall. Remove the new `open Feliz` (or the new ReactElement surface) rather than raising the census."
                where
        elif d.Current > d.Committed then
            sprintf "%s — test files may rise, but the census is stale: rewrite it in this change." where
        else
            sprintf "%s — the count fell; rewrite the census in this change so the decrease is recorded." where

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
                let current = scan root

                match judge committed current with
                | [] when text <> render current ->
                    Error [
                        sprintf
                            "%s: every cell matches, but the file is not the census's render (a hand-edited total or layout): rewrite it with --write."
                            CensusFile
                    ]
                | [] ->
                    Ok(
                        sprintf
                            "Feliz census holds: %d source file(s), %d test file(s), %d ReactElement baseline line(s)."
                            (total Sources current)
                            (total Tests current)
                            (total Baselines current)
                    )
                | drifts -> Error(drifts |> List.map describe)

    /// Write the tree's census at `root`. Refused, writing nothing, when it
    /// would raise a `sources` or `baselines` cell over the committed file.
    let write (root: string) : Result<string, string list> =
        let path = censusPath root
        let current = scan root

        let refusal =
            if File.Exists path then
                match parse (File.ReadAllText path) with
                | Error why ->
                    Some [
                        sprintf "%s: %s — delete it to seed a first census, deliberately." CensusFile why
                    ]
                | Ok committed ->
                    match judge committed current |> List.filter isRise with
                    | [] -> None
                    | rises -> Some(rises |> List.map describe)
            else
                None

        match refusal with
        | Some lines -> Error lines
        | None ->
            File.WriteAllText(path, render current)

            Ok(
                sprintf
                    "wrote %s: %d source file(s), %d test file(s), %d ReactElement baseline line(s)."
                    CensusFile
                    (total Sources current)
                    (total Tests current)
                    (total Baselines current)
            )