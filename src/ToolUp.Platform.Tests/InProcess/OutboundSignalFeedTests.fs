module ToolUp.Platform.Tests.InProcess.OutboundSignalFeedTests

open System
open System.Security.Cryptography
open Expecto
open ToolUp.Platform
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.Secrets
open ToolUp.InterPlatform
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 491 — the governed outbound signal feed ───────────────────
//
// Phase 490 proved an authorisation binds to one act. This pack is about
// what CONTINUITY changes, and every case here exists because a
// continuous feed can fail in a way a one-shot activation cannot:
//
//   1. **Revocation stops a RUNNING feed.** Measured, not inferred: the
//      feed emits, the counterparty revokes, and the very next emission
//      is refused — with the control that an unrevoked feed keeps
//      emitting, so the case cannot pass against a feed that had simply
//      broken.
//   2. **The bound is enforced**, on all three of its axes — the ε
//      ceiling (arithmetic: `⌊ceiling / εPerEmission⌋`), an explicit
//      `MaxEmissions`, and a `NotAfter` window.
//   3. **Budget exhaustion PAUSES rather than degrading.** The feed
//      stops; it does not emit un-noised, does not emit at a smaller ε,
//      and does not silently deliver a raw count.
//   4. **A restart does not replay.** The cursor is in the store, so a
//      wholly fresh set of dependencies over the same store resumes at
//      the next sequence and re-delivers nothing — except the ONE
//      emission whose delivery had not been confirmed, which is re-sent
//      byte-identically, taking no new sample and spending no new ε.
//   5. **Noise is never optional.** Every delivered count is measurably
//      the noised one, and a policy that draws nothing is refused at
//      validation rather than discovered on the first tick.

// ─── Fixtures ────────────────────────────────────────────────────────

[<Literal>]
let private seller = "seller-ssp"

[<Literal>]
let private buyer = "buyer-acme"

/// The deterministic offset every emission's count carries. Any
/// delivered count that is NOT `true + noiseUnits` did not go through
/// the mechanism.
[<Literal>]
let private noiseUnits = 3L

/// The true cohort size the stub signal reports each tick.
[<Literal>]
let private trueCount = 40

let private floor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count; Histogram ]
}

let private destinationD: ActivationDestination = {
    DestinationId = "propensity-endpoint"
    CounterpartyPeerId = buyer
    PermittedShapes = Set.ofList [ ReleaseCount; ReleaseHistogram ]
    Floor = floor
}

let private purposeA: ActivationPurpose = {
    PurposeId = "campaign-optimisation"
    Description = "Shape delivery against the partner's audience from a continuous propensity signal."
}

let private cohortC: CohortSpec = {
    CohortId = "high-value"
    Definition = CohortMembers [ for i in 0..39 -> sprintf "member-%02i" i ]
    Constraints = {
        MinCohortSize = 10
        Predicates = [ { Name = "recency"; Value = "P30D" } ]
    }
}

let private authorisationA: ActivationAuthorisation = {
    Cohort = cohortC
    Purpose = purposeA
    Destination = destinationD
}

/// ε = 1.0 per emission, so a ceiling of 3.0 admits exactly three.
let private noisePolicy = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 1.0m)

// ─── Stubs ───────────────────────────────────────────────────────────

/// A deterministic "noise" mechanism that always shifts by `+noiseUnits`.
/// Not a distributional claim — Phase 481's pack owns those. What this
/// pack needs is a released count that demonstrably MOVED, so "never
/// emits un-noised" is a measurement rather than a reading of the source.
type private FixedNoise() =
    interface INoiseMechanism with
        member _.SampleUnits(_) = noiseUnits
        member _.Sample(spec) = float noiseUnits * spec.Granularity

/// The caller's aggregate, stubbed, with a call counter — because "a
/// retry takes no new sample" is only checkable by observing that the
/// source was not asked.
type private CountingSignal(shape: OutputShape, cells: PrivacyCell list) =
    let mutable calls = 0
    member _.Calls = calls
    member val Fail: string option = None with get, set

    new() =
        CountingSignal(
            Count,
            [
                {
                    Label = "propensity"
                    Count = trueCount
                    Value = None
                }
            ]
        )

    interface ISignalSource with
        member this.Sample(_, _) = async {
            calls <- calls + 1

            match this.Fail with
            | Some reason -> return Error reason
            | None -> return Ok { Shape = shape; Cells = cells }
        }

/// Records every emission that actually crossed the boundary, and can
/// refuse — which is how the pending-redelivery path is reached.
type private RecordingSink(descriptor: ActivationDestination) =
    let delivered = ResizeArray<FeedEmission>()
    let attempted = ResizeArray<FeedEmission>()

    member _.Delivered = List.ofSeq delivered
    member _.Attempted = List.ofSeq attempted
    member val Refuse: string option = None with get, set

    interface ISignalFeedSink with
        member _.Descriptor = descriptor

        member this.Deliver(emission) = async {
            attempted.Add emission

            match this.Refuse with
            | Some reason -> return Error reason
            | None ->
                delivered.Add emission
                return Ok()
        }

type private DecisionSink() =
    let rows = ResizeArray<PeerCleanRoomDecisionPayload>()
    member _.Rows = List.ofSeq rows

    member _.Sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { rows.Add payload }

// ─── Real key material ───────────────────────────────────────────────
//
// Genuine P-256 keys through the shipped signer, on Phase 480's
// argument: the claim under test is that a revocation is a signed record
// the gate re-reads on every emission, and a stub signer returning "ok"
// would let the revocation cases pass against a feed that checked
// nothing.

type private InMemorySecretStore() =
    let store =
        System.Collections.Concurrent.ConcurrentDictionary<string * string, string>()

    interface ISecretStore with
        member _.GetSecret(scopeId, key) = async {
            match store.TryGetValue((scopeId, key)) with
            | true, v -> return Some v
            | false, _ -> return None
        }

        member _.SetSecret(scopeId, key, value) = async {
            store[(scopeId, key)] <- value
            return Ok()
        }

        member _.DeleteSecret(scopeId, key) = async {
            store.TryRemove((scopeId, key)) |> ignore
            return Ok()
        }

        member _.ListKeys(scopeId) = async {
            return
                store.Keys
                |> Seq.filter (fun (s, _) -> s = scopeId)
                |> Seq.map snd
                |> List.ofSeq
        }

