// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.IO
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform.ConfigValidatorAggregator

// ─── The prover's own lifecycle (Phase 597) ──────────────────────────
//
// [Phase 294] exports *what* the composition validator checks — every
// well-formedness rule as data (`CompositionRuleDescriptor`), so an
// external pre-build checker validates against forge's own invariants
// with no re-encoding. [Phase 585] added *which class* each rule is in.
// Neither says **which version of the check** a conclusion was reached
// under, and that omission has three consequences:
//
//   * a deployment that passed preflight holds no record of what it
//     passed — "green under forge 0.4.7" is archaeology, not data;
//   * a bug in a rule has no correction channel — the rule is silently
//     fixed and every prior conclusion drawn under the broken version
//     stays on the record, indistinguishable from a sound one;
//   * a rule that *tightens* silently invalidates prior conclusions —
//     a composition that passed yesterday's `orphaned-tool-reference`
//     may fail today's, and nothing in the passing result says so.
//
// For a platform whose external claim is machine-checkable
// well-formedness, the prover needs its own lifecycle, as data. Three
// pieces, all additive:
//
// **1. Versions.** A manifest version + a per-rule `RuleVersion`
// (semver). Every shipped rule seeds at 1.0.0; the manifest version
// moves when the rule *set* moves. Exported as
// `CompositionRuleVersions.allRules`, projected from the same declared
// `ruleManifest` lists the runtime checks read — so a version cannot
// exist for a rule that does not ship, or vice versa.
//
// **The bump discipline** (`RuleVersionBump`), which is the whole point
// of using semver here rather than a build number:
//
//   * **patch** — the rule's *message* or implementation changed; the
//     same compositions pass and fail as before. A prior conclusion
//     drawn under an earlier patch stands unchanged.
//   * **minor** — the rule **tightened**: strictly more compositions
//     now fail. A prior *pass* under an earlier minor is no longer
//     evidence of a pass under this one; a prior *fail* still fails.
//     This is the reading `RuleVersion.bumpBetween` exists to give a
//     checker mechanically.
//   * **major** — the rule's **meaning** changed (it now checks a
//     different thing, or loosened). Neither a prior pass nor a prior
//     fail carries over; the conclusion must be re-derived.
//
// **2. Stamps.** A result records what it was proven under.
// `RuleEvaluationStamp` is the manifest version + every (rule, version)
// pair that was evaluated; `StampedCompositionResult` pairs it with the
// defects, `StampedPreflightRun` with the [Phase 9m] preflight
// outcomes. The stamp is a pure projection of the shipped manifest, so
// stamping costs one list map and no I/O — a deployment that ignores it
// pays nothing (GP 11 / GP 13).
//
// **3. Errata.** A rule that shipped wrong is corrected in a later
// version, but the *conclusions already drawn under the wrong version*
// are the thing that matters, and they live outside this process — in a
// consumer's CI log, an attestation, a compliance file. So the
// correction is published as **data beside the manifest**
// (`rule-errata/composition-rule-errata.json`, the wire format below),
// enumerating "rule R at versions [a,b] carries erratum E: description
// + disposition (corrected in vN / withdrawn)". `RuleErrata.against`
// answers, mechanically, "was this stamped result evaluated under an
// erratum-affected rule version?" — a set intersection, not a research
// project. An absent errata file is `Ok []`: no errata is the honest
// reading of "nothing has been found wrong", and it must never be a
// load failure that stops a checker.
//
// **Generic over any exported rule id.** Nothing here knows what a rule
// *does*. `seed` versions any `CompositionRuleDescriptor list`, and the
// stamp / errata machinery keys on the rule code string alone — so the
// five shipped families ([Phase 294] `CompositionValidator`,
// [Phase 431] `EventTopologyPreflight`, [Phase 433]
// `DataFootprintPreflight`, [Phase 434] `ScaleReadinessPreflight`,
// [Phase 488] `ApplianceBootPosture`) are wired here by seeding their
// exported manifests, and a sixth family joins by adding one line.
//
// **Adding a line is the whole obligation, and forgetting it used to be
// silent.** 434's and 488's families were enforced at runtime from the
// day they shipped and absent from this manifest until the 2026-08-26
// tidy pass, because the file hardcoded three `seed` calls and the test
// asserted the same hardcoded three — the two agreed with each other and
// with nothing else. `RuleVersioningTests` now reflects over every
// `ruleManifest : CompositionRuleDescriptor list` in this assembly and
// fails naming any family `allRules` does not publish, so the next
// omission is a red test rather than an absence nobody can see.
//
// **Additive throughout.** No shipped record grew a field: every type
// here is a new sidecar, for the reason recorded on
// `ClassifiedCompositionRule` (growing an F# record changes its
// constructor, breaking every consumer that builds one, and reading as
// a removal under the public-API baseline gate). A consumer that
// ignores versions sees byte-for-byte what it saw before.

