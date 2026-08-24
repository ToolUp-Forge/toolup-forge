// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Phase 684 — the grounding envelope, sealed past boot ────────────
//
// Phase 657's migration doc names its own gap at equal length with its
// guarantees: the boot preflight "proves nothing about post-boot
// mutation", and "this profile does not freeze the composition to close
// that gap". For most of the composition that bound is simply the honest
// one. For the **declared grounding envelope** it is live, because that
// envelope is exactly what a later answer's provenance is judged
// against: which quantities are registered, which method a method-less
// query canonically resolves to, and which purposes may disclose at which
// egress surface. A deployment that verified at boot and then flipped a
// canonical method has a chain of evidence pointing at declarations that
// no longer hold, and nothing in the trail says so.
//
// This file closes that gap **the op-stream way rather than the freeze
// way**. Grounding-relevant mutation stays possible; it stops being
// invisible. Each mutation becomes a typed, audited operation carrying
// the before/after envelope digest, and the envelope is re-derivable at
// any moment such that
//
//     boot seal  +  recorded mutation chain  ⇒  current envelope
//
// verifies as a computation an auditor performs from the trail alone.
// Under the verified composition profile a mutation arriving OUTSIDE the
// door is refused with the Phase 657 refusal shape. Outside the profile
// nothing changes (GP 13): a deployment that never composes a mutator is
// byte-for-byte what it was, and one that composes it under
// `CompositionProfile.Standard` records what it would have refused
// instead of refusing — the same `LogAndServe` → `RefuseOnDrift` adoption
// ladder Phase 657 offers, for the same reason.
//
// ── The enumerated mutation surface ─────────────────────────────────
//
// `GroundingFacet` below IS the enumeration, and it is deliberately a
// closed union rather than a string: a facet that is not in it is not
// covered by any seal this file mints, and the compiler is the thing that
// says so when someone adds one. The five facets:
//
//   1. **metric registration** — a registered grounding metric appearing
//      or disappearing (Phase 519 / 526).
//   2. **subject registration** — the same for a subject hierarchy.
//   3. **purpose declaration** — a composition-declared disclosure
//      purpose and the taxonomy version it belongs to (Phase 592).
//   4. **canonical-method selection** — which method identity a
//      method-less query resolves to (Phase 566 / D19). D19 already
//      called a canonical flip "an audited registry op, never an implicit
//      effect"; this is the sentence made enforceable.
//   5. **disclosure policy** — the per-egress-surface allowed purpose
//      sets (Phase 592, carried as the manifest's
//      `DisclosurePurposes.<Surface>` knobs).
//
// **A sixth grounding-relevant declaration must join this union, the
// projection below, and the migration doc's enumeration — in the same
// change that introduces it.** Anything else is a declaration the seal
// silently does not cover, which is worse than one it visibly does not,
// because the digest still verifies.
//
// ── What this does NOT prove ────────────────────────────────────────
//
// Written here rather than left to be inferred, at the same level of
// care as Phase 657's own bound, and repeated verbatim in the migration
// doc.
//
//   * **The observation is only as live as the function supplied.** The
//     continuity check compares the chain against whatever `observe`
//     returns. A deployment whose grounding declarations are genuinely
//     compose-time immutable — which is every composition this SDK ships
//     — passes `observe` that re-derives from the composed value, and
//     continuity is then trivially continuous. That is a TRUE statement
//     about such a deployment, not a check that caught something. A
//     deployment holding mutable grounding state supplies a function that
//     reads it, and only then does the check have anything to catch.
//   * **It is a decision point, not a boundary.** Code that mutates
//     grounding state without going through the door is not stopped by
//     it; it is DETECTED — on the next continuity check, and on the next
//     mutation, which the door then refuses because it can no longer
//     prove the chain. Phase 300's gate carries the identical bound and
//     for the identical reason.
//   * **A recorded mutation is attributable, not correct.** The chain
//     proves who moved what and in what order. Whether the new canonical
//     method is the right one is not a question a digest can answer.
//   * **It says nothing about facets outside the enumeration**, which is
//     why the enumeration is a closed union and why adding to it is a
//     compile error rather than a documentation task.

// ─── The enumerated facets ───────────────────────────────────────────

