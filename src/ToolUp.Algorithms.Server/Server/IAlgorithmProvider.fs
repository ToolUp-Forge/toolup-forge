// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Algorithms

open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — the provider seam ───────────────────────────────
//
// The one execution binding for an algorithm: a provider owns the
// numerics, this companion owns identity, routing, validation, the AI
// tool surface and the catalog. A provider is a companion under
// `src/AlgorithmProviders/`, keyed by a stable `ProviderId`, declaring
// the `AlgorithmId`s it backs.
//
// **This is the entire surface a vendor companion implements.** Adding
// a provider requires no edit to any file in this package — only an
// append-only registration at the consumer's composition root
// (`AlgorithmsServerApp.withProvider`).
//
// **Why a `create`-from-parts helper rather than a bare interface.**
// The four fitter interfaces are the useful decomposition — a provider
// wants to write `MathNetDescriptiveStats` as an `IDescriptiveStats`,
// not as a case in a dispatch `match`. `AlgorithmProvider.create`
// assembles whichever subset the provider implements into the uniform
// `IAlgorithmProvider` the registry indexes, and routes each
// `AlgorithmInvocation` case to the matching fitter. A provider that
// declares an algorithm whose kind it did not supply a fitter for gets
// a typed `Unsupported` naming itself, not a null-reference.

/// An algorithm provider — the execution binding for a set of
/// `AlgorithmId`s.
///
/// Implement via `AlgorithmProvider.create` from the four fitter
/// interfaces; implementing this interface directly is supported but
/// unnecessary for the shipped fitter set.
type IAlgorithmProvider =
    /// Stable provider discriminator, e.g. `"mathnet"`. Appears in
    /// duplicate-registration diagnostics and is stamped onto every
    /// `AlgorithmInfo` the provider declares.
    abstract ProviderId: string

    /// Provider version, stamped onto every declared `AlgorithmInfo`.
    /// Follows the provider companion's own versioning policy.
    abstract ProviderVersion: string

    /// The algorithms this provider backs. **Synchronous by design** —
    /// compose-time declaration metadata, read once while building the
    /// container, mirroring `IModelFitProvider.DeclareGates`. Making it
    /// awaitable would force an `Async.RunSynchronously` at compose,
    /// which is the shape GP 12 rule 2 exists to avoid, not to create.
    ///
    /// The registry stamps `ProviderId` / `ProviderVersion` onto each
    /// entry, so a provider need not populate them.
    abstract DeclareAlgorithms: unit -> AlgorithmInfo list

    /// Execute one invocation against one of the declared ids.
    ///
    /// The dispatcher has already resolved this provider from the id
    /// and validated the request, but a provider MUST NOT assume it:
    /// direct callers exist, and defence here is cheap. Failure is
    /// always a typed `AlgorithmError` — a provider that raises is
    /// wrapped into `ExecutionFailed` by the dispatcher, but should not
    /// rely on that.
    abstract Execute:
        algorithmId: AlgorithmId * invocation: AlgorithmInvocation -> Async<Result<AlgorithmOutcome, AlgorithmError>>

/// The parts a provider companion supplies to `AlgorithmProvider.create`.
/// Every fitter is optional — a provider ships what it can serve.
type AlgorithmProviderParts = {
    ProviderId: string
    ProviderVersion: string
    /// Declarations for every `AlgorithmId` this provider backs. An id
    /// whose `Kind` has no matching fitter below is a declaration
    /// defect; `create` surfaces it as `Unsupported` at invocation
    /// rather than at compose, because a provider may legitimately
    /// declare an id it serves through a fitter added later in the same
    /// release.
    Algorithms: AlgorithmInfo list
    Regression: IRegressionFitter option
    Descriptive: IDescriptiveStats option
    Distribution: IDistributionFitter option
    TimeSeries: ITimeSeriesFilter option
}

