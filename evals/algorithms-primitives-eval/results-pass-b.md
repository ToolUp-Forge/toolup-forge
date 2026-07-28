# Pass B — hand-mocked `ToolUp.Algorithms` catalog tool

Pass B cannot be compiled — the catalog does not exist until Phase 11.E.2. What it *can* measure is
the thing Pass A showed actually matters: whether the **call shape** is unambiguous and whether the
**typed return** can be interpreted without knowing a convention that was never stated.

Each mock below is the tool signature as it would appear to a model in an `AIToolDefinition`. The
scoring asks three questions per prompt:

1. **Call shape** — is there exactly one correct way to invoke it, derivable from the signature?
2. **Return interpretation** — can every field be read correctly without external knowledge?
3. **Residual ambiguity** — what could still go wrong?

---

## Mock signatures

```
regression.linear
  response      : number[]                (required)
  numeric       : { name, values:number[] }[]     (required, may be empty)
  categorical   : { name, values:string[] }[]     (optional, default [])
  intercept     : boolean                 (optional, default true)
→ { coefficients: [{ term, estimate }], intercept, rSquared, adjustedRSquared,
    residualStandardError, observations, referenceLevels: [{ factor, level }] }

stats.describe
  values        : number[]                (required)
  quantiles     : number[]                (optional, default [0.25, 0.5, 0.75])
  quantileDefinition : "excelCompatible" | "medianUnbiased"   (optional, default "excelCompatible")
→ { count, mean, median, standardDeviation, variance, minimum, maximum, skewness,
    kurtosis, quantiles: [{ probability, value }], quantileDefinition }

distribution.fit
  values        : number[]                (required)
  family        : "normal" | "logNormal" | "poisson" | "negativeBinomial" | "gamma"   (required)
  method        : "maximumLikelihood" | "methodOfMoments"   (optional, provider default)
→ { family, method, parameters: [{ name, value }], logLikelihood, aic, bic, observations }

timeseries.smooth
  values        : number[]                (required)
  kind          : "trailingMean" | "centredMean" | "exponentiallyWeighted"   (required)
  window        : number                  (required for the mean kinds)
  alpha         : number                  (required for exponentiallyWeighted)
  warmUp        : "partialWindow" | "undefined"   (optional, default "undefined")
→ { values: (number | null)[], kind, window, alignment, warmUp }
```

---

## 1 — Bivariate linear regression

**Call shape: unambiguous.** `response = sales`, `numeric = [{ name: "spend", values: spend }]`,
`categorical` omitted. The one decision point — whether `numeric` is a bare array or a
name/values pair — is settled by the signature, and naming the predictor is what makes the returned
coefficient readable.

**Return interpretation: unambiguous.** `coefficients[0].term = "spend"`, `intercept`, `rSquared`
read directly. Pass A's ValueTuple failure has no analogue: a JSON object has no destructuring
convention to get wrong.

**Residual ambiguity: none material.** A caller could pass `intercept: false` by accident, but that
is a stated choice, not a hidden default.

**Delta vs Pass A: one deterministic compile failure removed.** Modest, and this is the arm where
the raw path was already close to fine.

---

## 2 — Multivariate regression with one categorical

**Call shape: unambiguous, and the interesting work is done by the *shape* rather than by the
caller.** `categorical: [{ name: "region", values: ["North"; "North"; …] }]` — the caller supplies
the raw labels. Dummy coding, dropping a reference level, and adding the intercept column are all on
the provider's side of the boundary, so the dummy-variable trap and the missing-intercept slip are
not reachable from the call site at all. Pass A had to get all three right by hand.

**Return interpretation: unambiguous, and this is the field that earns the wrapper.**
`referenceLevels: [{ factor: "region", level: "North" }]` states the contrast explicitly, so
`coefficients` entry `{ term: "region=South", estimate: 5.0 }` reads as "South is 5 above North"
without the reader reconstructing an encoding decision. Pass A returned three unlabelled numbers
and a `printfn` string the library never validated.

**Residual ambiguity: which level becomes the reference.** The signature does not let the caller
choose it. That is a deliberate simplification — the returned `referenceLevels` makes the choice
*legible*, which is the property that was missing, and a `referenceLevel` input can be added later
without breaking the shape.

**Delta vs Pass A: high on interpretability, nil on arithmetic.** Recorded honestly: the raw path
computed the right numbers first time.

---

## 3 — Descriptive statistics

**Call shape: unambiguous.** `values` plus optional `quantiles`.

**Return interpretation: unambiguous *because the convention is a field*.** `quantileDefinition`
appears in both the input and the echoed output. A caller who says nothing gets
`"excelCompatible"` — the R-7 convention that matches the spreadsheet they are comparing against —
and the output says so. A caller who wants the median-unbiased form asks for it by name.

