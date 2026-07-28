// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.MathNetSmoothingTests

open Expecto
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures

// ─── Phase 11.E.3 — smoothing alignment, pinned ─────────────────────
//
// The eval's second divergence-3 row: the library's only smoother is
// trailing with an expanding warm-up, so a request for a centred average
// returns the right numbers one period late — and the chart looks fine.
// A test that only checked the VALUES would pass on the defect. These
// cases check the POSITIONS, on a series (1..6) whose windowed means are
// obvious by eye.

let private series = smoothSample

let private smooth (kind: SmoothingKind) (window: int) (warmUp: WarmUpPolicy) =
    SmoothSeries {
        Values = series
        Kind = kind
        Window = window
        Alpha = None
        WarmUp = warmUp
    }

let private trailingTests =
    testList "trailing mean" [

        test "an undefined warm-up leaves the incomplete windows null" {
            let result = expectSmoothing (smooth TrailingMean 3 UndefinedWarmUp)

            smoothingEquals
                [| None; None; Some 2.0; Some 3.0; Some 4.0; Some 5.0 |]
                result.Values
                "the mean of the window ENDING at each period"

            Expect.equal result.Alignment TrailingAligned "alignment is echoed"
            Expect.equal result.Window 3 "the window is echoed"
            Expect.equal result.WarmUp UndefinedWarmUp "the warm-up policy is echoed"
        }

        test "a partial-window warm-up is the library's expanding mean, verbatim" {
            let result = expectSmoothing (smooth TrailingMean 3 PartialWindow)

            smoothingEquals
                [| Some 1.0; Some 1.5; Some 2.0; Some 3.0; Some 4.0; Some 5.0 |]
                result.Values
                "the first two periods average whatever part of the window exists"
        }

        test "a window of 1 is the identity" {
            let result = expectSmoothing (smooth TrailingMean 1 UndefinedWarmUp)

            smoothingEquals (series |> Array.map Some) result.Values "a one-period window changes nothing"
        }
    ]

let private centredTests =
    testList "centred mean" [

        test "an odd window is symmetric, with null padding at BOTH ends" {
            // The whole finding in one assertion: the trailing series
            // pads only the head, and its values sit one period late.
            let result = expectSmoothing (smooth CentredMean 3 UndefinedWarmUp)

            smoothingEquals
                [| None; Some 2.0; Some 3.0; Some 4.0; Some 5.0; None |]
                result.Values
                "the mean of the window CENTRED on each period"

            Expect.equal result.Alignment CentredAligned "alignment is echoed"
        }

        test "centred is the trailing series re-indexed by half a window" {
            let trailing = expectSmoothing (smooth TrailingMean 3 UndefinedWarmUp)
            let centred = expectSmoothing (smooth CentredMean 3 UndefinedWarmUp)

            for i in 1 .. series.Length - 2 do
                Expect.equal
                    centred.Values[i]
                    trailing.Values[i + 1]
                    (sprintf
                        "centred period %d must equal trailing period %d — the off-by-one that survives every visual check"
                        i
                        (i + 1))

            Expect.notEqual
                centred.Values
                trailing.Values
                "the two series must differ, or the alignment switch is being ignored"
        }

        test "an EVEN window leans one period backward — the declared tie-break" {
            // window 4 at period i covers [i - 2, i + 1]:
            //   i = 2 → mean(1,2,3,4) = 2.5
            //   i = 3 → mean(2,3,4,5) = 3.5
            //   i = 4 → mean(3,4,5,6) = 4.5
            let result = expectSmoothing (smooth CentredMean 4 UndefinedWarmUp)

            smoothingEquals
                [| None; None; Some 2.5; Some 3.5; Some 4.5; None |]
                result.Values
                "an even window has no exact centre; the provider's contract states which way it leans"
        }

        test "a partial-window warm-up fills both ends rather than only the head" {
            let result = expectSmoothing (smooth CentredMean 3 PartialWindow)

            smoothingEquals
                [| Some 1.5; Some 2.0; Some 3.0; Some 4.0; Some 5.0; Some 5.5 |]
                result.Values
                "the trailing smoother has no answer for the TAIL at all — this one does"
        }
    ]

let private exponentialTests =
    testList "exponential weighting" [

        test "the recursion is seeded at the first observation" {
            // alpha = 0.5 on [1; 2; 3; ...]:
            //   s0 = 1, s1 = 1.5, s2 = 2.25, s3 = 3.125, ...
            let result =
                expectSmoothing (SmoothSeries(SmoothingRequest.exponential [| 1.0; 2.0; 3.0; 4.0 |] 0.5))

            smoothingEquals
                [| Some 1.0; Some 1.5; Some 2.25; Some 3.125 |]
                result.Values
                "s(i) = alpha*x(i) + (1 - alpha)*s(i-1)"

            Expect.equal result.Alignment TrailingAligned "exponential weighting is trailing by construction"
            Expect.equal result.Window 0 "the window is not read, and is echoed as 0 rather than as the ignored input"
        }

        test "alpha = 1 is the identity" {
            let result = expectSmoothing (SmoothSeries(SmoothingRequest.exponential series 1.0))

            smoothingEquals (series |> Array.map Some) result.Values "all weight on the current observation"
        }

        test "no period is null under either warm-up policy" {
            for warmUp in [ UndefinedWarmUp; PartialWindow ] do
                let result =
                    expectSmoothing (
                        SmoothSeries {
                            Values = series
                            Kind = ExponentiallyWeighted
                            Window = 0
                            Alpha = Some 0.3
                            WarmUp = warmUp
                        }
                    )

                Expect.isTrue
                    (result.Values |> Array.forall Option.isSome)
                    "the recursion is defined from its seed onward, so there is no warm-up to be undefined"
        }
    ]

let private refusalTests =
    testList "refusals" [

        test "a window longer than the series is refused" {
            let error =
                expectError MathNetAlgorithmIds.Smooth (smooth TrailingMean 99 UndefinedWarmUp)

            Expect.equal (AlgorithmError.tag error) "invalidArguments" "there is no window to average"
        }

        test "exponential weighting without an alpha is refused" {
            let error =
                expectError
                    MathNetAlgorithmIds.Smooth
                    (SmoothSeries {
                        Values = series
                        Kind = ExponentiallyWeighted
                        Window = 3
                        Alpha = None
                        WarmUp = UndefinedWarmUp
                    })

            Expect.equal (AlgorithmError.tag error) "invalidArguments" "alpha is not defaulted silently"
        }

        test "every kind returns a series the same length as its input" {
            for kind in [ TrailingMean; CentredMean; ExponentiallyWeighted ] do
                let result =
                    expectSmoothing (
                        SmoothSeries {
                            Values = series
                            Kind = kind
                            Window = 3
                            Alpha = Some 0.4
                            WarmUp = UndefinedWarmUp
                        }
                    )

                Expect.equal
                    result.Values.Length
                    series.Length
                    "a smoothed series is index-comparable with its input or it is not usable"

                Expect.equal result.Kind kind "the kind is echoed"
        }
    ]

let tests =
    testList "Math.NET — time-series smoothing" [ trailingTests; centredTests; exponentialTests; refusalTests ]