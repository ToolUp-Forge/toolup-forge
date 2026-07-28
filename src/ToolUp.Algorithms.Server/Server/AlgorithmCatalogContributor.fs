// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Algorithms

open ToolUp.Platform
open ToolUp.Algorithms.AlgorithmTypes

// ─── Phase 11.E.2 — /dev/inspect contributor ────────────────────────
//
// Surfaces the composed catalog under an "Algorithms" panel, so an
// operator can answer "which provider is actually serving this
// algorithm, and what convention does it claim" without reading the
// composition root.
//
// The panel deliberately carries `PrecisionContract` verbatim. Swapping
// providers is a supported operation, and the contract text is the one
// place the numerical differences between two implementations are
// stated — diffing two deployments' panels is how that difference
// becomes visible.

/// `IDevDiagnosticsContributor` over the algorithm catalog. Registered
/// as a DI singleton by `AlgorithmsCompose.withAlgorithms`; resolves the
/// catalog lazily so it reflects whatever is composed.
type AlgorithmCatalogContributor(catalog: IAlgorithmCatalog) =

    interface IDevDiagnosticsContributor with

        member _.Contribute() = async {
            let! algorithms = catalog.ListAlgorithms()

            let byProvider =
                algorithms
                |> List.groupBy _.ProviderId
                |> List.map (fun (providerId, entries) -> {|
                    provider = providerId
                    version =
                        entries
                        |> List.tryHead
                        |> Option.map _.ProviderVersion
                        |> Option.defaultValue ""
                    algorithms = entries |> List.map _.Id
                |})

            let payload = {|
                count = List.length algorithms
                providers = byProvider
                algorithms =
                    algorithms
                    |> List.map (fun info -> {|
                        id = info.Id
                        kind = AlgorithmKind.name info.Kind
                        displayName = info.DisplayName
                        provider = info.ProviderId
                        providerVersion = info.ProviderVersion
                        precision = info.PrecisionContract
                    |})
            |}

            return "Algorithms", box payload
        }