module ToolUp.Platform.Tests.InProcess.ApplianceProfileTests

open System
open System.Collections.Generic
open System.Text.Json
open Expecto
open FSharp.Reflection
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.ConfigValidation

// ─── Phase 488 — the appliance deployment profile ─────────────────────
//
// Four acceptance shapes, one per task, plus the GP 13 no-op:
//
//   * **488.A offline boot** — an external-probe-class validator that
//     cannot reach its dependency does not abort a declared-offline boot,
//     and a security-class or structural-class one still does. Proven by
//     running the REAL Phase 9m aggregator over a rewritten
//     `IServiceCollection`, not by inspecting the decorator in isolation:
//     the claim is about what boots, and the aggregator is what decides
//     that.
//   * **488.B tampered-artefact refusal** — the refusal NAMES the
//     mismatch. Asserted on the message text, because "refused" without a
//     named cause is the failure mode the task exists to close.
//   * **488.C diode schema closure** — reflection over
//     `OperationalTelemetryFrame`'s transitive closure finds no `string`.
//     **The walk is falsified against a deliberately-open control type in
//     the same test list**, so a walk that had silently stopped matching
//     anything could not report closure.
//   * **488.D redaction coverage** — no content-bearing field survives a
//     masked bundle, measured by the shipped coverage function.
//
// **The cold-boot-with-no-network simulation is in-process, deliberately.**
// A CI leg that genuinely disabled the network stack would need a
// privileged container and would still not prove the interesting thing:
// the question is not "does the OS refuse to connect" but "does a
// validator whose dependency is unreachable abort this composition". A
// probe that throws a connection-shaped exception reaches the aggregator
// through exactly the same path a real socket failure does — the
// aggregator converts any throw to `Error` — so the in-process form
// exercises the whole decision path from the failure to the boot verdict.
// Recorded as a deviation on the phase.

// ── fixtures ──────────────────────────────────────────────────────────

/// An external-probe-class validator (unmarked — the Phase 585 default)
/// that reports a connection failure the way a real one does.
type private UnreachableProbe(name: string) =
    interface IConfigValidator with
        member _.Name = name
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async { return Error "connection refused: no route to host storage.example:443" }

/// An external-probe-class validator that THROWS, the way a socket
/// failure surfaces. The aggregator turns a throw into `Error`, so this
/// is the closest in-process analogue of a disabled network stack.
type private ThrowingProbe(name: string) =
    interface IConfigValidator with
        member _.Name = name
        member _.Timeout = IConfigValidator.defaultTimeout

        member _.Validate() = async {
            return failwith "Sockets: A socket operation was attempted to an unreachable network."
        }

/// An external-probe-class validator that HANGS, the way a TCP connect to
/// an unroutable address commonly does. Declares a short timeout so the
/// test does not wait five seconds for the point to be made.
type private HangingProbe(name: string) =
    interface IConfigValidator with
        member _.Name = name
        member _.Timeout = TimeSpan.FromMilliseconds 200.0

        member _.Validate() = async {
            do! Async.Sleep 60_000
            return Ok
        }

/// A security-class validator that fails. Must still abort — being
/// offline is not a reason to boot with an identity-spoofing hole.
type private FailingSecurityGuard() =
    interface IConfigValidator with
        member _.Name = "security-guard"
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return Error "CSRF protection is disabled" }

    interface ISecurityClassValidator

/// A structural-class validator that fails. Must still abort.
type private FailingStructuralGuard() =
    interface IConfigValidator with
        member _.Name = "structural-guard"
        member _.Timeout = IConfigValidator.defaultTimeout
        member _.Validate() = async { return Error "duplicate component id: companion:IBlobStorage" }

    interface IStructuralClassValidator

let private servicesWith (validators: IConfigValidator list) : IServiceCollection =
    let services = ServiceCollection() :> IServiceCollection

    for validator in validators do
        services.AddSingleton<IConfigValidator>(validator) |> ignore

    services

let private validatorInstances (services: IServiceCollection) : IConfigValidator list =
    services
    |> Seq.filter (fun d -> d.ServiceType = typeof<IConfigValidator>)
    |> Seq.choose (fun d ->
        match d.ImplementationInstance with
        | :? IConfigValidator as v -> Some v
        | _ -> None)
    |> List.ofSeq

let private runAggregator (services: IServiceCollection) =
    ConfigValidatorAggregator.validate services None false

let private outcomeFor (name: string) (outcomes: ConfigValidatorAggregator.ValidatorOutcome list) =
    outcomes |> List.find (fun o -> o.Name = name) |> _.Result

// ── 488.B fixtures ────────────────────────────────────────────────────

let private artefactBytes = Text.Encoding.UTF8.GetBytes "the-artefact-bytes"
let private sbomBytes = Text.Encoding.UTF8.GetBytes "{\"bomFormat\":\"CycloneDX\"}"

/// A provenance record that matches the fixture bytes.
let private goodProvenance: ArtefactProvenance = {
    ArtefactId = "ToolUp.Platform.Server"
    Version = "0.9.4"
    ArtefactSha256 = ApplianceUpgrade.sha256Hex artefactBytes
    SbomSha256 = ApplianceUpgrade.sha256Hex sbomBytes
    DetachedJws = "eyJhbGciOiJFUzI1NiJ9..c2lnbmF0dXJl"
}

/// A verifier that always accepts — isolates the digest arm.
let private acceptingVerifier: VerifyDetachedJws =
    fun _ _ -> async { return Result.Ok() }

/// A verifier that always rejects — isolates the signature arm.
let private rejectingVerifier: VerifyDetachedJws =
    fun _ _ -> async { return Result.Error "no verification key for key id: appliance-2026-01" }

// ── 488.C schema-closure walk ─────────────────────────────────────────

/// Every type reachable from `root` through F# record fields, DU case
/// fields, generic arguments, and array element types.
///
/// This is the mechanism the closure claim rests on, so it is written
/// once and exercised twice — against the frame (must find no string) and
/// against a control record that has one (must find it). A walk that
/// matched nothing would pass the first assertion and fail the second.
let private reachableTypes (root: Type) : Type list =
    let visited = HashSet<Type>()

    let rec walk (t: Type) =
        if not (isNull t) && visited.Add t then
            if t.IsArray then
                walk (t.GetElementType())

            if t.IsGenericType then
                for arg in t.GetGenericArguments() do
                    walk arg

            if FSharpType.IsRecord(t, true) then
                for field in FSharpType.GetRecordFields(t, true) do
                    walk field.PropertyType
            elif FSharpType.IsUnion(t, true) then
                for case in FSharpType.GetUnionCases(t, true) do
                    for field in case.GetFields() do
                        walk field.PropertyType
            elif FSharpType.IsTuple t then
                for element in FSharpType.GetTupleElements t do
                    walk element

    walk root
    List.ofSeq visited

