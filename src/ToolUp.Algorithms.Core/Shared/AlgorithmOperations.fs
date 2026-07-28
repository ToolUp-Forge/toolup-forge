// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmOperations

open ToolUp.Algorithms.AlgorithmTypes

// ─── Phase 11.E.2 — the four curated operation contracts ────────────
//
// Request / result records for the four operations the pre-build eval
// selected (`evals/algorithms-primitives-eval/findings.md`), plus the
// `AlgorithmInvocation` / `AlgorithmOutcome` DUs the dispatcher routes
// on.
//
// **The echoed-convention rule.** Wherever the raw numerics path made a
// choice silently, this tier makes it a field of the request AND echoes
// it on the result. Four such fields, one per measured divergence:
//
//   * `DescriptiveRequest.Convention`  — R-7 vs R-8 quantiles. Measured
//     4% divergence against every spreadsheet on a 9-point sample, with
//     nothing in the raw code recording which convention ran.
//   * `SmoothingRequest.Kind`          — trailing vs centred alignment.
//     The raw library's only smoother is trailing; asked for centred it
//     returns the right numbers one period late, and the result plots
//     correctly.
//   * `DistributionFitRequest.Method`  — MLE vs method-of-moments. The
//     raw library ships a fitter for four distributions and none for
//     the count families, so the estimator is hand-written and its
//     provenance was invisible in the output.
//   * `RegressionFitResult.ReferenceLevels` — which categorical level
//     became the contrast base. Raw output is an unlabelled coefficient
//     vector.
//
// A provider that reports one of these values is making a claim the
// contract packs check (see `src/ToolUp.Algorithms.Tests/`).
//
// **Fable-safe.** Packed under `fable/`; primitives, records, DUs and
// options only.
//
// **Identity by value (GP 12 rule 1).** Every request and result is a
// value record over primitives and arrays. No live handle, no stream,
// no store reference crosses this boundary, so a distributed provider
// implementation is a drop-in.

// ═══ Regression ═════════════════════════════════════════════════════

/// One numeric predictor column. `Name` is what the fitted coefficient
/// is labelled with, which is the difference between a readable result
/// and three anonymous numbers.
type NumericPredictor = { Name: string; Values: float[] }

/// One categorical predictor column, supplied as **raw labels**. Dummy
/// coding, reference-level selection and the intercept column all sit
/// on the provider's side of this boundary — which is what removes the
/// dummy-variable trap and the missing-intercept slip from the call
/// site entirely.
type CategoricalPredictor = { Name: string; Values: string[] }

/// A linear least-squares fit request. A bivariate fit is the
/// degenerate case: one numeric predictor, no categoricals.
type RegressionRequest = {
    /// The response (dependent) variable, one entry per observation.
    Response: float[]
    /// Numeric predictor columns. May be empty when every predictor is
    /// categorical.
    Numeric: NumericPredictor list
    /// Categorical predictor columns, as raw labels.
    Categorical: CategoricalPredictor list
    /// Include an intercept term. `true` for essentially every applied
    /// fit; `false` forces the line through the origin.
    Intercept: bool
}

/// One fitted coefficient. `Term` is the numeric predictor's `Name`, or
/// `"{factor}={level}"` for a categorical contrast — so a coefficient
/// is interpretable without reconstructing the encoding.
type RegressionTerm = { Term: string; Estimate: float }

/// The level of a categorical factor that became the contrast base.
/// Every non-reference level of that factor appears in `Coefficients`
/// as a contrast against this one.
type ReferenceLevel = { Factor: string; Level: string }

/// A fitted linear model.
type RegressionFitResult = {
    /// Fitted coefficients excluding the intercept, in predictor
    /// declaration order (numeric first, then categorical contrasts).
    Coefficients: RegressionTerm list
    /// Fitted intercept. `0.0` when the request set `Intercept = false`.
    Intercept: float
    /// Coefficient of determination.
    RSquared: float
    /// R² adjusted for the number of fitted terms.
    AdjustedRSquared: float
    /// Residual standard error — the scale of the residuals in the
    /// response's own units.
    ResidualStandardError: float
    /// Number of observations the fit consumed.
    Observations: int
    /// One entry per categorical factor. **Load-bearing** — without it
    /// a contrast coefficient is uninterpretable.
    ReferenceLevels: ReferenceLevel list
}

// ═══ Descriptive statistics ═════════════════════════════════════════