/// One facet of the declared grounding envelope — THE enumerated
/// mutation surface this seal covers.
///
/// A closed union on purpose. A stringly-typed facet would let a caller
/// name a sixth kind of declaration and receive a perfectly verifying
/// chain over an envelope that never carried it.
type GroundingFacet =
    /// A registered grounding metric (Phase 519 / 526).
    | MetricRegistrationFacet
    /// A registered grounding subject hierarchy.
    | SubjectRegistrationFacet
    /// A composition-declared disclosure purpose (Phase 592).
    | PurposeDeclarationFacet
    /// A metric's canonical-method selector (Phase 566 — D19).
    | CanonicalMethodFacet
    /// A per-egress-surface allowed purpose set (Phase 592).
    | DisclosurePolicyFacet

[<RequireQualifiedAccess>]
module GroundingFacet =

    /// Every enumerated facet. A test asserts this list's length, so a
    /// facet added without joining the projection and the migration doc's
    /// enumeration fails loudly rather than shipping a seal with a hole
    /// in it.
    let all: GroundingFacet list = [
        MetricRegistrationFacet
        SubjectRegistrationFacet
        PurposeDeclarationFacet
        CanonicalMethodFacet
        DisclosurePolicyFacet
    ]

    /// Stable lowercase label for canonical forms, audit payloads, and
    /// operator-facing lines. Wire tokens — never localised.
    let label =
        function
        | MetricRegistrationFacet -> "metric-registration"
        | SubjectRegistrationFacet -> "subject-registration"
        | PurposeDeclarationFacet -> "purpose-declaration"
        | CanonicalMethodFacet -> "canonical-method"
        | DisclosurePolicyFacet -> "disclosure-policy"

// ─── The envelope ────────────────────────────────────────────────────

/// One declaration in the grounding envelope: which facet it belongs to,
/// what it is about, and the declared value.
type GroundingDeclaration = {
    Facet: GroundingFacet
    /// The declaration's subject — a metric id, a subject-hierarchy id, a
    /// purpose id, or an egress-surface name.
    Subject: string
    /// The declared value, rendered: the taxonomy version for a purpose,
    /// the selector for a canonical method, the allowed purpose set for a
    /// disclosure policy, the id again for a bare registration.
    Value: string
}

/// The declared grounding envelope: every enumerated declaration a
/// composition made, as one comparable, digestible value.
///
/// **Not a field on `CompositionManifest` and not derived from its
/// entries alone.** The manifest projects a registered metric as an id
/// with no `Impl`, so a canonical-method flip is invisible in it — the
/// boot binding would verify perfectly across the flip. Widening the
/// manifest to carry the selector would move every existing deployment's
/// recorded composition and drift them all on upgrade (GP 11). A separate
/// projection leaves Phase 657's binding untouched and lets this one
/// carry what it needs.
type GroundingEnvelope = {
    SchemaVersion: int
    Declarations: GroundingDeclaration list
}

