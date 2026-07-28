// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.MathNetRegressionTests

open Expecto
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures

// ─── Phase 11.E.3 — linear regression, pinned ───────────────────────
//
// The eval scored the arithmetic correct and the OUTPUT uninterpretable:
// an unlabelled coefficient vector, no reference level, with the
// dummy-variable trap one keystroke away. These cases therefore pin
// three separate things, not one:
//
//   1. the numbers (hand-computed OLS on samples small enough to check);
//   2. the LABELS (`spend`, `region=b`) — half the eval's finding;
//   3. the encoding decisions the boundary now owns — which level became
//      the reference, and what happens to that choice with no intercept.

let private ordinaryLeastSquaresTests =
    testList "ordinary least squares" [

        test "bivariate fit matches the hand-computed normal equations" {
            // x = 1..5, y = [1; 3; 2; 5; 4]; xbar = 3, ybar = 3.
            // Sxy = 8, Sxx = 10 → slope 0.8, intercept 3 - 0.8*3 = 0.6.
            let result = expectRegression (FitRegression bivariateRequest)

            Expect.equal
                (result.Coefficients |> List.map _.Term)
                [ "spend" ]
                "the coefficient carries the predictor's NAME"

            closeTo 0.8 (List.head result.Coefficients).Estimate "slope = Sxy / Sxx = 8 / 10"
            closeTo 0.6 result.Intercept "intercept = ybar - slope * xbar"
            Expect.equal result.Observations 5 "observations consumed"
            Expect.isEmpty result.ReferenceLevels "a purely numeric fit has no categorical contrast base"
        }

        test "fit diagnostics follow from the same hand-computed residuals" {
            // Fitted: 1.4, 2.2, 3.0, 3.8, 4.6 → residuals
            // -0.4, 0.8, -1.0, 1.2, -0.6 → SSres = 3.6.
            // SStot = 10 → R^2 = 0.64.
            // adjusted = 1 - 0.36 * (5 - 1) / (5 - 2) = 0.52.
            // RSE = sqrt(3.6 / 3).
            let result = expectRegression (FitRegression bivariateRequest)

            closeTo 0.64 result.RSquared "R^2 = 1 - SSres / SStot"
            closeTo 0.52 result.AdjustedRSquared "adjusted R^2 uses n - k residual degrees of freedom"
            closeTo (sqrt 1.2) result.ResidualStandardError "residual standard error = sqrt(SSres / (n - k))"
        }

        test "multivariate fit recovers an exactly-determined model" {
            // y = 1 + 2*x1 + 3*x2 exactly, so the solve must return the
            // generating coefficients and R^2 = 1.
            let result =
                expectRegression (
                    FitRegression {
                        Response = [| 3.0; 8.0; 7.0; 12.0 |]
                        Numeric = [
                            {
                                Name = "x1"
                                Values = [| 1.0; 2.0; 3.0; 4.0 |]
                            }
                            {
                                Name = "x2"
                                Values = [| 0.0; 1.0; 0.0; 1.0 |]
                            }
                        ]
                        Categorical = []
                        Intercept = true
                    }
                )

            Expect.equal
                (result.Coefficients |> List.map _.Term)
                [ "x1"; "x2" ]
                "coefficients come back in predictor declaration order"

            closeTo 2.0 (result.Coefficients |> List.item 0).Estimate "x1 coefficient"
            closeTo 3.0 (result.Coefficients |> List.item 1).Estimate "x2 coefficient"
            closeTo 1.0 result.Intercept "intercept"
            closeTo 1.0 result.RSquared "an exactly-determined model explains everything"
        }

        test "intercept = false forces the line through the origin" {
            // y = 2x exactly, no intercept term.
            let result =
                expectRegression (
                    FitRegression {
                        Response = [| 2.0; 4.0; 6.0; 8.0 |]
                        Numeric = [
                            {
                                Name = "x"
                                Values = [| 1.0; 2.0; 3.0; 4.0 |]
                            }
                        ]
                        Categorical = []
                        Intercept = false
                    }
                )

            closeTo 2.0 (List.head result.Coefficients).Estimate "slope through the origin"
            closeTo 0.0 result.Intercept "the intercept is reported as exactly 0 when none was fitted"
        }
    ]

