// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.InterPlatform

open System
open System.Numerics
open System.Security.Cryptography
open System.Text

// ─── Phase 481 — the calibrated-noise mechanism library ──────────────
//
// `PrivacyBudgetLedger.fs` says, at length and on purpose, that it is an
// ACCOUNTING control and not a differential-privacy guarantee: ε-DP is a
// property of a RANDOMISED mechanism, `ICleanRoomBroker` suppresses and
// refuses without randomising anything, and summing ε over deterministic
// answers bounds nothing formally. This file is what makes that ε mean
// something. It ships the calibrated sampler; **which** ε to spend and
// **what** sensitivity to declare stay deployment decisions (GP 1) — the
// same mechanism-not-policy split the cipher seam takes.
//
// ── Which mechanisms, and which construction ──
//
// Two named mechanisms, both DISCRETE, both sampled with exact integer
// arithmetic over unbiased bits from a CSPRNG:
//
//   * **Discrete Laplace** (the two-sided geometric mechanism) — Ghosh,
//     Roughgarden & Sundararajan, *Universally Utility-Maximizing Privacy
//     Mechanisms*, STOC 2009. Pure ε-DP: adding noise drawn from
//     P[Y = y] ∝ exp(−|y|·ε/Δ) over the integers to a Δ-sensitive integer
//     query is ε-DP.
//   * **Discrete Gaussian** — Canonne, Kamath & Steinke, *The Discrete
//     Gaussian for Differential Privacy*, NeurIPS 2020. (ε, δ)-DP via
//     concentrated DP: their Theorem 7 gives ρ-zCDP with ρ = Δ²/(2σ²),
//     and Bun & Steinke, *Concentrated Differential Privacy:
//     Simplifications, Extensions, and Lower Bounds*, TCC 2016,
//     Proposition 1.3 converts ρ-zCDP to (ρ + 2√(ρ·ln(1/δ)), δ)-DP.
//
// The **samplers** are Canonne–Kamath–Steinke (2020) Algorithms 1–3:
// `Bernoulli(exp(−γ))` for a rational γ built only from unbiased coin
// flips (Algorithm 1 plus its γ > 1 extension), discrete Laplace by
// geometric decomposition (Algorithm 2), and discrete Gaussian by
// rejection sampling from a discrete Laplace proposal (Algorithm 3).
// Every intermediate is a `BigInteger` ratio; no floating point is
// evaluated anywhere on the sampling path.
//
// ── Why not the textbook float sampler, stated plainly ──
//
// The obvious implementation — draw U ~ Uniform(0,1) as a `float` and
// return −b·sgn(U−½)·ln(1−2|U−½|) — is a **published attack**, not a
// stylistic preference: Mironov, *On Significance of the Least
// Significant Bits for Differential Privacy*, CCS 2012. Floating-point
// Laplace output has a granular, non-uniform support; the set of doubles
// reachable from a given true value is very nearly disjoint from the set
// reachable from a neighbouring one, so the low-order bits of the
// released double leak the noiseless answer outright. The ε printed
// beside such an implementation is not the ε it delivers.
//
// The defence used here is the standard one and has two halves, both
// load-bearing:
//
//   1. **The draw is an integer**, obtained without ever evaluating a
//      transcendental in floating point. There is no rounding step in
//      which the true value can hide.
//   2. **The released value is SNAPPED to a public lattice** before the
//      noise is added (`NoiseSpec.Granularity`). Releasing `value +
//      noise` where `value` is an arbitrary double re-opens the attack
//      from the other side — the noise is discrete, so `value`'s own low
//      bits survive addition unchanged. `NoiseMechanism.release` is the
//      sound path and `INoiseMechanism.Sample` alone is NOT; the doc
//      comments say so where a caller will read them.
//
// Snapping costs sensitivity: rounding to a lattice of step `g` can move
// two neighbouring answers apart by up to `⌊Δ/g⌋ + 1` lattice steps
// rather than `Δ/g`, so `NoiseSpec.latticeSensitivity` charges the upper
// bound `⌈Δ/g⌉ + 1`. The `+1` is not slack to be tuned away — it is the
// lattice boundary two neighbouring inputs can straddle. It costs up to
// a factor of two in noise on the commonest case (a count, Δ = 1,
// g = 1), and that is the side to be wrong on: under-charging would
// deliver a larger ε than the one printed on the release, silently.
//
// ── What is and is not guaranteed ──
//
// Guaranteed, GIVEN a correctly declared `Sensitivity`:
//
//   * A `LaplaceNoise` release is ε-DP for the spec's ε, over the
//     lattice.
//   * A `GaussianNoise` release is (ε, δ)-DP for the spec's ε and δ, by
//     the zCDP conversion above. The σ chosen is valid for every ε > 0 —
//     deliberately not the classical σ ≥ Δ√(2 ln(1.25/δ))/ε, whose proof
//     holds only for ε ≤ 1 and which is quietly wrong above it.
//   * Sampling consumes only `RandomNumberGenerator` bits on the
//     production path.
//
// NOT guaranteed, and the caller owns each:
//
//   * **Sensitivity is an INPUT, and a wrong one voids everything.** A
//     count query over a dataset where one subject contributes one row
//     has sensitivity 1; a sum over a value clamped to [0, 500] has
//     sensitivity 500; an unclamped sum has UNBOUNDED sensitivity and no
//     finite noise makes it private. `NoisedReleasePolicy` therefore
//     carries a separate spec per target — a count's sensitivity is not
//     a sum's, and one shared number would silently be wrong for one of
//     them.
//   * **Composition across releases** is the ledger's job, and it is
//     basic (sequential) composition: charges add
//     (`NoisedReleasePolicy.totalEpsilon`), per Dwork & Roth Theorem
//     3.16. Nothing here offers advanced composition, for the reason
//     `PrivacyBudgetLedger.fs` gives.
//   * **Post-processing** (clamping a negative count to zero, rounding
//     for display) is DP-safe but biases the estimator, so the substrate
//     does none of it — a negative noised count is the honest output of
//     the mechanism, and a consumer that wants it clamped clamps it.
//   * **A saturating draw.** `SampleUnits` returns `int64` and a draw
//     outside that range saturates. It is reachable only at a scale
//     above ~10¹⁸ lattice steps, i.e. an ε so small the release carries
//     no information at all; it is written down rather than hidden
//     because a silent clamp on a noise distribution is exactly the
//     class of defect this file exists to avoid.
//   * **Nothing here defends the CHOICE of query.** The gate's k-floor,
//     the template surface and the ε ledger are the other three halves
//     of that story, and all four are needed.
//
// ── Cost when unused (GP 13) ──
//
// Nothing in this file is reachable unless a composition calls
// `PeerServerApp.withNoisedRelease`. With no policy composed no
// `INoiseMechanism` is registered, the gate takes one `option` match,
// and a gated answer is byte-for-byte the Phase 311 release (GP 11).

