// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.TemplateGateTests

open System
open System.IO
open Expecto
open ToolUp.Platform.Build
open ToolUp.Forge

// ─── Phase 754 — the stranger's-package guard, proven in both ────────
// ─── directions ──────────────────────────────────────────────────────
//
// `TemplateGate.unproducedIds` is what stops the template gates
// declaring a package id this repo does not emit. That failure is not
// theoretical: Phase 307 found `Feliz.AgGrid` / `Feliz.AgCharts` absent
// from the gate closure, and because unrelated packages of both names
// exist on nuget.org under another author, the restore did not fail —
// it silently served a STRANGER'S package.
//
// The guard's PASSING direction is satisfied by an implementation that
// returns `[]` unconditionally, which is exactly the shape the previous
// gate had (the id and the project were one string, so a wrong id could
// not be expressed and nothing checked one). So every property here is
// probed the other way as well: each case that asserts silence has a
// twin that asserts the guard names the offender.

let private version = "0.0.0-templategate"

let private nupkg id = sprintf "%s.%s.nupkg" id version

/// The real closure's shape after this phase: two entries whose id and
/// producing directory deliberately DISAGREE (the bindings kept their
/// `src/Feliz.*/` directories while their ids moved to `ToolUp.Feliz.*`),
/// because that disagreement is what the old string-only list could not
/// express.
let private declared = [
    TemplateGate.package "ToolUp.Platform.Core" "src/ToolUp.Platform.Core/ToolUp.Platform.Core.fsproj"
    TemplateGate.package "ToolUp.Feliz.AgGrid" "src/Feliz.AgGrid/Feliz.AgGrid.fsproj"
    TemplateGate.package "ToolUp.Feliz.AgCharts" "src/Feliz.AgCharts/Feliz.AgCharts.fsproj"
]

let private allProduced = declared |> List.map (fun p -> nupkg p.Id)