This is the single highest-value line in the whole eval. Pass A's `p25 = 106.67` was not a bug: it
is a correct R-8 quantile, silently substituted for the R-7 quantile the user's spreadsheet
computes. No amount of care at the call site surfaces that, because the question is never posed.
Promoting the convention from an undocumented default to a named parameter with an echoed
confirmation is the entire fix.

**Residual ambiguity: none on the quantile axis.** Sample-vs-population variance remains a
convention the signature does not name; the field names (`variance`, `standardDeviation`) follow
the sample form as everything else in the SDK does, and this should be documented on the algorithm
description rather than left to inference.

**Delta vs Pass A: highest interpretive delta in the set**, against a Pass A that compiled first
time.

---

## 4 — Distribution fit

**Call shape: unambiguous, and it removes a capability cliff rather than a syntax trap.**
`family: "negativeBinomial"` is a value in an enumerated set. Pass A's failure was that
`NegativeBinomial.Estimate` **does not exist** and four lines of hand-derived method-of-moments had
to be written to compensate; here, whether a fitter exists is the provider's problem, and a family a
provider cannot fit comes back as a typed refusal naming the family rather than as a compile error
against a method that was never there.

**Return interpretation: unambiguous, and `method` is load-bearing.** The echoed
`method: "methodOfMoments"` tells the caller — and the model reading the tool result — that these
parameters are moment estimates, not maximum-likelihood ones. Pass A printed `r = 1.06, p = 0.39`
with no such marker, so a discrepancy against another tool's MLE fit had no explanation attached.
`logLikelihood` / `aic` / `bic` arriving as first-class fields also removes the second hand-rolled
step: Pass A had to know that a discrete family is scored with `ProbabilityLn` and a continuous one
with `DensityLn`, and mixing them yields comparable-looking, incomparable numbers.

**Residual ambiguity: the parameterisation of the returned parameters.** `[{ name: "r", value: … },
{ name: "p", value: … }]` still needs `p` defined as success-probability rather than failure-rate.
This should be stated in the algorithm's description text, which the model reads alongside the
signature.

**Delta vs Pass A: highest overall.** It removes both a hard failure and the least checkable piece
of hand-written statistics in the eval.

---

## 5 — Time-series smoothing

**Call shape: unambiguous, and it makes the ambiguity impossible to skip.** `kind` is required and
`"trailingMean"` / `"centredMean"` are separate values, so the alignment decision has to be made at
the call site. Pass A asked for centred, wrote the obvious call, and got trailing.

**Return interpretation: unambiguous.** `alignment` is echoed, so a downstream consumer can assert
it. `values` is `(number | null)[]` with `warmUp: "undefined"` as the default, so the periods before
the window is full are *absent* rather than silently averaged over a one-element window — the
partial-window behaviour Pass A got is still available, by asking for it.

**Residual ambiguity: even-window centring.** A centred mean with an even `window` has no exact
centre and needs a stated tie-break (half-weight endpoints, or refuse). The signature does not
settle it; the provider must document it and the algorithm description should say which.

**Delta vs Pass A: highest correctness delta.** Pass A produced a wrong-for-the-question answer
that compiled, ran, and looks right on a chart.

---

## 6 — Nonlinear curve fit

Not run. The prompt exists to establish whether `ICurveFitter` should ship at all, and Pass A
answered that on its own: correct fit, rough initial guess, one generic compile failure shared with
prompt 1. Mocking a signature to confirm that a JSON object is easier to read than a ValueTuple
would measure nothing that prompts 1–5 have not already measured.

The one curve-fit-specific gap Pass A found — no convergence signal — is noted in `findings.md` as a
follow-up candidate rather than resolved here.

---

## Aggregate

| # | Operation | Call shape | Return interpretation | Residual ambiguity | Interpretive delta |
|---|---|---|---|---|---|
| 1 | bivariate regression | unambiguous | unambiguous | none material | low |
| 2 | multivariate + categorical | unambiguous | unambiguous (`referenceLevels`) | reference level not selectable | **high** |
| 3 | descriptive stats | unambiguous | unambiguous (`quantileDefinition`) | sample-vs-population unstated | **highest** |
| 4 | distribution fit | unambiguous | unambiguous (`method`, `logLikelihood`) | parameterisation of `r`/`p` | **highest** |
| 5 | time-series smoothing | unambiguous | unambiguous (`alignment`, nulls) | even-window centring | **high** |

Every mocked signature scored unambiguous on call shape. That is a weak result on its own — a
hand-mocked signature is *designed* to be callable, and Pass B cannot fail the way Pass A can. The
finding that carries weight is narrower and more specific: in four of five cases the field doing the
work is an **echoed convention** (`referenceLevels`, `quantileDefinition`, `method`, `alignment`) —
a value that makes explicit a choice the raw library made silently. Those four fields are the
measured deliverable of this eval, and each one traces to a specific Pass-A result.
