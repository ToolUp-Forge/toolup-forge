// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.AlgorithmProviders

open MathNet.Numerics.LinearRegression
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders.MathNetAlgorithmSupport

// ─── Phase 11.E.3 — IRegressionFitter over Math.NET ─────────────────
//
// The eval scored this row divergence 2: Math.NET's arithmetic was
// correct on both the bivariate and the multivariate arm, but the answer
// came back as an unlabelled coefficient vector — no term names, no
// reference level — with the dummy-variable trap and a missing intercept
// one keystroke away and neither erroring.
//
// So the encoding moves behind the boundary. Raw labels arrive; this
// file chooses the reference level, builds the design matrix, names
// every coefficient, and reports the contrast base it picked. The
// caller cannot fall into the trap because the caller no longer does the
// encoding.

/// Design-matrix construction, kept separate from the fit so the
/// encoding decisions — reference level, contrast vs full dummy coding,
/// term naming — are readable in one place.
module private MathNetRegressionDesign =

    /// A design column: its coefficient label and its per-observation
    /// values.
    type DesignColumn = { Label: string; Column: float[] }

    /// The distinct levels of a factor in ordinal order. **Sorted, not
    /// first-seen**, so the reference level is a property of the DATA
    /// rather than of the row order — two runs over the same rows
    /// shuffled differently must not report different contrasts.
    let levelsOf (predictor: CategoricalPredictor) : string[] =
        predictor.Values |> Array.distinct |> Array.sort

    /// One indicator column for `level`.
    let indicator (predictor: CategoricalPredictor) (level: string) : DesignColumn = {
        Label = sprintf "%s=%s" predictor.Name level
        Column = predictor.Values |> Array.map (fun v -> if v = level then 1.0 else 0.0)
    }

    /// The categorical columns plus the reference levels they contrast
    /// against.
    ///
    /// **With an intercept**: contrast coding — the first level is
    /// dropped and becomes the baseline the intercept absorbs, so the
    /// remaining indicators are interpretable as differences.
    ///
    /// **Without an intercept**: full dummy coding — there is no
    /// baseline for a contrast to be relative to, so every level gets
    /// its own indicator and `referenceLevels` comes back EMPTY. This is
    /// the honest answer rather than the convenient one: dropping a
    /// level with no intercept would silently constrain that level's
    /// effect to zero, which is precisely the invisible modelling
    /// decision this companion exists to remove.
    let categorical (intercept: bool) (predictors: CategoricalPredictor list) =
        let columns =
            predictors
            |> List.collect (fun p ->
                let levels = levelsOf p

                let coded =
                    if intercept then
                        Array.skip (min 1 levels.Length) levels
                    else
                        levels

                coded |> Array.toList |> List.map (indicator p))

        let references =
            if intercept then
                predictors
                |> List.choose (fun p ->
                    levelsOf p
                    |> Array.tryHead
                    |> Option.map (fun level -> { Factor = p.Name; Level = level }))
            else
                []

        columns, references

    /// Every design column in declaration order: numeric first, then
    /// categorical contrasts. The intercept column is NOT included —
    /// Math.NET's `MultipleRegression.QR` prepends it itself when asked.
    let columnsOf (request: RegressionRequest) =
        let numeric =
            request.Numeric |> List.map (fun p -> { Label = p.Name; Column = p.Values })

        let categorical, references = categorical request.Intercept request.Categorical
        numeric @ categorical, references

/// `IRegressionFitter` backed by Math.NET Numerics'
/// `MultipleRegression.QR` — a Householder QR least-squares solve rather
/// than the normal equations, which is the numerically stabler route on
/// an ill-conditioned design.
type MathNetRegressionFitter() =

    interface IRegressionFitter with

        member _.FitLinear request = async {
            match AlgorithmValidation.regression MathNetAlgorithmIds.Regression request with
            | Error e -> return Error e
            | Ok() ->
                let response = request.Response
                let observations = response.Length
                let columns, references = MathNetRegressionDesign.columnsOf request

                if List.isEmpty columns && not request.Intercept then
                    // Validation admits this (a single-level factor
                    // contributes zero contrast terms), and a zero-column
                    // design with no intercept has nothing to fit.
                    return
                        invalidArguments
                            MathNetAlgorithmIds.Regression
                            "the design has no columns — every categorical predictor has a single level and no intercept was requested, so there is nothing to estimate"
                else

                    let design =
                        Array.init observations (fun i -> columns |> List.map (fun c -> c.Column[i]) |> List.toArray)

                    // A zero-column design WITH an intercept is the
                    // intercept-only model: Math.NET's solver is not
                    // needed (and does not accept an empty column set),
                    // and the least-squares intercept is the mean.
                    let solved =
                        if List.isEmpty columns then
                            [| mean response |]
                        else
                            MultipleRegression.QR(design, response, request.Intercept)

                    let intercept = if request.Intercept then solved[0] else 0.0
                    let slopes = if request.Intercept then Array.skip 1 solved else solved

                    let coefficients =
                        List.zip columns (Array.toList slopes)
                        |> List.map (fun (column, estimate) -> {
                            Term = column.Label
                            Estimate = estimate
                        })

                    let fitted i =
                        let contribution =
                            List.zip columns (Array.toList slopes)
                            |> List.sumBy (fun (column, estimate) -> estimate * column.Column[i])

                        intercept + contribution

                    let residualSumOfSquares =
                        Array.init observations (fun i ->
                            let r = response[i] - fitted i
                            r * r)
                        |> Array.sum

                    // Centred when an intercept is fitted, uncentred when
                    // it is not. The two are NOT comparable — an
                    // uncentred R² is inflated by whatever the response's
                    // mean happens to be — which is why the choice is
                    // stated in the precision contract rather than left
                    // for the reader to infer from a number.
                    let totalSumOfSquares =
                        if request.Intercept then
                            let responseMean = mean response

                            response |> Array.sumBy (fun y -> (y - responseMean) * (y - responseMean))
                        else
                            response |> Array.sumBy (fun y -> y * y)

                    let rSquared =
                        if totalSumOfSquares = 0.0 then
                            nan
                        else
                            1.0 - residualSumOfSquares / totalSumOfSquares

                    let parameters = List.length columns + (if request.Intercept then 1 else 0)

                    let residualDegreesOfFreedom = observations - parameters

                    let adjustedRSquared =
                        if residualDegreesOfFreedom <= 0 then
                            nan
                        else
                            let centringLoss = if request.Intercept then 1 else 0

                            1.0
                            - (1.0 - rSquared) * float (observations - centringLoss)
                              / float residualDegreesOfFreedom

                    let residualStandardError =
                        if residualDegreesOfFreedom <= 0 then
                            nan
                        else
                            sqrt (residualSumOfSquares / float residualDegreesOfFreedom)

                    return
                        Ok {
                            Coefficients = coefficients
                            Intercept = intercept
                            RSquared = rSquared
                            AdjustedRSquared = adjustedRSquared
                            ResidualStandardError = residualStandardError
                            Observations = observations
                            // The contract (`IRegressionFitter`): a
                            // contrast coefficient without its base is
                            // uninterpretable, so the base is reported.
                            ReferenceLevels = references
                        }
        }