/// Which named mechanism draws the noise.
///
/// The case names carry the `Noise` qualifier rather than the bare
/// distribution names: `ToolUp.InterPlatform` is a wide namespace, and
/// two DUs one `open` apart sharing a case name is how a call site
/// silently binds the wrong one — the argument `RoundEvent`'s
/// `Federation` qualifier already makes in this namespace.
type NoiseDistribution =
    /// The discrete Laplace (two-sided geometric) mechanism — pure ε-DP,
    /// `Delta` unused. Ghosh, Roughgarden & Sundararajan (STOC 2009);
    /// sampled per Canonne–Kamath–Steinke (2020) Algorithm 2.
    | LaplaceNoise
    /// The discrete Gaussian mechanism — (ε, δ)-DP, `Delta` REQUIRED.
    /// Canonne–Kamath–Steinke (2020), sampled per their Algorithm 3; σ
    /// derived through zCDP (their Theorem 7 + Bun–Steinke Prop 1.3), so
    /// the calibration is valid for every ε > 0.
    | GaussianNoise

/// The caller-declared calibration of one noise draw. Every field is
/// deployment data (GP 1) and value-typed (GP 12 rule 1), so a spec
/// travels into an audit row unchanged.
type NoiseSpec = {
    /// Which mechanism draws the noise.
    Distribution: NoiseDistribution
    /// The query's sensitivity **in the released value's own units** —
    /// L1 for `LaplaceNoise`, L2 for `GaussianNoise` (they coincide for
    /// the scalar-per-cell releases this substrate makes).
    ///
    /// **This is the input that decides whether the guarantee holds.**
    /// It is the most one subject's presence or absence can move the
    /// answer, and it is a property of the QUERY, not of the data in
    /// front of it. Declaring 1 for a sum over unclamped revenue is not
    /// a conservative approximation, it is a false statement, and the
    /// mechanism has no way to notice.
    Sensitivity: float
    /// The privacy loss this draw spends. Charged against
    /// `IPrivacyBudgetLedger` when a meter is composed.
    Epsilon: decimal
    /// The failure probability for `GaussianNoise` — required there,
    /// ignored by `LaplaceNoise` (which is pure ε-DP). Conventionally
    /// well below 1/n for a dataset of n subjects: δ is, informally, the
    /// probability the guarantee simply does not hold.
    Delta: decimal option
    /// The public lattice a release is snapped to, in the value's own
    /// units. **Not a display convenience** — it is half the defence
    /// against the floating-point attack in this file's header, because
    /// discrete noise added to an unsnapped double leaves that double's
    /// low bits intact. `1.0` for a count; for a currency aggregate,
    /// something like `0.01`, or coarser where utility allows.
    Granularity: float
}

