// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System
open System.Security.Cryptography
open System.Text
open System.Text.Json.Nodes
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform.ConfigValidation

// ─── Phase 488 — the appliance deployment profile ─────────────────────
//
// A single-tenant container running IN SITU — inside the customer's own
// infrastructure, on their data, operated remotely by whoever supplied it
// — is a deployment class every other posture in this SDK assumes away.
// `ServerlessHost` and `ProcessProfile` describe where the process runs;
// `ReplicaCount` describes how many; none of them describe a deployment
// that **cannot reach the party operating it**. That single constraint
// changes four things at once, which is why they arrive together here
// rather than as four unrelated knobs:
//
//   * **Boot (488.A)** — nothing on the startup path may depend on a
//     remote call. Preflight validators that probe an external dependency
//     are the offenders, and they are exactly the class Phase 9m/585
//     already isolates, so the fix composes with what exists instead of
//     re-litigating it.
//   * **Upgrade (488.B)** — a build arrives as a file, not a pull, so its
//     authenticity has to be established from the artefact itself against
//     its Phase 182 provenance, and the operator has to be able to check
//     a new version BEFORE flipping to it.
//   * **Telemetry (488.C)** — health has to get out; content must not.
//     That is `OperationalTelemetryDiode`, in its own file.
//   * **Support (488.D)** — diagnosis has to be possible without the
//     supplying party ever holding the data, which inverts the usual
//     "send us a dump" flow: the operator generates, inspects, and
//     forwards; nobody pulls.
//
// **A profile, not a default (GP 13).** `ApplianceProfile.identity` is
// `ConnectedAppliance` with no skew allowance, and every registration
// helper here is a no-op under it: a deployment that never mentions this
// file composes a byte-for-byte identical `services`. There is no new
// `ServerConfig` field — the profile is a value a composition root passes
// where it wants the behaviour, which is the same call Phase 434 made and
// for the same reason (a field on `ServerConfig` is a breaking
// constructor change and a public-API baseline removal).
//
// **Generic and vendor-neutral (GP 1).** No container runtime, no
// registry, no crypto stack. Artefact verification arrives as the
// structural `VerifyDetachedJws` function seam — the same decoupling
// Phase 182's `Sbom.SignArtefact` uses so the Build package needs no
// reference to a signing implementation. A deployment that has composed
// an artefact verifier adapts it at its own call site.

// ─── 488.A — offline-tolerant boot posture ────────────────────────────

/// Whether this deployment has declared that it runs with no reachable
/// outbound network.
///
/// This is a DECLARATION, not a probe. Detecting "am I offline?" at
/// startup would require reaching for something, which is the behaviour
/// being eliminated — and a probe that fails is indistinguishable from a
/// probe that is slow, so the appliance would either block on it or guess.
/// The operator knows the answer and says it.
type AppliancePosture =
    /// Default and identity — an ordinary networked deployment. Every
    /// registration helper in this file is a no-op under it (GP 11).
    | ConnectedAppliance
    /// An in-situ appliance with no expected outbound reach.
    /// External-probe-class preflight validators are downgraded from
    /// `Error` to `Warning`, because a storage sentinel that cannot reach
    /// a cloud endpoint is describing the deployment's own topology
    /// rather than a fault.
    | DeclaredOffline

/// The appliance's declared posture, as a value a composition root passes
/// to the registration helpers here.
type ApplianceProfile = {
    Posture: AppliancePosture
    /// How far the appliance's clock may legitimately be from a
    /// counterparty's before a time-sensitive comparison should be
    /// treated as suspect.
    ///
    /// **Why this is declared rather than fixed.** An air-gapped
    /// appliance frequently has no NTP peer it is allowed to reach, so its
    /// clock drifts, and every default freshness window in this SDK —
    /// notably the Stripe webhook verifier's five minutes and the JWT
    /// peer layer's — was chosen for a deployment that syncs. An
    /// appliance operator who knows their host drifts by a quarter hour
    /// says so, once, here, instead of discovering it as intermittent
    /// signature rejections. `TimeSpan.Zero` (the identity) means "no
    /// additional allowance", i.e. exactly today's behaviour.
    ClockSkewTolerance: TimeSpan
}

[<RequireQualifiedAccess>]
module ApplianceProfile =

    /// A conventional starting allowance for an appliance with no clock
    /// peer. Not a default — `identity` carries `TimeSpan.Zero` — but the
    /// number an operator who has not measured their drift should start
    /// from.
    let DefaultClockSkewTolerance = TimeSpan.FromMinutes 5.0

    /// The identity: an ordinary connected deployment with no extra
    /// allowance. What a composition that mentions none of this behaves
    /// as (GP 11).
    let identity: ApplianceProfile = {
        Posture = ConnectedAppliance
        ClockSkewTolerance = TimeSpan.Zero
    }

    /// A declared-offline appliance with the conventional skew allowance.
    let offline: ApplianceProfile = {
        Posture = DeclaredOffline
        ClockSkewTolerance = DefaultClockSkewTolerance
    }

    /// A declared-offline appliance with a measured skew allowance.
    let offlineWithSkew (tolerance: TimeSpan) : ApplianceProfile = {
        Posture = DeclaredOffline
        ClockSkewTolerance =
            (if tolerance < TimeSpan.Zero then
                 TimeSpan.Zero
             else
                 tolerance)
    }

    /// Whether the deployment declared itself offline.
    let isOffline (profile: ApplianceProfile) : bool = profile.Posture = DeclaredOffline

    /// Whether two instants are within the declared tolerance of each
    /// other — the comparison a time-sensitive check on an appliance
    /// should make instead of an exact window.
    ///
    /// Symmetric by construction (the absolute difference), because an
    /// appliance clock can be behind as easily as ahead and a one-sided
    /// allowance would reject half the drift it was added to absorb.
    let withinSkew (profile: ApplianceProfile) (a: DateTimeOffset) (b: DateTimeOffset) : bool =
        let drift = a - b
        let magnitude = if drift < TimeSpan.Zero then -drift else drift
        magnitude <= profile.ClockSkewTolerance

    /// Widen a freshness window by the declared tolerance. For the
    /// verifier-shaped call sites that already carry a window
    /// (`WebhookSigner`'s five minutes, a nonce lifetime): an appliance
    /// with a declared drift needs `window + tolerance`, not a
    /// hand-edited constant.
    let widenWindow (profile: ApplianceProfile) (window: TimeSpan) : TimeSpan = window + profile.ClockSkewTolerance

