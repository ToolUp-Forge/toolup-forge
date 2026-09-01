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
    let gate = obj ()
    member _.Events = lock gate (fun () -> List.ofSeq recorded)

    /// Wait for `count` rows to land.
    ///
    /// The gate's deny observer is fire-and-forget by contract
    /// (`Async.Start` onto the thread pool) — a refusal must not wait
    /// on an audit backend — so a test asserting on the row has to wait
    /// for the write it deliberately did not await. Event-driven, not
    /// polled: `Record` pulses the monitor, so this returns the instant
    /// the `count`-th row lands, and the cap only bites when the rows
    /// are not coming at all. A timeout fails HERE, naming what did
    /// arrive — a 5s wall-clock poll once expired under machine load
    /// and the failure blamed the downstream ledger claim instead of
    /// the scheduler (2026-08-24, VerifyAll beside a second pack).
    member _.WaitFor(count: int) =
        let cap = TimeSpan.FromSeconds 30.0
        let sw = Diagnostics.Stopwatch.StartNew()

        lock gate (fun () ->
            while recorded.Count < count && sw.Elapsed < cap do
                let remaining = cap - sw.Elapsed

                if remaining > TimeSpan.Zero then
                    Threading.Monitor.Wait(gate, remaining) |> ignore

            if recorded.Count < count then
                failtestf
                    "audit wait: %d of %d expected row(s) arrived within %.0fs — with an event-driven wait this long the deny observer's write never happened (it is not merely late); the ledger assertion after this wait has NOT been evaluated"
                    recorded.Count
                    count
                    cap.TotalSeconds)

    interface IAuditLog with
        member _.Record(scopeId, audit) = async {
            lock gate (fun () ->
                recorded.Add(scopeId, audit)
                Threading.Monitor.PulseAll gate)
        }

        member _.GetAuditTrail(_, _, _) = async { return lock gate (fun () -> recorded |> Seq.map snd |> List.ofSeq) }

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

// ─── Phase 694 fixtures — canonical-method visibility ────────────────

/// A composition carrying one grounding metric, optionally declaring a
/// canonical-method selector for it.
let private grounded (selector: string option) : CompositionManifest =
    let withMetric =
        CompositionManifest.build [] [] [] [] [ CompositionManifest.knob "FactStore" "EnabledFactStore" ]
        |> CompositionManifest.withGrounding [ CompositionManifest.metricEntry "revenue" ] []

    match selector with
    | None -> withMetric |> CompositionManifest.withCanonicalMethods []
    | Some s ->
        withMetric
        |> CompositionManifest.withCanonicalMethods [ { MetricId = "revenue"; Selector = s } ]