let private secretsFor (peerIds: string list) : ISecretStore =
    let secrets = InMemorySecretStore() :> ISecretStore

    for peerId in peerIds do
        use ec = ECDsa.Create(ECCurve.NamedCurves.nistP256)

        secrets.SetSecret("_platform", $"peers/{peerId}/signing-private-key", ec.ExportPkcs8PrivateKeyPem())
        |> Async.RunSynchronously
        |> ignore

        secrets.SetSecret("_platform", $"peers/{peerId}/signing-public-key", ec.ExportSubjectPublicKeyInfoPem())
        |> Async.RunSynchronously
        |> ignore

    secrets

let private freshRegistry (secrets: ISecretStore) : ITemplateApprovalRegistry =
    let signer = PeerKeyTemplateApprovalSigner(secrets) :> ITemplateApprovalSigner

    BlobTemplateApprovalRegistry(InMemoryBlobStorage() :> IBlobStorage, signer) :> ITemplateApprovalRegistry

let private issue (registry: ITemplateApprovalRegistry) acting counterparty action template =
    let request: TemplateApprovalRequest = {
        Template = template
        ActingPeerId = acting
        CounterpartyPeerId = counterparty
        Action = action
        NotBefore = None
        ExpiresAt = None
    }

    match registry.Issue request |> Async.RunSynchronously with
    | Ok record -> record
    | Error e -> failtestf "Issuing an approval record must succeed in a fixture, got %A" e

let private approve (registry: ITemplateApprovalRegistry) (auth: ActivationAuthorisation) =
    let template = ActivationCanonical.template auth
    issue registry seller buyer TemplateApproved template |> ignore
    let counterpartyRecord = issue registry buyer seller TemplateApproved template

    match registry.Accept counterpartyRecord |> Async.RunSynchronously with
    | Ok() -> ()
    | Error e -> failtestf "Accepting a well-signed counterparty record must succeed, got %A" e

let private revoke (registry: ITemplateApprovalRegistry) (auth: ActivationAuthorisation) (by: string) =
    let template = ActivationCanonical.template auth
    let other = if by = seller then buyer else seller
    let record = issue registry by other TemplateRevoked template

    if by <> seller then
        match registry.Accept record |> Async.RunSynchronously with
        | Ok() -> ()
        | Error e -> failtestf "Accepting a counterparty revocation must succeed, got %A" e

// ─── The composed feed under test ────────────────────────────────────

/// An advanceable clock: the cadence, the `NotAfter` window and the
/// ledger's reservation TTL all read it, so a test crosses a boundary
/// without waiting for one.
type private TestClock(start: DateTimeOffset) =
    let mutable now = start
    member _.Now = now
    member _.Read: unit -> DateTimeOffset = fun () -> now
    member _.Advance(span: TimeSpan) = now <- now.Add span

let private origin = DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)

/// One composed harness. The state store and the ledger are passed in so
/// a "restart" can rebuild everything else around the same durable state.
let private depsWith
    (registry: ITemplateApprovalRegistry)
    (ledger: IPrivacyBudgetLedger)
    (state: IFeedStateStore)
    (signal: ISignalSource)
    (sink: ISignalFeedSink)
    (clock: TestClock)
    (ceiling: decimal)
    : SignalFeedDeps =
    let meter =
        PrivacyBudgetMeter.create ledger (PrivacyBudgetPolicy.create ceiling 1.0m)
        |> PrivacyBudgetMeter.withClock clock.Read

    SignalFeedDeps.create
        (CleanRoomBroker.create ())
        (TemplateApprovalGate.check (TemplateApprovalPolicy.forRegistry registry) seller)
        meter
        (FixedNoise())
        signal
        sink
        state
    |> SignalFeedDeps.withClock clock.Read

let private specWith (ceiling: decimal) : SignalFeedSpec =
    SignalFeedSpec.create "propensity-feed" authorisationA ReleaseCount (TimeSpan.FromHours 1.0) noisePolicy ceiling

/// Everything a case needs, wired: an approved feed, started, over a
/// shared in-memory store and ledger.
type private Harness(ceiling: decimal, spec: SignalFeedSpec) =
    let secrets = secretsFor [ seller; buyer ]
    let registry = freshRegistry secrets
    let clock = TestClock(origin)
    let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
    let state = InMemoryFeedStateStore() :> IFeedStateStore
    let signal = CountingSignal()
    let sink = RecordingSink(destinationD)
    let audit = DecisionSink()

    let deps =
        depsWith registry ledger state signal sink clock ceiling
        |> SignalFeedDeps.withAudit audit.Sink

    member _.Registry = registry
    member _.Ledger = ledger
    member _.State = state
    member _.Clock = clock
    member _.Signal = signal
    member _.Sink = sink
    member _.Audit = audit
    member _.Deps = deps
    member _.Spec = spec

    member _.Approve() = approve registry authorisationA
    member _.Revoke(by) = revoke registry authorisationA by

    member _.Start() =
        match SignalFeed.start deps spec |> Async.RunSynchronously with
        | Ok state -> state
        | Error e -> failtestf "Starting an approved feed must succeed in a fixture, got %s" e

    member _.Emit() =
        SignalFeed.emit deps spec |> Async.RunSynchronously

    /// Advance past the cadence and tick once.
    member this.Tick() =
        clock.Advance(TimeSpan.FromHours 2.0)
        this.Emit()

    member _.Inspect() =
        match SignalFeed.inspect deps spec |> Async.RunSynchronously with
        | Ok reading -> reading
        | Error e -> failtestf "Inspecting a started feed must succeed, got %s" e

let private harness ceiling = Harness(ceiling, specWith ceiling)

let private expectEmitted label tick =
    match tick with
    | FeedEmitted emission -> emission
    | other -> failtestf "%s must emit, got %A" label other

// ─── 1. Revocation stops a RUNNING feed ──────────────────────────────

