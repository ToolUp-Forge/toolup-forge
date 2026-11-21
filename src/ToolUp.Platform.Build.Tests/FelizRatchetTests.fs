// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The Feliz ratchet: the committed census of `open Feliz` files and
/// ReactElement baseline lines, held against the tree. The first arm IS the
/// gate (`VerifyAll` runs this pack); the rest pin its laws, its teeth and
/// its writer.
module ToolUp.Platform.Build.Tests.FelizRatchetTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Build
open ToolUp.Platform.Testing
open ToolUp.Forge

let private repoRoot =
    let rec walkUp (dir: DirectoryInfo) =
        if isNull (box dir) then
            None
        elif File.Exists(Path.Combine(dir.FullName, "ToolUp.Forge.sln")) then
            Some dir.FullName
        else
            walkUp dir.Parent

    match walkUp (DirectoryInfo AppContext.BaseDirectory) with
    | Some root -> root
    | None -> failwith "could not locate the repo root (ToolUp.Forge.sln) — the ratchet would be vacuously green"

let private census sources tests baselines : FelizRatchet.Census = {
    Sources = Map.ofList sources
    Tests = Map.ofList tests
    Baselines = Map.ofList baselines
}

/// A scratch tree: one client project, one test project, one baseline.
let private seedTree (root: string) =
    let put (rel: string) (text: string) =
        let path = Path.Combine(root, rel)
        Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore
        File.WriteAllText(path, text)

    put "src/Ratchet.Client/Ratchet.Client.fsproj" "<Project />"
    put "src/Ratchet.Client/View.fs" "module Ratchet.View\n\nopen Feliz\n\nlet v = 1\n"
    put "src/Ratchet.Client/Model.fs" "module Ratchet.Model\n\nlet m = 1\n"
    put "src/Ratchet.Client.Tests/Ratchet.Client.Tests.fsproj" "<Project />"
    put "src/Ratchet.Client.Tests/ViewTests.fs" "module Ratchet.ViewTests\n\nopen Feliz\n"
    put "src/Ratchet.Client/bin/Copied.fs" "open Feliz\n"
    put "api-baselines/Ratchet.Client.approved.txt" "Ratchet.View.v: ReactElement\nRatchet.Model.m: int\n"
    put "api-baselines/Ratchet.Server.approved.txt" "Ratchet.Server.run: unit -> unit\n"

let private expectRed (outcome: Result<string, string list>) (needle: string) (why: string) =
    match outcome with
    | Ok summary -> failtestf "%s: expected the ratchet to go red, it passed (%s)" why summary
    | Error lines ->
        Expect.exists lines (fun l -> l.Contains(needle, StringComparison.Ordinal)) (sprintf "%s: %A" why lines)

let private expectGreen (outcome: Result<string, string list>) (why: string) =
    match outcome with
    | Ok _ -> ()
    | Error lines -> failtestf "%s: expected green, got:\n  %s" why (String.Join("\n  ", lines))