/// The unbiased-bit source a `DiscreteNoiseMechanism` draws from.
///
/// A seam rather than a hard-wired `RandomNumberGenerator` for exactly
/// one reason: a test needs a reproducible stream to assert a
/// distribution against, and the alternative — a separate "seeded
/// sampler" implementation — would mean the code under test was never
/// the code that ships. Here both run the identical sampler and differ
/// only in where the bits come from.
type INoiseEntropy =
    /// Fill `buffer` with independent, uniformly-distributed bytes.
    abstract Fill: buffer: byte[] -> unit

/// The production bit source: `RandomNumberGenerator`, the platform
/// CSPRNG.
///
/// **A DP mechanism seeded from a predictable PRNG provides no privacy
/// at all** — an adversary who can reproduce the noise stream subtracts
/// it and reads the true answer. `System.Random`, and anything seeded
/// from a clock, a request id or a process id, is therefore not a
/// permissible source here; this type exists so the production path has
/// exactly one and it is named.
type CryptoNoiseEntropy() =

    interface INoiseEntropy with
        member _.Fill buffer =
            RandomNumberGenerator.Fill(Span<byte>(buffer))

/// **TEST-ONLY reproducible bit source.** A SHA-256 counter-mode stream
/// over a caller-supplied seed: uniformly distributed, and identical on
/// every run and every machine.
///
/// It is uniform but it is NOT secret, which is both the point and the
/// hazard: a deployment that composed this would publish its noise
/// stream alongside its answers. Nothing in `PeerCompose` can reach it —
/// the composed default is `CryptoNoiseEntropy` — and it lives here
/// rather than in the test project because a distribution assertion has
/// to drive the sampler that ships.
type SeededNoiseEntropy(seed: string) =
    let seedBytes = Encoding.UTF8.GetBytes(if isNull seed then "" else seed)
    let gate = obj ()
    let mutable counter = 0UL
    let mutable block = Array.empty<byte>
    let mutable offset = 0

    let refill () =
        let input = Array.zeroCreate<byte> (seedBytes.Length + 8)
        Array.blit seedBytes 0 input 0 seedBytes.Length

        BitConverter.TryWriteBytes(Span<byte>(input, seedBytes.Length, 8), counter)
        |> ignore

        counter <- counter + 1UL
        block <- SHA256.HashData input
        offset <- 0

    interface INoiseEntropy with
        member _.Fill buffer =
            lock gate (fun () ->
                for i in 0 .. buffer.Length - 1 do
                    if offset >= block.Length then
                        refill ()

                    buffer[i] <- block[offset]
                    offset <- offset + 1)

/// The calibrated-noise seam. Substitutable on the same terms as
/// `ICleanRoomBroker`: a deployment with its own accredited DP
/// implementation ships it here rather than forking the gate.
///
/// Two members rather than one because the integer draw is the primitive
/// and the float is a projection of it. A substituted mechanism that
/// implemented only the float would have nowhere to put the lattice
/// discipline, which is the half of the defence a caller cannot supply
/// from outside.
type INoiseMechanism =
    /// The draw, in LATTICE UNITS (multiples of `spec.Granularity`).
    /// This is the primitive: integer out, no floating point on the
    /// sampling path.
    abstract SampleUnits: spec: NoiseSpec -> int64

    /// The same draw expressed in the value's own units — exactly
    /// `float (SampleUnits spec) * spec.Granularity`.
    ///
    /// **Adding this to an unsnapped value is not a release path.** The
    /// noise is discrete, so `value + Sample spec` leaves every
    /// low-order bit of `value` intact and reopens the Mironov (CCS
    /// 2012) attack this file's header describes. Use
    /// `NoiseMechanism.release`, which snaps first.
    abstract Sample: spec: NoiseSpec -> float