let private isFSharpList (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<_ list>

/// The control: a record that DOES carry free text, walked by the same
/// function. Exists solely so the closure assertion is falsifiable.
type private OpenControlFrame = { Schema: int; Note: string }

// ── 488.D fixtures ────────────────────────────────────────────────────

let private classifications: FieldClassification list = [
    FieldClassification.create "Customer" "Email" Pii
    FieldClassification.create "Customer" "Profile.HomeAddress" Spi
    FieldClassification.create "Invoice" "NetAmount" Financial
    FieldClassification.create "Invoice" "Currency" Public
    FieldClassification.create "Contract" "NegotiatedRate" Confidential
]

let private vocabulary = ApplianceSupportBundle.vocabularyOf classifications

let private jsonSection (name: string) (content: string) : ApplianceSupportBundle.BundleSection = {
    Name = name
    Shape = ApplianceSupportBundle.JsonSection
    Content = content
}

// ── tests ─────────────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 488 — appliance deployment profile" [

        // ─── 488.A — offline-tolerant boot posture ────────────────────

        testList "488.A — offline boot posture" [

            testCase "the identity posture leaves the composed services untouched (GP 11/13)"
            <| fun _ ->
                let probe = UnreachableProbe "storage-sentinel" :> IConfigValidator
                let services = servicesWith [ probe ]

                let rewritten =
                    services
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.identity
                    |> ApplianceBootPosture.serviceRegistration
                        ApplianceProfile.identity
                        ComponentRequirements.emptySignature
                        (CompositionManifest.build [] [] [] [] [])

                Expect.equal (Seq.length rewritten) 1 "no descriptor was added under the identity posture"

                Expect.isTrue
                    (Object.ReferenceEquals(List.exactlyOne (validatorInstances rewritten), probe))
                    "the registered instance is the SAME object — not decorated, not replaced"

            testCase "a declared-offline appliance cold-boots though its external probe cannot connect"
            <| fun _ ->
                // The in-process stand-in for a disabled network stack: a
                // probe that throws a connection-shaped exception, which
                // reaches the aggregator by the same path a real socket
                // failure does.
                let services =
                    servicesWith [ UnreachableProbe "storage-sentinel"; ThrowingProbe "oidc-discovery" ]
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                // The whole claim: the aggregator does not throw, i.e. the
                // app boots.
                let outcomes = runAggregator services

                Expect.equal (List.length outcomes) 2 "both probes ran"

                for outcome in outcomes do
                    Expect.equal
                        (ValidationResult.status outcome.Result)
                        "Warning"
                        (sprintf "%s was downgraded to Warning, not left as Error" outcome.Name)

            testCase "the same composition WITHOUT the offline posture refuses to boot"
            <| fun _ ->
                // The control for the test above. Without it, "it booted"
                // proves nothing — the probes might never have failed.
                let services = servicesWith [ UnreachableProbe "storage-sentinel" ]

                Expect.throwsT<ConfigValidatorAggregator.ConfigPreflightFailedException>
                    (fun () -> runAggregator services |> ignore)
                    "a connected deployment still aborts on an unreachable dependency"

            testCase "the downgrade names itself and preserves the original message"
            <| fun _ ->
                let services =
                    servicesWith [ UnreachableProbe "storage-sentinel" ]
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                match outcomeFor "storage-sentinel" (runAggregator services) with
                | Warning message ->
                    Expect.stringContains
                        message
                        "no route to host storage.example:443"
                        "the original probe message survives — the operator still learns what could not be reached"

                    Expect.stringContains
                        message
                        "DeclaredOffline"
                        "and the message names the posture that downgraded it, so the Warning is not mysterious"
                | other -> failtestf "expected a Warning, got %s" (ValidationResult.status other)

            testCase "a probe that HANGS is downgraded too — the decorator times it out itself"
            <| fun _ ->
                // The third failure mode, and the one the aggregator's own
                // timeout cannot help with: F# async cancellation is not an
                // exception, so if the aggregator's CTS won the race the
                // outcome would be Error and the boot would abort.
                let services =
                    servicesWith [ HangingProbe "vector-store-connect" ]
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                match outcomeFor "vector-store-connect" (runAggregator services) with
                | Warning message ->
                    Expect.stringContains message "did not answer" "the non-answer is named as such"
                    Expect.stringContains message "DeclaredOffline" "and attributed to the posture"
                | other ->
                    failtestf
                        "a hanging probe on an offline appliance must not abort the boot. Got %s"
                        (ValidationResult.status other)

            testCase "the decorator's own budget stays inside the aggregator's global one"
            <| fun _ ->
                // A validator declaring more than the 10s aggregator budget
                // would otherwise get a child budget that never expires in
                // time to be caught, and the aggregator's cancellation would
                // abort the boot after all.
                let greedy =
                    { new IConfigValidator with
                        member _.Name = "greedy"
                        member _.Timeout = TimeSpan.FromMinutes 5.0
                        member _.Validate() = async { return Ok }
                    }

                let decorated = OfflineTolerantValidator greedy

                Expect.isTrue
                    (decorated.InnerBudget < ConfigValidatorAggregator.aggregatorBudget)
                    "the child budget is clamped below the aggregator's"

                Expect.isTrue
                    ((decorated :> IConfigValidator).Timeout
                     <= ConfigValidatorAggregator.aggregatorBudget)
                    "and the reported timeout does not exceed it either, so the clamp is not defeated by the margin"

            testCase "a security-class validator still aborts an offline boot"
            <| fun _ ->
                let services =
                    servicesWith [ UnreachableProbe "storage-sentinel"; FailingSecurityGuard() ]
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                Expect.throwsT<ConfigValidatorAggregator.ConfigPreflightFailedException>
                    (fun () -> runAggregator services |> ignore)
                    "being offline is a reason a sentinel cannot answer, not a reason to boot with a CSRF hole"

            testCase "a structural-class validator still aborts an offline boot"
            <| fun _ ->
                let services =
                    servicesWith [ UnreachableProbe "storage-sentinel"; FailingStructuralGuard() ]
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                Expect.throwsT<ConfigValidatorAggregator.ConfigPreflightFailedException>
                    (fun () -> runAggregator services |> ignore)
                    "a composition whose component ids collide must not boot, offline or not"

            testCase "only the external-probe-class instances are rewritten"
            <| fun _ ->
                let services =
                    servicesWith [
                        UnreachableProbe "storage-sentinel"
                        FailingSecurityGuard()
                        FailingStructuralGuard()
                    ]
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                let decorated =
                    validatorInstances services
                    |> List.filter (fun v ->
                        match box v with
                        | :? OfflineTolerantValidator -> true
                        | _ -> false)

                Expect.equal (List.length decorated) 1 "exactly one instance was decorated"

                Expect.equal (List.exactlyOne decorated).Name "storage-sentinel" "and it is the unmarked external probe"

            testCase "the rewrite is idempotent"
            <| fun _ ->
                let once =
                    servicesWith [ UnreachableProbe "storage-sentinel" ]
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                let instanceAfterOnce = List.exactlyOne (validatorInstances once)

                let twice =
                    once
                    |> ApplianceBootPosture.offlineTolerantRegistration ApplianceProfile.offline

                Expect.isTrue
                    (Object.ReferenceEquals(List.exactlyOne (validatorInstances twice), instanceAfterOnce))
                    "a second pass leaves the already-wrapped instance alone rather than double-wrapping"

            testCase "the clock-skew allowance is symmetric and defaults to no allowance"
            <| fun _ ->
                let now = DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)
                let behind = now.AddMinutes -4.0
                let ahead = now.AddMinutes 4.0

                Expect.isFalse
                    (ApplianceProfile.withinSkew ApplianceProfile.identity now behind)
                    "the identity allows nothing — today's behaviour exactly"

                Expect.isTrue
                    (ApplianceProfile.withinSkew ApplianceProfile.offline now behind)
                    "a clock four minutes behind is inside the five-minute allowance"

                Expect.isTrue
                    (ApplianceProfile.withinSkew ApplianceProfile.offline now ahead)
                    "and so is one four minutes ahead — an appliance clock drifts both ways"

                Expect.isFalse
                    (ApplianceProfile.withinSkew ApplianceProfile.offline now (now.AddMinutes 6.0))
                    "six minutes is outside it"

                Expect.equal
                    (ApplianceProfile.widenWindow
                        (ApplianceProfile.offlineWithSkew (TimeSpan.FromMinutes 15.0))
                        (TimeSpan.FromMinutes 5.0))
                    (TimeSpan.FromMinutes 20.0)
                    "an existing freshness window is widened by the declared drift, not replaced"

            testCase "a negative declared tolerance is clamped rather than inverting the comparison"
            <| fun _ ->
                let profile = ApplianceProfile.offlineWithSkew (TimeSpan.FromMinutes -5.0)

                Expect.equal profile.ClockSkewTolerance TimeSpan.Zero "clamped to no allowance"

            testCase "the endpoint findings name only COMPOSED components' URI knobs"
            <| fun _ ->
                let composedId = ComponentId.forCompanionSlot "IBlobStorage"
                let absentId = ComponentId.forCompanionSlot "IVectorStore"

                let signature =
                    ComponentRequirements.signatureOf [
                        ComponentRequirements.create composedId [] [
                            ConfigRequirement.required "endpoint" UriKnob "the object-store endpoint"
                            ConfigRequirement.required "bucket" StringKnob "the bucket name"
                        ]
                        ComponentRequirements.create absentId [] [
                            ConfigRequirement.required "endpoint" UriKnob "the vector-store endpoint"
                        ]
                    ]

                let manifest =
                    CompositionManifest.build [] [ CompositionManifest.companionSlotEntry "IBlobStorage" ] [] [] []

                let findings = ApplianceBootPosture.externalEndpointFindings signature manifest

                Expect.equal
                    (List.length findings)
                    1
                    "one finding — the string knob and the absent component are both out"

                let (foundId, knob) = List.exactlyOne findings
                Expect.equal foundId composedId "the composed component"
                Expect.equal knob.Path "endpoint" "and its URI-typed knob"

            testCase "the endpoint rule is a WARNING and dormant when connected"
            <| fun _ ->
                let signature =
                    ComponentRequirements.signatureOf [
                        ComponentRequirements.create (ComponentId.forCompanionSlot "IBlobStorage") [] [
                            ConfigRequirement.required "endpoint" UriKnob "the object-store endpoint"
                        ]
                    ]

                let manifest =
                    CompositionManifest.build [] [ CompositionManifest.companionSlotEntry "IBlobStorage" ] [] [] []

                Expect.isEmpty
                    (ApplianceBootPosture.defects ApplianceProfile.identity signature manifest)
                    "a connected deployment gets no finding at all"

                let defects =
                    ApplianceBootPosture.defects ApplianceProfile.offline signature manifest

                let defect = List.exactlyOne defects

                Expect.equal defect.Severity DefectWarning "a pre-install checklist, not a boot refusal"

                Expect.stringContains
                    defect.Message
                    "endpoint"
                    "and it names the knob the operator has to confirm resolves in-container"

                Expect.equal
                    (ApplianceBootPosture.toValidationResult defects |> ValidationResult.status)
                    "Warning"
                    "so the gate warns and the appliance boots"

            testCase "the boot-posture rule code does not collide with another family's"
            <| fun _ ->
                let others =
                    (CompositionValidator.ruleManifest
                     @ EventTopologyPreflight.ruleManifest
                     @ DataFootprintPreflight.ruleManifest
                     @ ScaleReadinessPreflight.ruleManifest)
                    |> List.map _.Code

                for code in ApplianceBootPosture.ruleManifest |> List.map _.Code do
                    Expect.isFalse (List.contains code others) (sprintf "rule code '%s' is unique across families" code)

            testCase "the classified projection cannot diverge from the rule manifest"
            <| fun _ ->
                Expect.equal
                    (ApplianceBootPosture.classifiedRuleManifest |> List.map _.Code)
                    (ApplianceBootPosture.ruleManifest |> List.map _.Code)
                    "the two projections read the same declared rule"

                Expect.all
                    ApplianceBootPosture.classifiedRuleManifest
                    (fun r -> r.Class = StructuralRule)
                    "the join reaches nothing outside the process, so it is structural"
        ]

        // ─── 488.B — signed upgrade verification ──────────────────────

        testList "488.B — signed upgrade verification" [

            testCase "a matching artefact verifies"
            <| fun _ ->
                let result =
                    ApplianceUpgrade.verifyArtefact acceptingVerifier goodProvenance artefactBytes sbomBytes
                    |> Async.RunSynchronously

                Expect.isOk result "the bytes hash to the declared digests and the signature verifies"

            testCase "a TAMPERED artefact is refused with the digest mismatch NAMED"
            <| fun _ ->
                let tampered = Text.Encoding.UTF8.GetBytes "the-artefact-bytes-with-one-more-thing"

                let result =
                    ApplianceUpgrade.verifyArtefact acceptingVerifier goodProvenance tampered sbomBytes
                    |> Async.RunSynchronously

                match result with
                | Result.Ok() -> failtest "a tampered artefact must not verify"
                | Result.Error mismatches ->
                    let mismatch = List.exactlyOne mismatches

                    match mismatch with
                    | ArtefactDigestMismatch(expected, actual) ->
                        Expect.equal expected goodProvenance.ArtefactSha256 "the expected digest is the declared one"

                        Expect.equal
                            actual
                            (ApplianceUpgrade.sha256Hex tampered)
                            "and the actual is what the received bytes hash to"

                        Expect.notEqual expected actual "which is the whole point"
                    | other -> failtestf "expected an artefact digest mismatch, got %A" other

                    // The acceptance criterion is the NAMED mismatch, so the
                    // rendered refusal is asserted too — a typed value nobody
                    // renders is not an operator-visible name.
                    let refusal = ApplianceUpgrade.describeRefusal goodProvenance mismatches

                    Expect.stringContains refusal "artefact digest mismatch" "the refusal names the class of mismatch"
                    Expect.stringContains refusal goodProvenance.ArtefactSha256 "the expected digest"
                    Expect.stringContains refusal (ApplianceUpgrade.sha256Hex tampered) "and the actual one"
                    Expect.stringContains refusal goodProvenance.Version "and which build was refused"

            testCase "a swapped SBOM is refused even when the artefact itself matches"
            <| fun _ ->
                let swappedSbom =
                    Text.Encoding.UTF8.GetBytes "{\"bomFormat\":\"CycloneDX\",\"components\":[]}"

                let result =
                    ApplianceUpgrade.verifyArtefact acceptingVerifier goodProvenance artefactBytes swappedSbom
                    |> Async.RunSynchronously

                match result with
                | Result.Error [ SbomDigestMismatch(expected, actual) ] ->
                    Expect.equal expected goodProvenance.SbomSha256 "the declared SBOM digest"
                    Expect.notEqual expected actual "does not match the SBOM that arrived"
                | other ->
                    failtestf
                        "the SBOM digest is verified independently of the artefact — an artefact-only check would accept a swapped dependency set. Got %A"
                        other

            testCase "a rejected signature is refused with the verifier's reason NAMED"
            <| fun _ ->
                let result =
                    ApplianceUpgrade.verifyArtefact rejectingVerifier goodProvenance artefactBytes sbomBytes
                    |> Async.RunSynchronously

                match result with
                | Result.Error [ SignatureRejected reason ] ->
                    Expect.stringContains reason "appliance-2026-01" "the verifier's own reason reaches the operator"
                | other -> failtestf "expected a rejected signature, got %A" other

            testCase "every mismatch is reported, not just the first"
            <| fun _ ->
                let tampered = Text.Encoding.UTF8.GetBytes "tampered"
                let swappedSbom = Text.Encoding.UTF8.GetBytes "swapped"

                let result =
                    ApplianceUpgrade.verifyArtefact rejectingVerifier goodProvenance tampered swappedSbom
                    |> Async.RunSynchronously

                match result with
                | Result.Error mismatches ->
                    Expect.equal
                        (List.length mismatches)
                        3
                        "both digests and the signature — an operator at 2am gets the whole picture from one run"
                | Result.Ok() -> failtest "must not verify"

            testCase "a verifier that THROWS is a refusal, not a pass"
            <| fun _ ->
                let throwing: VerifyDetachedJws =
                    fun _ _ -> async { return failwith "key resolver unavailable" }

                let result =
                    ApplianceUpgrade.verifyArtefact throwing goodProvenance artefactBytes sbomBytes
                    |> Async.RunSynchronously

                match result with
                | Result.Error [ SignatureRejected reason ] ->
                    Expect.stringContains reason "key resolver unavailable" "the throw is named"
                | other -> failtestf "a throwing verifier must refuse, never fall through to Ok. Got %A" other

            testCase "blank provenance is incomplete, not verified"
            <| fun _ ->
                let blank = {
                    goodProvenance with
                        DetachedJws = "   "
                }

                let result =
                    ApplianceUpgrade.verifyArtefact acceptingVerifier blank artefactBytes sbomBytes
                    |> Async.RunSynchronously

                match result with
                | Result.Error [ ProvenanceIncomplete field ] ->
                    Expect.equal field "DetachedJws" "the absent field is named"
                | other -> failtestf "expected an incomplete-provenance refusal, got %A" other

            testCase "digest comparison is case-insensitive"
            <| fun _ ->
                let upper = {
                    goodProvenance with
                        ArtefactSha256 = goodProvenance.ArtefactSha256.ToUpperInvariant()
                }

                Expect.isOk
                    (ApplianceUpgrade.verifyArtefact acceptingVerifier upper artefactBytes sbomBytes
                     |> Async.RunSynchronously)
                    "refusing an artefact over the case of a hex digit would refuse for the wrong reason"

            testCase "the staging check refuses before it previews the migration"
            <| fun _ ->
                let candidate: UpgradeCandidate = {
                    Provenance = goodProvenance
                    ArtefactBytes = Text.Encoding.UTF8.GetBytes "tampered"
                    SbomBytes = sbomBytes
                    Requirements =
                        ComponentRequirements.signatureOf [
                            ComponentRequirements.create (ComponentId.forCompanionSlot "IBlobStorage") [
                                SecretRequirement.required
                                    ComponentRequirements.PlatformScope
                                    "STORAGE_KEY"
                                    ApiKeySecret
                                    "authenticates to the object store"
                            ] []
                        ]
                }

                let report =
                    ApplianceUpgrade.stage acceptingVerifier MigrationProbes.noneProvisioned None candidate
                    |> Async.RunSynchronously

                Expect.equal report.Stage ProvenanceRefused "provenance first"
                Expect.isNonEmpty report.Mismatches "with the mismatch named"

                Expect.isEmpty
                    report.Gaps
                    "and no migration preview — reading requirement declarations out of an unauthenticated artefact is the same trust mistake in a smaller frame"

                Expect.isFalse (UpgradeStage.mayFlip report.Stage) "the operator must not flip"

            testCase "the migrate preview names the unprovisioned requirements the new version needs"
            <| fun _ ->
                let componentId = ComponentId.forCompanionSlot "IBlobStorage"

                let requirements =
                    ComponentRequirements.signatureOf [
                        ComponentRequirements.create componentId [
                            SecretRequirement.required
                                ComponentRequirements.PlatformScope
                                "STORAGE_KEY"
                                ApiKeySecret
                                "authenticates to the object store"
                            SecretRequirement.optional
                                ComponentRequirements.PlatformScope
                                "STORAGE_FALLBACK_KEY"
                                ApiKeySecret
                                "a secondary credential"
                        ] [
                            ConfigRequirement.required "endpoint" UriKnob "the object-store endpoint"
                            ConfigRequirement.defaulted "timeout" DurationKnob "00:00:30" "the request timeout"
                        ]
                    ]

                let candidate: UpgradeCandidate = {
                    Provenance = goodProvenance
                    ArtefactBytes = artefactBytes
                    SbomBytes = sbomBytes
                    Requirements = requirements
                }

                let report =
                    ApplianceUpgrade.stage acceptingVerifier MigrationProbes.noneProvisioned None candidate
                    |> Async.RunSynchronously

                Expect.equal report.Stage MigrationBlocked "verified, but not provisioned"

                Expect.equal
                    (List.length report.Gaps)
                    2
                    "the required secret and the default-less knob — an OPTIONAL secret and a DEFAULTED knob are not blockers"

                let requirementText = report.Gaps |> List.map _.Requirement |> String.concat " | "

                Expect.stringContains requirementText "STORAGE_KEY" "the secret is named"
                Expect.stringContains requirementText "api-key" "with its class"
                Expect.stringContains requirementText "endpoint" "and the knob is named"

                Expect.isFalse
                    (requirementText.Contains "STORAGE_FALLBACK_KEY")
                    "an optional credential's absence degrades a component; it does not block a flip"

            testCase "a gap report carries names and classes only — never a value"
            <| fun _ ->
                // The Phase 432 constraint, re-asserted at this seam: the
                // preview's probes return booleans, so there is no value in
                // scope that could reach the report even by accident.
                let secretValue = "sk-live-do-not-leak-this"

                let probes: MigrationProbes = {
                    SecretPresent = fun _ _ -> false
                    ConfigBound = fun _ -> false
                }

                let requirements =
                    ComponentRequirements.signatureOf [
                        ComponentRequirements.create (ComponentId.forCompanionSlot "IBlobStorage") [
                            SecretRequirement.required
                                ComponentRequirements.PlatformScope
                                "STORAGE_KEY"
                                ApiKeySecret
                                "authenticates to the object store"
                        ] []
                    ]

                let gaps = ApplianceUpgrade.migrationPreview probes requirements

                let rendered =
                    gaps |> List.map (fun g -> g.Requirement + g.Purpose) |> String.concat " "

                Expect.isFalse (rendered.Contains secretValue) "no report path can render a credential's value"
                Expect.stringContains rendered "STORAGE_KEY" "only its name"

            testCase "a verified, fully-provisioned candidate is ready to flip"
            <| fun _ ->
                let probes: MigrationProbes = {
                    SecretPresent = fun _ _ -> true
                    ConfigBound = fun _ -> true
                }

                let candidate: UpgradeCandidate = {
                    Provenance = goodProvenance
                    ArtefactBytes = artefactBytes
                    SbomBytes = sbomBytes
                    Requirements =
                        ComponentRequirements.signatureOf [
                            ComponentRequirements.create (ComponentId.forCompanionSlot "IBlobStorage") [
                                SecretRequirement.required
                                    ComponentRequirements.PlatformScope
                                    "STORAGE_KEY"
                                    ApiKeySecret
                                    "authenticates to the object store"
                            ] []
                        ]
                }

                let previous = {
                    goodProvenance with
                        Version = "0.9.3"
                }

                let report =
                    ApplianceUpgrade.stage acceptingVerifier probes (Some previous) candidate
                    |> Async.RunSynchronously

                Expect.equal report.Stage ReadyToFlip "verify → preview → flip"
                Expect.isTrue (UpgradeStage.mayFlip report.Stage) "the operator may proceed"

                Expect.equal
                    report.RollbackTo
                    (Some previous)
                    "and the rollback target is the previously verified build"

                Expect.stringContains
                    (ApplianceUpgrade.describeStaging report)
                    "0.9.3"
                    "the runbook output names the version to return to"

            testCase "an absent rollback target is surfaced as one-way, not omitted"
            <| fun _ ->
                let probes: MigrationProbes = {
                    SecretPresent = fun _ _ -> true
                    ConfigBound = fun _ -> true
                }

                let candidate: UpgradeCandidate = {
                    Provenance = goodProvenance
                    ArtefactBytes = artefactBytes
                    SbomBytes = sbomBytes
                    Requirements = ComponentRequirements.emptySignature
                }

                let report =
                    ApplianceUpgrade.stage acceptingVerifier probes None candidate
                    |> Async.RunSynchronously

                Expect.isNone report.RollbackTo "no verified predecessor"

                Expect.stringContains
                    (ApplianceUpgrade.describeStaging report)
                    "one-way"
                    "which the operator learns before the flip rather than during an incident"

            testCase "on-start verification is security-class, so SkipPreflight cannot bypass it"
            <| fun _ ->
                let source: ApplianceUpgrade.RunningArtefactSource =
                    fun () -> async {
                        return Result.Ok(goodProvenance, Text.Encoding.UTF8.GetBytes "tampered", sbomBytes)
                    }

                let services =
                    ServiceCollection() :> IServiceCollection
                    |> ApplianceUpgrade.serviceRegistration acceptingVerifier source

                let validator = List.exactlyOne (validatorInstances services)

                Expect.equal
                    (ConfigValidatorAggregator.classify validator)
                    ConfigValidatorAggregator.SecurityClass
                    "an unsigned artefact booting is not the class of thing an emergency-boot lever should cover"

                // `skipPreflight = true` — and it still aborts.
                Expect.throwsT<ConfigValidatorAggregator.ConfigPreflightFailedException>
                    (fun () -> ConfigValidatorAggregator.validate services None true |> ignore)
                    "the tampered artefact aborts the boot even under SkipPreflight"

            testCase "an unresolvable provenance is a failed verification, not a skipped one"
            <| fun _ ->
                let source: ApplianceUpgrade.RunningArtefactSource =
                    fun () -> async { return Result.Error "no .sig sidecar beside the artefact mount" }

                let services =
                    ServiceCollection() :> IServiceCollection
                    |> ApplianceUpgrade.serviceRegistration acceptingVerifier source

                match
                    (List.exactlyOne (validatorInstances services)).Validate()
                    |> Async.RunSynchronously
                with
                | Error message ->
                    Expect.stringContains message "no .sig sidecar" "naming why provenance could not be resolved"
                | other ->
                    failtestf
                        "an appliance that cannot locate its own provenance has verified nothing. Got %s"
                        (ValidationResult.status other)

            testCase "registering nothing is the default (GP 13)"
            <| fun _ ->
                let services = ServiceCollection() :> IServiceCollection

                Expect.equal
                    (Seq.length services)
                    0
                    "calling the registration IS the opt-in — a deployment that never verifies registers nothing"
        ]

        // ─── 488.C — the operational-telemetry diode ──────────────────

        testList "488.C — operational-telemetry diode" [

            testCase "the frame schema carries no string anywhere in its transitive closure"
            <| fun _ ->
                let closure = reachableTypes typeof<OperationalTelemetryFrame>

                Expect.isFalse
                    (closure |> List.contains typeof<string>)
                    "a string field is a hole a row of customer data fits through — the schema has none"

                Expect.isFalse (closure |> List.contains typeof<obj>) "nor an obj field"

                Expect.isFalse
                    (closure
                     |> List.exists (fun t ->
                         t.IsGenericType && t.Name.StartsWith("FSharpMap", StringComparison.Ordinal)))
                    "nor a Map, which is a string-keyed bag by another name"

            testCase "…and the closure walk can actually FAIL — falsified against an open control"
            <| fun _ ->
                // Without this, the assertion above would pass just as
                // happily if `reachableTypes` had stopped matching anything.
                let control = reachableTypes typeof<OpenControlFrame>

                Expect.isTrue
                    (control |> List.contains typeof<string>)
                    "the same walk finds the control record's string field, so its silence about the frame means something"

                Expect.isTrue (control |> List.contains typeof<int>) "and reaches the numeric field too"

            testCase "every enumeration in the closure is genuinely closed — no case carries data"
            <| fun _ ->
                let enumerations =
                    reachableTypes typeof<OperationalTelemetryFrame>
                    |> List.filter (fun t -> FSharpType.IsUnion(t, true) && not (isFSharpList t))

                Expect.isNonEmpty enumerations "the frame is built from enumerations, so some must be found"

                for enumeration in enumerations do
                    for case in FSharpType.GetUnionCases(enumeration, true) do
                        Expect.equal
                            (case.GetFields().Length)
                            0
                            (sprintf
                                "%s.%s carries data — a case with a payload is how a closed schema stops being closed"
                                enumeration.Name
                                case.Name)

            testCase "the diode is OFF by default and produces zero outbound traffic"
            <| fun _ ->
                let journal = DiodeTransmissionJournal() :> IDiodeTransmissionLog

                let send: DiodeTransmit =
                    fun _ -> failtest "the outbound function must not be invoked when consent is withheld"

                let frame =
                    OperationalTelemetryDiode.header (DiodeVersion.create 0 9 4) 3600L 1_780_000_000L
                    |> OperationalTelemetryDiode.withHealth [
                        {
                            Subsystem = StorageSubsystem
                            State = DiodeDegraded
                        }
                    ]

                let outcome =
                    OperationalTelemetryDiode.transmit DiodeWithheld journal send (fun () -> 1_780_000_000L) frame
                    |> Async.RunSynchronously

                Expect.equal outcome DiodeSuppressed "suppressed"

                Expect.equal (OperationalTelemetryDiode.bytesTransmitted journal) 0L "and zero bytes left the appliance"

                let entry = List.exactlyOne journal.Entries

                Expect.isNone
                    entry.Payload
                    "journalled with no payload — nothing was sent, as distinct from sending nothing"

            testCase "with consent granted, the payload matches the closed schema and is journalled verbatim"
            <| fun _ ->
                let journal = DiodeTransmissionJournal() :> IDiodeTransmissionLog
                let sent = ResizeArray<string>()

                let send: DiodeTransmit =
                    fun payload -> async {
                        sent.Add payload
                        return Result.Ok()
                    }

                let frame =
                    OperationalTelemetryDiode.header (DiodeVersion.parse "0.9.4-beta.2+build17") 3600L 1_780_000_000L
                    |> OperationalTelemetryDiode.withHealth [
                        {
                            Subsystem = StorageSubsystem
                            State = DiodeDegraded
                        }
                        {
                            Subsystem = PlatformSubsystem
                            State = DiodeHealthy
                        }
                    ]
                    |> OperationalTelemetryDiode.withPreflight [
                        {
                            Class = DiodeExternalProbeClass
                            Outcome = DiodePreflightWarning
                            Validators = 3
                        }
                    ]
                    |> OperationalTelemetryDiode.withCounters [
                        {
                            Counter = RequestsServed
                            Value = 41_233L
                        }
                        {
                            Counter = UpgradeRefusals
                            Value = 1L
                        }
                    ]

                let grant = {
                    GrantedAtUnixSeconds = 1_779_000_000L
                    Sections = DiodeSection.all
                }

                let outcome =
                    OperationalTelemetryDiode.transmit
                        (DiodeGranted grant)
                        journal
                        send
                        (fun () -> 1_780_000_000L)
                        frame
                    |> Async.RunSynchronously

                let payload = Expect.wantSome (Seq.tryExactlyOne sent) "exactly one transmission"

                match outcome with
                | DiodeSent bytes ->
                    Expect.equal bytes (Text.Encoding.UTF8.GetByteCount payload) "the outcome's size is the payload's"
                | other -> failtestf "expected DiodeSent, got %A" other

                let entry = List.exactlyOne journal.Entries

                Expect.equal
                    entry.Payload
                    (Some payload)
                    "the journal holds the EXACT bytes that left — an operator audits the payload, not a description of it"

                // The version arrived as three numbers, with the
                // pre-release and build metadata dropped rather than
                // carried as text.
                let document = JsonDocument.Parse payload
                let version = document.RootElement.GetProperty "version"
                Expect.equal (version.GetProperty("major").GetInt32()) 0 "major"
                Expect.equal (version.GetProperty("minor").GetInt32()) 9 "minor"
                Expect.equal (version.GetProperty("patch").GetInt32()) 4 "patch"

                Expect.isFalse (payload.Contains "beta") "the pre-release tag did not ride out"
                Expect.isFalse (payload.Contains "build17") "nor the build metadata"

                Expect.equal
                    (document.RootElement.GetProperty("schema").GetInt32())
                    OperationalTelemetryDiode.Schema
                    "the frame declares its wire schema so a receiver can reject one it does not understand"

            testCase "every string on the wire is SDK-declared vocabulary"
            <| fun _ ->
                // The complement of the closure test: the schema has no
                // string FIELD, and the rendering emits no string VALUE
                // that is not a token from a closed vocabulary in the
                // source file.
                let vocabulary =
                    (DiodeSubsystem.all |> List.map DiodeSubsystem.toWireString)
                    @ ([ DiodeHealthy; DiodeDegraded; DiodeUnhealthy ]
                       |> List.map DiodeHealthState.toWireString)
                    @ (DiodeValidatorClass.all |> List.map DiodeValidatorClass.toWireString)
                    @ ([ DiodePreflightOk; DiodePreflightWarning; DiodePreflightError ]
                       |> List.map DiodePreflightOutcome.toWireString)
                    @ (DiodeCounter.all |> List.map DiodeCounter.toWireString)
                    |> Set.ofList

                let frame =
                    OperationalTelemetryDiode.header (DiodeVersion.create 1 2 3) 10L 20L
                    |> OperationalTelemetryDiode.withHealth (
                        DiodeSubsystem.all
                        |> List.map (fun s -> {
                            Subsystem = s
                            State = DiodeUnhealthy
                        })
                    )
                    |> OperationalTelemetryDiode.withPreflight (
                        DiodeValidatorClass.all
                        |> List.map (fun c -> {
                            Class = c
                            Outcome = DiodePreflightError
                            Validators = 1
                        })
                    )
                    |> OperationalTelemetryDiode.withCounters (
                        DiodeCounter.all |> List.map (fun c -> { Counter = c; Value = 7L })
                    )

                let rec stringValues (element: JsonElement) : string list = [
                    match element.ValueKind with
                    | JsonValueKind.String -> element.GetString()
                    | JsonValueKind.Object ->
                        for property in element.EnumerateObject() do
                            yield! stringValues property.Value
                    | JsonValueKind.Array ->
                        for item in element.EnumerateArray() do
                            yield! stringValues item
                    | _ -> ()
                ]

                let payload = OperationalTelemetryDiode.render frame
                let values = stringValues (JsonDocument.Parse payload).RootElement

                Expect.isNonEmpty values "the maximal frame does emit tokens, so this is not vacuous"

                for value in values do
                    Expect.isTrue
                        (vocabulary.Contains value)
                        (sprintf
                            "'%s' is not a declared wire token — every string on the wire is enumerated in the source"
                            value)

            testCase "consent is per-section: granting health does not grant counters"
            <| fun _ ->
                let frame =
                    OperationalTelemetryDiode.header (DiodeVersion.create 0 9 4) 3600L 1_780_000_000L
                    |> OperationalTelemetryDiode.withHealth [
                        {
                            Subsystem = StorageSubsystem
                            State = DiodeHealthy
                        }
                    ]
                    |> OperationalTelemetryDiode.withPreflight [
                        {
                            Class = DiodeSecurityClass
                            Outcome = DiodePreflightOk
                            Validators = 2
                        }
                    ]
                    |> OperationalTelemetryDiode.withCounters [
                        {
                            Counter = RequestsServed
                            Value = 99L
                        }
                    ]

                let projected =
                    OperationalTelemetryDiode.project
                        {
                            GrantedAtUnixSeconds = 0L
                            Sections = [ HealthSection ]
                        }
                        frame

                Expect.isNonEmpty projected.Health "health was consented to"
                Expect.isEmpty projected.Counters "counters were not"
                Expect.isEmpty projected.Preflight "nor preflight"

                Expect.equal
                    projected.Version
                    frame.Version
                    "the header always rides — it is the irreducible 'alive, on this build' and carries no deployment-chosen value"

            testCase "a grant covering no sections transmits the header only"
            <| fun _ ->
                let journal = DiodeTransmissionJournal() :> IDiodeTransmissionLog
                let sent = ResizeArray<string>()

                let send: DiodeTransmit =
                    fun payload -> async {
                        sent.Add payload
                        return Result.Ok()
                    }

                let frame =
                    OperationalTelemetryDiode.header (DiodeVersion.create 0 9 4) 3600L 1_780_000_000L
                    |> OperationalTelemetryDiode.withCounters [
                        {
                            Counter = RequestsServed
                            Value = 99L
                        }
                    ]

                OperationalTelemetryDiode.transmit
                    (DiodeGranted {
                        GrantedAtUnixSeconds = 0L
                        Sections = []
                    })
                    journal
                    send
                    (fun () -> 0L)
                    frame
                |> Async.RunSynchronously
                |> ignore

                let payload = Expect.wantSome (Seq.tryExactlyOne sent) "one transmission"
                Expect.isFalse (payload.Contains "requests-served") "the unconsented counter did not ride"

            testCase "a delivery failure is journalled locally and never transmitted"
            <| fun _ ->
                let journal = DiodeTransmissionJournal() :> IDiodeTransmissionLog

                let send: DiodeTransmit =
                    fun _ -> async { return Result.Error "connect timed out after 30s to collector.example" }

                let outcome =
                    OperationalTelemetryDiode.transmit
                        (DiodeGranted {
                            GrantedAtUnixSeconds = 0L
                            Sections = DiodeSection.all
                        })
                        journal
                        send
                        (fun () -> 0L)
                        (OperationalTelemetryDiode.header DiodeVersion.zero 0L 0L)
                    |> Async.RunSynchronously

                match outcome with
                | DiodeFailed reason -> Expect.stringContains reason "collect" "the reason is available locally"
                | other -> failtestf "expected DiodeFailed, got %A" other

                Expect.equal
                    (OperationalTelemetryDiode.bytesTransmitted journal)
                    0L
                    "a failed delivery counts as nothing transmitted"

            testCase "a throwing transport is a failure, not an escaping exception"
            <| fun _ ->
                let journal = DiodeTransmissionJournal() :> IDiodeTransmissionLog
                let send: DiodeTransmit = fun _ -> async { return failwith "DNS resolution failed" }

                let outcome =
                    OperationalTelemetryDiode.transmit
                        (DiodeGranted {
                            GrantedAtUnixSeconds = 0L
                            Sections = []
                        })
                        journal
                        send
                        (fun () -> 0L)
                        (OperationalTelemetryDiode.header DiodeVersion.zero 0L 0L)
                    |> Async.RunSynchronously

                match outcome with
                | DiodeFailed reason ->
                    Expect.stringContains reason "DNS" "a telemetry channel must never be what takes an appliance down"
                | other -> failtestf "expected DiodeFailed, got %A" other

            testCase "the same state renders byte-identically"
            <| fun _ ->
                let build (healthOrder: DiodeHealthReading list) =
                    OperationalTelemetryDiode.header (DiodeVersion.create 0 9 4) 1L 2L
                    |> OperationalTelemetryDiode.withHealth healthOrder
                    |> OperationalTelemetryDiode.render

                let a =
                    build [
                        {
                            Subsystem = StorageSubsystem
                            State = DiodeHealthy
                        }
                        {
                            Subsystem = AuthSubsystem
                            State = DiodeDegraded
                        }
                    ]

                let b =
                    build [
                        {
                            Subsystem = AuthSubsystem
                            State = DiodeDegraded
                        }
                        {
                            Subsystem = StorageSubsystem
                            State = DiodeHealthy
                        }
                    ]

                Expect.equal a b "list order is the declared vocabulary, so two appliances in the same state agree"

            testCase "a duplicate reading collapses rather than shipping twice"
            <| fun _ ->
                let frame =
                    OperationalTelemetryDiode.header DiodeVersion.zero 0L 0L
                    |> OperationalTelemetryDiode.withHealth [
                        {
                            Subsystem = StorageSubsystem
                            State = DiodeHealthy
                        }
                        {
                            Subsystem = StorageSubsystem
                            State = DiodeUnhealthy
                        }
                    ]

                let reading = List.exactlyOne frame.Health
                Expect.equal reading.State DiodeUnhealthy "last write wins"

            testCase "a negative counter is clamped — a count is a count"
            <| fun _ ->
                let frame =
                    OperationalTelemetryDiode.header DiodeVersion.zero 0L 0L
                    |> OperationalTelemetryDiode.withCounters [ { Counter = JobsFailed; Value = -17L } ]

                Expect.equal (List.exactlyOne frame.Counters).Value 0L "clamped"

            testCase "the journal is bounded so a long-running appliance cannot journal itself out of memory"
            <| fun _ ->
                let journal = DiodeTransmissionJournal 3
                let log = journal :> IDiodeTransmissionLog

                for index in 1..10 do
                    log.Record {
                        AtUnixSeconds = int64 index
                        Frame = OperationalTelemetryDiode.header DiodeVersion.zero 0L (int64 index)
                        Payload = None
                        Outcome = DiodeSuppressed
                    }

                Expect.equal (List.length log.Entries) 3 "retained to capacity"

                Expect.equal
                    (log.Entries |> List.map _.AtUnixSeconds)
                    [ 8L; 9L; 10L ]
                    "and it is the most recent frames that survive, oldest first"

            testCase "an unparseable version is zero, not an exception"
            <| fun _ ->
                Expect.equal (DiodeVersion.parse "not-a-version") DiodeVersion.zero "total"
                Expect.equal (DiodeVersion.parse null) DiodeVersion.zero "and null-safe"
                Expect.equal (DiodeVersion.parse "7") (DiodeVersion.create 7 0 0) "a partial version fills with zeroes"
        ]

        // ─── 488.D — the redacted support bundle ──────────────────────

        testList "488.D — redacted support bundle" [

            testCase "the vocabulary is derived from the declared classifications, not a hand-written list"
            <| fun _ ->
                Expect.isTrue (vocabulary.ClassifiedNames.Contains "email") "a Pii field"
                Expect.isTrue (vocabulary.ClassifiedNames.Contains "netamount") "a Financial field"
                Expect.isTrue (vocabulary.ClassifiedNames.Contains "negotiatedrate") "a Confidential field"

                Expect.isFalse
                    (vocabulary.ClassifiedNames.Contains "currency")
                    "a Public field is not content-bearing and stays readable — a bundle with everything masked diagnoses nothing"

            testCase "a dotted field path masks under both its full path and its leaf"
            <| fun _ ->
                Expect.isTrue
                    (vocabulary.ClassifiedNames.Contains "profile.homeaddress")
                    "a flattened log line may carry the dotted path"

                Expect.isTrue
                    (vocabulary.ClassifiedNames.Contains "homeaddress")
                    "and a nested JSON property carries only the leaf — masking one spelling would leave the other exposed"

            testCase "Confidential is masked here though the access gate admits it"
            <| fun _ ->
                Expect.isFalse
                    (ClassificationLevel.isSensitive Confidential)
                    "the access gate treats Confidential as readable by any authenticated caller"

                Expect.isTrue
                    (ApplianceSupportBundle.isContentBearing Confidential)
                    "the bundle masks it anyway — that judgement is about callers inside the deployment, and a bundle leaves it"

            testCase "no content-bearing field survives a masked bundle"
            <| fun _ ->
                let sections = [
                    jsonSection
                        "config.json"
                        """{"StorageEndpoint":"https://minio.internal","ApiKey":"sk-live-1234","Nested":{"AuthToken":"abcdef"}}"""
                    jsonSection
                        "entities.json"
                        """{"Customer":{"Email":"ada@example.com","Profile":{"HomeAddress":"1 Analytical Engine Way"},"Id":42}}"""
                    {
                        ApplianceSupportBundle.BundleSection.Name = "audit-tail.jsonl"
                        Shape = ApplianceSupportBundle.JsonLinesSection
                        Content =
                            """{"EventType":"InvoiceRaised","NetAmount":18400.55,"Currency":"GBP"}
{"EventType":"CustomerUpdated","Email":"grace@example.com"}"""
                    }
                ]

                let masked = ApplianceSupportBundle.mask vocabulary sections

                Expect.isEmpty
                    (ApplianceSupportBundle.survivingContentFields vocabulary masked)
                    "the acceptance criterion, measured by the shipped coverage function"

                let all = masked |> List.map _.Content |> String.concat "\n"

                Expect.isFalse (all.Contains "ada@example.com") "the Pii value is gone"
                Expect.isFalse (all.Contains "1 Analytical Engine Way") "the Spi value is gone"
                Expect.isFalse (all.Contains "18400.55") "the Financial value is gone"
                Expect.isFalse (all.Contains "sk-live-1234") "and the suffix floor still catches credentials"
                Expect.isFalse (all.Contains "abcdef") "at any nesting depth"

                Expect.stringContains all "GBP" "a Public field survives, so the bundle is still diagnostic"
                Expect.stringContains all "minio.internal" "and so does an unclassified config shape"
                Expect.stringContains all "InvoiceRaised" "and the event types that make a log readable"

            testCase "…and the coverage check can actually FAIL — falsified against the unmasked bundle"
            <| fun _ ->
                // Same discipline as the diode closure walk: a coverage
                // function that had stopped matching would report an empty
                // list for a bundle full of content.
                let unmasked = [
                    jsonSection "entities.json" """{"Customer":{"Email":"ada@example.com","Id":42}}"""
                ]

                let surviving = ApplianceSupportBundle.survivingContentFields vocabulary unmasked

                Expect.equal
                    surviving
                    [ "entities.json", "Email" ]
                    "the unmasked Pii field is reported by name and section"

            testCase "a section whose content does not parse is masked WHOLESALE"
            <| fun _ ->
                let sections = [
                    jsonSection "broken.json" "this is not json, and it mentions ada@example.com"
                ]

                let masked = ApplianceSupportBundle.mask vocabulary sections
                let content = (List.exactlyOne masked).Content

                Expect.isFalse
                    (content.Contains "ada@example.com")
                    "an appliance bundle does not forward content it cannot walk"

                Expect.stringContains content "<masked:" "it is replaced by a length"

            testCase "an Opaque section is masked wholesale by declaration"
            <| fun _ ->
                let sections: ApplianceSupportBundle.BundleSection list = [
                    {
                        Name = "heap-dump.bin"
                        Shape = ApplianceSupportBundle.Opaque
                        Content = "raw bytes containing grace@example.com somewhere inside"
                    }
                ]

                let content =
                    (ApplianceSupportBundle.mask vocabulary sections |> List.exactlyOne).Content

                Expect.isFalse
                    (content.Contains "grace@example.com")
                    "Opaque is the honest classification, and it masks everything"

            testCase "one unparseable log line does not mask the whole log"
            <| fun _ ->
                let sections: ApplianceSupportBundle.BundleSection list = [
                    {
                        Name = "audit-tail.jsonl"
                        Shape = ApplianceSupportBundle.JsonLinesSection
                        Content =
                            """{"EventType":"InvoiceRaised","Currency":"GBP"}
not json at all
{"EventType":"CustomerUpdated","Email":"grace@example.com"}"""
                    }
                ]

                let content =
                    (ApplianceSupportBundle.mask vocabulary sections |> List.exactlyOne).Content

                Expect.stringContains content "InvoiceRaised" "the parseable lines are still readable"
                Expect.isFalse (content.Contains "grace@example.com") "the classified field is masked"
                Expect.isFalse (content.Contains "not json at all") "and only the bad line is masked wholesale"

            testCase "a deployment declaring no classifications still gets the suffix floor"
            <| fun _ ->
                let sections = [
                    jsonSection "config.json" """{"ApiKey":"sk-live-1234","Endpoint":"https://minio.internal"}"""
                ]

                let content =
                    (ApplianceSupportBundle.mask ApplianceSupportBundle.floorOnly sections
                     |> List.exactlyOne)
                        .Content

                Expect.isFalse (content.Contains "sk-live-1234") "the floor is not nothing"
                Expect.stringContains content "minio.internal" "and it does not over-mask"

            testCase "the masked value preserves shape without content"
            <| fun _ ->
                let sections = [ jsonSection "entities.json" """{"Email":"ada@example.com","Other":null}""" ]

                let content =
                    (ApplianceSupportBundle.mask vocabulary sections |> List.exactlyOne).Content

                Expect.stringContains
                    content
                    (ApplianceSupportBundle.maskedValue "ada@example.com".Length)
                    "a length distinguishes an empty column from a populated one, which is often the whole question"

            testCase "there is no route, endpoint, or scheduled emission — the operator generates and forwards"
            <| fun _ ->
                // The structural half of "the vendor never pulls": the only
                // outbound channel an appliance has is the diode, and the
                // diode's schema has no string field, so a bundle cannot
                // ride it. This test pins the property that makes that
                // argument hold rather than restating the policy.
                Expect.isFalse
                    (reachableTypes typeof<OperationalTelemetryFrame> |> List.contains typeof<string>)
                    "a bundle is text; the diode cannot carry text; therefore the diode cannot carry a bundle"
        ]
    ]