// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Text

// ─── Phase 657 — boot verification preflight + the verified profile ──
//
// Two things were promise-tier before this file existed, and both looked
// like guarantees from the outside.
//
// The first: **nothing checked at startup that the running composition is
// the one that was sealed.** Phase 656 records what was deployed and seals
// it, and Phase 655 supplies the scheme — but a sealed record is a
// statement filed away at deploy time. A process could start from a
// different composition entirely and no part of the substrate would
// notice, because no part of the substrate ever asked. `run` below is
// that question, asked once, at boot.
//
// The second: **runtime capability enforcement was opt-in and nothing
// required it.** `CompositionCapabilityGate` (Phase 300) is complete,
// default-deny and tested — and a deployment that simply never composes
// it is unenforced, indistinguishable from one that composed it and had
// nothing to refuse. The **verified composition profile** below is the
// opt-in under which that gate stops being optional.
//
// ── What the preflight verifies, exactly ─────────────────────────────
//
// Four questions, in this order, and no others:
//
//   1. *Was anything supplied to verify against?* — no sealed record, or
//      no composition binding, is `Unsealed`. It is reported as its own
//      verdict rather than folded into a failure, because "unsealed" and
//      "sealed and wrong" call for opposite operator responses.
//   2. *Does the seal hold, and do the recorded artifacts match the
//      files?* — Phase 656's `DeployRecords.verify`, unchanged and
//      un-reimplemented.
//   3. *Does the binding belong to THIS record?* — the binding carries
//      the digest of the record's canonical bytes, so a binding minted
//      for a different deploy cannot be presented alongside this one.
//   4. *Is the running composition the one the binding recorded?* — a
//      component-by-component, knob-by-knob comparison that names every
//      difference rather than reporting a digest that did not match.
//      **Phase 694 added the canonical-method selector to that
//      comparison**, and it is worth saying why it needed adding: the
//      manifest recorded a metric as its id alone, so two boots either
//      side of a canonical-method flip — the one grounding mutation that
//      changes what an already enumerated number MEANS — compared equal
//      and this preflight reported `verified`. It now compares them, and
//      reads a binding sealed before that field existed as SILENT on it
//      (`VerifiedUnrecorded`) rather than as agreeing.
//
// ── What it does NOT prove ───────────────────────────────────────────
//
// Written here rather than left to be inferred, because a reader who
// over-reads this is worse off than one who knows its bound. The same
// statement, at the same level of care, is in the migration doc.
//
//   * It proves nothing about **post-boot mutation**. The verdict
//     describes the composition at the instant it was derived. Anything a
//     process does to itself afterwards — a hot-reloaded module, a
//     re-registered handler, a knob flipped through an admin surface — is
//     outside what a boot-time check can see, and this profile does not
//     freeze the composition to close that. A deployment wanting that
//     property needs a freeze, and does not have one here.
//   * It proves nothing about the **truth of the recorded inputs**. A
//     seal makes a statement attributable and tamper-evident; it does not
//     make it correct. That is Phase 656's bound and it is inherited
//     whole.
//   * It proves nothing about **code that was never composed**. The
//     manifest enumerates composed units. A library linked into the
//     process but registered with nothing is invisible to it.
//   * The capability gate binds a component to the envelope its
//     composition **declared**. It is not a sandbox: a component that
//     bypasses the gate's call sites is not stopped by it, because the
//     gate is a decision point, not a boundary.
//
// ── Cost when unused ─────────────────────────────────────────────────
//
// Everything here is new, nothing existing is retyped, and no caller is
// obliged to invoke any of it. `CompositionProfile.Standard` keeps the
// log-and-serve default, leaves the capability gate `disabled`, and
// returns the external-compute dispatcher unwrapped — so an existing
// deployment that upgrades is byte-for-byte unchanged until it opts in
// (GP 11 / GP 13).

// ─── The composition binding ─────────────────────────────────────────

/// Phase 657 — the composition as it stood when a deployment was sealed,
/// bound to the sealed deploy record it accompanies.
///
/// **A separate sealed statement, not a field on `DeployRecord`.** The
/// same call Phase 656 made one phase earlier, for the same reason:
/// adding a field to `DeployRecord` would retype its constructor and
/// break every consumer that builds one literally, for the benefit of
/// consumers that fill it — none, today. As a separate record, an
/// existing deployment is untouched and sealing a composition is a thing
/// a deployment starts doing rather than a thing it is retyped into
/// (GP 11 / GP 13).
///
/// **`DeployRecordDigest` is what stops a swap.** Without it a binding
/// minted for a different deploy could be presented alongside this
/// record and both seals would verify perfectly, since each is
/// individually genuine. The digest ties the pair together, and the
/// preflight checks it.
type CompositionBinding = {
    /// Schema version this binding was authored against.
    SchemaVersion: int
    /// Lowercase-hex digest of the canonical bytes of the `DeployRecord`
    /// this binding accompanies (`DeployRecords.canonicalBytes` hashed
    /// by `DeployRecords.digestBytes`).
    DeployRecordDigest: string
    /// The composition as recorded at seal time — the value a later boot
    /// derives afresh and compares itself against.
    Composition: CompositionManifest
}

/// A `CompositionBinding` plus the seal over its canonical bytes. The
/// seal is minted by the same `IDeployRecordSealer` a deployment already
/// seals its deploy records with (Phase 656), so a deployment adopting
/// this needs no second key, no second scheme, and no second trust root.
type SealedCompositionBinding = {
    Binding: CompositionBinding
    Seal: DeployRecordSeal
}

// ─── Drift ───────────────────────────────────────────────────────────

/// One way the running composition differs from the one the binding
/// recorded.
///
/// Every case names its subject. "The composition drifted" is not a
/// finding an operator can act on; "the audit sink `splunk-hec` is
/// composed here and was not recorded" is.
type CompositionDrift =
    /// A component composed at boot that the sealed composition did not
    /// record.
    | ComponentAdded of kind: string * id: string
    /// A component the sealed composition recorded that is not composed
    /// at boot.
    | ComponentRemoved of kind: string * id: string
    /// A component present in both whose implementation label moved —
    /// the same slot filled by something else.
    | ComponentImplChanged of kind: string * id: string * recorded: string * observed: string
    /// A composition-shaping config knob whose value moved.
    | ConfigKnobChanged of name: string * recorded: string * observed: string
    /// A config knob present at boot that the sealed composition did not
    /// record.
    | ConfigKnobAdded of name: string * value: string
    /// A config knob the sealed composition recorded that is absent at
    /// boot.
    | ConfigKnobRemoved of name: string * value: string
    /// Phase 694 — a metric present in both compositions whose
    /// canonical-method selector moved: a method-less query over that
    /// metric now resolves to a different method's lineage than the sealed
    /// composition declared. Nothing else about the composition need have
    /// changed for this to change what a recorded number means.
    | CanonicalMethodChanged of metricId: string * recorded: string * observed: string
    /// Phase 694 — a metric the sealed composition recorded WITHOUT a
    /// canonical method that now declares one. A first declaration is a
    /// change of resolution behaviour, not an addition to a blank slate:
    /// before it, a method-less query surfaced every competing head.
    | CanonicalMethodDeclared of metricId: string * observed: string
    /// Phase 694 — a metric whose recorded canonical method is no longer
    /// declared. The twin of `CanonicalMethodDeclared`, and equally a
    /// change in what a method-less query returns.
    | CanonicalMethodWithdrawn of metricId: string * recorded: string

