// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.PureLaneTests

open System.IO
open System.Text.RegularExpressions
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Build
open ToolUp.Forge

// ─── The `pure` lane's membership audit, as a test ────────────────────
//
// The pure lane is the gate manifest's pack rows whose `Reads` is empty:
// rows whose own test sources touch no filesystem, process, socket or
// environment variable. The audit that first seeded the lane on
// 2026-09-12 was a grep run once; by 2026-10-23 two of its six members
// matched it and nothing noticed. This re-runs that grep over every
// `Reads = []` row on every push, so a row that starts reading the tree is
// red here rather than quietly in the ten-second lane.

/// The audit's pattern.
let private readsTheTree =
    Regex
        "System\\.IO|File\\.|Directory\\.|Path\\.GetTempPath|Process|HttpClient|Socket|Environment\\.GetEnvironmentVariable|__SOURCE_DIRECTORY__"

let private rows = GateManifest.rows GateManifest.unbound

/// The `*.fs` files under a pack's project directory, outside `bin`/`obj`,
/// that match the audit's pattern.
let private offendingFiles (projectDir: string) =
    Directory.EnumerateFiles(projectDir, "*.fs", SearchOption.AllDirectories)
    |> Seq.filter (fun f ->
        let p = f.Replace('\\', '/')
        not (p.Contains "/bin/" || p.Contains "/obj/"))
    |> Seq.filter (fun f -> readsTheTree.IsMatch(File.ReadAllText f))
    |> Seq.map (fun f -> Path.GetRelativePath(GateReconcileTests.repoRoot, f).Replace('\\', '/'))
    |> List.ofSeq

let private projectDir (project: string) =
    Path.GetDirectoryName(Path.Combine(GateReconcileTests.repoRoot, project))

let tests =
    testList "the pure lane's membership audit" [
        test "every Reads = [] pack row's test sources pass the grep" {
            let pure = Gate.pureRows rows
            Expect.isNonEmpty pure "the pure lane has members"

            for row in pure do
                match row.Kind with
                | LegKind.ExpectoPack project ->
                    match offendingFiles (projectDir project) with
                    | [] -> ()
                    | files ->
                        failtestf
                            "row `%s` declares `Reads = []` and its sources read the tree — the audit matches %s. Give the row its inputs, or remove the read."
                            row.Name
                            (String.concat ", " files)
                | LegKind.Body _
                | LegKind.Script _ -> failtestf "`%s` is not a pack row" row.Name
        }

        test "the audit is not vacuous: it matches a pack known to read the tree" {
            let offline =
                rows
                |> List.pick (fun r ->
                    match r.Kind with
                    | LegKind.ExpectoPack project when r.Name = "Offline" -> Some project
                    | _ -> None)

            Expect.isNonEmpty
                (offendingFiles (projectDir offline))
                "the Offline pack reads the tree, so the pattern must match it"
        }
    ]