/// Which released quantities a gate noises, and with what calibration.
///
/// **Two specs rather than one, deliberately.** A cohort count and a
/// summed aggregate are different queries with different sensitivities —
/// one subject moves a count by 1 and a revenue sum by whatever the
/// clamp bound is — so a single shared spec would necessarily be wrong
/// for one of them. Declaring them separately makes that wrongness
/// impossible to express by accident, and their ε values add
/// (`totalEpsilon`), because that is what basic composition says two
/// releases about the same cohort cost.
type NoisedReleasePolicy = {
    /// Applied to `PrivacyCell.Count`. `Sensitivity` is in cohort units
    /// (1 where one subject contributes at most one row) and
    /// `Granularity` should be `1.0` — a count lives on the integers
    /// already.
    CountNoise: NoiseSpec option
    /// Applied to `PrivacyCell.Value` where a cell carries one. A cell
    /// whose `Value` is `None` is left alone: there is nothing to noise,
    /// and inventing a zero would be a disclosure of its own.
    ValueNoise: NoiseSpec option
}

[<RequireQualifiedAccess>]
module NoiseSpec =

    /// The lattice a spec snaps to when it declares none: `1.0`, the
    /// integers, which is where a count already lives.
    [<Literal>]
    let DefaultGranularity = 1.0

    /// A pure ε-DP discrete-Laplace spec over the integers.
    let laplace (sensitivity: float) (epsilon: decimal) : NoiseSpec = {
        Distribution = LaplaceNoise
        Sensitivity = sensitivity
        Epsilon = epsilon
        Delta = None
        Granularity = DefaultGranularity
    }

    /// An (ε, δ)-DP discrete-Gaussian spec over the integers.
    let gaussian (sensitivity: float) (epsilon: decimal) (delta: decimal) : NoiseSpec = {
        Distribution = GaussianNoise
        Sensitivity = sensitivity
        Epsilon = epsilon
        Delta = Some delta
        Granularity = DefaultGranularity
    }

    /// Snap releases to a lattice of `granularity` instead of the
    /// integers. Coarser is cheaper in sensitivity terms and lossier in
    /// resolution; finer is the reverse.
    let withGranularity (granularity: float) (spec: NoiseSpec) = { spec with Granularity = granularity }

    /// Every way a spec is uncalibratable, as data rather than an
    /// exception — the posture `GateDecision` takes for a refusal (GP 12
    /// rule 3). Run at compose time by `PeerServerApp.run`, so a
    /// deployment cannot boot carrying a spec the sampler would have to
    /// reject on every call.
    let validate (spec: NoiseSpec) : Result<unit, string> =
        if
            Double.IsNaN spec.Sensitivity
            || Double.IsInfinity spec.Sensitivity
            || spec.Sensitivity <= 0.0
        then
            Error
                "Sensitivity must be a finite number greater than zero: it is the most one subject can move the answer, and an unbounded query cannot be made private by any finite noise"
        elif
            Double.IsNaN spec.Granularity
            || Double.IsInfinity spec.Granularity
            || spec.Granularity <= 0.0
        then
            Error "Granularity must be a finite number greater than zero — it is the lattice the release is snapped to"
        elif spec.Epsilon <= 0m then
            Error "Epsilon must be greater than zero — a zero-epsilon mechanism releases nothing at all"
        else
            match spec.Distribution, spec.Delta with
            | GaussianNoise, None ->
                Error
                    "GaussianNoise requires a Delta: the Gaussian mechanism is (epsilon, delta)-DP and has no pure-epsilon calibration. Use NoiseSpec.gaussian, or LaplaceNoise for a pure-epsilon release"
            | GaussianNoise, Some delta when delta <= 0m || delta >= 1m ->
                Error "Delta must lie strictly between 0 and 1 — it is the probability the guarantee does not hold"
            | _ -> Ok()

    /// The spec's sensitivity measured in LATTICE steps: `⌈Δ/g⌉ + 1`.
    ///
    /// The `+1` is the lattice boundary. Two neighbouring answers `a`
    /// and `b` with `|a − b| ≤ Δ` can straddle one, so `round(a/g)` and
    /// `round(b/g)` differ by up to `⌊Δ/g⌋ + 1` steps — more than the
    /// `Δ/g` a naive conversion would charge. See the file header for
    /// why the loose side is the correct side.
    let latticeSensitivity (spec: NoiseSpec) : int =
        let steps = ceil (spec.Sensitivity / spec.Granularity)

        if Double.IsNaN steps || steps > 1e15 then
            Int32.MaxValue - 1
        else
            int steps + 1

    /// An audit-facing one-liner. Every number in it is a declared
    /// parameter of a public mechanism — ε, δ and sensitivity are not
    /// secrets, and Kerckhoffs applies: a mechanism whose safety needed
    /// them hidden would not be one.
    let describe (spec: NoiseSpec) : string =
        let mechanism =
            match spec.Distribution with
            | LaplaceNoise -> "discrete Laplace"
            | GaussianNoise -> "discrete Gaussian"

        let delta =
            match spec.Delta with
            | Some d -> sprintf ", delta=%M" d
            | None -> ""

        sprintf
            "%s (epsilon=%M%s, sensitivity=%g, granularity=%g)"
            mechanism
            spec.Epsilon
            delta
            spec.Sensitivity
            spec.Granularity

