// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.MathNetDescriptiveTests

open Expecto
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures

// ─── Phase 11.E.3 — descriptive statistics, pinned ──────────────────
//
// The eval's headline divergence: R-8 substituted for the R-7 the
// caller's spreadsheet computes, ~4% apart, with nothing objecting. The
// cases below pin BOTH conventions against hand-computed order
// statistics, and pin the case where they disagree — because a test
// that only checks the default would pass just as happily if the switch
// were ignored.
//
// The sample is `[2; 4; 4; 4; 5; 5; 7; 9]` throughout (n = 8, already
// sorted, mean 5, sum of squared deviations 32).

let private describe (convention: QuantileConvention) (quantiles: float[]) =
    SummariseDescriptive {
        Values = describeSample
        Quantiles = quantiles
        Convention = convention
    }

let private summaryTests =
    testList "summary statistics" [

        test "count, mean, extrema and the sample (n - 1) dispersion forms" {
            let summary = expectSummary (describe ExcelCompatible [||])

            Expect.equal summary.Count 8 "count is the sample size"
            closeTo 5.0 summary.Mean "mean = 40 / 8"
            closeTo 2.0 summary.Minimum "minimum"
            closeTo 9.0 summary.Maximum "maximum"
            // Sum of squared deviations is 32 over n - 1 = 7.
            closeTo (32.0 / 7.0) summary.Variance "variance is the SAMPLE (n - 1) form, not the population one"

            closeTo
                (sqrt (32.0 / 7.0))
                summary.StandardDeviation
                "standard deviation is the root of the sample variance"

            Expect.isEmpty summary.Quantiles "an empty probability list produces an empty quantile list"
        }

        test "skewness and kurtosis are the unbiased sample estimators, kurtosis EXCESS" {
            let summary = expectSummary (describe ExcelCompatible [||])

            // Independent re-derivation of G1 / G2 from the raw moments,
            // written out here rather than read back from the library
            // under test. G2 subtracts the normal's 3, so a normal sample
            // reads 0 — a consumer treating it as raw kurtosis would be
            // off by 3 and never know.
            let n = float describeSample.Length
            let mean' = 5.0
            let deviations = describeSample |> Array.map (fun x -> x - mean')
            let m2 = deviations |> Array.sumBy (fun d -> d * d)
            let m3 = deviations |> Array.sumBy (fun d -> d * d * d)
            let m4 = deviations |> Array.sumBy (fun d -> d * d * d * d)
            let s = sqrt (m2 / (n - 1.0))

            let g1 = n * m3 / ((n - 1.0) * (n - 2.0) * s * s * s)

            let g2 =
                n * (n + 1.0) * m4 / ((n - 1.0) * (n - 2.0) * (n - 3.0) * s * s * s * s)
                - 3.0 * (n - 1.0) * (n - 1.0) / ((n - 2.0) * (n - 3.0))

            closeTo g1 summary.Skewness "skewness is the unbiased G1 estimator (Excel SKEW)"
            closeTo g2 summary.Kurtosis "kurtosis is the unbiased EXCESS kurtosis G2 (Excel KURT)"
            Expect.isGreaterThan summary.Skewness 0.0 "this sample has a long right tail, so G1 is positive"
        }

        test "statistics undefined at the sample size are NaN, not an error" {
            // The declared precision contract: variance needs n >= 2,
            // skewness n >= 3, kurtosis n >= 4. A refusal would be worse
            // than a NaN here — the sample is legitimate, the statistic
            // is not defined on it.
            let single =
                expectSummary (
                    SummariseDescriptive {
                        Values = [| 4.0 |]
                        Quantiles = [||]
                        Convention = ExcelCompatible
                    }
                )

            Expect.isTrue (System.Double.IsNaN single.Variance) "variance is undefined for n = 1"

            let three =
                expectSummary (
                    SummariseDescriptive {
                        Values = [| 1.0; 2.0; 4.0 |]
                        Quantiles = [||]
                        Convention = ExcelCompatible
                    }
                )

            Expect.isFalse (System.Double.IsNaN three.Skewness) "skewness is defined at n = 3"
            Expect.isTrue (System.Double.IsNaN three.Kurtosis) "kurtosis is undefined for n < 4"
        }
    ]

let private quantileTests =
    testList "quantile conventions" [

        test "R-7 (excelCompatible) matches the spreadsheet, order statistic by order statistic" {
            // R-7: h = (n - 1)p + 1, linear interpolation between the
            // bracketing order statistics. n = 8, so h = 7p + 1.
            //   p = 0.25 → h = 2.75  → 4 + 0.75 * (4 - 4) = 4
            //   p = 0.50 → h = 4.5   → 4 + 0.50 * (5 - 4) = 4.5
            //   p = 0.75 → h = 6.25  → 5 + 0.25 * (7 - 5) = 5.5
            //   p = 0.90 → h = 7.3   → 7 + 0.30 * (9 - 7) = 7.6
            let summary = expectSummary (describe ExcelCompatible [| 0.25; 0.5; 0.75; 0.9 |])
            let values = summary.Quantiles |> List.map _.Value

            Expect.equal
                (summary.Quantiles |> List.map _.Probability)
                [ 0.25; 0.5; 0.75; 0.9 ]
                "quantiles come back in request order, each labelled with its probability"

            closeTo 4.0 values[0] "R-7 first quartile"
            closeTo 4.5 values[1] "R-7 median"
            closeTo 5.5 values[2] "R-7 third quartile"
            closeTo 7.6 values[3] "R-7 90th percentile — Excel PERCENTILE returns 7.6 here"
        }

        test "R-8 (medianUnbiased) is a DIFFERENT number, and the provider serves it" {
            // R-8: h = (n + 1/3)p + 1/3. n = 8, p = 0.9 →
            // h = 8.3333 * 0.9 + 0.3333 = 7.8333 →
            // 7 + 0.8333 * (9 - 7) = 8.6667.
            let summary = expectSummary (describe MedianUnbiased [| 0.9 |])
            let value = (List.head summary.Quantiles).Value

            closeTo (7.0 + (5.0 / 6.0) * 2.0) value "R-8 90th percentile"

            Expect.notEqual
                value
                7.6
                "R-7 and R-8 must not agree here — if they do, the convention switch is being ignored"
        }

        test "the convention is echoed, for both values" {
            for convention in [ ExcelCompatible; MedianUnbiased ] do
                let summary = expectSummary (describe convention [| 0.25 |])

                Expect.equal
                    summary.Convention
                    convention
                    "a quantile without its convention is not a reproducible number"
        }

        test "the median agrees across conventions, and with the p = 0.5 quantile" {
            let excel = expectSummary (describe ExcelCompatible [| 0.5 |])
            let unbiased = expectSummary (describe MedianUnbiased [| 0.5 |])

            closeTo 4.5 excel.Median "R-7 median of the sample"
            closeTo 4.5 unbiased.Median "R-8 agrees with R-7 at the median"

            closeTo
                (List.head excel.Quantiles).Value
                excel.Median
                "the `median` field is the same estimator as the p = 0.5 quantile, not a second definition"
        }
    ]

let private refusalTests =
    testList "refusals" [

        test "an empty sample is refused as invalid arguments, not summarised" {
            let error =
                expectError
                    MathNetAlgorithmIds.Describe
                    (SummariseDescriptive {
                        Values = [||]
                        Quantiles = [||]
                        Convention = ExcelCompatible
                    })

            Expect.stringContains (AlgorithmError.describe error) "empty" "the diagnostic names the empty sample"
        }

        test "an out-of-range probability is refused" {
            let error =
                expectError MathNetAlgorithmIds.Describe (describe ExcelCompatible [| 1.5 |])

            Expect.equal (AlgorithmError.tag error) "invalidArguments" "a probability outside [0, 1] is a bad request"
        }
    ]

let tests =
    testList "Math.NET — descriptive statistics" [ summaryTests; quantileTests; refusalTests ]