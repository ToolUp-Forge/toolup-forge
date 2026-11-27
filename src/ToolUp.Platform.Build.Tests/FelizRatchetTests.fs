// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The Feliz ratchet: the committed census of the files that depend on
/// Feliz and of the ReactElement baseline lines, held against the tree. The first arm IS the
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
    Allowances = []
}

/// A census file rewritten with one allowance added, as a reviewer adds it.
let private withAllowance (root: string) (allowance: FelizRatchet.Allowance) =
    let path = Path.Combine(root, FelizRatchet.CensusFile)

    match FelizRatchet.parse (File.ReadAllText path) with
    | Ok c ->
        File.WriteAllText(
            path,
            FelizRatchet.render {
                c with
                    Allowances = c.Allowances @ [ allowance ]
            }
        )
    | Error why -> failtestf "the seeded census does not parse: %s" why

let private allowance (sources: (string * int) list) : FelizRatchet.Allowance = {
    Date = "2026-11-26"
    Reason = "the counting rule widened"
    Sources = Map.ofList sources
    Baselines = Map.empty
    Spent = false
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

        test "an allowance admits one rise, is spent by the write, and admits nothing after" {
            use dir = TempDir.create "feliz-ratchet"
            let root = dir.Path
            seedTree root
            expectGreen (FelizRatchet.write root) "seed"

            let island (name: string) =
                Path.Combine(root, "src", "Ratchet.Client", name)

            let plant (name: string) =
                File.WriteAllText(island name, "module Ratchet.Island\n\nlet v = Feliz.Html.none\n")

            plant "IslandA.fs"
            plant "IslandB.fs"
            expectRed (FelizRatchet.write root) "may only fall" "no allowance: the rise is refused"

            withAllowance root (allowance [ "src/Ratchet.Client", 1 ])
            expectRed (FelizRatchet.write root) "may only fall" "an allowance of one does not admit two"

            // Reseed without the short allowance, then admit the rise properly.
            File.Delete(Path.Combine(root, FelizRatchet.CensusFile))
            File.Delete(island "IslandA.fs")
            File.Delete(island "IslandB.fs")
            expectGreen (FelizRatchet.write root) "reseed"
            plant "IslandA.fs"
            plant "IslandB.fs"

            withAllowance root (allowance [ "src/Ratchet.Client", 2 ])
            expectRed (FelizRatchet.check root) "is unspent" "the gate names an allowance not yet spent"
            expectGreen (FelizRatchet.write root) "the allowance admits the rise"
            expectGreen (FelizRatchet.check root) "and the gate holds"

            match FelizRatchet.parse (File.ReadAllText(Path.Combine(root, FelizRatchet.CensusFile))) with
            | Ok c ->
                Expect.equal (c.Sources |> Map.find "src/Ratchet.Client") 3 "the rise recorded"
                Expect.equal (c.Allowances |> List.map _.Spent) [ true ] "the allowance kept, spent"
            | Error why -> failtestf "%s" why

            // A later rise is refused exactly as before: the spent allowance
            // admits nothing, even for the same cell and amount.
            plant "IslandC.fs"
            expectRed (FelizRatchet.check root) "may only fall" "the gate refuses the later rise"
            expectRed (FelizRatchet.write root) "may only fall" "and so does the writer"
        }

        test "an unspent allowance with no rise is red, and a write spends it" {
            use dir = TempDir.create "feliz-ratchet"
            let root = dir.Path
            seedTree root
            expectGreen (FelizRatchet.write root) "seed"
            withAllowance root (allowance [ "src/Ratchet.Client", 5 ])
            expectRed (FelizRatchet.check root) "is unspent" "an allowance cannot be staged for later"
            expectGreen (FelizRatchet.write root) "the write spends it"
            expectGreen (FelizRatchet.check root) "spent, it is a record"
        }

        // ── Laws ───────────────────────────────────────────────────────
        test "an open Feliz line or a qualified Feliz name counts, once" {
            let counts (text: string) = FelizRatchet.dependsOnFeliz text
            Expect.isTrue (counts "module M\n\nopen Feliz\n") "bare open"
            Expect.isTrue (counts "module M\r\n  open Feliz // the DSL\r\n") "indented, CRLF, comment"

            Expect.isTrue
                (counts "module M\n\nlet v = Feliz.Html.label [ Feliz.prop.className \"c\" ]\n")
                "elements and props"

            Expect.isTrue (counts "module M\n\n[<Feliz.ReactComponent>]\nlet C () = 1\n") "a component attribute"
            Expect.isTrue (counts "module M\n\nlet f (e: Feliz.ReactElement) = e\n") "a type"
            Expect.isTrue (counts "module M\n\nopen type Feliz.prop\n") "open type"
            Expect.isTrue (counts "module M\n\nlet v = global.Feliz.Html.none\n") "through global."
            Expect.isTrue (counts "module M\n\nmodule H = Feliz.Html\n") "an alias"

            let both = "module M\n\nopen Feliz\n\nlet v = Feliz.Html.none\n"
            let census = FelizRatchet.censusOf (set [ "src/A" ]) [ "src/A/Both.fs", both ] []
            Expect.equal census.Sources (Map.ofList [ "src/A", 1 ]) "a file counted both ways is counted once"
        }

        test "comments, strings and other packages' names do not count" {
            let counts (text: string) = FelizRatchet.dependsOnFeliz text
            Expect.isFalse (counts "// open Feliz\n") "a line comment"
            Expect.isFalse (counts "/// Renders a Feliz.Html.div\nlet v = 1\n") "a doc comment"
            Expect.isFalse (counts "(* old:\nopen Feliz\nFeliz.Html.none *)\nlet v = 1\n") "a block comment"
            Expect.isFalse (counts "(* outer (* inner *) open Feliz *)\nlet v = 1\n") "a nested block comment"
            Expect.isFalse (counts "let s = \"open Feliz\"\n") "a string literal"
            Expect.isFalse (counts "let s = \"open Feliz\nFeliz.Html.none\n\"\n") "a multi-line string"
            Expect.isFalse (counts "let s = \"Feliz.Html \\\" Feliz.prop\"\n") "an escaped quote in a string"
            Expect.isFalse (counts "let s = @\"C:\\src\\\"\"Feliz.Html\"\"\"\n") "a verbatim string"
            Expect.isFalse (counts "let s = \"\"\"\nopen Feliz\nFeliz.Html.none\n\"\"\"\n") "a triple-quoted string"
            Expect.isFalse (counts "let q = '\"'\nlet s = \"Feliz.Html\"\n") "a quote character literal"
            Expect.isTrue (counts "let f (x: 'a) = x\nlet v = Feliz.Html.none\n") "a type variable is not a literal"
            Expect.isFalse (counts "let x = (*)\nlet v = 1\n") "the (*) operator is not a comment"
            Expect.isFalse (counts "open Feliz.AgGrid\nopen Feliz.AgCharts\n") "the AG Grid / AG Charts bindings"
            Expect.isFalse (counts "open Feliz.ViewEngine\nlet v = Feliz.ViewEngine.Html.div []\n") "the SSR engine"

            Expect.isFalse (counts "let c : Feliz.AgGrid.AgGridModuleConfig option = None\n") "a qualified AG Grid type"

            Expect.isFalse
                (counts "do Feliz.AgGridRuntime.load ()\ndo Feliz.AgChartRuntime.load ()\n")
                "the runtime modules"

            Expect.isFalse (counts "let id = ToolUp.Feliz.AgGrid.version\n") "a name that only ends in Feliz"
        }

        test "a namespace or module declaration is the file naming itself, not a use" {
            let counts (text: string) = FelizRatchet.dependsOnFeliz text
            Expect.isFalse (counts "namespace Feliz.AgGrid\n\ntype T = int\n") "the namespace of another package"
            Expect.isFalse (counts "module Feliz.Custom\n\nlet v = 1\n") "a module declared in the namespace, alone"
            Expect.isFalse (counts "module internal Feliz.Custom\n\nlet v = 1\n") "with an access modifier"
            Expect.isTrue (counts "module Feliz.AgGrid\n\nopen Feliz\n") "a declaration does not hide an open"
            Expect.isTrue (counts "module Feliz.Custom\n\nlet v = Feliz.Html.none\n") "nor a qualified use"
        }

        test "every name the rule sets aside is another package in this tree" {
            let props = File.ReadAllText(Path.Combine(repoRoot, "Directory.Packages.props"))

            let declared =
                Directory.EnumerateDirectories(Path.Combine(repoRoot, "src"), "Feliz.*")
                |> Seq.collect (fun d -> Directory.EnumerateFiles(d, "*.fs"))
                |> Seq.map (fun f -> File.ReadAllText(f).Replace("\r\n", "\n"))
                |> String.concat "\n"

            for name in FelizRatchet.otherPackages do
                let isBinding = declared.Contains($"\nmodule Feliz.{name}\n")
                let isPackage = props.Contains($"Include=\"Feliz.{name}\"")

                Expect.isTrue
                    (isBinding || isPackage)
                    $"Feliz.{name} is neither a binding module under src/Feliz.* nor a Feliz.{name} package: a stale exclusion hides Feliz use"
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

            let withA = {
                c with
                    Allowances = [ allowance [ "src/A", 2 ] ]
            }

            Expect.equal (FelizRatchet.parse (FelizRatchet.render withA)) (Ok withA) "round trip with an allowance"

            let undated =
                (FelizRatchet.render withA).Replace("\"date\": \"2026-11-26\"", "\"date\": \"soon\"")

            Expect.isError (FelizRatchet.parse undated) "an allowance needs a date"
            let zero = (FelizRatchet.render withA).Replace("\"src/A\": 2 }", "\"src/A\": 0 }")
            Expect.isError (FelizRatchet.parse zero) "an allowance admits a positive amount"
        }

        test "the driver accepts --write for the FelizRatchet target" {
            match Flags.parse VerifyGate.driverFlags [ "FelizRatchet"; "--write" ] with
            | Ok flags -> Expect.isTrue (Flags.has "--write" flags) "the switch is read"
            | Error why -> failtestf "refused: %s" why
        }
    ]