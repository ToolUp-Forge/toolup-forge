// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.GroundingEnvelopeSealTests

open System
open Expecto
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Grounding
open ToolUp.Facts

// ─── Phase 684 — the grounding envelope, sealed past boot ────────────
//
// One claim carries this phase, and it is only worth anything if it is
// probed in BOTH directions:
//
//   * A recorded mutation chain VERIFIES — the seal plus the chain
//     accounts for the live envelope, and the door lets the mutation
//     through.
//   * An unrecorded one DOES NOT — the continuity walk names the
//     position, and the next mutation through the door is refused
//     because the chain can no longer prove the state.
//
// A check that only ever passed would satisfy the first perfectly and be
// worthless; a door that refused everything would satisfy the second.
// Every probe below therefore has a twin, and the two profiles are
// probed separately because `Standard` records exactly what `Verified`
// refuses and a summary line cannot tell them apart.

// ─── Doubles ─────────────────────────────────────────────────────────

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add((scopeId, audit)) }

        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

// ─── Fixtures ────────────────────────────────────────────────────────

let private metric id canonical : MetricDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Unit = "count"
    Dimensionality = "count"
    Direction = HigherIsBetter
    DisplayFormat = "N0"
    Staleness = UntilSuperseded
    ProducingOperation = None
    CanonicalMethod = canonical
    RecomputePolicy = None
    RollUp = None
}

let private subject id : SubjectDefinition = {
    Id = id
    Name = id.ToUpperInvariant()
    Levels = [ "root" ]
    Calendar = None
}

let private purpose id version surfaces : RegisteredPurpose = {
    PurposeId = id
    Description = id
    TaxonomyVersion = version
    AllowedSurfaces = surfaces
}

/// A composition declaring metrics (one with a canonical method), a
/// subject hierarchy, and a purpose regime — every enumerated facet at
/// once, so a projection that drops one is visible.
let private groundedApp (canonical: string option) =
    let sales =
        ServerModule.create "sales"
        |> ServerModule.declareMetrics [ metric "revenue" canonical; metric "margin" None ]
        |> ServerModule.declareSubjects [ subject "product" ]

    {
        ServerApp.empty with
            Config = {
                ServerConfig.defaults with
                    FactStore = EnabledFactStore
            }
    }
    |> ServerApp.addModules [ sales ]
    |> ServerApp.withRegisteredPurposes [ purpose "reporting" "v1" [ "narrative"; "tool-result" ] ]

let private envelopeOf (app: ServerApp) =
    GroundingEnvelope.ofComposition (ServerApp.compositionManifest app) app.RegisteredMetrics

/// The mutation a caller builds when it wants to move one declaration:
/// the current envelope's digest as the baseline, and the envelope it
/// wants to arrive at.
let private request
    (mutator: IGroundingEnvelopeMutator)
    (facet: GroundingFacet)
    (subject: string)
    (proposed: GroundingEnvelope)
    : GroundingMutationRequest =
    {
        Facet = facet
        Subject = subject
        Baseline = GroundingEnvelope.digest mutator.Current
        Proposed = proposed
        Principal = "ops@example.test"
        Reason = "probe"
    }

/// Add one declaration to an envelope.
let private plus (facet: GroundingFacet) (subject: string) (value: string) (envelope: GroundingEnvelope) = {
    envelope with
        Declarations =
            envelope.Declarations
            @ [
                {
                    Facet = facet
                    Subject = subject
                    Value = value
                }
            ]
}

/// Replace a declaration's value in place — the canonical-method flip.
let private flip (facet: GroundingFacet) (subject: string) (value: string) (envelope: GroundingEnvelope) = {
    envelope with
        Declarations =
            envelope.Declarations
            |> List.map (fun d ->
                if d.Facet = facet && d.Subject = subject then
                    { d with Value = value }
                else
                    d)
}

let private refusalCodes (refusals: GroundingMutationRefusal list) =
    refusals |> List.map GroundingMutationRefusal.code |> List.sort

let private mutations (audit: RecordingAuditLog) = [
    for _, event in audit.Events do
        match event with
        | GroundingEnvelopeMutated payload -> payload
        | _ -> ()
]

