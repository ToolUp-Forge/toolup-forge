# Pass A — raw MathNet.Numerics 5.0

Every script below was written **from memory before any API lookup**, then executed with
`dotnet fsi`. Compiler and runtime output is verbatim. Where a first attempt failed, the second
attempt is the minimal repair a code assistant would make on seeing that exact error.

Preamble on every script:

```fsharp
#r "nuget: MathNet.Numerics.FSharp, 5.0.0"
```

---

## 1 — Bivariate linear regression

### Attempt 1 (as written from memory)

```fsharp
open MathNet.Numerics
open MathNet.Numerics.LinearRegression

let xs = [| 1.0; 2.0; 3.0; 4.0; 5.0; 6.0; 7.0; 8.0 |]
let ys = [| 2.1; 4.3; 6.2; 8.1; 10.4; 12.2; 14.1; 16.3 |]

let (intercept, slope) = SimpleRegression.Fit(xs, ys)
printfn "intercept = %f, slope = %f" intercept slope

let predicted = xs |> Array.map (fun x -> intercept + slope * x)
let r2 = GoodnessOfFit.RSquared(predicted, ys)
printfn "r2 = %f" r2
```

**Result: COMPILE FAILURE.**

```
p1.fsx(10,26): error FS0193: Type constraint mismatch. The type
    'struct (float * float)'
is not compatible with type
    ''a * 'b'
```

`SimpleRegression.Fit` returns a **`ValueTuple`**, and F#'s `let (a, b) = …` pattern only destructures
a reference tuple. The fix is `let struct (intercept, slope) = …`.

### Attempt 2 (one-line repair)

```fsharp
let struct (intercept, slope) = SimpleRegression.Fit(xs, ys)
```

**Result: GREEN.**

```
intercept = 0.175000, slope = 2.008333
r2 = 0.999494
```

Slope ≈ 2.0, R² > 0.99 — **numerically correct.**

### Analysis

- **Attempts to green: 2.** One failure, of the `struct`-tuple class. Deterministic, loud, and
  one-line to fix. It recurs on every MathNet entry point that returns a tuple (see §6).
- **Silent-divergence risk: LOW.** `GoodnessOfFit.RSquared(modelled, observed)` takes its arguments
  in that order and I happened to pass them that way; reversing them is possible and would go
  unnoticed, but the statistic is a well-defined single convention with no competing definitions.

---

## 2 — Multivariate regression with one categorical predictor

### Attempt 1 (as written from memory)

```fsharp
open MathNet.Numerics.LinearAlgebra
open MathNet.Numerics.LinearRegression

let rows = [
    10.0, "North";  12.0, "North";  15.0, "North";  18.0, "South"
    20.0, "South";  22.0, "South";  25.0, "North";  30.0, "South"
]
let ys = [| 25.0; 29.0; 35.0; 46.0; 50.0; 54.0; 55.0; 70.0 |]

// one-hot encode region, dropping the first level as the reference
let design =
    rows
    |> List.map (fun (spend, region) ->
        [| 1.0; spend; (if region = "South" then 1.0 else 0.0) |])
    |> array2D

let X = DenseMatrix.ofArray2 design
let y = DenseVector.ofArray ys

let coeffs = MultipleRegression.QR(X, y)
printfn "intercept = %f"   coeffs.[0]
printfn "spend     = %f"   coeffs.[1]
printfn "regionSouth = %f" coeffs.[2]
```

**Result: GREEN, first attempt.**

```
intercept = 5.000000
spend     = 2.000000
regionSouth = 5.000000
```

Exact recovery of the generating model — **numerically correct.**

### Analysis

- **Attempts to green: 1.** No failure. `MultipleRegression.QR` returns a `Vector<float>`, not a
  tuple, so the §1 trap does not fire here.
- **Silent-divergence risk: MEDIUM, and not where it looks.** The arithmetic needed no help. What
  the raw path does *not* produce is any statement of **which level became the reference**. The
  output above is three unlabelled numbers; `regionSouth = 5` is a name I wrote into a `printfn`,
  not something the library knows. Read back a week later, or handed to a model to interpret, the
  vector is ambiguous — `5.0` is the South-vs-North contrast only if you remember the encoding
  choice made eleven lines earlier.
- Two failure modes the eval did **not** trigger but which the design must survive: encoding all
  levels *and* an intercept (singular design matrix — the dummy-variable trap), and omitting the
  intercept column entirely. Both are one keystroke away and neither produces an error here; the
  first would make `QR` return a least-norm solution rather than fail.