/// Wraps an external-probe-class `IConfigValidator` so that, under a
/// declared-offline posture, an `Error` becomes a `Warning` naming the
/// downgrade.
///
/// **Why a decorator and not a flag on the aggregator.** The Phase 9m
/// aggregator's contract is "any `Error` aborts", and the Phase 585
/// marker classification is derived from the validators themselves
/// precisely so no central list can drift. Adding an offline mode inside
/// `validate` would put a second bypass lever beside `SkipPreflight` in
/// the one function whose job is to refuse to be bypassed. Decorating the
/// instances instead leaves the aggregator's semantics untouched: it still
/// aborts on any `Error` it sees, and there simply is no `Error` from a
/// probe whose dependency the operator has declared unreachable.
///
/// **The decorator carries neither marker deliberately.** A
/// security-class or structural-class validator is never decorated (see
/// `ApplianceProfile.offlineTolerantRegistration`), so an
/// identity-spoofing guard or a composition-integrity invariant still
/// aborts an offline appliance's boot. Being offline is a reason a
/// storage sentinel cannot answer; it is not a reason to start with
/// colliding component ids.
///
/// **Three ways a probe fails on an unreachable network, and all three
/// have to be caught here.** A validator that returns `Error` is only the
/// tidiest of them:
///
///   1. It **returns `Error`** ("connection refused").
///   2. It **throws** — which is what a real socket does. The Phase 9m
///      aggregator catches throws itself and converts them to `Error`, so
///      a decorator that only pattern-matched the returned value would
///      let the most likely failure mode straight through to an abort.
///      This was not hypothetical: the acceptance test for offline boot
///      failed on exactly that hole before this `try` existed.
///   3. It **hangs** — a TCP connect to an unroutable address commonly
///      does, until something times it out. Here the decorator cannot
///      rely on the aggregator's timeout, because F# async CANCELLATION
///      is not an exception and does not pass through a `try/with`: the
///      aggregator's `CancellationTokenSource` would fire, the outcome
///      would be `Error("validator exceeded timeout")`, and the boot
///      would abort with nothing this type could do about it. So the
///      decorator imposes its OWN bounded wait via `Async.StartChild`
///      (whose expiry IS a catchable `TimeoutException`) and reports a
///      slightly longer `Timeout` upward, so it observes the non-answer
///      itself before the aggregator gives up on it.
[<Sealed>]
type OfflineTolerantValidator(inner: IConfigValidator) =

    /// Margin between the decorator's own bounded wait and the timeout it
    /// reports to the aggregator. Small — it only has to be enough that
    /// the child expires first.
    static let margin = TimeSpan.FromMilliseconds 500.0

    /// The bounded wait imposed on the inner probe. Clamped below the
    /// aggregator's global budget as well as the probe's own declared
    /// timeout: a validator declaring 60s gets 10s from the aggregator
    /// regardless, and a child budget above that would never expire in
    /// time to be caught.
    let innerBudget =
        let ceiling = ConfigValidatorAggregator.aggregatorBudget - margin

        if inner.Timeout > ceiling then ceiling
        elif inner.Timeout < TimeSpan.Zero then TimeSpan.Zero
        else inner.Timeout

    /// The innermost message of a possibly-aggregated exception — an
    /// `AggregateException` renders as "One or more errors occurred (…)",
    /// which tells an operator nothing about what could not be reached.
    static let rec innermost (ex: exn) : string =
        match ex with
        | :? AggregateException as aggregate ->
            match Seq.tryHead (aggregate.Flatten().InnerExceptions) with
            | Some inner' -> innermost inner'
            | None -> aggregate.Message
        | _ when not (isNull ex.InnerException) -> innermost ex.InnerException
        | _ -> ex.Message

    let downgraded (detail: string) =
        Warning(
            sprintf
                "%s — downgraded from Error by the declared-offline appliance posture (ApplianceProfile.Posture = DeclaredOffline). An external-probe-class validator cannot reach its dependency on an appliance with no outbound network; that is the declared topology, not a fault. Re-check this probe if the appliance is ever given network reach."
                detail
        )

    /// The validator this wraps — exposed so the registration rewrite can
    /// be asserted, and so double-wrapping is detectable.
    member _.Inner = inner

    /// The bounded wait this decorator imposes on the inner probe.
    member _.InnerBudget = innerBudget

    interface IConfigValidator with
        member _.Name = inner.Name

        // Deliberately longer than the child budget, so the decorator's own
        // wait expires first and the hang arrives as a catchable
        // TimeoutException rather than as the aggregator's cancellation.
        member _.Timeout = innerBudget + margin

        member _.Validate() = async {
            try
                let! child = Async.StartChild(inner.Validate(), int innerBudget.TotalMilliseconds)

                match! child with
                | Error message -> return downgraded message
                | result -> return result
            with
            | :? TimeoutException ->
                return downgraded (sprintf "probe did not answer within %dms" (int innerBudget.TotalMilliseconds))
            | ex -> return downgraded ("validator threw: " + innermost ex)
        }