[<Tests>]
let tests =
    testList "Feliz ratchet" [

        // ── The gate ───────────────────────────────────────────────────
        test "the committed census is the tree's census" {
            match FelizRatchet.check repoRoot with
            | Ok _ -> ()
            | Error lines ->
                failtestf
                    "feliz-census.json does not hold against the tree:\n  %s\nA rise is new Feliz the ratchet refuses; a fall or a stale test count is the census to rewrite in this change (dev-scripts/feliz-ratchet.ps1 -Write)."
                    (String.Join("\n  ", lines))
        }

        test "the committed census is not vacuous" {
            let committed =
                match FelizRatchet.parse (File.ReadAllText(Path.Combine(repoRoot, FelizRatchet.CensusFile))) with
                | Ok c -> c
                | Error why -> failtestf "%s" why

            // The programme's starting point: forge's client tier is Feliz.
            // A census of zero here means the scan found nothing, not that
            // the migration finished — that day the gate becomes a ban.
            Expect.isGreaterThan (FelizRatchet.total FelizRatchet.Sources committed) 0 "source cells"
            Expect.isGreaterThan (FelizRatchet.total FelizRatchet.Baselines committed) 0 "baseline cells"
            Expect.isTrue (committed.Sources.ContainsKey "src/ToolUp.Platform.Client") "the client tier is counted"
        }

        // ── Teeth ──────────────────────────────────────────────────────
        test "a planted `open Feliz` in a scratch file goes red, naming the cell" {
            let committed = FelizRatchet.scan repoRoot
            let projectDirs = set [ "src/ToolUp.Platform.Client"; "src/ToolUp.Platform.Core" ]

            // The tree's census plus one scratch file, judged as the gate
            // judges: the planted file is the only difference.
            let planted =
                FelizRatchet.censusOf projectDirs [
                    "src/ToolUp.Platform.Core/Scratch.fs", "module Scratch\n\nopen Feliz\n"
                ] []

            let withPlant = {
                committed with
                    Sources =
                        planted.Sources
                        |> Map.fold
                            (fun acc cell n ->
                                acc |> Map.add cell (n + (acc |> Map.tryFind cell |> Option.defaultValue 0)))
                            committed.Sources
            }

            match FelizRatchet.judge committed withPlant with
            | [ d ] ->
                Expect.equal d.Cell "src/ToolUp.Platform.Core" "the cell the plant is in"
                Expect.isTrue (FelizRatchet.isRise d) "a rise the ratchet refuses"
                Expect.stringContains (FelizRatchet.describe d) "may only fall" "the refusal says why"
            | other -> failtestf "expected exactly one drift, got %A" other
        }

        test "on disk: green, red on a plant, red on an unrecorded fall, green once rewritten" {
            use dir = TempDir.create "feliz-ratchet"
            let root = dir.Path
            seedTree root

            expectGreen (FelizRatchet.write root) "the first write seeds the census"
            expectGreen (FelizRatchet.check root) "the census holds against its own tree"

            let plant = Path.Combine(root, "src", "Ratchet.Client", "Scratch.fs")
            File.WriteAllText(plant, "module Ratchet.Scratch\n\nopen Feliz\n")
            expectRed (FelizRatchet.check root) "may only fall" "a planted open Feliz"
            expectRed (FelizRatchet.write root) "may only fall" "the writer refuses the rise"
            File.Delete plant

            File.WriteAllText(Path.Combine(root, "src", "Ratchet.Client", "View.fs"), "module Ratchet.View\n")
            expectRed (FelizRatchet.check root) "fell" "a fall not written down"
            expectGreen (FelizRatchet.write root) "the writer records a fall"
            expectGreen (FelizRatchet.check root) "and the gate holds again"
        }

        test "the writer refuses a new ReactElement baseline line, and admits a new test file" {
            use dir = TempDir.create "feliz-ratchet"
            let root = dir.Path
            seedTree root
            expectGreen (FelizRatchet.write root) "seed"

            let baseline =
                Path.Combine(root, FelizRatchet.BaselineDir, "Ratchet.Server" + FelizRatchet.BaselineSuffix)

            File.AppendAllText(baseline, "Ratchet.Server.page: ReactElement\n")
            expectRed (FelizRatchet.write root) "baselines `Ratchet.Server`" "a new ReactElement surface"
            File.WriteAllText(baseline, "Ratchet.Server.run: unit -> unit\n")

            File.WriteAllText(
                Path.Combine(root, "src", "Ratchet.Client.Tests", "MoreTests.fs"),
                "module Ratchet.MoreTests\n\nopen Feliz\n"
            )

            expectRed (FelizRatchet.check root) "stale" "an unwritten test rise is still a stale census"
            expectGreen (FelizRatchet.write root) "test files may rise"
            expectGreen (FelizRatchet.check root) "recorded"
        }

        test "a hand-edited total is refused even when every cell matches" {
            use dir = TempDir.create "feliz-ratchet"
            let root = dir.Path
            seedTree root
            expectGreen (FelizRatchet.write root) "seed"
            let path = Path.Combine(root, FelizRatchet.CensusFile)
            let text = File.ReadAllText path
            File.WriteAllText(path, text.Replace("\"sources\": 1,", "\"sources\": 0,"))
            expectRed (FelizRatchet.check root) "not the census's render" "the totals are part of the file"
        }

        // ── Laws ───────────────────────────────────────────────────────
        test "only an `open Feliz` line counts" {
            Expect.isTrue (FelizRatchet.opensFeliz "module M\n\nopen Feliz\n") "bare"
            Expect.isTrue (FelizRatchet.opensFeliz "module M\r\n  open Feliz // the DSL\r\n") "indented, CRLF, comment"
            Expect.isFalse (FelizRatchet.opensFeliz "open Feliz.AgGrid\n") "a Feliz.* namespace"
            Expect.isFalse (FelizRatchet.opensFeliz "open Feliz.ViewEngine\n") "the SSR engine"
            Expect.isFalse (FelizRatchet.opensFeliz "// open Feliz\n") "a comment"
            Expect.isFalse (FelizRatchet.opensFeliz "let s = \"open Feliz\"\n") "a string literal"
        }

        test "test files, projects and the walk's exclusions" {
            Expect.isTrue (FelizRatchet.isTest "src/ToolUp.AI.Client.Tests/X.fs") ".Tests project"
            Expect.isTrue (FelizRatchet.isTest "templates/t/tests/My.Tests/Contracts/X.fs") "tests directory"
            Expect.isFalse (FelizRatchet.isTest "src/ToolUp.Platform.Testing/Testing/X.fs") "a harness library"

            let dirs = set [ "src/A"; "src/A/Nested" ]
            Expect.equal (FelizRatchet.projectOf dirs "src/A/Client/X.fs") "src/A" "nearest project above"
            Expect.equal (FelizRatchet.projectOf dirs "src/A/Nested/X.fs") "src/A/Nested" "the nearest wins"
            Expect.equal (FelizRatchet.projectOf dirs "docs/X.fsx") "(no project)/docs" "no project"

            use dir = TempDir.create "feliz-ratchet"
            seedTree dir.Path
            let scanned = FelizRatchet.scan dir.Path

            Expect.equal
                scanned
                (census [ "src/Ratchet.Client", 1 ] [ "src/Ratchet.Client.Tests", 1 ] [ "Ratchet.Client", 1 ])
                "bin/ skipped; the test file apart; baselines by assembly"
        }

        test "render and parse round-trip, and a stranger's file is refused" {
            let c = census [ "src/A", 3; "src/B", 1 ] [ "src/A.Tests", 2 ] [ "A", 7 ]

            Expect.equal (FelizRatchet.parse (FelizRatchet.render c)) (Ok c) "round trip"
            Expect.isError (FelizRatchet.parse "{\"kind\":\"gateReads\"}") "another file's kind"
            Expect.isError (FelizRatchet.parse "not json") "not JSON"
        }

        test "the driver accepts --write for the FelizRatchet target" {
            match Flags.parse VerifyGate.driverFlags [ "FelizRatchet"; "--write" ] with
            | Ok flags -> Expect.isTrue (Flags.has "--write" flags) "the switch is read"
            | Error why -> failtestf "refused: %s" why
        }
    ]