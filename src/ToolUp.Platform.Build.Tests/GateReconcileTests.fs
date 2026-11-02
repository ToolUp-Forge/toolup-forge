// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.GateReconcileTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Build
open ToolUp.Forge

// ─── Every test project runs or is excluded by name ───────────────────
//
// The go-red proof for the `GateReconcile` row of the gate manifest. The
// row reconciles the manifest's pack rows and `GateManifest.exclusions`
// against the solution and the disk; this reads the same two lists and
// hands them to the SAME function (`Gate.reconcile`). On the tree as
// found in Phase 1096 it was red, naming four dormant packs; removing a
// row with the project still in the solution makes it red again (the
// last case below), so the green is not vacuous.

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
    | None -> failwith "could not locate the repo root (ToolUp.Forge.sln) — this pack would be vacuously green"

/// The manifest's pack rows as `TestPack`s, and its named exclusions.
let declared () =
    Gate.packs (GateManifest.rows GateManifest.unbound), GateManifest.exclusions

let private expectFinding (result: Result<unit, string list>) (fragment: string) =
    match result with
    | Ok() -> failtestf "expected a finding naming %s, got a clean reconciliation" fragment
    | Error found ->
        Expect.exists
            found
            (fun f -> f.Contains(fragment: string))
            (sprintf "a finding naming %s, got: %s" fragment (String.concat " | " found))

[<Tests>]
let tests =
    testList "Gate.reconcile" [
        test
            "the manifest declares the packs and the exclusions (a list that read nothing would make every case below vacuous)" {
            let packs, excluded = declared ()
            Expect.isGreaterThanOrEqual packs.Length 30 "the manifest declares at least thirty pack rows"
            Expect.isNonEmpty excluded "the manifest declares its named exclusions"
        }

        test "every test project in the solution and on disk is a pack or a named exclusion" {
            let packs, excluded = declared ()

            match Gate.reconcile repoRoot packs excluded with
            | Ok() -> ()
            | Error found -> failtestf "the reconciliation found:\n  %s" (String.concat "\n  " found)
        }

        test "removing a pack row while its project stays in the solution is red, naming the project" {
            let packs, excluded = declared ()
            let dropped = packs |> List.find (fun p -> p.Name = "Tabular")
            let without = packs |> List.filter (fun p -> p.Name <> dropped.Name)

            expectFinding
                (Gate.reconcile repoRoot without excluded)
                "src/ToolUp.Tabular.Tests/ToolUp.Tabular.Tests.fsproj"
        }

        test "removing an exclusion for a project outside the solution is red, saying it is not in the solution" {
            let packs, excluded = declared ()

            let without =
                excluded |> List.filter (fun e -> not (e.Project.Contains "CompanionFuzz"))

            expectFinding (Gate.reconcile repoRoot packs without) "on disk, not in the solution"
        }

        test "a pack row naming a project outside the solution is red" {
            let findings =
                Gate.findings (Some [ "src/A.Tests/A.Tests.fsproj" ]) [ "src/A.Tests/A.Tests.fsproj" ] [
                    TestPack.create "A" "src/A.Tests/A.Tests.fsproj"
                    TestPack.create "B" "src/B.Tests/B.Tests.fsproj"
                ] []

            Expect.exists findings (fun f -> f.Contains "`B`" && f.Contains "never built") "the unbuilt pack is named"
        }

        test "a stale exclusion and a pack that is also excluded are both red" {
            let findings =
                Gate.findings (Some [ "src/A.Tests/A.Tests.fsproj" ]) [ "src/A.Tests/A.Tests.fsproj" ] [
                    TestPack.create "A" "src/A.Tests/A.Tests.fsproj"
                ] [
                    Gate.exclude "src/A.Tests/A.Tests.fsproj" "both"
                    Gate.exclude "src/Gone.Tests/Gone.Tests.fsproj" "stale"
                ]

            Expect.exists findings (fun f -> f.Contains "Gone.Tests" && f.Contains "stale") "the stale exclusion"
            Expect.exists findings (fun f -> f.Contains "both a TestPacks row") "the contradiction"
        }

        test "a project named with a .Tests. segment is a test project, so a partitioned pack cannot escape" {
            let pack = "src/A.Tests.Part/A.Tests.Part.fsproj"

            let findings =
                Gate.findings (Some [ pack; "src/A.Testsuite/A.Testsuite.fsproj" ]) [ pack ] [] []

            Expect.exists findings (fun f -> f.Contains pack) "an undeclared A.Tests.Part is not reported"

            Expect.isFalse
                (findings |> List.exists (fun f -> f.Contains "Testsuite"))
                "a name that only starts with Tests is not a test project"

            Expect.isEmpty
                (Gate.findings (Some [ pack ]) [ pack ] [ TestPack.create "Part" pack ] [])
                "the same project declared as a row is clean"
        }

        test "the solution parser reads .sln and .slnx project paths, forward-slashed" {
            let sln =
                "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"A.Tests\", \"src\\A.Tests\\A.Tests.fsproj\", \"{0D1DC5CD-4588-4303-9932-B1EEB6ED64FA}\""

            let slnx = "<Solution><Project Path=\"src/B.Tests/B.Tests.fsproj\" /></Solution>"
            Expect.equal (Gate.solutionProjects sln) [ "src/A.Tests/A.Tests.fsproj" ] ".sln"
            Expect.equal (Gate.solutionProjects slnx) [ "src/B.Tests/B.Tests.fsproj" ] ".slnx"
        }
    ]