/// The Canonne–Kamath–Steinke (NeurIPS 2020) exact samplers, over
/// `BigInteger` ratios and unbiased bits. Internal: the algorithms are
/// published and cited, but their signatures are not a contract — the
/// contract is `INoiseMechanism`.
module internal ExactSampling =

    let private two = BigInteger 2

    /// Uniform over `{0, …, n−1}` by rejection over the smallest whole
    /// number of bytes covering `n`, with the surplus high bits masked
    /// off.
    ///
    /// Rejection rather than a modulo fold is what keeps it exactly
    /// uniform: reducing a power-of-two range modulo a non-dividing `n`
    /// is biased towards the low residues, and a biased coin here is a
    /// biased noise distribution — the calibration would be wrong by an
    /// amount nobody could see.
    let uniformBelow (entropy: INoiseEntropy) (n: BigInteger) : BigInteger =
        if n <= BigInteger.One then
            BigInteger.Zero
        else
            let bits = int ((n - BigInteger.One).GetBitLength())
            let byteCount = (bits + 7) / 8

            let mask =
                match bits % 8 with
                | 0 -> 0xFFuy
                | r -> byte ((1 <<< r) - 1)

            let buffer = Array.zeroCreate<byte> byteCount
            let mutable candidate = n

            while candidate >= n do
                entropy.Fill buffer
                buffer[byteCount - 1] <- buffer[byteCount - 1] &&& mask
                candidate <- BigInteger(ReadOnlySpan<byte>(buffer), true, false)

            candidate

    /// `Bernoulli(num/den)` for `0 ≤ num ≤ den`.
    let bernoulli (entropy: INoiseEntropy) (num: BigInteger) (den: BigInteger) : bool = uniformBelow entropy den < num

    /// One unbiased coin flip.
    let private coin (entropy: INoiseEntropy) : bool = bernoulli entropy BigInteger.One two

    /// `Bernoulli(exp(−γ))` for a rational `γ = num/den ∈ [0, 1]` —
    /// Canonne–Kamath–Steinke (2020) Algorithm 1. Consumes only coin
    /// flips; no exponential is ever evaluated.
    let private bernoulliExpNegUnit (entropy: INoiseEntropy) (num: BigInteger) (den: BigInteger) : bool =
        if num.IsZero then
            true
        else
            let mutable k = BigInteger.One
            let mutable running = true

            while running do
                if bernoulli entropy num (den * k) then
                    k <- k + BigInteger.One
                else
                    running <- false

            not (k % two).IsZero

    /// `Bernoulli(exp(−γ))` for any rational `γ = num/den ≥ 0` — the
    /// Canonne–Kamath–Steinke (2020) extension of Algorithm 1 past 1:
    /// `exp(−γ) = exp(−1)^⌊γ⌋ · exp(−(γ − ⌊γ⌋))`, so `⌊γ⌋` independent
    /// `Bernoulli(exp(−1))` flips gate one final fractional flip. The
    /// loop exits after ~1.6 flips in expectation whatever `⌊γ⌋` is,
    /// because each flip fails with probability `1 − e⁻¹`.
    let bernoulliExpNeg (entropy: INoiseEntropy) (num: BigInteger) (den: BigInteger) : bool =
        if num <= den then
            bernoulliExpNegUnit entropy num den
        else
            let whole = BigInteger.Divide(num, den)
            let remainder = num - whole * den
            let mutable survived = true
            let mutable i = BigInteger.Zero

            while survived && i < whole do
                if not (bernoulliExpNegUnit entropy BigInteger.One BigInteger.One) then
                    survived <- false

                i <- i + BigInteger.One

            survived && bernoulliExpNegUnit entropy remainder den

    /// Discrete Laplace of scale `t/s` — `P[Y = y] ∝ exp(−|y|·s/t)` over
    /// the integers — by Canonne–Kamath–Steinke (2020) Algorithm 2.
    ///
    /// `X = U + t·V`, with `U` uniform on `{0, …, t−1}` accepted with
    /// probability `exp(−U/t)` and `V` geometric of ratio `exp(−1)`,
    /// gives a one-sided `P[X = x] ∝ exp(−x/t)`; `Y = ⌊X/s⌋` rescales it
    /// to `∝ exp(−y·s/t)`; the final coin folds it symmetrically about
    /// zero, rejecting the duplicate representation of 0 so that the
    /// point mass at the origin stays correct.
    let discreteLaplace (entropy: INoiseEntropy) (t: BigInteger) (s: BigInteger) : BigInteger =
        let mutable answer = BigInteger.Zero
        let mutable settled = false

        while not settled do
            let u = uniformBelow entropy t

            if bernoulliExpNeg entropy u t then
                let mutable v = BigInteger.Zero

                while bernoulliExpNeg entropy BigInteger.One BigInteger.One do
                    v <- v + BigInteger.One

                let y = BigInteger.Divide(u + t * v, s)
                let negative = coin entropy

                if not (negative && y.IsZero) then
                    answer <- (if negative then -y else y)
                    settled <- true

        answer

    /// The largest integer `t` with `t² ≤ num/den`, by doubling then
    /// bisection — an integer square root of a rational, so the Gaussian
    /// proposal scale is derived without a `sqrt` on the sampling path
    /// and without a linear scan (which is minutes of work at the σ a
    /// small ε implies).
    let private floorSqrtRatio (num: BigInteger) (den: BigInteger) : BigInteger =
        if num.Sign <= 0 then
            BigInteger.Zero
        else
            let mutable hi = BigInteger.One

            while hi * hi * den <= num do
                hi <- hi * two

            let mutable lo = BigInteger.Divide(hi, two)

            while hi - lo > BigInteger.One do
                let mid = BigInteger.Divide(lo + hi, two)

                if mid * mid * den <= num then lo <- mid else hi <- mid

            lo

    /// Discrete Gaussian of variance `σ² = num/den` — Canonne–Kamath–
    /// Steinke (2020) Algorithm 3: rejection sampling with a discrete
    /// Laplace proposal of integer scale `t = ⌊σ⌋ + 1`, accepting `Y`
    /// with probability `exp(−(|Y| − σ²/t)² / (2σ²))`.
    ///
    /// The acceptance ratio stays rational throughout: with `σ² =
    /// num/den`, `(|Y| − σ²/t)² / (2σ²) = (|Y|·den·t − num)² /
    /// (2·num·den·t²)`.
    let discreteGaussian (entropy: INoiseEntropy) (num: BigInteger) (den: BigInteger) : BigInteger =
        let t = floorSqrtRatio num den + BigInteger.One
        let mutable answer = BigInteger.Zero
        let mutable settled = false

        while not settled do
            let y = discreteLaplace entropy t BigInteger.One
            let deviation = BigInteger.Abs y * den * t - num

            if bernoulliExpNeg entropy (deviation * deviation) (two * num * den * t * t) then
                answer <- y
                settled <- true

        answer

