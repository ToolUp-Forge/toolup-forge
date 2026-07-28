// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.Tests.Contracts.IAlgorithmProviderContract

open System
open System.Reflection
open Expecto
open ToolUp.Algorithms
open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — the six-portability-rule contract pack ──────────
//
// Runs against ANY `IAlgorithmProvider`, so a provider companion
// (11.E.3 onward) validates against the same bar the in-tree reference
// provider does:
//
//     testList "MathNet" (IAlgorithmProviderContract.tests
//                             MathNetProvider.provider samples)
//
// Four rules are checked structurally by reflection over the shipped
// interfaces — those results are provider-independent, so they are also
// exposed as a standalone `shapeTests` the SDK's own pack runs once.
// Two are checked behaviourally against the supplied provider.

let private asyncType = typedefof<Async<_>>

let private isAsync (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = asyncType

let private isFunction (t: Type) =
    t.IsGenericType && t.GetGenericTypeDefinition() = typedefof<FSharpFunc<_, _>>

let private interfaceMethods (t: Type) =
    t.GetMethods() |> Array.filter (fun m -> not m.IsSpecialName)

/// The four fitter interfaces plus the provider and dispatcher seams.
let private executionInterfaces = [
    typeof<IRegressionFitter>
    typeof<IDescriptiveStats>
    typeof<IDistributionFitter>
    typeof<ITimeSeriesFilter>
    typeof<IAlgorithmDispatcher>
]

/// Every type that crosses an execution boundary.
let private boundaryTypes = [
    typeof<RegressionRequest>
    typeof<RegressionFitResult>
    typeof<DescriptiveRequest>
    typeof<DescriptiveSummary>
    typeof<DistributionFitRequest>
    typeof<DistributionFitResult>
    typeof<SmoothingRequest>
    typeof<SmoothingResult>
]

// ═══ Rules 1–3 + 6: structural, provider-independent ════════════════

/// The provider-independent half of the audit — the shape of the
/// shipped interfaces and the records that cross them.
let shapeTests =
    testList "GP 12 — structural" [

        test "rule 1 — identity by value: no boundary record carries an interface, delegate or function field" {
            for t in boundaryTypes do
                for p in t.GetProperties() do
                    let pt = p.PropertyType

                    Expect.isFalse
                        (pt.IsInterface || isFunction pt || typeof<Delegate>.IsAssignableFrom pt)
                        (sprintf
                            "%s.%s is typed %s — a live handle across the boundary breaks rule 1"
                            t.Name
                            p.Name
                            pt.Name)
        }

        test "rule 2 — async at every execution boundary" {
            for iface in executionInterfaces do
                for m in interfaceMethods iface do
                    Expect.isTrue
                        (isAsync m.ReturnType)
                        (sprintf "%s.%s returns %s, not Async<_>" iface.Name m.Name m.ReturnType.Name)
        }

        test "rule 2 — declaration metadata is deliberately synchronous" {
            // The documented exception, pinned so a later refactor to
            // Async<_> is a deliberate decision rather than drift: making
            // it awaitable forces Async.RunSynchronously at compose,
            // which is what rule 2 exists to prevent.
            let m = typeof<IAlgorithmProvider>.GetMethod "DeclareAlgorithms"

            Expect.isFalse
                (isAsync m.ReturnType)
                "DeclareAlgorithms is compose-time metadata and must stay synchronous — see IAlgorithmProvider.fs"
        }

        test "rule 3 — behaviour as data: no execution method takes a callback" {
            for iface in executionInterfaces do
                for m in interfaceMethods iface do
                    for p in m.GetParameters() do
                        Expect.isFalse
                            (isFunction p.ParameterType || typeof<Delegate>.IsAssignableFrom p.ParameterType)
                            (sprintf
                                "%s.%s takes a callback parameter '%s' — retry / failure behaviour must be data"
                                iface.Name
                                m.Name
                                p.Name)
        }

        test "rule 3 — every execution boundary returns Result<_, AlgorithmError>" {
            for iface in executionInterfaces do
                for m in interfaceMethods iface do
                    let inner = m.ReturnType.GetGenericArguments()[0]

                    Expect.isTrue
                        (inner.IsGenericType
                         && inner.GetGenericTypeDefinition() = typedefof<Result<_, _>>
                         && inner.GetGenericArguments()[1] = typeof<AlgorithmError>)
                        (sprintf
                            "%s.%s returns Async<%s> — failure must be a typed AlgorithmError in a Result"
                            iface.Name
                            m.Name
                            inner.Name)
        }
    ]

// ═══ The per-provider pack ══════════════════════════════════════════

/// Contract tests for one provider. `samples` supplies at least one
/// `(algorithmId, invocation)` the provider is expected to serve.
let tests (provider: IAlgorithmProvider) (samples: (AlgorithmId * AlgorithmInvocation) list) =
    let declared = provider.DeclareAlgorithms()

    testList "IAlgorithmProvider contract" [

        test "declares a non-empty provider id and version" {
            Expect.isNotEmpty provider.ProviderId "ProviderId must be a stable non-empty discriminator"
            Expect.isNotEmpty provider.ProviderVersion "ProviderVersion is part of the catalog stamp"
        }

        test "rule 6 — every declaration states a precision contract" {
            for info in declared do
                Expect.isFalse
                    (String.IsNullOrWhiteSpace info.PrecisionContract)
                    (sprintf
                        "'%s' declares no PrecisionContract — numerical precision is implementation-defined and must be declared, not assumed"
                        info.Id)
        }

        test "rule 1 — declared ids are non-empty strings" {
            for info in declared do
                Expect.isNotEmpty info.Id "an algorithm id is its identity and must be a non-empty string"
        }

        test "declares no id twice" {
            let dupes =
                declared
                |> List.countBy _.Id
                |> List.filter (fun (_, n) -> n > 1)
                |> List.map fst

            Expect.isEmpty dupes (sprintf "provider '%s' declares duplicate ids: %A" provider.ProviderId dupes)
        }

        test "rule 4 — stateless between invocations: repeat calls agree" {
            for (id, invocation) in samples do
                let first = provider.Execute(id, invocation) |> Async.RunSynchronously

                // An unrelated call in between must not perturb the
                // provider — a fitter caching per-caller state would
                // fail here.
                for (otherId, other) in samples do
                    if otherId <> id then
                        provider.Execute(otherId, other) |> Async.RunSynchronously |> ignore

                let second = provider.Execute(id, invocation) |> Async.RunSynchronously

                Expect.equal
                    second
                    first
                    (sprintf "'%s' returned a different outcome on a repeat call — rule 4 requires statelessness" id)
        }

        test "rule 5 — no cross-shard ordering: parallel agrees with sequential" {
            let sequential =
                samples
                |> List.map (fun (id, inv) -> provider.Execute(id, inv) |> Async.RunSynchronously)

            let parallel' =
                samples
                |> List.map provider.Execute
                |> Async.Parallel
                |> Async.RunSynchronously
                |> Array.toList

            Expect.equal
                parallel'
                sequential
                "outcomes differed between parallel and sequential execution — no ordering may be required across invocations"
        }

        test "failure is data: an undeclared id is refused, never thrown" {
            match samples with
            | [] -> ()
            | (_, invocation) :: _ ->
                let outcome =
                    provider.Execute("__not_declared__", invocation) |> Async.RunSynchronously

                match outcome with
                | Error _ -> ()
                | Ok _ -> failtest "an undeclared algorithm id must not produce a successful outcome"
        }

        test "every served sample returns an outcome of the invocation's own kind" {
            for (id, invocation) in samples do
                match provider.Execute(id, invocation) |> Async.RunSynchronously with
                | Ok outcome ->
                    Expect.equal
                        (AlgorithmOutcome.kind outcome)
                        (AlgorithmInvocation.kind invocation)
                        (sprintf "'%s' returned an outcome of the wrong kind" id)
                | Error e ->
                    failtestf
                        "'%s' was expected to be served by this provider but returned %s"
                        id
                        (AlgorithmError.describe e)
        }
    ]

/// The echoed-convention obligations, run against a provider that
/// serves the descriptive and smoothing algorithms. These are the four
/// fields the pre-build eval identified as carrying the wrapper's
/// entire measured value, so they are pinned as contract, not left to
/// each provider's own tests.
let echoedConventionTests (provider: IAlgorithmProvider) (describeId: AlgorithmId) (smoothId: AlgorithmId) =
    testList "echoed conventions" [

        test "the quantile convention is honoured and echoed" {
            for convention in [ ExcelCompatible; MedianUnbiased ] do
                let request = {
                    DescriptiveRequest.create [| 1.0; 2.0; 3.0; 4.0; 5.0 |] with
                        Convention = convention
                }

                match
                    provider.Execute(describeId, SummariseDescriptive request)
                    |> Async.RunSynchronously
                with
                | Ok(DescriptiveOutcome summary) ->
                    Expect.equal
                        summary.Convention
                        convention
                        "a summary must echo the convention it was asked for — reporting one while computing the other is the exact defect this field exists to prevent"
                | Ok other -> failtestf "expected a descriptive outcome, got %A" (AlgorithmOutcome.kind other)
                | Error e -> failtest (AlgorithmError.describe e)
        }

        test "smoothing alignment is derived from the kind and echoed" {
            for kind, expected in [ TrailingMean, TrailingAligned; CentredMean, CentredAligned ] do
                let request = SmoothingRequest.mean [| 1.0; 2.0; 3.0; 4.0; 5.0 |] kind 3

                match provider.Execute(smoothId, SmoothSeries request) |> Async.RunSynchronously with
                | Ok(SmoothingOutcome result) ->
                    Expect.equal
                        result.Alignment
                        expected
                        "alignment must match the requested kind — trailing and centred differ by half a window and the offset survives every visual check"

                    Expect.equal
                        result.Values.Length
                        request.Values.Length
                        "a smoothed series must have the same length as its input"
                | Ok other -> failtestf "expected a smoothing outcome, got %A" (AlgorithmOutcome.kind other)
                | Error e -> failtest (AlgorithmError.describe e)
        }

        test "an undefined warm-up emits None, never a partial-window value" {
            let request = SmoothingRequest.mean [| 1.0; 2.0; 3.0; 4.0; 5.0 |] TrailingMean 3

            match provider.Execute(smoothId, SmoothSeries request) |> Async.RunSynchronously with
            | Ok(SmoothingOutcome result) ->
                Expect.isNone
                    result.Values[0]
                    "the first period of a 3-window trailing mean cannot be defined under UndefinedWarmUp"

                Expect.isSome result.Values[2] "the third period completes the first full window"
            | Ok other -> failtestf "expected a smoothing outcome, got %A" (AlgorithmOutcome.kind other)
            | Error e -> failtest (AlgorithmError.describe e)
        }

        test "a partial-window warm-up emits values where undefined would emit None" {
            let request = {
                SmoothingRequest.mean [| 1.0; 2.0; 3.0; 4.0; 5.0 |] TrailingMean 3 with
                    WarmUp = PartialWindow
            }

            match provider.Execute(smoothId, SmoothSeries request) |> Async.RunSynchronously with
            | Ok(SmoothingOutcome result) ->
                Expect.isSome result.Values[0] "PartialWindow averages whatever part of the window exists"
                Expect.equal result.WarmUp PartialWindow "the warm-up policy must be echoed"
            | Ok other -> failtestf "expected a smoothing outcome, got %A" (AlgorithmOutcome.kind other)
            | Error e -> failtest (AlgorithmError.describe e)
        }
    ]