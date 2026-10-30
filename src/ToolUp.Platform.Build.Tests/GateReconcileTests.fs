// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.GateReconcileTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Build

// ─── Phase 1096 — every test project runs or is excluded by name ─────
//
// The go-red proof for the `GateReconcile` leg `Build.fs` registers. The
// leg reconciles the real `config.TestPacks` and `gateExclusions`; this
// pack cannot reference the FAKE driver, so it reads the same two
// declarations out of `Build.fs` and hands them to the SAME function
// (`Gate.reconcile`). On the tree as found it was red, naming the four
// dormant packs; removing a row with the project still in the solution
// makes it red again (the last case below), so the green is not vacuous.

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

/// The `TestPack.create "<name>" "<project>"` rows and the
/// `Gate.exclude "<project>"` declarations in `Build.fs`, in source order.
let declared () =
    let text = File.ReadAllText(Path.Combine(repoRoot, "Build.fs"))

    let packs =
        Regex.Matches(text, "TestPack\\.create\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"")
        |> Seq.map (fun m -> TestPack.create m.Groups[1].Value m.Groups[2].Value)
        |> List.ofSeq

    let excluded =
        Regex.Matches(text, "Gate\\.exclude\\s+\"([^\"]+)\"\\s+\"([^\"]+)\"")
        |> Seq.map (fun m -> Gate.exclude m.Groups[1].Value m.Groups[2].Value)
        |> List.ofSeq

    packs, excluded

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
    testList "Phase 1096 — Gate.reconcile" [
        test "the declarations parse (a parser that read nothing would make every case below vacuous)" {
            let packs, excluded = declared ()
            Expect.isGreaterThanOrEqual packs.Length 30 "Build.fs declares at least thirty TestPacks rows"
            Expect.isNonEmpty excluded "Build.fs declares its named exclusions"
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

        test "the solution parser reads .sln and .slnx project paths, forward-slashed" {
            let sln =
                "Project(\"{F2A71F9B-5D33-465A-A702-920D77279786}\") = \"A.Tests\", \"src\\A.Tests\\A.Tests.fsproj\", \"{0D1DC5CD-4588-4303-9932-B1EEB6ED64FA}\""

            let slnx = "<Solution><Project Path=\"src/B.Tests/B.Tests.fsproj\" /></Solution>"
            Expect.equal (Gate.solutionProjects sln) [ "src/A.Tests/A.Tests.fsproj" ] ".sln"
            Expect.equal (Gate.solutionProjects slnx) [ "src/B.Tests/B.Tests.fsproj" ] ".slnx"
        }
    ]