/// The semantic version of a single well-formedness rule. Fields are
/// prefixed because `Major` / `Minor` / `Patch` are common enough field
/// names that an unannotated record literal elsewhere could resolve
/// here by accident.
type RuleVersion = {
    RuleMajor: int
    RuleMinor: int
    RulePatch: int
}

/// The three kinds of change a rule can undergo, and what each one does
/// to a conclusion drawn under the previous version. This is the bump
/// discipline as data — see the file header for the full statement.
type RuleVersionBump =
    /// Message / implementation changed; the same compositions pass and
    /// fail. A prior conclusion stands.
    | PatchBump
    /// The rule tightened — strictly more compositions fail. A prior
    /// *pass* is no longer evidence; a prior fail still fails.
    | MinorBump
    /// The rule's meaning changed (different check, or loosened).
    /// Neither a prior pass nor a prior fail carries over.
    | MajorBump

[<RequireQualifiedAccess>]
module RuleVersion =

    /// The version every rule that shipped before Phase 597 seeds at.
    /// Deliberately 1.0.0 rather than 0.x: these rules have been
    /// enforced in shipped builds, so treating them as pre-release
    /// would misdescribe the conclusions already drawn under them.
    let initial: RuleVersion = {
        RuleMajor = 1
        RuleMinor = 0
        RulePatch = 0
    }

    /// `"1.2.3"` — the wire / display form.
    let format (v: RuleVersion) : string =
        sprintf "%d.%d.%d" v.RuleMajor v.RuleMinor v.RulePatch

    /// Parse `"1.2.3"`. `None` for anything else — a caller reading a
    /// published errata document gets an honest rejection rather than a
    /// silently-defaulted range that would under- or over-match.
    let tryParse (text: string) : RuleVersion option =
        if String.IsNullOrWhiteSpace text then
            None
        else
            match text.Trim().Split '.' with
            | [| a; b; c |] ->
                match Int32.TryParse a, Int32.TryParse b, Int32.TryParse c with
                | (true, major), (true, minor), (true, patch) when major >= 0 && minor >= 0 && patch >= 0 ->
                    Some {
                        RuleMajor = major
                        RuleMinor = minor
                        RulePatch = patch
                    }
                | _ -> None
            | _ -> None

    /// Total order on versions: major, then minor, then patch.
    let compare (a: RuleVersion) (b: RuleVersion) : int =
        match Operators.compare a.RuleMajor b.RuleMajor with
        | 0 ->
            match Operators.compare a.RuleMinor b.RuleMinor with
            | 0 -> Operators.compare a.RulePatch b.RulePatch
            | m -> m
        | m -> m

    /// Is `v` inside the inclusive range `[lo, hi]`? The shape an
    /// erratum's affected-version range is tested with.
    let isWithin (lo: RuleVersion) (hi: RuleVersion) (v: RuleVersion) : bool = compare v lo >= 0 && compare v hi <= 0

    /// Apply a bump, per the discipline: a minor bump zeroes the patch,
    /// a major bump zeroes both. The rule author states the *kind* of
    /// change; the arithmetic is not a judgement call.
    let applyBump (bump: RuleVersionBump) (v: RuleVersion) : RuleVersion =
        match bump with
        | PatchBump -> { v with RulePatch = v.RulePatch + 1 }
        | MinorBump -> {
            v with
                RuleMinor = v.RuleMinor + 1
                RulePatch = 0
          }
        | MajorBump -> {
            RuleMajor = v.RuleMajor + 1
            RuleMinor = 0
            RulePatch = 0
          }

    /// Classify an observed version change — what a checker holding a
    /// stamped result asks of the current manifest. `None` when the
    /// versions are equal (nothing changed) or when `current` is
    /// *older* than `stamped` (the checker is holding a newer result
    /// than the build it is asking; not a bump, and not this function's
    /// business to explain).
    let bumpBetween (stamped: RuleVersion) (current: RuleVersion) : RuleVersionBump option =
        if compare current stamped <= 0 then
            None
        elif current.RuleMajor > stamped.RuleMajor then
            Some MajorBump
        elif current.RuleMinor > stamped.RuleMinor then
            Some MinorBump
        else
            Some PatchBump

    /// Did the rule tighten or change meaning between the stamped
    /// version and the current one — i.e. is a prior *pass* no longer
    /// evidence of a pass today? The single question a consumer holding
    /// a green preflight result most often needs answered.
    let invalidatesPriorPass (stamped: RuleVersion) (current: RuleVersion) : bool =
        match bumpBetween stamped current with
        | Some MinorBump
        | Some MajorBump -> true
        | Some PatchBump
        | None -> false