let private categoricalTests =
    testList "categorical encoding" [

        test "a two-level factor becomes one named contrast against a reported reference" {
            // "a" rows mean 1.5, "b" rows mean 5.5 → intercept 1.5,
            // contrast 4.0.
            let result = expectRegression (FitRegression categoricalRequest)

            Expect.equal
                (result.Coefficients |> List.map _.Term)
                [ "region=b" ]
                "a categorical coefficient reads '{factor}={level}', not an anonymous slot"

            closeTo 4.0 (List.head result.Coefficients).Estimate "contrast = mean(b) - mean(a)"
            closeTo 1.5 result.Intercept "the intercept absorbs the reference level"

            Expect.equal
                result.ReferenceLevels
                [ { Factor = "region"; Level = "a" } ]
                "the contrast base is REPORTED — without it the coefficient above is uninterpretable"
        }

        test "the reference level is the first in ordinal order, not the first row" {
            // "z" appears first in the data; "a" sorts first. The
            // reference must be a property of the data, not of the row
            // order, or two runs over the same rows shuffled differently
            // report different contrasts.
            let result =
                expectRegression (
                    FitRegression {
                        Response = [| 5.0; 1.0; 7.0; 3.0 |]
                        Numeric = []
                        Categorical = [
                            {
                                Name = "grade"
                                Values = [| "z"; "a"; "z"; "a" |]
                            }
                        ]
                        Intercept = true
                    }
                )

            Expect.equal
                result.ReferenceLevels
                [ { Factor = "grade"; Level = "a" } ]
                "the sorted-first level is the reference"

            Expect.equal
                (result.Coefficients |> List.map _.Term)
                [ "grade=z" ]
                "the non-reference level gets the contrast"

            closeTo 2.0 result.Intercept "mean of the 'a' rows"
            closeTo 4.0 (List.head result.Coefficients).Estimate "mean(z) - mean(a) = 6 - 2"
        }

        test "a three-level factor drops exactly one level" {
            let result =
                expectRegression (
                    FitRegression {
                        Response = [| 1.0; 2.0; 4.0; 5.0; 9.0; 10.0 |]
                        Numeric = []
                        Categorical = [
                            {
                                Name = "tier"
                                Values = [| "low"; "low"; "mid"; "mid"; "top"; "top" |]
                            }
                        ]
                        Intercept = true
                    }
                )

            Expect.equal
                (result.Coefficients |> List.map _.Term)
                [ "tier=mid"; "tier=top" ]
                "k levels produce k - 1 contrasts — the dummy-variable trap is structurally unreachable"

            Expect.equal
                result.ReferenceLevels
                [ { Factor = "tier"; Level = "low" } ]
                "'low' sorts first and becomes the base"

            closeTo 1.5 result.Intercept "mean of the reference rows"
            closeTo 3.0 (result.Coefficients |> List.item 0).Estimate "mid - low = 4.5 - 1.5"
            closeTo 8.0 (result.Coefficients |> List.item 1).Estimate "top - low = 9.5 - 1.5"
        }

        test "without an intercept every level gets its own indicator and no reference is claimed" {
            // There is no baseline for a contrast to be relative to, so
            // dropping a level would silently constrain its effect to
            // zero. Full dummy coding is the honest answer, and the
            // EMPTY referenceLevels list is how the caller is told.
            let result =
                expectRegression (
                    FitRegression {
                        Response = [| 1.0; 1.0; 5.0; 5.0 |]
                        Numeric = []
                        Categorical = [
                            {
                                Name = "region"
                                Values = [| "a"; "a"; "b"; "b" |]
                            }
                        ]
                        Intercept = false
                    }
                )

            Expect.equal
                (result.Coefficients |> List.map _.Term)
                [ "region=a"; "region=b" ]
                "with no intercept, every level is an absolute level effect"

            closeTo 1.0 (result.Coefficients |> List.item 0).Estimate "the 'a' level effect"
            closeTo 5.0 (result.Coefficients |> List.item 1).Estimate "the 'b' level effect"

            Expect.isEmpty
                result.ReferenceLevels
                "no intercept means no contrast base — claiming one would be a false report"
        }

        test "numeric and categorical predictors compose, numeric first" {
            let result =
                expectRegression (
                    FitRegression {
                        // y = 1 + 2*x + 3*[region = b]
                        Response = [| 3.0; 5.0; 10.0; 12.0 |]
                        Numeric = [
                            {
                                Name = "spend"
                                Values = [| 1.0; 2.0; 3.0; 4.0 |]
                            }
                        ]
                        Categorical = [
                            {
                                Name = "region"
                                Values = [| "a"; "a"; "b"; "b" |]
                            }
                        ]
                        Intercept = true
                    }
                )

            Expect.equal
                (result.Coefficients |> List.map _.Term)
                [ "spend"; "region=b" ]
                "numeric terms precede categorical contrasts"

            closeTo 2.0 (result.Coefficients |> List.item 0).Estimate "numeric slope"
            closeTo 3.0 (result.Coefficients |> List.item 1).Estimate "categorical contrast"
            closeTo 1.0 result.Intercept "intercept"
        }
    ]

let private refusalTests =
    testList "refusals and degenerate designs" [

        test "a predictor of the wrong length is refused, naming the column" {
            let error =
                expectError
                    MathNetAlgorithmIds.Regression
                    (FitRegression {
                        Response = [| 1.0; 2.0; 3.0 |]
                        Numeric = [
                            {
                                Name = "spend"
                                Values = [| 1.0; 2.0 |]
                            }
                        ]
                        Categorical = []
                        Intercept = true
                    })

            let text = AlgorithmError.describe error
            Expect.stringContains text "spend" "the diagnostic names the offending column"
            Expect.equal (AlgorithmError.tag error) "invalidArguments" "a length mismatch is a bad request"
        }

        test "a fit with no predictors at all is refused" {
            let error =
                expectError
                    MathNetAlgorithmIds.Regression
                    (FitRegression {
                        Response = [| 1.0; 2.0; 3.0 |]
                        Numeric = []
                        Categorical = []
                        Intercept = true
                    })

            Expect.equal (AlgorithmError.tag error) "invalidArguments" "there is nothing to regress on"
        }

        test "a single-level factor degenerates to the intercept-only model, not a crash" {
            // Contrast coding drops the only level, so the design has
            // ZERO columns and the least-squares intercept is the
            // response mean. The reference level is still reported —
            // that factor genuinely has a base, it just has nothing to
            // contrast against.
            let result =
                expectRegression (
                    FitRegression {
                        Response = [| 1.0; 2.0; 6.0 |]
                        Numeric = []
                        Categorical = [
                            {
                                Name = "constant"
                                Values = [| "only"; "only"; "only" |]
                            }
                        ]
                        Intercept = true
                    }
                )

            Expect.isEmpty result.Coefficients "a single-level factor contributes no contrast terms"
            closeTo 3.0 result.Intercept "the intercept-only fit is the response mean"

            Expect.equal
                result.ReferenceLevels
                [ { Factor = "constant"; Level = "only" } ]
                "the base is reported even when nothing contrasts against it"
        }
    ]

let tests =
    testList "Math.NET — linear regression" [ ordinaryLeastSquaresTests; categoricalTests; refusalTests ]