module AlgorithmProviderParts =

    /// An empty parts record for `providerId` at `providerVersion` —
    /// the starting point of a fluent build.
    let create (providerId: string) (providerVersion: string) : AlgorithmProviderParts = {
        ProviderId = providerId
        ProviderVersion = providerVersion
        Algorithms = []
        Regression = None
        Descriptive = None
        Distribution = None
        TimeSeries = None
    }

    /// Declare the algorithms this provider backs (appends).
    let withAlgorithms (algorithms: AlgorithmInfo list) (parts: AlgorithmProviderParts) : AlgorithmProviderParts = {
        parts with
            Algorithms = parts.Algorithms @ algorithms
    }

    let withRegression (fitter: IRegressionFitter) (parts: AlgorithmProviderParts) : AlgorithmProviderParts = {
        parts with
            Regression = Some fitter
    }

    let withDescriptive (fitter: IDescriptiveStats) (parts: AlgorithmProviderParts) : AlgorithmProviderParts = {
        parts with
            Descriptive = Some fitter
    }

    let withDistribution (fitter: IDistributionFitter) (parts: AlgorithmProviderParts) : AlgorithmProviderParts = {
        parts with
            Distribution = Some fitter
    }

    let withTimeSeries (fitter: ITimeSeriesFilter) (parts: AlgorithmProviderParts) : AlgorithmProviderParts = {
        parts with
            TimeSeries = Some fitter
    }

module AlgorithmProvider =

    /// Assemble an `IAlgorithmProvider` from a parts record. Routes
    /// each `AlgorithmInvocation` case to the matching fitter and
    /// returns a typed `Unsupported` — naming the provider and the
    /// missing capability — when the fitter for that case was not
    /// supplied.
    ///
    /// Also enforces the kind agreement: an invocation whose kind
    /// differs from the declared algorithm's `Kind` returns
    /// `KindMismatch` rather than being executed against a fitter that
    /// happens to be present.
    let create (parts: AlgorithmProviderParts) : IAlgorithmProvider =
        let declaredKinds =
            parts.Algorithms |> List.map (fun a -> a.Id, a.Kind) |> Map.ofList

        let unsupported (id: AlgorithmId) (capability: string) =
            Error(
                AlgorithmError.Unsupported(
                    id,
                    parts.ProviderId,
                    sprintf "no %s implementation was supplied to this provider" capability
                )
            )

        { new IAlgorithmProvider with
            member _.ProviderId = parts.ProviderId
            member _.ProviderVersion = parts.ProviderVersion
            member _.DeclareAlgorithms() = parts.Algorithms

            member _.Execute(algorithmId, invocation) = async {
                let supplied = AlgorithmInvocation.kind invocation

                match Map.tryFind algorithmId declaredKinds with
                | None when not (List.isEmpty parts.Algorithms) ->
                    // A provider refuses an id it never declared, even
                    // when called directly rather than through the
                    // dispatcher — the dispatcher's own NotFound guard
                    // protects composed callers, not a module holding a
                    // provider reference.
                    return Error(AlgorithmError.NotFound algorithmId)
                | Some declared when declared <> supplied ->
                    return
                        Error(
                            AlgorithmError.KindMismatch(
                                algorithmId,
                                AlgorithmKind.name declared,
                                AlgorithmKind.name supplied
                            )
                        )
                | _ ->
                    match invocation with
                    | FitRegression request ->
                        match parts.Regression with
                        | None -> return unsupported algorithmId "IRegressionFitter"
                        | Some fitter ->
                            let! outcome = fitter.FitLinear request
                            return outcome |> Result.map RegressionOutcome
                    | SummariseDescriptive request ->
                        match parts.Descriptive with
                        | None -> return unsupported algorithmId "IDescriptiveStats"
                        | Some fitter ->
                            let! outcome = fitter.Summarise request
                            return outcome |> Result.map DescriptiveOutcome
                    | FitDistribution request ->
                        match parts.Distribution with
                        | None -> return unsupported algorithmId "IDistributionFitter"
                        | Some fitter ->
                            let! outcome = fitter.Fit request
                            return outcome |> Result.map DistributionOutcome
                    | SmoothSeries request ->
                        match parts.TimeSeries with
                        | None -> return unsupported algorithmId "ITimeSeriesFilter"
                        | Some fitter ->
                            let! outcome = fitter.Smooth request
                            return outcome |> Result.map SmoothingOutcome
            }
        }