[<RequireQualifiedAccess>]
module CompositionDrift =
    /// One rendered line per difference, naming the subject and both
    /// sides where both exist.
    let describe =
        function
        | ComponentAdded(kind, id) -> $"composed but not recorded: {kind} '{id}'"
        | ComponentRemoved(kind, id) -> $"recorded but not composed: {kind} '{id}'"
        | ComponentImplChanged(kind, id, recorded, observed) ->
            $"{kind} '{id}' is filled by '{observed}', recorded as '{recorded}'"
        | ConfigKnobChanged(name, recorded, observed) ->
            $"config knob '{name}' is '{observed}', recorded as '{recorded}'"
        | ConfigKnobAdded(name, value) -> $"config knob '{name}' = '{value}' is set but was not recorded"
        | ConfigKnobRemoved(name, value) -> $"config knob '{name}' was recorded as '{value}' and is not set"
        | CanonicalMethodChanged(metricId, recorded, observed) ->
            $"metric '{metricId}' resolves a method-less query by canonical method '{observed}', recorded as '{recorded}'"
        | CanonicalMethodDeclared(metricId, observed) ->
            $"metric '{metricId}' declares canonical method '{observed}', and the sealed composition recorded none for it"
        | CanonicalMethodWithdrawn(metricId, recorded) ->
            $"metric '{metricId}' declares no canonical method, and the sealed composition recorded '{recorded}'"

// ─── Phase 694 — what an older binding could not record ──────────────

/// One declaration the running composition makes that the sealed binding
/// is too old to have recorded — so the comparison could not be
/// performed, and the honest report of that is neither a match nor a
/// difference.
///
/// **This exists so that "unrecorded" never renders as "unchanged".** A
/// binding sealed before a field joined the manifest is silent about it;
/// resolving that silence as agreement would let the single most
/// consequential grounding mutation pass a preflight that reported
/// `verified`, which is worse than reporting nothing because it is
/// believed.
type CompositionUnrecorded =
    /// The sealed binding predates the manifest's canonical-method field,
    /// so this metric's selector could not be compared. `observed` is what
    /// the running composition declares — `None` when it declares none,
    /// which is equally unprovable against a silent binding.
    | CanonicalMethodUnrecorded of metricId: string * observed: string option

[<RequireQualifiedAccess>]
module CompositionUnrecorded =
    /// One rendered line naming the metric, what is live, and why nothing
    /// can be concluded about it.
    let describe =
        function
        | CanonicalMethodUnrecorded(metricId, Some selector) ->
            $"metric '{metricId}' resolves a method-less query by canonical method '{selector}', and the sealed binding predates canonical-method recording — so this could not be compared, and is not evidence that it did not move"
        | CanonicalMethodUnrecorded(metricId, None) ->
            $"metric '{metricId}' declares no canonical method, and the sealed binding predates canonical-method recording — so this could not be compared, and is not evidence that it did not move"

    /// Stable lowercase code for a payload / dashboard cut.
    let code =
        function
        | CanonicalMethodUnrecorded _ -> "canonical-method-unrecorded"

// ─── Verdict ─────────────────────────────────────────────────────────

/// What the boot verification concluded.
[<RequireQualifiedAccess>]
type BootVerificationVerdict =
    /// Every question the preflight asked was answered affirmatively:
    /// the record is sealed, its artifacts match, the binding belongs to
    /// it, and the running composition is the one it recorded.
    | Verified
    /// Phase 694 — everything the sealed binding recorded matched, and the
    /// binding is too old to have recorded one or more declarations, so
    /// those could not be compared. Carries each.
    ///
    /// **Affirmative, and deliberately not `Verified`.** Affirmative
    /// because an old binding is not a drifted deployment: the transition
    /// boot after an upgrade must not refuse to start, or the honest fix
    /// for a blind spot would cost every sealed deployment an outage.
    /// Distinct from `Verified` because the preflight did not check what
    /// it did not check, and a verdict that said otherwise would be the
    /// silent equality this case exists to prevent. The remedy is one
    /// act — re-seal the binding from the running composition — after
    /// which the verdict is `Verified` and stays there.
    | VerifiedUnrecorded of unrecorded: CompositionUnrecorded list
    /// Nothing was supplied to verify against — no sealed deploy record,
    /// or no sealed composition binding. Its own verdict rather than a
    /// failure: an unsealed deployment is not a tampered one, and
    /// conflating them teaches operators to ignore both.
    | Unsealed of reason: string
    /// The seal did not hold, an artifact did not match, or the binding
    /// belongs to a different deploy. Carries Phase 656's own failure
    /// list plus any binding-level finding, each already self-describing.
    | VerificationFailed of failures: DeployRecords.DeployRecordVerificationFailure list * bindingFindings: string list
    /// Everything sealed verified, and the running composition is not
    /// the one that was sealed. Carries every difference found.
    | Drifted of drift: CompositionDrift list
    /// Phase 678 — the sealed deploy record this deployment was started
    /// from has been RETIRED: a signed terminal op closed its audit
    /// ledger and a retirement reference binds the two.
    ///
    /// **Its own case, and not a `VerificationFailed` finding.** A retired
    /// record is not a broken one — its seal verifies, its artifacts
    /// match, and its composition may be exactly what was sealed. What is
    /// true of it is that the deployment it describes is over, which is a
    /// different fact calling for a different response: an operator
    /// looking at `unverified` hunts for tampering, and an operator
    /// looking at `retired` restores from the wrong backup or points a
    /// container at a decommissioned engagement.
    ///
    /// **Not affirmative, and it refuses under EVERY policy** — see
    /// `runWithRetirement`. Log-and-serve exists so a deployment can adopt
    /// a check before knowing whether it passes; there is no equivalent
    /// grace period for serving a deployment somebody signed a
    /// decommission for.
    | Retired of retirement: DeployRetirement