/// **488.A — the boot-posture gate.** One rule in the Phase 294
/// `CompositionRuleDescriptor` vocabulary, plus the registration that
/// downgrades external probes under a declared-offline posture.
[<RequireQualifiedAccess>]
module ApplianceBootPosture =

    /// Stable `IConfigValidator.Name` for the boot-posture gate.
    /// Structural-class — `SkipPreflight` does not bypass it.
    [<Literal>]
    let ValidatorName = "appliance-boot-posture"

    /// Stable rule code: a declared-offline appliance composed a component
    /// that declares an outbound endpoint knob.
    [<Literal>]
    let ExternalEndpointRule = "appliance-offline-external-endpoint"

    /// Phase 294 — the introspectable rule manifest.
    ///
    /// `DefectWarning`, not `DefectError`, and the distinction is
    /// load-bearing. A `UriKnob` on an offline appliance is *usually*
    /// correct: the appliance composes a database, an object store, and an
    /// identity provider that all live in the same customer network, and
    /// every one of them is a URI the deployment must supply. What the
    /// rule can honestly say is "these are the endpoints that must resolve
    /// from inside the container, and nobody has confirmed they do" —
    /// which is a pre-install checklist, the thing Phase 432 exists to
    /// produce. Failing boot on it would refuse every real appliance.
    let ruleManifest: CompositionRuleDescriptor list = [
        {
            Code = ExternalEndpointRule
            Severity = DefectWarning
            Description =
                "A deployment declaring ApplianceProfile.Posture = DeclaredOffline composed one or more components whose Phase 432 requirements include a URI-typed config knob. Each named endpoint must resolve from inside the appliance container; an endpoint reachable only from the supplying party's network will fail at first use rather than at preflight."
        }
    ]

    /// Phase 585 — the same rule with its class. Structural: a pure
    /// in-memory join over declared requirements and the composed
    /// manifest, with nothing external to be down. That it reports on
    /// endpoints does not make it an external probe — it never dials one.
    let classifiedRuleManifest: ClassifiedCompositionRule list =
        ruleManifest
        |> List.map (fun rule -> {
            Code = rule.Code
            Severity = rule.Severity
            Description = rule.Description
            Class = StructuralRule
        })

    /// Every URI-typed config knob a COMPOSED component declares, derived
    /// from the Phase 432 `RequirementsSignature` joined against the Phase
    /// 280 manifest.
    ///
    /// Joined against the manifest rather than read straight off the
    /// signature so the report describes THIS composition: a signature
    /// carries declarations for components a given deployment may not have
    /// composed, and naming an endpoint requirement for an absent
    /// component is exactly the kind of noise that trains an operator to
    /// stop reading preflight output.
    let externalEndpointFindings
        (signature: RequirementsSignature)
        (manifest: CompositionManifest)
        : (ComponentId * ConfigRequirement) list =
        let composed =
            (manifest.Modules
             @ manifest.CompanionSlots
             @ manifest.DataTypes
             @ manifest.Tools
             @ manifest.Metrics
             @ manifest.Subjects)
            |> List.map _.Id
            |> List.distinct

        composed
        |> List.collect (fun componentId ->
            (ComponentRequirements.resolve signature componentId).Config
            |> List.filter (fun knob -> knob.KnobType = UriKnob)
            |> List.map (fun knob -> componentId, knob))
        |> List.sortBy (fun (componentId, knob) -> ComponentId.value componentId, knob.Path)

    /// The defects of a composition against its declared posture. Empty
    /// under `ConnectedAppliance` (the gate is dormant) and empty for a
    /// declared-offline appliance that composed no endpoint-bearing
    /// component.
    let defects
        (profile: ApplianceProfile)
        (signature: RequirementsSignature)
        (manifest: CompositionManifest)
        : CompositionDefect list =
        if not (ApplianceProfile.isOffline profile) then
            []
        else
            match externalEndpointFindings signature manifest with
            | [] -> []
            | findings ->
                let lines =
                    findings
                    |> List.map (fun (componentId, knob) ->
                        sprintf
                            "\n  • %s requires %s — %s"
                            (ComponentId.value componentId)
                            (ConfigRequirement.describe knob)
                            knob.Purpose)
                    |> String.concat ""

                [
                    {
                        RuleCode = ExternalEndpointRule
                        Severity = DefectWarning
                        Message =
                            sprintf
                                "This deployment declares an offline appliance posture, and %d composed component requirement(s) name an endpoint:%s\nConfirm each resolves from inside the appliance container before install. This is the Phase 432 pre-install checklist for an air-gapped topology, not a fault."
                                (List.length findings)
                                lines
                    }
                ]

    let private renderDefects (defects: CompositionDefect list) : string =
        defects
        |> List.map (fun d -> sprintf "[%s] %s" d.RuleCode d.Message)
        |> String.concat "\n"

    /// Translate boot-posture defects into a `ValidationResult`. The rule
    /// is warning-severity, so this never returns `Error` today — the
    /// `DefectError` arm is kept because the translation belongs to the
    /// defect list's severity, not to what the current rule set happens to
    /// declare.
    let toValidationResult (defects: CompositionDefect list) : ValidationResult =
        let errors = defects |> List.filter (fun d -> d.Severity = DefectError)

        if not errors.IsEmpty then
            Error(renderDefects errors)
        else
            match defects with
            | [] -> Ok
            | warnings -> Warning(renderDefects warnings)

    /// The structural-class `IConfigValidator` that runs the gate.
    type ApplianceBootPostureValidator
        (profile: ApplianceProfile, signature: RequirementsSignature, manifest: CompositionManifest) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async { return toValidationResult (defects profile signature manifest) }

        interface IStructuralClassValidator

    /// Registers the boot-posture gate. **Nothing is registered under
    /// `ConnectedAppliance`** (GP 13), so a deployment that does not
    /// declare an appliance posture composes an identical `services`.
    let serviceRegistration
        (profile: ApplianceProfile)
        (signature: RequirementsSignature)
        (manifest: CompositionManifest)
        : IServiceCollection -> IServiceCollection =
        fun services ->
            if ApplianceProfile.isOffline profile then
                services.AddSingleton<IConfigValidator>(
                    ApplianceBootPostureValidator(profile, signature, manifest) :> IConfigValidator
                )
                |> ignore

            services

    /// Rewrite every already-registered external-probe-class
    /// `IConfigValidator` into its offline-tolerant form.
    ///
    /// **Call this LAST**, after every companion has registered — the
    /// same ordering constraint `ConfigValidatorAggregator.validate`
    /// carries, and for the same reason: a validator registered after this
    /// runs is not rewritten and will abort the boot.
    ///
    /// **Classification is read from the validator, never from a list
    /// here.** `ConfigValidatorAggregator.classify` type-tests the two
    /// Phase 585 markers, so a security or structural validator authored
    /// tomorrow is excluded from the downgrade without anyone remembering
    /// to exclude it. An instance already wrapped is left alone, so the
    /// rewrite is idempotent.
    ///
    /// A factory-registered validator is skipped rather than rewritten:
    /// the aggregator itself refuses to run those (it reads
    /// `ImplementationInstance`), so there is nothing here to decorate and
    /// failing loudly is the aggregator's job, not this pass's.
    let offlineTolerantRegistration (profile: ApplianceProfile) : IServiceCollection -> IServiceCollection =
        fun services ->
            if not (ApplianceProfile.isOffline profile) then
                services
            else
                let indices =
                    services
                    |> Seq.indexed
                    |> Seq.filter (fun (_, descriptor) -> descriptor.ServiceType = typeof<IConfigValidator>)
                    |> Seq.choose (fun (index, descriptor) ->
                        match descriptor.ImplementationInstance with
                        | :? OfflineTolerantValidator -> None
                        | :? IConfigValidator as validator ->
                            match ConfigValidatorAggregator.classify validator with
                            | ConfigValidatorAggregator.ExternalProbeClass -> Some(index, validator)
                            | ConfigValidatorAggregator.SecurityClass
                            | ConfigValidatorAggregator.StructuralClass -> None
                        | _ -> None)
                    |> List.ofSeq

                for (index, validator) in indices do
                    services[index] <-
                        ServiceDescriptor.Singleton<IConfigValidator>(
                            OfflineTolerantValidator(validator) :> IConfigValidator
                        )

                services

