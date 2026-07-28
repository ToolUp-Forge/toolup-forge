// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.Tests.InProcess.ReferenceProvider

open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — the in-test reference provider ──────────────────
//
// A deliberately trivial provider used to bind the contract packs and
// to stand in for the "a consumer registers a provider and lists
// algorithms" acceptance path. It uses NO numerics library — the
// arithmetic here is the minimum needed to return a well-formed,
// deterministic result, and is not a statistical implementation.
//
// It is honest about its conventions rather than accurate: the quantile
// estimator is nearest-rank, so it satisfies the *contract* (honour the
// requested convention field, echo it back) while making no claim to
// match R-7 or R-8 numerically. The contract packs test the contract;
// numerical accuracy is a provider companion's own test surface.

let private mean (xs: float[]) = Array.sum xs / float xs.Length

let private sampleVariance (xs: float[]) =
    if xs.Length < 2 then
        0.0
    else
        let m = mean xs
        (xs |> Array.sumBy (fun x -> (x - m) * (x - m))) / float (xs.Length - 1)

/// Nearest-rank quantile. Deterministic and convention-agnostic — see
/// the module header.
let private quantileAt (sorted: float[]) (p: float) =
    let idx = int (ceil (p * float sorted.Length)) - 1
    sorted[max 0 (min (sorted.Length - 1) idx)]

type ReferenceRegressionFitter() =
    interface IRegressionFitter with
        member _.FitLinear request = async {
            // Bivariate OLS on the first numeric predictor; every other
            // term is reported at zero. Enough to exercise the shape.
            let y = request.Response
            let n = float y.Length

            let coefficients, intercept =
                match request.Numeric with
                | first :: _ ->
                    let x = first.Values
                    let mx, my = mean x, mean y

                    let sxx = x |> Array.sumBy (fun v -> (v - mx) * (v - mx))

                    let sxy = Array.map2 (fun a b -> (a - mx) * (b - my)) x y |> Array.sum
                    let slope = if sxx = 0.0 then 0.0 else sxy / sxx
                    [ { Term = first.Name; Estimate = slope } ], (if request.Intercept then my - slope * mx else 0.0)
                | [] -> [], (if request.Intercept then mean y else 0.0)

            // Categorical contrasts: reported as zero-estimate terms
            // against the first-sorted level, which is the reference this
            // provider declares.
            let referenceLevels =
                request.Categorical
                |> List.choose (fun c ->
                    c.Values
                    |> Array.distinct
                    |> Array.sort
                    |> Array.tryHead
                    |> Option.map (fun level -> { Factor = c.Name; Level = level }))

            let categoricalTerms =
                request.Categorical
                |> List.collect (fun c ->
                    let levels = c.Values |> Array.distinct |> Array.sort |> Array.toList

                    levels
                    |> List.skip (min 1 (List.length levels))
                    |> List.map (fun level -> {
                        Term = sprintf "%s=%s" c.Name level
                        Estimate = 0.0
                    }))

            return
                Ok {
                    Coefficients = coefficients @ categoricalTerms
                    Intercept = intercept
                    RSquared = 1.0
                    AdjustedRSquared = 1.0
                    ResidualStandardError = 0.0
                    Observations = int n
                    ReferenceLevels = referenceLevels
                }
        }

type ReferenceDescriptiveStats() =
    interface IDescriptiveStats with
        member _.Summarise request = async {
            let sorted = Array.sort request.Values
            let v = sampleVariance request.Values

            return
                Ok {
                    Count = request.Values.Length
                    Mean = mean request.Values
                    Median = quantileAt sorted 0.5
                    StandardDeviation = sqrt v
                    Variance = v
                    Minimum = sorted[0]
                    Maximum = sorted[sorted.Length - 1]
                    Skewness = 0.0
                    Kurtosis = 0.0
                    Quantiles =
                        request.Quantiles
                        |> Array.toList
                        |> List.map (fun p -> {
                            Probability = p
                            Value = quantileAt sorted p
                        })
                    // The contract: echo what was requested.
                    Convention = request.Convention
                }
        }

