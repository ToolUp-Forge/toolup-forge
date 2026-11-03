// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.GateReadsCensusTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open ToolUp.Forge

// ─── The census of what the gate reads ────────────────────────────────
//
// `gate-reads.json` names the non-code files the gate reads beyond the
// .NET default; everything outside the union is gate-inert, so a commit
// touching only such files lands without a re-gate. A file wrongly left
// out is a file a reuse stands across unmeasured, so the record is checked
// here rather than trusted.
//
// .NET has no hook on `File.ReadAllText` short of instrumenting every call
// site, so the census is STATIC, and it is sound in the direction that
// matters by over-reporting. A file is NAMED, and must be in `reads` or
// `notRead`, when:
//
//   * a source that reaches the real repository (it mentions a root
//     locator: `repoRoot`, `ToolUp.Forge.sln`, `__SOURCE_DIRECTORY__`,
//     `CorpusAnchor`), or a driver file at the repository root, or a
//     source of `ToolUp.Platform.Build` (the Build pack links them, and
//     they name their files by constant), carries a string literal equal
//     to the file's NAME or its repo-relative PATH, or a
//     `Path.Combine(<locator>, "a", "b", ...)` whose literal parts are a
//     prefix of the path (which covers a directory walk);
//   * a project, props or targets file outside `templates/` carries an
//     `Include` / `Update` item that resolves to the file (the build copies
//     or embeds it: this is how the Build pack reaches `perf-budgets.json`).
//
// Every over-report — a fixture that writes a `README.md` into a temp
// directory shares the literal — is settled by a reviewed `notRead` entry
// with its reason. What it cannot see, stated rather than discovered: a
// path assembled with no literal naming it and no locator in its file, and
// a read made by product code that follows a reference held in DATA. The
// falsifier is a test that opens a repository file named by no literal of
// any source above. The `reads` declarations in `GateManifest.fs` were
// written from this census and from the sources it points at; the census
// was shown red by hiding the `perf-budgets.json` entry (the failure named
// the file and the Build pack project that copies it).

let repoRoot =
    let rec walkUp (dir: DirectoryInfo) =
        if isNull (box dir) then
            None
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            Some dir.FullName
        else
            walkUp dir.Parent

    match walkUp (DirectoryInfo AppContext.BaseDirectory) with
    | Some root -> root
    | None -> failwith "could not locate the repo root (ToolUp.Forge.sln) — the census would be vacuously green"

/// This file's own name: its go-red fixtures quote paths on purpose.
let private selfName = "GateReadsCensusTests.fs"

/// The paths the landing tooling treats as gate inputs for any repository
/// that carries the record, before the record's own `reads`: sources,
/// tests, proofs, every build and project file, the tool manifest under
/// `.config/`, the formatter's configuration and the gate script. Mirrored
/// here so the census asks only about files the default does not cover; a
/// spec missing from this copy makes the census stricter, never looser.
let dotnetDefault = [
    "src/**"
    "tests/**"
    "proofs/**"
    ".config/**"
    "*.slnx"
    "**/*.slnx"
    "*.sln"
    "**/*.sln"
    "*.fsproj"
    "**/*.fsproj"
    "*.csproj"
    "**/*.csproj"
    "*.props"
    "**/*.props"
    "*.targets"
    "**/*.targets"
    "*.fs"
    "**/*.fs"
    "*.fsi"
    "**/*.fsi"
    "*.fsx"
    "**/*.fsx"
    "*.cs"
    "**/*.cs"
    "**/packages.lock.json"
    "global.json"
    "nuget.config"
    "NuGet.config"
    "NuGet.Config"
    ".editorconfig"
    "**/.editorconfig"
    ".fantomasignore"
    ".gitattributes"
    "verify.ps1"
    "run.ps1"
    GateManifest.GateReadsFile
]

/// Every project directory the solution names, as `<dir>/**`: the
/// landing tooling counts each as a gate input too.
let solutionProjectDirs (slnText: string) : string list =
    [
        for m in Regex.Matches(slnText, "\"([^\"]+\\.(?:fs|cs)proj)\"") do
            let p = m.Groups[1].Value.Replace('\\', '/')
            let i = p.LastIndexOf '/'

            if i > 0 then
                yield p.Substring(0, i) + "/**"
    ]
    |> List.distinct

// ─── The census (PURE: the go-red cases run THIS) ─────────────────────

let private locator =
    Regex(@"repoRoot|RepoRoot|ToolUp\.Forge\.sln|__SOURCE_DIRECTORY__|CorpusAnchor", RegexOptions.Compiled)