let revocationTests =
    testList "Phase 491 — a revocation stops a feed that is already running" [

        for revoker in [ seller; buyer ] do
            test $"a running feed revoked by {revoker} refuses its next emission" {
                let h = harness 100m
                h.Approve()
                h.Start() |> ignore

                // It is running: two emissions cross the boundary.
                expectEmitted "the first tick of an approved feed" (h.Tick()) |> ignore
                expectEmitted "the second tick of an approved feed" (h.Tick()) |> ignore
                Expect.hasLength h.Sink.Delivered 2 "the feed was demonstrably running"

                h.Revoke revoker

                // The measurement: the very next tick, with nothing else
                // changed.
                match h.Tick() with
                | FeedHalted(FeedUnauthorised reason) ->
                    Expect.stringContains reason "revoked" "the halt names the withdrawal"
                    Expect.stringContains reason revoker "…and who withdrew it"
                | other -> failtestf "A revoked feed must halt on its next emission, got %A" other

                Expect.hasLength
                    h.Sink.Delivered
                    2
                    "nothing crossed after the revocation — the approval check is re-asked per emission, not cached at start"

                // …and it stays halted, from the persisted cursor,
                // without re-reading the signal.
                let sampledBefore = h.Signal.Calls

                match h.Tick() with
                | FeedHalted(FeedUnauthorised _) -> ()
                | other -> failtestf "A halted feed must stay halted, got %A" other

                Expect.equal h.Signal.Calls sampledBefore "…and a halted feed does not even sample its source"
            }

        test "the control: an unrevoked feed keeps emitting for as many ticks" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore

            for _ in 1..4 do
                h.Tick() |> expectEmitted "an approved, unrevoked feed" |> ignore

            Expect.hasLength
                h.Sink.Delivered
                4
                "without this half, 'the revoked feed stopped' would pass equally against a feed that had broken and stopped emitting at all"
        }

        test "resuming a still-revoked feed pauses it again on the next tick" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "the first tick" |> ignore
            h.Revoke buyer
            h.Tick() |> ignore

            match SignalFeed.resume h.Deps h.Spec |> Async.RunSynchronously with
            | Ok _ -> ()
            | Error e -> failtestf "Resuming a paused feed must succeed, got %s" e

            match h.Tick() with
            | FeedHalted(FeedUnauthorised _) -> ()
            | other ->
                failtestf
                    "Resume re-checks nothing itself — the next tick re-runs invariant 0, so a still-revoked feed halts again. Got %A"
                    other

            Expect.hasLength h.Sink.Delivered 1 "and nothing crossed in the meantime"
        }

        // _(A "re-approved feed resumes emitting" case is deliberately
        //  NOT here. `TemplateApproval.latestFor` resolves an
        //  `IssuedAt` tie towards `TemplateRevoked`, and the registry
        //  stamps `IssuedAt` from the wall clock truncated to whole
        //  seconds — so a revocation and a re-approval issued in the
        //  same test run land in the same second and the revocation
        //  wins, fail-closed. Asserting either outcome would be
        //  asserting on which side of a second boundary the run fell.
        //  That a halt is RECOVERABLE is measured instead by
        //  "raising the ceiling and resuming restarts the feed" in the
        //  budget list, and by the operator pause / resume pair.)_

        test "a sink whose descriptor has drifted from the authorised one halts the feed" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
            let state = InMemoryFeedStateStore() :> IFeedStateStore
            let signal = CountingSignal()

            // A WIDER shape set than the one signed.
            let drifted =
                RecordingSink {
                    destinationD with
                        PermittedShapes = Set.ofList [ ReleaseCount; ReleaseHistogram; ReleaseTokens ]
                }

            let deps = depsWith registry ledger state signal drifted clock 100m
            let spec = specWith 100m

            SignalFeed.start deps spec |> Async.RunSynchronously |> ignore
            clock.Advance(TimeSpan.FromHours 2.0)

            match SignalFeed.emit deps spec |> Async.RunSynchronously with
            | FeedHalted(FeedUnauthorised reason) ->
                Expect.stringContains reason "differs from the authorised one" "the drift is named"
            | other -> failtestf "A drifted sink descriptor must halt the feed, got %A" other

            Expect.equal signal.Calls 0 "…before the signal is even sampled"
        }
    ]

// ─── 2. The bound is enforced ────────────────────────────────────────

let boundTests =
    testList "Phase 491 — a feed carries a bound it cannot outrun" [

        test "the epsilon ceiling IS the emission bound, arithmetically" {
            Expect.equal
                (SignalFeedSpec.emissionBound (specWith 3m))
                3
                "one emission costs the noise policy's total epsilon, so a ceiling of 3.0 at 1.0 each admits exactly three"

            Expect.equal
                (SignalFeedSpec.emissionBound (specWith 0.5m))
                0
                "…and a ceiling below one emission admits none"

            Expect.equal
                (SignalFeedSpec.epsilonPerEmission (specWith 3m))
                1.0m
                "the per-emission epsilon is the policy's own total, which is exactly what the gate reserves"
        }

        test "a feed stops at its declared MaxEmissions" {
            let h = Harness(100m, specWith 100m |> SignalFeedSpec.withMaxEmissions 2)
            h.Approve()
            h.Start() |> ignore

            h.Tick() |> expectEmitted "emission 1" |> ignore
            h.Tick() |> expectEmitted "emission 2" |> ignore

            match h.Tick() with
            | FeedHalted(FeedVolumeReached 2) -> ()
            | other -> failtestf "The third tick must halt on the declared volume bound, got %A" other

            Expect.hasLength h.Sink.Delivered 2 "…and the third emission did not cross"

            Expect.equal
                h.Signal.Calls
                2
                "…and was never even sampled: the bound is checked before any epsilon is spent"

            // Terminal, and it says so.
            match SignalFeed.resume h.Deps h.Spec |> Async.RunSynchronously with
            | Error reason -> Expect.stringContains reason "stopped" "a spent volume bound is a spent agreement"
            | Ok _ -> failtest "A feed stopped on its declared bound must not be resumable"
        }

        test "a feed stops after its NotAfter window closes" {
            let spec = specWith 100m |> SignalFeedSpec.withNotAfter (origin.AddHours 5.0)

            let h = Harness(100m, spec)
            h.Approve()
            h.Start() |> ignore

            h.Tick() |> expectEmitted "an emission inside the window" |> ignore

            // Well past the declared end.
            h.Clock.Advance(TimeSpan.FromHours 10.0)

            match h.Emit() with
            | FeedHalted(FeedWindowClosed at) ->
                Expect.equal at (origin.AddHours 5.0) "the halt names the declared instant"
            | other -> failtestf "A feed past its NotAfter must halt, got %A" other

            Expect.hasLength h.Sink.Delivered 1 "nothing crossed after the window closed"
        }

        test "a tick inside the cadence is not due, and emits nothing" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "the first tick" |> ignore

            h.Clock.Advance(TimeSpan.FromMinutes 5.0)

            match h.Emit() with
            | FeedNotDue at -> Expect.isTrue (at > h.Clock.Now) "the caller is told when the feed is next due"
            | other -> failtestf "A tick inside the cadence must not emit, got %A" other

            Expect.hasLength h.Sink.Delivered 1 "rate shaping is enforced, not advisory"
            Expect.equal h.Signal.Calls 1 "…and an undue tick does not sample the source"
        }
    ]