// ─── Every vendored contract copy matches its source ──────────────────
//
// A companion pack hosts an SDK contract pack by SOURCE-LINKING it from
// ToolUp.Platform.Tests (`<Compile Include="…" Link="…" />`): one file, no
// copy, nothing to drift. Where a copy is unavoidable — a template that
// ships to consumers and cannot link into this repository — the copy is
// compared with its source up to the laws / self-test boundary. This is
// the packaged-module template gate's check generalised from its one
// template to every test project: a contract-named file under any
// `Contracts/` directory outside ToolUp.Platform.Tests is a vendored copy,
// and a copy with no registered boundary is itself a finding.

/// The directory the SDK's contract packs live in.
let contractSourceDir (root: string) =
    Path.Combine(root, "src", "ToolUp.Platform.Tests", "Contracts")

/// The laws / self-test boundary a vendored copy is compared up to, by
/// file name: the copy carries everything before the marker line.
let vendoredBoundaries = [ "ModuleContract.fs", "// ── a conforming reference module" ]

let private normaliseLines (s: string) =
    s.Replace("\r\n", "\n").Replace("\r", "\n").TrimEnd().Split '\n'
    |> Array.map _.TrimEnd()

/// Every vendored copy of an SDK contract pack on disk: a `.fs` file under
/// a `Contracts/` directory of `src/`, `templates/`, `tests/` or `tools/`,
/// outside ToolUp.Platform.Tests, named after one of its packs. Returns
/// (copy, source), both repo-relative and forward-slashed.
let vendoredCopies (root: string) : (string * string) list =
    let sources = contractSourceDir root

    let rel (p: string) =
        Path.GetRelativePath(root, p).Replace(Path.DirectorySeparatorChar, '/')

    [ "src"; "templates"; "tests"; "tools" ]
    |> List.map (fun d -> Path.Combine(root, d))
    |> List.filter Directory.Exists
    |> List.collect (fun d -> Directory.EnumerateFiles(d, "*.fs", SearchOption.AllDirectories) |> List.ofSeq)
    |> List.filter (fun p ->
        let r = rel p

        r.Contains "/Contracts/"
        && not (r.StartsWith "src/ToolUp.Platform.Tests/")
        && not (r.Contains "/obj/")
        && not (r.Contains "/bin/")
        && File.Exists(Path.Combine(sources, Path.GetFileName p)))
    |> List.map (fun p -> rel p, rel (Path.Combine(sources, Path.GetFileName p)))
    |> List.sort

