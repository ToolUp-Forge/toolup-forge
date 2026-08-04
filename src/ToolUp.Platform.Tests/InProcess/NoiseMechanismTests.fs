module ToolUp.Platform.Tests.InProcess.NoiseMechanismTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.InterPlatform
open ToolUp.InterPlatform.PeerCompose

// ─── Phase 481 — the calibrated-noise mechanism ──────────────────────
//
// Phase 190 shipped an ε ledger and said, in its own header, that it was
// an ACCOUNTING control: nothing randomised the answers, so summing ε
// over them bounded nothing formally. This pack is about the claim that
// closes that gap, and the hard part is that **a statistical test passes
// on a broken sampler by default**. A mechanism that always returns 0
// has a perfect mean; one seeded from a predictable PRNG has a textbook
// distribution and no privacy at all; one that ignores its ε has a
// beautiful histogram at the wrong width. So every distributional case
// here is paired with something that must FAIL it:
//
//   1. **A zero-noise negative control.** Every spread / shape
//      assertion is re-run against `ZeroNoiseMechanism`, which is the
//      pre-481 deterministic release. If the zero mechanism passed, the
//      assertion measured nothing.
//   2. **Calibration, not merely presence.** Doubling the sensitivity
//      and halving ε each widen the empirical distribution by a
//      predicted factor, asserted against the closed form for the
//      DISCRETE Laplace (`Var = 2q/(1−q)²`, `P[0] = (1−q)/(1+q)` with
//      `q = e^{−1/b}`), not against a hand-typed constant. Noise that is
//      merely non-zero passes nothing here.
//   3. **The shipped path is the CSPRNG.** Reproducibility is what a
//      distribution test needs and is exactly what production must not
//      have, so the two are asserted separately: `seeded` repeats,
//      `create ()` does not, and `create ()`'s bit source is
//      `CryptoNoiseEntropy` by type.
//   4. **The snap is real.** The Mironov (CCS 2012) defence has two
//      halves and only one of them is the sampler; a case drives
//      `release` with the zero mechanism and shows the released value is
//      the lattice point, not the input.
//
// **On flakiness.** Every statistical case drives `NoiseMechanism.seeded`
// — the same sampler, a fixed bit stream — so these assertions are
// DETERMINISTIC: a failure is a change in the sampler, never bad luck.
// The bands are still set at roughly ±8% of the closed form, which at
// n = 20 000 is on the order of ten standard errors, so re-seeding or
// re-tuning the harness cannot turn them into flakes either. The
// targeted false-failure rate is therefore zero as written and below
// ~1e-9 under any reseed.

// ─── Fixtures ────────────────────────────────────────────────────────

let private run x = Async.RunSynchronously x

/// The negative control: the pre-481 deterministic release, wearing the
/// mechanism's interface. Every distributional assertion below is run
/// against this too, and must fail.
type private ZeroNoiseMechanism() =
    interface INoiseMechanism with
        member _.SampleUnits _ = 0L
        member _.Sample _ = 0.0

let private zero = ZeroNoiseMechanism() :> INoiseMechanism

/// Draws enough to make a spread estimate tight. Deterministic: the
/// mechanism is seeded.
[<Literal>]
let private SampleCount = 20000

let private draws (mechanism: INoiseMechanism) (spec: NoiseSpec) (n: int) = [
    for _ in 1..n -> mechanism.SampleUnits spec
]

let private meanOf (xs: int64 list) =
    (xs |> List.sumBy float) / float (List.length xs)

let private stdDevOf (xs: int64 list) =
    let m = meanOf xs
    let n = float (List.length xs)
    sqrt ((xs |> List.sumBy (fun x -> (float x - m) ** 2.0)) / n)

/// The closed-form standard deviation of a DISCRETE Laplace of scale
/// `b`: `Var = 2q/(1−q)²` with `q = e^{−1/b}`. Written out rather than
/// approximated by the continuous `b√2`, which is 4% adrift at the
/// scales this pack uses — near enough to hide a real calibration error.
let private discreteLaplaceStdDev (b: float) =
    let q = exp (-1.0 / b)
    sqrt (2.0 * q / ((1.0 - q) ** 2.0))

/// The point mass at zero for the same distribution.
let private discreteLaplaceAtZero (b: float) =
    let q = exp (-1.0 / b)
    (1.0 - q) / (1.0 + q)

