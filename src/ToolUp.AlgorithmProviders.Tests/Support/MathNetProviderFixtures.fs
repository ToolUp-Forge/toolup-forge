// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures

open Expecto
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders

// ─── Phase 11.E.3 — shared fixtures for the Math.NET provider pack ──
//
// The eval's closing note on this companion: the hand-written estimators
// are "the least checkable code in the whole family". So every numeric
// claim in this pack is a KNOWN ANSWER — either arithmetic worked out by
// hand on a sample small enough to check by eye, or an independent
// closed-form re-derivation written out here rather than read back from
// the library under test. Nothing is pinned by running the
// implementation and copying what it printed.

/// Every fitter is stateless, so one provider instance serves the whole
/// pack (and the repeat-call cases in the contract pack are testing
/// exactly that).
let provider = MathNetAlgorithms.provider

/// Execute an invocation against the provider, unwrapping the async.
let execute (algorithmId: AlgorithmId) (invocation: AlgorithmInvocation) =
    provider.Execute(algorithmId, invocation) |> Async.RunSynchronously

/// Execute and require success, failing the test with the typed error's
/// own description when it refuses.
let expectOutcome (algorithmId: AlgorithmId) (invocation: AlgorithmInvocation) =
    match execute algorithmId invocation with
    | Ok outcome -> outcome
    | Error e -> failtestf "'%s' was expected to succeed but returned: %s" algorithmId (AlgorithmError.describe e)

/// Execute and require a typed refusal.
let expectError (algorithmId: AlgorithmId) (invocation: AlgorithmInvocation) =
    match execute algorithmId invocation with
    | Error e -> e
    | Ok _ -> failtestf "'%s' was expected to refuse this request but returned an outcome" algorithmId

let expectRegression (invocation: AlgorithmInvocation) =
    match expectOutcome MathNetAlgorithmIds.Regression invocation with
    | RegressionOutcome result -> result
    | other -> failtestf "expected a regression outcome, got %A" (AlgorithmOutcome.kind other)

let expectSummary (invocation: AlgorithmInvocation) =
    match expectOutcome MathNetAlgorithmIds.Describe invocation with
    | DescriptiveOutcome summary -> summary
    | other -> failtestf "expected a descriptive outcome, got %A" (AlgorithmOutcome.kind other)

let expectDistribution (invocation: AlgorithmInvocation) =
    match expectOutcome MathNetAlgorithmIds.DistributionFit invocation with
    | DistributionOutcome result -> result
    | other -> failtestf "expected a distribution outcome, got %A" (AlgorithmOutcome.kind other)

let expectSmoothing (invocation: AlgorithmInvocation) =
    match expectOutcome MathNetAlgorithmIds.Smooth invocation with
    | SmoothingOutcome result -> result
    | other -> failtestf "expected a smoothing outcome, got %A" (AlgorithmOutcome.kind other)

// ─── Assertion helpers ──────────────────────────────────────────────

/// Float comparison at Expecto's `high` accuracy — tight enough that a
/// wrong estimator or a wrong denominator fails, loose enough that a
/// QR solve's last bit does not.
let closeTo (expected: float) (actual: float) (message: string) =
    Expect.floatClose Accuracy.high actual expected message

/// Compare a smoothed series against an expected `float option` series,
/// reporting the whole series on a mismatch — an off-by-one is far
/// easier to read as two aligned sequences than as one index.
let smoothingEquals (expected: float option[]) (actual: float option[]) (message: string) =
    Expect.equal actual.Length expected.Length (sprintf "%s — series length" message)

    for i in 0 .. expected.Length - 1 do
        match expected[i], actual[i] with
        | None, None -> ()
        | Some e, Some a -> closeTo e a (sprintf "%s — period %d (expected %A, got %A)" message i expected actual)
        | _ -> failtestf "%s — period %d: expected %A, got %A" message i expected actual

/// The named parameter of a fit, failing the test when the estimator
/// reported it under a different name (a wrong parameterisation is the
/// eval's measured failure mode, and the NAME is half of it).
let parameterNamed (name: string) (result: DistributionFitResult) =
    match result.Parameters |> List.tryFind (fun p -> p.Name = name) with
    | Some p -> p.Value
    | None ->
        failtestf
            "fit of '%s' reported no parameter named '%s' — it reported %A"
            (DistributionFamily.name result.Family)
            name
            (result.Parameters |> List.map _.Name)

// ─── Samples ────────────────────────────────────────────────────────

/// A deliberately small, ties-carrying sample whose every summary
/// statistic is checkable by hand: n = 8, mean 5, sum of squared
/// deviations 32.
let describeSample = [| 2.0; 4.0; 4.0; 4.0; 5.0; 5.0; 7.0; 9.0 |]

/// A six-period series of consecutive integers — the smoothing
/// arithmetic is then obvious by eye, which is the point: an alignment
/// error is visible in the expected array rather than hidden in it.
let smoothSample = [| 1.0; 2.0; 3.0; 4.0; 5.0; 6.0 |]

/// A bivariate fit with a hand-computed answer: Sxy = 8, Sxx = 10, so
/// slope = 0.8 and intercept = 3 - 0.8 * 3 = 0.6.
let bivariateRequest = {
    Response = [| 1.0; 3.0; 2.0; 5.0; 4.0 |]
    Numeric = [
        {
            Name = "spend"
            Values = [| 1.0; 2.0; 3.0; 4.0; 5.0 |]
        }
    ]
    Categorical = []
    Intercept = true
}

/// A two-level factor with an exactly-representable contrast: the "a"
/// rows mean 1.5, the "b" rows mean 5.5, so the intercept is 1.5 and the
/// contrast is 4.0.
let categoricalRequest = {
    Response = [| 1.0; 2.0; 5.0; 6.0 |]
    Numeric = []
    Categorical = [
        {
            Name = "region"
            Values = [| "a"; "a"; "b"; "b" |]
        }
    ]
    Intercept = true
}

/// One `(algorithmId, invocation)` pair per declared algorithm — what
/// the shared `IAlgorithmProviderContract` pack exercises.
let sampleInvocations = [
    MathNetAlgorithmIds.Regression, FitRegression bivariateRequest
    MathNetAlgorithmIds.Describe, SummariseDescriptive(DescriptiveRequest.create describeSample)
    MathNetAlgorithmIds.DistributionFit, FitDistribution(DistributionFitRequest.create describeSample NormalFamily)
    MathNetAlgorithmIds.Smooth, SmoothSeries(SmoothingRequest.mean smoothSample CentredMean 3)
]