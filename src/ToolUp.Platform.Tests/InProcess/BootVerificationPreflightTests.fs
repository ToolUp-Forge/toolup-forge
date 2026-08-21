module ToolUp.Platform.Tests.InProcess.BootVerificationPreflightTests

open System
open System.IO
open Expecto
open ToolUp.Platform
open ToolUp.Platform.Tests.InProcess.BuildTranscriptTests

// ─── Phase 657 — boot verification preflight + the verified profile ──
//
// Two claims carry this phase, and each is probed in BOTH directions
// rather than only the one that would pass:
//
//   * An honest deployment verifies. Probed because a preflight that
//     refused everything would satisfy every tamper test perfectly and
//     be useless.
//   * A tampered one does not. Probed separately per axis — a seal
//     broken, an artifact edited, a binding swapped from another deploy,
//     a composition that gained a module — because a check that only
//     ever caught one of them would look identical on a summary line.
//
// The gate half is probed the same way: a composition inside its
// declared envelope serves, and one exceeding it is refused with the
// component and the envelope named on the audit row. A refusal nobody
// can attribute is not enforcement, it is noise.

// ─── Doubles ─────────────────────────────────────────────────────────

/// Records every `Record` call so a test can assert audit shape + count.
type private RecordingAuditLog() =
    let recorded = ResizeArray<string * AuditEvent>()
    member _.Events = List.ofSeq recorded

    /// Wait for `count` rows to land, up to a generous bound.
    ///
    /// The gate's deny observer is fire-and-forget by contract — a
    /// refusal must not wait on an audit backend — so a test asserting
    /// on the row has to wait for the write it deliberately did not
    /// await. Bounded rather than a sleep: it returns the instant the
    /// row arrives, and a genuine regression still fails rather than
    /// hanging.
    member _.WaitFor(count: int) =
        let deadline = DateTime.UtcNow.AddSeconds 5.0

        while recorded.Count < count && DateTime.UtcNow < deadline do
            Threading.Thread.Sleep 5

    interface IAuditLog with
        member _.Record(scopeId, audit) = async { recorded.Add(scopeId, audit) }

        member _.GetAuditTrail(_, _, _) = async { return recorded |> Seq.map snd |> List.ofSeq }

/// A dispatcher that declares no isolation posture — the shape every
/// pre-478 companion has.
type private PlainDispatcher() =
    let submitted = ResizeArray<ExternalWorkSpec>()
    member _.Submitted = List.ofSeq submitted

    interface IExternalComputeDispatcher with
        member _.Backend = "plain-backend"

        member _.Submit(_scopeId, spec) = async {
            submitted.Add spec

            return
                Ok {
                    HandleId = Guid.Empty
                    Backend = "plain-backend"
                    ScopeId = "scope"
                    NativeRef = "ref-1"
                    SubmittedAt = DateTime.UnixEpoch
                }
        }

        member _.Poll(_handle) = async { return ExternalOutcome.Pending }

        member _.Cancel(_handle) = async { return () }

// ─── Fixtures ────────────────────────────────────────────────────────

let private manifest: DeployManifest = {
    DeployManifest.empty with
        App = {
            Name = "Example"
            Slug = "example"
            Region = "eu-west"
        }
        Runtime = {
            DeployManifest.empty.Runtime with
                Framework = "dotnet:10"
        }
}

let private baseRecord =
    DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest DeployProvenance.none

