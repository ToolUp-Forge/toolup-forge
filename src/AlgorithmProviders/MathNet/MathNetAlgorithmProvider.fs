// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.MathNetAlgorithms

open ToolUp.Platform
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmsCompose
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.MathNetAlgorithmSupport

// ─── Phase 11.E.3 — the assembled Math.NET provider ─────────────────
//
// The four fitters, their declarations, and the compose helper a
// deployment reaches for. Nothing above this file in the dependency
// graph names Math.NET (GP 1): a deployment that never composes this
// companion carries no numerics dependency at all (GP 13).
//
// **Declarations carry no provider stamp.** `ProviderId` /
// `ProviderVersion` are written by `AlgorithmProviderRegistry` at
// compose, so the catalog can never report a provenance that disagrees
// with the provider that will actually execute the call — a declaration
// that populated them itself could drift.

// ═══ Precision contracts (GP 12 rule 6) ═════════════════════════════
//
// One per algorithm, stated rather than assumed, and quoted verbatim in
// the catalog so a deployment swapping providers can diff them. Each
// names the Math.NET release it was written against — a precision claim
// that does not name its implementation is not a contract.

let private regressionPrecision =
    sprintf
        "Math.NET Numerics %s `MultipleRegression.QR` — a Householder QR least-squares solve rather than the normal equations, which is the stabler route on an ill-conditioned design. Categorical factors are dummy-coded with the FIRST level in ordinal (sorted) order as the reference, so the contrast base is a property of the data and not of the row order. With intercept = false there is no baseline for a contrast to be relative to, so every level gets its own indicator and `referenceLevels` comes back EMPTY. `rSquared` is centred (1 - SSres/sum((y - ybar)^2)) when an intercept is fitted and UNCENTRED (1 - SSres/sum(y^2)) when it is not — the two are not comparable across that switch. `adjustedRSquared` and `residualStandardError` use the residual degrees of freedom n - k (k counting the intercept) and are NaN when n = k. A rank-deficient design (a duplicated predictor, a collinear dummy set) is NOT detected: the solve returns one of the infinitely many minimisers, or NaN coefficients. Deterministic for a given request; IEEE-754 double throughout."
        VendorVersion

let private descriptivePrecision =
    sprintf
        "Math.NET Numerics %s. Quantiles are computed by `SortedArrayStatistics.QuantileCustom` with an EXPLICIT definition — R-7 for excelCompatible, R-8 for medianUnbiased — never the library's bare `Quantile` or `Percentile`, both of which default to R-8 under names that read as spreadsheet-compatible. `median` is the requested definition evaluated at p = 0.5 (R-7 and R-8 agree there, but deriving it from the same estimator keeps the summary internally consistent). `variance` and `standardDeviation` are the sample (n - 1) forms; `skewness` and `kurtosis` are the unbiased sample estimators and `kurtosis` is EXCESS kurtosis (0 for a normal). Statistics undefined at the sample size return NaN rather than an error: variance and standardDeviation for n < 2, skewness for n < 3, kurtosis for n < 4. Deterministic for a given request; no randomness and no parallel reduction, so repeated calls are bit-identical."
        VendorVersion

let private distributionPrecision =
    sprintf
        "Math.NET Numerics %s supplies the log-densities and log-masses (`Normal.PDFLn`, `LogNormal.PDFLn`, `Gamma.PDFLn`, `Poisson.PMFLn`, `NegativeBinomial.PMFLn`); the ESTIMATORS are this provider's own closed forms, because the library's `Estimate` helpers return the unbiased (n - 1) sample standard deviation, which is neither the maximum-likelihood nor a moment estimate — reporting either `method` over it would be a false claim. maximumLikelihood: normal (mu = xbar, sigma^2 = sum((x - xbar)^2)/n), logNormal (the same on log x), poisson (lambda = xbar). methodOfMoments matches the sample mean and the SAMPLE (n - 1) variance, so it differs from the MLE for normal and logNormal; for poisson the mean is the only moment and the two estimators COINCIDE (both are served and reported as asked — there is no second estimator to substitute). gamma and negativeBinomial are method-of-moments ONLY: a request naming maximumLikelihood for either is refused as `unsupported`, because their MLEs need an iterative digamma root-find this provider does not ship. Parameterisations: normal (mu, sigma); logNormal (mu, sigma on the LOG scale); gamma (shape, RATE — not scale, the two are reciprocals); poisson (lambda); negativeBinomial (r, p) with p the SUCCESS probability, mean r(1 - p)/p and variance r(1 - p)/p^2, matching Math.NET's own parameterisation. logNormal and gamma require strictly positive values; negativeBinomial requires genuine overdispersion (sample variance > mean) and refuses an equidispersed sample rather than fitting a negative shape. `logLikelihood` is a DENSITY for the continuous families and a MASS for the discrete ones, so aic / bic compare only within a class. Deterministic for a given request."
        VendorVersion

