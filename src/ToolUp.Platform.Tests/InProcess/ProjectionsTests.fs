// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The registry of generated artefacts (`projections.json`) held to the tree,
/// in both directions: every row names a file that exists and a check that
/// holds it; every regeneration switch read in the test tree is the one
/// reading (`Approve`) of a stem the registry has; and `.fantomasignore` is
/// the registry's render.
///
/// What it does not read: the workspace version ledger and `copies.json`
/// track copied and derived files across repositories, and this repository
/// declares no `version-derives.json`, so neither is a reader of a projection
/// here. Each row's CONTENT is held by the check the row names (`heldBy`), not
/// by this list: this list holds the registry, those hold the bytes.
module ToolUp.Platform.Tests.InProcess.ProjectionsTests

open System
open System.IO
open System.Reflection
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform.Tests.Contracts

let private repoRoot () =
    let assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private registry = lazy (Projections.read (repoRoot ()))

/// The files a row's path names: `*` within one segment, a trailing `/` a
/// directory that must exist and hold something.
let private resolve (root: string) (path: string) =
    if path.EndsWith "/" then
        let dir = Path.Combine(root, path.TrimEnd '/')

        if
            Directory.Exists dir
            && not (Seq.isEmpty (Directory.EnumerateFileSystemEntries dir))
        then
            [ dir ]
        else
            []
    else
        let dir = Path.Combine(root, Path.GetDirectoryName path)

        if Directory.Exists dir then
            Directory.EnumerateFiles(dir, Path.GetFileName path) |> List.ofSeq
        else
            []

/// The `.fs` files of the test tree: every project directory whose name ends
/// `.Tests` under `src/` and `tools/`, and the Platform pack's sources.
let private testTree (root: string) =
    let rec walk (dir: string) = seq {
        for d in Directory.EnumerateDirectories dir do
            match Path.GetFileName d with
            | "bin"
            | "obj"
            | "node_modules"
            | "output" -> ()
            | _ -> yield! walk d

        yield! Directory.EnumerateFiles(dir, "*.fs")
    }

    [ "src"; "tools" ]
    |> List.map (fun r -> Path.Combine(root, r))
    |> List.filter Directory.Exists
    |> List.collect (fun r ->
        Directory.EnumerateDirectories r
        |> Seq.collect (fun d -> if d.Contains ".Tests" then walk d else Seq.empty)
        |> List.ofSeq)
    |> List.distinct

/// The switches a test site may read only through `Approve`: any
/// `TOOLUP_APPROVE*`, `TOOLUP_REGEN_*` or `TOOLUP_EMIT_*` name. `TOOLUP_EMIT_SBOM`
/// is not a projection switch: it is the Publish target's opt-in, and its
/// tests name it.
let private switchRead =
    Regex(@"GetEnvironmentVariable\s*\(?\s*""(TOOLUP_(?:APPROVE|REGEN|EMIT)[A-Z_]*)""")

/// A stem a site reads: the literal argument of `Approve.admit`,
/// `Approve.admitName` or `Approve.scope`, or a binding named `*Stem`.
let private stemRead =
    Regex(@"(?:Approve\.(?:admit|admitName|scope)\s+""([a-z0-9-]+)"")|(?:\w*Stem\s*=\s*""([a-z0-9-]+)"")")

