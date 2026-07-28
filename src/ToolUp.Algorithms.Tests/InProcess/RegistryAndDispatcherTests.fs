// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.Tests.InProcess.RegistryAndDispatcherTests

open Expecto
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.Algorithms.Tests.InProcess.ReferenceProvider

// ─── Phase 11.E.2 — registry, catalog and dispatcher paths ──────────

let private catalogOver (providers: IAlgorithmProvider list) =
    AlgorithmCatalog(AlgorithmProviderRegistry providers) :> IAlgorithmCatalog

let private dispatcherOver (providers: IAlgorithmProvider list) =
    AlgorithmDispatcher(AlgorithmProviderRegistry providers) :> IAlgorithmDispatcher

let registryTests =
    testList "AlgorithmProviderRegistry" [

        test "indexes every declaration in registration order" {
            let registry = AlgorithmProviderRegistry [ provider ]

            Expect.equal
                registry.AlgorithmIds
                [ Ids.Regression; Ids.Describe; Ids.DistributionFit; Ids.Smooth ]
                "declarations are indexed in provider-declaration order"
        }

        test "stamps provider provenance onto every declaration" {
            // The provider authors declarations WITHOUT provenance
            // (AlgorithmInfo.declare leaves both blank); the registry is
            // the single writer, so the catalog can never report a
            // provider that differs from the one that will execute.
            let authored = provider.DeclareAlgorithms()

            Expect.all
                authored
                (fun i -> i.ProviderId = "")
                "AlgorithmInfo.declare must leave provenance unset — the registry stamps it"

            let registry = AlgorithmProviderRegistry [ provider ]

            Expect.all
                registry.Algorithms
                (fun i -> i.ProviderId = "reference" && i.ProviderVersion = "0.0.1-test")
                "every indexed declaration carries its registering provider's stamp"
        }

        test "resolves a declared id to its declaration and provider" {
            let registry = AlgorithmProviderRegistry [ provider ]

            match registry.TryResolve Ids.Describe with
            | Some(info, p) ->
                Expect.equal info.Kind DescriptiveStatistics "resolved the wrong declaration"
                Expect.equal p.ProviderId "reference" "resolved the wrong provider"
            | None -> failtest "a declared id must resolve"
        }

        test "an undeclared id resolves to None" {
            let registry = AlgorithmProviderRegistry [ provider ]
            Expect.isNone (registry.TryResolve "nope") "an undeclared id must not resolve"
        }

        // ── The duplicate-registration failure path ──────────────────

        test "two providers claiming one id fail compose, naming both and the id" {
            Expect.throwsC (fun () -> AlgorithmProviderRegistry [ provider; clashingProvider ] |> ignore) (fun ex ->
                // The diagnostic is the point: an operator must be
                // able to act on it without reading the composition
                // root.
                Expect.stringContains ex.Message "'reference'" "the diagnostic must name the first provider"
                Expect.stringContains ex.Message "'clashing'" "the diagnostic must name the second provider"

                Expect.stringContains ex.Message Ids.Describe "the diagnostic must name the contested algorithm id")
        }

        test "one provider declaring the same id twice is the same failure" {
            let doubled =
                AlgorithmProviderParts.create "doubled" "0.0.1-test"
                |> AlgorithmProviderParts.withAlgorithms [ declarations[1]; declarations[1] ]
                |> AlgorithmProvider.create

            Expect.throws
                (fun () -> AlgorithmProviderRegistry [ doubled ] |> ignore)
                "a provider declaring one id twice is the same defect wearing one name"
        }

        test "an empty provider set builds an empty registry rather than failing" {
            let registry = AlgorithmProviderRegistry []

            Expect.isEmpty
                registry.Algorithms
                "composing with no providers is legal — an empty catalog, no execution path"
        }
    ]