[<RequireQualifiedAccess>]
module GroundingEnvelope =

    /// Schema version of `GroundingEnvelope`.
    [<Literal>]
    let SchemaVersion = 1

    /// Framing version for the envelope's canonical form. Part of the
    /// framed bytes, so an envelope canonicalised under a future scheme
    /// can never collide with one canonicalised under this.
    [<Literal>]
    let FramingVersion = "toolup.groundingenvelope.v1"

    /// Knob-name prefix the manifest carries per-surface allowed purpose
    /// sets under (Phase 592).
    [<Literal>]
    let DisclosurePurposeKnobPrefix = "DisclosurePurposes."

    /// The envelope of a composition that declared no grounding at all —
    /// what every pre-526 deployment projects to.
    let empty: GroundingEnvelope = {
        SchemaVersion = SchemaVersion
        Declarations = []
    }

    /// Sort key: facet, then subject, then value. Total and stable, so
    /// the canonical form is a function of the declarations rather than
    /// of the order they were accumulated in.
    let private declarationKey (d: GroundingDeclaration) : string * string * string =
        GroundingFacet.label d.Facet, d.Subject, d.Value

    /// Null-list coercion on the read path — an envelope that round-
    /// tripped through a serialiser predating the field deserialises it
    /// as `null`, and a null F# list faults on the first list operation,
    /// including the comparison this file exists to perform.
    let private coerce (declarations: GroundingDeclaration list) : GroundingDeclaration list =
        if isNull (box declarations) then [] else declarations

    /// The declarations in canonical order, de-duplicated.
    let canonicalDeclarations (envelope: GroundingEnvelope) : GroundingDeclaration list =
        coerce envelope.Declarations
        |> List.distinctBy declarationKey
        |> List.sortBy declarationKey

    /// Length-framed canonical text for an envelope — the exact bytes its
    /// digest is taken over.
    ///
    /// Framed with the same injective scheme Phase 656 / 657 use, for the
    /// same reason: without it two distinct envelopes could canonicalise
    /// to identical text by concatenation, and a digest over them would
    /// be meaningless.
    let canonicalForm (envelope: GroundingEnvelope) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder

        let declarations = canonicalDeclarations envelope

        frame FramingVersion
        frame (string envelope.SchemaVersion)
        frame (string declarations.Length)

        for declaration in declarations do
            frame (GroundingFacet.label declaration.Facet)
            frame declaration.Subject
            frame declaration.Value

        builder.ToString()

    /// The canonical bytes an envelope's digest covers.
    let canonicalBytes (envelope: GroundingEnvelope) : byte[] =
        envelope |> canonicalForm |> Encoding.UTF8.GetBytes

    /// Lowercase-hex SHA-256 over the canonical bytes — the value the
    /// seal names, every mutation record carries, and the continuity
    /// check compares.
    let digest (envelope: GroundingEnvelope) : string =
        envelope |> canonicalBytes |> DeployRecords.digestBytes

    // ─── Projection ──────────────────────────────────────────────────

    /// Project the manifest-visible grounding facets: registered metrics
    /// and subjects (Phase 526), declared purposes with their taxonomy
    /// version (Phase 592), and the per-surface allowed purpose sets the
    /// manifest carries as `DisclosurePurposes.<Surface>` knobs.
    ///
    /// The canonical-method facet is NOT here — the manifest does not
    /// carry it. `withCanonicalMethods` (or `ofComposition`) adds it from
    /// the registry the composition accumulated.
    let ofManifest (manifest: CompositionManifest) : GroundingEnvelope =
        let entries (kind: ComponentKind) (list: ComponentEntry list) =
            (if isNull (box list) then [] else list)
            |> List.filter (fun e -> e.Kind = kind)
            |> List.map (fun e -> e.Id.Value, e.Impl |> Option.defaultValue e.Label)

        let metricDeclarations =
            entries MetricComponent manifest.Metrics
            |> List.map (fun (id, value) -> {
                Facet = MetricRegistrationFacet
                Subject = id
                Value = value
            })

        let subjectDeclarations =
            entries SubjectComponent manifest.Subjects
            |> List.map (fun (id, value) -> {
                Facet = SubjectRegistrationFacet
                Subject = id
                Value = value
            })

        let purposeDeclarations =
            entries PurposeComponent manifest.Purposes
            |> List.map (fun (id, value) -> {
                Facet = PurposeDeclarationFacet
                Subject = id
                Value = value
            })

        let disclosureDeclarations =
            (if isNull (box manifest.ConfigKnobs) then
                 []
             else
                 manifest.ConfigKnobs)
            |> List.filter (fun k ->
                not (isNull k.Name)
                && k.Name.StartsWith(DisclosurePurposeKnobPrefix, StringComparison.Ordinal))
            |> List.map (fun k -> {
                Facet = DisclosurePolicyFacet
                Subject = k.Name.Substring DisclosurePurposeKnobPrefix.Length
                Value = k.Value
            })

        {
            SchemaVersion = SchemaVersion
            Declarations =
                metricDeclarations
                @ subjectDeclarations
                @ purposeDeclarations
                @ disclosureDeclarations
        }

    /// Add the canonical-method facet from `(metricId, selector)` pairs.
    ///
    /// An UNDECLARED canonical method contributes nothing, so a
    /// composition that declares none has an envelope byte-identical to
    /// the pre-566 one (GP 11) — and a later declaration therefore reads
    /// as an addition rather than a change from a synthetic default,
    /// which is what it is.
    let withCanonicalMethods (selectors: (string * string) list) (envelope: GroundingEnvelope) : GroundingEnvelope = {
        envelope with
            Declarations =
                coerce envelope.Declarations
                @ (selectors
                   |> List.map (fun (metricId, selector) -> {
                       Facet = CanonicalMethodFacet
                       Subject = metricId
                       Value = selector
                   }))
    }

    /// The whole declared envelope of a composition: the manifest facets
    /// plus the canonical-method selectors the accumulated metric
    /// registrations declared.
    ///
    /// A composition root calls this with its own manifest and
    /// `ServerApp.RegisteredMetrics`:
    ///
    /// ```fsharp
    /// let envelope =
    ///     GroundingEnvelope.ofComposition (ServerApp.compositionManifest app) app.RegisteredMetrics
    /// ```
    let ofComposition (manifest: CompositionManifest) (metrics: Grounding.MetricRegistration list) : GroundingEnvelope =
        let selectors =
            (if isNull (box metrics) then [] else metrics)
            |> List.choose (fun r -> r.Definition.CanonicalMethod |> Option.map (fun s -> r.Definition.Id, s))
            |> List.distinct

        ofManifest manifest |> withCanonicalMethods selectors

    // ─── Comparison ──────────────────────────────────────────────────

    /// Every way `observed` differs from `recorded`, each naming its
    /// subject.
    ///
    /// Accumulates rather than stopping at the first difference, for
    /// Phase 657's reason: a caller holding a drifted envelope wants the
    /// whole list, and a comparison that stopped early invites a second
    /// pass to discover the second difference.
    let diff (recorded: GroundingEnvelope) (observed: GroundingEnvelope) : string list =
        let key (d: GroundingDeclaration) = GroundingFacet.label d.Facet, d.Subject

        let recordedMap =
            canonicalDeclarations recorded
            |> List.map (fun d -> key d, d.Value)
            |> Map.ofList

        let observedMap =
            canonicalDeclarations observed
            |> List.map (fun d -> key d, d.Value)
            |> Map.ofList

        [
            for KeyValue((facet, subject), observedValue) in observedMap do
                match Map.tryFind (facet, subject) recordedMap with
                | None -> $"declared but not recorded: {facet} '{subject}' = '{observedValue}'"
                | Some recordedValue ->
                    if recordedValue <> observedValue then
                        $"{facet} '{subject}' is '{observedValue}', recorded as '{recordedValue}'"

            for KeyValue((facet, subject), recordedValue) in recordedMap do
                if not (Map.containsKey (facet, subject) observedMap) then
                    $"recorded but no longer declared: {facet} '{subject}' = '{recordedValue}'"
        ]

    /// The `(facet, subject)` pairs that differ between two envelopes —
    /// what a mutation record must name to be an honest description of
    /// the move it accompanies.
    let movedSubjects (before: GroundingEnvelope) (after: GroundingEnvelope) : (string * string) list =
        let key (d: GroundingDeclaration) = GroundingFacet.label d.Facet, d.Subject

        let beforeMap =
            canonicalDeclarations before |> List.map (fun d -> key d, d.Value) |> Map.ofList

        let afterMap =
            canonicalDeclarations after |> List.map (fun d -> key d, d.Value) |> Map.ofList

        let changed = [
            for KeyValue(k, v) in afterMap do
                match Map.tryFind k beforeMap with
                | Some existing when existing = v -> ()
                | _ -> k

            for KeyValue(k, _) in beforeMap do
                if not (Map.containsKey k afterMap) then
                    k
        ]

        changed |> List.distinct |> List.sort