/// The scale a spec of `sensitivity` at `epsilon` actually draws from,
/// in lattice units — `⌈Δ/g⌉ + 1` over ε, mirroring
/// `NoiseSpec.latticeSensitivity`.
let private laplaceScale (spec: NoiseSpec) =
    float (NoiseSpec.latticeSensitivity spec) / float spec.Epsilon

/// ±8% of `expected`. Roughly ten standard errors at `SampleCount`; see
/// the header on why that is not slack.
let private expectNear (label: string) (expected: float) (actual: float) =
    let tolerance = abs expected * 0.08

    Expect.isTrue (abs (actual - expected) <= tolerance) $"{label}: expected {expected} +/- {tolerance}, got {actual}"

// ─── 1. The sampler ──────────────────────────────────────────────────

let samplerTests =
    testList "Phase 481 — the sampler is calibrated, reproducible only under a seed, and CSPRNG in production" [

        test "the production mechanism draws from the platform CSPRNG" {
            // The single most important assertion in this file and the
            // one no distribution test can make: a mechanism seeded from
            // a predictable PRNG has a textbook Laplace histogram and
            // provides no privacy whatsoever, because an adversary who
            // reproduces the stream subtracts it.
            match NoiseMechanism.create () with
            | :? DiscreteNoiseMechanism as mechanism ->
                Expect.isTrue
                    (mechanism.Entropy :? CryptoNoiseEntropy)
                    "NoiseMechanism.create () must draw from CryptoNoiseEntropy (RandomNumberGenerator), never a seeded stream"
            | other -> failtestf "expected a DiscreteNoiseMechanism, got %s" (other.GetType().FullName)
        }

        test "two production mechanisms do not repeat each other" {
            // The behavioural half of the case above: a `create ()` that
            // had quietly become seeded would still pass the type check
            // if someone changed what `CryptoNoiseEntropy` wrapped.
            let spec = NoiseSpec.laplace 1.0 0.2m
            let first = draws (NoiseMechanism.create ()) spec 64
            let second = draws (NoiseMechanism.create ()) spec 64

            Expect.notEqual
                first
                second
                "two independently constructed production mechanisms must not produce the same stream"
        }

        test "the seeded mechanism repeats exactly, and a different seed does not" {
            let spec = NoiseSpec.laplace 1.0 1m
            let a = draws (NoiseMechanism.seeded "alpha") spec 200
            let b = draws (NoiseMechanism.seeded "alpha") spec 200
            let c = draws (NoiseMechanism.seeded "beta") spec 200

            Expect.equal
                a
                b
                "the same seed must reproduce the same stream — this is what makes the cases below deterministic"

            Expect.notEqual a c "a different seed must not, else the 'seed' is not a seed"
        }

        test "the discrete Laplace draw matches its closed form, and zero noise does not" {
            let spec = NoiseSpec.laplace 1.0 1m
            let sample = draws (NoiseMechanism.seeded "laplace-1") spec SampleCount
            let scale = laplaceScale spec

            expectNear "empirical standard deviation" (discreteLaplaceStdDev scale) (stdDevOf sample)
            Expect.isTrue (abs (meanOf sample) < 0.2) $"the mechanism must be unbiased, mean was {meanOf sample}"

            // Shape, not merely spread: the point mass at zero pins the
            // distribution's form. A sampler with the right variance and
            // the wrong shape fails here.
            let atZero =
                float (sample |> List.filter (fun x -> x = 0L) |> List.length)
                / float SampleCount

            expectNear "P[Y = 0]" (discreteLaplaceAtZero scale) atZero

            // Symmetry — a one-sided sampler would leak the direction of
            // every perturbation.
            let positive = sample |> List.filter (fun x -> x > 0L) |> List.length
            let negative = sample |> List.filter (fun x -> x < 0L) |> List.length
            expectNear "positive/negative balance" (float positive) (float negative)

            // NEGATIVE CONTROL — the deterministic release must fail
            // every one of those. Without this the assertions above
            // would pass on a mechanism that had stopped drawing.
            let flat = draws zero spec 512
            Expect.equal (stdDevOf flat) 0.0 "the zero mechanism has no spread…"

            Expect.isFalse
                (abs (stdDevOf flat - discreteLaplaceStdDev scale)
                 <= discreteLaplaceStdDev scale * 0.08)
                "…so it fails the spread assertion above, which is what makes that assertion a measurement"

            Expect.isFalse
                (abs (1.0 - discreteLaplaceAtZero scale) <= discreteLaplaceAtZero scale * 0.08)
                "…and its point mass at zero is 1, which fails the shape assertion too"
        }

        test "halving epsilon doubles the spread" {
            // Calibration, not presence. Noise that ignored its epsilon
            // would pass every "is it non-zero" check and fail here.
            let wide = NoiseSpec.laplace 1.0 0.5m
            let narrow = NoiseSpec.laplace 1.0 1m

            let wideSd = stdDevOf (draws (NoiseMechanism.seeded "eps-wide") wide SampleCount)

            let narrowSd =
                stdDevOf (draws (NoiseMechanism.seeded "eps-narrow") narrow SampleCount)

            expectNear "wide arm" (discreteLaplaceStdDev (laplaceScale wide)) wideSd
            expectNear "narrow arm" (discreteLaplaceStdDev (laplaceScale narrow)) narrowSd

            Expect.isTrue
                (wideSd > narrowSd * 1.7)
                $"halving epsilon must widen the distribution measurably: {wideSd} vs {narrowSd}"
        }

        test "doubling sensitivity widens the spread by the predicted factor" {
            // Sensitivity is the input that decides whether the
            // guarantee holds, so it has to visibly move the mechanism.
            // The predicted factor is 3/2, not 2: the lattice
            // sensitivities are ⌈2/1⌉+1 = 3 and ⌈1/1⌉+1 = 2.
            let single = NoiseSpec.laplace 1.0 1m
            let double = NoiseSpec.laplace 2.0 1m

            Expect.equal (NoiseSpec.latticeSensitivity single) 2 "⌈1/1⌉ + 1"
            Expect.equal (NoiseSpec.latticeSensitivity double) 3 "⌈2/1⌉ + 1"

            let singleSd = stdDevOf (draws (NoiseMechanism.seeded "sens-1") single SampleCount)
            let doubleSd = stdDevOf (draws (NoiseMechanism.seeded "sens-2") double SampleCount)

            expectNear "sensitivity 1" (discreteLaplaceStdDev (laplaceScale single)) singleSd
            expectNear "sensitivity 2" (discreteLaplaceStdDev (laplaceScale double)) doubleSd
            expectNear "ratio" 1.5 (doubleSd / singleSd)
        }

        test "a coarser lattice costs less sensitivity" {
            // The other axis of the same arithmetic: ⌈Δ/g⌉ + 1 falls as
            // g rises, so a coarser release buys a narrower draw.
            let fine = NoiseSpec.laplace 10.0 1m
            let coarse = fine |> NoiseSpec.withGranularity 5.0

            Expect.equal (NoiseSpec.latticeSensitivity fine) 11 "⌈10/1⌉ + 1"
            Expect.equal (NoiseSpec.latticeSensitivity coarse) 3 "⌈10/5⌉ + 1"
        }

        test "the discrete Gaussian matches the sigma its (epsilon, delta) implies" {
            let spec = NoiseSpec.gaussian 1.0 1m 0.000001m
            let sample = draws (NoiseMechanism.seeded "gauss") spec SampleCount

            // sigma from the zCDP conversion the mechanism uses:
            // rho = (sqrt(L + eps) - sqrt L)^2 with L = ln(1/delta),
            // then sigma^2 = latticeSensitivity^2 / (2 rho).
            let l = -log 0.000001
            let root = sqrt (l + 1.0) - sqrt l
            let rho = root * root
            let sensitivity = float (NoiseSpec.latticeSensitivity spec)
            let sigma = sqrt (sensitivity * sensitivity / (2.0 * rho))

            expectNear "empirical standard deviation" sigma (stdDevOf sample)
            Expect.isTrue (abs (meanOf sample) < 0.5) $"the mechanism must be unbiased, mean was {meanOf sample}"

            // A tighter delta must widen it — the parameter is real.
            let looser = NoiseSpec.gaussian 1.0 1m 0.001m

            let looserSd =
                stdDevOf (draws (NoiseMechanism.seeded "gauss-loose") looser SampleCount)

            Expect.isTrue
                (looserSd < stdDevOf sample)
                $"a larger delta buys a narrower draw: {looserSd} vs {stdDevOf sample}"
        }

        test "a Gaussian spec with no delta is refused rather than silently calibrated" {
            let spec = {
                NoiseSpec.laplace 1.0 1m with
                    Distribution = GaussianNoise
            }

            Expect.isError (NoiseSpec.validate spec) "the Gaussian mechanism has no pure-epsilon calibration"

            Expect.throws
                (fun () -> (NoiseMechanism.create ()).SampleUnits spec |> ignore)
                "…and the sampler refuses it rather than inventing a delta"
        }

        test "release snaps onto the public lattice — the other half of the Mironov defence" {
            // Driven with the ZERO mechanism on purpose: with no noise at
            // all, whatever comes out is entirely the snap. If `release`
            // returned the input, this is where it shows.
            let spec = NoiseSpec.laplace 1.0 1m |> NoiseSpec.withGranularity 0.25
            let raw = 1.3719283746

            Expect.equal (NoiseMechanism.release zero spec raw) 1.25 "the release is a lattice point, not the input"

            Expect.notEqual
                (raw + zero.Sample spec)
                1.25
                "…whereas naive `value + Sample` keeps every low-order bit of the input, which is the attack"

            Expect.equal (NoiseMechanism.snapUnits spec raw) 5L "1.3719… is 5 steps of 0.25"

            // The lattice survives a real draw: every released value is a
            // multiple of the granularity, which is what makes the
            // support public.
            let mechanism = NoiseMechanism.seeded "lattice"

            let offGrid =
                [ for _ in 1..256 -> NoiseMechanism.release mechanism spec raw ]
                |> List.filter (fun v -> abs (Math.Round(v / 0.25) * 0.25 - v) > 1e-9)

            Expect.isEmpty offGrid "every noised release must land on the declared lattice"
        }

        test "Sample is SampleUnits scaled by the granularity" {
            let spec = NoiseSpec.laplace 1.0 1m |> NoiseSpec.withGranularity 4.0
            let mechanism = NoiseMechanism.seeded "scaled"
            let units = [ for _ in 1..64 -> mechanism.Sample spec ]

            Expect.isEmpty
                (units |> List.filter (fun v -> abs (v % 4.0) > 1e-9))
                "every float draw is an exact multiple of the lattice step"
        }
    ]

