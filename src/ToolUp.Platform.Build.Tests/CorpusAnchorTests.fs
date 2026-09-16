// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.CorpusAnchorTests

open System
open System.IO
open System.Threading.Tasks
open Expecto
open ToolUp.Platform.Tests.Support

// ─── Phase 735 — corpus anchoring spawns git a constant number of times ──
//
// `CorpusAnchor` shells `git` to find the main working tree and the
// other worktrees of this repository. The phase that added these tests
// believed that was happening per LOOKUP — O(tests) subprocesses across
// the Platform pack — and it was not: every caller already held its
// answer in a module-level `lazy`. What this pack pins is the stronger
// property the module now holds ITSELF: for one checkout root, the first
// ask pays the spawns and every later ask, from any thread, pays none. A
// caller written tomorrow without a `lazy` therefore cannot reintroduce
// the per-lookup cost, and if the memoisation is ever removed the count
// below goes red rather than the gate quietly getting slower.
//
// ── The counter is proven to count ──
// A memoisation test that only ever sees zero cannot tell "cached" from
// "never spawned anything". So a FRESH root — one no case has asked about
// — is shown to cost at least one spawn first. That is the falsifier for
// every zero asserted after it.
//
// ── Why this pack and not the Platform pack ──
// The property is the module's, not the Platform pack's, and this pack
// already compiles the repo's other build-hygiene modules by source link.
// The measured per-run figure for the Platform pack itself is recorded in
// the phase's migration doc, taken with `TOOLUP_CORPUS_ANCHOR_TRACE=1`.

/// A directory nothing else in this process has anchored on. Under the
/// system temp dir, so it is not inside this repository — git answers
/// "not a repository" there, which still costs the spawn the test counts.
let private freshRoot () =
    let dir =
        Path.Combine(Path.GetTempPath(), "toolup-corpus-anchor-" + Guid.NewGuid().ToString "N")

    Directory.CreateDirectory dir |> ignore
    dir

let private spawnsDuring (body: unit -> unit) =
    let before = CorpusAnchor.gitSpawnCount ()
    body ()
    CorpusAnchor.gitSpawnCount () - before

let tests =
    testList "CorpusAnchor" [
        testCase "the counter counts: a fresh root costs at least one spawn (falsifier for every zero below)"
        <| fun () ->
            let root = freshRoot ()

            try
                let cost = spawnsDuring (fun () -> CorpusAnchor.resolve root |> ignore)
                Expect.isGreaterThanOrEqual cost 1 "the first ask for a root shells git"
            finally
                Directory.Delete(root, true)

        testCase "the second ask for the same root spawns nothing"
        <| fun () ->
            let root = freshRoot ()

            try
                CorpusAnchor.resolve root |> ignore
                let cost = spawnsDuring (fun () -> CorpusAnchor.resolve root |> ignore)
                Expect.equal cost 0 "resolved once per root per process"
            finally
                Directory.Delete(root, true)

        testCase "mainWorkingTree is memoised on its own, since callers ask it directly on failure paths"
        <| fun () ->
            let root = freshRoot ()

            try
                let first = spawnsDuring (fun () -> CorpusAnchor.mainWorkingTree root |> ignore)
                Expect.isGreaterThanOrEqual first 1 "the first ask spawns"
                let second = spawnsDuring (fun () -> CorpusAnchor.mainWorkingTree root |> ignore)
                Expect.equal second 0 "the second does not"
            finally
                Directory.Delete(root, true)

        testCase "a hundred concurrent asks for one root cost the spawns of exactly one"
        <| fun () ->
            let root = freshRoot ()

            try
                // Measure the cost of a cold resolve on a SIBLING root first,
                // so the bound below is the module's own figure rather than a
                // number written here that could drift from it.
                let sibling = freshRoot ()

                let cold =
                    try
                        spawnsDuring (fun () -> CorpusAnchor.resolve sibling |> ignore)
                    finally
                        Directory.Delete(sibling, true)

                let cost =
                    spawnsDuring (fun () ->
                        Parallel.For(0, 100, (fun _ -> CorpusAnchor.resolve root |> ignore)) |> ignore)

                Expect.equal cost cold "however many threads ask at once, the root is resolved exactly once"

                Expect.isLessThanOrEqual
                    cost
                    2
                    "and a resolve is at most two spawns: the common dir and the worktree list"
            finally
                Directory.Delete(root, true)

        testCase "two spellings of one root share one entry"
        <| fun () ->
            let root = freshRoot ()

            try
                CorpusAnchor.resolve root |> ignore

                let trailing =
                    spawnsDuring (fun () -> CorpusAnchor.resolve (root + string Path.DirectorySeparatorChar) |> ignore)

                Expect.equal trailing 0 "a trailing separator is the same root"

                if OperatingSystem.IsWindows() then
                    let cased =
                        spawnsDuring (fun () -> CorpusAnchor.resolve (root.ToUpperInvariant()) |> ignore)

                    Expect.equal cased 0 "on Windows, case is the same root"
            finally
                Directory.Delete(root, true)

        testCase "the anchoring for this checkout resolves, and asking again is free"
        <| fun () ->
            // The real checkout this pack runs in: whatever the first ask
            // cost (possibly nothing, if a sibling case already asked), the
            // second is a dictionary read.
            let root = Environment.CurrentDirectory
            let anchoring = CorpusAnchor.resolve root
            Expect.isTrue (Directory.Exists anchoring.Anchor) "the anchor is a directory"

            let again = spawnsDuring (fun () -> CorpusAnchor.resolve root |> ignore)
            Expect.equal again 0 "free on the second ask"
    ]