/// The law a line of a contract pack belongs to: the nearest top-level
/// `let` at or above it.
let private lawAt (lines: string array) (index: int) =
    seq { min index (lines.Length - 1) .. -1 .. 0 }
    |> Seq.tryPick (fun i ->
        if lines[i].StartsWith "let " then
            lines[i].Substring(4).Split([| ' '; '('; ':' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.filter (fun w -> w <> "private" && w <> "inline" && w <> "rec")
            |> Array.tryHead
        else
            None)
    |> Option.defaultValue "<the file header>"

/// The drift finding for one vendored copy, `None` when it matches its
/// source up to the boundary. Names the pack (the copy's test project) and
/// the law whose text first differs.
let vendoredDrift (copy: string) (source: string) (sourceText: string) (copyText: string) : string option =
    let pack =
        match copy.IndexOf "/Contracts/" with
        | -1 -> copy
        | i -> copy.Substring(0, i)

    match
        vendoredBoundaries
        |> List.tryFind (fun (name, _) -> name = Path.GetFileName copy)
    with
    | None ->
        Some(
            sprintf
                "%s vendors %s with no registered laws/self-test boundary: source-link it from %s instead of copying it, or register its boundary in vendoredBoundaries."
                pack
                (Path.GetFileName copy)
                source
        )
    | Some(_, marker) ->
        let at = sourceText.IndexOf(marker, StringComparison.Ordinal)

        if at < 0 then
            Some(sprintf "%s: the boundary marker %s is gone from %s; %s cannot be compared." pack marker source copy)
        else
            let laws = normaliseLines (sourceText.Substring(0, at))
            let vendored = normaliseLines copyText

            if laws = vendored then
                None
            else
                let at i (xs: string array) = if i < xs.Length then xs[i] else null

                let first =
                    seq { 0 .. max laws.Length vendored.Length }
                    |> Seq.find (fun i -> at i laws <> at i vendored)

                Some(
                    sprintf
                        "%s: the vendored %s has drifted from %s in the law `%s` (first difference at line %d). Re-copy the laws region, everything before `%s`."
                        pack
                        (Path.GetFileName copy)
                        source
                        (lawAt laws first)
                        (first + 1)
                        marker
                )

[<Tests>]
let vendoredContractTests =
    testList "vendored contract copies" [
        test "every vendored contract copy matches its source up to the self-test boundary" {
            let copies = vendoredCopies repoRoot

            // Non-vacuity: the packaged-module template's copy is the one
            // copy the tree is known to carry.
            Expect.contains
                (copies |> List.map fst)
                "templates/platformsdk-module-packaged/tests/MyModule.Tests/Contracts/ModuleContract.fs"
                "the scan finds the packaged-module template's vendored ModuleContract"

            let findings =
                copies
                |> List.choose (fun (copy, source) ->
                    vendoredDrift
                        copy
                        source
                        (File.ReadAllText(Path.Combine(repoRoot, source)))
                        (File.ReadAllText(Path.Combine(repoRoot, copy))))

            Expect.isEmpty findings (String.concat "\n" findings)
        }

        test "a drifted copy is red, naming the pack and the law" {
            let source =
                "module Laws\nlet lawOne () = 1\nlet lawTwo () = 2\n// ── a conforming reference module\nlet selfTests = ()\n"

            let copy = "module Laws\nlet lawOne () = 1\nlet lawTwo () = 3\n"

            match
                vendoredDrift
                    "templates/T/tests/X.Tests/Contracts/ModuleContract.fs"
                    "src/S/ModuleContract.fs"
                    source
                    copy
            with
            | Some f ->
                Expect.stringContains f "templates/T/tests/X.Tests" "the finding names the pack"
                Expect.stringContains f "`lawTwo`" "the finding names the law that drifted"
            | None -> failtest "a drifted copy read as matching"

            Expect.isNone
                (vendoredDrift
                    "a/Contracts/ModuleContract.fs"
                    "s"
                    source
                    "module Laws\r\nlet lawOne () = 1\r\nlet lawTwo () = 2  \n\n")
                "line endings and trailing space aside, a faithful copy matches"
        }

        test "a copy with no registered boundary is red, and the remedy is a link" {
            match
                vendoredDrift "src/A/Tests/Contracts/IBlobStorageContract.fs" "src/P/IBlobStorageContract.fs" "" ""
            with
            | Some f -> Expect.stringContains f "source-link it" "the remedy is a link, not a second copy"
            | None -> failtest "an unregistered copy read as matching"
        }
    ]