// ─── 2. The declared policy ──────────────────────────────────────────

let policyTests =
    testList "Phase 481 — the calibration is declared data, and an undeclarable one is refused" [

        test "epsilon adds across the targets a policy noises" {
            let policy =
                NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.4m)
                |> NoisedReleasePolicy.withValueNoise (NoiseSpec.laplace 500.0 0.6m)

            Expect.equal
                (NoisedReleasePolicy.totalEpsilon policy)
                1.0m
                "basic sequential composition — two draws about one cohort cost the sum of their epsilons"

            Expect.equal
                (NoisedReleasePolicy.totalEpsilon (NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.4m)))
                0.4m
                "one target costs one epsilon"
        }

        test "a policy carrying a separate spec per target is the point" {
            // A count's sensitivity is not a sum's; one shared number
            // would necessarily be wrong for one of them.
            let policy =
                NoisedReleasePolicy.forValues (NoiseSpec.laplace 500.0 0.5m)
                |> NoisedReleasePolicy.withCountNoise (NoiseSpec.laplace 1.0 0.5m)

            Expect.equal (policy.CountNoise |> Option.map _.Sensitivity) (Some 1.0) "the count spec keeps cohort units"
            Expect.equal (policy.ValueNoise |> Option.map _.Sensitivity) (Some 500.0) "the value spec keeps value units"
        }

        test "a policy that draws no noise is an error, not a no-op" {
            let empty: NoisedReleasePolicy = { CountNoise = None; ValueNoise = None }

            Expect.isNonEmpty
                (NoisedReleasePolicy.validate empty)
                "charging epsilon for a deterministic answer is the one thing the ledger must not be asked to account for"
        }

        test "an uncalibratable spec is rejected on every axis" {
            let cases = [
                "unbounded sensitivity",
                {
                    NoiseSpec.laplace 1.0 1m with
                        Sensitivity = infinity
                }
                "zero sensitivity", NoiseSpec.laplace 0.0 1m
                "zero epsilon", NoiseSpec.laplace 1.0 0m
                "negative epsilon", NoiseSpec.laplace 1.0 -1m
                "zero granularity", (NoiseSpec.laplace 1.0 1m |> NoiseSpec.withGranularity 0.0)
                "delta at 1", NoiseSpec.gaussian 1.0 1m 1m
                "delta at 0", NoiseSpec.gaussian 1.0 1m 0m
            ]

            for label, spec in cases do
                Expect.isError (NoiseSpec.validate spec) $"{label} must be refused"

            Expect.isOk (NoiseSpec.validate (NoiseSpec.laplace 1.0 1m)) "CONTROL — a healthy spec passes"

            Expect.isOk
                (NoiseSpec.validate (NoiseSpec.gaussian 1.0 1m 0.000001m))
                "CONTROL — so does a healthy Gaussian"
        }

        test "the audit description names the mechanism and every declared parameter" {
            let described =
                NoisedReleasePolicy.describe (
                    NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)
                    |> NoisedReleasePolicy.withValueNoise (NoiseSpec.gaussian 500.0 0.5m 0.000001m)
                )

            Expect.stringContains described "discrete Laplace" "the count mechanism is named"
            Expect.stringContains described "discrete Gaussian" "…and the value mechanism"
            Expect.stringContains described "epsilon=0.5" "…with the epsilon it spends"
            Expect.stringContains described "sensitivity=500" "…and the sensitivity it was calibrated against"
        }
    ]

