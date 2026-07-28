# Findings — the curated interface list for Phase 11.E.2

## The delta table

Scores are `0`–`3`. **Friction** = compile/run failures on the raw path. **Divergence** = the code
compiles, runs, and returns a plausible number that is wrong for the question asked, with nothing in
the toolchain objecting. **Delta** = what a curated catalog measurably removes.

| Operation | Friction (Pass A) | Divergence (Pass A) | Delta (Pass B) | Verdict |
|---|---|---|---|---|
| Descriptive statistics | 0 — green first attempt | **3** — R-8 default silently substituted for the R-7 the user's spreadsheet computes (106.67 vs 111.0, ~4%); culture-sensitive `TryParse` | **3** — `quantileDefinition` in *and* echoed out | **WRAP** |
| Distribution fit | **3** — `NegativeBinomial.Estimate` does not exist; only 4 of the library's distributions ship any fitter | **3** — the repair is four lines of hand-derived method-of-moments the compiler cannot check; a wrong parameterisation compiles and prints plausibly | **3** — family as an enumerated value; `method` + `logLikelihood`/`aic`/`bic` as fields | **WRAP** |
| Time-series smoothing | 0 — green first attempt | **3** — `MovingAverage` is trailing with expanding warm-up; the request said centred, so the result is one period late and plots correctly. STL / HP have **no surface at all** | **3** — `kind` required, `alignment` echoed, warm-up as `null` | **WRAP** |
| Linear regression (bivariate + multivariate + categorical) | 1 — ValueTuple destructuring (`FS0193`) on the bivariate arm; multivariate green first attempt | 2 — arithmetic correct on both arms, but the coefficient vector is **unlabelled**: no reference level, no term names. Dummy-variable trap and missing intercept are one keystroke away and neither errors | 2 — `referenceLevels` + named `term`s; encoding moves behind the boundary | **WRAP** |
| Nonlinear curve fit *(control)* | 1 — ValueTuple destructuring, the **same generic class** as the bivariate arm | 1 — correct fit from a rough initial guess; the gap is the absence of a convergence signal, a diagnostics gap on an otherwise-correct operation | — | **EXCLUDE** |

## What the table says

**Friction and divergence are close to anti-correlated.** The two operations that compiled on the
first attempt — descriptive statistics and time-series smoothing — produced the two most dangerous
results in the eval. The operation recommended for exclusion is the one that failed to compile
twice. An eval scoring only "did it compile" would have reached the opposite conclusion on every
row.

**The wrapper's value is almost never the arithmetic.** MathNet computed correct numbers everywhere
it had a surface at all. What it does not do is *say which convention it used*. Four fields carry
essentially the whole measured delta:

| Field | Removes |
|---|---|
| `quantileDefinition` | R-7 vs R-8 divergence against every spreadsheet and every Python notebook |
| `alignment` | trailing-vs-centred off-by-one that survives visual inspection |
| `method` | MLE-vs-moments discrepancy against another tool, with no explanation attached |
| `referenceLevels` | an unlabelled contrast coefficient that is uninterpretable a week later |

Each is a value that was implicit on the raw path and is explicit on the catalog path. That, rather
than convenience, is what 11.E.2 ships.

## The curated interface list

**Ship four** — `src/ToolUp.Algorithms.Server/Server/`:

| Interface | Method | Justified by |
|---|---|---|
| `IDescriptiveStats` | `Summarise: DescriptiveRequest -> Async<Result<DescriptiveSummary, AlgorithmError>>` | divergence 3, delta 3. `QuantileConvention` is a **field of the request**, defaulting to `ExcelCompatible` (R-7), and echoed on the result |
| `IDistributionFitter` | `Fit: DistributionFitRequest -> Async<Result<DistributionFitResult, AlgorithmError>>` | friction 3 + divergence 3. `EstimationMethod` echoed; `LogLikelihood` / `Aic` / `Bic` are fields, so no caller hand-picks PMF-vs-density |
| `ITimeSeriesFilter` | `Smooth: SmoothingRequest -> Async<Result<SmoothingResult, AlgorithmError>>` | divergence 3, plus a capability gap. `SmoothingKind` separates `TrailingMean` from `CentredMean`; warm-up periods are `None`, not silently-partial means |
| `IRegressionFitter` | `FitLinear: RegressionRequest -> Async<Result<RegressionFitResult, AlgorithmError>>` | one interface covers both arms — a bivariate fit is one numeric predictor and no categoricals. Categoricals arrive as raw labels; encoding, reference-level selection and the intercept column sit behind the boundary, and `ReferenceLevels` comes back on the result |

**Exclude one** — `ICurveFitter`:

The control prompt was run precisely so this decision would rest on evidence. MathNet's
Levenberg-Marquardt fitted a two-parameter diminishing-returns curve correctly from a deliberately
rough starting guess, first fit. Its only compile failure was the ValueTuple-destructuring class it
shares with the bivariate regression arm — a *generic* finding that any record-returning wrapper
removes for every operation at once, so counting it here would be double-counting.

The one curve-fit-specific gap is real, and is recorded rather than resolved: `Fit.Curve` returns no
convergence signal — no iteration count, no residual, no converged flag — so a stalled fit is shaped
exactly like a good one. That is a legitimate future interface, and the right trigger for it is a
`ConvergenceReport`-shaped requirement with a caller asking for it, not one probe in a pre-build
eval. Shipping a nonlinear-optimisation seam on this evidence would be exactly the intuition-driven
scoping the eval exists to prevent.

The phase text also names Levenberg-Marquardt-with-analytical-Jacobian under `ICurveFitter`. That
requirement traces to a response-curve hoist out of the commercial analytics library, not to a
vibe-coder primitive — a domain-specific need belonging to the consumer-adoption sub-phase, not to
the OSS catalog's opening surface.

## Consequences for the provider seam (Phase 11.E.3)

- Four typed fitters to implement, not five. `Fit.Curve` is not needed.
- The four echoed-convention fields are **provider obligations**, not decorations. A provider
  reporting `QuantileConvention.ExcelCompatible` must call `QuantileCustom(..., R7)`, not
  `Quantile`. MathNet's `Quantile` **and** `Percentile` both default to R-8; the R-7 path is
  `QuantileCustom` with an explicit `QuantileDefinition.R7`.
- `MovingAverage` cannot serve `CentredMean` directly — it is trailing with an expanding warm-up. A
  centred window is a re-index plus explicit `None` padding at both ends.
- No fitter exists for Poisson, Gamma, Beta, Binomial, Exponential or Negative Binomial. A provider
  declaring those families is committing to hand-written estimators, and each one needs a test
  pinning the parameterisation — that is the least checkable code in the whole family.
- A declared family or smoothing kind the provider cannot serve must come back as a typed
  `AlgorithmError`, never an exception and never a silent substitution.