/// Which quantile definition to use. The measured divergence this
/// entire companion exists to close: the two conventions disagree by
/// several percent on small samples, both are defensible, and the raw
/// path never says which one it ran.
type QuantileConvention =
    /// R-7 — linear interpolation of the order statistics. What Excel
    /// `PERCENTILE`, `numpy.percentile` and `pandas.quantile` compute by
    /// default. **The SDK default**, because it is the number the
    /// caller is checking against.
    | ExcelCompatible
    /// R-8 — the median-unbiased definition. Preferred in some
    /// statistical work, and the default of several numerics libraries
    /// (including MathNet's `Quantile` *and* its `Percentile`).
    | MedianUnbiased

module QuantileConvention =

    /// Stable wire string.
    let name =
        function
        | ExcelCompatible -> "excelCompatible"
        | MedianUnbiased -> "medianUnbiased"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "excelCompatible" -> Some ExcelCompatible
        | "medianUnbiased" -> Some MedianUnbiased
        | _ -> None

/// A univariate descriptive-summary request.
type DescriptiveRequest = {
    /// The sample. Missing observations are the caller's to exclude
    /// before the call — the catalog summarises what it is given.
    Values: float[]
    /// Probabilities in `[0, 1]` to estimate. Empty is legal and
    /// produces an empty `Quantiles` list.
    Quantiles: float[]
    /// Which definition the quantile estimates use. Echoed on the
    /// result.
    Convention: QuantileConvention
}

module DescriptiveRequest =

    /// The conventional default probabilities: quartiles.
    let defaultQuantiles = [| 0.25; 0.5; 0.75 |]

    /// A request over `values` with quartiles and the Excel-compatible
    /// (R-7) convention — the shape that matches what a caller
    /// comparing against a spreadsheet expects.
    let create (values: float[]) : DescriptiveRequest = {
        Values = values
        Quantiles = defaultQuantiles
        Convention = ExcelCompatible
    }

/// One estimated quantile.
type QuantileEstimate = { Probability: float; Value: float }

/// A univariate descriptive summary. Dispersion statistics are the
/// **sample** (n − 1) forms throughout.
type DescriptiveSummary = {
    Count: int
    Mean: float
    Median: float
    /// Sample standard deviation (n − 1 denominator).
    StandardDeviation: float
    /// Sample variance (n − 1 denominator).
    Variance: float
    Minimum: float
    Maximum: float
    Skewness: float
    Kurtosis: float
    /// One entry per requested probability, in request order.
    Quantiles: QuantileEstimate list
    /// Echoed from the request. **Load-bearing** — a quantile without
    /// its convention is not a reproducible number.
    Convention: QuantileConvention
}

// ═══ Distribution fitting ═══════════════════════════════════════════

/// Parametric family to fit. A provider declares which families it can
/// serve; one it cannot returns `AlgorithmError.Unsupported` naming the
/// family, never a silent substitution.
type DistributionFamily =
    | NormalFamily
    | LogNormalFamily
    | PoissonFamily
    | NegativeBinomialFamily
    | GammaFamily

module DistributionFamily =

    /// Stable wire string.
    let name =
        function
        | NormalFamily -> "normal"
        | LogNormalFamily -> "logNormal"
        | PoissonFamily -> "poisson"
        | NegativeBinomialFamily -> "negativeBinomial"
        | GammaFamily -> "gamma"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "normal" -> Some NormalFamily
        | "logNormal" -> Some LogNormalFamily
        | "poisson" -> Some PoissonFamily
        | "negativeBinomial" -> Some NegativeBinomialFamily
        | "gamma" -> Some GammaFamily
        | _ -> None

    /// Every family, in declaration order.
    let all = [
        NormalFamily
        LogNormalFamily
        PoissonFamily
        NegativeBinomialFamily
        GammaFamily
    ]

    /// `true` for families supported on integer counts, whose
    /// likelihood is a probability MASS, not a density. A log-likelihood
    /// computed with the wrong one is a plausible number that cannot be
    /// compared against the other family — which is exactly the mistake
    /// the raw path invites.
    let isDiscrete =
        function
        | PoissonFamily
        | NegativeBinomialFamily -> true
        | NormalFamily
        | LogNormalFamily
        | GammaFamily -> false

/// How the parameters were estimated.
type EstimationMethod =
    | MaximumLikelihood
    | MethodOfMoments

