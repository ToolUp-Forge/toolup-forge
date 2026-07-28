// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmParameters

open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — canonical parameter specs per kind ──────────────
//
// The declared parameters of an algorithm are not a provider's choice:
// they are the JSON projection of the kind's typed request record, and
// the AI tool executor in `AlgorithmAITools.fs` parses exactly these
// names. Publishing them here — rather than leaving each provider to
// author its own list — makes drift between the advertised schema and
// the parser structurally impossible.
//
// A provider therefore declares an algorithm as
// `AlgorithmInfo.declare id name kind description
//     (AlgorithmParameters.forKind kind) returns precision`
// and spends its declaration effort on the description and the
// precision contract, which are the parts that genuinely differ per
// implementation.
//
// **Fable-safe** (packed under `fable/`): plain data, no reflection.

/// Parameters of a `Regression` algorithm.
let regression: AlgorithmParameterSpec list = [
    AlgorithmParameterSpec.required "response" NumberArrayParameter "The dependent variable, one value per observation."
    AlgorithmParameterSpec.optional
        "numeric"
        ObjectParameter
        "Numeric predictor columns, as an array of { name, values } objects. Each `values` array must be the same length as `response`. The `name` is what the fitted coefficient is labelled with."
        "[]"
    AlgorithmParameterSpec.optional
        "categorical"
        ObjectParameter
        "Categorical predictor columns, as an array of { name, values } objects where `values` holds the RAW string labels (do not encode them yourself). Dummy coding and reference-level selection are handled server-side; the response reports which level became the reference."
        "[]"
    AlgorithmParameterSpec.optional
        "intercept"
        BooleanParameter
        "Fit an intercept term. Leave true unless the relationship is known to pass through the origin."
        "true"
]

/// Parameters of a `DescriptiveStatistics` algorithm.
let descriptiveStatistics: AlgorithmParameterSpec list = [
    AlgorithmParameterSpec.required
        "values"
        NumberArrayParameter
        "The sample. Exclude missing observations before calling — every supplied value is summarised."
    AlgorithmParameterSpec.optional
        "quantiles"
        NumberArrayParameter
        "Probabilities in [0, 1] to estimate. Defaults to the quartiles."
        "[0.25, 0.5, 0.75]"
    AlgorithmParameterSpec.optional
        "quantileDefinition"
        StringParameter
        "Which quantile convention to use: \"excelCompatible\" (R-7 — matches Excel PERCENTILE, numpy.percentile and pandas.quantile) or \"medianUnbiased\" (R-8). These disagree by several percent on small samples, so if the user is comparing against a spreadsheet, keep the default. The convention that ran is echoed on the response."
        "\"excelCompatible\""
]

/// Parameters of a `DistributionFit` algorithm.
let distributionFit: AlgorithmParameterSpec list = [
    AlgorithmParameterSpec.required "values" NumberArrayParameter "The sample to fit."
    AlgorithmParameterSpec.required
        "family"
        StringParameter
        (sprintf
            "Parametric family to fit — one of: %s. The discrete families (poisson, negativeBinomial) require non-negative integer values."
            (DistributionFamily.all |> List.map DistributionFamily.name |> String.concat ", "))
    AlgorithmParameterSpec.optional
        "method"
        StringParameter
        "Estimator to use: \"maximumLikelihood\" or \"methodOfMoments\". Omit to let the provider pick its best available estimator for the family — it reports which one ran either way, and refuses rather than substituting when a named method is unavailable."
        "null"
]

/// Parameters of a `TimeSeriesSmoothing` algorithm.
let timeSeriesSmoothing: AlgorithmParameterSpec list = [
    AlgorithmParameterSpec.required "values" NumberArrayParameter "The series, in time order."
    AlgorithmParameterSpec.required
        "kind"
        StringParameter
        "Smoother to apply: \"trailingMean\" (window ends at each period), \"centredMean\" (window centred on each period), or \"exponentiallyWeighted\". Trailing and centred produce the same numbers offset by half a window — pick deliberately; the alignment is echoed on the response."
    AlgorithmParameterSpec.optional
        "window"
        NumberParameter
        "Window length, for the mean kinds. Prefer an odd window for centredMean. Ignored by exponentiallyWeighted."
        "3"
    AlgorithmParameterSpec.optional
        "alpha"
        NumberParameter
        "Smoothing factor in (0, 1], for exponentiallyWeighted. Ignored by the mean kinds."
        "null"
    AlgorithmParameterSpec.optional
        "warmUp"
        StringParameter
        "What to emit before the window is full: \"undefined\" (null entries — the default, so a partial window is never mistaken for a real dip) or \"partialWindow\" (average whatever part of the window exists)."
        "\"undefined\""
]

/// The canonical parameter list for a kind.
let forKind (kind: AlgorithmKind) : AlgorithmParameterSpec list =
    match kind with
    | Regression -> regression
    | DescriptiveStatistics -> descriptiveStatistics
    | DistributionFit -> distributionFit
    | TimeSeriesSmoothing -> timeSeriesSmoothing

/// The canonical prose gloss on a kind's returned shape. Providers may
/// override it on `AlgorithmInfo.ReturnsDescription`, but the default
/// is accurate for every result record this package defines.
let returnsFor (kind: AlgorithmKind) : string =
    match kind with
    | Regression ->
        "{ coefficients: [{ term, estimate }], intercept, rSquared, adjustedRSquared, residualStandardError, observations, referenceLevels: [{ factor, level }] }. A categorical coefficient's `term` reads \"{factor}={level}\" and is the contrast against that factor's entry in `referenceLevels`."
    | DescriptiveStatistics ->
        "{ count, mean, median, standardDeviation, variance, minimum, maximum, skewness, kurtosis, quantiles: [{ probability, value }], quantileDefinition }. Dispersion statistics are the sample (n-1) forms."
    | DistributionFit ->
        "{ family, method, parameters: [{ name, value }], logLikelihood, aic, bic, observations }. `logLikelihood` is a probability mass for discrete families and a density for continuous ones, so compare only within a class."
    | TimeSeriesSmoothing ->
        "{ values: [number | null], kind, window, alignment, warmUp }. `values` has the same length as the input; entries are null where the window was not full."