type ReferenceDistributionFitter() =
    interface IDistributionFitter with
        member _.Fit request = async {
            match request.Family with
            | NormalFamily ->
                let m = mean request.Values
                let v = sampleVariance request.Values
                // Deterministic placeholder likelihood — the shape is
                // what the contract packs read, not the value.
                let logLikelihood = -float request.Values.Length
                let method' = request.Method |> Option.defaultValue MaximumLikelihood

                return
                    Ok {
                        Family = NormalFamily
                        Method = method'
                        Parameters = [ { Name = "mu"; Value = m }; { Name = "sigma"; Value = sqrt v } ]
                        LogLikelihood = logLikelihood
                        Aic = DistributionFitResult.aicOf 2 logLikelihood
                        Bic = DistributionFitResult.bicOf 2 request.Values.Length logLikelihood
                        Observations = request.Values.Length
                    }
            | other ->
                // The contract: refuse as typed data naming the family,
                // never substitute a family we can fit.
                return
                    Error(
                        AlgorithmError.Unsupported(
                            "distribution.fit",
                            "reference",
                            sprintf
                                "family '%s' has no estimator in the reference provider"
                                (DistributionFamily.name other)
                        )
                    )
        }

type ReferenceTimeSeriesFilter() =
    interface ITimeSeriesFilter with
        member _.Smooth request = async {
            let xs = request.Values
            let n = xs.Length

            let windowMean (lo: int) (hi: int) =
                let lo, hi = max 0 lo, min (n - 1) hi
                let slice = xs[lo..hi]
                Array.sum slice / float slice.Length

            let values =
                match request.Kind with
                | TrailingMean ->
                    Array.init n (fun i ->
                        if i + 1 >= request.Window then
                            Some(windowMean (i - request.Window + 1) i)
                        elif request.WarmUp = PartialWindow then
                            Some(windowMean 0 i)
                        else
                            None)
                | CentredMean ->
                    let half = request.Window / 2

                    Array.init n (fun i ->
                        if i - half >= 0 && i + half <= n - 1 then
                            Some(windowMean (i - half) (i + half))
                        elif request.WarmUp = PartialWindow then
                            Some(windowMean (i - half) (i + half))
                        else
                            None)
                | ExponentiallyWeighted ->
                    let alpha = request.Alpha |> Option.defaultValue 0.3
                    let mutable prev = xs[0]

                    Array.init n (fun i ->
                        prev <-
                            if i = 0 then
                                xs[0]
                            else
                                alpha * xs[i] + (1.0 - alpha) * prev

                        Some prev)

            return
                Ok {
                    Values = values
                    Kind = request.Kind
                    Window =
                        if SmoothingKind.usesWindow request.Kind then
                            request.Window
                        else
                            0
                    // The contract: alignment is derived from the kind and
                    // reported, never left to the caller to infer.
                    Alignment = SmoothingAlignment.ofKind request.Kind
                    WarmUp = request.WarmUp
                }
        }

/// The algorithm ids this provider declares.
module Ids =
    [<Literal>]
    let Regression = "regression.linear"

    [<Literal>]
    let Describe = "stats.describe"

    [<Literal>]
    let DistributionFit = "distribution.fit"

    [<Literal>]
    let Smooth = "timeseries.smooth"

let private declare (id: AlgorithmId) (displayName: string) (kind: AlgorithmKind) (description: string) =
    AlgorithmInfo.declare
        id
        displayName
        kind
        description
        (AlgorithmParameters.forKind kind)
        (AlgorithmParameters.returnsFor kind)
        "Deterministic for a given request. Reference implementation — shape-correct, not numerically authoritative."