module EstimationMethod =

    /// Stable wire string.
    let name =
        function
        | MaximumLikelihood -> "maximumLikelihood"
        | MethodOfMoments -> "methodOfMoments"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "maximumLikelihood" -> Some MaximumLikelihood
        | "methodOfMoments" -> Some MethodOfMoments
        | _ -> None

/// A distribution-fit request.
type DistributionFitRequest = {
    Values: float[]
    Family: DistributionFamily
    /// `None` lets the provider pick its best available estimator for
    /// the family — which it must then report on the result. A caller
    /// that needs a specific estimator names it, and a provider that
    /// cannot honour the named method returns `Unsupported` rather than
    /// quietly using the other one.
    Method: EstimationMethod option
}

module DistributionFitRequest =

    /// A request over `values` for `family`, letting the provider pick
    /// its estimator.
    let create (values: float[]) (family: DistributionFamily) : DistributionFitRequest = {
        Values = values
        Family = family
        Method = None
    }

/// One fitted distribution parameter. Names are family-specific and the
/// algorithm's `Description` states the parameterisation — `p` as a
/// success probability rather than a failure rate, and so on.
type DistributionParameter = { Name: string; Value: float }

/// A fitted distribution, with the comparison statistics attached so a
/// caller never hand-picks between a probability mass and a density.
type DistributionFitResult = {
    Family: DistributionFamily
    /// The estimator that actually ran. **Load-bearing** when the
    /// request left `Method = None`.
    Method: EstimationMethod
    Parameters: DistributionParameter list
    /// Log-likelihood of the sample under the fitted parameters,
    /// evaluated as a mass for discrete families and a density for
    /// continuous ones.
    LogLikelihood: float
    /// Akaike information criterion — `2k − 2·logLik`.
    Aic: float
    /// Bayesian information criterion — `k·ln(n) − 2·logLik`.
    Bic: float
    Observations: int
}

module DistributionFitResult =

    /// AIC from a log-likelihood and a parameter count.
    let aicOf (parameterCount: int) (logLikelihood: float) : float =
        2.0 * float parameterCount - 2.0 * logLikelihood

    /// BIC from a log-likelihood, a parameter count and a sample size.
    /// `n <= 0` yields `nan` rather than a domain error on `log`.
    let bicOf (parameterCount: int) (observations: int) (logLikelihood: float) : float =
        if observations <= 0 then
            nan
        else
            float parameterCount * log (float observations) - 2.0 * logLikelihood

// ═══ Time-series smoothing ══════════════════════════════════════════

/// Which smoother to apply. Trailing and centred are separate values
/// rather than a flag on one "moving average" so the alignment decision
/// cannot be skipped — the measured failure was a caller asking for
/// centred, writing the obvious call, and getting trailing.
type SmoothingKind =
    /// Mean of the window ENDING at each period. `Window` applies.
    | TrailingMean
    /// Mean of the window CENTRED on each period. `Window` applies and
    /// should be odd; an even window has no exact centre, and the
    /// provider's `PrecisionContract` states its tie-break.
    | CentredMean
    /// Exponentially weighted mean. `Alpha` applies; `Window` is
    /// ignored.
    | ExponentiallyWeighted

module SmoothingKind =

    /// Stable wire string.
    let name =
        function
        | TrailingMean -> "trailingMean"
        | CentredMean -> "centredMean"
        | ExponentiallyWeighted -> "exponentiallyWeighted"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "trailingMean" -> Some TrailingMean
        | "centredMean" -> Some CentredMean
        | "exponentiallyWeighted" -> Some ExponentiallyWeighted
        | _ -> None

    /// `true` when the kind reads `Window`; `false` when it reads
    /// `Alpha`.
    let usesWindow =
        function
        | TrailingMean
        | CentredMean -> true
        | ExponentiallyWeighted -> false

/// Where a smoothed value sits relative to the window that produced it.
/// Derived from `SmoothingKind` and echoed on the result so a
/// downstream consumer can assert on it.
type SmoothingAlignment =
    | TrailingAligned
    | CentredAligned

module SmoothingAlignment =

    /// Stable wire string.
    let name =
        function
        | TrailingAligned -> "trailing"
        | CentredAligned -> "centred"

    /// The alignment a kind produces. Exponential weighting is trailing
    /// by construction.
    let ofKind =
        function
        | TrailingMean -> TrailingAligned
        | CentredMean -> CentredAligned
        | ExponentiallyWeighted -> TrailingAligned