// ─── Continuity ──────────────────────────────────────────────────────

/// One mutation of the grounding envelope, as recorded in the chain.
///
/// Identity by value throughout (GP 12 rule 1) — digests and strings,
/// never a live handle to an envelope.
type GroundingMutationRecord = {
    /// Position in the chain, counting from 1.
    Sequence: int
    Facet: GroundingFacet
    Subject: string
    /// Digest of the envelope before this mutation.
    Before: string
    /// Digest of the envelope after it.
    After: string
    Principal: string
    Reason: string
    /// Findings that would have refused this mutation under the verified
    /// profile but were recorded instead because the deployment runs
    /// `Standard`. Empty on a clean mutation.
    Observations: string list
    OccurredAt: DateTimeOffset
}

/// Where a continuity walk first stopped agreeing with the evidence.
///
/// Every case carries a **position**: 0 is the boot seal, N is the Nth
/// recorded mutation, and N+1 is the live observation past the last one.
/// "The envelope diverged" is not a finding an operator can act on;
/// "the chain's step 3 claims to start from a digest step 2 did not end
/// at" is.
type GroundingDivergence =
    /// The chain's first recorded mutation does not start from the boot
    /// seal — the earliest possible break, reported at position 0.
    | SealMismatch of expected: string * observed: string
    /// A recorded mutation does not start from where its predecessor
    /// ended. `position` is the mutation that broke the link.
    | ChainBreak of position: int * expected: string * observed: string
    /// The chain is internally sound and its head does not describe the
    /// envelope actually observed — the unrecorded mutation. `position`
    /// is one past the last recorded step, and `differences` names every
    /// declaration that moved.
    | HeadMismatch of position: int * expected: string * observed: string * differences: string list