/// Declarations for every algorithm the reference provider backs.
let declarations = [
    declare Ids.Regression "Linear regression" Regression "Fit a linear model by ordinary least squares."
    declare Ids.Describe "Descriptive statistics" DescriptiveStatistics "Summarise a numeric sample."
    declare Ids.DistributionFit "Distribution fit" DistributionFit "Fit a parametric distribution to a sample."
    declare Ids.Smooth "Time-series smoothing" TimeSeriesSmoothing "Smooth a series."
]

/// The full reference provider — all four fitters.
let provider: IAlgorithmProvider =
    AlgorithmProviderParts.create "reference" "0.0.1-test"
    |> AlgorithmProviderParts.withAlgorithms declarations
    |> AlgorithmProviderParts.withRegression (ReferenceRegressionFitter())
    |> AlgorithmProviderParts.withDescriptive (ReferenceDescriptiveStats())
    |> AlgorithmProviderParts.withDistribution (ReferenceDistributionFitter())
    |> AlgorithmProviderParts.withTimeSeries (ReferenceTimeSeriesFilter())
    |> AlgorithmProvider.create

/// A provider that DECLARES the descriptive algorithm but supplies no
/// fitter for it — the "declared but unimplemented" path.
let hollowProvider: IAlgorithmProvider =
    AlgorithmProviderParts.create "hollow" "0.0.1-test"
    |> AlgorithmProviderParts.withAlgorithms [
        declare Ids.Describe "Descriptive statistics" DescriptiveStatistics "Declared but not implemented."
    ]
    |> AlgorithmProvider.create

/// A provider whose fitter raises — the escaped-exception path.
let throwingProvider: IAlgorithmProvider =
    let fitter =
        { new IDescriptiveStats with
            member _.Summarise _ = async { return failwith "reference explosion" }
        }

    AlgorithmProviderParts.create "throwing" "0.0.1-test"
    |> AlgorithmProviderParts.withAlgorithms [
        declare Ids.Describe "Descriptive statistics" DescriptiveStatistics "Raises."
    ]
    |> AlgorithmProviderParts.withDescriptive fitter
    |> AlgorithmProvider.create

/// A second provider declaring one id the reference provider already
/// declares — the duplicate-registration path.
let clashingProvider: IAlgorithmProvider =
    AlgorithmProviderParts.create "clashing" "0.0.1-test"
    |> AlgorithmProviderParts.withAlgorithms [
        declare Ids.Describe "Descriptive statistics" DescriptiveStatistics "A second claim on the same id."
    ]
    |> AlgorithmProviderParts.withDescriptive (ReferenceDescriptiveStats())
    |> AlgorithmProvider.create

// ─── Sample invocations, shared across the packs ────────────────────

let sampleRegression =
    FitRegression {
        Response = [| 2.0; 4.0; 6.0; 8.0 |]
        Numeric = [
            {
                Name = "spend"
                Values = [| 1.0; 2.0; 3.0; 4.0 |]
            }
        ]
        Categorical = [
            {
                Name = "region"
                Values = [| "North"; "North"; "South"; "South" |]
            }
        ]
        Intercept = true
    }

let sampleDescriptive =
    SummariseDescriptive(DescriptiveRequest.create [| 3.0; 1.0; 4.0; 1.0; 5.0; 9.0; 2.0; 6.0 |])

let sampleDistribution =
    FitDistribution(DistributionFitRequest.create [| 1.0; 2.0; 3.0; 4.0; 5.0 |] NormalFamily)

let sampleSmoothing =
    SmoothSeries(SmoothingRequest.mean [| 1.0; 2.0; 3.0; 4.0; 5.0; 6.0 |] CentredMean 3)

/// Every `(algorithmId, invocation)` pair the reference provider serves.
let sampleInvocations = [
    Ids.Regression, sampleRegression
    Ids.Describe, sampleDescriptive
    Ids.DistributionFit, sampleDistribution
    Ids.Smooth, sampleSmoothing
]