let catalogTests =
    testList "IAlgorithmCatalog" [

        // The acceptance path: a consumer registers a provider and
        // lists algorithms through the catalog interface.
        test "lists every registered algorithm with its provider stamp" {
            let catalog = catalogOver [ provider ]
            let algorithms = catalog.ListAlgorithms() |> Async.RunSynchronously

            Expect.hasLength algorithms 4 "the reference provider declares four algorithms"

            Expect.all
                algorithms
                (fun a -> a.ProviderId = "reference")
                "every listed algorithm carries its provider stamp"
        }

        test "gets one algorithm by id" {
            let catalog = catalogOver [ provider ]

            match catalog.GetAlgorithm Ids.Smooth |> Async.RunSynchronously with
            | Some info -> Expect.equal info.Kind TimeSeriesSmoothing "wrong declaration returned"
            | None -> failtest "a registered id must be retrievable"
        }

        test "an unknown id gets None" {
            let catalog = catalogOver [ provider ]
            Expect.isNone (catalog.GetAlgorithm "nope" |> Async.RunSynchronously) "an unknown id yields None"
        }

        test "filters by kind" {
            let catalog = catalogOver [ provider ]

            for kind in AlgorithmKind.all do
                let matched = catalog.ListByKind kind |> Async.RunSynchronously
                Expect.hasLength matched 1 (sprintf "the reference provider declares exactly one %A" kind)
                Expect.all matched (fun a -> a.Kind = kind) "ListByKind must not leak other kinds"
        }

        test "an empty catalog lists nothing" {
            let catalog = catalogOver []
            Expect.isEmpty (catalog.ListAlgorithms() |> Async.RunSynchronously) "no providers, no algorithms"
        }
    ]

