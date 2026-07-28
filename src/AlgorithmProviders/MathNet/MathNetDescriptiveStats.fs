// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AlgorithmProviders

open MathNet.Numerics.Statistics
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders.MathNetAlgorithmSupport

// ─── Phase 11.E.3 — IDescriptiveStats over Math.NET ─────────────────
//
// The row the pre-build eval scored divergence 3 on: the raw path
// compiled first time, returned a plausible number, and silently used
// R-8 where the caller's spreadsheet uses R-7 (~4% apart on a 9-point
// sample, with nothing in the toolchain objecting).
//
// The fix is one line and one obligation. The line is
// `quantileDefinitionOf`, which routes every estimate through
// `QuantileCustom` with an explicit definition. The obligation is that
// `DescriptiveSummary.Convention` echoes what was ASKED for — and it can
// only honestly do that because the estimator is selected from the same
// value, in the same expression, rather than defaulted somewhere else.

/// `IDescriptiveStats` backed by Math.NET Numerics' `Statistics` /
/// `SortedArrayStatistics`.
///
/// Dispersion statistics are the sample (n − 1) forms; `Kurtosis` is
/// EXCESS kurtosis (0 for a normal), which is what Math.NET's estimator
/// returns and what every consumer of the field should assume.
type MathNetDescriptiveStats() =

    interface IDescriptiveStats with

        member _.Summarise request = async {
            // Defence in depth: the dispatcher validates before
            // delegating, but a module holding a provider reference does
            // not, and `AlgorithmValidation` is pure and cheap.
            match AlgorithmValidation.descriptive MathNetAlgorithmIds.Describe request with
            | Error e -> return Error e
            | Ok() ->
                let values = request.Values
                let sorted = Array.sort values
                let quantileAt = quantileOfSorted request.Convention sorted
                let variance = sampleVariance values

                return
                    Ok {
                        Count = values.Length
                        Mean = mean values
                        // The median is the requested definition at
                        // p = 0.5 rather than `Statistics.Median`, so a
                        // summary is internally consistent: R-7 and R-8
                        // agree at the median, but deriving it from the
                        // same estimator as the other quantiles means a
                        // future convention that does NOT agree stays
                        // coherent for free.
                        Median = quantileAt 0.5
                        StandardDeviation = sqrt variance
                        Variance = variance
                        Minimum = sorted[0]
                        Maximum = sorted[sorted.Length - 1]
                        Skewness = Statistics.Skewness values
                        Kurtosis = Statistics.Kurtosis values
                        Quantiles =
                            request.Quantiles
                            |> Array.toList
                            |> List.map (fun p -> {
                                Probability = p
                                Value = quantileAt p
                            })
                        // The contract (`IDescriptiveStats`): echo the
                        // convention that actually ran. It is the same
                        // value the estimator above was selected from,
                        // which is what makes the echo a fact rather
                        // than a claim.
                        Convention = request.Convention
                    }
        }