// ─── 3. Applying a policy to a cleared cohort ────────────────────────

let private cell label count value : PrivacyCell = {
    Label = label
    Count = count
    Value = value
}

let private histogram cells : CohortResult = { Shape = Histogram; Cells = cells }

let cohortTests =
    testList "Phase 481 — noise lands on the cells, per cell, after the floor" [

        test "each cell draws independently" {
            // One draw reused across a histogram is a single offset an
            // observer differences out — the mechanism would be
            // deterministic in every way that matters.
            let policy = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)
            let source = histogram [ for i in 1..40 -> cell $"b{i}" 100 None ]
            let noised = CohortNoise.apply (NoiseMechanism.seeded "per-cell") policy source

            let distinct = noised.Cells |> List.map _.Count |> List.distinct

            Expect.isGreaterThan (List.length distinct) 5 "40 identical cells must not come back with one shared offset"
        }

        test "a cell with no aggregate value keeps None" {
            let policy = NoisedReleasePolicy.forValues (NoiseSpec.laplace 10.0 0.5m)
            let source = histogram [ cell "a" 100 None; cell "b" 100 (Some 42.0) ]
            let noised = CohortNoise.apply (NoiseMechanism.seeded "values") policy source

            Expect.equal
                (noised.Cells |> List.item 0 |> _.Value)
                None
                "inventing a zero would be a disclosure of its own"

            Expect.isSome (noised.Cells |> List.item 1 |> _.Value) "…while a real aggregate is noised"

            Expect.equal
                (noised.Cells |> List.map _.Count)
                [ 100; 100 ]
                "…and counts are untouched by a values-only policy"
        }

        test "applyTo leaves a withhold alone and never noises twice" {
            let policy = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)
            let mechanism = NoiseMechanism.seeded "idempotent"

            match CohortNoise.applyTo mechanism policy (Withheld "below floor") with
            | Withheld reason -> Expect.equal reason "below floor" "a withhold has nothing released to noise"
            | other -> failtestf "expected the withhold to pass through, got %A" other

            let once =
                CohortNoise.applyTo mechanism policy (Released(histogram [ cell "a" 100 None ], []))

            match CohortNoise.applyTo mechanism policy once with
            | NoisedRelease(result, _, _) ->
                match once with
                | NoisedRelease(first, _, _) ->
                    Expect.equal result first "noising twice would spend epsilon that was never reserved"
                | other -> failtestf "expected a NoisedRelease, got %A" other
            | other -> failtestf "expected a NoisedRelease, got %A" other
        }

        test "CONTROL — the zero mechanism leaves a cohort byte-identical" {
            // The GP 13 shape at the cohort level: the noise application
            // itself introduces nothing when the draw is zero, so any
            // difference observed elsewhere in this file is the draw.
            let policy = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)
            let source = histogram [ cell "a" 100 (Some 3.5); cell "b" 40 None ]

            Expect.equal (CohortNoise.apply zero policy source) source "a zero draw changes nothing"
        }
    ]