let private literal = Regex("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled)

let private combine =
    Regex(@"Path\.Combine\(([^()]*(?:\([^()]*\)[^()]*)*)\)", RegexOptions.Compiled)

let private item = Regex("(?:Include|Update)=\"([^\"]+)\"", RegexOptions.Compiled)

let private norm (s: string) =
    s.Replace(@"\\", "/").Replace('\\', '/').Trim('/')

/// Is `rel` (a source's repo-relative path) one the census reads whatever
/// it mentions: a root driver file or a `ToolUp.Platform.Build` source?
let private alwaysRead (rel: string) =
    not (rel.Contains "/")
    || rel.StartsWith("src/ToolUp.Platform.Build/", StringComparison.Ordinal)

/// The names a source gives the census: its literals and its locator
/// `Path.Combine` prefixes. `None` when the source does not reach the
/// repository.
let private sourceNames (rel: string, text: string) =
    if not (alwaysRead rel || locator.IsMatch text) then
        None
    else
        let lits =
            [ for m in literal.Matches text -> norm m.Groups[1].Value ]
            |> List.filter (fun s -> s <> "")
            |> Set.ofList

        let prefixes = [
            for m in combine.Matches text do
                let args = m.Groups[1].Value

                if locator.IsMatch(args.Split(',')[0]) then
                    match
                        [ for p in literal.Matches args -> norm p.Groups[1].Value ]
                        |> List.filter ((<>) "")
                    with
                    | [] -> ()
                    | parts -> yield String.Join("/", parts)
        ]

        Some(rel, lits, prefixes)

/// The repo-relative paths a project file's items resolve to.
let private projectItems (rel: string, text: string) : string list = [
    let dir =
        match rel.LastIndexOf '/' with
        | -1 -> ""
        | i -> rel.Substring(0, i)

    for m in item.Matches text do
        for part in m.Groups[1].Value.Split(';') do
            let v = part.Trim().Replace("$(MSBuildThisFileDirectory)", "").Replace('\\', '/')

            if v <> "" && not (v.Contains "$(") && not (v.Contains "*") then
                let full = Path.GetFullPath(Path.Combine("/r", dir, v)).Replace('\\', '/')
                let anchor = Path.GetFullPath("/r").Replace('\\', '/').TrimEnd('/') + "/"

                if full.StartsWith(anchor, StringComparison.Ordinal) then
                    yield full.Substring anchor.Length
]

/// Which of `candidates` the `sources` (F# text) and `projects` (MSBuild
/// text) name, each with the files that name it. Both inputs are
/// `(repo-relative path, text)`.
let census
    (sources: (string * string) list)
    (projects: (string * string) list)
    (candidates: string list)
    : (string * string list) list =
    let perSource = sources |> List.choose sourceNames

    let perProject =
        projects
        |> List.map (fun (rel, text) -> rel, Set.ofList (projectItems (rel, text)))

    candidates
    |> List.choose (fun c ->
        let name = c.Substring(c.LastIndexOf('/') + 1)

        let bySource =
            perSource
            |> List.filter (fun (_, lits, prefixes) ->
                Set.contains name lits
                || Set.contains c lits
                || prefixes
                   |> List.exists (fun p -> c = p || c.StartsWith(p + "/", StringComparison.Ordinal)))
            |> List.map (fun (rel, _, _) -> rel)

        let byProject =
            perProject
            |> List.filter (fun (_, items) -> Set.contains c items)
            |> List.map fst

        match bySource @ byProject with
        | [] -> None
        | by -> Some(c, by))

/// The named files no spec in `covered` covers, each with who named it.
let uncovered (named: (string * string list) list) (covered: string list) =
    named
    |> List.filter (fun (path, _) -> not (covered |> List.exists (fun s -> GateManifest.specCovers s path)))

// ─── The real repository ──────────────────────────────────────────────

let private tracked =
    lazy
        (let psi =
            Diagnostics.ProcessStartInfo("git", "ls-files -z", WorkingDirectory = repoRoot)

         psi.RedirectStandardOutput <- true
         psi.UseShellExecute <- false
         use p = Diagnostics.Process.Start psi
         let out = p.StandardOutput.ReadToEnd()
         p.WaitForExit()

         if p.ExitCode <> 0 then
             failwithf "git ls-files exited %d in %s" p.ExitCode repoRoot

         out.Split('\000', StringSplitOptions.RemoveEmptyEntries)
         |> Array.map (fun s -> s.Trim().Replace('\\', '/'))
         |> Array.filter (fun s -> s <> "")
         |> Array.toList)

let private read (rel: string) =
    File.ReadAllText(Path.Combine(repoRoot, rel))

/// Tracked files the census asks about: outside the .NET default and the
/// solution's project directories.
let candidates () =
    let defaults = dotnetDefault @ solutionProjectDirs (read "ToolUp.Forge.sln")

    tracked.Value
    |> List.filter (fun f -> not (defaults |> List.exists (fun s -> GateManifest.specCovers s f)))

/// The F# sources the census reads: tracked, and outside the trees whose
/// sources are content rather than the gate's own code.
let sources () =
    let content = [ "templates/"; "samples/"; "docs-snippets/"; "docs/"; "proofs/" ]

    tracked.Value
    |> List.filter (fun f -> f.EndsWith ".fs" || f.EndsWith ".fsx")
    |> List.filter (fun f -> not (content |> List.exists (fun c -> f.StartsWith(c, StringComparison.Ordinal))))
    |> List.filter (fun f -> Path.GetFileName f <> selfName)
    |> List.filter (fun f -> File.Exists(Path.Combine(repoRoot, f)))
    |> List.map (fun f -> f, read f)

/// The MSBuild files the census reads: every tracked project, props and
/// targets file outside `templates/`, whose projects the gate never builds.
let projects () =
    tracked.Value
    |> List.filter (fun f -> f.EndsWith "proj" || f.EndsWith ".props" || f.EndsWith ".targets")
    |> List.filter (fun f -> not (f.StartsWith("templates/", StringComparison.Ordinal)))
    |> List.filter (fun f -> File.Exists(Path.Combine(repoRoot, f)))
    |> List.map (fun f -> f, read f)

let private named = lazy (census (sources ()) (projects ()) (candidates ()))

let private render (rows: (string * string list) list) =
    rows
    |> List.map (fun (p, by) -> sprintf "  %s  (named by %s)" p (String.concat ", " (List.truncate 4 by)))
    |> String.concat "\n"

let tests =
    testList "gate-reads.json census" [
        test "the census sees the repository (a census that read nothing would make the next case vacuous)" {
            Expect.isGreaterThan (candidates ()).Length 100 "the census asks about at least a hundred tracked files"

            Expect.exists
                named.Value
                (fun (p, _) -> p = "perf-budgets.json")
                "the census names perf-budgets.json, which the Build pack copies into its output"
        }

        test "every file the suite or the build names is in reads or notRead" {
            let covered = GateManifest.gateReads () @ (GateManifest.notRead |> List.map fst)

            match uncovered named.Value covered with
            | [] -> ()
            | missing ->
                failtestf
                    "%d file(s) the gate may read are in neither `reads` nor `notRead` of %s. Declare each on the row that reads it (`rowReads` in GateManifest.fs), or as `notRead` with its reason, then run `dotnet run --project Build.fsproj -- Gate --render`:\n%s"
                    missing.Length
                    GateManifest.GateReadsFile
                    (render missing)
        }

        test "every notRead entry settles a file the census names (a stale waiver is a finding)" {
            let stale =
                GateManifest.notRead
                |> List.filter (fun (spec, _) ->
                    not (named.Value |> List.exists (fun (p, _) -> GateManifest.specCovers spec p)))

            Expect.isEmpty stale (sprintf "notRead entries that settle nothing: %A" (List.map fst stale))
        }

        test "every reads entry names a tracked file" {
            let dead =
                GateManifest.gateReads ()
                |> List.filter (fun spec -> not (tracked.Value |> List.exists (GateManifest.specCovers spec)))

            Expect.isEmpty dead (sprintf "reads entries that cover no tracked file: %A" dead)
        }

        testList "the census goes red (fabricated sources)" [
            let cands = [ "perf-budgets.json"; "docs/a/b.md"; "LICENSE"; "spec/x.json" ]

            test "a literal naming the file, in a source that reaches the repository" {
                let found =
                    census [ "src/T/A.fs", "let p = Path.Combine(repoRoot, \"perf-budgets.json\")" ] [] cands

                Expect.equal (List.map fst found) [ "perf-budgets.json" ] "named by its literal"
            }

            test "a directory walk from the locator names everything under it" {
                let found =
                    census [ "src/T/A.fs", "let d = Path.Combine(repoRoot, \"docs\", \"a\")" ] [] cands

                Expect.equal (List.map fst found) [ "docs/a/b.md" ] "named by the Path.Combine prefix"
            }

            test "a source that does not reach the repository names nothing" {
                let found = census [ "src/T/A.fs", "let p = \"perf-budgets.json\"" ] [] cands
                Expect.isEmpty found "no locator, no read"
            }

            test "a project item names the file it copies" {
                let found =
                    census
                        []
                        [
                            "src/T/T.fsproj", "<None Include=\"..\\..\\spec\\x.json\" Link=\"x.json\" />"
                        ]
                        cands

                Expect.equal (List.map fst found) [ "spec/x.json" ] "named by the project's item"
            }

            test "a named file covered by no spec is reported, and a covering glob settles it" {
                let found = census [ "src/T/A.fs", "repoRoot, \"LICENSE\"" ] [] cands
                Expect.equal (uncovered found [ "docs/**" ] |> List.map fst) [ "LICENSE" ] "uncovered"
                Expect.isEmpty (uncovered found [ "LICEN*" ]) "settled by a glob"
            }
        ]
    ]