/// What to emit for periods where the window is not yet full.
type WarmUpPolicy =
    /// Average over whatever part of the window exists. Convenient, and
    /// the reason a raw-library warm-up reads as a genuine dip: a
    /// one-element "mean" sits beside a three-element one.
    | PartialWindow
    /// Emit `None`. **The SDK default** — an undefined value is data,
    /// and a consumer that must handle it cannot fail to notice it.
    | UndefinedWarmUp

module WarmUpPolicy =

    /// Stable wire string.
    let name =
        function
        | PartialWindow -> "partialWindow"
        | UndefinedWarmUp -> "undefined"

    /// Inverse of `name`. `None` for an unknown tag.
    let parse =
        function
        | "partialWindow" -> Some PartialWindow
        | "undefined" -> Some UndefinedWarmUp
        | _ -> None

/// A smoothing request.
type SmoothingRequest = {
    Values: float[]
    Kind: SmoothingKind
    /// Window length. Read by `TrailingMean` / `CentredMean`; ignored by
    /// `ExponentiallyWeighted`.
    Window: int
    /// Smoothing factor in `(0, 1]`. Read by `ExponentiallyWeighted`;
    /// ignored by the mean kinds.
    Alpha: float option
    WarmUp: WarmUpPolicy
}

module SmoothingRequest =

    /// A windowed-mean request with the undefined warm-up default.
    let mean (values: float[]) (kind: SmoothingKind) (window: int) : SmoothingRequest = {
        Values = values
        Kind = kind
        Window = window
        Alpha = None
        WarmUp = UndefinedWarmUp
    }

    /// An exponentially-weighted request.
    let exponential (values: float[]) (alpha: float) : SmoothingRequest = {
        Values = values
        Kind = ExponentiallyWeighted
        Window = 0
        Alpha = Some alpha
        WarmUp = UndefinedWarmUp
    }

/// A smoothed series, same length as the input.
type SmoothingResult = {
    /// One entry per input period. `None` where the window was not full
    /// and `WarmUp = UndefinedWarmUp` — never `nan`, which a downstream
    /// mean would silently propagate.
    Values: float option[]
    Kind: SmoothingKind
    /// Echoed from the request (`0` for exponential weighting).
    Window: int
    /// **Load-bearing.** The off-by-one that survives every eyeball
    /// check is exactly the one this field makes assertable.
    Alignment: SmoothingAlignment
    WarmUp: WarmUpPolicy
}

// ═══ Dispatch shapes ════════════════════════════════════════════════

/// A typed request to one of the four curated operations. The
/// dispatcher routes on the pair `(algorithmId, invocation)`: the id
/// selects the provider, the invocation case selects the fitter.
///
/// A DU rather than a JSON blob so the wire between the dispatcher and
/// a provider stays type-checked — the AI tool surface is the only
/// place stringly-typed arguments exist, and it parses into these
/// records at the edge.
type AlgorithmInvocation =
    | FitRegression of RegressionRequest
    | SummariseDescriptive of DescriptiveRequest
    | FitDistribution of DistributionFitRequest
    | SmoothSeries of SmoothingRequest

module AlgorithmInvocation =

    /// The kind an invocation belongs to. A provider whose declared
    /// algorithm's `Kind` disagrees with the supplied invocation is a
    /// wiring defect, surfaced as `AlgorithmError.KindMismatch`.
    let kind =
        function
        | FitRegression _ -> Regression
        | SummariseDescriptive _ -> DescriptiveStatistics
        | FitDistribution _ -> DistributionFit
        | SmoothSeries _ -> TimeSeriesSmoothing

/// The typed result of a successful invocation, in the same one-case-
/// per-operation shape.
type AlgorithmOutcome =
    | RegressionOutcome of RegressionFitResult
    | DescriptiveOutcome of DescriptiveSummary
    | DistributionOutcome of DistributionFitResult
    | SmoothingOutcome of SmoothingResult

module AlgorithmOutcome =

    /// The kind an outcome belongs to.
    let kind =
        function
        | RegressionOutcome _ -> Regression
        | DescriptiveOutcome _ -> DescriptiveStatistics
        | DistributionOutcome _ -> DistributionFit
        | SmoothingOutcome _ -> TimeSeriesSmoothing

// ═══ Request validation ═════════════════════════════════════════════
//
// Pure, Fable-safe, shared by the dispatcher (which runs it before
// reaching a provider) and by providers (which may run it again). The
// point is that a malformed request produces a specific diagnostic
// naming the offending field, rather than an index-out-of-range from
// deep inside a numerics library.