// ─── 3. Budget exhaustion pauses; it does not degrade ────────────────

let budgetTests =
    testList "Phase 491 — an exhausted budget pauses the feed rather than weakening it" [

        test "a ceiling of three emissions admits exactly three, then pauses" {
            let h = harness 3m
            h.Approve()
            h.Start() |> ignore

            for i in 1..3 do
                let emission = h.Tick() |> expectEmitted $"emission {i} inside the ceiling"
                Expect.equal emission.Epsilon 1.0m "each emission is charged the mechanism's own epsilon"

            match h.Tick() with
            | FeedHalted(FeedBudgetExhausted reason) ->
                Expect.stringContains reason "epsilon remaining" "the operator is told what bound"
                Expect.stringContains reason "paused rather than degraded" "…and that the feed did not weaken instead"
            | other -> failtestf "The fourth emission must exhaust the ceiling and pause, got %A" other

            Expect.hasLength
                h.Sink.Delivered
                3
                "the ceiling is a hard bound: three emissions crossed and the fourth did not"
        }

        test "an exhausted feed emits nothing at all — not un-noised, not smaller, not raw" {
            let h = harness 2m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "emission 1" |> ignore
            h.Tick() |> expectEmitted "emission 2" |> ignore
            h.Tick() |> ignore

            Expect.hasLength h.Sink.Delivered 2 "no third delivery of any kind"

            // Every delivery that DID happen carries the noised count.
            for emission in h.Sink.Delivered do
                match emission.Release with
                | ActivatedCount released ->
                    Expect.equal
                        released
                        (trueCount + int noiseUnits)
                        "the released count is the calibrated one; a feed that fell back to the raw answer when the budget tightened would be worse than no feed at all"
                | other -> failtestf "A ReleaseCount feed must deliver a count, got %A" other
        }

        test "the control: the same feed under a wider ceiling keeps emitting" {
            let h = harness 10m
            h.Approve()
            h.Start() |> ignore

            for i in 1..6 do
                h.Tick() |> expectEmitted $"emission {i} under a wider ceiling" |> ignore

            Expect.hasLength
                h.Sink.Delivered
                6
                "without this half, 'the exhausted feed stopped' would pass against a feed that stopped for any reason at all"
        }

        test "raising the ceiling and resuming restarts the feed" {
            // A tight feed, exhausted.
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
            let state = InMemoryFeedStateStore() :> IFeedStateStore
            let signal = CountingSignal()
            let sink = RecordingSink(destinationD)

            let tightDeps = depsWith registry ledger state signal sink clock 2m
            let tightSpec = specWith 2m

            SignalFeed.start tightDeps tightSpec |> Async.RunSynchronously |> ignore

            let tick (deps: SignalFeedDeps) spec =
                clock.Advance(TimeSpan.FromHours 2.0)
                SignalFeed.emit deps spec |> Async.RunSynchronously

            tick tightDeps tightSpec |> expectEmitted "emission 1" |> ignore
            tick tightDeps tightSpec |> expectEmitted "emission 2" |> ignore

            match tick tightDeps tightSpec with
            | FeedHalted(FeedBudgetExhausted _) -> ()
            | other -> failtestf "The tight feed must exhaust, got %A" other

            // The composition raises the ceiling — same ledger, same
            // cursor, same authorisation.
            let wideDeps = depsWith registry ledger state signal sink clock 6m
            let wideSpec = specWith 6m

            match SignalFeed.resume wideDeps wideSpec |> Async.RunSynchronously with
            | Ok _ -> ()
            | Error e -> failtestf "Resuming after a ceiling raise must succeed, got %s" e

            tick wideDeps wideSpec
            |> expectEmitted "an emission under the raised ceiling"
            |> ignore

            Expect.hasLength sink.Delivered 3 "the pause was recoverable, and the spend already made still counts"

            let reading =
                match SignalFeed.inspect wideDeps wideSpec |> Async.RunSynchronously with
                | Ok r -> r
                | Error e -> failtestf "Inspect must succeed, got %s" e

            Expect.equal reading.EmissionCount 3 "the cursor carried across the raise"
            Expect.equal reading.EpsilonCommitted 3.0m "…and so did the epsilon already spent — a raise is not a reset"
            Expect.equal reading.EpsilonRemaining 3.0m "…leaving exactly the difference"
        }

        test "the gate's ledger is the accountant — inspect reports what actually binds" {
            let h = harness 5m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "emission 1" |> ignore
            h.Tick() |> expectEmitted "emission 2" |> ignore

            let reading = h.Inspect()

            Expect.equal reading.EpsilonCeiling 5m "the feed's own ceiling, not the composed policy's"
            Expect.equal reading.EpsilonCommitted 2.0m "two emissions at one epsilon each"
            Expect.equal reading.EpsilonRemaining 3.0m "…read from the ledger the gate reserves against"
            Expect.equal reading.LastEpsilon 1.0m "491.C — the last value's epsilon"
            Expect.equal reading.EmissionCount 2 "…and the emissions to date"
            Expect.equal reading.EmissionBound 5 "…against the bound the ceiling implies"
            Expect.isNone reading.Pending "nothing is awaiting delivery"
        }
    ]