let private smoothingPrecision =
    sprintf
        "Math.NET Numerics %s. trailingMean is `Statistics.MovingAverage`, whose warm-up is an EXPANDING partial window — used verbatim when warmUp = partialWindow and masked to null otherwise. centredMean is that same series RE-INDEXED, with explicit null padding at BOTH ends: the value at period i averages [i - window/2, i + (window - 1)/2], so an odd window is symmetric and an EVEN window leans one period backward (the declared tie-break, since an even window has no exact centre). exponentiallyWeighted is s0 = x0, si = alpha*xi + (1 - alpha)*s(i-1) — defined at every period, so no entry is null under either warm-up policy, and the seed is the first observation rather than a mean of any prefix. The output always has the same length as the input. Deterministic; each full window is summed directly rather than updated incrementally, so no drift accumulates along a long series."
        VendorVersion

// ═══ Declarations ═══════════════════════════════════════════════════

let private declare
    (id: AlgorithmId)
    (displayName: string)
    (kind: AlgorithmKind)
    (description: string)
    (precision: string)
    =
    AlgorithmInfo.declare
        id
        displayName
        kind
        description
        (AlgorithmParameters.forKind kind)
        (AlgorithmParameters.returnsFor kind)
        precision

/// The four algorithms this provider backs.
let declarations: AlgorithmInfo list = [
    declare
        MathNetAlgorithmIds.Regression
        "Linear regression"
        Regression
        "Fit a linear model by ordinary least squares over any mix of numeric and categorical predictors. Supply categorical predictors as RAW labels — dummy coding, reference-level selection and the intercept column are handled here, and the level that became each factor's contrast base comes back in `referenceLevels`. A bivariate fit is the degenerate case: one numeric predictor and no categoricals."
        regressionPrecision

    declare
        MathNetAlgorithmIds.Describe
        "Descriptive statistics"
        DescriptiveStatistics
        "Summarise a numeric sample: count, mean, median, sample standard deviation and variance, minimum, maximum, skewness, excess kurtosis, and any requested quantiles. `quantileDefinition` defaults to excelCompatible (R-7 — what Excel PERCENTILE, numpy.percentile and pandas.quantile compute); medianUnbiased (R-8) is available and disagrees by several percent on small samples. Whichever ran is echoed on the response."
        descriptivePrecision

    declare
        MathNetAlgorithmIds.DistributionFit
        "Distribution fit"
        DistributionFit
        "Fit a parametric distribution (normal, logNormal, poisson, negativeBinomial, gamma) to a sample and report its parameters with the log-likelihood, AIC and BIC. `method` selects maximum likelihood or method of moments and is echoed on the response; gamma and negativeBinomial are method-of-moments only and REFUSE a maximum-likelihood request rather than substituting. The parameterisation of each family is stated in the precision contract — gamma is (shape, rate) and negativeBinomial's p is a success probability."
        distributionPrecision

    declare
        MathNetAlgorithmIds.Smooth
        "Time-series smoothing"
        TimeSeriesSmoothing
        "Smooth a series by trailing mean, centred mean, or exponential weighting. Trailing and centred produce the same numbers offset by half a window — an off-by-one that survives every visual check — so the kind is required and the resulting `alignment` is echoed. Periods before the window is full are null by default rather than partial averages, which would read as a real dip in the data."
        smoothingPrecision
]

// ═══ The assembled provider ═════════════════════════════════════════

/// The Math.NET algorithm provider — all four curated fitters.
///
/// A single shared value rather than a factory: every fitter is
/// stateless between invocations (GP 12 rule 4) and holds no
/// configuration, so one instance serves every caller and can be
/// composed into as many pipelines as a process hosts.
let provider: IAlgorithmProvider =
    AlgorithmProviderParts.create ProviderId ProviderVersion
    |> AlgorithmProviderParts.withAlgorithms declarations
    |> AlgorithmProviderParts.withRegression (MathNetRegressionFitter())
    |> AlgorithmProviderParts.withDescriptive (MathNetDescriptiveStats())
    |> AlgorithmProviderParts.withDistribution (MathNetDistributionFitter())
    |> AlgorithmProviderParts.withTimeSeries (MathNetTimeSeriesFilter())
    |> AlgorithmProvider.create

// ═══ Composition ════════════════════════════════════════════════════

/// Register the Math.NET provider on an algorithms pipeline. The shape
/// `AlgorithmsServerApp.withProvider` expects, curried for the
/// `withAlgorithms` configurator:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> AlgorithmsCompose.withAlgorithms MathNetAlgorithms.withMathNetAlgorithmProvider
///     |> ServerApp.run
///
/// A deployment registering several providers chains this with its own:
///
///     |> AlgorithmsCompose.withAlgorithms (fun a ->
///         a
///         |> MathNetAlgorithms.withMathNetAlgorithmProvider
///         |> AlgorithmsServerApp.withProvider myOtherProvider)
///
/// Registration is append-only, and the registry REFUSES a second claim
/// on any of the four canonical ids at compose time — two
/// implementations of `regression.linear` differ in convention and
/// precision, which is exactly what the catalog exists to make legible.
let withMathNetAlgorithmProvider (app: AlgorithmsServerApp) : AlgorithmsServerApp =
    AlgorithmsServerApp.withProvider provider app

/// One-call composition for a deployment whose algorithms come entirely
/// from this provider: stacks the algorithms companion onto an existing
/// `ServerApp` pipeline with the Math.NET provider registered and the
/// catalog API plus the AI tool family at their defaults.
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> MathNetAlgorithms.withMathNetAlgorithms
///     |> ServerApp.run
let withMathNetAlgorithms (app: ServerApp) : ServerApp =
    withAlgorithms withMathNetAlgorithmProvider app