/// The calibration arithmetic between a declared `NoiseSpec` and the
/// exact samplers: decimal / float to exact rationals, and the zCDP
/// conversion that turns an (ε, δ) pair into a discrete-Gaussian
/// variance. Internal for the same reason `ExactSampling` is.
module internal NoiseCalibration =

    /// A `decimal` to its exact rational form. Exact, not approximate:
    /// `decimal` is a base-10 fixed-point type, so `0.1m` is `1/10` on
    /// the nose where `0.1` (the float) is not.
    let exactOfDecimal (d: decimal) : BigInteger * BigInteger =
        let bits = Decimal.GetBits d
        let scale = (bits[3] >>> 16) &&& 0xFF

        let mantissa =
            BigInteger(uint64 (uint32 bits[0]))
            + (BigInteger(uint64 (uint32 bits[1])) <<< 32)
            + (BigInteger(uint64 (uint32 bits[2])) <<< 64)

        let numerator = if d < 0m then -mantissa else mantissa
        numerator, BigInteger.Pow(BigInteger 10, scale)

    /// The denominator every float-derived rational is taken over: a
    /// power of two, so the conversion loses nothing the `float` was
    /// carrying at ordinary magnitudes.
    let private floatScale = BigInteger.Pow(BigInteger 2, 30)

    /// A float to an exact rational, rounded **up**. Used only for the
    /// Gaussian variance, where rounding up means marginally MORE noise
    /// than the calibration demands — the safe direction. Rounding down
    /// would deliver marginally less privacy than the ε printed on the
    /// release.
    let ceilRationalOfFloat (value: float) : BigInteger * BigInteger =
        let scaled = ceil (value * float floatScale)
        BigInteger(max 1.0 scaled), floatScale

    /// The ρ of the tightest ρ-zCDP guarantee implying (ε, δ)-DP, by Bun
    /// & Steinke (TCC 2016) Proposition 1.3: ρ-zCDP implies
    /// `(ρ + 2√(ρ·ln(1/δ)), δ)`-DP. Solving `ρ + 2√(ρL) = ε` for `√ρ`
    /// gives `√ρ = √(L + ε) − √L` with `L = ln(1/δ)`.
    ///
    /// Float arithmetic is sound HERE where it is not on the sampling
    /// path: this derives a public parameter from two public parameters,
    /// and neither input nor output depends on the data. The draw itself
    /// is still taken over exact integers.
    let rhoFor (epsilon: float) (delta: float) : float =
        let l = -log delta
        let root = sqrt (l + epsilon) - sqrt l
        root * root

    /// The discrete-Gaussian variance for a spec, in lattice units:
    /// `σ² = Δ² / (2ρ)`, from Canonne–Kamath–Steinke (2020) Theorem 7's
    /// `ρ = Δ²/(2σ²)` read the other way round.
    let gaussianVariance (spec: NoiseSpec) (latticeSensitivity: int) : float =
        let delta =
            match spec.Delta with
            | Some d -> float d
            | None -> invalidArg "spec" "GaussianNoise requires a Delta"

        let rho = rhoFor (float spec.Epsilon) delta
        let sensitivity = float latticeSensitivity
        sensitivity * sensitivity / (2.0 * rho)

    let saturate (v: BigInteger) : int64 =
        if v > BigInteger Int64.MaxValue then Int64.MaxValue
        elif v < BigInteger Int64.MinValue then Int64.MinValue
        else int64 v

    /// The integer draw for `spec`, in lattice units.
    let sampleUnits (entropy: INoiseEntropy) (spec: NoiseSpec) : int64 =
        let sensitivity = NoiseSpec.latticeSensitivity spec

        match spec.Distribution with
        | LaplaceNoise ->
            // Scale b = Δ_lattice / ε, exactly: with ε = eNum/eDen that
            // is (Δ_lattice · eDen) / eNum, and Algorithm 2 takes the
            // scale as a ratio `t/s` directly, so no division is ever
            // rounded.
            let eNum, eDen = exactOfDecimal spec.Epsilon
            saturate (ExactSampling.discreteLaplace entropy (BigInteger sensitivity * eDen) eNum)
        | GaussianNoise ->
            let varianceNum, varianceDen =
                ceilRationalOfFloat (gaussianVariance spec sensitivity)

            saturate (ExactSampling.discreteGaussian entropy varianceNum varianceDen)

