// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Build.Tests.ConformanceCoverageTests

open System.IO
open Expecto

// ─── Phase 259 — the conformance-pack coverage gate ───────────────────
//
// Two halves, and the second is the one that makes the first worth
// anything.
//
// **The tree half** asserts the committed ratchet baseline agrees with
// the checkout: no replaceable seam has appeared without a pack, no pack
// has appeared that only one implementation runs, and no baseline row
// has outlived the debt it records.
//
// **The go-red half** drives every finding class and every parser from
// SYNTHETIC inputs. The gate's whole content is a set difference, and a
// set difference over two empty sets is clean — so a derivation that
// silently matched nothing (a moved `src/`, a regex that stopped
// matching, a repo-root walk landing above the checkout) would report
// perfect coverage forever, which is the exact failure mode this gate
// was built to prevent one level down. The vacuity pins and the six
// go-red cases are what make a green run here mean the comparison
// actually happened.
//
// ── A hazard specific to THIS pack ──
// `ConformanceCoverage.bindingCounts` scans every `.fs` under `src/` for
// `<Pack>.tests` call sites, and this file is under `src/`. Writing a
// REAL pack name followed by `.tests` in a fixture would inflate that
// pack's binding count and could silently retire a legitimate
// [SINGLE-BOUND] row. Every fixture below therefore uses invented names
// (`IFooContract`, `IBarContract`) that no pack file matches.
//
// Zero shipped code: test-tier only (GP 13).

/// Repo root (`toolup-forge`) from the running test assembly:
/// `src/ToolUp.Platform.Build.Tests/bin/<Config>/net10.0` -> up 5. The
/// same walk `SdkManifestTests` and `ArchitectureFitness` use.
let private repoRoot () =
    let assemblyDir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location)

    Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."))

let private reconciliation = lazy (ConformanceCoverage.reconcile (repoRoot ()))

// ─── Synthetic fixtures ───────────────────────────────────────────────

let private pack name bindable : ConformanceCoverage.Pack = {
    Name = name
    Interface = ConformanceCoverage.interfaceOfPack name
    Bindable = bindable
}

/// A world with one packed seam, one unpacked seam, and the unpacked one
/// acknowledged in the baseline: clean by construction, and the control
/// every go-red case below perturbs by exactly one fact.
let private cleanWorld () =
    let seams = Map.ofList [ "IFoo", 3; "IBar", 2 ]
    let packs = [ pack "IFooContract" true ]
    let bindings = Map.ofList [ "IFooContract", 2 ]

    let registry = {
        ConformanceCoverage.emptyRegistry with
            Unpacked = [ "IBar" ]
    }

    seams, packs, bindings, registry

let private findingsOf (seams, packs, bindings, registry) =
    (ConformanceCoverage.reconcileWith seams packs bindings registry).Findings

// ─── The pack ─────────────────────────────────────────────────────────

