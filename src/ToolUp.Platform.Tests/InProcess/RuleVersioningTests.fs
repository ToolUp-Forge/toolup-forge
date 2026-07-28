module ToolUp.Platform.Tests.InProcess.RuleVersioningTests

open Expecto
open ToolUp.Platform

// ─── Phase 597 — rule-manifest versioning + the errata channel ────────
//
// The prover's own lifecycle. Four properties the phase turns on:
//
//   1. **Every exported rule carries a version.** The manifest is
//      projected from the same `ruleManifest` lists the runtime checks
//      read, across all three families, so a rule cannot ship
//      unversioned.
//   2. **Stamp round-trip.** A stamped result records what it was
//      proven under, and that record survives its published wire form.
//   3. **Bump semantics.** A tightening is a minor bump, and a checker
//      holding a result stamped under the earlier version can tell,
//      mechanically, that its prior *pass* is no longer evidence.
//   4. **Errata lookup over a stamped result.** An erratum against a
//      rule version identifies the stamped results evaluated under it —
//      and, just as load-bearing, does NOT flag results stamped outside
//      the affected range.

let private v major minor patch : RuleVersion = {
    RuleMajor = major
    RuleMinor = minor
    RulePatch = patch
}

/// A stamped result standing in for one a consumer persisted: a green
/// composition check, stamped under the shipped rule versions.
let private greenStamped () : StampedCompositionResult =
    CompositionRuleVersions.checkStamped CompositionReferences.empty (CompositionManifest.build [] [] [] [] [])

/// A published correction against a rule at a range of versions — the
/// unit the errata channel trades in.
let private erratum: RuleErratum = {
    ErratumId = "ERR-TEST-001"
    ErratumRuleCode = "duplicate-component-id"
    ErratumFirstAffected = {
        RuleMajor = 1
        RuleMinor = 0
        RulePatch = 0
    }
    ErratumLastAffected = {
        RuleMajor = 1
        RuleMinor = 0
        RulePatch = 2
    }
    ErratumDescription = "The rule compared ids case-sensitively, so a case-only collision was not reported."
    ErratumDisposition =
        CorrectedIn {
            RuleMajor = 1
            RuleMinor = 0
            RulePatch = 3
        }
}