// ─── 488.B — signed upgrade verification ──────────────────────────────

/// What a build claims about itself, in the shape Phase 182 emits: the
/// artefact's own digest, the digest of its CycloneDX SBOM, and the
/// detached JWS binding the artefact bytes to a signing key.
///
/// Both digests are carried, and the SBOM one is not redundant. The
/// artefact digest answers "are these the bytes that were signed"; the
/// SBOM digest answers "is this the dependency set that was reviewed".
/// An appliance that verified only the artefact would accept a rebuild
/// whose accompanying SBOM had been swapped for a cleaner one, and the
/// SBOM is the artefact an operator's own supply-chain policy reads.
type ArtefactProvenance = {
    /// The artefact's identity — a package or image id.
    ArtefactId: string
    /// The version this artefact claims to be.
    Version: string
    /// Lowercase hex SHA-256 over the artefact bytes.
    ArtefactSha256: string
    /// Lowercase hex SHA-256 over the CycloneDX SBOM bytes.
    SbomSha256: string
    /// The Phase 182 detached-JWS sidecar over the artefact bytes.
    DetachedJws: string
}

/// The verification seam: given artefact bytes and a detached JWS,
/// `Ok ()` or a failure description.
///
/// **A structural function, not an interface over a crypto stack** — the
/// same GP 1 decoupling `Sbom.SignArtefact` uses on the emitting side. A
/// deployment that has composed an artefact verifier adapts it at its own
/// call site:
///
/// ```fsharp skip=fragment
/// let verify : VerifyDetachedJws =
///     fun bytes jws -> async {
///         let signature = { KeyId = keyId; Algorithm = alg; SignedAt = signedAt; DetachedJws = jws }
///         let! result = verifier.Verify(bytes, signature)
///         return result |> Result.mapError VerificationError.describe
///     }
/// ```
type VerifyDetachedJws = byte[] -> string -> Async<Result<unit, string>>

/// Why an artefact was refused. Every case NAMES what did not match —
/// the acceptance criterion for 488.B is not "refused" but "refused with
/// the provenance mismatch named", because an appliance operator who
/// cannot tell a corrupted download from a wrong-version file from a
/// revoked key has to escalate to the supplying party to make any
/// progress, which is the dependency an appliance exists to avoid.
type ProvenanceMismatch =
    /// The artefact bytes do not hash to the declared digest.
    | ArtefactDigestMismatch of expected: string * actual: string
    /// The SBOM bytes do not hash to the declared digest.
    | SbomDigestMismatch of expected: string * actual: string
    /// The detached JWS did not verify against the artefact bytes.
    | SignatureRejected of reason: string
    /// A provenance field the check needs was absent or blank.
    | ProvenanceIncomplete of field: string

[<RequireQualifiedAccess>]
module ProvenanceMismatch =

    /// A one-line operator-readable description naming the mismatch.
    let describe (mismatch: ProvenanceMismatch) : string =
        match mismatch with
        | ArtefactDigestMismatch(expected, actual) ->
            sprintf
                "artefact digest mismatch: provenance declares sha256 %s, the artefact bytes hash to %s"
                expected
                actual
        | SbomDigestMismatch(expected, actual) ->
            sprintf "SBOM digest mismatch: provenance declares sha256 %s, the SBOM bytes hash to %s" expected actual
        | SignatureRejected reason -> sprintf "detached-JWS signature rejected: %s" reason
        | ProvenanceIncomplete field -> sprintf "provenance incomplete: %s is absent or blank" field

/// Name-only presence probes for the migrate-preview step — the Phase 432
/// preflight's dry-run shape.
///
/// **Neither probe returns a value.** They answer "is this name
/// provisioned", which is all a pre-flip check needs and all a preview
/// report can safely contain. This mirrors `SecretRequirement`'s own
/// design constraint: the type has no field a secret's value could
/// occupy, and the preview must not reintroduce one through the back
/// door of a probe signature.
type MigrationProbes = {
    /// `scope -> key -> present`. Existence only.
    SecretPresent: string -> string -> bool
    /// `knob path -> bound`. Existence only.
    ConfigBound: string -> bool
}

[<RequireQualifiedAccess>]
module MigrationProbes =

    /// Probes that report everything absent — the honest default for a
    /// preview run on a machine that is not the appliance, where every
    /// answer would be a guess. Reports every required name as a gap,
    /// which reads as "here is the full provisioning checklist".
    let noneProvisioned: MigrationProbes = {
        SecretPresent = fun _ _ -> false
        ConfigBound = fun _ -> false
    }

/// One requirement the incoming version needs and this appliance has not
/// provisioned. Names and classes only, via the Phase 432 `describe`
/// renderers.
type MigrationGap = {
    /// The component that requires it.
    Component: ComponentId
    /// `scope/key (class)` for a secret, `path (type)` for a knob.
    Requirement: string
    /// What the component does with it — the sentence that tells the
    /// operator what to go and provision.
    Purpose: string
}

/// Where an upgrade got to.
type UpgradeStage =
    /// Provenance verification failed. Nothing else ran — the migrate
    /// preview reads the candidate's declared requirements, and reading
    /// declarations out of an artefact whose authenticity is unproven is
    /// the same trust mistake in a smaller frame.
    | ProvenanceRefused
    /// Provenance verified, but the incoming version needs something this
    /// appliance has not been given. Flipping would boot a version whose
    /// Phase 432 preflight aborts.
    | MigrationBlocked
    /// Verified and fully provisioned. The operator may flip.
    | ReadyToFlip