[<RequireQualifiedAccess>]
module BootVerificationVerdict =
    /// Stable lowercase label for logs / audit payloads / dashboards.
    let label =
        function
        | BootVerificationVerdict.Verified -> "verified"
        | BootVerificationVerdict.VerifiedUnrecorded _ -> "verified-unrecorded"
        | BootVerificationVerdict.Unsealed _ -> "unsealed"
        | BootVerificationVerdict.VerificationFailed _ -> "unverified"
        | BootVerificationVerdict.Drifted _ -> "drifted"
        | BootVerificationVerdict.Retired _ -> "retired"

    /// `true` for the two affirmative verdicts. Everything else is a
    /// reason not to serve under a refusing policy.
    ///
    /// `VerifiedUnrecorded` is affirmative on purpose — see its own
    /// documentation. A caller that needs the stricter question ("did the
    /// preflight compare everything it now knows how to compare?") matches
    /// on the verdict; that is the discrimination the case exists to make
    /// available, and folding it in here would remove it again.
    let isAffirmative =
        function
        | BootVerificationVerdict.Verified
        | BootVerificationVerdict.VerifiedUnrecorded _ -> true
        | _ -> false

    /// `true` only for the verdict under which every question the
    /// preflight can ask was asked AND answered affirmatively.
    let isFullyCompared =
        function
        | BootVerificationVerdict.Verified -> true
        | _ -> false

    /// One rendered line per finding — the operator-facing detail behind
    /// the label. Empty for `Verified`.
    let findings =
        function
        | BootVerificationVerdict.Verified -> []
        | BootVerificationVerdict.VerifiedUnrecorded unrecorded -> unrecorded |> List.map CompositionUnrecorded.describe
        | BootVerificationVerdict.Unsealed reason -> [ reason ]
        | BootVerificationVerdict.VerificationFailed(failures, bindingFindings) ->
            (failures |> List.map DeployRecords.DeployRecordVerificationFailure.describe)
            @ bindingFindings
        | BootVerificationVerdict.Drifted drift -> drift |> List.map CompositionDrift.describe
        | BootVerificationVerdict.Retired retirement -> [ DeployRetirement.describe retirement ]

    /// One-line account naming the verdict and how many findings sit
    /// behind it.
    let describe (verdict: BootVerificationVerdict) : string =
        match verdict with
        | BootVerificationVerdict.Verified ->
            "boot verification: the running composition is the one the sealed deploy record covers"
        | BootVerificationVerdict.VerifiedUnrecorded unrecorded ->
            $"boot verification: the running composition matches everything the sealed binding recorded, and the binding is too old to have recorded {unrecorded.Length} declaration(s) — re-seal the composition binding to close the gap"
        | BootVerificationVerdict.Unsealed reason -> $"boot verification: nothing to verify against — {reason}"
        | BootVerificationVerdict.VerificationFailed _ ->
            let count = (findings verdict).Length
            $"boot verification: the sealed deploy record did not verify ({count} finding(s))"
        | BootVerificationVerdict.Drifted drift ->
            $"boot verification: the running composition differs from the sealed one ({drift.Length} difference(s))"
        | BootVerificationVerdict.Retired retirement ->
            $"boot verification: this deployment's sealed record was retired by '{retirement.RetiredBy}' at {retirement.RetiredAt} — its audit ledger is closed and it must not serve"

// ─── Policy + profile ────────────────────────────────────────────────

/// What a non-affirmative verdict does to the process.
[<RequireQualifiedAccess>]
type BootVerificationPolicy =
    /// **The default (GP 11).** Record the verdict and serve. An existing
    /// deployment that starts running the preflight gains a row in its
    /// audit trail and changes no behaviour — which is the only way a
    /// check like this can be adopted before anyone knows whether their
    /// deployment passes it.
    | LogAndServe
    /// Refuse to start on anything but `Verified`. The posture a
    /// deployment moves to once it has watched `LogAndServe` report
    /// clean for long enough to believe it.
    | RefuseOnDrift

[<RequireQualifiedAccess>]
module BootVerificationPolicy =
    let label =
        function
        | BootVerificationPolicy.LogAndServe -> "log-and-serve"
        | BootVerificationPolicy.RefuseOnDrift -> "refuse-on-drift"

/// Phase 657 — how much the platform binds a composition to what it
/// declared.
///
/// Deliberately a composition-level sibling of Phase 478's
/// `ExecutionProfile` rather than a case added to it: one is about the
/// environment a unit of external work runs in, the other about the
/// authority the composition itself carries, and a deployment can want
/// either without the other.
[<RequireQualifiedAccess>]
type CompositionProfile =
    /// Behaviour before this phase, unchanged. The preflight honours
    /// whatever policy it was given, the capability gate stays whatever
    /// the deployment composed, and the external-compute dispatcher is
    /// returned as handed over.
    | Standard
    /// The **verified composition profile**. Three things become true at
    /// once, and the value is in the conjunction rather than any one of
    /// them:
    ///
    ///   1. The boot preflight is refuse-on-drift, whatever policy was
    ///      passed. A profile that could be configured back to serving on
    ///      drift would not be a profile.
    ///   2. The capability gate is **mandatory**: a composition that
    ///      declares no `CapabilitySignature` is refused rather than
    ///      quietly granted everything, so each module's runtime
    ///      authority is bound to its declared envelope.
    ///   3. External compute is submitted through Phase 478's
    ///      `ExecutionProfileGate`, so an `Isolated` spec a backend
    ///      cannot honour is refused before submission. This profile is
    ///      that profile's enforcement layer — one build, two products.
    | Verified

[<RequireQualifiedAccess>]
module CompositionProfile =
    /// Stable lowercase label for logs / audit payloads / dev panels.
    let label =
        function
        | CompositionProfile.Standard -> "standard"
        | CompositionProfile.Verified -> "verified"

    /// The policy actually in force. `Standard` honours the policy it was
    /// given — a deployment may adopt refuse-on-drift without the rest of
    /// the profile — while `Verified` is refuse-on-drift by definition.
    let effectivePolicy (profile: CompositionProfile) (policy: BootVerificationPolicy) : BootVerificationPolicy =
        match profile with
        | CompositionProfile.Standard -> policy
        | CompositionProfile.Verified -> BootVerificationPolicy.RefuseOnDrift

    /// Phase 688 — whether declared reachable-seam sets are required
    /// rather than optional. Same ladder as `requiresCapabilityGate`, and
    /// deliberately a separate predicate: a deployment reading the profile
    /// to decide what to demand should not have to know that the two
    /// happen to move together today.
    let requiresSeamGrants =
        function
        | CompositionProfile.Standard -> false
        | CompositionProfile.Verified -> true

    /// Whether an enabled capability gate is required rather than
    /// optional.
    let requiresCapabilityGate =
        function
        | CompositionProfile.Standard -> false
        | CompositionProfile.Verified -> true

