// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.TemplateGateTests

open Expecto
open ToolUp.Platform.Build

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
    ]