// ─── 4. End to end, through the structural gate ──────────────────────

/// NOT `private`: `JsonRpcPeerHost.contract` reflects via
/// `FSharpType.IsRecord` without the private-representation flag.
type ReachContract = {
    EstimateReach: string -> Async<CohortResult>
}

let private gateFloor: PrivacyGate = {
    MinCohortSize = 10
    SuppressionThreshold = 5
    PermittedShapes = Set.ofList [ Count ]
}

let private template: CleanRoomTemplate = {
    TemplateId = "reach"
    AllowedMethods = Set.ofList [ "EstimateReach" ]
    Floor = gateFloor
}

let private v1: ContractVersion = { Major = 1; Minor = 0 }

[<Literal>]
let private contractId = "example.reach"

let private callContext: PeerCallContext = {
    Peer = {
        PeerId = "buyer"
        DisplayName = "Buyer"
    }
    User = Anonymous
    ContractVersion = v1
    Route = [ "buyer" ]
    RootRequestId = "root-481"
    ParentRequestId = None
    HopsRemaining = 4
}

type private DecisionSink() =
    let rows = ResizeArray<PeerCleanRoomDecisionPayload>()
    member _.Rows = List.ofSeq rows

    member _.Sink: PeerCleanRoomDecisionPayload -> Async<unit> =
        fun payload -> async { rows.Add payload }