/// A composition that cannot satisfy the profile it declared. Refused at
/// composition time, before anything serves — a profile discovered to be
/// unsatisfiable on the first request is a profile that already failed.
type CompositionProfileRefusal =
    /// The verified profile was declared and no `CapabilitySignature`
    /// was supplied, so there is no envelope to bind any component to.
    | CapabilityGateUndeclared
    /// The verified profile was declared and a backend asked to run
    /// isolated work does not assert the clauses that make it isolating.
    | IsolationPostureShortfall of backend: string * missing: string list
    /// Phase 688 — the verified profile was declared and one or more
    /// components declared no reachable-seam set, so their outbound
    /// authority is bound to nothing. An EMPTY component list means no
    /// `SeamGrantSignature` was supplied at all.
    | SeamGrantsUndeclared of components: string list

[<RequireQualifiedAccess>]
module CompositionProfileRefusal =
    let describe =
        function
        | SeamGrantsUndeclared [] ->
            "the verified composition profile requires a SeamGrantSignature: each component's outbound authority is bound to the seams it declared it reaches, and a composition that declares no seam sets has nothing to bind. Declare the components' SeamGrant values and compose the signature, or run CompositionProfile.Standard."
        | SeamGrantsUndeclared components ->
            let named = String.concat "; " components

            $"the verified composition profile requires every component to declare the seams it reaches, and these declare none: {named}. Add a SeamGrant for each (SeamGrant.ofInterfaces [ \"IEntityStore\"; … ]), or run CompositionProfile.Standard."
        | CapabilityGateUndeclared ->
            "the verified composition profile requires a CapabilitySignature: each component's runtime authority is bound to its declared envelope, and a composition that declares no envelopes has nothing to bind. Declare the components' CompanionCapability values and compose the signature, or run CompositionProfile.Standard."
        | IsolationPostureShortfall(backend, missing) ->
            let clauses = String.concat "; " missing

            $"the verified composition profile requires backend '{backend}' to honour ExecutionProfile.Isolated, and it does not assert: {clauses}. Compose a backend that declares the isolation posture, or drop the work to ExecutionProfile.Standard."

// ─── The preflight ───────────────────────────────────────────────────

/// The result of a boot verification, and the decision it produced.
type BootVerificationResult = {
    Verdict: BootVerificationVerdict
    /// The profile the deployment declared.
    Profile: CompositionProfile
    /// The policy actually in force (the profile may have overridden the
    /// one that was passed).
    Policy: BootVerificationPolicy
    /// Whether this verdict refused the process a start.
    RefusedStart: bool
}

/// Everything the preflight needs that is not the composition itself.
///
/// A new options record rather than fields on `ServerConfig`: widening
/// that record retypes its constructor and every consumer that builds
/// one, for a feature no existing consumer configures. A deployment
/// opting in constructs this and calls `run`; one that does not is
/// untouched (GP 11, and the standing design corollary for opt-in
/// surface).
type BootVerificationOptions = {
    /// The profile the deployment declares.
    Profile: CompositionProfile
    /// The policy for a non-affirmative verdict. Overridden to
    /// refuse-on-drift under `CompositionProfile.Verified`.
    Policy: BootVerificationPolicy
    /// The sealer whose scheme the deployment's seals were minted under.
    Sealer: IDeployRecordSealer
    /// How to find a recorded artifact on this host.
    Locate: DeployRecords.ArtifactLocator
    /// The sealed deploy record this deployment was started from, when
    /// it has one.
    Record: SealedDeployRecord option
    /// The sealed composition binding that accompanies it, when it has
    /// one.
    Binding: SealedCompositionBinding option
    /// The build transcript to check the record's transcript digest
    /// against, when the deployment holds it. `None` SKIPS that question
    /// rather than answering it affirmatively — Phase 656's contract,
    /// inherited unchanged.
    Transcript: BuildTranscript option
    /// Where the verdict is recorded. `None` records nothing, which is
    /// what a deployment with no audit log composed has to mean.
    AuditLog: IAuditLog option
    /// Scope the verdict is recorded under.
    ScopeId: string
}