// ─── 4. Restart does not replay ──────────────────────────────────────

let restartTests =
    testList "Phase 491 — a restart resumes; it does not replay" [

        test "a wholly fresh composition over the same store continues at the next sequence" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
            // The only thing that survives the "restart".
            let state = InMemoryFeedStateStore() :> IFeedStateStore
            let spec = specWith 100m

            let tick (deps: SignalFeedDeps) =
                clock.Advance(TimeSpan.FromHours 2.0)
                SignalFeed.emit deps spec |> Async.RunSynchronously

            let signalA = CountingSignal()
            let sinkA = RecordingSink(destinationD)
            let depsA = depsWith registry ledger state signalA sinkA clock 100m

            SignalFeed.start depsA spec |> Async.RunSynchronously |> ignore
            tick depsA |> expectEmitted "emission 1" |> ignore
            tick depsA |> expectEmitted "emission 2" |> ignore
            Expect.hasLength sinkA.Delivered 2 "the first process delivered two"

            // The restart: new signal, new sink, new deps record. Only
            // the cursor and the ledger persist, as they would across a
            // process boundary.
            let signalB = CountingSignal()
            let sinkB = RecordingSink(destinationD)
            let depsB = depsWith registry ledger state signalB sinkB clock 100m

            let resumed = tick depsB |> expectEmitted "the first emission after a restart"

            Expect.equal
                resumed.Sequence
                3L
                "the sequence resumes from the persisted cursor — a feed that restarted at 1 would re-emit its whole history"

            Expect.hasLength
                sinkB.Delivered
                1
                "exactly one delivery after the restart: nothing already delivered was re-sent"

            Expect.equal
                (sinkB.Delivered |> List.map _.Sequence)
                [ 3L ]
                "…and it is the NEXT emission, not any earlier one"
        }

        test "starting an already-started feed is idempotent and does not reset the cursor" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "emission 1" |> ignore

            // What a restarting process does: start again.
            let adopted = h.Start()

            Expect.equal adopted.EmissionCount 1 "the existing cursor is adopted, not replaced"
            Expect.equal adopted.LastSequence 1L "…so the next emission is 2, not 1"

            let next = h.Tick() |> expectEmitted "the emission after a re-start"
            Expect.equal next.Sequence 2L "…which it is"
        }

        test "an undelivered emission is re-sent byte-identically, taking no new sample and no new epsilon" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
            let state = InMemoryFeedStateStore() :> IFeedStateStore
            let spec = specWith 100m

            let signalA = CountingSignal()
            let sinkA = RecordingSink(destinationD)
            let depsA = depsWith registry ledger state signalA sinkA clock 100m

            SignalFeed.start depsA spec |> Async.RunSynchronously |> ignore

            sinkA.Refuse <- Some "the partner endpoint was unreachable"
            clock.Advance(TimeSpan.FromHours 2.0)

            match SignalFeed.emit depsA spec |> Async.RunSynchronously with
            | FeedEmissionRefused(ActivationDeliveryFailed reason) ->
                Expect.stringContains reason "unreachable" "the downstream reason is preserved"
            | other -> failtestf "A refused delivery must surface, got %A" other

            Expect.isEmpty sinkA.Delivered "nothing was accepted"

            let pending =
                match SignalFeed.inspect depsA spec |> Async.RunSynchronously with
                | Ok reading ->
                    match reading.Pending with
                    | Some p -> p
                    | None -> failtest "The undelivered emission must be persisted as pending"
                | Error e -> failtestf "Inspect must succeed, got %s" e

            let committedBefore =
                match SignalFeed.inspect depsA spec |> Async.RunSynchronously with
                | Ok r -> r.EpsilonCommitted
                | Error e -> failtestf "Inspect must succeed, got %s" e

            // The restart, with the partner back.
            let signalB = CountingSignal()
            let sinkB = RecordingSink(destinationD)
            let depsB = depsWith registry ledger state signalB sinkB clock 100m
            clock.Advance(TimeSpan.FromHours 2.0)

            match SignalFeed.emit depsB spec |> Async.RunSynchronously with
            | FeedRedelivered emission ->
                Expect.equal
                    emission
                    pending
                    "the re-delivery is the SAME value: a fresh draw would hand the recipient two independent samples of one true number and halve the noise it paid for"
            | other -> failtestf "A pending emission must be re-delivered, got %A" other

            Expect.equal signalB.Calls 0 "the source was not asked again"

            match SignalFeed.inspect depsB spec |> Async.RunSynchronously with
            | Ok reading ->
                Expect.equal reading.EpsilonCommitted committedBefore "…and no further epsilon was spent on the retry"
                Expect.isNone reading.Pending "…and the cursor is clear once the partner accepted"
            | Error e -> failtestf "Inspect must succeed, got %s" e

            // The next tick is a genuinely new emission.
            clock.Advance(TimeSpan.FromHours 2.0)

            let fresh =
                SignalFeed.emit depsB spec
                |> Async.RunSynchronously
                |> expectEmitted "the tick after the retry"

            Expect.equal fresh.Sequence 2L "the feed advances once the pending emission is settled"
            Expect.equal signalB.Calls 1 "…and only then is the source sampled again"
        }

        test "the idempotency key is stable per sequence and distinct across them" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore

            let first = h.Tick() |> expectEmitted "emission 1"
            let second = h.Tick() |> expectEmitted "emission 2"

            Expect.notEqual first.IdempotencyKey second.IdempotencyKey "two emissions are two deliveries"

            Expect.equal
                first.IdempotencyKey
                (SignalFeedCanonical.idempotencyKey first.FeedId first.AuthorisationId first.Sequence)
                "the key is derived from three public values, so the receiving partner recomputes it rather than trusting it"

            Expect.notEqual
                first.IdempotencyKey
                (SignalFeedCanonical.idempotencyKey "other-feed" first.AuthorisationId first.Sequence)
                "…and it is bound to the feed"
        }

        test "an unreadable cursor refuses to emit rather than restarting the feed at sequence one" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger

            let broken =
                { new IFeedStateStore with
                    member _.IsDurable = true
                    member _.Load(_) = async { return Error "the cursor document could not be read" }
                    member _.Save(state) = async { return Ok state }
                }

            let sink = RecordingSink(destinationD)
            let deps = depsWith registry ledger broken (CountingSignal()) sink clock 100m
            clock.Advance(TimeSpan.FromHours 2.0)

            match SignalFeed.emit deps (specWith 100m) |> Async.RunSynchronously with
            | FeedUnavailable reason -> Expect.stringContains reason "could not be read" "fail-closed, and it says why"
            | other -> failtestf "An unreadable cursor must not emit, got %A" other

            Expect.isEmpty sink.Delivered "a feed whose emission history is unknown must not emit"
        }

        test "a feed that was never started does not emit" {
            let h = harness 100m
            h.Approve()

            match h.Tick() with
            | FeedUnavailable reason -> Expect.stringContains reason "has not been started" "start is the audited act"
            | other -> failtestf "An unstarted feed must not emit, got %A" other

            Expect.isEmpty h.Sink.Delivered "nothing crossed"
        }
    ]