let tests =
    testList "Projections — the registry of generated artefacts" [
        test "every committed row names a file in the tree and a check that exists" {
            let root = repoRoot ()

            let missing = [
                for row in registry.Value.Rows do
                    if row.Committed && row.External.IsNone && List.isEmpty (resolve root row.Path) then
                        sprintf "%s: no file in the tree" row.Path

                    if not (File.Exists(Path.Combine(root, row.HeldBy))) then
                        sprintf "%s: its check %s does not exist" row.Path row.HeldBy

                    match row.Regen with
                    | Projections.Regen.Pack(project, _)
                    | Projections.Regen.Build project ->
                        if not (File.Exists(Path.Combine(root, project))) then
                            sprintf "%s: its regeneration project %s does not exist" row.Path project
                    | Projections.Regen.Script script ->
                        if not (File.Exists(Path.Combine(root, script))) then
                            sprintf "%s: its regeneration script %s does not exist" row.Path script
                    | Projections.Regen.Target _ -> ()
            ]

            Expect.isEmpty missing "a row names what exists"
        }

        test "a pack regeneration's filter names a list its check declares" {
            let root = repoRoot ()

            let unmatched = [
                for row in registry.Value.Rows do
                    match row.Regen with
                    | Projections.Regen.Pack(_, Some filter) when row.HeldBy.EndsWith ".fs" ->
                        let held = File.ReadAllText(Path.Combine(root, row.HeldBy))

                        if not (held.Contains filter) then
                            sprintf
                                "%s: filter %s is not in %s, so the regeneration would run nothing"
                                row.Path
                                filter
                                row.HeldBy
                    | _ -> ()
            ]

            Expect.isEmpty unmatched "a filter that matches nothing is a vacuous regeneration"
        }

        test "every retired switch spells a stem the registry has" {
            let stems = registry.Value.Rows |> List.map _.Stem |> Set.ofList

            let orphans =
                Approve.legacy |> List.filter (fun (_, stem) -> not (stems.Contains stem))

            Expect.isEmpty orphans "a retired switch must map to a row's stem"
        }

        test "every switch read in the test tree is the one reading of a registered stem" {
            let root = repoRoot ()
            let stems = registry.Value.Rows |> List.map _.Stem |> Set.ofList

            let sites =
                testTree root
                |> List.filter (fun f -> Path.GetFileName f <> "Approval.fs")
                |> List.map (fun f -> Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText f)

            let direct = [
                for rel, text in sites do
                    for m in switchRead.Matches text do
                        if m.Groups[1].Value <> "TOOLUP_EMIT_SBOM" then
                            sprintf
                                "%s reads %s directly: read it through Approve with a stem row"
                                rel
                                m.Groups[1].Value
            ]

            let unregistered = [
                for rel, text in sites do
                    for m in stemRead.Matches text do
                        let stem =
                            if m.Groups[1].Success then
                                m.Groups[1].Value
                            else
                                m.Groups[2].Value

                        if not (stems.Contains stem) then
                            sprintf "%s reads stem `%s`, which projections.json has no row for" rel stem
            ]

            let read =
                sites
                |> List.collect (fun (_, text) -> [
                    for m in stemRead.Matches text ->
                        if m.Groups[1].Success then
                            m.Groups[1].Value
                        else
                            m.Groups[2].Value
                ])

            Expect.isEmpty (direct @ unregistered) "a switch site without a row"
            Expect.isGreaterThanOrEqual read.Length 10 "the grep found the sites it exists to hold"
        }

        test ".fantomasignore is the registry's render" {
            let path = Path.Combine(repoRoot (), ".fantomasignore")
            let committed = File.ReadAllText(path).Replace("\r\n", "\n")

            Expect.equal
                committed
                (Projections.renderFantomasIgnore registry.Value)
                ".fantomasignore drifted from projections.json: run `dotnet run --project Build.fsproj -- Projections --render`"
        }

        test "every generated F# file in the registry is in .fantomasignore" {
            let unlisted =
                registry.Value.Rows
                |> List.filter (fun r -> r.Path.EndsWith ".fs" && not r.FantomasIgnore)

            Expect.isEmpty unlisted "Fantomas must not reformat a projection"
        }

        test "the approval reading: names-or-1, stems and names within a stem" {
            Expect.equal (Approve.read "api" null) Approve.Admission.Nothing "unset"
            Expect.equal (Approve.read "api" "1") Approve.Admission.Everything "1 is every stem"
            Expect.equal (Approve.read "api" "true") Approve.Admission.Everything "true is every stem"
            Expect.equal (Approve.read "api" "composition") Approve.Admission.Nothing "another stem"
            Expect.equal (Approve.read "composition" "api,composition") Approve.Admission.Everything "a list"

            Expect.equal
                (Approve.read "api" "api:ToolUp.Platform.Core; api:ToolUp.AI.Core.approved.txt")
                (Approve.Admission.Names(set [ "ToolUp.Platform.Core"; "ToolUp.AI.Core" ]))
                "names within a stem, the pasted suffix tolerated"

            Expect.equal
                (Approve.readLegacy "ToolUp.Platform.Core")
                (Approve.Admission.Names(set [ "ToolUp.Platform.Core" ]))
                "the old API switch's names"

            Expect.equal (Approve.readLegacy "1") Approve.Admission.Everything "the old switches' 1"
            Expect.equal (Approve.readLegacy "0") Approve.Admission.Nothing "the old switches' 0"
        }
    ]