---

## 3 — Descriptive statistics on a CSV column

### Attempt 1 (as written from memory)

```fsharp
open System.IO
open MathNet.Numerics.Statistics

let lines = File.ReadAllLines "sample.csv"
let header = lines.[0].Split(',')
let idx = header |> Array.findIndex (fun h -> h = "revenue")

let values =
    lines
    |> Array.skip 1
    |> Array.choose (fun line ->
        let cells = line.Split(',')
        match System.Double.TryParse(cells.[idx]) with
        | true, v -> Some v
        | _ -> None)

printfn "n       = %d" values.Length
printfn "mean    = %f" (Statistics.Mean values)
printfn "median  = %f" (Statistics.Median values)
printfn "stddev  = %f" (Statistics.StandardDeviation values)
printfn "min     = %f" (Statistics.Minimum values)
printfn "max     = %f" (Statistics.Maximum values)
printfn "p25     = %f" (Statistics.Quantile(values, 0.25))
printfn "p75     = %f" (Statistics.Quantile(values, 0.75))
printfn "skew    = %f" (Statistics.Skewness values)
```

**Result: GREEN, first attempt.**

```
n       = 9
mean    = 134.694444
median  = 132.250000
stddev  = 36.328101
min     = 87.500000
max     = 205.750000
p25     = 106.666667
p75     = 154.666667
skew    = 0.728860
```

### The p25 is not the p25 the user is checking against

Follow-up probe on the same nine values:

```
default Quantile p25   = 106.666667
R7 (Excel/numpy) p25   = 111.000000
R8 p25                 = 106.666667
Percentile(25)         = 106.666667
```

MathNet's `Statistics.Quantile` — **and** `Statistics.Percentile`, which reads like the
spreadsheet-compatible one — default to the **R-8 (median-unbiased)** convention. Excel
`PERCENTILE`, `numpy.percentile` and `pandas.quantile` all default to **R-7**. For this sample the
two differ by 4.3 units, about 4%.

Nothing in the code, the types, or the output records which convention produced the number. The
vibe-coder's spreadsheet says 111, their app says 106.67, and there is no thread to pull.

`QuantileCustom(values, p, QuantileDefinition.R7)` is the fix — but it is only reachable by someone
who already knew there was a question.

### Analysis

- **Attempts to green: 1.** No failure.
- **Silent-divergence risk: HIGH.** The quantile convention above, and one more: `Double.TryParse`
  without an `IFormatProvider` is **culture-sensitive**. On this machine it parsed `143.25`
  correctly; under a comma-decimal culture it would parse `143.25` as `14325` — or, given the
  comma-split, silently drop the row. The blank-cell handling was correct by luck of the same
  `TryParse` returning `false`, which also silently swallows genuinely malformed cells.

---

## 4 — Distribution fit

### Attempt 1 (as written from memory)

```fsharp
open MathNet.Numerics.Distributions

let counts = [| 0.;0.;1.;0.;2.;1.;3.;0.;5.;1.;2.;0.;7.;1.;0.;4. |]

let normal = Normal.Estimate counts
printfn "Normal: mu = %f, sigma = %f" normal.Mean normal.StdDev

let nb = NegativeBinomial.Estimate counts          // <-- does not exist
printfn "NegBinomial: r = %f, p = %f" nb.R nb.P

let llNormal = counts |> Array.sumBy normal.DensityLn
let llNb = counts |> Array.sumBy (fun x -> nb.ProbabilityLn(int x))
printfn "loglik normal = %f, loglik nb = %f" llNormal llNb
```

**Result: COMPILE FAILURE.**

```
p4.fsx(13,27): error FS0039: The type 'NegativeBinomial' does not define a field,
constructor, or member named 'Estimate'.
```

This is a straight **API hallucination**: `Normal.Estimate` exists, so `NegativeBinomial.Estimate`
is the obvious extrapolation, and it is wrong. A reflection probe over the whole
`MathNet.Numerics.Distributions` namespace shows how narrow the real surface is:

```
distributions with an Estimate method:
  InverseGaussian
  LogNormal
  Normal
  Weibull

NegativeBinomial public statics:
  IsValidParameterSet, PMF, PMFLn, CDF, Sample, Samples
```

**Four** distributions ship a fitter. Poisson, Gamma, Beta, Binomial, Exponential and Negative
Binomial — between them most of what count and duration data actually needs — ship none. The
assistant must derive an estimator by hand.