/// One rule of some family, with its version: the [Phase 294]
/// descriptor kept whole (never re-spelled field by field, so it cannot
/// drift from the family's own export) plus the family label and the
/// rule's semver.
type VersionedCompositionRule = {
    /// Which exporting family this rule belongs to — the token an
    /// external checker groups by. Free-form by design: the machinery
    /// is generic over any exported rule id.
    VersionedFamily: string
    VersionedRule: CompositionRuleDescriptor
    RuleSemVer: RuleVersion
}

/// One (rule, version) pair a result was evaluated under.
type StampedRuleVersion = {
    StampedRuleCode: string
    StampedRuleSemVer: RuleVersion
}

/// What a conclusion was proven under: the manifest version plus every
/// rule version evaluated. Pure data — serialise it into an
/// attestation, a CI artefact, or a compliance record, and the errata
/// channel can still speak to it years later.
type RuleEvaluationStamp = {
    StampManifestVersion: RuleVersion
    StampRules: StampedRuleVersion list
}

/// A composition check result and the stamp it was produced under.
type StampedCompositionResult = {
    StampedDefects: CompositionDefect list
    StampedUnder: RuleEvaluationStamp
}

/// A [Phase 9m] preflight run and the rule versions in force when it
/// ran. The additive companion to `IPreflightSnapshot.LastRun`, which
/// is a shipped surface and therefore not grown a field.
type StampedPreflightRun = {
    RunOutcomes: ValidatorOutcome list
    RunStamp: RuleEvaluationStamp
}

/// What was done about an erratum.
type RuleErratumDisposition =
    /// The rule was fixed; this version and later are sound. A stamped
    /// result at or above it is unaffected.
    | CorrectedIn of RuleVersion
    /// The rule was withdrawn — it should never have shipped, and no
    /// conclusion drawn from it means anything.
    | ErratumWithdrawn

/// A published correction against a rule at a range of versions. The
/// unit of the errata channel: "rule R at versions [a, b] carries
/// erratum E: description + disposition".
type RuleErratum = {
    /// Stable, citable id (e.g. `"ERR-2026-001"`). An external record
    /// references this, so it never changes once published.
    ErratumId: string
    /// The rule code this erratum is against — the same token the rule
    /// manifest publishes.
    ErratumRuleCode: string
    /// First affected rule version (inclusive).
    ErratumFirstAffected: RuleVersion
    /// Last affected rule version (inclusive).
    ErratumLastAffected: RuleVersion
    /// What was wrong, in the terms a reader of an affected conclusion
    /// needs: what the rule did, and what it should have done.
    ErratumDescription: string
    ErratumDisposition: RuleErratumDisposition
}

/// The serialised form of an erratum — plain strings only, so the
/// published document never depends on an F# union or record shape
/// round-tripping through a serialiser. Same posture as the
/// `EventTopologyWireEntry` / `DataFootprintWireEntry` golden-file
/// projections.
type RuleErratumWireEntry = {
    ErratumId: string
    RuleCode: string
    AffectedFrom: string
    AffectedTo: string
    Description: string
    /// `"corrected-in:1.1.0"` or `"withdrawn"`.
    Disposition: string
}

/// One stamped rule version an erratum speaks to — the answer to "was
/// this result evaluated under an erratum-affected version?".
type ErratumImpact = {
    ImpactErratum: RuleErratum
    ImpactStamped: StampedRuleVersion
}

/// The serialised form of one versioned rule — the shape the published
/// rule manifest (and the [Phase 287] golden-file gate) carries, so a
/// rules-only change is a visible diff rather than silence.
type RuleVersionWireEntry = {
    Family: string
    Rule: string
    Version: string
    Severity: string
    RuleDescription: string
}