[<RequireQualifiedAccess>]
module GroundingDivergence =

    /// The position the divergence sits at.
    let position =
        function
        | SealMismatch _ -> 0
        | ChainBreak(position, _, _) -> position
        | HeadMismatch(position, _, _, _) -> position

    /// One rendered line naming the position, both digests, and — for a
    /// head mismatch — what actually moved.
    let describe =
        function
        | SealMismatch(expected, observed) ->
            $"grounding continuity broke at position 0: the first recorded mutation starts from '{observed}', and the boot seal is '{expected}'"
        | ChainBreak(position, expected, observed) ->
            $"grounding continuity broke at position {position}: the mutation starts from '{observed}', and its predecessor ended at '{expected}'"
        | HeadMismatch(position, expected, observed, differences) ->
            let detail =
                if List.isEmpty differences then
                    ""
                else
                    " — " + String.concat "; " differences

            $"grounding continuity broke at position {position}: the recorded chain ends at '{expected}' and the live envelope digests to '{observed}'{detail}"

/// What a continuity walk concluded.
[<RequireQualifiedAccess>]
type GroundingContinuityVerdict =
    /// `boot seal + recorded mutation chain ⇒ current envelope` holds.
    /// Carries how many mutations were walked and the digest they arrive
    /// at, so a caller can quote the proof rather than the conclusion.
    | Continuous of steps: int * digest: string
    /// The FIRST position at which it stopped holding. First rather than
    /// all: a break at step 2 makes every later comparison meaningless,
    /// and reporting the cascade buries the one finding that matters.
    | Diverged of GroundingDivergence

[<RequireQualifiedAccess>]
module GroundingContinuityVerdict =

    /// Stable lowercase label for logs and dashboards.
    let label =
        function
        | GroundingContinuityVerdict.Continuous _ -> "continuous"
        | GroundingContinuityVerdict.Diverged _ -> "diverged"

    /// `true` only for the affirmative verdict.
    let isContinuous =
        function
        | GroundingContinuityVerdict.Continuous _ -> true
        | _ -> false

    /// One-line account of the verdict.
    let describe =
        function
        | GroundingContinuityVerdict.Continuous(steps, digest) ->
            $"grounding continuity: the boot seal plus {steps} recorded mutation(s) accounts for the live envelope '{digest}'"
        | GroundingContinuityVerdict.Diverged divergence -> GroundingDivergence.describe divergence

[<RequireQualifiedAccess>]
module GroundingContinuity =

    /// **The continuity proof.** Walk the boot seal and the recorded
    /// mutation chain forward and check they arrive at the envelope
    /// actually observed.
    ///
    /// Pure and total: no I/O, no clock, no failure mode of its own. An
    /// auditor holding the seal, the audit rows, and a rendered envelope
    /// runs this and reaches the same verdict this process does — which
    /// is what makes the chain evidence rather than bookkeeping.
    ///
    /// The walk reports the FIRST position at which the evidence stops
    /// accounting for the state, in this order: the seal (position 0),
    /// each link (position N), then the head against the live envelope
    /// (position N+1).
    let verify
        (sealDigest: string)
        (chain: GroundingMutationRecord list)
        (observed: GroundingEnvelope)
        : GroundingContinuityVerdict =
        let chain = if isNull (box chain) then [] else chain
        let observedDigest = GroundingEnvelope.digest observed

        let sameDigest (a: string) (b: string) =
            String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

        let rec walk (position: int) (endedAt: string) (remaining: GroundingMutationRecord list) =
            match remaining with
            | [] ->
                if sameDigest endedAt observedDigest then
                    GroundingContinuityVerdict.Continuous(position, endedAt)
                else
                    // The recorded envelope at the head of the chain is
                    // not reconstructible here — only its digest is — so
                    // the differences are named from whichever end the
                    // caller can supply. When the head IS the seal (an
                    // empty chain) the caller's own `verifyAgainst` arm
                    // fills them in; otherwise the digest pair is the
                    // honest whole of what the walk knows.
                    GroundingContinuityVerdict.Diverged(HeadMismatch(position + 1, endedAt, observedDigest, []))
            | record :: rest ->
                if not (sameDigest record.Before endedAt) then
                    if position = 0 then
                        GroundingContinuityVerdict.Diverged(SealMismatch(endedAt, record.Before))
                    else
                        GroundingContinuityVerdict.Diverged(ChainBreak(position + 1, endedAt, record.Before))
                else
                    walk (position + 1) record.After rest

        walk 0 sealDigest chain

    /// `verify` with the SEALED envelope in hand as well as its digest,
    /// so a head mismatch can name every declaration that moved rather
    /// than only reporting that two digests differ.
    ///
    /// The named differences are only exact for an empty chain — the
    /// sealed envelope is then the head. With mutations recorded, the
    /// list describes the drift from the SEAL, which is a superset of the
    /// unrecorded part and is labelled as such by the digests either side
    /// of it.
    let verifyAgainst
        (sealedEnvelope: GroundingEnvelope)
        (chain: GroundingMutationRecord list)
        (observed: GroundingEnvelope)
        : GroundingContinuityVerdict =
        match verify (GroundingEnvelope.digest sealedEnvelope) chain observed with
        | GroundingContinuityVerdict.Diverged(HeadMismatch(position, expected, actual, [])) ->
            GroundingContinuityVerdict.Diverged(
                HeadMismatch(position, expected, actual, GroundingEnvelope.diff sealedEnvelope observed)
            )
        | verdict -> verdict