let private answering count : ReachContract = {
    EstimateReach =
        fun _ -> async {
            return {
                Shape = Count
                Cells = [ cell "all" count None ]
            }
        }
}

let private registrationFor (impl: ReachContract) =
    (JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] None impl).Registration

let private noisedGate
    (broker: ICleanRoomBroker)
    (sink: DecisionSink)
    (budget: PrivacyBudgetMeter option)
    (noise: (INoiseMechanism * NoisedReleasePolicy) option)
    (impl: ReachContract)
    =
    (CleanRoomGate.wrapNoised broker template None budget noise sink.Sink (registrationFor impl)).Registration

let private call (registration: PeerContractRegistration) =
    registration.Dispatch callContext "EstimateReach" "[\"any\"]" |> run

let private releasedCounts (registration: PeerContractRegistration) n =
    [ 1..n ]
    |> List.choose (fun _ ->
        match call registration with
        | Ok payload -> Some (JsonRpc.deserialize<CohortResult> payload).Cells.Head.Count
        | Error _ -> None)

/// A broker that noises the answer itself — the substituted mechanism
/// the substrate refuses, because there is nothing left for the floor to
/// bind against.
type private SelfNoisingBroker() =
    interface ICleanRoomBroker with
        member _.Enforce(_, _, _, result) =
            NoisedRelease(result, [], NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 1m))

let private at = DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero)

let private meterFor (ledger: IPrivacyBudgetLedger) (policy: PrivacyBudgetPolicy) =
    PrivacyBudgetMeter.create ledger policy
    |> PrivacyBudgetMeter.withClock (fun () -> at)