let tests =
    testList "RuleVersioning" [

        // ── 1. Every exported rule carries a version ──

        test "every rule every family exports carries a version" {
            let exported =
                CompositionValidator.ruleManifest
                @ EventTopologyPreflight.ruleManifest
                @ DataFootprintPreflight.ruleManifest
                |> List.map _.Code

            Expect.isNonEmpty exported "the fixture is meaningless if no rule ships"

            for code in exported do
                Expect.isSome
                    (CompositionRuleVersions.tryVersion code)
                    (sprintf "rule '%s' is exported by a manifest but carries no version" code)

            Expect.equal
                (CompositionRuleVersions.allRules |> List.map _.VersionedRule.Code)
                exported
                "the versioned manifest is exactly the exported rules, in family order — no extras, no omissions"
        }

        test "rules that shipped before Phase 597 seed at 1.0.0" {
            for rule in CompositionRuleVersions.allRules do
                Expect.equal
                    rule.RuleSemVer
                    RuleVersion.initial
                    (sprintf "rule '%s' seeds at the initial version" rule.VersionedRule.Code)
        }

        test "an unknown rule code has no version rather than a defaulted one" {
            Expect.isNone
                (CompositionRuleVersions.tryVersion "no-such-rule")
                "a checker holding a stale rule set gets an honest unknown"
        }

        test "seeding honours a per-rule override and leaves the rest at 1.0.0" {
            let overrides = [ "duplicate-component-id", v 1 1 0 ]

            let seeded =
                CompositionRuleVersions.seed overrides "test-family" CompositionValidator.ruleManifest

            let versionOf code =
                seeded |> List.find (fun r -> r.VersionedRule.Code = code) |> _.RuleSemVer

            Expect.equal (versionOf "duplicate-component-id") (v 1 1 0) "the override wins"

            Expect.equal
                (versionOf "orphaned-tool-reference")
                RuleVersion.initial
                "an unlisted rule still seeds at 1.0.0"

            Expect.all seeded (fun r -> r.VersionedFamily = "test-family") "the family label rides every seeded rule"
        }

        test "the exported manifest carries the versions in its wire form" {
            let document =
                CompositionRuleVersions.toWireDocument CompositionRuleVersions.allRules

            Expect.equal document.ManifestVersion "1.0.0" "the manifest version is published"

            Expect.all document.Rules (fun r -> r.Version = "1.0.0") "every published rule carries its version string"

            Expect.contains
                (document.Rules |> List.map _.Rule)
                "duplicate-component-id"
                "a known rule is present in the published manifest"

            Expect.contains
                (document.Rules |> List.map _.Family)
                "event-topology"
                "the sibling rule families are published alongside the composition ones"
        }

        // ── 2. Stamp round-trip ──

        test "a composition check records what it was proven under" {
            let result = greenStamped ()

            Expect.isEmpty result.StampedDefects "an empty composition is well-formed"

            Expect.equal
                result.StampedUnder.StampManifestVersion
                CompositionRuleVersions.ManifestVersion
                "the stamp names the manifest version"

            Expect.equal
                (result.StampedUnder.StampRules |> List.map _.StampedRuleCode)
                (CompositionValidator.ruleManifest |> List.map _.Code)
                "the stamp enumerates exactly the rules that ran, in order"
        }

        test "the stamp round-trips through version formatting" {
            let stamp = CompositionRuleVersions.currentStamp

            let back =
                stamp.StampRules
                |> List.map (fun s -> {
                    s with
                        StampedRuleSemVer =
                            RuleVersion.format s.StampedRuleSemVer
                            |> RuleVersion.tryParse
                            |> Option.defaultValue (v 0 0 0)
                })

            Expect.equal back stamp.StampRules "format -> parse preserves every stamped rule version"
        }

        test "a class-restricted check stamps only the rules of that class" {
            let stamped =
                CompositionRuleVersions.checkClassStamped
                    ExternalProbeRule
                    CompositionReferences.empty
                    (CompositionManifest.build [] [] [] [] [])

            Expect.isEmpty
                stamped.StampedUnder.StampRules
                "no external-probe rule ships, so a run of that class must not stamp as though rules ran"
        }

        test "the shipped stamp is not drifting against the shipped manifest" {
            Expect.isEmpty
                (CompositionRuleVersions.driftSince CompositionRuleVersions.currentStamp)
                "a result stamped by this build has nothing to re-derive against this build"
        }

        // ── 3. Bump semantics ──

        test "a tightening is a minor bump and zeroes the patch" {
            Expect.equal (RuleVersion.applyBump MinorBump (v 1 0 4)) (v 1 1 0) "minor bump zeroes the patch"

            Expect.equal (RuleVersion.applyBump MajorBump (v 1 3 4)) (v 2 0 0) "major bump zeroes minor and patch"

            Expect.equal (RuleVersion.applyBump PatchBump (v 1 3 4)) (v 1 3 5) "patch bump moves only the patch"
        }

        test "a checker classifies an observed version change" {
            Expect.equal (RuleVersion.bumpBetween (v 1 0 0) (v 1 0 1)) (Some PatchBump) "1.0.0 -> 1.0.1 is a patch"

            Expect.equal (RuleVersion.bumpBetween (v 1 0 0) (v 1 1 0)) (Some MinorBump) "1.0.0 -> 1.1.0 is a tightening"

            Expect.equal
                (RuleVersion.bumpBetween (v 1 4 2) (v 2 0 0))
                (Some MajorBump)
                "1.4.2 -> 2.0.0 is a meaning change"

            Expect.isNone (RuleVersion.bumpBetween (v 1 0 0) (v 1 0 0)) "an unchanged rule is not a bump"

            Expect.isNone (RuleVersion.bumpBetween (v 1 1 0) (v 1 0 0)) "a backwards reading is not a bump"
        }

        test "a tightening invalidates a prior pass; a message fix does not" {
            Expect.isTrue
                (RuleVersion.invalidatesPriorPass (v 1 0 0) (v 1 1 0))
                "a pass under 1.0.0 is not evidence of a pass under a tightened 1.1.0"

            Expect.isTrue (RuleVersion.invalidatesPriorPass (v 1 0 0) (v 2 0 0)) "a meaning change carries nothing over"

            Expect.isFalse
                (RuleVersion.invalidatesPriorPass (v 1 0 0) (v 1 0 3))
                "a message / implementation fix leaves prior conclusions standing"
        }

        test "driftSince reports a tightening against a result stamped earlier" {
            // A result stamped under 0.9.0 of a rule this build ships at
            // 1.0.0 — the shape of a consumer holding an older green.
            let stale: RuleEvaluationStamp = {
                StampManifestVersion = v 0 9 0
                StampRules = [
                    {
                        StampedRuleCode = "duplicate-component-id"
                        StampedRuleSemVer = v 0 9 0
                    }
                ]
            }

            match CompositionRuleVersions.driftSince stale with
            | [ (stamped, bump) ] ->
                Expect.equal stamped.StampedRuleCode "duplicate-component-id" "the drift names the rule"
                Expect.equal bump MajorBump "0.9.0 -> 1.0.0 crosses a major"
            | other -> failtestf "expected exactly one drifted rule, got %A" other
        }

        // ── 4. Errata lookup over a stamped result ──

        test "an erratum identifies a stamped result evaluated under an affected version" {
            let result = greenStamped ()
            let impacts = RuleErrata.against [ erratum ] result.StampedUnder

            Expect.hasLength impacts 1 "the shipped rule sits at 1.0.0, inside [1.0.0, 1.0.2]"

            Expect.equal (List.head impacts).ImpactErratum.ErratumId "ERR-TEST-001" "the impact cites the erratum"

            Expect.isTrue (RuleErrata.isAffected [ erratum ] result.StampedUnder) "the result is flagged as affected"

            let rendered = RuleErrata.render impacts

            Expect.stringContains rendered "ERR-TEST-001" "the readable form cites the erratum id"
            Expect.stringContains rendered "1.0.3" "and names the version the correction landed in"
        }

        test "a result stamped outside the affected range is not flagged" {
            let clean: RuleEvaluationStamp = {
                StampManifestVersion = v 1 1 0
                StampRules = [
                    {
                        StampedRuleCode = "duplicate-component-id"
                        StampedRuleSemVer = v 1 0 3
                    }
                ]
            }

            Expect.isEmpty (RuleErrata.against [ erratum ] clean) "1.0.3 is past the corrected-in version"
        }

        test "an erratum against another rule does not flag this result" {
            let otherRule = {
                erratum with
                    ErratumRuleCode = "orphaned-tool-reference"
            }

            let stamp: RuleEvaluationStamp = {
                StampManifestVersion = v 1 0 0
                StampRules = [
                    {
                        StampedRuleCode = "duplicate-component-id"
                        StampedRuleSemVer = v 1 0 0
                    }
                ]
            }

            Expect.isEmpty
                (RuleErrata.against [ otherRule ] stamp)
                "errata are matched by rule code, not by version alone"
        }

        test "errata round-trip through their published wire form" {
            let withdrawn = {
                erratum with
                    ErratumId = "ERR-TEST-002"
                    ErratumDisposition = ErratumWithdrawn
            }

            let both = [ erratum; withdrawn ]

            match RuleErrata.ofWire (RuleErrata.toWire both) with
            | Ok back -> Expect.equal back both "toWire -> ofWire preserves both dispositions"
            | Error why -> failtestf "round-trip failed: %s" why
        }

        test "a published errata document parses" {
            let json =
                """
                [
                  {
                    "ErratumId": "ERR-2026-001",
                    "RuleCode": "orphaned-tool-reference",
                    "AffectedFrom": "1.0.0",
                    "AffectedTo": "1.0.0",
                    "Description": "The reserved-source exemption was too broad.",
                    "Disposition": "corrected-in:1.0.1"
                  }
                ]
                """

            match RuleErrata.parse json with
            | Ok [ one ] ->
                Expect.equal one.ErratumId "ERR-2026-001" "the id round-trips"
                Expect.equal one.ErratumDisposition (CorrectedIn(v 1 0 1)) "the disposition parses to its typed form"
            | Ok other -> failtestf "expected exactly one erratum, got %A" other
            | Error why -> failtestf "expected a parse, got: %s" why
        }

        test "an absent errata document is no errata, not a failure" {
            match RuleErrata.tryLoad "no-such-directory/composition-rule-errata.json" with
            | Ok errata -> Expect.isEmpty errata "a missing document must never fail a checker shut"
            | Error why -> failtestf "a missing errata document must not be an error: %s" why
        }

        test "an empty errata document is no errata" {
            Expect.equal (RuleErrata.parse "[]") (Ok []) "the shipped empty document reads as no errata"
            Expect.equal (RuleErrata.parse "   ") (Ok []) "so does an empty file"
        }

        test "a malformed erratum is an error naming it, never a silently-dropped row" {
            let json =
                """[{"ErratumId":"ERR-BAD","RuleCode":"r","AffectedFrom":"one","AffectedTo":"1.0.0","Description":"d","Disposition":"withdrawn"}]"""

            match RuleErrata.parse json with
            | Ok other ->
                failtestf "a malformed erratum must not load as %A — that reads as a clean bill of health" other
            | Error why ->
                Expect.stringContains why "ERR-BAD" "the failure names the offending erratum"
                Expect.stringContains why "one" "and the value it could not read"
        }

        test "an unknown disposition is an error" {
            let json =
                """[{"ErratumId":"ERR-BAD-2","RuleCode":"r","AffectedFrom":"1.0.0","AffectedTo":"1.0.0","Description":"d","Disposition":"maybe"}]"""

            match RuleErrata.parse json with
            | Ok _ -> failtest "an unrecognised disposition must not load"
            | Error why -> Expect.stringContains why "withdrawn" "the failure states the accepted forms"
        }

        test "an inverted affected range is an error" {
            let json =
                """[{"ErratumId":"ERR-BAD-3","RuleCode":"r","AffectedFrom":"1.2.0","AffectedTo":"1.0.0","Description":"d","Disposition":"withdrawn"}]"""

            match RuleErrata.parse json with
            | Ok _ -> failtest "an inverted range matches nothing and must not load silently"
            | Error why -> Expect.stringContains why "later than" "the failure says what is wrong with the range"
        }

        // ── Version parsing / ordering ──

        test "versions parse, format and order" {
            Expect.equal (RuleVersion.tryParse "2.11.3") (Some(v 2 11 3)) "a well-formed version parses"
            Expect.equal (RuleVersion.format (v 2 11 3)) "2.11.3" "and formats back"
            Expect.isNone (RuleVersion.tryParse "1.0") "a two-part version is rejected"
            Expect.isNone (RuleVersion.tryParse "1.0.x") "a non-numeric component is rejected"
            Expect.isNone (RuleVersion.tryParse "-1.0.0") "a negative component is rejected"
            Expect.isNone (RuleVersion.tryParse "") "an empty string is rejected"

            Expect.isTrue (RuleVersion.compare (v 1 2 0) (v 1 10 0) < 0) "ordering is numeric, not lexicographic"

            Expect.isTrue (RuleVersion.isWithin (v 1 0 0) (v 1 2 0) (v 1 1 9)) "isWithin is inclusive of the interior"

            Expect.isTrue (RuleVersion.isWithin (v 1 0 0) (v 1 2 0) (v 1 2 0)) "and of the upper bound"

            Expect.isFalse (RuleVersion.isWithin (v 1 0 0) (v 1 2 0) (v 1 2 1)) "but excludes past it"
        }
    ]