module AlgorithmValidation =

    let private err (id: AlgorithmId) (detail: string) =
        Error(AlgorithmError.InvalidArguments(id, detail))

    /// Validate a regression request: a non-empty response, at least one
    /// predictor, every predictor column the same length as the
    /// response, and enough observations to fit the terms.
    let regression (id: AlgorithmId) (r: RegressionRequest) : Result<unit, AlgorithmError> =
        let n = r.Response.Length

        if n = 0 then
            err id "response is empty"
        elif List.isEmpty r.Numeric && List.isEmpty r.Categorical then
            err id "no predictors supplied — a fit needs at least one numeric or categorical predictor"
        else
            let badNumeric = r.Numeric |> List.tryFind (fun p -> p.Values.Length <> n)

            let badCategorical = r.Categorical |> List.tryFind (fun p -> p.Values.Length <> n)

            match badNumeric, badCategorical with
            | Some p, _ ->
                err id (sprintf "numeric predictor '%s' has %d values but the response has %d" p.Name p.Values.Length n)
            | _, Some p ->
                err
                    id
                    (sprintf "categorical predictor '%s' has %d values but the response has %d" p.Name p.Values.Length n)
            | None, None ->
                // Terms: one per numeric column, plus (levels - 1) per
                // categorical column, plus the intercept.
                let categoricalTerms =
                    r.Categorical
                    |> List.sumBy (fun p ->
                        let levels = p.Values |> Array.distinct |> Array.length
                        max 0 (levels - 1))

                let terms =
                    List.length r.Numeric + categoricalTerms + (if r.Intercept then 1 else 0)

                if n < terms then
                    err id (sprintf "%d observations cannot fit %d terms" n terms)
                else
                    Ok()

    /// Validate a descriptive request: a non-empty sample and
    /// probabilities within `[0, 1]`.
    let descriptive (id: AlgorithmId) (r: DescriptiveRequest) : Result<unit, AlgorithmError> =
        if r.Values.Length = 0 then
            err id "sample is empty"
        else
            match
                r.Quantiles
                |> Array.tryFind (fun p -> p < 0.0 || p > 1.0 || System.Double.IsNaN p)
            with
            | Some p -> err id (sprintf "quantile probability %g is outside [0, 1]" p)
            | None -> Ok()

    /// Validate a distribution-fit request: a non-empty sample, and
    /// non-negative integral values for a discrete family.
    let distribution (id: AlgorithmId) (r: DistributionFitRequest) : Result<unit, AlgorithmError> =
        if r.Values.Length = 0 then
            err id "sample is empty"
        elif DistributionFamily.isDiscrete r.Family then
            match r.Values |> Array.tryFind (fun v -> v < 0.0 || v <> floor v) with
            | Some v ->
                err
                    id
                    (sprintf
                        "family '%s' is discrete but the sample contains %g — counts must be non-negative integers"
                        (DistributionFamily.name r.Family)
                        v)
            | None -> Ok()
        else
            Ok()

    /// Validate a smoothing request: a non-empty series, a positive
    /// window no longer than the series for the mean kinds, and an
    /// alpha in `(0, 1]` for exponential weighting.
    let smoothing (id: AlgorithmId) (r: SmoothingRequest) : Result<unit, AlgorithmError> =
        if r.Values.Length = 0 then
            err id "series is empty"
        elif SmoothingKind.usesWindow r.Kind then
            if r.Window < 1 then
                err id (sprintf "window must be at least 1, got %d" r.Window)
            elif r.Window > r.Values.Length then
                err id (sprintf "window %d exceeds the series length %d" r.Window r.Values.Length)
            else
                Ok()
        else
            match r.Alpha with
            | None -> err id "exponentially-weighted smoothing requires an alpha"
            | Some a when a <= 0.0 || a > 1.0 -> err id (sprintf "alpha must be in (0, 1], got %g" a)
            | Some _ -> Ok()

    /// Validate whichever request an invocation carries.
    let invocation (id: AlgorithmId) (inv: AlgorithmInvocation) : Result<unit, AlgorithmError> =
        match inv with
        | FitRegression r -> regression id r
        | SummariseDescriptive r -> descriptive id r
        | FitDistribution r -> distribution id r
        | SmoothSeries r -> smoothing id r