// ─── The choke point ─────────────────────────────────────────────────

/// A mutation of the grounding envelope, presented at the door.
///
/// The caller supplies the envelope it wants to move TO, and the digest
/// it computed that move against. The door decides; it never guesses what
/// the caller meant.
type GroundingMutationRequest = {
    /// The facet the caller is moving. Must actually be among the facets
    /// that differ between the current and proposed envelopes — a record
    /// naming a facet that did not move is a chain annotated with
    /// fiction.
    Facet: GroundingFacet
    /// The declaration subject being moved.
    Subject: string
    /// The envelope digest this mutation was computed against — the
    /// compare-and-swap baseline. A request built against a superseded
    /// envelope is refused rather than silently rebased.
    Baseline: string
    /// The envelope the mutation produces.
    Proposed: GroundingEnvelope
    /// The principal asking.
    Principal: string
    /// Why. Free-form operator text, recorded verbatim.
    Reason: string
}

/// Why the door refused a mutation.
///
/// Each case names both sides, for Phase 657's reason: a refusal an
/// operator cannot read the cause of is a refusal they will route around.
type GroundingMutationRefusal =
    /// The request was computed against an envelope that is no longer the
    /// chain head — a lost compare-and-swap. Rebase the mutation on the
    /// current envelope and present it again.
    | StaleBaseline of presented: string * current: string
    /// The LIVE envelope no longer matches the chain head: something
    /// moved the grounding declarations without coming through this door.
    /// The chain can no longer prove the state, so it is not extended.
    /// **This is the out-of-path refusal.**
    | OutOfPathDrift of chained: string * observed: string * differences: string list
    /// The request names a `(facet, subject)` that does not appear among
    /// the declarations that actually differ between the current and
    /// proposed envelopes.
    | MutationSubjectMismatch of facet: string * subject: string * moved: string list
    /// The proposed envelope is identical to the current one. A chain
    /// entry for a move that did not happen is a record with no subject.
    | MutationMovesNothing of digest: string

[<RequireQualifiedAccess>]
module GroundingMutationRefusal =

    /// One rendered line naming the subject, both sides, and the remedy.
    let describe =
        function
        | StaleBaseline(presented, current) ->
            $"the mutation was computed against envelope '{presented}' and the current envelope is '{current}'. Re-derive the mutation from the current envelope and present it again."
        | OutOfPathDrift(chained, observed, differences) ->
            let detail =
                if List.isEmpty differences then
                    ""
                else
                    " — " + String.concat "; " differences

            $"the live grounding envelope digests to '{observed}' and the recorded mutation chain accounts only for '{chained}'{detail}. A declaration moved outside the audited path, so the chain can no longer prove the state and is not extended. Record the out-of-path change, or restore the declarations the chain describes."
        | MutationSubjectMismatch(facet, subject, moved) ->
            let actual =
                if List.isEmpty moved then
                    "nothing moved"
                else
                    "what moved was: " + String.concat "; " moved

            $"the mutation claims to move {facet} '{subject}' and {actual}. Name the declaration the proposed envelope actually changes."
        | MutationMovesNothing digest ->
            $"the proposed envelope is identical to the current one ('{digest}'). A mutation record for a move that did not happen makes the chain longer without making it more true."

    /// Stable lowercase code for a payload / dashboard cut.
    let code =
        function
        | StaleBaseline _ -> "stale-baseline"
        | OutOfPathDrift _ -> "out-of-path-drift"
        | MutationSubjectMismatch _ -> "subject-mismatch"
        | MutationMovesNothing _ -> "moves-nothing"