let private refusals (audit: RecordingAuditLog) = [
    for _, event in audit.Events do
        match event with
        | GroundingMutationRefused payload -> payload
        | _ -> ()
]

let tests =
    testList "Grounding envelope sealed past boot (Phase 684)" [

        // ─── The enumeration ──────────────────────────────────────────

        test "the enumerated mutation surface is the five declared facets" {
            // A guard, not a tautology. The whole seal rests on the union
            // being closed: a sixth grounding-relevant declaration that
            // does not join it is one the digest silently does not cover,
            // and a digest that still verifies over a hole is worse than
            // one that visibly does not. If this fails because a facet was
            // added, the change is not done — join it to
            // `GroundingEnvelope.ofManifest` / `ofComposition` and to the
            // enumeration in docs/migrations/grounding-envelope-seal.md,
            // then move the number here.
            Expect.equal
                (GroundingFacet.all |> List.map GroundingFacet.label |> List.sort)
                [
                    "canonical-method"
                    "disclosure-policy"
                    "metric-registration"
                    "purpose-declaration"
                    "subject-registration"
                ]
                "the enumerated grounding-relevant mutation surface"

            Expect.equal
                (GroundingFacet.all |> List.distinct |> List.length)
                (List.length GroundingFacet.all)
                "no facet enumerated twice"
        }

        // ─── Projection ───────────────────────────────────────────────

        test "the envelope projects every enumerated facet from a real composition" {
            let envelope = envelopeOf (groundedApp (Some "computed:rollup"))

            let byFacet =
                envelope.Declarations
                |> List.map (fun d -> GroundingFacet.label d.Facet, d.Subject, d.Value)
                |> List.sort

            Expect.isTrue
                (byFacet |> List.exists (fun (f, _, _) -> f = "metric-registration"))
                "registered metrics are declared"

            Expect.isTrue
                (byFacet |> List.exists (fun (f, _, _) -> f = "subject-registration"))
                "registered subjects are declared"

            Expect.isTrue
                (byFacet |> List.exists (fun (f, _, _) -> f = "purpose-declaration"))
                "declared purposes are declared"

            Expect.isTrue
                (byFacet |> List.exists (fun (f, _, _) -> f = "disclosure-policy"))
                "per-surface allowed purpose sets are declared"

            Expect.isTrue
                (byFacet
                 |> List.exists (fun (f, s, v) -> f = "canonical-method" && s = "revenue" && v = "computed:rollup"))
                "the canonical-method selector is declared, keyed by metric id"

            // Phase 694 — the facet the manifest could not see, and now
            // does. This assertion was the exact inverse until 694: the
            // manifest recorded a metric as its id alone, so a flip was
            // invisible to Phase 657's binding and only this envelope saw
            // it. The manifest now records the selector under a versioned
            // schema, and `ofManifest` reads it back rather than a second
            // code path deriving it from the registry.
            let declaringApp = groundedApp (Some "computed:rollup")
            let manifest = ServerApp.compositionManifest declaringApp

            Expect.equal
                (GroundingEnvelope.ofManifest manifest)
                (GroundingEnvelope.ofComposition manifest declaringApp.RegisteredMetrics)
                "ONE derivation: the envelope read from the manifest and the envelope derived beside it are the same value by construction, not two paths that happen to agree"

            Expect.isTrue
                (GroundingEnvelope.ofManifest manifest
                 |> _.Declarations
                 |> List.exists (fun d -> d.Facet = CanonicalMethodFacet && d.Subject = "revenue"))
                "the manifest alone now carries the canonical-method facet"

            // The probe that proves the agreement is not vacuous: a
            // manifest too old to record selectors still contributes none,
            // which is the pre-694 behaviour of `ofManifest` unchanged.
            let legacyManifest = {
                manifest with
                    SchemaVersion = 0
                    CanonicalMethods = Unchecked.defaultof<MetricCanonicalMethod list>
            }

            Expect.isFalse
                (GroundingEnvelope.ofManifest legacyManifest
                 |> _.Declarations
                 |> List.exists (fun d -> d.Facet = CanonicalMethodFacet))
                "a manifest predating canonical-method recording declares none, and does not fault on the null list"
        }

        test "Phase 694 — a canonical-method flip moves the envelope AND the boot comparison, naming the same metric" {
            // The agreement point, probed as one fact rather than two.
            // Before 694 the left-hand side moved and the right-hand side
            // did not: the envelope digest changed across a flip while
            // Phase 657's binding verified perfectly, so the deployment
            // held two pieces of evidence that disagreed about whether
            // anything had happened.
            let before = groundedApp (Some "computed:rollup:1")
            let after = groundedApp (Some "computed:rollup:2")

            let beforeManifest = ServerApp.compositionManifest before
            let afterManifest = ServerApp.compositionManifest after

            Expect.notEqual
                (GroundingEnvelope.digest (envelopeOf before))
                (GroundingEnvelope.digest (envelopeOf after))
                "the envelope sees the flip (this held before Phase 694 too)"

            let drift = BootVerificationPreflight.compare beforeManifest afterManifest
            let rendered = drift |> List.map CompositionDrift.describe |> String.concat " | "

            Expect.equal
                drift.Length
                1
                "exactly one difference — the selector, nothing else about the composition moved"

            Expect.stringContains rendered "revenue" "the finding names the metric"
            Expect.stringContains rendered "computed:rollup:1" "and the recorded selector"
            Expect.stringContains rendered "computed:rollup:2" "and the observed one"

            // The control: the same two compositions with no flip between
            // them must produce neither signal, or the assertions above
            // would pass for a comparison that reported everything.
            Expect.equal
                (GroundingEnvelope.digest (envelopeOf before))
                (GroundingEnvelope.digest (envelopeOf (groundedApp (Some "computed:rollup:1"))))
                "an unflipped pair digests identically"

            Expect.isEmpty
                (BootVerificationPreflight.compare
                    beforeManifest
                    (ServerApp.compositionManifest (groundedApp (Some "computed:rollup:1"))))
                "and reports no drift"
        }

        test "a grounding-free composition seals to the empty envelope (GP 11)" {
            let bare = ServerApp.empty
            let envelope = envelopeOf bare

            Expect.isEmpty envelope.Declarations "nothing declared, nothing sealed"

            Expect.equal
                (GroundingEnvelope.digest envelope)
                (GroundingEnvelope.digest GroundingEnvelope.empty)
                "a pre-526 composition digests to the empty envelope"
        }

        test "an undeclared canonical method contributes nothing, and declaring one is an addition" {
            let undeclared = envelopeOf (groundedApp None)
            let declared = envelopeOf (groundedApp (Some "computed:rollup"))

            Expect.isFalse
                (undeclared.Declarations |> List.exists (fun d -> d.Facet = CanonicalMethodFacet))
                "no synthetic default is invented for an undeclared metric"

            Expect.notEqual
                (GroundingEnvelope.digest undeclared)
                (GroundingEnvelope.digest declared)
                "declaring a canonical method moves the envelope"

            Expect.equal
                (GroundingEnvelope.diff undeclared declared)
                [ "declared but not recorded: canonical-method 'revenue' = 'computed:rollup'" ]
                "and reads as an addition, which is what it is"
        }

        test "the digest is a function of the declarations, not of their order or multiplicity" {
            let envelope = envelopeOf (groundedApp (Some "computed:rollup"))

            let shuffled = {
                envelope with
                    Declarations = List.rev envelope.Declarations @ envelope.Declarations
            }

            Expect.equal
                (GroundingEnvelope.digest shuffled)
                (GroundingEnvelope.digest envelope)
                "reordering and duplicating declarations does not move the digest"
        }

        test "the canonical form is injective across a field boundary" {
            // Without length framing these two canonicalise to the same
            // concatenation, and a digest over them would be meaningless.
            let a =
                GroundingEnvelope.empty
                |> plus CanonicalMethodFacet "ab" "c"
                |> GroundingEnvelope.digest

            let b =
                GroundingEnvelope.empty
                |> plus CanonicalMethodFacet "a" "bc"
                |> GroundingEnvelope.digest

            Expect.notEqual a b "framed fields cannot collide by concatenation"
        }

        // ─── Direction A — a recorded chain verifies ──────────────────

        testAsync "a chain of recorded mutations verifies, and each row carries both digests" {
            let audit = RecordingAuditLog()
            let sealedEnvelope = envelopeOf (groundedApp None)

            let mutator =
                GroundingEnvelopeMutator.forImmutableComposition
                    CompositionProfile.Verified
                    audit
                    GroundingEnvelopeMutator.PlatformScopeId
                    sealedEnvelope

            let sealDigest = mutator.Seal

            // 1. a canonical-method flip — D19's "audited registry op".
            let afterFlip =
                sealedEnvelope |> plus CanonicalMethodFacet "revenue" "computed:rollup"

            let! first = mutator.Apply(request mutator CanonicalMethodFacet "revenue" afterFlip)

            // 2. a metric registration.
            let afterRegister = afterFlip |> plus MetricRegistrationFacet "metric:churn" "churn"

            let! second = mutator.Apply(request mutator MetricRegistrationFacet "metric:churn" afterRegister)

            // 3. a disclosure-policy change.
            let afterPolicy = afterRegister |> flip DisclosurePolicyFacet "narrative" ""

            let! third = mutator.Apply(request mutator DisclosurePolicyFacet "narrative" afterPolicy)

            Expect.isOk first "the canonical flip lands"
            Expect.isOk second "the registration lands"
            Expect.isOk third "the policy change lands"

            match mutator.Continuity() with
            | GroundingContinuityVerdict.Continuous(steps, digest) ->
                Expect.equal steps 3 "three mutations walked"

                Expect.equal digest (GroundingEnvelope.digest afterPolicy) "the walk arrives at the live envelope"
            | GroundingContinuityVerdict.Diverged d ->
                failtestf "expected continuity, diverged: %s" (GroundingDivergence.describe d)

            // The chain is walkable because each row carries BOTH digests.
            let rows = mutations audit
            Expect.hasLength rows 3 "one audit row per landed mutation"

            Expect.equal (rows |> List.map _.Sequence) [ 1; 2; 3 ] "sequence counts from 1"

            Expect.equal (List.head rows).BeforeDigest sealDigest "the first row starts from the boot seal"

            Expect.equal
                (rows |> List.map _.BeforeDigest |> List.tail)
                (rows |> List.map _.AfterDigest |> List.truncate 2)
                "each row starts where its predecessor ended"

            Expect.equal
                (rows |> List.last).AfterDigest
                (GroundingEnvelope.digest afterPolicy)
                "the last row ends at the live envelope"

            Expect.equal
                (rows |> List.map _.Facet)
                [ "canonical-method"; "metric-registration"; "disclosure-policy" ]
                "each row names the facet that moved"

            Expect.isEmpty (rows |> List.collect _.Observations) "a clean in-path mutation records no observations"

            Expect.isEmpty (refusals audit) "and nothing was refused"
        }

        testAsync "an empty chain verifies against the sealed envelope" {
            let audit = RecordingAuditLog()
            let sealedEnvelope = envelopeOf (groundedApp (Some "computed:rollup"))

            let mutator =
                GroundingEnvelopeMutator.forImmutableComposition
                    CompositionProfile.Verified
                    audit
                    GroundingEnvelopeMutator.PlatformScopeId
                    sealedEnvelope

            match mutator.Continuity() with
            | GroundingContinuityVerdict.Continuous(steps, digest) ->
                Expect.equal steps 0 "nothing has moved"
                Expect.equal digest mutator.Seal "and the seal is still the head"
            | GroundingContinuityVerdict.Diverged d ->
                failtestf "expected continuity, diverged: %s" (GroundingDivergence.describe d)
        }

        // ─── Direction B — an unrecorded mutation does not ────────────

        test "an envelope that drifted without a record fails continuity, at a named position" {
            let sealedEnvelope = envelopeOf (groundedApp None)

            // Nobody came through the door; the declarations simply moved.
            let drifted =
                sealedEnvelope |> plus CanonicalMethodFacet "revenue" "computed:rollup"

            match GroundingContinuity.verifyAgainst sealedEnvelope [] drifted with
            | GroundingContinuityVerdict.Diverged(HeadMismatch(position, expected, observed, differences)) ->
                Expect.equal position 1 "reported one past the last recorded step"

                Expect.equal expected (GroundingEnvelope.digest sealedEnvelope) "the chain accounts only for the seal"

                Expect.equal observed (GroundingEnvelope.digest drifted) "and the live envelope is elsewhere"

                Expect.equal
                    differences
                    [ "declared but not recorded: canonical-method 'revenue' = 'computed:rollup'" ]
                    "naming exactly what moved"
            | verdict -> failtestf "expected a head mismatch, got: %s" (GroundingContinuityVerdict.describe verdict)
        }

        testAsync "under the verified profile the next mutation after an out-of-path change is refused" {
            let audit = RecordingAuditLog()
            let sealedEnvelope = envelopeOf (groundedApp None)

            // A deployment holding MUTABLE grounding state: `observe`
            // reads it, so a change made behind the door's back is
            // visible to the door.
            let mutable live = sealedEnvelope

            let mutator =
                GroundingEnvelopeMutator.create
                    CompositionProfile.Verified
                    audit
                    GroundingEnvelopeMutator.PlatformScopeId
                    sealedEnvelope
                    (fun () -> live)

            // Out of path: the canonical method is flipped directly.
            live <- sealedEnvelope |> plus CanonicalMethodFacet "revenue" "computed:rollup"

            match mutator.Continuity() with
            | GroundingContinuityVerdict.Diverged(HeadMismatch(position, _, _, differences)) ->
                Expect.equal position 1 "the divergence is reported by position"
                Expect.isNonEmpty differences "and names the declaration that moved"
            | verdict -> failtestf "expected a head mismatch, got: %s" (GroundingContinuityVerdict.describe verdict)

            // A later, entirely well-formed mutation is now refused: the
            // chain can no longer prove the state it would extend.
            let proposed = live |> plus MetricRegistrationFacet "metric:churn" "churn"

            let! outcome =
                mutator.Apply {
                    Facet = MetricRegistrationFacet
                    Subject = "metric:churn"
                    Baseline = GroundingEnvelope.digest mutator.Current
                    Proposed = proposed
                    Principal = "ops@example.test"
                    Reason = "register churn"
                }

            match outcome with
            | Ok record -> failtestf "expected a refusal, the mutation landed at sequence %d" record.Sequence
            | Error found -> Expect.contains (refusalCodes found) "out-of-path-drift" "the out-of-path refusal fires"

            Expect.isEmpty (mutations audit) "nothing was appended to the chain"
            Expect.hasLength (refusals audit) 1 "and the refusal is audited"

            let refusal = (refusals audit) |> List.head

            Expect.equal refusal.Profile "verified" "recorded under the profile that refused"

            Expect.equal refusal.ChainedDigest (GroundingEnvelope.digest sealedEnvelope) "naming what the chain proves"

            Expect.equal refusal.ObservedDigest (GroundingEnvelope.digest live) "and what was actually observed"

            Expect.isNonEmpty refusal.Reasons "with a readable reason"
            Expect.hasLength mutator.Chain 0 "the chain is unextended"
        }

        // ─── Divergence is reported at the FIRST position ─────────────

        test "a chain that does not start from the seal is reported at position 0" {
            let sealedEnvelope = envelopeOf (groundedApp None)

            let elsewhere =
                sealedEnvelope |> plus MetricRegistrationFacet "metric:churn" "churn"

            let orphan = {
                Sequence = 1
                Facet = MetricRegistrationFacet
                Subject = "metric:churn"
                Before = GroundingEnvelope.digest elsewhere
                After = GroundingEnvelope.digest elsewhere
                Principal = "ops@example.test"
                Reason = "probe"
                Observations = []
                OccurredAt = DateTimeOffset.UnixEpoch
            }

            match GroundingContinuity.verify (GroundingEnvelope.digest sealedEnvelope) [ orphan ] elsewhere with
            | GroundingContinuityVerdict.Diverged(SealMismatch(expected, observed) as divergence) ->
                Expect.equal (GroundingDivergence.position divergence) 0 "the earliest possible break"

                Expect.equal expected (GroundingEnvelope.digest sealedEnvelope) "expected the seal"
                Expect.equal observed (GroundingEnvelope.digest elsewhere) "found something else"
            | verdict -> failtestf "expected a seal mismatch, got: %s" (GroundingContinuityVerdict.describe verdict)
        }

        test "a broken link is reported at its position, and only the first break is reported" {
            let a = GroundingEnvelope.empty
            let b = a |> plus MetricRegistrationFacet "m1" "m1"
            let c = b |> plus MetricRegistrationFacet "m2" "m2"
            let d = c |> plus MetricRegistrationFacet "m3" "m3"

            let step seq' before after = {
                Sequence = seq'
                Facet = MetricRegistrationFacet
                Subject = $"m{seq'}"
                Before = GroundingEnvelope.digest before
                After = GroundingEnvelope.digest after
                Principal = "ops@example.test"
                Reason = "probe"
                Observations = []
                OccurredAt = DateTimeOffset.UnixEpoch
            }

            // Step 2 starts from `c` when step 1 ended at `b`, and step 3
            // is broken too. The first break is the one that matters —
            // everything after it compares states nobody can vouch for.
            let chain = [ step 1 a b; step 2 c d; step 3 a d ]

            match GroundingContinuity.verify (GroundingEnvelope.digest a) chain d with
            | GroundingContinuityVerdict.Diverged(ChainBreak(position, expected, observed) as divergence) ->
                Expect.equal position 2 "the second mutation broke the link"
                Expect.equal (GroundingDivergence.position divergence) 2 "and reports that position"

                Expect.equal expected (GroundingEnvelope.digest b) "its predecessor ended at b"
                Expect.equal observed (GroundingEnvelope.digest c) "and it claims to start from c"
            | verdict -> failtestf "expected a chain break, got: %s" (GroundingContinuityVerdict.describe verdict)
        }

        // ─── The other refusals ───────────────────────────────────────

        testAsync "a mutation computed against a superseded envelope is refused under the verified profile" {
            let audit = RecordingAuditLog()
            let sealedEnvelope = envelopeOf (groundedApp None)

            let mutator =
                GroundingEnvelopeMutator.forImmutableComposition
                    CompositionProfile.Verified
                    audit
                    GroundingEnvelopeMutator.PlatformScopeId
                    sealedEnvelope

            let first = sealedEnvelope |> plus MetricRegistrationFacet "metric:churn" "churn"
            let! _ = mutator.Apply(request mutator MetricRegistrationFacet "metric:churn" first)

            // Built against the seal, presented after the envelope moved.
            let! outcome =
                mutator.Apply {
                    Facet = CanonicalMethodFacet
                    Subject = "revenue"
                    Baseline = GroundingEnvelope.digest sealedEnvelope
                    Proposed = first |> plus CanonicalMethodFacet "revenue" "computed:rollup"
                    Principal = "ops@example.test"
                    Reason = "flip"
                }

            match outcome with
            | Ok _ -> failtest "a lost compare-and-swap must not be silently rebased"
            | Error found -> Expect.equal (refusalCodes found) [ "stale-baseline" ] "refused for exactly that reason"

            Expect.hasLength mutator.Chain 1 "the chain stayed where it was"
        }

        testAsync "a mutation naming a declaration that did not move is refused" {
            let audit = RecordingAuditLog()
            let sealedEnvelope = envelopeOf (groundedApp None)

            let mutator =
                GroundingEnvelopeMutator.forImmutableComposition
                    CompositionProfile.Verified
                    audit
                    GroundingEnvelopeMutator.PlatformScopeId
                    sealedEnvelope

            let proposed = sealedEnvelope |> plus MetricRegistrationFacet "metric:churn" "churn"

            let! outcome = mutator.Apply(request mutator DisclosurePolicyFacet "narrative" proposed)

            match outcome with
            | Ok _ -> failtest "a chain annotated with a subject that did not move is fiction"
            | Error found -> Expect.equal (refusalCodes found) [ "subject-mismatch" ] "refused for exactly that reason"
        }

        testAsync "a mutation that moves nothing is refused under BOTH profiles" {
            for profile in [ CompositionProfile.Standard; CompositionProfile.Verified ] do
                let audit = RecordingAuditLog()
                let sealedEnvelope = envelopeOf (groundedApp None)

                let mutator =
                    GroundingEnvelopeMutator.forImmutableComposition
                        profile
                        audit
                        GroundingEnvelopeMutator.PlatformScopeId
                        sealedEnvelope

                let! outcome = mutator.Apply(request mutator MetricRegistrationFacet "metric:revenue" sealedEnvelope)

                match outcome with
                | Ok _ ->
                    failtestf "under %s a no-op mutation must not lengthen the chain" (CompositionProfile.label profile)
                | Error found ->
                    Expect.equal (refusalCodes found) [ "moves-nothing" ] "a malformed request, not a policy question"
        }

        // ─── Outside the profile, nothing changes (GP 13) ─────────────

        testAsync "under the standard profile the same findings are recorded and the mutation lands" {
            let audit = RecordingAuditLog()
            let sealedEnvelope = envelopeOf (groundedApp None)
            let mutable live = sealedEnvelope

            let mutator =
                GroundingEnvelopeMutator.create
                    CompositionProfile.Standard
                    audit
                    GroundingEnvelopeMutator.PlatformScopeId
                    sealedEnvelope
                    (fun () -> live)

            // The same out-of-path drift the verified probe refused on.
            live <- sealedEnvelope |> plus CanonicalMethodFacet "revenue" "computed:rollup"

            let proposed = live |> plus MetricRegistrationFacet "metric:churn" "churn"

            let! outcome =
                mutator.Apply {
                    Facet = MetricRegistrationFacet
                    Subject = "metric:churn"
                    Baseline = GroundingEnvelope.digest mutator.Current
                    Proposed = proposed
                    Principal = "ops@example.test"
                    Reason = "register churn"
                }

            Expect.isOk outcome "the standard profile refuses nothing"
            Expect.isEmpty (refusals audit) "and audits no refusal"

            let row = (mutations audit) |> List.head
            Expect.equal row.Profile "standard" "recorded under the standard profile"

            Expect.isNonEmpty
                row.Observations
                "with the finding that WOULD have refused it recorded — the log-and-serve rung of the ladder"

            Expect.isTrue
                (row.Observations |> List.exists _.Contains("outside the audited path"))
                "naming the out-of-path drift"
        }

        // ─── Compose ──────────────────────────────────────────────────

        test "the seal composes over the fact store, and a NoFactStore deployment is unchanged" {
            let bare =
                FactsCompose.withGroundingEnvelopeSeal CompositionProfile.Verified None ServerApp.empty

            Expect.isNone bare.Extensions.ServiceConfig "a NoFactStore deployment composes nothing (GP 11/13)"

            let composed =
                FactsCompose.withGroundingEnvelopeSeal CompositionProfile.Verified None (groundedApp None)

            let services = ServiceCollection()
            services.AddSingleton<IAuditLog>(RecordingAuditLog()) |> ignore

            match composed.Extensions.ServiceConfig with
            | None -> failtest "the composed deployment registered nothing"
            | Some configure ->
                configure services |> ignore
                let provider = services.BuildServiceProvider()
                let mutator = provider.GetRequiredService<IGroundingEnvelopeMutator>()

                Expect.equal
                    mutator.Seal
                    (GroundingEnvelope.digest (envelopeOf (groundedApp None)))
                    "sealed to the grounding envelope this app declares"

                Expect.isTrue
                    (GroundingContinuityVerdict.isContinuous (mutator.Continuity()))
                    "a composition with no mutable grounding state has nothing that could drift"
        }
    ]