let gateTests =
    testList "Phase 481 — a gate configured for noised release charges, noises, and never falls back" [

        test "the gate releases calibrated noise around the true cohort" {
            let sink = DecisionSink()
            let policy = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)

            let registration =
                noisedGate
                    (CleanRoomBroker.create ())
                    sink
                    None
                    (Some(NoiseMechanism.seeded "gate", policy))
                    (answering 5000)

            let released = releasedCounts registration 400
            Expect.hasLength released 400 "every in-floor query is released"

            let mean = (released |> List.sumBy float) / 400.0
            expectNear "released mean" 5000.0 mean

            Expect.isGreaterThan
                (released |> List.distinct |> List.length)
                20
                "the released answer must vary — a constant 5000 is the pre-481 deterministic release"

            let noisedRows =
                sink.Rows |> List.filter (fun r -> r.Reason.Contains "calibrated noise")

            Expect.hasLength noisedRows 400 "every noised release is audited as one"

            Expect.stringContains
                (List.head noisedRows).Reason
                "epsilon=0.5"
                "…naming the calibration, which is a public parameter of a public mechanism"
        }

        test "CONTROL — the identical gate with no policy releases the true cohort, byte for byte" {
            // GP 13 / GP 11: a suppression-only deployment is unchanged.
            // Without this half, "the answer varied" above would pass
            // equally against a gate that had started corrupting answers.
            let noisedSink = DecisionSink()
            let plainSink = DecisionSink()

            let plain =
                noisedGate (CleanRoomBroker.create ()) plainSink None None (answering 5000)

            let preExisting =
                (CleanRoomGate.wrap
                    (CleanRoomBroker.create ())
                    template
                    noisedSink.Sink
                    (registrationFor (answering 5000)))
                    .Registration

            Expect.equal (releasedCounts plain 5) [ 5000; 5000; 5000; 5000; 5000 ] "an unnoised gate is deterministic"

            match call plain, call preExisting with
            | Ok a, Ok b -> Expect.equal a b "the pre-481 wrap and an unnoised wrapNoised produce identical bytes"
            | a, b -> failtestf "both routes must release: %A / %A" a b

            Expect.isEmpty
                (plainSink.Rows |> List.filter (fun r -> r.Reason <> ""))
                "…and record no calibration, because none was applied"
        }

        test "the floor is decided on TRUE counts — noise cannot rescue a sub-floor cohort" {
            // The ordering claim. A generous noise policy over a cohort
            // of 3 must still be withheld: if noise ran first, a lucky
            // draw would lift it over k and the floor would hold only in
            // expectation.
            let sink = DecisionSink()
            let policy = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.05m)

            let registration =
                noisedGate
                    (CleanRoomBroker.create ())
                    sink
                    None
                    (Some(NoiseMechanism.seeded "subfloor", policy))
                    (answering 3)

            Expect.isEmpty (releasedCounts registration 50) "a sub-floor cohort is withheld however wide the noise is"

            Expect.isEmpty (sink.Rows |> List.filter _.Released) "…and nothing was recorded as a release"
        }

        test "a broker that noises the answer itself is withheld" {
            let sink = DecisionSink()

            let registration = noisedGate (SelfNoisingBroker()) sink None None (answering 5000)

            match call registration with
            | Error(PeerCleanRoomWithheld id) ->
                Expect.equal id template.TemplateId "the refusal names the template only"
            | Error e -> failtestf "expected PeerCleanRoomWithheld, got %A" e
            | Ok payload -> failtestf "a broker-noised answer the substrate cannot check reached the wire: %s" payload

            match sink.Rows with
            | [ row ] ->
                Expect.isFalse row.Released "the refusal is recorded"
                Expect.stringContains row.Reason "already-noised" "…naming why the substrate could not check it"
            | other -> failtestf "expected exactly one decision row, got %i" (List.length other)
        }

        test "each noised release charges the ledger the mechanism's own epsilon" {
            let ledger = InMemoryPrivacyBudgetLedger(fun () -> at) :> IPrivacyBudgetLedger
            let budgetPolicy = PrivacyBudgetPolicy.create 3m 1m
            let sink = DecisionSink()

            // The declared per-query schedule is 1m; the mechanism spends
            // 0.5m. The ledger must book the mechanism's number, because
            // that is the one an actual privacy loss was incurred at.
            let noise = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)

            let registration =
                noisedGate
                    (CleanRoomBroker.create ())
                    sink
                    (Some(meterFor ledger budgetPolicy))
                    (Some(NoiseMechanism.seeded "charged", noise))
                    (answering 5000)

            Expect.hasLength (releasedCounts registration 4) 4 "four releases at 0.5 each fit inside a ceiling of 3"

            let audited =
                run (ledger.RemainingBudget(PrivacyBudgetPolicy.scopeFor "reach" "buyer" at budgetPolicy, 3m))

            Expect.equal audited.EpsilonCommitted 2.0m "four draws at epsilon 0.5 committed 2.0, not the schedule's 4.0"
            Expect.equal audited.QueryCount 4 "…over four accounted queries"
        }

        test "an exhausted budget withholds and never falls back to the un-noised answer" {
            let ledger = InMemoryPrivacyBudgetLedger(fun () -> at) :> IPrivacyBudgetLedger
            let sink = DecisionSink()
            let noise = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 1m)

            let registration =
                noisedGate
                    (CleanRoomBroker.create ())
                    sink
                    (Some(meterFor ledger (PrivacyBudgetPolicy.create 2m 1m)))
                    (Some(NoiseMechanism.seeded "exhausted", noise))
                    (answering 5000)

            let released = releasedCounts registration 6
            Expect.hasLength released 2 "a ceiling of 2 at epsilon 1 admits exactly two releases"

            // Both of them went through the mechanism. Asserted from the
            // audit trail rather than by checking the value differs from
            // 5000: a draw of zero is a legitimate outcome of a correct
            // sampler (P[Y = 0] is about a quarter at this scale), so
            // "the number changed" is not the property. The property is
            // that no release took a path that skipped the mechanism —
            // an un-noised fallback when the budget runs low is the one
            // behaviour that would be worse than not having the feature.
            let releasedRows = sink.Rows |> List.filter _.Released
            Expect.hasLength releasedRows 2 "…and the audit trail agrees on how many"

            Expect.isTrue
                (releasedRows |> List.forall (fun r -> r.Reason.Contains "calibrated noise"))
                "every release that happened was a noised one — there is no un-noised fallback path"

            let refusals = sink.Rows |> List.filter (fun r -> not r.Released)
            Expect.hasLength refusals 4 "the remaining four were refused"

            Expect.stringContains
                (List.head refusals).Reason
                "privacy budget"
                "…by the budget, recorded receiver-side where the quantities may live"
        }

        test "a no-ledger deployment still noises — the weaker, documented posture" {
            let sink = DecisionSink()
            let noise = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.5m)

            let registration =
                noisedGate
                    (CleanRoomBroker.create ())
                    sink
                    None
                    (Some(NoiseMechanism.seeded "unmetered", noise))
                    (answering 5000)

            let released = releasedCounts registration 40

            Expect.hasLength released 40 "no ledger means no ceiling: every query is answered"

            Expect.isGreaterThan
                (released |> List.distinct |> List.length)
                5
                "…and every answer still carries calibrated noise, spent untracked"
        }
    ]