[<RequireQualifiedAccess>]
module UpgradeStage =

    let toWireString (stage: UpgradeStage) : string =
        match stage with
        | ProvenanceRefused -> "provenance-refused"
        | MigrationBlocked -> "migration-blocked"
        | ReadyToFlip -> "ready-to-flip"

    /// Whether the operator may proceed to the flip.
    let mayFlip (stage: UpgradeStage) : bool = stage = ReadyToFlip

/// A build offered to an appliance, with everything the staging check
/// needs to decide about it without reaching the network.
type UpgradeCandidate = {
    Provenance: ArtefactProvenance
    /// The artefact bytes as received.
    ArtefactBytes: byte[]
    /// The accompanying CycloneDX SBOM bytes.
    SbomBytes: byte[]
    /// The INCOMING version's Phase 432 requirements — what the new build
    /// will demand at its own preflight. Read before the flip, which is
    /// the whole point: the alternative is discovering it from a crashed
    /// container after the switch.
    Requirements: RequirementsSignature
}

/// The staging report an operator reads before flipping.
type UpgradeStagingReport = {
    /// What the candidate claims to be.
    Candidate: ArtefactProvenance
    Stage: UpgradeStage
    /// Empty unless `Stage = ProvenanceRefused`.
    Mismatches: ProvenanceMismatch list
    /// Empty unless `Stage = MigrationBlocked`.
    Gaps: MigrationGap list
    /// The artefact to return to if the flip goes wrong: the previously
    /// verified build.
    ///
    /// **Rollback is a previous VERIFIED artefact, and nothing else.**
    /// `None` means there is no verified predecessor — a first install, or
    /// an appliance whose running build was never verified — and in that
    /// state a flip is one-way. That is worth surfacing in the report
    /// rather than discovering during an incident, which is why the field
    /// is here and not left to the runbook.
    RollbackTo: ArtefactProvenance option
}