/// The shipped mechanism: exact discrete sampling over `entropy`.
///
/// `NoiseMechanism.create ()` is the production construction. The type is
/// public so a test — or a deployment auditing the sampler — can drive it
/// from a declared bit source and see which one it is (`Entropy`).
type DiscreteNoiseMechanism(entropy: INoiseEntropy) =

    /// The production construction: `CryptoNoiseEntropy`, i.e. the
    /// platform CSPRNG.
    new() = DiscreteNoiseMechanism(CryptoNoiseEntropy())

    /// The bit source this mechanism draws from. Diagnostic — a
    /// composition (or its test) asserts the production path is the
    /// CSPRNG rather than trusting that it is.
    member _.Entropy = entropy

    interface INoiseMechanism with
        member _.SampleUnits spec =
            match NoiseSpec.validate spec with
            | Error reason -> invalidArg "spec" reason
            | Ok() -> NoiseCalibration.sampleUnits entropy spec

        member this.Sample spec =
            float ((this :> INoiseMechanism).SampleUnits spec) * spec.Granularity

[<RequireQualifiedAccess>]
module NoiseMechanism =

    /// The production mechanism: exact discrete sampling over the
    /// platform CSPRNG.
    let create () : INoiseMechanism =
        DiscreteNoiseMechanism() :> INoiseMechanism

    /// **TEST-ONLY.** The identical sampler over a reproducible SHA-256
    /// counter stream, so a distribution assertion measures the code that
    /// ships rather than a parallel implementation. A deployment that
    /// composed this would publish its noise stream — see
    /// `SeededNoiseEntropy`.
    let seeded (seed: string) : INoiseMechanism =
        DiscreteNoiseMechanism(SeededNoiseEntropy seed) :> INoiseMechanism

    /// Snap `value` onto the spec's public lattice, in lattice units.
    let snapUnits (spec: NoiseSpec) (value: float) : int64 =
        let units = Math.Round(value / spec.Granularity, MidpointRounding.ToEven)

        if Double.IsNaN units then 0L
        elif units > 9.2e18 then Int64.MaxValue
        elif units < -9.2e18 then Int64.MinValue
        else int64 units

    /// **The sound release path**: snap onto the public lattice, add the
    /// integer draw, return the lattice point.
    ///
    /// Both halves matter. Without the snap, `value`'s own low-order bits
    /// survive the addition and leak it (Mironov, CCS 2012); without an
    /// exact integer draw, the noise's low bits do the same. A caller
    /// that takes `INoiseMechanism.Sample` and adds it to a raw double
    /// has implemented the attack, not the mechanism.
    let release (mechanism: INoiseMechanism) (spec: NoiseSpec) (value: float) : float =
        let noised = snapUnits spec value + mechanism.SampleUnits spec
        float noised * spec.Granularity

