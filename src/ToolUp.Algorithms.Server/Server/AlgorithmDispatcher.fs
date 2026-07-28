// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Algorithms

open ToolUp.Algorithms.AlgorithmTypes
open ToolUp.Algorithms.AlgorithmOperations

// ─── Phase 11.E.2 — registry, catalog, dispatcher ───────────────────
//
// `AlgorithmProviderRegistry` is the compose-time index: it stamps
// provider provenance onto every declaration and REFUSES a duplicate
// `AlgorithmId`. `AlgorithmCatalog` is the read projection over it;
// `AlgorithmDispatcher` is the execution path.
//
// **Why duplicate registration is fatal at compose.** Two providers
// declaring `regression.linear` is not a preference to be resolved by
// registration order — the two implementations differ in convention,
// precision, and estimator choice, which are exactly the things this
// companion exists to make legible. Silently picking one would
// reintroduce, at the composition root, the invisible-default problem
// the eval measured inside the numerics library. So the registry
// throws, naming BOTH providers and the contested id, and the
// deployment fails to start loudly. This mirrors the `INotificationSink`
// one-per-`Kind` rule and `ModelFitProviderRegistry`'s one-per-`Kind`
// rule; the diagnostic shape is deliberately the same family.

/// Compose-time index of registered algorithm providers, keyed by
/// `AlgorithmId`. Constructed eagerly by
/// `AlgorithmsCompose.withAlgorithms`, so a duplicate declaration is a
/// compose-time failure rather than a first-request failure.
///
/// Raises on construction when two providers declare the same
/// `AlgorithmId`. The message names the id and both providers.
type AlgorithmProviderRegistry(providers: IAlgorithmProvider list) =

    // (id, provider) pairs in registration order, provenance stamped.
    let declared =
        providers
        |> List.collect (fun provider ->
            provider.DeclareAlgorithms()
            |> List.map (fun info ->
                let stamped =
                    AlgorithmInfo.withProvider provider.ProviderId provider.ProviderVersion info

                stamped, provider))

    do
        // Duplicate ids across providers (and within one provider's own
        // declaration list — a provider declaring an id twice is the
        // same defect wearing one name).
        let duplicates =
            declared
            |> List.groupBy (fun (info, _) -> info.Id)
            |> List.filter (fun (_, group) -> List.length group > 1)

        if not (List.isEmpty duplicates) then
            let describe (id: AlgorithmId, group: (AlgorithmInfo * IAlgorithmProvider) list) =
                let names =
                    group
                    |> List.map (fun (_, p) -> sprintf "'%s'" p.ProviderId)
                    |> String.concat " and "

                sprintf "providers %s both registered algorithm id '%s'" names id

            let listing = duplicates |> List.map describe |> String.concat "; "

            failwithf
                "ToolUp.Algorithms: duplicate algorithm registration — %s. One provider per algorithm id: the implementations differ in convention and precision, so resolving the clash by registration order would hide exactly the choice the catalog exists to surface. Remove one provider, or have one of them declare a distinct id."
                listing

    let byId =
        declared
        |> List.map (fun (info, provider) -> info.Id, (info, provider))
        |> Map.ofList

    /// Every registered provider, in composition order.
    member _.Providers: IAlgorithmProvider list = providers

    /// Every declared algorithm, provenance-stamped, in registration
    /// order.
    member _.Algorithms: AlgorithmInfo list = declared |> List.map fst

    /// The registered algorithm ids, in registration order.
    member _.AlgorithmIds: AlgorithmId list =
        declared |> List.map (fun (info, _) -> info.Id)

    /// Resolve an id to its declaration and the provider that backs it.
    /// `None` when nothing declared it.
    member _.TryResolve(algorithmId: AlgorithmId) : (AlgorithmInfo * IAlgorithmProvider) option =
        Map.tryFind algorithmId byId

/// In-process `IAlgorithmCatalog` over a registry snapshot. Pure
/// projection — no I/O, no mutation, safe to resolve as a singleton.
type AlgorithmCatalog(registry: AlgorithmProviderRegistry) =

    interface IAlgorithmCatalog with

        member _.ListAlgorithms() = async { return registry.Algorithms }

        member _.GetAlgorithm(algorithmId) = async { return registry.TryResolve algorithmId |> Option.map fst }

        member _.ListByKind(kind) = async { return registry.Algorithms |> List.filter (fun info -> info.Kind = kind) }

/// In-process `IAlgorithmDispatcher` over a registry snapshot.
///
/// Order of operations per call, each failure typed:
///   1. resolve the id  → `NotFound`
///   2. check the invocation's kind against the declaration
///      → `KindMismatch`
///   3. validate the request (`AlgorithmValidation`)
///      → `InvalidArguments`
///   4. delegate to the provider, converting an escaped exception
///      → `ExecutionFailed`
///
/// Step 3 runs HERE rather than being left to each provider so a
/// malformed request produces the same diagnostic whichever provider is
/// composed — a mismatched predictor length reads as a named field
/// problem, not as an index-out-of-range from inside a matrix library.
/// Providers may re-validate; the check is pure and cheap.
type AlgorithmDispatcher(registry: AlgorithmProviderRegistry) =

    interface IAlgorithmDispatcher with

        member _.Execute(algorithmId, invocation) = async {
            match registry.TryResolve algorithmId with
            | None -> return Error(AlgorithmError.NotFound algorithmId)
            | Some(info, provider) ->
                let supplied = AlgorithmInvocation.kind invocation

                if info.Kind <> supplied then
                    return
                        Error(
                            AlgorithmError.KindMismatch(
                                algorithmId,
                                AlgorithmKind.name info.Kind,
                                AlgorithmKind.name supplied
                            )
                        )
                else
                    match AlgorithmValidation.invocation algorithmId invocation with
                    | Error e -> return Error e
                    | Ok() ->
                        try
                            return! provider.Execute(algorithmId, invocation)
                        with ex ->
                            // A provider that raises has broken the data
                            // contract (GP 12 rule 3). Wrapping keeps the
                            // caller's contract intact rather than letting
                            // the breach propagate.
                            return Error(AlgorithmError.ExecutionFailed(algorithmId, ex.Message))
        }