/// **488.B — on-start verification and the pre-flip staging check.**
[<RequireQualifiedAccess>]
module ApplianceUpgrade =

    /// Stable `IConfigValidator.Name` for the on-start artefact
    /// verification.
    [<Literal>]
    let ValidatorName = "appliance-artefact-provenance"

    /// Lowercase hex SHA-256 — the digest form Phase 182 records and
    /// every artefact-provenance convention in this repo uses.
    let sha256Hex (bytes: byte[]) : string =
        use sha = SHA256.Create()
        sha.ComputeHash bytes |> Convert.ToHexString |> _.ToLowerInvariant()

    /// Case-insensitive hex comparison. Digests are quoted upper-case by
    /// some tooling and lower-case by others; a verifier that refused an
    /// artefact over the case of a hex digit would be refusing for the
    /// wrong reason, and the operator would have no way to tell.
    let private digestMatches (expected: string) (actual: string) =
        String.Equals(expected.Trim(), actual, StringComparison.OrdinalIgnoreCase)

    /// Verify a build against its declared Phase 182 provenance: both
    /// digests, then the detached-JWS signature.
    ///
    /// **Digests before signature, deliberately.** Hashing is local and
    /// cheap; the signature check may reach a key resolver. Ordering the
    /// local checks first means the common failure (a truncated or
    /// substituted file) is named without any external dependency at all,
    /// which is the behaviour an offline appliance needs.
    ///
    /// Returns EVERY mismatch found rather than the first: an operator
    /// staring at a refused upgrade at 2am should get the whole picture
    /// from one run.
    let verifyArtefact
        (verify: VerifyDetachedJws)
        (provenance: ArtefactProvenance)
        (artefactBytes: byte[])
        (sbomBytes: byte[])
        : Async<Result<unit, ProvenanceMismatch list>> =
        async {
            let blankFields = [
                if String.IsNullOrWhiteSpace provenance.ArtefactSha256 then
                    ProvenanceIncomplete "ArtefactSha256"
                if String.IsNullOrWhiteSpace provenance.SbomSha256 then
                    ProvenanceIncomplete "SbomSha256"
                if String.IsNullOrWhiteSpace provenance.DetachedJws then
                    ProvenanceIncomplete "DetachedJws"
            ]

            if not blankFields.IsEmpty then
                return Result.Error blankFields
            else
                let artefactActual = sha256Hex artefactBytes
                let sbomActual = sha256Hex sbomBytes

                let digestMismatches = [
                    if not (digestMatches provenance.ArtefactSha256 artefactActual) then
                        ArtefactDigestMismatch(provenance.ArtefactSha256.Trim(), artefactActual)
                    if not (digestMatches provenance.SbomSha256 sbomActual) then
                        SbomDigestMismatch(provenance.SbomSha256.Trim(), sbomActual)
                ]

                // `Result.Ok` / `Result.Error` are qualified throughout this
                // module: `open ToolUp.Platform.ConfigValidation` brings
                // `ValidationResult`'s own `Ok` and `Error` cases into scope,
                // and they shadow the `Result` ones.
                let! signatureMismatches = async {
                    try
                        match! verify artefactBytes provenance.DetachedJws with
                        | Result.Ok() -> return []
                        | Result.Error reason -> return [ SignatureRejected reason ]
                    with ex ->
                        return [ SignatureRejected("verifier threw: " + ex.Message) ]
                }

                match digestMismatches @ signatureMismatches with
                | [] -> return Result.Ok()
                | mismatches -> return Result.Error mismatches
        }

    /// A one-line-per-mismatch refusal summary naming every mismatch.
    let describeRefusal (provenance: ArtefactProvenance) (mismatches: ProvenanceMismatch list) : string =
        let lines =
            mismatches
            |> List.map (ProvenanceMismatch.describe >> sprintf "\n  • %s")
            |> String.concat ""

        sprintf
            "Artefact provenance verification REFUSED for %s %s:%s\nThe running build does not match the provenance it shipped with. Do not flip to it; return to the previously verified artefact."
            provenance.ArtefactId
            provenance.Version
            lines

    /// **Migrate preview** — the incoming version's required Phase 432
    /// requirements that this appliance has not provisioned.
    ///
    /// Pure and offline: it reads the candidate's DECLARED requirements
    /// and asks the injected probes whether each name is present. Only
    /// `RequiredRequirement` secrets and default-less knobs are reported
    /// — an optional credential's absence degrades a component, which is
    /// a legitimate configuration and not a blocker for a flip.
    let migrationPreview (probes: MigrationProbes) (requirements: RequirementsSignature) : MigrationGap list =
        ComponentRequirements.all requirements
        |> List.collect (fun reqs ->
            let secretGaps =
                reqs
                |> ComponentRequirements.requiredSecrets
                |> List.filter (fun secret -> not (probes.SecretPresent secret.Scope secret.Key))
                |> List.map (fun secret -> {
                    Component = reqs.Component
                    Requirement = SecretRequirement.describe secret
                    Purpose = secret.Purpose
                })

            let configGaps =
                reqs
                |> ComponentRequirements.requiredConfig
                |> List.filter (fun knob -> not (probes.ConfigBound knob.Path))
                |> List.map (fun knob -> {
                    Component = reqs.Component
                    Requirement = ConfigRequirement.describe knob
                    Purpose = knob.Purpose
                })

            secretGaps @ configGaps)

    /// The full pre-flip staging check: **verify, then migrate-preview,
    /// then flip** — the three-step sequence 488.B specifies, with the
    /// flip left to the operator.
    ///
    /// Nothing here switches anything. The check produces a report and a
    /// verdict; the flip is a container-runtime action taken by whoever
    /// operates the appliance, deliberately outside this SDK's reach. An
    /// upgrade path that could flip itself would be a remote-callback
    /// startup dependency wearing a different hat.
    let stage
        (verify: VerifyDetachedJws)
        (probes: MigrationProbes)
        (previousVerified: ArtefactProvenance option)
        (candidate: UpgradeCandidate)
        : Async<UpgradeStagingReport> =
        async {
            let! verification = verifyArtefact verify candidate.Provenance candidate.ArtefactBytes candidate.SbomBytes

            match verification with
            | Result.Error mismatches ->
                return {
                    Candidate = candidate.Provenance
                    Stage = ProvenanceRefused
                    Mismatches = mismatches
                    Gaps = []
                    RollbackTo = previousVerified
                }
            | Result.Ok() ->
                match migrationPreview probes candidate.Requirements with
                | [] ->
                    return {
                        Candidate = candidate.Provenance
                        Stage = ReadyToFlip
                        Mismatches = []
                        Gaps = []
                        RollbackTo = previousVerified
                    }
                | gaps ->
                    return {
                        Candidate = candidate.Provenance
                        Stage = MigrationBlocked
                        Mismatches = []
                        Gaps = gaps
                        RollbackTo = previousVerified
                    }
        }

    /// Render a staging report as the operator-facing runbook output.
    let describeStaging (report: UpgradeStagingReport) : string =
        let rollback =
            match report.RollbackTo with
            | Some previous ->
                sprintf "Rollback target: %s %s (previously verified)." previous.ArtefactId previous.Version
            | None ->
                "Rollback target: NONE — there is no previously verified artefact, so this flip is one-way. Verify the running build first if you need a rollback path."

        match report.Stage with
        | ProvenanceRefused -> describeRefusal report.Candidate report.Mismatches + "\n" + rollback
        | MigrationBlocked ->
            let lines =
                report.Gaps
                |> List.map (fun gap ->
                    sprintf "\n  • %s requires %s — %s" (ComponentId.value gap.Component) gap.Requirement gap.Purpose)
                |> String.concat ""

            sprintf
                "Provenance VERIFIED for %s %s, but the incoming version needs %d requirement(s) this appliance has not been given:%s\nProvision these, re-run the staging check, then flip.\n%s"
                report.Candidate.ArtefactId
                report.Candidate.Version
                (List.length report.Gaps)
                lines
                rollback
        | ReadyToFlip ->
            sprintf
                "Provenance VERIFIED and every required credential / knob is provisioned for %s %s. Ready to flip.\n%s"
                report.Candidate.ArtefactId
                report.Candidate.Version
                rollback

    /// The running artefact and its provenance, resolved lazily at
    /// preflight so a deployment that does not verify pays no file read.
    ///
    /// `Result.Error` describes why provenance could not be resolved — an
    /// absent sidecar, an unreadable mount. That is a verification
    /// failure, not a pass: an appliance that cannot find its own
    /// provenance has not proved anything about itself.
    type RunningArtefactSource = unit -> Async<Result<ArtefactProvenance * byte[] * byte[], string>>

    /// The **security-class** `IConfigValidator` that verifies the running
    /// artefact at startup.
    ///
    /// Security-class, so `ServerConfig.SkipPreflight` does not bypass it.
    /// An emergency boot taken to ride out a dependency outage is a
    /// legitimate operator choice; booting an artefact that does not match
    /// the provenance it shipped with is not the same kind of decision at
    /// all, and the one lever must not cover both. It reads local bytes
    /// and hashes them, so it is not the class `SkipPreflight` exists for.
    type ApplianceArtefactValidator(verify: VerifyDetachedJws, source: RunningArtefactSource) =
        interface IConfigValidator with
            member _.Name = ValidatorName
            member _.Timeout = IConfigValidator.defaultTimeout

            member _.Validate() = async {
                match! source () with
                | Result.Error reason ->
                    return
                        Error(
                            sprintf
                                "Could not resolve the running artefact's provenance: %s. An appliance that cannot locate its own Phase 182 provenance has verified nothing; treat this as a failed verification, not a skipped one."
                                reason
                        )
                | Result.Ok(provenance, artefactBytes, sbomBytes) ->
                    match! verifyArtefact verify provenance artefactBytes sbomBytes with
                    // The bare `Ok` / `Error` here ARE `ValidationResult`'s —
                    // this is the aggregator's return type, not `Result`.
                    | Result.Ok() -> return Ok
                    | Result.Error mismatches -> return Error(describeRefusal provenance mismatches)
            }

        interface ISecurityClassValidator

    /// Registers on-start artefact verification. **Calling this IS the
    /// opt-in** (GP 13) — a deployment that does not verify its own
    /// artefact never calls it and registers nothing.
    let serviceRegistration
        (verify: VerifyDetachedJws)
        (source: RunningArtefactSource)
        : IServiceCollection -> IServiceCollection =
        fun services ->
            services.AddSingleton<IConfigValidator>(ApplianceArtefactValidator(verify, source) :> IConfigValidator)
            |> ignore

            services

// ─── 488.D — the redacted support bundle ──────────────────────────────