let dispatcherTests =
    testList "IAlgorithmDispatcher" [

        test "routes a declared invocation to its provider" {
            let dispatcher = dispatcherOver [ provider ]

            match dispatcher.Execute(Ids.Describe, sampleDescriptive) |> Async.RunSynchronously with
            | Ok(DescriptiveOutcome summary) -> Expect.equal summary.Count 8 "the sample has eight observations"
            | Ok other -> failtestf "wrong outcome kind: %A" (AlgorithmOutcome.kind other)
            | Error e -> failtest (AlgorithmError.describe e)
        }

        test "an unregistered id is NotFound" {
            let dispatcher = dispatcherOver [ provider ]

            match dispatcher.Execute("nope", sampleDescriptive) |> Async.RunSynchronously with
            | Error(AlgorithmError.NotFound id) -> Expect.equal id "nope" "the error names the missing id"
            | other -> failtestf "expected NotFound, got %A" other
        }

        test "a mismatched invocation kind is KindMismatch, not executed" {
            let dispatcher = dispatcherOver [ provider ]

            // A descriptive request routed at the regression algorithm.
            match dispatcher.Execute(Ids.Regression, sampleDescriptive) |> Async.RunSynchronously with
            | Error(AlgorithmError.KindMismatch(id, expected, supplied)) ->
                Expect.equal id Ids.Regression "the error names the algorithm"
                Expect.equal expected "regression" "the error names the declared kind"
                Expect.equal supplied "descriptiveStatistics" "the error names the supplied kind"
            | other -> failtestf "expected KindMismatch, got %A" other
        }

        test "a malformed request is InvalidArguments, checked before the provider runs" {
            let dispatcher = dispatcherOver [ provider ]

            let mismatched =
                FitRegression {
                    Response = [| 1.0; 2.0; 3.0 |]
                    Numeric = [
                        {
                            Name = "spend"
                            Values = [| 1.0; 2.0 |]
                        }
                    ]
                    Categorical = []
                    Intercept = true
                }

            match dispatcher.Execute(Ids.Regression, mismatched) |> Async.RunSynchronously with
            | Error(AlgorithmError.InvalidArguments(id, detail)) ->
                Expect.equal id Ids.Regression "the error names the algorithm"

                Expect.stringContains
                    detail
                    "spend"
                    "the diagnostic must name the offending column, not surface as an index error from inside a matrix library"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "a declared-but-unimplemented algorithm is Unsupported, naming the provider" {
            let dispatcher = dispatcherOver [ hollowProvider ]

            match dispatcher.Execute(Ids.Describe, sampleDescriptive) |> Async.RunSynchronously with
            | Error(AlgorithmError.Unsupported(id, providerId, reason)) ->
                Expect.equal id Ids.Describe "the error names the algorithm"
                Expect.equal providerId "hollow" "the error names the provider the operator must look at"
                Expect.stringContains reason "IDescriptiveStats" "the error names the missing capability"
            | other -> failtestf "expected Unsupported, got %A" other
        }

        test "a provider refusal is passed through as data" {
            let dispatcher = dispatcherOver [ provider ]

            let poisson =
                FitDistribution(DistributionFitRequest.create [| 0.0; 1.0; 2.0 |] PoissonFamily)

            match dispatcher.Execute(Ids.DistributionFit, poisson) |> Async.RunSynchronously with
            | Error(AlgorithmError.Unsupported(_, _, reason)) ->
                Expect.stringContains
                    reason
                    "poisson"
                    "a family the provider cannot fit is refused by name, never substituted"
            | other -> failtestf "expected Unsupported, got %A" other
        }

        test "an escaped provider exception becomes ExecutionFailed, not a throw" {
            let dispatcher = dispatcherOver [ throwingProvider ]

            match dispatcher.Execute(Ids.Describe, sampleDescriptive) |> Async.RunSynchronously with
            | Error(AlgorithmError.ExecutionFailed(id, message)) ->
                Expect.equal id Ids.Describe "the error names the algorithm"

                Expect.stringContains
                    message
                    "reference explosion"
                    "the provider's own message is preserved for the operator"
            | other -> failtestf "expected ExecutionFailed, got %A" other
        }

        test "a discrete family rejects non-integer values before dispatch" {
            let dispatcher = dispatcherOver [ provider ]

            let fractional =
                FitDistribution(DistributionFitRequest.create [| 1.5; 2.0 |] NegativeBinomialFamily)

            match dispatcher.Execute(Ids.DistributionFit, fractional) |> Async.RunSynchronously with
            | Error(AlgorithmError.InvalidArguments(_, detail)) ->
                Expect.stringContains detail "discrete" "the diagnostic explains why the sample is inadmissible"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }
    ]

let validationTests =
    testList "AlgorithmValidation" [

        test "rejects an empty sample" {
            match AlgorithmValidation.descriptive "x" (DescriptiveRequest.create [||]) with
            | Error(AlgorithmError.InvalidArguments(_, d)) -> Expect.stringContains d "empty" "names the problem"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "rejects an out-of-range quantile probability" {
            let request = {
                DescriptiveRequest.create [| 1.0 |] with
                    Quantiles = [| 0.5; 1.5 |]
            }

            match AlgorithmValidation.descriptive "x" request with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "1.5" "names the offending probability"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "rejects a regression with no predictors" {
            let request = {
                Response = [| 1.0; 2.0 |]
                Numeric = []
                Categorical = []
                Intercept = true
            }

            match AlgorithmValidation.regression "x" request with
            | Error(AlgorithmError.InvalidArguments(_, d)) -> Expect.stringContains d "predictor" "names the problem"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "rejects a regression with fewer observations than terms" {
            let request = {
                Response = [| 1.0; 2.0 |]
                Numeric = [
                    { Name = "a"; Values = [| 1.0; 2.0 |] }
                    { Name = "b"; Values = [| 3.0; 4.0 |] }
                    { Name = "c"; Values = [| 5.0; 6.0 |] }
                ]
                Categorical = []
                Intercept = true
            }

            match AlgorithmValidation.regression "x" request with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "cannot fit" "an under-determined fit is refused up front"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "rejects a window longer than the series" {
            match AlgorithmValidation.smoothing "x" (SmoothingRequest.mean [| 1.0; 2.0 |] TrailingMean 5) with
            | Error(AlgorithmError.InvalidArguments(_, d)) -> Expect.stringContains d "exceeds" "names the problem"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "rejects exponential smoothing without an alpha" {
            let request = {
                SmoothingRequest.exponential [| 1.0; 2.0 |] 0.5 with
                    Alpha = None
            }

            match AlgorithmValidation.smoothing "x" request with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "alpha" "names the missing parameter"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "rejects an alpha outside (0, 1]" {
            match AlgorithmValidation.smoothing "x" (SmoothingRequest.exponential [| 1.0 |] 1.5) with
            | Error(AlgorithmError.InvalidArguments(_, d)) ->
                Expect.stringContains d "alpha" "names the offending parameter"
            | other -> failtestf "expected InvalidArguments, got %A" other
        }

        test "accepts a well-formed request of every kind" {
            for (id, invocation) in sampleInvocations do
                match AlgorithmValidation.invocation id invocation with
                | Ok() -> ()
                | Error e -> failtestf "the shared sample for '%s' must validate: %s" id (AlgorithmError.describe e)
        }
    ]

let wireVocabularyTests =
    testList "wire vocabulary round-trips" [

        // These strings are the package's public contract — they appear
        // in AI tool payloads, catalog JSON and /dev/inspect output. A
        // re-spelling is a breaking change, so the round-trip is pinned.
        test "AlgorithmKind" {
            for k in AlgorithmKind.all do
                Expect.equal (AlgorithmKind.parse (AlgorithmKind.name k)) (Some k) "kind must round-trip"
        }

        test "QuantileConvention" {
            for c in [ ExcelCompatible; MedianUnbiased ] do
                Expect.equal
                    (QuantileConvention.parse (QuantileConvention.name c))
                    (Some c)
                    "convention must round-trip"
        }

        test "DistributionFamily" {
            for f in DistributionFamily.all do
                Expect.equal (DistributionFamily.parse (DistributionFamily.name f)) (Some f) "family must round-trip"
        }

        test "EstimationMethod" {
            for m in [ MaximumLikelihood; MethodOfMoments ] do
                Expect.equal (EstimationMethod.parse (EstimationMethod.name m)) (Some m) "method must round-trip"
        }

        test "SmoothingKind" {
            for k in [ TrailingMean; CentredMean; ExponentiallyWeighted ] do
                Expect.equal (SmoothingKind.parse (SmoothingKind.name k)) (Some k) "kind must round-trip"
        }

        test "WarmUpPolicy" {
            for w in [ PartialWindow; UndefinedWarmUp ] do
                Expect.equal (WarmUpPolicy.parse (WarmUpPolicy.name w)) (Some w) "policy must round-trip"
        }

        test "an unknown tag parses to None rather than throwing" {
            Expect.isNone (AlgorithmKind.parse "regressionn") "an unknown tag is data"
            Expect.isNone (DistributionFamily.parse "gaussian") "an unknown tag is data"
        }

        test "the default quantile convention is the spreadsheet-compatible one" {
            // Load-bearing: the eval measured a ~4% silent divergence
            // against Excel / numpy / pandas from a library defaulting
            // the other way. A caller who says nothing must get the
            // number they are checking against.
            Expect.equal
                (DescriptiveRequest.create [| 1.0 |]).Convention
                ExcelCompatible
                "the default convention must stay R-7 — see evals/algorithms-primitives-eval/findings.md"
        }

        test "the default warm-up policy leaves the warm-up undefined" {
            Expect.equal
                (SmoothingRequest.mean [| 1.0 |] TrailingMean 1).WarmUp
                UndefinedWarmUp
                "a partial window must not silently read as a real value"
        }
    ]