### Attempt 2 (hand-rolled method-of-moments)

```fsharp
// No NegativeBinomial.Estimate in MathNet 5.0 — method-of-moments by hand.
let m = Statistics.Mean counts
let v = Statistics.Variance counts
let p = m / v
let r = m * m / (v - m)
let nb = NegativeBinomial(r, p)
```

**Result: GREEN.**

```
Normal: mu = 1.687500, sigma = 2.088660
NegBinomial (MoM): r = 1.064544, p = 0.386819
loglik normal = -33.987379, loglik nb = -28.388466
```

The Negative Binomial wins on log-likelihood, as it should for over-dispersed counts —
**numerically correct.**

### Analysis

- **Attempts to green: 2.** One hard failure, of the worst kind: not a syntax slip but a
  *capability* the library does not have, discovered only at compile time.
- **Silent-divergence risk: HIGHEST of the six.** The repair is four lines of statistics the
  compiler cannot check. `p = m/v; r = m²/(v−m)` is the correct method-of-moments pair *for
  MathNet's `(r, p)` parameterisation* — a different parameterisation (`(r, μ)`, or `p` as the
  failure rather than success probability) inverts `p`, and the code still compiles, still runs,
  and still prints a plausible-looking pair. It also silently requires `v > m`: under-dispersed
  counts give a negative `r`, which `NegativeBinomial` accepts as a parameter and evaluates into
  nonsense rather than rejecting.
- The estimator choice is invisible in the output. `r = 1.06, p = 0.39` does not say
  "method-of-moments"; a reader comparing against an MLE fit from another tool sees a discrepancy
  with no explanation.

---

## 5 — Time-series smoothing

### Attempt 1 (as written from memory)

```fsharp
open MathNet.Numerics.Statistics

let series =
    [| 100.0; 104.0; 99.0; 108.0; 112.0; 107.0; 115.0; 121.0; 118.0; 126.0; 130.0; 128.0 |]

// 3-period centred rolling mean
let window = 3
let rolling = Statistics.MovingAverage(series, window) |> Seq.toArray
printfn "rolling mean: %A" rolling

let alpha = 0.3
let ewma =
    series
    |> Array.scan (fun prev x -> alpha * x + (1.0 - alpha) * prev) series.[0]
    |> Array.skip 1
printfn "ewma: %A" ewma
```

**Result: GREEN, first attempt.**

```
rolling mean: [|100.0; 102.0; 101.0; 103.6666667; 106.3333333; 109.0; 111.3333333;
  114.3333333; 118.0; 121.6666667; 124.6666667; 128.0|]
ewma: [|100.0; 101.2; 100.54; 102.778; 105.5446; 105.98122; 108.686854; 112.3807978;
  114.0665585; 117.6465909; 121.3526136; 123.3468296|]
```

### It is not the centred mean that was asked for

Read the first three outputs against the input `[100, 104, 99, …]`:

| index | output | what it is |
|---|---|---|
| 0 | 100.0 | the first value alone |
| 1 | 102.0 | `mean(100, 104)` — a partial window |
| 2 | 101.0 | `mean(100, 104, 99)` — the first full window |

`Statistics.MovingAverage` is **trailing**, with an expanding partial-window warm-up. The prompt
asked for **centred**, which would place `mean(100, 104, 99) = 101.0` at index **1**, not index 2.

The returned series is the right numbers one period late. It plots as a smooth curve that tracks the
raw series; there is no visual, dimensional or type-level cue that it is shifted. Feed it to a
trend-change detector and every detection is a week out.

The warm-up disagrees too: pandas `rolling(3, center=True).mean()` returns `NaN` at both ends,
MathNet returns partial-window means at the head and nothing at the tail. Averaging over a
one-element "window" and an averaged three is the kind of thing that reads as a genuine dip.

### Analysis

- **Attempts to green: 1.** No failure.
- **Silent-divergence risk: HIGH.** Alignment and warm-up are both wrong for the question asked,
  and both are invisible.

### 5b — Seasonal decomposition and Hodrick–Prescott: no surface at all

The phase's candidate list puts STL and HP under `ITimeSeriesFilter`. A reflection sweep of the
assembly for any public type matching `Stl` / `Seasonal` / `Decompos` / `Filter` / `Smooth` returns
**nothing**, and the full `Statistics` static surface is:

```
Covariance, EmpiricalCDF, EmpiricalCDFFunc, EmpiricalInvCDF, EmpiricalInvCDFFunc, Entropy,
FiveNumberSummary, GeometricMean, HarmonicMean, InterquartileRange, Kurtosis, LowerQuartile,
Maximum, MaximumAbsolute, MaximumMagnitudePhase, Mean, MeanStandardDeviation, MeanVariance,
Median, Minimum, MinimumAbsolute, MinimumMagnitudePhase, MovingAverage, OrderStatistic,
OrderStatisticFunc, Percentile, PercentileFunc, PopulationCovariance, PopulationKurtosis,
PopulationSkewness, PopulationSkewnessKurtosis, PopulationStandardDeviation, PopulationVariance,
Quantile, QuantileCustom, QuantileCustomFunc, QuantileFunc, QuantileRank, QuantileRankFunc,
Ranks, RootMeanSquare, Skewness, SkewnessKurtosis, StandardDeviation, UpperQuartile, Variance
```

`MovingAverage` is the only smoother in the library. There is no raw-library baseline for
decomposition because there is no raw-library capability — an assistant asked for STL either writes
one from scratch or reaches outside MathNet entirely.

---

## 6 — Nonlinear curve fit (control)

### Attempt 1 (as written from memory)

```fsharp
open MathNet.Numerics

let xs = [| 1.0; 2.0; 3.0; 5.0; 8.0; 12.0; 18.0; 25.0 |]
let ys = [| 18.1; 33.0; 45.2; 63.4; 80.5; 90.7; 96.9; 98.9 |]

let fitted =
    Fit.Curve(xs, ys, (fun a b x -> a * (1.0 - exp (-b * x))), 50.0, 0.1)

printfn "a = %f, b = %f" (fst fitted) (snd fitted)
```

**Result: COMPILE FAILURE.**

```
p6.fsx(14,31): error FS0001: One tuple type is a struct tuple, the other is a reference tuple
```

The **same** `struct`-tuple class as §1 — `Fit.Curve` also returns a `ValueTuple`.

### Attempt 2 (one-line repair)

```fsharp
let struct (a, b) = fitted
printfn "a = %f, b = %f" a b
```

**Result: GREEN.**

```
a = 99.588466, b = 0.202673
```

Generating parameters were `a = 100`, `b ≈ 0.2` — **numerically correct**, converged from a
deliberately rough initial guess (`50.0`, `0.1`).

### Analysis

- **Attempts to green: 2**, but the failure is *not curve-fit-specific* — it is the same
  ValueTuple-destructuring class as §1, which any typed wrapper removes for every operation at once
  simply by returning a record. Counting it as evidence for a dedicated `ICurveFitter` would be
  double-counting a generic finding.
- **Silent-divergence risk: LOW-to-MEDIUM, and of a different kind.** The arithmetic was right
  first-fit with a rough starting guess, which is the substantive result: MathNet's
  Levenberg–Marquardt is robust here and needs no help. What `Fit.Curve` does not return is any
  **convergence signal** — no iteration count, no final residual, no converged flag. A fit that
  stalled at a local optimum or exhausted its budget returns a `(float, float)` shaped exactly like
  this one. That is a real gap, but it is a *diagnostics* gap on an otherwise-correct operation, not
  the silent-arithmetic-divergence the other prompts surfaced.

---

## Aggregate

| # | Operation | Attempts to green | Failure class | Numerically correct once green | Silent-divergence risk |
|---|---|---|---|---|---|
| 1 | bivariate regression | 2 | ValueTuple destructuring | yes | LOW |
| 2 | multivariate + categorical | 1 | — | yes | MEDIUM (unlabelled reference level) |
| 3 | descriptive stats | 1 | — | yes, *for R-8* | **HIGH** (quantile convention; culture-sensitive parse) |
| 4 | distribution fit | 2 | API absent — no fitter exists | yes | **HIGHEST** (hand-rolled estimator, uncheckable) |
| 5 | time-series smoothing | 1 | — | **no — trailing, not centred** | **HIGH** (alignment + warm-up) |
| 5b | STL / HP decomposition | n/a | capability absent | n/a | n/a |
| 6 | nonlinear curve fit *(control)* | 2 | ValueTuple destructuring (generic) | yes | LOW–MEDIUM (no convergence signal) |

The headline is the disagreement between the two columns. Prompts 3 and 5 compiled and ran on the
first attempt and are the two most dangerous results in the table. Prompt 6 failed to compile and is
the least dangerous. **Attempts-to-green and silent-divergence risk are close to anti-correlated
here**, which is why the wrapper decision cannot be made on friction alone.