/// The redaction vocabulary for an appliance support bundle: what counts
/// as content-bearing, and therefore what must not survive into a file an
/// operator may forward.
///
/// Two sources, joined:
///
///   * **The Phase 41/188 classifications the deployment declared** —
///     every field whose `ClassificationLevel` is anything other than
///     `Public`. This is the substantive half, and the reason 488.D reads
///     the classification vocabulary rather than inventing a list: the
///     deployment has ALREADY said which of its fields carry personal,
///     financial and regulated data, and a support bundle that used a
///     different definition of "sensitive" than the entity store's own
///     gate would be wrong in one direction or the other.
///   * **The Phase 9n suffix floor** — `apikey` / `token` / `secret` /
///     `password`. Kept as a floor because it catches credential-shaped
///     property names in surfaces that have no entity classification at
///     all (a config tree, a dependency graph, a validator message), and
///     an appliance bundle that dropped it would be less careful than the
///     bundle it is a stricter variant of.
type ApplianceBundleVocabulary = {
    /// Property names derived from the declared classifications. Both the
    /// full dotted `FieldPath` and its leaf segment are present — a JSON
    /// property name is the leaf, while a flattened log line may carry
    /// the dotted path, and masking one but not the other would leave the
    /// same value exposed in whichever surface used the other spelling.
    ClassifiedNames: Set<string>
    /// Case-insensitive property-name suffixes, from the Phase 9n
    /// allowlist.
    SuffixFloor: string list
}