let tests =
    testList "ConformanceCoverage" [

        // ── Vacuity pins: prove each derivation saw the tree ──────────

        testList "the derivations are non-empty" [
            test "api-baselines yield a public interface universe" {
                let interfaces = ConformanceCoverage.publicInterfaces (repoRoot ())

                Expect.isGreaterThan
                    (Set.count interfaces)
                    100
                    (sprintf
                        "found %d public interface(s) across %s. This repo publishes hundreds; a count this low means the baseline glob or the repo-root walk is broken, and every coverage assertion below would then be comparing empty sets."
                        (Set.count interfaces)
                        (ConformanceCoverage.apiBaselinesDir (repoRoot ())))
            }

            test "the contracts directory yields packs" {
                let packs = ConformanceCoverage.packs (repoRoot ())

                Expect.isGreaterThan
                    (List.length packs)
                    50
                    (sprintf
                        "found %d contract pack(s) under %s — the directory carries well over fifty."
                        (List.length packs)
                        (ConformanceCoverage.contractsDir (repoRoot ())))

                Expect.isGreaterThan
                    (packs |> List.filter _.Bindable |> List.length)
                    50
                    "almost every pack exposes a `tests` entry point; a low count means the entry-point regex stopped matching and every pack would read as unbindable (and so escape the binding checks entirely)."
            }

            test "the tree yields replaceable seams" {
                let seams = ConformanceCoverage.replaceableSeams (repoRoot ())

                Expect.isGreaterThan
                    (Map.count seams)
                    50
                    (sprintf
                        "found %d replaceable seam(s) (public interface with >=2 production implementations). The measured figure when this gate was written was 146; a collapse means the implementation-site scan or the test-path exclusion is broken."
                        (Map.count seams))
            }

            test "packs are bound by real call sites" {
                let root = repoRoot ()
                let packs = ConformanceCoverage.packs root
                let bindings = ConformanceCoverage.bindingCounts root packs

                Expect.isGreaterThan
                    (bindings |> Map.toSeq |> Seq.sumBy snd)
                    50
                    "the binding scan found almost no `<Pack>.tests` call sites. Every pack would then read as unbound, so the check would report dozens of findings rather than passing vacuously — but the count is pinned here so the opposite failure (a scan that matches nothing AND a baseline that lists everything) cannot hide."
            }
        ]

        // ── The tree itself ───────────────────────────────────────────

        test "the committed baseline agrees with the tree" {
            // Under `TOOLUP_APPROVE_API` this REWRITES rather than
            // checks, exactly as the api-baselines beside it do. Note
            // the consequence the api-baseline gate learned the hard
            // way: approve mode passes trivially, so a green run with
            // the variable set proves nothing — re-run without it.
            if ConformanceCoverage.approvalRequested () then
                let path = ConformanceCoverage.approve (repoRoot ())

                skiptestf
                    "TOOLUP_APPROVE_API is set — rewrote %s rather than checking it. Re-run without the variable to verify."
                    path

            match ConformanceCoverage.describe reconciliation.Value with
            | None -> ()
            | Some report -> failtest report
        }

        test "the baseline file exists and parses" {
            if ConformanceCoverage.approvalRequested () then
                skiptest
                    "TOOLUP_APPROVE_API is set — the baseline is being rewritten, so its prior content is not the subject."

            let path = ConformanceCoverage.registryPath (repoRoot ())

            Expect.isTrue
                (File.Exists path)
                (sprintf
                    "the conformance-coverage baseline is missing at %s. Without it every replaceable seam without a pack reads as a NEW finding, so the gate is red rather than absent — but generate it deliberately rather than letting a run write it: %s"
                    path
                    ConformanceCoverage.regenerateCommand)

            let registry = ConformanceCoverage.parseRegistry (File.ReadAllText path)

            Expect.isNonEmpty
                (registry.Unpacked @ registry.SingleBound)
                "the baseline parsed to no rows at all. It was generated carrying real coverage debt, so an empty parse means the section headers or the comment stripping changed and the gate is no longer reading what it thinks it is."
        }

        // ── Go-red: every finding class fires ─────────────────────────

        testList "each finding class fires on a synthetic tree" [
            test "the control world is clean" {
                Expect.isEmpty
                    (findingsOf (cleanWorld ()))
                    "the control must be clean, or the go-red cases below prove nothing about the fact each perturbs."
            }

            test "a replaceable seam with no pack and no baseline row" {
                let seams, packs, bindings, _ = cleanWorld ()

                let findings =
                    findingsOf (seams, packs, bindings, ConformanceCoverage.emptyRegistry)

                Expect.contains
                    findings
                    (ConformanceCoverage.NewUnpackedSeam("IBar", 2))
                    "a seam with two production implementations and no pack must be reported when the baseline does not acknowledge it — this is the finding the whole gate exists for."
            }

            test "an EXEMPT row silences a new unpacked seam" {
                let seams, packs, bindings, _ = cleanWorld ()

                let registry = {
                    ConformanceCoverage.emptyRegistry with
                        Exempt = [ "IBar", "single-implementation by construction" ]
                }

                Expect.isEmpty
                    (findingsOf (seams, packs, bindings, registry))
                    "an exemption carrying a reason is the sanctioned route for a seam that will never be packed; it must silence the finding, and must not itself be reported as stale while the seam is genuinely unpacked."
            }

            test "a baseline row that has since been packed" {
                let seams, packs, bindings, registry = cleanWorld ()

                let stale = {
                    registry with
                        Unpacked = "IFoo" :: registry.Unpacked
                }

                let findings = findingsOf (seams, packs, bindings, stale)

                Expect.isTrue
                    (findings
                     |> List.exists (function
                         | ConformanceCoverage.StaleUnpacked("IFoo", _) -> true
                         | _ -> false))
                    "a seam listed as unpacked that now HAS a pack must fail. This is the ratchet's other direction: without it the file rots into a list of things that used to be true, and coverage could silently regress behind a row nobody removed."
            }

            test "a pack only one implementation binds" {
                let seams, packs, _, registry = cleanWorld ()
                let findings = findingsOf (seams, packs, Map.ofList [ "IFooContract", 1 ], registry)

                Expect.contains
                    findings
                    (ConformanceCoverage.NewSingleBoundPack("IFooContract", 1))
                    "GP 12 treats a portable interface as unproven until a SECOND implementation runs the same pack; a newly single-bound pack must be reported."
            }

            test "a baseline row for a pack that is now bound twice" {
                let seams, packs, bindings, registry = cleanWorld ()

                let stale = {
                    registry with
                        SingleBound = [ "IFooContract" ]
                }

                let findings = findingsOf (seams, packs, bindings, stale)

                Expect.isTrue
                    (findings
                     |> List.exists (function
                         | ConformanceCoverage.StaleSingleBound("IFooContract", _) -> true
                         | _ -> false))
                    "the single-bound ratchet must fail when the debt it records is paid, for the same reason the unpacked one does."
            }

            test "a bindable pack nothing calls" {
                let seams, packs, _, registry = cleanWorld ()
                let findings = findingsOf (seams, packs, Map.empty, registry)

                Expect.contains
                    findings
                    (ConformanceCoverage.UnboundPack "IFooContract")
                    "a conformance pack no implementation runs proves nothing at all, and unlike the two ratchets this one has no baseline — the tree has none today and none may appear."
            }

            test "an exemption for something that is not an unpacked seam" {
                let seams, packs, bindings, registry = cleanWorld ()

                let stale = {
                    registry with
                        Exempt = [ "IFoo", "spent — it has a pack now" ]
                }

                let findings = findingsOf (seams, packs, bindings, stale)

                Expect.isTrue
                    (findings
                     |> List.exists (function
                         | ConformanceCoverage.StaleExempt("IFoo", _) -> true
                         | _ -> false))
                    "an exemption that no longer excuses anything must be removed, or the next reader takes it as a live judgement about a seam it no longer describes."
            }

            test "an unbindable pack is not reported as unbound" {
                let seams, _, _, _ = cleanWorld ()
                let packs = [ pack "IFooContract" false ]

                Expect.isEmpty
                    (findingsOf (seams |> Map.remove "IBar", packs, Map.empty, ConformanceCoverage.emptyRegistry))
                    "a pack with no `tests` entry point (FailClosedContract is the shipped example — a cross-cutting pack, not a per-interface one) cannot be bound by anything, so it must be excluded from both binding checks rather than reported forever."
            }
        ]

        // ── Go-red: the parsers ───────────────────────────────────────

        testList "the parsers read what they claim to" [
            test "interface names come off a baseline, classes do not" {
                let text =
                    "# a comment\nColumnMapping (class)\nToolUp.Platform+IWidget (interface)\nIPlain (interface)\nIGeneric`1 (interface)\n"

                Expect.equal
                    (ConformanceCoverage.interfaceNamesIn text |> List.sort)
                    [ "IGeneric"; "IPlain"; "IWidget" ]
                    "the parser must take only `(interface)` lines, strip namespace/nested qualification to the simple name, and drop the generic arity suffix — every other derivation keys on the name a source file writes."
            }

            test "a baseline with no interface lines yields nothing" {
                Expect.isEmpty
                    (ConformanceCoverage.interfaceNamesIn "Foo (class)\nBar.Baz(System.String) : System.Int32\n")
                    "a baseline of nothing but classes must yield no interfaces — the empty case has to be genuinely empty for the vacuity pins above to mean anything."
            }

            test "implementation sites are found in both F# shapes" {
                let text =
                    "type Impl() =\n    interface IWidget with\n        member _.Go() = ()\n\nlet anon =\n    { new IWidget with\n        member _.Go() = () }\n\nlet generic = { new IStore<string> with\n    member _.Get() = \"\" }\n"

                Expect.equal
                    (ConformanceCoverage.implementedInterfacesIn text |> List.sort)
                    [ "IStore"; "IWidget"; "IWidget" ]
                    "both the type-implementation clause and the object expression are implementations, a generic argument between the name and `with` must not defeat the match, and two sites for one interface must count twice."
            }

            test "a mention that is not an implementation is not counted" {
                Expect.isEmpty
                    (ConformanceCoverage.implementedInterfacesIn
                        "let f (x: IWidget) = x.Go()\ntype T = { Store: IStore }\nabstract Get: unit -> IWidget\n")
                    "taking an interface as a parameter, holding one in a field, or returning one is not implementing it; counting those would promote every consumed interface into the must-pack set."
            }

            test "a pack file name is recognised, a non-pack is not" {
                Expect.equal
                    (ConformanceCoverage.packNameOfFile "IJobFooContract.fs")
                    (Some "IJobFooContract")
                    "a `*Contract.fs` file is a pack"

                Expect.isNone (ConformanceCoverage.packNameOfFile "Helpers.fs") "an ordinary file is not a pack"

                Expect.isNone
                    (ConformanceCoverage.packNameOfFile "Contract.fs")
                    "a bare `Contract.fs` names no interface"
            }

            test "the entry point is found in every shape the tree uses" {
                Expect.isTrue
                    (ConformanceCoverage.hasEntryPoint
                        "module M\n\nlet tests (name: string) (factory: unit -> int) =\n    []\n")
                    "the single-line curried shape"

                Expect.isTrue
                    (ConformanceCoverage.hasEntryPoint
                        "module M\n\nlet tests\n    (name: string)\n    (factory: unit -> int)\n    =\n    []\n")
                    "the wrapped-signature shape — four shipped packs are formatted this way, and a stricter pattern silently classified all four as unbindable, which would have excluded them from the binding checks entirely."

                Expect.isTrue
                    (ConformanceCoverage.hasEntryPoint "module M\n\nlet tests: Test =\n    testList \"x\" []\n")
                    "the type-annotated zero-argument shape"

                Expect.isFalse
                    (ConformanceCoverage.hasEntryPoint
                        "module M\n\nlet private testsHelper () = ()\nlet internal classifierContract f = []\n")
                    "a pack with no `tests` binding is not bindable — a name merely starting with `tests` must not count."
            }

            test "binding call sites are filtered to known packs" {
                let text =
                    "let a = IFooContract.tests \"one\" f\nlet b = IUnknownContract.tests \"two\" g\n"

                Expect.equal
                    (ConformanceCoverage.bindingsIn (Set.ofList [ "IFooContract" ]) text)
                    [ "IFooContract" ]
                    "only call sites naming a pack that exists count; an unknown name is a stale reference or a fixture, not a binding."
            }
        ]
    ]