[<Tests>]
let tests =
    testList "TemplateGate.unproducedIds" [

        // ── GREEN: the closure the repo actually produces ────────────
        test "a closure whose every declared id was emitted reports nothing" {
            Expect.equal (TemplateGate.unproducedIds version declared allProduced) [] "the real closure must be silent"
        }

        test "full paths are accepted, not only bare file names" {
            let paths = allProduced |> List.map (fun f -> "C:/repo/obj/template-gate-feed/" + f)

            Expect.equal
                (TemplateGate.unproducedIds version declared paths)
                []
                "the gate passes the enumerated feed paths straight in — reading anything but the file name would report every package missing"
        }

        test "extra packages in the feed are not an error" {
            // The scratch feed also collects the .snupkg siblings and any
            // package a dependency pack emitted. The guard asks whether
            // every DECLARED id is present, never whether the feed is
            // exactly the closure.
            let noisy = allProduced @ [ nupkg "ToolUp.Something.Else"; "readme.txt" ]

            Expect.equal (TemplateGate.unproducedIds version declared noisy) [] "a superset feed is fine"
        }

        test "package id casing does not decide the verdict" {
            let shouted = allProduced |> List.map _.ToUpperInvariant()

            Expect.equal
                (TemplateGate.unproducedIds version declared shouted)
                []
                "NuGet ids are case-insensitive; a case-only difference must not read as a stranger's package"
        }

        // ── RED: the case the guard exists for ───────────────────────
        test "an id this repo does not produce is named" {
            // THE go-red case, and the exact 2026-09 instance: the gate
            // declares the bare `Feliz.AgGrid` id — which nuget.org
            // serves as Compositional-IT's unrelated binding — while the
            // repo emits `ToolUp.Feliz.AgGrid`.
            let strangers =
                declared
                @ [ TemplateGate.package "Feliz.AgGrid" "src/Feliz.AgGrid/Feliz.AgGrid.fsproj" ]

            Expect.equal
                (TemplateGate.unproducedIds version strangers allProduced)
                [ "Feliz.AgGrid" ]
                "the id the pack did not emit must be named"
        }

        test "several unproduced ids are all named, in declaration order" {
            let strangers =
                declared
                @ [
                    TemplateGate.package "Feliz.AgGrid" "src/Feliz.AgGrid/Feliz.AgGrid.fsproj"
                    TemplateGate.package "Feliz.AgCharts" "src/Feliz.AgCharts/Feliz.AgCharts.fsproj"
                ]

            Expect.equal
                (TemplateGate.unproducedIds version strangers allProduced)
                [ "Feliz.AgGrid"; "Feliz.AgCharts" ]
                "reporting only the first would cost a round trip per offender"
        }

        test "an id produced at a DIFFERENT version does not satisfy the declaration" {
            // The throwaway gate version is load-bearing: a package left
            // in the feed from a previous run at the released version is
            // not evidence that THIS run packed it.
            let stale = declared |> List.map (fun p -> sprintf "%s.0.23.0.nupkg" p.Id)

            Expect.equal
                (TemplateGate.unproducedIds version declared stale)
                (declared |> List.map _.Id)
                "a stale-version artefact must not be read as this run's output"
        }

        test "an empty feed reports every declared id" {
            Expect.equal
                (TemplateGate.unproducedIds version declared [])
                (declared |> List.map _.Id)
                "a pack that produced nothing at all must not read as green"
        }

        // ── The message a reader gets ────────────────────────────────
        test "the report names the gate, the version, every id and the class" {
            let message = TemplateGate.report "VerifyTemplates" version [ "Feliz.AgGrid" ]

            Expect.stringContains message "VerifyTemplates" "the gate that failed"
            Expect.stringContains message version "the version it packed at"
            Expect.stringContains message "Feliz.AgGrid" "the offending id"
            Expect.stringContains message "stranger" "the class, so the reader knows why this matters"
            Expect.stringContains message "PackageId" "where to look in the declared project"
        }

        // ─── Phase 903 — the private-packages fix ────────────────────
        //
        // `VerifyTemplates` / `VerifyPackagedModuleTemplate` used to wipe
        // the throwaway gate version's cache entries out of a GUESSED
        // global-packages folder — `NUGET_PACKAGES` if set, else
        // `~/.nuget/packages`. That guess is narrower than NuGet's own
        // resolution (a `globalPackagesFolder` nuget.config setting wins
        // over both), so a machine that sets it made the eviction step a
        // silent no-op while restore kept resolving whatever NuGet's REAL
        // cache already held — a stale copy could mask genuinely broken
        // source and the gate would report green. `withScratchRoot`
        // reproduces the retired guess as a LOCAL function (production
        // code no longer has one to call) purely to pin the class of bug
        // it had; every other test below exercises the actual fix.
        testList "TemplateGatePackages" [

            let withScratchRoot (body: string -> unit) =
                let temp =
                    Path.Combine(Path.GetTempPath(), "toolup-tgp-" + Guid.NewGuid().ToString "N")

                Directory.CreateDirectory temp |> ignore

                try
                    body temp
                finally
                    if Directory.Exists temp then
                        Directory.Delete(temp, true)

            /// The retired two-path guess `VerifyTemplates` used to make,
            /// reproduced here only to demonstrate what it misses — this is
            /// NOT called from production code any more.
            let retiredGuessedFolder (envNugetPackages: string option) (assumedProfileNuget: string) =
                match envNugetPackages with
                | Some dir when dir <> "" -> dir
                | _ -> assumedProfileNuget

            // ── RED (pinned): the guess misses a real, configured folder ─
            test
                "pin it red — a globalPackagesFolder that is neither of the two assumed paths hides a stale entry from the retired guess" {
                withScratchRoot (fun scratch ->
                    // The machine's REAL global-packages folder, as set by a
                    // `globalPackagesFolder` entry in some nuget.config the
                    // retired guess never reads — neither `NUGET_PACKAGES`
                    // (unset here) nor the profile default.
                    let realConfiguredFolder = Path.Combine(scratch, "elsewhere", "nuget-packages")

                    let staleVersionDir =
                        Path.Combine(realConfiguredFolder, "toolup.platform.core", "0.0.0-templategate")

                    Directory.CreateDirectory staleVersionDir |> ignore
                    File.WriteAllText(Path.Combine(staleVersionDir, "stale.nupkg"), "stale")

                    let assumedProfileNuget = Path.Combine(scratch, "profile", ".nuget", "packages")

                    let guessed = retiredGuessedFolder None assumedProfileNuget

                    Expect.notEqual
                        guessed
                        realConfiguredFolder
                        "the retired guess never considered a configured globalPackagesFolder, so it names a folder that is not the real one"

                    Expect.isTrue
                        (Directory.Exists staleVersionDir)
                        "the stale entry in the REAL folder is untouched by the retired guess — this is the bug: restore would still resolve it")
            }

            // ── GREEN: the fix removes the guess, so it cannot repeat that miss ─
            test "privatePackagesFolder is a pure function of the repo root — it consults no ambient config" {
                let repoRoot = @"C:\repos\some-sdk-checkout"

                let a = TemplateGatePackages.privatePackagesFolder repoRoot
                let b = TemplateGatePackages.privatePackagesFolder repoRoot

                Expect.equal a b "computing it twice for the same root must agree"

                Expect.stringContains
                    a
                    (Path.Combine(repoRoot, "obj"))
                    "it is scoped under the repo's own obj/, never a machine-wide folder"
            }

            test "two different repo roots (two worktrees) never resolve the same private folder" {
                let checkoutA =
                    TemplateGatePackages.privatePackagesFolder @"C:\repos\some-sdk-checkout-a"

                let checkoutB =
                    TemplateGatePackages.privatePackagesFolder @"C:\repos\some-sdk-checkout-b"

                Expect.notEqual
                    checkoutA
                    checkoutB
                    "two worktrees running the gate at once must never contend for one folder"
            }

            test "evict wipes a populated private folder and reports every entry it removed, sorted" {
                withScratchRoot (fun scratch ->
                    let folder = Path.Combine(scratch, "template-gate-packages")

                    Directory.CreateDirectory(Path.Combine(folder, "toolup.platform.server"))
                    |> ignore

                    Directory.CreateDirectory(Path.Combine(folder, "toolup.platform.core"))
                    |> ignore

                    File.WriteAllText(Path.Combine(folder, "toolup.platform.core", "marker.txt"), "x")

                    let report = TemplateGatePackages.evict folder

                    Expect.equal
                        report.Removed
                        [ "toolup.platform.core"; "toolup.platform.server" ]
                        "both prior entries are named, sorted for a stable report"

                    Expect.isTrue (Directory.Exists folder) "the folder itself is left present"

                    Expect.isEmpty
                        (Directory.EnumerateFileSystemEntries folder |> List.ofSeq)
                        "and empty — every entry was actually removed")
            }

            test "evict on a folder that does not exist yet creates it and reports nothing removed" {
                withScratchRoot (fun scratch ->
                    let folder = Path.Combine(scratch, "never-seen-before")

                    let report = TemplateGatePackages.evict folder

                    Expect.equal report.Removed [] "a cold run removed nothing, because there was nothing"
                    Expect.isTrue (Directory.Exists folder) "evict leaves the folder present either way")
            }

            test "evict never touches a folder other than the one it was given" {
                withScratchRoot (fun scratch ->
                    // The whole point: a private folder's evict cannot reach
                    // outside itself, unlike the retired shared-folder guess,
                    // which read (and could evict) a machine-wide path.
                    let untouched = Path.Combine(scratch, "untouched", "toolup.platform.core")
                    Directory.CreateDirectory untouched |> ignore

                    let ownFolder = Path.Combine(scratch, "own", "template-gate-packages")

                    TemplateGatePackages.evict ownFolder |> ignore

                    Expect.isTrue
                        (Directory.Exists untouched)
                        "evicting the private folder must never reach a sibling directory")
            }
        ]
    ]