// ─── 5. Noise is never optional ──────────────────────────────────────

let noiseTests =
    testList "Phase 491 — every emission is calibrated, and the shape is the approved one" [

        test "the delivered count is the noised one, on every emission" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore

            for _ in 1..3 do
                h.Tick() |> ignore

            Expect.hasLength h.Sink.Delivered 3 "three emissions"

            for emission in h.Sink.Delivered do
                match emission.Release with
                | ActivatedCount released ->
                    Expect.notEqual released trueCount "the true count never leaves"
                    Expect.equal released (trueCount + int noiseUnits) "…the calibrated one does"
                | other -> failtestf "Expected a count release, got %A" other
        }

        test "a histogram feed releases the caller's buckets, suppressed under the floor by the gate" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
            let state = InMemoryFeedStateStore() :> IFeedStateStore

            // "rare" is below the suppression threshold of 5.
            let signal =
                CountingSignal(
                    Histogram,
                    [
                        {
                            Label = "bulk"
                            Count = 30
                            Value = None
                        }
                        {
                            Label = "rare"
                            Count = 2
                            Value = None
                        }
                    ]
                )

            let sink = RecordingSink(destinationD)
            let deps = depsWith registry ledger state signal sink clock 100m

            let spec = {
                specWith 100m with
                    Shape = ReleaseHistogram
            }

            SignalFeed.start deps spec |> Async.RunSynchronously |> ignore
            clock.Advance(TimeSpan.FromHours 2.0)

            match SignalFeed.emit deps spec |> Async.RunSynchronously with
            | FeedEmitted emission ->
                match emission.Release with
                | ActivatedHistogram buckets ->
                    Expect.equal
                        buckets
                        [
                            {
                                Label = "bulk"
                                Count = 30 + int noiseUnits
                            }
                        ]
                        "the sub-threshold bucket was suppressed by the gate before the noise was drawn, and the survivor is calibrated"
                | other -> failtestf "Expected a histogram release, got %A" other
            | other -> failtestf "The histogram feed must emit, got %A" other
        }

        test "a signal answering in the wrong shape is refused, and the feed keeps running" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
            let state = InMemoryFeedStateStore() :> IFeedStateStore

            // A histogram answer for a count feed.
            let signal =
                CountingSignal(
                    Histogram,
                    [
                        {
                            Label = "bulk"
                            Count = 30
                            Value = None
                        }
                    ]
                )

            let sink = RecordingSink(destinationD)
            let deps = depsWith registry ledger state signal sink clock 100m
            let spec = specWith 100m

            SignalFeed.start deps spec |> Async.RunSynchronously |> ignore
            clock.Advance(TimeSpan.FromHours 2.0)

            match SignalFeed.emit deps spec |> Async.RunSynchronously with
            | FeedEmissionRefused(ActivationEgressRefused reason) ->
                Expect.stringContains reason "expects" "the mismatch names both shapes"
            | other -> failtestf "A shape mismatch must refuse, got %A" other

            Expect.isEmpty sink.Delivered "nothing crossed"
        }

        test "a sub-floor signal is withheld without halting the feed" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            approve registry authorisationA

            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger
            let state = InMemoryFeedStateStore() :> IFeedStateStore

            // 3 is under the k-floor of 10.
            let signal =
                CountingSignal(
                    Count,
                    [
                        {
                            Label = "propensity"
                            Count = 3
                            Value = None
                        }
                    ]
                )

            let sink = RecordingSink(destinationD)
            let deps = depsWith registry ledger state signal sink clock 100m
            let spec = specWith 100m

            SignalFeed.start deps spec |> Async.RunSynchronously |> ignore
            clock.Advance(TimeSpan.FromHours 2.0)

            match SignalFeed.emit deps spec |> Async.RunSynchronously with
            | FeedEmissionRefused(ActivationWithheld templateId) ->
                Expect.equal
                    templateId
                    (SignalFeedSpec.authorisationId spec)
                    "the refusal carries the authorisation id and nothing quantitative"
            | other -> failtestf "A sub-floor emission must be withheld, got %A" other

            Expect.isEmpty sink.Delivered "nothing crossed"

            // The feed is NOT halted — a thin hour is not a governance
            // stop, and the next sample may clear.
            match SignalFeed.inspect deps spec |> Async.RunSynchronously with
            | Ok reading -> Expect.equal reading.Status FeedRunning "the feed is still running"
            | Error e -> failtestf "Inspect must succeed, got %s" e
        }

        test "no member id ever appears in what the sink received" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> ignore

            let ids =
                match cohortC.Definition with
                | CohortMembers members -> members
                | CohortQuery _ -> failtest "the fixture cohort is a materialised set"

            for emission in h.Sink.Delivered do
                let wire = JsonRpc.serialize emission

                for memberId in ids do
                    Expect.isFalse
                        (wire.Contains memberId)
                        $"'{memberId}' must not appear in an emission — a feed releases aggregates, never the ids behind them"
        }
    ]

// ─── 6. Validation: the tap is unexpressible ─────────────────────────