/// The published rule manifest as one document: the manifest version
/// plus every versioned rule. The envelope exists because the two move
/// independently — the manifest version tracks the rule *set*, a rule's
/// version tracks that rule — and a reader needs both to interpret a
/// stamp.
type RuleManifestWireDocument = {
    ManifestVersion: string
    Rules: RuleVersionWireEntry list
}

[<RequireQualifiedAccess>]
module RuleErrata =

    /// The conventional file name of a published errata document. The
    /// document lives beside the manifest it corrects; forge ships its
    /// own at `rule-errata/composition-rule-errata.json`.
    [<Literal>]
    let FileName = "composition-rule-errata.json"

    /// The canonical F#-aware serialiser options (Option / DU / record
    /// converters), so the published document reads the same way every
    /// other forge JSON surface does.
    let private jsonOptions = FableConverters.create ()

    let private dispositionToWire (disposition: RuleErratumDisposition) : string =
        match disposition with
        | CorrectedIn v -> "corrected-in:" + RuleVersion.format v
        | ErratumWithdrawn -> "withdrawn"

    let private dispositionOfWire (token: string) : Result<RuleErratumDisposition, string> =
        let trimmed = if isNull token then "" else token.Trim()

        if trimmed = "withdrawn" then
            Ok ErratumWithdrawn
        elif trimmed.StartsWith "corrected-in:" then
            let versionText = trimmed.Substring "corrected-in:".Length

            match RuleVersion.tryParse versionText with
            | Some v -> Ok(CorrectedIn v)
            | None ->
                Error(sprintf "disposition 'corrected-in:%s' does not name a major.minor.patch version" versionText)
        else
            Error(sprintf "disposition '%s' is neither 'withdrawn' nor 'corrected-in:<major.minor.patch>'" trimmed)

    /// Project errata to their published wire form.
    let toWire (errata: RuleErratum list) : RuleErratumWireEntry list =
        errata
        |> List.map (fun e -> {
            ErratumId = e.ErratumId
            RuleCode = e.ErratumRuleCode
            AffectedFrom = RuleVersion.format e.ErratumFirstAffected
            AffectedTo = RuleVersion.format e.ErratumLastAffected
            Description = e.ErratumDescription
            Disposition = dispositionToWire e.ErratumDisposition
        })

    /// Read errata back from their wire form. A malformed entry is an
    /// `Error` naming the offending erratum id, never a silently-dropped
    /// row: a correction channel that loses corrections is worse than
    /// none, because it reads as a clean bill of health.
    let ofWire (entries: RuleErratumWireEntry list) : Result<RuleErratum list, string> =
        let fail (entry: RuleErratumWireEntry) (why: string) =
            Error(sprintf "erratum '%s' (rule '%s') is malformed: %s" entry.ErratumId entry.RuleCode why)

        let rec go acc remaining =
            match remaining with
            | [] -> Ok(List.rev acc)
            | (entry: RuleErratumWireEntry) :: rest ->
                match RuleVersion.tryParse entry.AffectedFrom, RuleVersion.tryParse entry.AffectedTo with
                | None, _ ->
                    fail entry (sprintf "affectedFrom '%s' is not a major.minor.patch version" entry.AffectedFrom)
                | _, None -> fail entry (sprintf "affectedTo '%s' is not a major.minor.patch version" entry.AffectedTo)
                | Some first, Some last ->
                    if RuleVersion.compare first last > 0 then
                        fail entry "affectedFrom is later than affectedTo"
                    else
                        match dispositionOfWire entry.Disposition with
                        | Error why -> fail entry why
                        | Ok disposition ->
                            go
                                ({
                                    ErratumId = entry.ErratumId
                                    ErratumRuleCode = entry.RuleCode
                                    ErratumFirstAffected = first
                                    ErratumLastAffected = last
                                    ErratumDescription = entry.Description
                                    ErratumDisposition = disposition
                                 }
                                 :: acc)
                                rest

        go [] entries

    /// Parse a published errata document. An empty / whitespace
    /// document is `Ok []` (nothing has been found wrong); anything
    /// unparseable is an `Error`, for the reason `ofWire` gives.
    let parse (json: string) : Result<RuleErratum list, string> =
        if String.IsNullOrWhiteSpace json then
            Ok []
        else
            let parsed =
                try
                    Ok(JsonSerializer.Deserialize<RuleErratumWireEntry list>(json, jsonOptions))
                with ex ->
                    Error(sprintf "errata document is not valid JSON: %s" ex.Message)

            match parsed with
            | Error why -> Error why
            | Ok entries when isNull (box entries) -> Ok []
            | Ok entries -> ofWire entries

    /// Load a published errata document from disk. **A missing file is
    /// `Ok []`, not an error** — no errata is the honest reading of "no
    /// correction has been published", and a checker must not fail shut
    /// on its absence.
    let tryLoad (path: string) : Result<RuleErratum list, string> =
        if String.IsNullOrWhiteSpace path || not (File.Exists path) then
            Ok []
        else
            try
                parse (File.ReadAllText path)
            with ex ->
                Error(sprintf "errata document at '%s' could not be read: %s" path ex.Message)

    /// Does this erratum speak to this stamped rule version?
    let affectsStamped (erratum: RuleErratum) (stamped: StampedRuleVersion) : bool =
        erratum.ErratumRuleCode = stamped.StampedRuleCode
        && RuleVersion.isWithin erratum.ErratumFirstAffected erratum.ErratumLastAffected stamped.StampedRuleSemVer

    /// **The question the channel exists to answer.** Given a published
    /// errata set and a stamped result, which errata speak to a rule
    /// version that result was actually evaluated under? Empty means the
    /// conclusion is unaffected — mechanically, not by inspection.
    let against (errata: RuleErratum list) (stamp: RuleEvaluationStamp) : ErratumImpact list = [
        for erratum in errata do
            for stamped in stamp.StampRules do
                if affectsStamped erratum stamped then
                    {
                        ImpactErratum = erratum
                        ImpactStamped = stamped
                    }
    ]

    /// Is a stamped result affected by any published erratum?
    let isAffected (errata: RuleErratum list) (stamp: RuleEvaluationStamp) : bool =
        not (List.isEmpty (against errata stamp))

    /// Render impacts for an operator: what was proven, what is wrong
    /// with it, and what to do about it.
    let render (impacts: ErratumImpact list) : string =
        impacts
        |> List.map (fun impact ->
            let disposition =
                match impact.ImpactErratum.ErratumDisposition with
                | CorrectedIn v ->
                    sprintf
                        "corrected in %s — re-run the check under %s or later"
                        (RuleVersion.format v)
                        (RuleVersion.format v)
                | ErratumWithdrawn -> "the rule was withdrawn — no conclusion drawn from it holds"

            sprintf
                "[%s] rule '%s' at version %s: %s (%s)"
                impact.ImpactErratum.ErratumId
                impact.ImpactStamped.StampedRuleCode
                (RuleVersion.format impact.ImpactStamped.StampedRuleSemVer)
                impact.ImpactErratum.ErratumDescription
                disposition)
        |> String.concat "\n"