/// The single audited door every grounding-relevant mutation goes
/// through.
///
/// **GP 12.** Identity by value — every parameter and return is a record,
/// a string, or a digest; no live handle crosses it. Async at the
/// boundary that writes audit. Stateless between calls in the sense that
/// matters: the whole of the door's state is the chain it exposes, and a
/// caller reconstructs it from the audit trail without asking this
/// object.
type IGroundingEnvelopeMutator =
    /// The envelope digest sealed at boot — position 0 of every
    /// continuity walk.
    abstract Seal: string

    /// The envelope the recorded chain accounts for.
    abstract Current: GroundingEnvelope

    /// Every recorded mutation, in order.
    abstract Chain: GroundingMutationRecord list

    /// Present a mutation. `Ok` records it and moves the envelope;
    /// `Error` carries every refusal that applied, and nothing moved.
    /// Both arms are audited before returning.
    abstract Apply:
        request: GroundingMutationRequest -> Async<Result<GroundingMutationRecord, GroundingMutationRefusal list>>

    /// Walk the seal and the chain against the LIVE envelope. The
    /// continuity proof, on demand.
    abstract Continuity: unit -> GroundingContinuityVerdict

[<RequireQualifiedAccess>]
module GroundingEnvelopeMutator =

    /// Scope grounding-envelope mutations are recorded under by default —
    /// the platform scope, since the grounding envelope belongs to the
    /// deployment rather than to any tenant.
    [<Literal>]
    let PlatformScopeId = "_platform"

    /// The audit payload for a landed mutation.
    let mutatedPayload
        (profile: CompositionProfile)
        (record: GroundingMutationRecord)
        : GroundingEnvelopeMutatedPayload =
        {
            Facet = GroundingFacet.label record.Facet
            Subject = record.Subject
            BeforeDigest = record.Before
            AfterDigest = record.After
            Sequence = record.Sequence
            Profile = CompositionProfile.label profile
            Principal = record.Principal
            Reason = record.Reason
            Observations = record.Observations
            OccurredAt = record.OccurredAt
        }

    /// The audit payload for a refused mutation.
    let refusedPayload
        (profile: CompositionProfile)
        (request: GroundingMutationRequest)
        (chained: string)
        (observed: string)
        (refusals: GroundingMutationRefusal list)
        : GroundingMutationRefusedPayload =
        {
            Facet = GroundingFacet.label request.Facet
            Subject = request.Subject
            ChainedDigest = chained
            ObservedDigest = observed
            Reasons = refusals |> List.map GroundingMutationRefusal.describe
            Profile = CompositionProfile.label profile
            Principal = request.Principal
            OccurredAt = DateTimeOffset.UtcNow
        }

    /// The door, over a sealed boot envelope and a way to observe the
    /// live one.
    ///
    /// `observe` re-derives the grounding envelope from whatever live
    /// state the deployment actually holds. **This is the load-bearing
    /// argument**, and the honest bound on the whole mechanism: a
    /// deployment whose grounding declarations are compose-time immutable
    /// passes a function returning the composed envelope, and its
    /// continuity check is then trivially continuous — a true statement
    /// about that deployment, not a check that caught anything. A
    /// deployment holding mutable grounding state passes a function that
    /// reads it, and only then can drift be seen.
    ///
    /// Under `CompositionProfile.Verified` every refusal fires. Under
    /// `Standard` the same findings are RECORDED on the mutation row as
    /// observations and the mutation lands — the `LogAndServe` →
    /// `RefuseOnDrift` adoption ladder Phase 657 offers, for the same
    /// reason: a control adopted before anyone knows whether their
    /// deployment passes it is a control that gets switched off.
    /// `MutationMovesNothing` is the one exception and refuses under both
    /// profiles: it is a malformed request rather than a policy question,
    /// and there is no state in which appending it is right.
    let create
        (profile: CompositionProfile)
        (auditLog: IAuditLog)
        (scopeId: string)
        (sealedEnvelope: GroundingEnvelope)
        (observe: unit -> GroundingEnvelope)
        : IGroundingEnvelopeMutator =
        let gate = obj ()
        let sealDigest = GroundingEnvelope.digest sealedEnvelope
        let mutable current = sealedEnvelope
        let chain = ResizeArray<GroundingMutationRecord>()

        let enforcing =
            match profile with
            | CompositionProfile.Standard -> false
            | CompositionProfile.Verified -> true

        { new IGroundingEnvelopeMutator with
            member _.Seal = sealDigest
            member _.Current = lock gate (fun () -> current)
            member _.Chain = lock gate (fun () -> List.ofSeq chain)

            member _.Continuity() =
                let snapshotSeal, snapshotChain =
                    lock gate (fun () -> sealedEnvelope, List.ofSeq chain)

                GroundingContinuity.verifyAgainst snapshotSeal snapshotChain (observe ())

            member _.Apply request = async {
                // Everything that decides the outcome is computed inside
                // the lock, and the audit write happens outside it: a
                // door whose throughput depends on an audit backend's
                // latency is a door that gets bypassed.
                let outcome =
                    lock gate (fun () ->
                        let currentDigest = GroundingEnvelope.digest current
                        let proposedDigest = GroundingEnvelope.digest request.Proposed
                        let live = observe ()
                        let liveDigest = GroundingEnvelope.digest live

                        let refusals = [
                            if
                                not (
                                    String.Equals(request.Baseline, currentDigest, StringComparison.OrdinalIgnoreCase)
                                )
                            then
                                StaleBaseline(request.Baseline, currentDigest)

                            if not (String.Equals(liveDigest, currentDigest, StringComparison.OrdinalIgnoreCase)) then
                                OutOfPathDrift(currentDigest, liveDigest, GroundingEnvelope.diff current live)

                            let moved = GroundingEnvelope.movedSubjects current request.Proposed

                            if List.isEmpty moved then
                                MutationMovesNothing proposedDigest
                            elif not (List.contains (GroundingFacet.label request.Facet, request.Subject) moved) then
                                MutationSubjectMismatch(
                                    GroundingFacet.label request.Facet,
                                    request.Subject,
                                    moved |> List.map (fun (facet, subject) -> $"{facet} '{subject}'")
                                )
                        ]

                        let fatal =
                            refusals
                            |> List.filter (fun r ->
                                enforcing
                                || match r with
                                   | MutationMovesNothing _ -> true
                                   | _ -> false)

                        if not (List.isEmpty fatal) then
                            Error(currentDigest, liveDigest, fatal)
                        else
                            let record = {
                                Sequence = chain.Count + 1
                                Facet = request.Facet
                                Subject = request.Subject
                                Before = currentDigest
                                After = proposedDigest
                                Principal = request.Principal
                                Reason = request.Reason
                                Observations = refusals |> List.map GroundingMutationRefusal.describe
                                OccurredAt = DateTimeOffset.UtcNow
                            }

                            current <- request.Proposed
                            chain.Add record
                            Ok record)

                match outcome with
                | Ok record ->
                    do! auditLog.Record(scopeId, AuditEvent.GroundingEnvelopeMutated(mutatedPayload profile record))

                    return Ok record
                | Error(currentDigest, liveDigest, refusals) ->
                    do!
                        auditLog.Record(
                            scopeId,
                            AuditEvent.GroundingMutationRefused(
                                refusedPayload profile request currentDigest liveDigest refusals
                            )
                        )

                    return Error refusals
            }
        }

    /// The door over a composition that holds its grounding declarations
    /// immutably — every composition this SDK ships.
    ///
    /// The observation is the sealed envelope itself, so continuity is
    /// continuous by construction and stays so until a mutation lands
    /// through the door. Stated plainly rather than presented as a
    /// verified property: what this arm proves is that the deployment has
    /// nothing that could drift, which is a fact about the deployment.
    let forImmutableComposition
        (profile: CompositionProfile)
        (auditLog: IAuditLog)
        (scopeId: string)
        (sealedEnvelope: GroundingEnvelope)
        : IGroundingEnvelopeMutator =
        // The observation follows the door's own recorded state: a
        // composition with no mutable grounding state is exactly the one
        // whose live envelope IS whatever the chain last recorded.
        let mutable observed = sealedEnvelope

        let inner = create profile auditLog scopeId sealedEnvelope (fun () -> observed)

        { new IGroundingEnvelopeMutator with
            member _.Seal = inner.Seal
            member _.Current = inner.Current
            member _.Chain = inner.Chain
            member _.Continuity() = inner.Continuity()

            member _.Apply request = async {
                match! inner.Apply request with
                | Ok record ->
                    observed <- request.Proposed
                    return Ok record
                | Error refusals -> return Error refusals
            }
        }