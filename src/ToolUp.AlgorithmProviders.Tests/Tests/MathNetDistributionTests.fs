// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.MathNetDistributionTests

open Expecto
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures

// ─── Phase 11.E.3 — distribution fitting, pinned ────────────────────
//
// The eval's only row scoring 3 on both axes, and its closing warning:
// a hand-written estimator's PARAMETERISATION "is the least checkable
// code in the whole family" — a wrong one compiles and prints plausibly.
//
// So every family gets a case that pins the parameterisation by NAME and
// by VALUE against arithmetic worked out here, and the two
// moment-only families get a case pinning the refusal — because
// substituting the estimator we have for the one that was asked for is
// the failure mode this seam exists to make impossible.

let private fit (family: DistributionFamily) (method': EstimationMethod option) (values: float[]) =
    FitDistribution {
        Values = values
        Family = family
        Method = method'
    }

/// exp(1..3) — logs are exactly [0; 1; 2; 3], so the log-scale MLE is
/// mean 1.5 and population variance 1.25 by inspection.
let private logNormalSample = [| exp 0.0; exp 1.0; exp 2.0; exp 3.0 |]

let private countSample = [| 1.0; 2.0; 3.0; 4.0 |]

/// mean 2, sum of squared deviations 12 over n - 1 = 3 → sample
/// variance 4, so the negative-binomial moment solution is exactly
/// p = 2/4 = 0.5 and r = 4/(4 - 2) = 2.
let private overdispersedSample = [| 1.0; 1.0; 1.0; 5.0 |]

let private continuousTests =
    testList "continuous families" [

        test "normal MLE uses the n denominator" {
            // describeSample: mean 5, sum of squared deviations 32.
            // MLE sigma^2 = 32 / 8 = 4 → sigma = 2 exactly.
            let result =
                expectDistribution (fit NormalFamily (Some MaximumLikelihood) describeSample)

            Expect.equal result.Family NormalFamily "the family is echoed"
            Expect.equal result.Method MaximumLikelihood "the estimator that ran is reported"
            closeTo 5.0 (parameterNamed "mu" result) "mu is the sample mean"
            closeTo 2.0 (parameterNamed "sigma" result) "the MLE divides by n, giving sigma = 2"
            Expect.equal result.Observations 8 "observations"
        }

        test "normal method-of-moments uses the n - 1 denominator, and differs" {
            let result =
                expectDistribution (fit NormalFamily (Some MethodOfMoments) describeSample)

            Expect.equal result.Method MethodOfMoments "the requested estimator is honoured and echoed"
            closeTo 5.0 (parameterNamed "mu" result) "mu is unchanged"
            closeTo (sqrt (32.0 / 7.0)) (parameterNamed "sigma" result) "moment matching uses the sample variance"

            Expect.notEqual
                (parameterNamed "sigma" result)
                2.0
                "the two estimators must differ here — if they agree, the method switch is being ignored"
        }

        test "the normal log-likelihood matches the closed form" {
            // -n/2 * ln(2*pi) - n*ln(sigma) - SS/(2*sigma^2), written out
            // rather than read back from the library under test.
            let result =
                expectDistribution (fit NormalFamily (Some MaximumLikelihood) describeSample)

            let n = float describeSample.Length

            let expected =
                -(n / 2.0) * log (2.0 * System.Math.PI) - n * log 2.0 - 32.0 / (2.0 * 4.0)

            closeTo expected result.LogLikelihood "log-likelihood is a DENSITY for a continuous family"
            closeTo (2.0 * 2.0 - 2.0 * expected) result.Aic "AIC = 2k - 2*logLik with k = 2"
            closeTo (2.0 * log n - 2.0 * expected) result.Bic "BIC = k*ln(n) - 2*logLik with k = 2"
        }

        test "log-normal MLE fits on the log scale" {
            // logs are [0; 1; 2; 3]: mean 1.5, population variance 1.25.
            let result =
                expectDistribution (fit LogNormalFamily (Some MaximumLikelihood) logNormalSample)

            closeTo 1.5 (parameterNamed "mu" result) "mu is the mean of the LOGS, not of the values"
            closeTo (sqrt 1.25) (parameterNamed "sigma" result) "sigma is the log-scale MLE"
        }

        test "log-normal method-of-moments solves the raw-scale moments instead" {
            let result =
                expectDistribution (fit LogNormalFamily (Some MethodOfMoments) logNormalSample)

            let mu = parameterNamed "mu" result
            let sigma = parameterNamed "sigma" result

            // The defining identities: E[X] = exp(mu + sigma^2/2) and
            // Var[X] = (exp(sigma^2) - 1) * exp(2mu + sigma^2).
            let sampleMean = Array.average logNormalSample

            let sampleVariance =
                (logNormalSample |> Array.sumBy (fun x -> (x - sampleMean) * (x - sampleMean)))
                / float (logNormalSample.Length - 1)

            closeTo sampleMean (exp (mu + sigma * sigma / 2.0)) "the fitted mean matches the sample mean"

            closeTo
                sampleVariance
                ((exp (sigma * sigma) - 1.0) * exp (2.0 * mu + sigma * sigma))
                "the fitted variance matches the SAMPLE (n - 1) variance"

            Expect.notEqual mu 1.5 "moment matching does not reproduce the log-scale MLE"
        }

        test "gamma is method-of-moments with a (shape, RATE) parameterisation" {
            // [1; 2; 3; 4]: mean 2.5, sample variance 5/3.
            // shape = m^2/v = 6.25 / (5/3) = 3.75; rate = m/v = 1.5.
            let result = expectDistribution (fit GammaFamily None countSample)

            Expect.equal result.Method MethodOfMoments "gamma defaults to method of moments — and says so"
            closeTo 3.75 (parameterNamed "shape" result) "shape = mean^2 / variance"
            closeTo 1.5 (parameterNamed "rate" result) "RATE, not scale — the two are reciprocals"

            closeTo
                (parameterNamed "shape" result / parameterNamed "rate" result)
                2.5
                "shape / rate is the fitted mean, which pins rate as a rate"
        }
    ]

let private discreteTests =
    testList "discrete families" [

        test "poisson lambda is the sample mean under either estimator" {
            for method' in [ MaximumLikelihood; MethodOfMoments ] do
                let result = expectDistribution (fit PoissonFamily (Some method') countSample)

                closeTo 2.5 (parameterNamed "lambda" result) "lambda = xbar"

                Expect.equal
                    result.Method
                    method'
                    "the mean is the only moment, so the two estimators coincide — each is still reported as asked"
        }

        test "the poisson log-likelihood is a MASS, matching the closed form" {
            let result = expectDistribution (fit PoissonFamily None countSample)
            let lambda = 2.5

            let logFactorial k =
                [ 1..k ] |> List.sumBy (fun i -> log (float i))

            let expected =
                countSample
                |> Array.sumBy (fun x -> x * log lambda - lambda - logFactorial (int x))

            closeTo expected result.LogLikelihood "sum of log PMF, not of a density"
            closeTo (2.0 * 1.0 - 2.0 * expected) result.Aic "one parameter"
        }

        test "negative binomial moment-matches to Math.NET's (r, p) parameterisation" {
            // mean 2, sample variance 4 → p = m/v = 0.5,
            // r = m^2/(v - m) = 4/2 = 2.
            let result =
                expectDistribution (fit NegativeBinomialFamily None overdispersedSample)

            Expect.equal result.Method MethodOfMoments "the only estimator this provider ships for the family"
            closeTo 2.0 (parameterNamed "r" result) "r = mean^2 / (variance - mean)"
            closeTo 0.5 (parameterNamed "p" result) "p is the SUCCESS probability, so p = mean / variance"

            let r = parameterNamed "r" result
            let p = parameterNamed "p" result

            closeTo 2.0 (r * (1.0 - p) / p) "the fitted mean r(1 - p)/p reproduces the sample mean"
            closeTo 4.0 (r * (1.0 - p) / (p * p)) "the fitted variance r(1 - p)/p^2 reproduces the sample variance"
        }

        test "an equidispersed count sample is refused, not fitted to a negative shape" {
            // [1; 2; 3; 4]: mean 2.5, variance 5/3 < mean, so
            // r = m^2/(v - m) would be NEGATIVE and the fit meaningless.
            let error =
                expectError MathNetAlgorithmIds.DistributionFit (fit NegativeBinomialFamily None countSample)

            Expect.equal (AlgorithmError.tag error) "invalidArguments" "the sample cannot carry the estimator"

            Expect.stringContains
                (AlgorithmError.describe error)
                "overdispersion"
                "the diagnostic names what the estimator needs"
        }

        test "a non-integer sample is refused for a discrete family" {
            let error =
                expectError MathNetAlgorithmIds.DistributionFit (fit PoissonFamily None [| 1.0; 2.5 |])

            Expect.equal (AlgorithmError.tag error) "invalidArguments" "counts must be non-negative integers"
        }
    ]

let private refusalTests =
    testList "refusals" [

        test "a maximum-likelihood request for a moment-only family is REFUSED, not substituted" {
            for family in [ GammaFamily; NegativeBinomialFamily ] do
                let sample =
                    if family = GammaFamily then
                        countSample
                    else
                        overdispersedSample

                let error =
                    expectError MathNetAlgorithmIds.DistributionFit (fit family (Some MaximumLikelihood) sample)

                Expect.equal
                    (AlgorithmError.tag error)
                    "unsupported"
                    "running the moment estimator under an MLE label is exactly the silent substitution the seam forbids"

                let text = AlgorithmError.describe error

                Expect.stringContains text MathNetAlgorithmSupport.ProviderId "the refusal names the provider"

                Expect.stringContains
                    text
                    (DistributionFamily.name family)
                    "the refusal names the family that cannot be served"
        }

        test "a non-positive value is refused for a strictly-positive family" {
            for family in [ LogNormalFamily; GammaFamily ] do
                let error =
                    expectError MathNetAlgorithmIds.DistributionFit (fit family None [| 1.0; 2.0; 0.0 |])

                Expect.equal (AlgorithmError.tag error) "invalidArguments" "log(0) is not a fit, it is a defect"
        }

        test "an empty sample is refused" {
            let error =
                expectError MathNetAlgorithmIds.DistributionFit (fit NormalFamily None [||])

            Expect.equal (AlgorithmError.tag error) "invalidArguments" "nothing to fit"
        }

        test "every family this provider declares is either served or refused as data — never raises" {
            for family in DistributionFamily.all do
                for method' in [ None; Some MaximumLikelihood; Some MethodOfMoments ] do
                    let sample =
                        if DistributionFamily.isDiscrete family then
                            overdispersedSample
                        else
                            describeSample

                    // The assertion is that this returns at all: an
                    // escaped exception would fail the test here rather
                    // than being wrapped downstream by the dispatcher.
                    match execute MathNetAlgorithmIds.DistributionFit (fit family method' sample) with
                    | Ok _
                    | Error _ -> ()
        }
    ]

let tests =
    testList "Math.NET — distribution fitting" [ continuousTests; discreteTests; refusalTests ]