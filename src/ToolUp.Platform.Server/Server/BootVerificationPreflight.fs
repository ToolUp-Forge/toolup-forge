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

// ─── Verdict ─────────────────────────────────────────────────────────

/// What the boot verification concluded.
[<RequireQualifiedAccess>]
type BootVerificationVerdict =
    /// Every question the preflight asked was answered affirmatively:
    /// the record is sealed, its artifacts match, the binding belongs to
    /// it, and the running composition is the one it recorded.
    | Verified
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

[<RequireQualifiedAccess>]
module BootVerificationVerdict =
    /// Stable lowercase label for logs / audit payloads / dashboards.
    let label =
        function
        | BootVerificationVerdict.Verified -> "verified"
        | BootVerificationVerdict.Unsealed _ -> "unsealed"
        | BootVerificationVerdict.VerificationFailed _ -> "unverified"
        | BootVerificationVerdict.Drifted _ -> "drifted"

    /// `true` only for the affirmative verdict. Everything else is a
    /// reason not to serve under a refusing policy.
    let isAffirmative =
        function
        | BootVerificationVerdict.Verified -> true
        | _ -> false

    /// One rendered line per finding — the operator-facing detail behind
    /// the label. Empty for `Verified`.
    let findings =
        function
        | BootVerificationVerdict.Verified -> []
        | BootVerificationVerdict.Unsealed reason -> [ reason ]
        | BootVerificationVerdict.VerificationFailed(failures, bindingFindings) ->
            (failures |> List.map DeployRecords.DeployRecordVerificationFailure.describe)
            @ bindingFindings
        | BootVerificationVerdict.Drifted drift -> drift |> List.map CompositionDrift.describe

    /// One-line account naming the verdict and how many findings sit
    /// behind it.
    let describe (verdict: BootVerificationVerdict) : string =
        match verdict with
        | BootVerificationVerdict.Verified ->
            "boot verification: the running composition is the one the sealed deploy record covers"
        | BootVerificationVerdict.Unsealed reason -> $"boot verification: nothing to verify against — {reason}"
        | BootVerificationVerdict.VerificationFailed _ ->
            let count = (findings verdict).Length
            $"boot verification: the sealed deploy record did not verify ({count} finding(s))"
        | BootVerificationVerdict.Drifted drift ->
            $"boot verification: the running composition differs from the sealed one ({drift.Length} difference(s))"

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

    /// Length-framed canonical text for a composition manifest.
    ///
    /// Framed with the same injective scheme Phase 656 uses, for the same
    /// reason: without it, two distinct compositions could canonicalise
    /// to the same text by concatenation and a digest over them would be
    /// meaningless.
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

        componentFindings @ knobFindings

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

    /// Ask the four questions in the file header and return the verdict.
    ///
    /// Pure with respect to the process: it decides nothing and refuses
    /// nothing. `run` is what turns a verdict into a decision.
    let verify (options: BootVerificationOptions) (observed: CompositionManifest) : Async<BootVerificationVerdict> = async {
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
                | [] -> return BootVerificationVerdict.Verified
                | drift -> return BootVerificationVerdict.Drifted drift
    }

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

    /// Run the preflight, record the verdict, and decide whether the
    /// process may serve.
    ///
    /// `Ok` means serve; `Error` means refuse to start. Both carry the
    /// same result value, because the caller wants the verdict either
    /// way — a refusal an operator cannot read the reason for is a
    /// refusal they will disable.
    ///
    /// The verdict is recorded on both arms, before the decision is
    /// returned. A refusal that never reached the audit trail because the
    /// process stopped first is the one an incident review most needs.
    let run
        (options: BootVerificationOptions)
        (observed: CompositionManifest)
        : Async<Result<BootVerificationResult, BootVerificationResult>> =
        async {
            let policy = CompositionProfile.effectivePolicy options.Profile options.Policy
            let! verdict = verify options observed

            let refusedStart =
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