// ─── 5. Composition posture ──────────────────────────────────────────

let private enabledConfig = {
    ServerConfig.defaults with
        PeerSubstrate = EnabledPeerSubstrate
}

let private appHosting () =
    PeerServerApp.create ()
    |> PeerServerApp.withConfig enabledConfig
    |> PeerServerApp.withContract (fun fusion ->
        JsonRpcPeerHost.contract<ReachContract> contractId [ v1 ] fusion (answering 5000))

let compositionTests =
    testList "Phase 481 — a noise policy nothing applies cannot ship" [

        test "a policy naming an ungated contract is refused" {
            let app =
                appHosting ()
                |> PeerServerApp.withNoisedRelease contractId (NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 1m))

            let problems = PeerServerApp.auditNoisedRelease app

            Expect.isNonEmpty problems "a noise policy with no clean-room template is applied by nothing"

            Expect.stringContains
                (String.concat "; " problems)
                "no clean-room template"
                "…and the refusal says which half is missing"
        }

        test "a policy whose calibration is invalid is refused at compose time" {
            let app =
                appHosting ()
                |> PeerServerApp.withCleanRoomTemplate contractId template
                |> PeerServerApp.withNoisedRelease contractId (NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0m))

            Expect.isNonEmpty
                (PeerServerApp.auditNoisedRelease app)
                "a privacy parameter that is wrong is wrong at compose time, not at the first call"

            Expect.throws (fun () -> PeerServerApp.enforceNoisedRelease app) "…and enforcing it refuses to start"
        }

        test "CONTROL — a healthy composition reports nothing, and one that composes no policy runs no probe" {
            let healthy =
                appHosting ()
                |> PeerServerApp.withCleanRoomTemplate contractId template
                |> PeerServerApp.withNoisedRelease contractId (NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 1m))

            Expect.isEmpty (PeerServerApp.auditNoisedRelease healthy) "a gated contract with a valid policy is fine"
            PeerServerApp.enforceNoisedRelease healthy

            let untouched =
                appHosting () |> PeerServerApp.withCleanRoomTemplate contractId template

            Expect.isEmpty (untouched.NoisedReleases) "a composition that never calls withNoisedRelease carries nothing"
            Expect.isEmpty (PeerServerApp.auditNoisedRelease untouched) "…and is not probed"
        }

        test "the last policy for a contract wins, matching withCleanRoomTemplate" {
            let first = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 1m)
            let second = NoisedReleasePolicy.forCounts (NoiseSpec.laplace 1.0 0.25m)

            let app =
                appHosting ()
                |> PeerServerApp.withCleanRoomTemplate contractId template
                |> PeerServerApp.withNoisedRelease contractId first
                |> PeerServerApp.withNoisedRelease contractId second

            Expect.equal
                (PeerServerApp.noisedReleaseMap app |> Map.tryFind contractId)
                (Some second)
                "the later declaration is the effective one"
        }
    ]