let validationTests =
    testList "Phase 491 — a feed that could become an open-ended tap does not validate" [

        test "a token release is refused at validation, not on the first tick" {
            let spec = {
                specWith 100m with
                    Shape = ReleaseTokens
                    Authorisation = {
                        authorisationA with
                            Destination = {
                                destinationD with
                                    PermittedShapes = Set.ofList [ ReleaseCount; ReleaseTokens ]
                            }
                    }
            }

            let errors = SignalFeedSpec.validate spec

            Expect.isTrue
                (errors |> List.exists (fun e -> e.Contains "per-member tokens"))
                "a feed that only discovers its shape is impossible on the first tick is a feed an operator believes is running"
        }

        test "a policy that draws no noise is refused" {
            let spec = {
                specWith 100m with
                    Noise = { CountNoise = None; ValueNoise = None }
            }

            let errors = SignalFeedSpec.validate spec

            Expect.isNonEmpty
                errors
                "a feed with no mechanism would charge epsilon for a deterministic answer and its ceiling would bound nothing"
        }

        test "a ceiling smaller than one emission is refused" {
            let errors = SignalFeedSpec.validate (specWith 0.5m)

            Expect.isTrue
                (errors |> List.exists (fun e -> e.Contains "could never emit"))
                "a feed that cannot afford its first emission is a configuration error, not a paused feed"
        }

        test "a refilling budget epoch needs an explicit bound the refill cannot outwait" {
            let secrets = secretsFor [ seller; buyer ]
            let registry = freshRegistry secrets
            let clock = TestClock(origin)
            let ledger = InMemoryPrivacyBudgetLedger(clock.Read) :> IPrivacyBudgetLedger

            let dailyDeps =
                let meter =
                    PrivacyBudgetMeter.create
                        ledger
                        (PrivacyBudgetPolicy.create 100m 1.0m
                         |> PrivacyBudgetPolicy.withEpoch DailyBudget)
                    |> PrivacyBudgetMeter.withClock clock.Read

                SignalFeedDeps.create
                    (CleanRoomBroker.create ())
                    (TemplateApprovalGate.check (TemplateApprovalPolicy.forRegistry registry) seller)
                    meter
                    (FixedNoise())
                    (CountingSignal())
                    (RecordingSink(destinationD))
                    (InMemoryFeedStateStore())
                |> SignalFeedDeps.withClock clock.Read

            let unbounded = specWith 100m

            Expect.isTrue
                (SignalFeed.validate dailyDeps unbounded
                 |> List.exists (fun e -> e.Contains "REFILLING"))
                "a ceiling that refills is a ceiling a patient counterparty simply outwaits"

            // The controls: the same feed with either bound declared,
            // and the same feed on a perpetual budget.
            Expect.isEmpty
                (SignalFeed.validate dailyDeps (unbounded |> SignalFeedSpec.withMaxEmissions 10))
                "…and an explicit volume bound settles it"

            Expect.isEmpty
                (SignalFeed.validate dailyDeps (unbounded |> SignalFeedSpec.withNotAfter (origin.AddDays 7.0)))
                "…as does an explicit end date"

            let perpetualDeps =
                depsWith
                    registry
                    ledger
                    (InMemoryFeedStateStore())
                    (CountingSignal())
                    (RecordingSink(destinationD))
                    clock
                    100m

            Expect.isEmpty
                (SignalFeed.validate perpetualDeps unbounded)
                "…and a perpetual budget needs neither, because its ceiling IS the total"
        }

        test "start refuses a spec this composition cannot honour" {
            let h = Harness(0.5m, specWith 0.5m)
            h.Approve()

            match SignalFeed.start h.Deps h.Spec |> Async.RunSynchronously with
            | Error reason -> Expect.stringContains reason "could never emit" "the refusal names what is wrong"
            | Ok _ -> failtest "A feed that cannot afford an emission must not start"
        }

        test "start refuses to adopt a cursor whose authorisation has moved" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "emission 1" |> ignore

            // One more member: a different cohort, a different
            // authorisation, the same feed id.
            let edited = {
                h.Spec with
                    Authorisation = {
                        authorisationA with
                            Cohort = {
                                cohortC with
                                    Definition = CohortMembers [ "member-99" ]
                            }
                    }
            }

            match SignalFeed.start h.Deps edited |> Async.RunSynchronously with
            | Error reason ->
                Expect.stringContains
                    reason
                    "new agreement"
                    "letting an edited cohort inherit the emission count and epsilon spend would be the cheapest possible way to reset a bound"
            | Ok _ -> failtest "An edited authorisation must not adopt the signed one's cursor"
        }
    ]

// ─── 7. Operator controls + audit ────────────────────────────────────