[<RequireQualifiedAccess>]
module NoisedReleasePolicy =

    /// Noise cohort counts only; leave any aggregate value untouched.
    let forCounts (spec: NoiseSpec) : NoisedReleasePolicy = {
        CountNoise = Some spec
        ValueNoise = None
    }

    /// Noise aggregate values only; leave cohort counts untouched.
    let forValues (spec: NoiseSpec) : NoisedReleasePolicy = {
        CountNoise = None
        ValueNoise = Some spec
    }

    /// Also noise cohort counts, with a spec of their own — a count's
    /// sensitivity is not an aggregate's.
    let withCountNoise (spec: NoiseSpec) (policy: NoisedReleasePolicy) = { policy with CountNoise = Some spec }

    /// Also noise aggregate values, with a spec of their own.
    let withValueNoise (spec: NoiseSpec) (policy: NoisedReleasePolicy) = { policy with ValueNoise = Some spec }

    /// The privacy loss one release under this policy spends: the sum of
    /// the specs it draws.
    ///
    /// **Basic (sequential) composition** — Dwork & Roth, *The
    /// Algorithmic Foundations of Differential Privacy*, Theorem 3.16:
    /// the composition of ε₁- and ε₂-DP mechanisms is (ε₁ + ε₂)-DP. The
    /// same bound `PrivacyBudgetLedger.fs` accounts a series under, and
    /// for the same reason — a tighter bound derived from assumptions
    /// the deployment does not meet is worse than a loose one.
    let totalEpsilon (policy: NoisedReleasePolicy) : decimal =
        let epsilonOf (spec: NoiseSpec option) =
            spec |> Option.map _.Epsilon |> Option.defaultValue 0m

        epsilonOf policy.CountNoise + epsilonOf policy.ValueNoise

    /// Every reason this policy could not be honoured, as data. Empty on
    /// a healthy policy.
    ///
    /// A policy that noises nothing is itself an error: composing it
    /// would charge ε for a deterministic release, which is precisely
    /// the thing `PrivacyBudgetLedger.fs`'s header warns must not be
    /// described as differential privacy.
    let validate (policy: NoisedReleasePolicy) : string list =
        let specErrors =
            [ "CountNoise", policy.CountNoise; "ValueNoise", policy.ValueNoise ]
            |> List.choose (fun (label, spec) ->
                match spec with
                | None -> None
                | Some s ->
                    match NoiseSpec.validate s with
                    | Ok() -> None
                    | Error reason -> Some $"{label}: {reason}")

        match policy.CountNoise, policy.ValueNoise with
        | None, None ->
            "a noised-release policy that draws no noise is not a mechanism: it would charge epsilon for a deterministic answer, which is the one thing an epsilon budget must not be asked to account for"
            :: specErrors
        | _ -> specErrors

    /// An audit-facing one-liner naming each mechanism and its declared
    /// parameters. Recorded receiver-side on every noised release.
    let describe (policy: NoisedReleasePolicy) : string =
        let part label (spec: NoiseSpec option) =
            spec |> Option.map (fun s -> sprintf "%s %s" label (NoiseSpec.describe s))

        [ part "counts:" policy.CountNoise; part "values:" policy.ValueNoise ]
        |> List.choose id
        |> String.concat "; "