/// **488.D — a data-class-aware variant of the Phase 9n `/dev/bundle`.**
///
/// The 9n bundle is already redacted, against a four-suffix credential
/// allowlist, and that is right for its purpose: an operator pulls it
/// from their OWN deployment for their OWN support ticket, and the
/// bundle's own file header calls the redaction "defence-in-depth".
///
/// An appliance inverts the trust direction. The party who would read the
/// bundle is not the party who owns the data, so redaction stops being
/// defence-in-depth and becomes the load-bearing guarantee — and a
/// four-suffix credential list is not a guarantee about CONTENT. Hence
/// this variant, which masks against what the deployment itself declared
/// to be sensitive.
///
/// **The operator generates and inspects; nobody pulls.** There is
/// deliberately no route, no endpoint, and no scheduled emission here —
/// only pure functions over section content the operator has already
/// collected. The diode (488.C) is the only outbound channel an appliance
/// has, and it structurally cannot carry a bundle: its schema has no
/// string field. So "the vendor never pulls" is not a policy statement
/// about this module; it is the absence of any mechanism that could.
[<RequireQualifiedAccess>]
module ApplianceSupportBundle =

    /// The Phase 9n credential-suffix allowlist, kept as the floor.
    ///
    /// Was a deliberate third COPY, on 9n's own "a shared module does not
    /// earn its keep at two consumers" reasoning. It reads the shared
    /// `RedactionAllowlist` now: three copies is where that reasoning
    /// expired, and this was the copy the source-parsing parity guard did
    /// not cover — so a suffix added to either of the other two would
    /// have left the appliance floor behind, on the one surface where
    /// redaction is the load-bearing guarantee rather than
    /// defence-in-depth.
    ///
    /// Still only the FLOOR: `vocabularyOf` adds the deployment's
    /// declared field classifications on top, because a four-suffix
    /// credential list is not a statement about content.
    let SuffixFloor = RedactionAllowlist.suffixes

    /// Whether a classification level marks a field as carrying content.
    ///
    /// **Everything except `Public`** — a wider net than
    /// `ClassificationLevel.isSensitive`, which admits `Confidential` on
    /// the grounds that any authenticated caller may read it. That
    /// judgement is about callers INSIDE the deployment; a support bundle
    /// leaves it. Commercially confidential material is exactly what an
    /// in-situ customer is protecting, so the appliance bundle masks it
    /// and the access gate does not, and the two are not in conflict.
    let isContentBearing (level: ClassificationLevel) : bool = level <> Public

    /// Build the redaction vocabulary from a deployment's declared
    /// classifications.
    let vocabularyOf (classifications: FieldClassification list) : ApplianceBundleVocabulary =
        let names =
            classifications
            |> List.filter (fun c -> isContentBearing c.Level)
            |> List.collect (fun c ->
                let path = c.FieldPath

                let leaf =
                    match path.LastIndexOf '.' with
                    | -1 -> path
                    | index -> path.Substring(index + 1)

                [ path; leaf ])
            |> List.map _.ToLowerInvariant()
            |> List.filter (String.IsNullOrWhiteSpace >> not)
            |> Set.ofList

        {
            ClassifiedNames = names
            SuffixFloor = SuffixFloor
        }

    /// The vocabulary with no declared classifications — the suffix floor
    /// alone. What an appliance whose modules declare nothing gets, and
    /// still stricter than nothing.
    let floorOnly: ApplianceBundleVocabulary = {
        ClassifiedNames = Set.empty
        SuffixFloor = SuffixFloor
    }

    /// Whether a property name must be masked: a declared classified
    /// field name (exact, case-insensitive) or a credential-shaped
    /// suffix.
    let shouldMask (vocabulary: ApplianceBundleVocabulary) (propertyName: string) : bool =
        if String.IsNullOrEmpty propertyName then
            false
        else
            let lower = propertyName.ToLowerInvariant()

            vocabulary.ClassifiedNames.Contains lower
            || vocabulary.SuffixFloor |> List.exists lower.EndsWith

    /// The replacement written in place of a masked value: the shape is
    /// preserved (a length), the content is not.
    ///
    /// A length rather than a fixed token because "this field was present
    /// and 47 characters long" is genuinely diagnostic — it distinguishes
    /// an empty column from a populated one, which is often the whole
    /// question — and a length is not the content. Same choice the 9n
    /// walk makes, kept so an operator reading both bundles sees one
    /// convention.
    let maskedValue (length: int) : string = sprintf "<masked:length=%d>" length

    /// Serialiser options for writing a masked document back out.
    ///
    /// `UnsafeRelaxedJsonEscaping` matters here and is not a shortcut: the
    /// default encoder escapes `<` and `>`, so the mask marker would land
    /// in the bundle as `<masked:length=15>`. That is still
    /// masked, but an operator scanning a bundle for what was removed
    /// would be reading escape sequences, and the Phase 9n bundle they
    /// have seen before writes the marker plainly (its serialiser comes
    /// from `FableConverters.create ()`, which sets the same encoder). One
    /// convention across both bundles. "Unsafe" refers to HTML-context
    /// injection; these documents are read as files and by JSON parsers,
    /// never interpolated into a page.
    let private maskWriterOptions =
        System.Text.Json.JsonSerializerOptions(
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        )

    let rec private maskNode (vocabulary: ApplianceBundleVocabulary) (node: JsonNode) : unit =
        if isNull node then
            ()
        else
            match node with
            | :? JsonObject as object' ->
                // Snapshot the keys BEFORE mutating — `JsonObject` throws
                // `InvalidOperationException` if mutated during
                // enumeration. Same hazard, same fix, as the Phase 9n
                // walk; see docs/migrations/fablejsonconverter-to-stj.md.
                let names = object' |> Seq.map _.Key |> Seq.toArray

                for name in names do
                    let child = object'[name]

                    if shouldMask vocabulary name then
                        if isNull child then
                            ()
                        else
                            match child.GetValueKind() with
                            | System.Text.Json.JsonValueKind.Null -> ()
                            | System.Text.Json.JsonValueKind.String ->
                                let value = child.GetValue<string>()
                                object'[name] <- JsonValue.Create(maskedValue value.Length) :> JsonNode
                            | _ ->
                                let rendered = child.ToJsonString()
                                object'[name] <- JsonValue.Create(maskedValue rendered.Length) :> JsonNode
                    else
                        maskNode vocabulary child
            | :? JsonArray as array' ->
                for child in array' do
                    maskNode vocabulary child
            | _ -> ()

    /// Mask a JSON document. Total — content that does not parse as JSON
    /// is returned as a single masked value rather than passed through,
    /// because an unparseable section is precisely the case where nothing
    /// is known about what it contains.
    let maskJson (vocabulary: ApplianceBundleVocabulary) (json: string) : string =
        if String.IsNullOrWhiteSpace json then
            json
        else
            try
                let parsed = JsonNode.Parse json
                maskNode vocabulary parsed
                parsed.ToJsonString maskWriterOptions
            with _ ->
                maskedValue json.Length

    /// Mask a JSON-lines document (the `audit-tail.jsonl` shape) line by
    /// line, so one unparseable line does not mask the whole log.
    let maskJsonLines (vocabulary: ApplianceBundleVocabulary) (jsonl: string) : string =
        if String.IsNullOrWhiteSpace jsonl then
            jsonl
        else
            jsonl.Split '\n'
            |> Array.map (fun line ->
                let trimmed = line.TrimEnd '\r'

                if String.IsNullOrWhiteSpace trimmed then
                    trimmed
                else
                    maskJson vocabulary trimmed)
            |> String.concat "\n"

    /// How a bundle section's content is shaped, and therefore which mask
    /// walk applies. A closed set — a section whose shape is not one of
    /// these has no walk that could be trusted, and `Opaque` is the
    /// honest classification for it.
    type SectionShape =
        /// A single JSON document.
        | JsonSection
        /// Newline-delimited JSON records.
        | JsonLinesSection
        /// Anything else. Masked WHOLESALE — an appliance bundle does not
        /// forward content it cannot walk.
        | Opaque

    /// One section of an appliance support bundle.
    type BundleSection = {
        /// The file name the section lands under, matching the Phase 9n
        /// bundle's names where the section is the same
        /// (`config.json`, `health.json`, `audit-tail.jsonl`, …).
        Name: string
        Shape: SectionShape
        Content: string
    }

    /// Mask one section according to its shape.
    let maskSection (vocabulary: ApplianceBundleVocabulary) (section: BundleSection) : BundleSection =
        let masked =
            match section.Shape with
            | JsonSection -> maskJson vocabulary section.Content
            | JsonLinesSection -> maskJsonLines vocabulary section.Content
            | Opaque ->
                if String.IsNullOrEmpty section.Content then
                    section.Content
                else
                    maskedValue section.Content.Length

        { section with Content = masked }

    /// Mask a whole bundle. The operator inspects the result before it
    /// goes anywhere.
    let mask (vocabulary: ApplianceBundleVocabulary) (sections: BundleSection list) : BundleSection list =
        sections |> List.map (maskSection vocabulary)

    /// **The coverage check.** Every property name still carrying a
    /// non-masked, non-null value that the vocabulary says is
    /// content-bearing — reported as `(section name, property name)`.
    ///
    /// This is the acceptance criterion expressed as a function rather
    /// than only as a test, so an operator (or a composition's own smoke
    /// test) can assert it against a real bundle from their own
    /// deployment. **A non-empty result means do not forward the
    /// bundle.**
    let survivingContentFields
        (vocabulary: ApplianceBundleVocabulary)
        (sections: BundleSection list)
        : (string * string) list =
        let rec walk (sectionName: string) (node: JsonNode) : (string * string) list =
            if isNull node then
                []
            else
                match node with
                | :? JsonObject as object' -> [
                    for pair in object' do
                        let child = pair.Value

                        if shouldMask vocabulary pair.Key then
                            let survives =
                                if isNull child then
                                    false
                                else
                                    match child.GetValueKind() with
                                    | System.Text.Json.JsonValueKind.Null -> false
                                    | System.Text.Json.JsonValueKind.String ->
                                        not (child.GetValue<string>().StartsWith("<masked:", StringComparison.Ordinal))
                                    | _ -> true

                            if survives then
                                sectionName, pair.Key
                        else
                            yield! walk sectionName child
                  ]
                | :? JsonArray as array' -> [
                    for child in array' do
                        yield! walk sectionName child
                  ]
                | _ -> []

        sections
        |> List.collect (fun section ->
            let documents =
                match section.Shape with
                | JsonSection -> [ section.Content ]
                | JsonLinesSection ->
                    section.Content.Split '\n'
                    |> Array.toList
                    |> List.map _.TrimEnd('\r')
                    |> List.filter (String.IsNullOrWhiteSpace >> not)
                | Opaque -> []

            documents
            |> List.collect (fun document ->
                try
                    walk section.Name (JsonNode.Parse document)
                with _ ->
                    // An unparseable document in a section declared JSON:
                    // `maskJson` would have masked it wholesale, so
                    // reaching here means the content was not masked at
                    // all. Report it rather than passing it.
                    [ section.Name, "<unparseable>" ]))
        |> List.distinct