let operatorTests =
    testList "Phase 491 — start / pause / resume / stop are audited operator acts" [

        test "a paused feed emits nothing until it is resumed" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "emission 1" |> ignore

            match
                SignalFeed.pause h.Deps h.Spec "the campaign is on hold"
                |> Async.RunSynchronously
            with
            | Ok _ -> ()
            | Error e -> failtestf "Pausing must succeed, got %s" e

            match h.Tick() with
            | FeedHalted(FeedOperatorPaused reason) ->
                Expect.stringContains reason "on hold" "the operator's reason is kept"
            | other -> failtestf "A paused feed must not emit, got %A" other

            Expect.hasLength h.Sink.Delivered 1 "nothing crossed while paused"
            Expect.equal h.Signal.Calls 1 "…and the source was not sampled"

            match SignalFeed.resume h.Deps h.Spec |> Async.RunSynchronously with
            | Ok _ -> ()
            | Error e -> failtestf "Resuming must succeed, got %s" e

            h.Tick() |> expectEmitted "a resumed feed" |> ignore
            Expect.hasLength h.Sink.Delivered 2 "…and it emits again once resumed"
        }

        test "a stopped feed is terminal" {
            let h = harness 100m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> expectEmitted "emission 1" |> ignore

            match SignalFeed.stop h.Deps h.Spec "the agreement ended" |> Async.RunSynchronously with
            | Ok _ -> ()
            | Error e -> failtestf "Stopping must succeed, got %s" e

            match h.Tick() with
            | FeedHalted(FeedOperatorStopped _) -> ()
            | other -> failtestf "A stopped feed must not emit, got %A" other

            match SignalFeed.resume h.Deps h.Spec |> Async.RunSynchronously with
            | Error reason -> Expect.stringContains reason "stopped" "a stopped feed is not resumable"
            | Ok _ -> failtest "A stopped feed must not resume"
        }

        test "every operator act and every halt is recorded" {
            let h = harness 2m
            h.Approve()
            h.Start() |> ignore
            h.Tick() |> ignore
            h.Tick() |> ignore
            h.Tick() |> ignore

            let reasons = h.Audit.Rows |> List.map _.Reason

            Expect.isTrue (reasons |> List.exists (fun r -> r.Contains "started")) "the start is audited"

            Expect.isTrue
                (reasons |> List.exists (fun r -> r.Contains "emitted signal"))
                "…each emission names the cohort, the purpose, the destination and its epsilon"

            Expect.isTrue
                (reasons |> List.exists (fun r -> r.Contains "FeedBudgetExhausted"))
                "…and the pause carries its cause, so an operator does not have to guess whether to chase an approval or raise a ceiling"

            let feedRows =
                h.Audit.Rows |> List.filter (fun r -> r.Reason.Contains "emitted signal")

            Expect.hasLength feedRows 2 "one feed row per emission that crossed"

            for feedRow in feedRows do
                Expect.isTrue feedRow.Released "…each records that something was released"
                Expect.equal feedRow.ContractId destinationD.DestinationId "what it went to"
                Expect.equal feedRow.CallerPeerId buyer "…and to whom"
                Expect.equal feedRow.TemplateId (SignalFeedSpec.authorisationId h.Spec) "…under which authorisation"
                Expect.equal feedRow.MethodName "ReleaseCount" "…in which shape"

                Expect.isTrue
                    (h.Audit.Rows
                     |> List.exists (fun gateRow ->
                         gateRow.RootRequestId = feedRow.RootRequestId
                         && gateRow.Reason.Contains "calibrated noise"))
                    "…and it joins the gate's own decision row for the SAME emission on the idempotency key, which is also what the partner received"
        }

        test "inspect on an unstarted feed refuses rather than inventing a status" {
            let h = harness 100m
            h.Approve()

            match SignalFeed.inspect h.Deps h.Spec |> Async.RunSynchronously with
            | Error reason -> Expect.stringContains reason "has not been started" "there is no cursor to report"
            | Ok _ -> failtest "Inspecting an unstarted feed must refuse"
        }
    ]

// ─── 8. The cursor stores ────────────────────────────────────────────

let stateStoreTests =
    testList "Phase 491 — the emission cursor" [

        test "the in-memory store declares itself non-durable and guards its revision" {
            let store = InMemoryFeedStateStore() :> IFeedStateStore

            Expect.isFalse
                store.IsDurable
                "a process-local cursor cannot honour the no-replay claim across a restart, and says so rather than being inferred"

            let fresh = {
                FeedId = "feed-a"
                AuthorisationId = "_platform.activation.abc"
                AuthorisationVersion = "sha256:abc"
                Status = FeedRunning
                EmissionCount = 0
                EpsilonSpent = 0m
                LastEpsilon = 0m
                LastSequence = 0L
                LastEmittedAt = None
                Pending = None
                Revision = 0L
            }

            match store.Save fresh |> Async.RunSynchronously with
            | Ok saved -> Expect.equal saved.Revision 1L "the store owns the revision"
            | Error e -> failtestf "The first save must succeed, got %A" e

            // The racing tick: a second writer still holding the
            // pre-race revision. Refusing it is what stops two ticks
            // committing an emission over one another.
            match store.Save fresh |> Async.RunSynchronously with
            | Error FeedStateConflict -> ()
            | other -> failtestf "A stale revision must be refused, got %A" other
        }

        test "the blob store is durable and round-trips a cursor" {
            let store = BlobFeedStateStore(InMemoryBlobStorage()) :> IFeedStateStore
            Expect.isTrue store.IsDurable "a compare-and-swap blob cursor is shared and survives a restart"

            let fresh = {
                FeedId = "feed/with spaces"
                AuthorisationId = "_platform.activation.abc"
                AuthorisationVersion = "sha256:abc"
                Status = FeedRunning
                EmissionCount = 0
                EpsilonSpent = 0m
                LastEpsilon = 0m
                LastSequence = 0L
                LastEmittedAt = None
                Pending = None
                Revision = 0L
            }

            let saved =
                match store.Save fresh |> Async.RunSynchronously with
                | Ok state -> state
                | Error e -> failtestf "The first save must succeed, got %A" e

            Expect.equal saved.Revision 1L "the store owns the revision"

            match store.Load fresh.FeedId |> Async.RunSynchronously with
            | Ok(Some loaded) -> Expect.equal loaded saved "…and the cursor round-trips"
            | other -> failtestf "The cursor must load back, got %A" other

            // A stale write loses — which is what stops two ticks
            // committing an emission over one another.
            match store.Save fresh |> Async.RunSynchronously with
            | Error FeedStateConflict -> ()
            | other -> failtestf "A stale revision must be refused, got %A" other
        }

        test "a blob backend without conditional writes is refused at construction" {
            let plain =
                { new IBlobStorage with
                    member _.Upload(_, _, _) = async { return Ok "" }
                    member _.Download(_, _) = async { return Error "no" }
                    member _.Delete(_, _) = async { return Ok() }
                    member _.Exists(_, _) = async { return false }
                    member _.List(_, _) = async { return [] }
                    member _.GetMetadata(_, _) = async { return Error "no" }
                    member _.DownloadRange(_, _, _, _) = async { return Error "no" }

                    member _.Erase(_, _, _, _) = async {
                        return Error(HandlerRefused("stub", "this stub exists only to lack conditional writes"))
                    }
                }

            Expect.throws
                (fun () -> BlobFeedStateStore plain |> ignore)
                "the compare-and-swap IS the defence against two ticks double-delivering; a download-modify-upload fallback would race exactly there"

            Expect.isNone (BlobFeedStateStore.TryCreate plain) "…and the probing form says so without throwing"
        }
    ]