[<RequireQualifiedAccess>]
module BootVerificationPreflight =

    /// Scope boot verification verdicts are recorded under by default —
    /// the platform scope, since a boot verdict belongs to the
    /// deployment rather than to any tenant.
    [<Literal>]
    let PlatformScopeId = "_platform"

    /// Framing version for the composition binding's canonical form.
    [<Literal>]
    let BindingFramingVersion = "toolup.compositionbinding.v1"

    /// Schema version of `CompositionBinding`.
    [<Literal>]
    let BindingSchemaVersion = 1

    // ─── Canonical form ──────────────────────────────────────────────

    let private kindLabel =
        function
        | ModuleComponent -> "module"
        | CompanionComponent -> "companion"
        | DataTypeComponent -> "datatype"
        | ToolComponent -> "tool"
        | MetricComponent -> "metric"
        | SubjectComponent -> "subject"
        | PurposeComponent -> "purpose"

    /// Null-list coercion on the read path. A binding that round-tripped
    /// through a serialiser predating one of these lists deserialises it
    /// as `null`, and a null F# list faults on the first list operation
    /// — including the comparison this whole file exists to perform.
    let private coerceEntries (entries: ComponentEntry list) : ComponentEntry list =
        if isNull (box entries) then [] else entries

    /// Sort key: kind, then id, then impl. Total and stable, so the
    /// canonical form is a function of the composition rather than of
    /// the order the accumulators happened to be walked in.
    let private entryKey (entry: ComponentEntry) : string * string * string =
        kindLabel entry.Kind, entry.Id.Value, (entry.Impl |> Option.defaultValue "")

    /// Every entry in the manifest, in a canonical order.
    let private canonicalEntries (manifest: CompositionManifest) : ComponentEntry list =
        [
            yield! coerceEntries manifest.Modules
            yield! coerceEntries manifest.CompanionSlots
            yield! coerceEntries manifest.DataTypes
            yield! coerceEntries manifest.Tools
            yield! coerceEntries manifest.Metrics
            yield! coerceEntries manifest.Subjects
            yield! coerceEntries manifest.Purposes
        ]
        |> List.distinctBy entryKey
        |> List.sortBy entryKey

    /// The knobs in a canonical order, de-duplicated by name.
    let private canonicalKnobs (manifest: CompositionManifest) : ConfigKnob list =
        (if isNull (box manifest.ConfigKnobs) then
             []
         else
             manifest.ConfigKnobs)
        |> List.distinctBy _.Name
        |> List.sortBy _.Name

    /// Framing version for the Phase 694 canonical-method block. Part of
    /// the framed bytes, so a manifest canonicalised under a future
    /// scheme can never collide with one canonicalised under this.
    [<Literal>]
    let CanonicalMethodFramingVersion = "toolup.compositionmanifest.canonicalmethods.v1"

    /// Length-framed canonical text for a composition manifest.
    ///
    /// Framed with the same injective scheme Phase 656 uses, for the same
    /// reason: without it, two distinct compositions could canonicalise
    /// to the same text by concatenation and a digest over them would be
    /// meaningless.
    ///
    /// **Phase 694: the canonical-method block is emitted only for a
    /// manifest that records it, and that gate is load-bearing.** A
    /// binding sealed before Phase 694 must canonicalise to the exact
    /// bytes its seal was minted over — for ever, on every host that
    /// re-reads it. Appending even a `"0"` length for an absent block
    /// would change the canonical form of every manifest in existence, and
    /// the first thing an upgraded deployment would find is that its own
    /// genuine, untampered seal no longer verifies. Versioned evolution
    /// means the old bytes stay reachable, not merely that the new field
    /// is optional.
    let compositionCanonicalForm (manifest: CompositionManifest) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder
        let frameOptional = ProvenanceFraming.frameOptional builder

        let entries = canonicalEntries manifest
        let knobs = canonicalKnobs manifest

        frame (string entries.Length)

        for entry in entries do
            frame (kindLabel entry.Kind)
            frame entry.Id.Value
            frame entry.Label
            frameOptional entry.Impl

        frame (string knobs.Length)

        for knob in knobs do
            frame knob.Name
            frame knob.Value

        if CompositionManifest.recordsCanonicalMethods manifest then
            let methods = CompositionManifest.canonicalMethods manifest

            frame CanonicalMethodFramingVersion
            frame (string (CompositionManifest.effectiveSchemaVersion manifest))
            frame (string methods.Length)

            for method in methods do
                frame method.MetricId
                frame method.Selector

        builder.ToString()

    /// Length-framed canonical text for a composition binding — the
    /// exact bytes its seal is taken over.
    let bindingCanonicalForm (binding: CompositionBinding) : string =
        let builder = StringBuilder()
        let frame = ProvenanceFraming.frame builder

        frame BindingFramingVersion
        frame (string binding.SchemaVersion)
        frame binding.DeployRecordDigest
        frame (compositionCanonicalForm binding.Composition)

        builder.ToString()

    /// The canonical bytes a binding's seal covers.
    let bindingCanonicalBytes (binding: CompositionBinding) : byte[] =
        binding |> bindingCanonicalForm |> Text.Encoding.UTF8.GetBytes

    // ─── Minting a binding ───────────────────────────────────────────

    /// Bind a composition to the deploy record it was composed for.
    ///
    /// Called on the producing side — the operator tooling that sealed
    /// the deploy record seals this beside it, from the same composition
    /// the deployment will derive afresh at boot.
    let bindingFor (record: DeployRecord) (composition: CompositionManifest) : CompositionBinding = {
        SchemaVersion = BindingSchemaVersion
        DeployRecordDigest = record |> DeployRecords.canonicalBytes |> DeployRecords.digestBytes
        Composition = composition
    }

    /// Seal a composition binding with the deployment's own sealer.
    let sealBinding
        (sealer: IDeployRecordSealer)
        (binding: CompositionBinding)
        : Async<Result<SealedCompositionBinding, string>> =
        async {
            match! sealer.Seal(bindingCanonicalBytes binding) with
            | Ok seal -> return Ok { Binding = binding; Seal = seal }
            | Error reason -> return Error reason
        }

    // ─── Comparing compositions ──────────────────────────────────────

    /// Every way `observed` differs from `recorded`, each naming its
    /// subject.
    ///
    /// Accumulates: a caller holding a drifted deployment wants the whole
    /// list, and a comparison that stopped at the first difference would
    /// invite a second boot to discover the second one.
    let compare (recorded: CompositionManifest) (observed: CompositionManifest) : CompositionDrift list =
        let key (entry: ComponentEntry) = kindLabel entry.Kind, entry.Id.Value

        let recordedEntries =
            canonicalEntries recorded |> List.map (fun e -> key e, e) |> Map.ofList

        let observedEntries =
            canonicalEntries observed |> List.map (fun e -> key e, e) |> Map.ofList

        let componentFindings = [
            for KeyValue((kind, id), observedEntry) in observedEntries do
                match Map.tryFind (kind, id) recordedEntries with
                | None -> ComponentAdded(kind, id)
                | Some recordedEntry ->
                    let recordedImpl = recordedEntry.Impl |> Option.defaultValue recordedEntry.Label
                    let observedImpl = observedEntry.Impl |> Option.defaultValue observedEntry.Label

                    if recordedImpl <> observedImpl then
                        ComponentImplChanged(kind, id, recordedImpl, observedImpl)

            for KeyValue((kind, id), _) in recordedEntries do
                if not (Map.containsKey (kind, id) observedEntries) then
                    ComponentRemoved(kind, id)
        ]

        let recordedKnobs =
            canonicalKnobs recorded |> List.map (fun k -> k.Name, k.Value) |> Map.ofList

        let observedKnobs =
            canonicalKnobs observed |> List.map (fun k -> k.Name, k.Value) |> Map.ofList

        let knobFindings = [
            for KeyValue(name, observedValue) in observedKnobs do
                match Map.tryFind name recordedKnobs with
                | None -> ConfigKnobAdded(name, observedValue)
                | Some recordedValue ->
                    if recordedValue <> observedValue then
                        ConfigKnobChanged(name, recordedValue, observedValue)

            for KeyValue(name, recordedValue) in recordedKnobs do
                if not (Map.containsKey name observedKnobs) then
                    ConfigKnobRemoved(name, recordedValue)
        ]

        // Phase 694 — the canonical-method selectors, compared ONLY when
        // both manifests record them. When the recorded side does not, the
        // comparison is not performed at all and `unrecorded` below says
        // so; silently treating the legacy side's empty list as "no metric
        // declared one" would resolve the upgrade of every sealed
        // deployment as a fresh declaration on every metric.
        let canonicalMethodFindings =
            if
                not (
                    CompositionManifest.recordsCanonicalMethods recorded
                    && CompositionManifest.recordsCanonicalMethods observed
                )
            then
                []
            else
                let metricIds (manifest: CompositionManifest) =
                    coerceEntries manifest.Metrics |> List.map _.Label |> Set.ofList

                // Restricted to metrics BOTH compositions carry: a metric
                // that appeared or vanished is already one finding
                // (`ComponentAdded` / `ComponentRemoved`), and reporting
                // its selector as a second is two lines for one move.
                let shared = Set.intersect (metricIds recorded) (metricIds observed)

                let selectors (manifest: CompositionManifest) =
                    CompositionManifest.canonicalMethods manifest
                    |> List.filter (fun m -> Set.contains m.MetricId shared)
                    |> List.map (fun m -> m.MetricId, m.Selector)
                    |> Map.ofList

                let recordedMethods = selectors recorded
                let observedMethods = selectors observed

                [
                    for KeyValue(metricId, observedSelector) in observedMethods do
                        match Map.tryFind metricId recordedMethods with
                        | None -> CanonicalMethodDeclared(metricId, observedSelector)
                        | Some recordedSelector ->
                            if recordedSelector <> observedSelector then
                                CanonicalMethodChanged(metricId, recordedSelector, observedSelector)

                    for KeyValue(metricId, recordedSelector) in recordedMethods do
                        if not (Map.containsKey metricId observedMethods) then
                            CanonicalMethodWithdrawn(metricId, recordedSelector)
                ]

        componentFindings @ knobFindings @ canonicalMethodFindings

    /// Phase 694 — every declaration the running composition makes that
    /// the sealed binding is too old to have recorded.
    ///
    /// Separate from `compare` rather than folded into its return, because
    /// these are categorically not differences: a caller acting on drift
    /// must not act on these, and a policy refusing on drift must not
    /// refuse on these. Empty whenever the binding is new enough to speak
    /// — so a deployment that re-seals once never sees this again.
    let unrecorded (recorded: CompositionManifest) (observed: CompositionManifest) : CompositionUnrecorded list =
        if CompositionManifest.recordsCanonicalMethods recorded then
            []
        else
            let metricIds (manifest: CompositionManifest) =
                coerceEntries manifest.Metrics |> List.map _.Label |> Set.ofList

            let observedSelectors =
                CompositionManifest.canonicalMethods observed
                |> List.map (fun m -> m.MetricId, m.Selector)
                |> Map.ofList

            // Only metrics BOTH sides carry. A grounding-free composition
            // therefore reports nothing at all: with no metric recorded on
            // either side, no selector can exist to be silent about, and
            // that is a provable statement rather than a hedge.
            Set.intersect (metricIds recorded) (metricIds observed)
            |> Set.toList
            |> List.map (fun metricId -> CanonicalMethodUnrecorded(metricId, Map.tryFind metricId observedSelectors))

    // ─── Verifying ───────────────────────────────────────────────────

    /// Verify the seal over a composition binding.
    let private verifyBindingSeal
        (sealer: IDeployRecordSealer)
        (sealedBinding: SealedCompositionBinding)
        : Async<Result<unit, string list>> =
        async {
            let scheme = sealer.Scheme()

            if sealedBinding.Seal.Scheme <> scheme then
                return
                    Error [
                        $"composition binding seal scheme mismatch: verifier owns '{scheme}', seal was minted as '{sealedBinding.Seal.Scheme}'"
                    ]
            else
                let bytes = bindingCanonicalBytes sealedBinding.Binding

                match! sealer.VerifySeal(bytes, sealedBinding.Seal) with
                | Ok() -> return Ok()
                | Error reason -> return Error [ $"composition binding seal does not cover this binding: {reason}" ]
        }

    /// The four questions in the file header, without the Phase 678
    /// retirement question. Private: every caller reaches it through
    /// `verifyWithRetirement`, which is the whole check.
    let private verifyUnretired (options: BootVerificationOptions) (observed: CompositionManifest) = async {
        match options.Record, options.Binding with
        | None, _ ->
            return
                BootVerificationVerdict.Unsealed
                    "this deployment was started without a sealed deploy record, so there is nothing to verify the running composition against"
        | Some _, None ->
            return
                BootVerificationVerdict.Unsealed
                    "this deployment holds a sealed deploy record but no sealed composition binding, so the running composition cannot be compared with the sealed one"
        | Some sealedRecord, Some sealedBinding ->
            let! recordResult = DeployRecords.verify options.Sealer options.Locate options.Transcript sealedRecord

            let! bindingSealResult = verifyBindingSeal options.Sealer sealedBinding

            let recordFailures =
                match recordResult with
                | Ok() -> []
                | Error failures -> failures

            let bindingFindings = [
                match bindingSealResult with
                | Ok() -> ()
                | Error findings -> yield! findings

                let recordDigest =
                    sealedRecord.Record
                    |> DeployRecord.coerce
                    |> DeployRecords.canonicalBytes
                    |> DeployRecords.digestBytes

                if
                    not (
                        String.Equals(
                            sealedBinding.Binding.DeployRecordDigest,
                            recordDigest,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                then
                    $"composition binding belongs to a different deploy record: binding names {sealedBinding.Binding.DeployRecordDigest}, this record digests to {recordDigest}"
            ]

            if not (List.isEmpty recordFailures && List.isEmpty bindingFindings) then
                return BootVerificationVerdict.VerificationFailed(recordFailures, bindingFindings)
            else
                match compare sealedBinding.Binding.Composition observed with
                | [] ->
                    // Phase 694 — drift first, then what could not be
                    // compared. A drifted verdict already names an
                    // actionable difference whose remedy (re-seal the
                    // binding from the running composition) is the same
                    // act that closes the unrecorded gap, so precedence
                    // costs the reader nothing and keeps the verdict
                    // single-subject.
                    match unrecorded sealedBinding.Binding.Composition observed with
                    | [] -> return BootVerificationVerdict.Verified
                    | items -> return BootVerificationVerdict.VerifiedUnrecorded items
                | drift -> return BootVerificationVerdict.Drifted drift
    }

    /// Phase 678 — the four questions in the file header, preceded by a
    /// fifth: *has this deployment been retired?*
    ///
    /// **The retirement is a parameter rather than a field on
    /// `BootVerificationOptions`.** Widening that record would retype its
    /// constructor and break every consumer that builds one literally, for
    /// a feature no existing consumer supplies — the repo's standing
    /// design corollary, and the same call Phase 656 and Phase 657 each
    /// made for their own records. `verify` below delegates here with
    /// `None`, so a deployment that never retires is byte-for-byte what it
    /// was (GP 11 / GP 13).
    ///
    /// **Retirement is asked FIRST, and answered only when it BINDS this
    /// record.** First, because a decommissioned deployment is the single
    /// most actionable fact available and reporting it behind a drift list
    /// would bury it. Only when bound, because a retirement naming another
    /// deploy says nothing about this one — it has been presented
    /// alongside the wrong record, which is a finding in the same
    /// binding-mismatch family the composition binding already uses, and
    /// emphatically not a reason to refuse the boot of a deployment nobody
    /// retired.
    let verifyWithRetirement
        (options: BootVerificationOptions)
        (retirement: DeployRetirement option)
        (observed: CompositionManifest)
        : Async<BootVerificationVerdict> =
        async {
            let recordDigest =
                options.Record
                |> Option.map (fun sealedRecord ->
                    sealedRecord.Record
                    |> DeployRecord.coerce
                    |> DeployRecords.canonicalBytes
                    |> DeployRecords.digestBytes)

            match recordDigest, retirement with
            | Some digest, Some retirement when DeployRetirement.bindsRecord digest retirement ->
                return BootVerificationVerdict.Retired retirement
            | _ ->
                let misbound = [
                    match recordDigest, retirement with
                    | Some digest, Some retirement ->
                        $"a retirement was supplied for a different deploy record: it retires {retirement.DeployRecordDigest}, this record digests to {digest}"
                    | None, Some retirement ->
                        $"a retirement was supplied with no sealed deploy record to bind it to: it retires {retirement.DeployRecordDigest}"
                    | _, None -> ()
                ]

                let! verdict = verifyUnretired options observed

                match verdict, misbound with
                | _, [] -> return verdict
                | BootVerificationVerdict.VerificationFailed(failures, findings), _ ->
                    return BootVerificationVerdict.VerificationFailed(failures, findings @ misbound)
                | _, _ -> return BootVerificationVerdict.VerificationFailed([], misbound)
        }

    /// Ask the four questions in the file header and return the verdict.
    ///
    /// Pure with respect to the process: it decides nothing and refuses
    /// nothing. `run` is what turns a verdict into a decision.
    let verify (options: BootVerificationOptions) (observed: CompositionManifest) : Async<BootVerificationVerdict> =
        verifyWithRetirement options None observed

    /// Record a verdict through the audit seam.
    ///
    /// Emitted as an ordinary `AuditEvent`, so whichever sinks the
    /// deployment composed receive it and this file depends on none of
    /// them. A deployment running a hash-chained ledger gets a
    /// chain-covered boot verdict for free; one running no sink at all
    /// still gets the row in its own audit trail.
    let recordVerdict (options: BootVerificationOptions) (result: BootVerificationResult) : Async<unit> = async {
        match options.AuditLog with
        | None -> ()
        | Some auditLog ->
            let payload: CompositionVerificationRecordedPayload = {
                Verdict = BootVerificationVerdict.label result.Verdict
                Profile = CompositionProfile.label result.Profile
                Policy = BootVerificationPolicy.label result.Policy
                RefusedStart = result.RefusedStart
                Findings = BootVerificationVerdict.findings result.Verdict
                Summary = BootVerificationVerdict.describe result.Verdict
                OccurredAt = DateTimeOffset.UtcNow
            }

            do! auditLog.Record(options.ScopeId, AuditEvent.CompositionVerificationRecorded payload)
    }

    /// Phase 678 — run the preflight against a deployment that may have
    /// been retired, record the verdict, and decide whether the process
    /// may serve.
    ///
    /// `Ok` means serve; `Error` means refuse to start. Both carry the
    /// same result value, because the caller wants the verdict either
    /// way — a refusal an operator cannot read the reason for is a
    /// refusal they will disable.
    ///
    /// The verdict is recorded on both arms, before the decision is
    /// returned. A refusal that never reached the audit trail because the
    /// process stopped first is the one an incident review most needs.
    ///
    /// **`Retired` refuses under EVERY policy, log-and-serve included**,
    /// and that is the one place this file departs from the policy ladder
    /// it otherwise honours exactly. Log-and-serve exists so a deployment
    /// can adopt a check before it knows whether it passes — a grace
    /// period for a check that might be wrong about a deployment that is
    /// fine. A retirement is not that: it is a signed statement, made by
    /// the deployment's own key holder, that this deployment is over.
    /// There is nothing for an operator to watch and grow confident about,
    /// and a policy under which a decommissioned engagement keeps serving
    /// is a policy that makes the certificate a lie.
    ///
    /// `run` above is this with `None`, so nothing changes for a
    /// deployment that supplies no retirement.
    let runWithRetirement
        (options: BootVerificationOptions)
        (retirement: DeployRetirement option)
        (observed: CompositionManifest)
        : Async<Result<BootVerificationResult, BootVerificationResult>> =
        async {
            let policy = CompositionProfile.effectivePolicy options.Profile options.Policy
            let! verdict = verifyWithRetirement options retirement observed

            let refusedStart =
                match verdict with
                | BootVerificationVerdict.Retired _ -> true
                | _ ->
                    match policy with
                    | BootVerificationPolicy.LogAndServe -> false
                    | BootVerificationPolicy.RefuseOnDrift -> not (BootVerificationVerdict.isAffirmative verdict)

            let result = {
                Verdict = verdict
                Profile = options.Profile
                Policy = policy
                RefusedStart = refusedStart
            }

            do! recordVerdict options result

            return if refusedStart then Error result else Ok result
        }

    /// Run the preflight, record the verdict, and decide whether the
    /// process may serve. `runWithRetirement` with no retirement supplied
    /// — unchanged behaviour for every deployment that has not
    /// decommissioned.
    let run
        (options: BootVerificationOptions)
        (observed: CompositionManifest)
        : Async<Result<BootVerificationResult, BootVerificationResult>> =
        runWithRetirement options None observed

// ─── The verified composition profile ────────────────────────────────

[<RequireQualifiedAccess>]
module VerifiedCompositionProfile =

    /// Render a capability as `effect/determinism/readiness` for an audit
    /// payload. Stable and lowercase; the axis vocabulary is Phase 282's.
    let renderCapability (capability: CompanionCapability) : string =
        let effect =
            match capability.Effect with
            | Pure -> "pure"
            | Effecting -> "effecting"

        let determinism =
            match DeterminismSource.factors capability.Determinism |> Set.toList with
            | [] -> "deterministic"
            | factors -> factors |> List.map DeterminismFactor.toWireString |> String.concat "+"

        let readiness =
            match capability.Readiness with
            | DistributedReady -> "distributed-ready"
            | DevOnly -> "dev-only"

        $"{effect}/{determinism}/{readiness}"

    /// The audit payload for a refused capability access.
    let refusalPayload (profile: CompositionProfile) (denial: CapabilityDenial) : CompositionCapabilityRefusedPayload = {
        Component = denial.Component.Value
        Required = renderCapability denial.Required
        Declared = renderCapability denial.Declared
        Reason = denial.Reason
        Profile = CompositionProfile.label profile
        OccurredAt = DateTimeOffset.UtcNow
    }

    /// Put a refusal on the audit path, as an awaitable.
    ///
    /// The emission itself, separate from how it is scheduled. A caller
    /// that needs to observe the write — a conformance check, an operator
    /// tool draining a refusal before it reports — awaits this;
    /// `auditingObserver` is the same call started fire-and-forget.
    let recordRefusal
        (auditLog: IAuditLog)
        (scopeId: string)
        (profile: CompositionProfile)
        (denial: CapabilityDenial)
        : Async<unit> =
        auditLog.Record(scopeId, AuditEvent.CompositionCapabilityRefused(refusalPayload profile denial))

    /// The deny observer that puts a refusal on the audit path.
    ///
    /// Fire-and-forget, matching `IAuditLog.Record`'s own best-effort
    /// contract: a refusal is already fail-closed by the time this runs,
    /// and blocking the refusing call site on an audit write would make
    /// the security control's cost depend on the audit backend's latency.
    let auditingObserver
        (auditLog: IAuditLog)
        (scopeId: string)
        (profile: CompositionProfile)
        : CapabilityDenial -> unit =
        fun denial -> recordRefusal auditLog scopeId profile denial |> Async.Start

    /// Resolve the capability gate a composition runs under.
    ///
    /// Under `Standard` an absent signature leaves the gate `disabled` —
    /// the pre-657 behaviour, unchanged. Under `Verified` an absent
    /// signature is **refused**: a mandatory gate with nothing to check
    /// against would grant everything while presenting as enforcement,
    /// which is worse than no gate at all because it is believed.
    let resolveGate
        (profile: CompositionProfile)
        (onDeny: CapabilityDenial -> unit)
        (signature: CapabilitySignature option)
        : Result<ICompositionCapabilityGate, CompositionProfileRefusal> =
        match profile, signature with
        | CompositionProfile.Standard, None -> Ok CompositionCapabilityGate.disabled
        | CompositionProfile.Standard, Some declared -> Ok(CompositionCapabilityGate.create onDeny declared)
        | CompositionProfile.Verified, None -> Error CapabilityGateUndeclared
        | CompositionProfile.Verified, Some declared -> Ok(CompositionCapabilityGate.create onDeny declared)

    /// The mandatory gate with its refusals already on the audit path —
    /// the one call a verified composition makes.
    let auditedGate
        (auditLog: IAuditLog)
        (scopeId: string)
        (profile: CompositionProfile)
        (signature: CapabilitySignature option)
        : Result<ICompositionCapabilityGate, CompositionProfileRefusal> =
        resolveGate profile (auditingObserver auditLog scopeId profile) signature

    // ─── Phase 688 — the seam-authority arm of the profile ────────────

    /// Every component that appears in the capability signature but has
    /// declared no reachable-seam set, ordinally sorted so the refusal
    /// message is deterministic.
    ///
    /// The signature is the roster deliberately: it is the set of
    /// components the composition has already declared an envelope for,
    /// so "declared an effect envelope but no seam set" is exactly the
    /// half-declared state the verified profile must not accept. A
    /// component absent from both is invisible to the effect gate too,
    /// and Phase 300's default-deny already covers it.
    let undeclaredSeamComponents (signature: CapabilitySignature) (grants: SeamGrantSignature) : string list =
        signature
        |> Map.toList
        |> List.map fst
        |> List.filter (fun componentId -> not (SeamGrant.isDeclared (SeamGrant.resolve grants componentId)))
        |> List.map ComponentId.value
        |> List.sortWith (fun a b -> System.String.CompareOrdinal(a, b))

    /// Resolve the **seam-authority** gate a composition runs under.
    ///
    /// Under `Standard` this is additive by construction: with no grant
    /// signature the returned gate grants every seam, so its decisions are
    /// exactly `resolveGate`'s and a composition that declares nothing is
    /// byte-for-byte unchanged (GP 11). Under `Verified` a missing grant
    /// signature — or a component in the envelope that declared no seams —
    /// is **refused**, for the same reason `CapabilityGateUndeclared`
    /// exists: a mandatory seam check with nothing declared would permit
    /// every seam while presenting as enforcement, which is worse than no
    /// check because it is believed.
    let resolveSeamGate
        (profile: CompositionProfile)
        (onDeny: CapabilityDenial -> unit)
        (signature: CapabilitySignature option)
        (grants: SeamGrantSignature option)
        : Result<ISeamAuthorityGate, CompositionProfileRefusal> =
        match profile, signature, grants with
        | CompositionProfile.Standard, None, _ -> Ok SeamAuthorityGate.disabled
        | CompositionProfile.Standard, Some declared, None ->
            Ok(SeamAuthorityGate.unrestricted (CompositionCapabilityGate.create onDeny declared))
        | CompositionProfile.Standard, Some declared, Some declaredSeams ->
            Ok(SeamAuthorityGate.create onDeny declared declaredSeams)
        | CompositionProfile.Verified, None, _ -> Error CapabilityGateUndeclared
        | CompositionProfile.Verified, Some _, None -> Error(SeamGrantsUndeclared [])
        | CompositionProfile.Verified, Some declared, Some declaredSeams ->
            match undeclaredSeamComponents declared declaredSeams with
            | [] -> Ok(SeamAuthorityGate.create onDeny declared declaredSeams)
            | undeclared -> Error(SeamGrantsUndeclared undeclared)

    /// The mandatory seam gate with its refusals already on the audit path
    /// — the one call a verified composition makes for the outbound half,
    /// mirroring `auditedGate` for the effect half.
    let auditedSeamGate
        (auditLog: IAuditLog)
        (scopeId: string)
        (profile: CompositionProfile)
        (signature: CapabilitySignature option)
        (grants: SeamGrantSignature option)
        : Result<ISeamAuthorityGate, CompositionProfileRefusal> =
        resolveSeamGate profile (auditingObserver auditLog scopeId profile) signature grants

    /// Bind an external-compute dispatcher to the profile.
    ///
    /// Under `Verified` the dispatcher is wrapped in Phase 478's
    /// `ExecutionProfileGate`, so an `Isolated` spec a backend does not
    /// declare the posture for is refused before the payload leaves the
    /// process. Under `Standard` the dispatcher is returned exactly as
    /// handed over — no decorator, no branch, no allocation (GP 13).
    ///
    /// This is what makes the verified profile the isolated-execution
    /// profile's enforcement layer rather than a second thing to
    /// remember to switch on.
    let enforceExecutionProfile
        (profile: CompositionProfile)
        (dispatcher: IExternalComputeDispatcher)
        : IExternalComputeDispatcher =
        match profile with
        | CompositionProfile.Standard -> dispatcher
        | CompositionProfile.Verified -> ExecutionProfileGate.enforce dispatcher

    /// Check a backend can honour the execution profile it will be asked
    /// for, at composition time rather than at first submission.
    ///
    /// Under `Standard` the question is not asked — Phase 478's own
    /// per-submission check remains the gate. Under `Verified` a backend
    /// that will be handed `Isolated` work and does not assert the
    /// clauses is refused up front, naming every clause it is missing.
    let verifyIsolation
        (profile: CompositionProfile)
        (backend: string)
        (execution: ExecutionProfile)
        (posture: IsolationPosture)
        : Result<unit, CompositionProfileRefusal> =
        match profile with
        | CompositionProfile.Standard -> Ok()
        | CompositionProfile.Verified ->
            if IsolationPosture.honours execution posture then
                Ok()
            else
                Error(IsolationPostureShortfall(backend, IsolationPosture.shortfall posture))