/// A composition with one of every shape the manifest carries, so a
/// perturbation of any of them has somewhere to land.
let private composition: CompositionManifest =
    CompositionManifest.build
        [ CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports") ]
        [
            CompositionManifest.companionSlotEntry "IBlobStorage"
            CompositionManifest.companionImplEntry "IAuditSink" "ledger"
        ]
        [ CompositionManifest.dataTypeEntry "sales" ] [ CompositionManifest.toolEntry "reports.summarise" ] [
            CompositionManifest.knob "ProcessProfile" "AllInOne"
            CompositionManifest.knob "RateLimiter" "NoRateLimiter"
        ]

let private sealer = StubSealer "boot-secret" :> IDeployRecordSealer

let private optionsFor
    (profile: CompositionProfile)
    (policy: BootVerificationPolicy)
    (record: SealedDeployRecord option)
    (binding: SealedCompositionBinding option)
    (auditLog: IAuditLog option)
    : BootVerificationOptions =
    {
        Profile = profile
        Policy = policy
        Sealer = sealer
        Locate = (fun _ -> None)
        Record = record
        Binding = binding
        Transcript = None
        AuditLog = auditLog
        ScopeId = BootVerificationPreflight.PlatformScopeId
    }

/// Seal a record + a binding over `recordedComposition`, the honest pair
/// a deployment is started with.
let private sealPair (record: DeployRecord) (recordedComposition: CompositionManifest) =
    async {
        let! seal = sealer.Seal(DeployRecords.canonicalBytes record)

        let seal =
            match seal with
            | Ok s -> s
            | Error e -> failwithf "fixture could not seal the record: %s" e

        let binding = BootVerificationPreflight.bindingFor record recordedComposition
        let! sealedBinding = BootVerificationPreflight.sealBinding sealer binding

        let sealedBinding =
            match sealedBinding with
            | Ok b -> b
            | Error e -> failwithf "fixture could not seal the binding: %s" e

        return { Record = record; Seal = seal }, sealedBinding
    }
    |> Async.RunSynchronously

let private verdictOf (options: BootVerificationOptions) (observed: CompositionManifest) =
    BootVerificationPreflight.verify options observed |> Async.RunSynchronously

// ─── Capability fixtures ─────────────────────────────────────────────

let private reportsId = ComponentId.ofModule "reports"

let private declaredPure: CapabilitySignature =
    Map.ofList [ reportsId, CompanionCapability.identity ]

let private effecting: CompanionCapability = {
    CompanionCapability.identity with
        Effect = Effecting
}

// ─── Tests ───────────────────────────────────────────────────────────

let tests =
    testList "BootVerificationPreflight" [

        // ─── Canonical form ──────────────────────────────────────────

        testList "composition canonical form" [
            test "the same composition canonicalises identically however its entries were ordered" {
                let shuffled =
                    CompositionManifest.build
                        [ CompositionManifest.moduleEntry ("Reports", ComponentId.ofModule "reports") ]
                        [
                            CompositionManifest.companionImplEntry "IAuditSink" "ledger"
                            CompositionManifest.companionSlotEntry "IBlobStorage"
                        ]
                        [ CompositionManifest.dataTypeEntry "sales" ] [
                            CompositionManifest.toolEntry "reports.summarise"
                        ] [
                            CompositionManifest.knob "RateLimiter" "NoRateLimiter"
                            CompositionManifest.knob "ProcessProfile" "AllInOne"
                        ]

                Expect.equal
                    (BootVerificationPreflight.compositionCanonicalForm shuffled)
                    (BootVerificationPreflight.compositionCanonicalForm composition)
                    "accumulator walk order is an artefact of the composition root and must not reach the canonical form"
            }

            test "a composition that gained a module canonicalises differently" {
                let widened = {
                    composition with
                        Modules =
                            composition.Modules
                            @ [ CompositionManifest.moduleEntry ("Extra", ComponentId.ofModule "extra") ]
                }

                Expect.notEqual
                    (BootVerificationPreflight.compositionCanonicalForm widened)
                    (BootVerificationPreflight.compositionCanonicalForm composition)
                    "a canonical form that dropped the module set would pass every same-inputs test and catch nothing"
            }

            test "a composition whose knob value moved canonicalises differently" {
                let flipped = {
                    composition with
                        ConfigKnobs = [
                            CompositionManifest.knob "ProcessProfile" "AllInOne"
                            CompositionManifest.knob "RateLimiter" "InMemoryRateLimiter"
                        ]
                }

                Expect.notEqual
                    (BootVerificationPreflight.compositionCanonicalForm flipped)
                    (BootVerificationPreflight.compositionCanonicalForm composition)
                    "config knobs shape what gets composed, so they are covered"
            }

            test "the framing is injective across a field boundary" {
                let a = {
                    composition with
                        ConfigKnobs = [ CompositionManifest.knob "ab" "c" ]
                }

                let b = {
                    composition with
                        ConfigKnobs = [ CompositionManifest.knob "a" "bc" ]
                }

                Expect.notEqual
                    (BootVerificationPreflight.compositionCanonicalForm a)
                    (BootVerificationPreflight.compositionCanonicalForm b)
                    "without length framing these two concatenate to the same text"
            }

            test "a binding over a different record canonicalises differently" {
                let one = BootVerificationPreflight.bindingFor baseRecord composition

                let two =
                    BootVerificationPreflight.bindingFor
                        {
                            baseRecord with
                                DeployId = "deploy-2"
                        }
                        composition

                Expect.notEqual
                    (BootVerificationPreflight.bindingCanonicalForm one)
                    (BootVerificationPreflight.bindingCanonicalForm two)
                    "the record digest is what ties a binding to its deploy, so it is covered by the seal"
            }
        ]

        // ─── Comparison ──────────────────────────────────────────────

        testList "compare" [
            test "identical compositions report no drift" {
                Expect.isEmpty
                    (BootVerificationPreflight.compare composition composition)
                    "a comparison that found drift in a composition against itself would be measuring nothing"
            }

            test "an added component is reported, naming its kind and id" {
                let observed = {
                    composition with
                        CompanionSlots =
                            composition.CompanionSlots
                            @ [ CompositionManifest.companionImplEntry "IAuditSink" "shadow" ]
                }

                let drift = BootVerificationPreflight.compare composition observed

                Expect.equal drift.Length 1 "exactly one difference"

                let rendered = CompositionDrift.describe drift.Head

                Expect.stringContains rendered "shadow" "the finding names the component that appeared"
            }

            test "a removed component is reported" {
                let observed = { composition with Tools = [] }
                let drift = BootVerificationPreflight.compare composition observed

                Expect.equal drift.Length 1 "exactly one difference"

                Expect.stringContains
                    (CompositionDrift.describe drift.Head)
                    "reports.summarise"
                    "the finding names the component that vanished"
            }

            test "a knob whose value moved is reported with both sides" {
                let observed = {
                    composition with
                        ConfigKnobs = [
                            CompositionManifest.knob "ProcessProfile" "WebOnly"
                            CompositionManifest.knob "RateLimiter" "NoRateLimiter"
                        ]
                }

                let drift = BootVerificationPreflight.compare composition observed
                let rendered = drift |> List.map CompositionDrift.describe |> String.concat " | "

                Expect.stringContains rendered "AllInOne" "the recorded value is named"
                Expect.stringContains rendered "WebOnly" "the observed value is named"
            }

            test "every difference is reported, not just the first" {
                let observed = {
                    composition with
                        Modules = []
                        Tools = []
                        ConfigKnobs = []
                }

                let drift = BootVerificationPreflight.compare composition observed

                Expect.isGreaterThan
                    drift.Length
                    3
                    "an operator holding a drifted deployment wants the whole list in one boot"
            }

            test "a null list on the read path is coerced, not faulted on" {
                let observed = {
                    composition with
                        Metrics = Unchecked.defaultof<ComponentEntry list>
                }

                Expect.isEmpty
                    (BootVerificationPreflight.compare composition observed)
                    "a binding round-tripped through a serialiser predating a list deserialises it as null"
            }
        ]

        // ─── The preflight, both directions ──────────────────────────

        testList "verify" [
            test "an honest deployment verifies" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                Expect.equal
                    (verdictOf options composition)
                    BootVerificationVerdict.Verified
                    "the running composition IS the one the sealed record covers"
            }

            test "a composition that gained a module is Drifted, naming the module" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let observed = {
                    composition with
                        Modules =
                            composition.Modules
                            @ [ CompositionManifest.moduleEntry ("Payouts", ComponentId.ofModule "payouts") ]
                }

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                match verdictOf options observed with
                | BootVerificationVerdict.Drifted drift ->
                    let rendered = drift |> List.map CompositionDrift.describe |> String.concat " | "

                    Expect.stringContains rendered "payouts" "the verdict names what moved"
                | other -> failtestf "expected Drifted, got %A" other
            }

            test "a record edited after sealing does not verify" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let tampered = {
                    sealedRecord with
                        Record = {
                            sealedRecord.Record with
                                TenantId = "tenant-elsewhere"
                        }
                }

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some tampered)
                        (Some sealedBinding)
                        None

                match verdictOf options composition with
                | BootVerificationVerdict.VerificationFailed(failures, _) ->
                    Expect.isNonEmpty failures "the seal no longer covers the record"
                | other -> failtestf "expected VerificationFailed, got %A" other
            }

            test "a binding minted for another deploy is refused, naming the mismatch" {
                let sealedRecord, _ = sealPair baseRecord composition

                let _, foreignBinding =
                    sealPair
                        {
                            baseRecord with
                                DeployId = "deploy-elsewhere"
                        }
                        composition

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some foreignBinding)
                        None

                match verdictOf options composition with
                | BootVerificationVerdict.VerificationFailed(_, bindingFindings) ->
                    let rendered = String.concat " | " bindingFindings

                    Expect.stringContains
                        rendered
                        "different deploy record"
                        "both seals are individually genuine — the pairing is what fails"
                | other -> failtestf "expected VerificationFailed, got %A" other
            }

            test "a binding edited after sealing does not verify" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let tampered = {
                    sealedBinding with
                        Binding = {
                            sealedBinding.Binding with
                                Composition = { composition with Tools = [] }
                        }
                }

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some tampered)
                        None

                match verdictOf options composition with
                | BootVerificationVerdict.VerificationFailed(_, bindingFindings) ->
                    Expect.isNonEmpty bindingFindings "editing the recorded composition breaks its own seal"
                | other -> failtestf "expected VerificationFailed, got %A" other
            }

            test "a recorded artifact that no longer matches its digest is reported by file" {
                let root = Path.Combine(Path.GetTempPath(), $"toolup-657-{Guid.NewGuid():N}")
                Directory.CreateDirectory root |> ignore

                try
                    let artifact = Path.Combine(root, "app.dll")
                    File.WriteAllText(artifact, "honest bytes")

                    let provenance =
                        DeployProvenance.withArtifacts (DeployRecords.artifactsUnder root) DeployProvenance.none

                    let record = DeployRecord.create "deploy-1" "tenant-1" "build-1" manifest provenance

                    let sealedRecord, sealedBinding = sealPair record composition

                    File.WriteAllText(artifact, "swapped bytes")

                    let options = {
                        optionsFor
                            CompositionProfile.Standard
                            BootVerificationPolicy.LogAndServe
                            (Some sealedRecord)
                            (Some sealedBinding)
                            None with
                            Locate = DeployRecords.locateUnder root
                    }

                    match verdictOf options composition with
                    | BootVerificationVerdict.VerificationFailed(failures, _) ->
                        let rendered =
                            failures
                            |> List.map DeployRecords.DeployRecordVerificationFailure.describe
                            |> String.concat " | "

                        Expect.stringContains rendered "app.dll" "the finding an operator acts on names the file"
                    | other -> failtestf "expected VerificationFailed, got %A" other
                finally
                    try
                        Directory.Delete(root, true)
                    with _ ->
                        ()
            }

            test "no sealed record is Unsealed, not a failure" {
                let options =
                    optionsFor CompositionProfile.Standard BootVerificationPolicy.LogAndServe None None None

                match verdictOf options composition with
                | BootVerificationVerdict.Unsealed reason ->
                    Expect.stringContains reason "sealed deploy record" "the reason says what was absent"
                | other -> failtestf "expected Unsealed, got %A" other
            }

            test "a sealed record with no composition binding is Unsealed" {
                let sealedRecord, _ = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        None
                        None

                match verdictOf options composition with
                | BootVerificationVerdict.Unsealed _ -> ()
                | other -> failtestf "expected Unsealed, got %A" other
            }
        ]

        // ─── Policy + profile ────────────────────────────────────────

        testList "policy" [
            test "the log-and-serve default serves a drifted composition (GP 11)" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition
                let observed = { composition with Tools = [] }

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                match BootVerificationPreflight.run options observed |> Async.RunSynchronously with
                | Ok result ->
                    Expect.isFalse result.RefusedStart "an existing deployment adopting the check changes no behaviour"

                    Expect.equal
                        (BootVerificationVerdict.label result.Verdict)
                        "drifted"
                        "it still says what it found — it just does not act on it"
                | Error _ -> failtest "log-and-serve must serve"
            }

            test "refuse-on-drift refuses a drifted composition without the whole profile" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition
                let observed = { composition with Tools = [] }

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.RefuseOnDrift
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                match BootVerificationPreflight.run options observed |> Async.RunSynchronously with
                | Error result -> Expect.isTrue result.RefusedStart "the policy is adoptable on its own"
                | Ok _ -> failtest "refuse-on-drift must refuse"
            }

            test "the verified profile overrides a log-and-serve policy" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition
                let observed = { composition with Tools = [] }

                let options =
                    optionsFor
                        CompositionProfile.Verified
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                match BootVerificationPreflight.run options observed |> Async.RunSynchronously with
                | Error result ->
                    Expect.equal
                        result.Policy
                        BootVerificationPolicy.RefuseOnDrift
                        "a profile that could be configured back to serving on drift would not be a profile"
                | Ok _ -> failtest "the verified profile must refuse a drifted composition"
            }

            test "the verified profile refuses an unsealed deployment" {
                let options =
                    optionsFor CompositionProfile.Verified BootVerificationPolicy.LogAndServe None None None

                match BootVerificationPreflight.run options composition |> Async.RunSynchronously with
                | Error result ->
                    Expect.equal (BootVerificationVerdict.label result.Verdict) "unsealed" "and says which of the two"
                | Ok _ -> failtest "an unsealed composition cannot serve under the verified profile"
            }

            test "the verified profile serves an in-envelope composition" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Verified
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                match BootVerificationPreflight.run options composition |> Async.RunSynchronously with
                | Ok result -> Expect.isFalse result.RefusedStart "an honest deployment is not refused"
                | Error result -> failtestf "expected a serve, got %A" result.Verdict
            }
        ]

        // ─── The audit path ──────────────────────────────────────────

        testList "audit path" [
            test "the affirmative verdict is recorded too" {
                let auditLog = RecordingAuditLog()
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        (Some(auditLog :> IAuditLog))

                BootVerificationPreflight.run options composition
                |> Async.RunSynchronously
                |> ignore

                match auditLog.Events with
                | [ scopeId, CompositionVerificationRecorded payload ] ->
                    Expect.equal scopeId "_platform" "a boot verdict belongs to the deployment, not a tenant"
                    Expect.equal payload.Verdict "verified" ""

                    Expect.isEmpty
                        payload.Findings
                        "absence of a row means the check did not run — a different fact from a clean one"
                | other -> failtestf "expected exactly one verification row, got %A" other
            }

            test "a refused start is recorded before the process is told to stop" {
                let auditLog = RecordingAuditLog()
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Verified
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        (Some(auditLog :> IAuditLog))

                let observed = { composition with Tools = [] }

                BootVerificationPreflight.run options observed
                |> Async.RunSynchronously
                |> ignore

                match auditLog.Events with
                | [ _, CompositionVerificationRecorded payload ] ->
                    Expect.isTrue payload.RefusedStart "the row an incident review most needs"
                    Expect.isNonEmpty payload.Findings "and it carries what moved"
                    Expect.equal payload.Profile "verified" ""
                | other -> failtestf "expected exactly one verification row, got %A" other
            }

            test "no audit log composed records nothing and still decides" {
                let options =
                    optionsFor CompositionProfile.Standard BootVerificationPolicy.LogAndServe None None None

                match BootVerificationPreflight.run options composition |> Async.RunSynchronously with
                | Ok _ -> ()
                | Error _ -> failtest "a deployment with no audit log composed still serves under the default"
            }
        ]

        // ─── The mandatory capability gate ───────────────────────────

        testList "mandatory capability gate" [
            test "standard with no signature leaves the gate disabled (GP 11)" {
                match VerifiedCompositionProfile.resolveGate CompositionProfile.Standard ignore None with
                | Ok gate ->
                    Expect.equal
                        (gate.Check reportsId effecting)
                        CapabilityGateDecision.Granted
                        "the pre-657 passthrough, unchanged"
                | Error refusal -> failtestf "expected the disabled gate, got %A" refusal
            }

            test "the verified profile refuses a composition that declared no envelopes" {
                match VerifiedCompositionProfile.resolveGate CompositionProfile.Verified ignore None with
                | Error CapabilityGateUndeclared ->
                    Expect.stringContains
                        (CompositionProfileRefusal.describe CapabilityGateUndeclared)
                        "CapabilitySignature"
                        "the refusal names what to supply"
                | other ->
                    failtestf
                        "a mandatory gate with nothing to check against would grant everything while presenting as enforcement: %A"
                        other
            }

            test "the verified profile grants an access inside the declared envelope" {
                let declared: CapabilitySignature = Map.ofList [ reportsId, effecting ]

                match VerifiedCompositionProfile.resolveGate CompositionProfile.Verified ignore (Some declared) with
                | Ok gate ->
                    Expect.equal
                        (gate.Check reportsId effecting)
                        CapabilityGateDecision.Granted
                        "a module exercising exactly what it declared serves"
                | Error refusal -> failtestf "expected a gate, got %A" refusal
            }

            test "a module exceeding its declared envelope is refused, audited, with module and envelope named" {
                let auditLog = RecordingAuditLog()

                let gate =
                    match
                        VerifiedCompositionProfile.auditedGate
                            (auditLog :> IAuditLog)
                            "_platform"
                            CompositionProfile.Verified
                            (Some declaredPure)
                    with
                    | Ok g -> g
                    | Error refusal -> failtestf "expected a gate, got %A" refusal

                match gate.Check reportsId effecting with
                | CapabilityGateDecision.Denied denial ->
                    Expect.equal denial.Component reportsId "the refusal is fail-closed and names the component"
                | CapabilityGateDecision.Granted -> failtest "an effecting access beyond a pure declaration is refused"

                auditLog.WaitFor 1

                match auditLog.Events with
                | [ scopeId, CompositionCapabilityRefused payload ] ->
                    Expect.equal scopeId "_platform" ""
                    Expect.stringContains payload.Component "reports" "the module is named"

                    Expect.stringContains
                        payload.Declared
                        "pure"
                        "the envelope it declared is named — a refusal nobody can attribute is noise"

                    Expect.stringContains payload.Required "effecting" "and what it attempted"
                    Expect.equal payload.Profile "verified" ""
                | other -> failtestf "expected exactly one refusal row, got %A" other
            }

            test "an undeclared component is refused by default-deny, and the refusal is audited" {
                let auditLog = RecordingAuditLog()

                let gate =
                    match
                        VerifiedCompositionProfile.auditedGate
                            (auditLog :> IAuditLog)
                            "_platform"
                            CompositionProfile.Verified
                            (Some Map.empty)
                    with
                    | Ok g -> g
                    | Error refusal -> failtestf "expected a gate, got %A" refusal

                match gate.Check (ComponentId.ofModule "unknown") effecting with
                | CapabilityGateDecision.Denied _ ->
                    auditLog.WaitFor 1
                    Expect.equal auditLog.Events.Length 1 "the deny reached the audit path"
                | CapabilityGateDecision.Granted ->
                    failtest "an undeclared component resolves to the identity, so an effecting access is denied"
            }

            test "a grant notifies the observer not at all" {
                // Deliberately a SYNCHRONOUS observer rather than the audited
                // one: `auditingObserver` schedules its write, so asserting
                // an empty audit log straight after a grant would pass
                // whether or not the observer fired. Counting the
                // synchronous call answers the question the assertion is
                // actually about.
                let denials = ResizeArray<CapabilityDenial>()
                let declared: CapabilitySignature = Map.ofList [ reportsId, effecting ]

                let gate =
                    match
                        VerifiedCompositionProfile.resolveGate CompositionProfile.Verified denials.Add (Some declared)
                    with
                    | Ok g -> g
                    | Error refusal -> failtestf "expected a gate, got %A" refusal

                gate.Check reportsId effecting |> ignore

                Expect.isEmpty denials "an audit row per permitted access would drown the refusals it exists for"

                gate.Check reportsId { effecting with Readiness = DevOnly } |> ignore

                Expect.equal denials.Count 1 "and the observer does fire when there is something to report"
            }
        ]

        // ─── The isolated-execution enforcement layer ────────────────

        testList "isolated-execution enforcement" [
            test "standard returns the dispatcher exactly as handed over (GP 13)" {
                let inner = PlainDispatcher() :> IExternalComputeDispatcher

                Expect.isTrue
                    (Object.ReferenceEquals(
                        VerifiedCompositionProfile.enforceExecutionProfile CompositionProfile.Standard inner,
                        inner
                    ))
                    "no decorator, no branch, no allocation on the path a standard deployment takes"
            }

            test "the verified profile refuses isolated work to a backend that declares no posture" {
                let inner = PlainDispatcher()

                let dispatcher =
                    VerifiedCompositionProfile.enforceExecutionProfile
                        CompositionProfile.Verified
                        (inner :> IExternalComputeDispatcher)

                let spec = ExternalWorkSpec.create "fit-model" "{}" |> ExternalWorkSpec.isolated

                match dispatcher.Submit("scope", spec) |> Async.RunSynchronously with
                | Error _ -> Expect.isEmpty inner.Submitted "the payload never leaves the process"
                | Ok _ -> failtest "an Isolated spec a backend cannot honour is refused before submission"
            }

            test "the verified profile still submits standard work" {
                let inner = PlainDispatcher()

                let dispatcher =
                    VerifiedCompositionProfile.enforceExecutionProfile
                        CompositionProfile.Verified
                        (inner :> IExternalComputeDispatcher)

                let spec = ExternalWorkSpec.create "fit-model" "{}"

                match dispatcher.Submit("scope", spec) |> Async.RunSynchronously with
                | Ok _ -> Expect.equal inner.Submitted.Length 1 "the profile constrains isolation, not throughput"
                | Error e -> failtestf "standard work must still submit: %A" e
            }

            test "verifyIsolation asks nothing under standard" {
                Expect.isOk
                    (VerifiedCompositionProfile.verifyIsolation
                        CompositionProfile.Standard
                        "b"
                        ExecutionProfile.Isolated
                        IsolationPosture.standardOnly)
                    "Phase 478's per-submission check remains the gate outside the profile"
            }

            test "verifyIsolation names every clause a backend is missing" {
                match
                    VerifiedCompositionProfile.verifyIsolation
                        CompositionProfile.Verified
                        "plain-backend"
                        ExecutionProfile.Isolated
                        IsolationPosture.standardOnly
                with
                | Error(IsolationPostureShortfall(backend, missing)) ->
                    Expect.equal backend "plain-backend" "the backend is named"
                    Expect.equal missing.Length 3 "two of three is not a weaker clean room"
                | other -> failtestf "expected a shortfall refusal, got %A" other
            }

            test "verifyIsolation accepts a backend that asserts the clauses" {
                Expect.isOk
                    (VerifiedCompositionProfile.verifyIsolation
                        CompositionProfile.Verified
                        "sandboxed"
                        ExecutionProfile.Isolated
                        (IsolationPosture.clauses "gvisor"))
                    "a declaring backend is not refused — a control nobody can satisfy is a control nobody leaves on"
            }
        ]
    ]