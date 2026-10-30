// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.PureLaneTests

open System.IO
open System.Text.RegularExpressions
open Expecto

// ─── Phase 1096 — the `pure` lane's membership audit, as a test ──────
//
// `pureLanePacks` in `Build.fs` admits a pack whose own test sources touch
// no filesystem, process, socket or environment variable. The audit that
// seeded it on 2026-09-12 was a grep run once; by 2026-10-23 two of its six
// members matched it (`RemotingAnalyzers` grew a tree scan, `Offline` a
// sync-handler test that reads the tree) and nothing noticed. This re-runs
// that grep over every member on every push, so a member that starts
// reading the tree is red here rather than quietly in the ten-second lane.
// Phase 1098 moves this onto the gate manifest's `Reads = []` row.

/// The audit's pattern, verbatim from the `pureLanePacks` doc comment.
let private readsTheTree =
    Regex
        "System\\.IO|File\\.|Directory\\.|Path\\.GetTempPath|Process|HttpClient|Socket|Environment\\.GetEnvironmentVariable|__SOURCE_DIRECTORY__"

/// The names in `let private pureLanePacks = set [ ... ]` in `Build.fs`.
let private pureLaneMembers () =
    let text = File.ReadAllText(Path.Combine(GateReconcileTests.repoRoot, "Build.fs"))

    let m =
        Regex.Match(text, "let private pureLanePacks\\s*=\\s*set\\s*\\[([^\\]]*)\\]", RegexOptions.Singleline)

    if not m.Success then
        failtest "Build.fs declares no `let private pureLanePacks = set [ ... ]` — the audit would read nothing"

    Regex.Matches(m.Groups[1].Value, "\"([^\"]+)\"")
    |> Seq.map _.Groups[1].Value
    |> List.ofSeq

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

let tests =
    testList "Phase 1096 — the pure lane's membership audit" [
        test "every pure-lane member is a declared pack, and none of its test sources reads the tree" {
            let members = pureLaneMembers ()
            Expect.isNonEmpty members "the pure lane names its members"
            let packs, _ = GateReconcileTests.declared ()

            for name in members do
                match packs |> List.tryFind (fun p -> p.Name = name) with
                | None -> failtestf "pure-lane member `%s` is not a TestPacks row in Build.fs" name
                | Some pack ->
                    let dir =
                        Path.GetDirectoryName(Path.Combine(GateReconcileTests.repoRoot, pack.Project))

                    match offendingFiles dir with
                    | [] -> ()
                    | files ->
                        failtestf
                            "pure-lane member `%s` reads the tree — the audit matches %s. Drop it from `pureLanePacks`, or remove the read."
                            name
                            (String.concat ", " files)
        }

        test "the audit is not vacuous: it matches a pack known to read the tree" {
            let packs, _ = GateReconcileTests.declared ()
            let offline = packs |> List.find (fun p -> p.Name = "Offline")

            let dir =
                Path.GetDirectoryName(Path.Combine(GateReconcileTests.repoRoot, offline.Project))

            Expect.isNonEmpty (offendingFiles dir) "the Offline pack reads the tree, so the pattern must match it"
        }
    ]