/// The versioned rule manifest: every rule every shipped family exports
/// (Phase 294 vocabulary), with its version — plus the stamping helpers
/// that record what a result was proven under.
[<RequireQualifiedAccess>]
module CompositionRuleVersions =

    /// The version of the rule *manifest* as a whole — distinct from any
    /// individual rule's version. It moves when the rule SET moves: a
    /// minor bump when a rule is added (a composition that passed the
    /// old manifest may fail the new one, exactly the tightening
    /// reading), a major bump when one is removed or renamed.
    ///
    /// 1.0.0 was the manifest as it stood when Phase 597 landed: the five
    /// `CompositionValidator` rules, the two `EventTopologyPreflight`
    /// rules, and the two `DataFootprintPreflight` rules.
    ///
    /// **1.1.0** adds the two families that shipped alongside 597 and
    /// were never wired in: [Phase 434]'s `ScaleReadinessPreflight` and
    /// [Phase 488]'s `ApplianceBootPosture`. A minor bump, per the rule
    /// above — a composition that passed the 1.0.0 manifest may fail the
    /// 1.1.0 one, because two rules that were being *enforced at runtime*
    /// were absent from the *published* manifest and so from every stamp
    /// drawn under it. Nothing about either rule changed; what changed is
    /// that a conclusion now records having been reached under them.
    let ManifestVersion: RuleVersion = {
        RuleMajor = 1
        RuleMinor = 1
        RulePatch = 0
    }

    /// Per-rule version overrides, keyed by rule code. **Empty today by
    /// construction** — every rule that shipped before Phase 597 seeds
    /// at 1.0.0, because they were all enforced under the same
    /// unversioned manifest and no earlier state exists to distinguish.
    ///
    /// **This list is the bump surface.** Tightening
    /// `orphaned-tool-reference` means adding
    /// `"orphaned-tool-reference", { RuleMajor = 1; RuleMinor = 1; RulePatch = 0 }`
    /// here in the same commit as the evaluator change — and the golden
    /// rule-manifest baseline turns that into a reviewed diff rather
    /// than a silent behaviour change.
    let overrides: (string * RuleVersion) list = []

    /// Version a family's exported descriptors. Generic over any
    /// exported rule id: a rule with no override seeds at 1.0.0, so a
    /// newly-shipped rule is versioned the day it lands with no
    /// bookkeeping, and a fourth rule family joins by calling this.
    let seed
        (versionOverrides: (string * RuleVersion) list)
        (family: string)
        (descriptors: CompositionRuleDescriptor list)
        : VersionedCompositionRule list =
        let lookup = Map.ofList versionOverrides

        descriptors
        |> List.map (fun descriptor -> {
            VersionedFamily = family
            VersionedRule = descriptor
            RuleSemVer =
                match lookup.TryFind descriptor.Code with
                | Some v -> v
                | None -> RuleVersion.initial
        })

    /// Family token for the [Phase 281] / [Phase 294] composition
    /// well-formedness rules.
    [<Literal>]
    let CompositionFamily = "composition-well-formedness"

    /// Family token for the [Phase 431] event-topology rules.
    [<Literal>]
    let EventTopologyFamily = "event-topology"

    /// Family token for the [Phase 433] data-footprint / DSR-coverage
    /// rules.
    [<Literal>]
    let DataFootprintFamily = "data-footprint"

    /// Family token for the [Phase 434] scale-readiness rule.
    [<Literal>]
    let ScaleReadinessFamily = "scale-readiness"

    /// Family token for the [Phase 488] appliance boot-posture rule.
    [<Literal>]
    let ApplianceBootPostureFamily = "appliance-boot-posture"

    /// The versioned `CompositionValidator` rules, projected from the
    /// same `ruleManifest` the runtime check reads.
    let compositionRules: VersionedCompositionRule list =
        seed overrides CompositionFamily CompositionValidator.ruleManifest

    /// The versioned `EventTopologyPreflight` rules.
    let eventTopologyRules: VersionedCompositionRule list =
        seed overrides EventTopologyFamily EventTopologyPreflight.ruleManifest

    /// The versioned `DataFootprintPreflight` rules.
    let dataFootprintRules: VersionedCompositionRule list =
        seed overrides DataFootprintFamily DataFootprintPreflight.ruleManifest

    /// The versioned `ScaleReadinessPreflight` rules ([Phase 434]).
    let scaleReadinessRules: VersionedCompositionRule list =
        seed overrides ScaleReadinessFamily ScaleReadinessPreflight.ruleManifest

    /// The versioned `ApplianceBootPosture` rules ([Phase 488]).
    let applianceBootPostureRules: VersionedCompositionRule list =
        seed overrides ApplianceBootPostureFamily ApplianceBootPosture.ruleManifest

    /// Every versioned rule this build ships, in family order. The
    /// published manifest an external checker reads.
    ///
    /// **The list is explicit, and the guard against forgetting a family
    /// is a TEST rather than reflection here.** Deriving the family set at
    /// runtime would buy "a new family cannot be forgotten" at the cost of
    /// the two properties this manifest exists to have — a deterministic
    /// family ORDER (the wire document and its golden baseline are
    /// ordered) and a stable family NAME (a module name is not one). So
    /// the shipped projection stays declared, and
    /// `RuleVersioningTests`' reflection sweep over every
    /// `ruleManifest : CompositionRuleDescriptor list` in this assembly is
    /// what makes an omission loud: that is precisely how 434's and 488's
    /// families sat unpublished from the day they shipped until this pass,
    /// with the hardcoded test asserting the hardcoded list and agreeing
    /// with itself.
    let allRules: VersionedCompositionRule list =
        compositionRules
        @ eventTopologyRules
        @ dataFootprintRules
        @ scaleReadinessRules
        @ applianceBootPostureRules

    /// The version of a rule by code, across every family. `None` for a
    /// code this build does not ship — the same honest "unknown" answer
    /// `CompositionValidator.tryRuleClass` gives a checker holding a
    /// stale rule set.
    let tryVersion (code: string) : RuleVersion option =
        allRules
        |> List.tryFind (fun r -> r.VersionedRule.Code = code)
        |> Option.map _.RuleSemVer

    /// The stamp a result evaluated under some rule set was proven
    /// under.
    let stampOf (rules: VersionedCompositionRule list) : RuleEvaluationStamp = {
        StampManifestVersion = ManifestVersion
        StampRules =
            rules
            |> List.map (fun r -> {
                StampedRuleCode = r.VersionedRule.Code
                StampedRuleSemVer = r.RuleSemVer
            })
    }

    /// The stamp covering every rule this build ships.
    let currentStamp: RuleEvaluationStamp = stampOf allRules

    /// The stamp covering exactly the `CompositionValidator` rules —
    /// what `checkStamped` records, since those are the rules it ran.
    let compositionStamp: RuleEvaluationStamp = stampOf compositionRules

    /// `CompositionValidator.checkWith`, stamped: the same defects, plus
    /// the record of which rule versions produced them. The stamped form
    /// is what a consumer persists when it wants the conclusion to
    /// remain interpretable after the rules move.
    let checkStamped (refs: CompositionReferences) (manifest: CompositionManifest) : StampedCompositionResult = {
        StampedDefects = CompositionValidator.checkWith refs manifest
        StampedUnder = compositionStamp
    }

    /// `CompositionValidator.checkClassWith`, stamped — the class-
    /// restricted counterpart, stamped with the rules of that class
    /// alone so the record says what was actually evaluated (an
    /// emergency boot that skipped the external-probe class must not
    /// stamp as though it ran them).
    let checkClassStamped
        (ruleClass: CompositionRuleClass)
        (refs: CompositionReferences)
        (manifest: CompositionManifest)
        : StampedCompositionResult =
        let classRules =
            compositionRules
            |> List.filter (fun r -> CompositionValidator.tryRuleClass r.VersionedRule.Code = Some ruleClass)

        {
            StampedDefects = CompositionValidator.checkClassWith ruleClass refs manifest
            StampedUnder = stampOf classRules
        }

    /// Stamp a [Phase 9m] preflight run. `IPreflightSnapshot.LastRun` is
    /// a shipped surface and stays exactly as it is; this pairs it with
    /// the rule versions in force, so an export of the snapshot records
    /// what the run was proven under.
    let stampRun (outcomes: ValidatorOutcome list) : StampedPreflightRun = {
        RunOutcomes = outcomes
        RunStamp = currentStamp
    }

    let private severityToken (severity: CompositionDefectSeverity) : string =
        match severity with
        | DefectError -> "error"
        | DefectWarning -> "warning"

    /// The published manifest in its wire form — the projection an
    /// external checker consumes and the [Phase 287] golden-file gate
    /// pins, so a rules-only change (a new rule, a tightened one, a
    /// reworded message) surfaces as a reviewed diff.
    let toWire (rules: VersionedCompositionRule list) : RuleVersionWireEntry list =
        rules
        |> List.map (fun r -> {
            Family = r.VersionedFamily
            Rule = r.VersionedRule.Code
            Version = RuleVersion.format r.RuleSemVer
            Severity = severityToken r.VersionedRule.Severity
            RuleDescription = r.VersionedRule.Description
        })

    /// The whole published manifest as one document — what forge
    /// publishes beside the errata file, and what the golden-file gate
    /// pins.
    let toWireDocument (rules: VersionedCompositionRule list) : RuleManifestWireDocument = {
        ManifestVersion = RuleVersion.format ManifestVersion
        Rules = toWire rules
    }

    /// Which stamped rules have moved since a result was stamped, and
    /// how — the checker-side reading of the bump discipline. A
    /// `MinorBump` or `MajorBump` entry means the prior *pass* is no
    /// longer evidence; a rule this build no longer ships yields `None`
    /// for its current version and is reported as unknown by the caller
    /// (`tryVersion`), never silently treated as unchanged.
    let driftSince (stamp: RuleEvaluationStamp) : (StampedRuleVersion * RuleVersionBump) list = [
        for stamped in stamp.StampRules do
            match tryVersion stamped.StampedRuleCode with
            | Some current ->
                match RuleVersion.bumpBetween stamped.StampedRuleSemVer current with
                | Some bump -> stamped, bump
                | None -> ()
            | None -> ()
    ]