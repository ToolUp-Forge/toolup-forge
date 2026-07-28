// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AlgorithmProviders.Tests.MathNetProviderCompositionTests

open Expecto
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations
open ToolUp.AlgorithmProviders
open ToolUp.AlgorithmProviders.Tests.Support.MathNetProviderFixtures

// ─── Phase 11.E.3 — declaration + end-to-end composition ────────────
//
// The contract packs (bound in `Program.fs`) prove the provider meets
// the six-portability bar. These cases cover the two things either side
// of it: what the provider DECLARES, and what a deployment gets when it
// registers the provider and drives a call through the real registry,
// catalog and dispatcher rather than calling the provider directly.

let private declarationTests =
    testList "declarations" [

        test "declares exactly the four curated algorithms, one per kind" {
            let declared = provider.DeclareAlgorithms()

            Expect.equal
                (declared |> List.map _.Id)
                [
                    MathNetAlgorithmIds.Regression
                    MathNetAlgorithmIds.Describe
                    MathNetAlgorithmIds.DistributionFit
                    MathNetAlgorithmIds.Smooth
                ]
                "the four ids the eval selected"

            Expect.equal
                (declared |> List.map _.Kind |> List.sort)
                (AlgorithmKind.all |> List.sort)
                "every catalogued kind is backed, and none twice"
        }

        test "declarations carry NO provider stamp — the registry writes it" {
            // A declaration that populated its own provenance could drift
            // from the provider that actually executes the call.
            for info in provider.DeclareAlgorithms() do
                Expect.equal info.ProviderId "" (sprintf "'%s' must not stamp its own ProviderId" info.Id)
                Expect.equal info.ProviderVersion "" (sprintf "'%s' must not stamp its own version" info.Id)
        }

        test "every declaration uses the canonical parameter specs for its kind" {
            // The AI tool executor parses exactly these names, so a
            // provider authoring its own list would drift the advertised
            // schema away from the parser.
            for info in provider.DeclareAlgorithms() do
                Expect.equal
                    info.Parameters
                    (AlgorithmParameters.forKind info.Kind)
                    (sprintf "'%s' must declare AlgorithmParameters.forKind" info.Id)
        }

        test "every precision contract names the Math.NET release it was written against" {
            for info in provider.DeclareAlgorithms() do
                Expect.stringContains
                    info.PrecisionContract
                    "Math.NET Numerics"
                    (sprintf "'%s' — a precision claim that does not name its implementation is not a contract" info.Id)

                Expect.stringContains
                    info.PrecisionContract
                    MathNetAlgorithmSupport.VendorVersion
                    (sprintf "'%s' must name the bound vendor version" info.Id)
        }

        test "the descriptive contract states the R-7 / R-8 mapping, which is the whole point" {
            let describe =
                provider.DeclareAlgorithms()
                |> List.find (fun i -> i.Id = MathNetAlgorithmIds.Describe)

            Expect.stringContains describe.PrecisionContract "R-7" "the excelCompatible mapping is declared"
            Expect.stringContains describe.PrecisionContract "R-8" "the medianUnbiased mapping is declared"

            Expect.stringContains
                describe.PrecisionContract
                "QuantileCustom"
                "the contract names the call that makes R-7 reachable at all"
        }
    ]

let private compositionTests =
    testList "end-to-end composition" [

        test "a registry over this provider stamps provenance onto every declaration" {
            let registry = AlgorithmProviderRegistry [ provider ]

            Expect.equal registry.AlgorithmIds.Length 4 "four algorithms registered"

            for info in registry.Algorithms do
                Expect.equal
                    info.ProviderId
                    MathNetAlgorithmSupport.ProviderId
                    (sprintf "'%s' is stamped with the registering provider" info.Id)

                Expect.equal
                    info.ProviderVersion
                    MathNetAlgorithmSupport.ProviderVersion
                    (sprintf "'%s' is stamped with the provider version" info.Id)
        }

        test "the catalog lists and resolves what the provider declared" {
            let registry = AlgorithmProviderRegistry [ provider ]
            let catalog = AlgorithmCatalog(registry) :> IAlgorithmCatalog

            let listed = catalog.ListAlgorithms() |> Async.RunSynchronously
            Expect.equal listed.Length 4 "the catalog lists every declared algorithm"

            let regression =
                catalog.GetAlgorithm MathNetAlgorithmIds.Regression |> Async.RunSynchronously

            Expect.isSome regression "the regression algorithm resolves by id"

            let byKind =
                catalog.ListByKind DescriptiveStatistics
                |> Async.RunSynchronously
                |> List.map _.Id

            Expect.equal byKind [ MathNetAlgorithmIds.Describe ] "kind filtering resolves the descriptive algorithm"

            Expect.isNone
                (catalog.GetAlgorithm "not.registered" |> Async.RunSynchronously)
                "an unknown id resolves to None rather than raising"
        }

        test "a regression fit runs end-to-end through the dispatcher" {
            // The acceptance path: a composition registers the provider,
            // and a caller reaches the numerics through the SDK's own
            // dispatcher — id resolution, kind check, shared validation,
            // delegation — rather than by holding a provider reference.
            let registry = AlgorithmProviderRegistry [ provider ]
            let dispatcher = AlgorithmDispatcher(registry) :> IAlgorithmDispatcher

            match
                dispatcher.Execute(MathNetAlgorithmIds.Regression, FitRegression bivariateRequest)
                |> Async.RunSynchronously
            with
            | Ok(RegressionOutcome result) ->
                closeTo 0.8 (List.head result.Coefficients).Estimate "the dispatched fit is the hand-computed slope"
                closeTo 0.6 result.Intercept "the dispatched fit is the hand-computed intercept"
            | Ok other -> failtestf "expected a regression outcome, got %A" (AlgorithmOutcome.kind other)
            | Error e -> failtest (AlgorithmError.describe e)
        }

        test "the dispatcher's typed failure surface holds over this provider" {
            let registry = AlgorithmProviderRegistry [ provider ]
            let dispatcher = AlgorithmDispatcher(registry) :> IAlgorithmDispatcher

            let run id invocation =
                dispatcher.Execute(id, invocation) |> Async.RunSynchronously

            match run "not.registered" (FitRegression bivariateRequest) with
            | Error(AlgorithmError.NotFound _) -> ()
            | other -> failtestf "an unregistered id must be NotFound, got %A" other

            // A descriptive payload routed at the regression id.
            match
                run MathNetAlgorithmIds.Regression (SummariseDescriptive(DescriptiveRequest.create describeSample))
            with
            | Error(AlgorithmError.KindMismatch _) -> ()
            | other -> failtestf "a mismatched payload must be KindMismatch, got %A" other
        }

        test "the compose helper registers the provider on an algorithms pipeline" {
            let app =
                AlgorithmsCompose.AlgorithmsServerApp.create ()
                |> MathNetAlgorithms.withMathNetAlgorithmProvider

            Expect.equal app.Providers.Length 1 "one provider registered"

            Expect.equal app.Providers.Head.ProviderId MathNetAlgorithmSupport.ProviderId "and it is this one"

            // Registration is append-only, so composing it twice claims
            // the same four ids twice — which the registry refuses at
            // compose rather than resolving by order.
            let doubled = app |> MathNetAlgorithms.withMathNetAlgorithmProvider

            Expect.throws
                (fun () -> AlgorithmProviderRegistry doubled.Providers |> ignore)
                "a second claim on the four canonical ids must fail compose, not pick a winner"
        }
    ]

let tests =
    testList "Math.NET — declarations and composition" [ declarationTests; compositionTests ]