/// The pre-694 projection of a manifest, as a binding sealed before that
/// phase actually deserialises: no schema field at all (so `0`) and no
/// canonical-method list (so `null`).
///
/// Both halves matter. A test that set `SchemaVersion = 1` and
/// `CanonicalMethods = []` would exercise the version gate without ever
/// touching the null-list read path, which is the half that faults rather
/// than misreports.
let private asLegacy (manifest: CompositionManifest) : CompositionManifest = {
    manifest with
        SchemaVersion = 0
        CanonicalMethods = Unchecked.defaultof<MetricCanonicalMethod list>
}

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

        // ─── Phase 694 — the canonical-method flip ───────────────────
        //
        // Two claims, and the whole phase turns on them being probed in
        // BOTH directions rather than only the one that would pass:
        //
        //   * A flip between two RECORDED boots is drift. Probed because
        //     this comparison silently returned "verified" across exactly
        //     this move until now.
        //   * The legacy→recorded transition boot is NOT drift. Probed
        //     because the naive fix — putting the selector in the metric
        //     entry's unused `Impl` slot — passes the first probe and
        //     fails this one, drifting every already-sealed deployment the
        //     moment it upgrades.

        testList "canonical-method visibility (Phase 694)" [
            test "a manifest predating the field is silent about selectors, and says so rather than claiming none" {
                Expect.isFalse
                    (CompositionManifest.recordsCanonicalMethods (asLegacy (grounded (Some "computed:rollup:1"))))
                    "an absent schema field reads as the pre-694 schema, never as version zero"

                Expect.isTrue
                    (CompositionManifest.recordsCanonicalMethods (grounded None))
                    "a manifest this binary projected records selectors even when no metric declares one — that is a claim a legacy manifest cannot make"
            }

            test "a legacy manifest canonicalises to its pre-694 bytes, so an already-minted seal still verifies" {
                let legacy = asLegacy (grounded (Some "computed:rollup:1"))
                let form = BootVerificationPreflight.compositionCanonicalForm legacy

                Expect.isFalse
                    (form.Contains BootVerificationPreflight.CanonicalMethodFramingVersion)
                    "the canonical-method block is not emitted for a manifest too old to carry it — emitting even a zero length would change every existing binding's canonical form and break its genuine seal"

                // And the gate is real rather than vacuous: the same
                // composition recorded at the current schema DOES carry the
                // block.
                let currentForm = BootVerificationPreflight.compositionCanonicalForm (grounded None)

                Expect.isTrue
                    (currentForm.Contains BootVerificationPreflight.CanonicalMethodFramingVersion)
                    "a manifest at the current schema emits the block"

                // End-to-end: a binding sealed over a legacy composition
                // verifies. This is the property the gate exists for, and
                // the only one an operator experiences.
                let sealedRecord, sealedBinding = sealPair baseRecord legacy

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                match verdictOf options legacy with
                | BootVerificationVerdict.VerificationFailed(failures, findings) ->
                    failtestf "a legacy binding's own seal stopped verifying: %A / %A" failures findings
                | _ -> ()
            }

            test "a flip between two recorded boots is drift, naming the metric and both selectors" {
                let recorded = grounded (Some "computed:rollup:1")
                let observed = grounded (Some "computed:rollup:2")

                let drift = BootVerificationPreflight.compare recorded observed

                Expect.equal drift.Length 1 "the selector moved and nothing else did"

                let rendered = CompositionDrift.describe drift.Head

                Expect.stringContains rendered "revenue" "the finding names the metric"
                Expect.stringContains rendered "computed:rollup:1" "and the recorded selector"
                Expect.stringContains rendered "computed:rollup:2" "and the observed one"
            }

            test "a first declaration and a withdrawal are each drift between recorded boots" {
                let declaredDrift =
                    BootVerificationPreflight.compare (grounded None) (grounded (Some "computed:rollup:1"))

                Expect.equal
                    declaredDrift.Length
                    1
                    "declaring a canonical method changes what a method-less query returns"

                Expect.stringContains
                    (CompositionDrift.describe declaredDrift.Head)
                    "computed:rollup:1"
                    "the finding names the newly declared selector"

                let withdrawnDrift =
                    BootVerificationPreflight.compare (grounded (Some "computed:rollup:1")) (grounded None)

                Expect.equal withdrawnDrift.Length 1 "and so does withdrawing one"

                Expect.stringContains
                    (CompositionDrift.describe withdrawnDrift.Head)
                    "computed:rollup:1"
                    "the finding names the selector that is gone"
            }

            test "identical recorded selectors report no drift" {
                Expect.isEmpty
                    (BootVerificationPreflight.compare
                        (grounded (Some "computed:rollup:1"))
                        (grounded (Some "computed:rollup:1")))
                    "a comparison that flagged an unmoved selector would be measuring nothing"
            }

            test "the legacy to recorded transition reports NO drift, and is reported as unrecorded instead" {
                let recorded = asLegacy (grounded (Some "computed:rollup:1"))
                let observed = grounded (Some "computed:rollup:2")

                Expect.isEmpty
                    (BootVerificationPreflight.compare recorded observed)
                    "an upgrade is not a drift: the binding never recorded the selector, so it cannot be evidence that this one is different"

                let unrecorded = BootVerificationPreflight.unrecorded recorded observed

                Expect.equal unrecorded.Length 1 "and the fact that it could not be compared is not dropped"

                let rendered = CompositionUnrecorded.describe unrecorded.Head

                Expect.stringContains rendered "revenue" "naming the metric"
                Expect.stringContains rendered "computed:rollup:2" "and what is live"
            }

            test "unrecorded is empty once the binding records selectors, so a re-seal closes it for good" {
                Expect.isEmpty
                    (BootVerificationPreflight.unrecorded
                        (grounded (Some "computed:rollup:1"))
                        (grounded (Some "computed:rollup:2")))
                    "a binding at the current schema is never silent — its disagreement is drift, which is the other list"
            }

            test "a grounding-free composition under a legacy binding is Verified, not qualified" {
                let legacy = asLegacy composition
                let sealedRecord, sealedBinding = sealPair baseRecord legacy

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
                    "with no metric on either side no selector can exist to be silent about — a provable statement, not a hedge, and a caveat every deployment saw would be one nobody read"
            }

            test "the transition boot is VerifiedUnrecorded — affirmative, and not a match" {
                let legacy = asLegacy (grounded (Some "computed:rollup:1"))
                let observed = grounded (Some "computed:rollup:2")
                let sealedRecord, sealedBinding = sealPair baseRecord legacy

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                let verdict = verdictOf options observed

                match verdict with
                | BootVerificationVerdict.VerifiedUnrecorded items ->
                    Expect.equal items.Length 1 "the one declaration that could not be compared"
                | other -> failtestf "expected VerifiedUnrecorded, got %A" other

                Expect.equal (BootVerificationVerdict.label verdict) "verified-unrecorded" "its own label"

                Expect.isTrue
                    (BootVerificationVerdict.isAffirmative verdict)
                    "affirmative: an old binding is not a drifted deployment, and an upgrade must not cost a refuse-on-drift deployment an outage"

                Expect.isFalse
                    (BootVerificationVerdict.isFullyCompared verdict)
                    "and NOT a full comparison — the whole point is that the preflight does not claim to have checked what it did not check"

                Expect.stringContains
                    (BootVerificationVerdict.findings verdict |> String.concat " | ")
                    "revenue"
                    "the finding names the metric"
            }

            test "under refuse-on-drift the transition boot serves and the flip does not" {
                let observed = grounded (Some "computed:rollup:2")

                let runWith (recordedComposition: CompositionManifest) =
                    let sealedRecord, sealedBinding = sealPair baseRecord recordedComposition

                    let options =
                        optionsFor
                            CompositionProfile.Verified
                            BootVerificationPolicy.RefuseOnDrift
                            (Some sealedRecord)
                            (Some sealedBinding)
                            None

                    BootVerificationPreflight.run options observed |> Async.RunSynchronously

                match runWith (asLegacy (grounded (Some "computed:rollup:1"))) with
                | Ok result -> Expect.isFalse result.RefusedStart "the transition boot serves"
                | Error result -> failtestf "the upgrade refused the start: %A" result.Verdict

                match runWith (grounded (Some "computed:rollup:1")) with
                | Error result ->
                    Expect.isTrue result.RefusedStart "a flip between two recorded boots refuses"

                    Expect.stringContains
                        (BootVerificationVerdict.findings result.Verdict |> String.concat " | ")
                        "computed:rollup:2"
                        "naming what it is running"
                | Ok result -> failtestf "the flip was allowed to serve: %A" result.Verdict
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

        // ─── Phase 678 — a retired deployment refuses to serve ────────
        //
        // The claim is narrow and each half is probed against its own
        // opposite: a retirement that BINDS this record stops the boot
        // under the most permissive policy there is, a retirement that
        // does not bind it stops nothing and is reported as the
        // mis-presentation it is, and a deployment supplying no
        // retirement reaches exactly the verdict it reached before this
        // phase existed.

        testList "retirement" [

            test "a bound retirement retires the record and refuses the start under log-and-serve" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                let digest = baseRecord |> DeployRecords.canonicalBytes |> DeployRecords.digestBytes

                let retirement =
                    DeployRetirement.create
                        digest
                        "terminal-op-digest"
                        "head-digest"
                        7L
                        "ops@example.com"
                        "2026-08-31T09:00:00.0000000Z"
                        "engagement closed"

                // The unretired control: this exact pair verifies, so the
                // refusal below is caused by the retirement and by
                // nothing else.
                Expect.equal
                    (BootVerificationPreflight.verifyWithRetirement options None composition
                     |> Async.RunSynchronously)
                    BootVerificationVerdict.Verified
                    "without a retirement this deployment verifies — the control the refusal is measured against"

                let verdict =
                    BootVerificationPreflight.verifyWithRetirement options (Some retirement) composition
                    |> Async.RunSynchronously

                match verdict with
                | BootVerificationVerdict.Retired found ->
                    Expect.equal found retirement "the verdict carries the retirement it acted on"
                | other -> failtestf "expected a retired verdict, got %A" other

                Expect.equal (BootVerificationVerdict.label verdict) "retired" "its own label, not 'unverified'"

                Expect.isFalse
                    (BootVerificationVerdict.isAffirmative verdict)
                    "a decommissioned deployment is not an affirmative verdict"

                Expect.stringContains
                    (BootVerificationVerdict.findings verdict |> String.concat " | ")
                    "ops@example.com"
                    "the finding names who decommissioned it"

                match
                    BootVerificationPreflight.runWithRetirement options (Some retirement) composition
                    |> Async.RunSynchronously
                with
                | Error result ->
                    Expect.isTrue result.RefusedStart "log-and-serve is no grace period for a signed decommission"
                | Ok _ ->
                    failtest
                        "a retired deployment must refuse to start under EVERY policy — serving one makes the certificate a lie"
            }

            test "a retirement for a different record retires nothing and is reported as mis-presented" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.RefuseOnDrift
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                let retirement =
                    DeployRetirement.create
                        "0000000000000000000000000000000000000000000000000000000000000000"
                        "terminal-op-digest"
                        "head-digest"
                        7L
                        "ops@example.com"
                        "2026-08-31T09:00:00.0000000Z"
                        "a different engagement"

                match
                    BootVerificationPreflight.verifyWithRetirement options (Some retirement) composition
                    |> Async.RunSynchronously
                with
                | BootVerificationVerdict.VerificationFailed(_, findings) ->
                    Expect.isTrue
                        (findings
                         |> List.exists (fun finding -> finding.Contains "retirement was supplied for a different"))
                        "the finding says the retirement belongs to another deploy, which is what an operator acts on"
                | BootVerificationVerdict.Retired _ ->
                    failtest
                        "a retirement naming another record must never retire this one — that is a swap the digest exists to stop"
                | other -> failtestf "expected the mis-presentation to be a binding finding, got %A" other
            }

            test "supplying no retirement leaves every verdict exactly as it was" {
                let sealedRecord, sealedBinding = sealPair baseRecord composition

                let options =
                    optionsFor
                        CompositionProfile.Standard
                        BootVerificationPolicy.LogAndServe
                        (Some sealedRecord)
                        (Some sealedBinding)
                        None

                let drifted = {
                    composition with
                        Modules =
                            composition.Modules
                            @ [ CompositionManifest.moduleEntry ("Extra", ComponentId.ofModule "extra") ]
                }

                // Both arms, because a delegation that only preserved the
                // happy path would be a regression nobody's green test
                // could see.
                Expect.equal
                    (verdictOf options composition)
                    (BootVerificationPreflight.verifyWithRetirement options None composition
                     |> Async.RunSynchronously)
                    "verify is verifyWithRetirement with None — the verified arm"

                Expect.equal
                    (verdictOf options drifted)
                    (BootVerificationPreflight.verifyWithRetirement options None drifted
                     |> Async.RunSynchronously)
                    "verify is verifyWithRetirement with None — the drifted arm"
            }
        ]
    ]