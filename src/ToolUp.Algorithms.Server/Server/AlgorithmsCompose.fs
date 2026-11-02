// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmsCompose

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.Compose.RemotingHelpers
open ToolUp.Platform.Compose.Server
open ToolUp.Algorithms.AlgorithmCatalogApiHandler
open ToolUp.Platform.Compose

// ─── The algorithms companion: `AlgorithmsOptions` + `withAlgorithms` ─
//
// The algorithms catalog is a companion composed onto a `ServerApp`, not a
// record that wraps one. `AlgorithmsOptions` holds the providers and the
// algorithms-specific switches; `withAlgorithms options` registers the
// companion with `ServerApp.withCompanion`, so it is applied once, when the
// deployment resolves, and every `ServerApp` builder stays reachable on
// either side of it.
//
// **The registry is built when the companion is applied**, at resolve —
// before the server binds a port — not lazily from the built container. A
// duplicate algorithm id must fail at compose, because the alternative
// (first-request failure, or worse, silent resolution by registration
// order) reintroduces the invisible-default problem this whole companion
// exists to remove. The cost is that `DeclareAlgorithms` must be
// synchronous, which is why `IAlgorithmProvider` declares it that way.
//
// **Zero cost when unused (GP 13).** A deployment that never calls
// `withAlgorithms` registers no service, mounts no route, and adds no
// tool. A deployment that calls it with no providers gets an empty
// catalog and an enumeration tool that says so — still no execution
// path, still no vendor dependency anywhere in the graph.

/// The algorithms companion's identity: the slot `withAlgorithms` composes
/// under and the owner of its contributions.
let algorithmsCompanion: ComponentId =
    ComponentId.forCompanionSlot "ToolUp.Algorithms"

/// What the algorithms companion composes.
type AlgorithmsOptions = {
    /// Providers in composition order. Their declarations are indexed
    /// in this order, and a duplicate id across two of them fails
    /// compose naming both.
    Providers: IAlgorithmProvider list
    /// Register the algorithm AI tools (`_algorithms.list` plus one per
    /// algorithm) onto a `ServerModule`. Default `true`; a deployment
    /// that wants the catalog and the dispatcher without exposing them
    /// to the agent loop calls `withoutAITools`.
    ExposeAITools: bool
    /// Mount the read-only `IAlgorithmCatalogApi` remoting endpoint.
    /// Default `true`.
    ExposeCatalogApi: bool
}

module AlgorithmsOptions =

    /// No providers, the AI tools and the catalog API exposed.
    let create () : AlgorithmsOptions = {
        Providers = []
        ExposeAITools = true
        ExposeCatalogApi = true
    }

    /// **Adds** one algorithm provider. A second provider declaring an
    /// id the first already declared fails compose.
    let withProvider (provider: IAlgorithmProvider) (options: AlgorithmsOptions) : AlgorithmsOptions = {
        options with
            Providers = options.Providers @ [ provider ]
    }

    /// **Adds** several providers in one call.
    let withProviders (providers: IAlgorithmProvider list) (options: AlgorithmsOptions) : AlgorithmsOptions = {
        options with
            Providers = options.Providers @ providers
    }

    /// **Sets** the catalog and dispatcher to compose without exposing
    /// the algorithms to the AI agent loop.
    let withoutAITools (options: AlgorithmsOptions) : AlgorithmsOptions = { options with ExposeAITools = false }

    /// **Sets** the companion to compose without mounting the read-only
    /// catalog remoting endpoint (server-side consumers only).
    let withoutCatalogApi (options: AlgorithmsOptions) : AlgorithmsOptions = {
        options with
            ExposeCatalogApi = false
    }

/// The companion's `apply`: registers `IAlgorithmCatalog`,
/// `IAlgorithmDispatcher` and the `/dev/inspect` contributor as DI
/// singletons over one eagerly-built registry; contributes the catalog
/// API; and (by default) adds a `_algorithms` `ServerModule` carrying the
/// tool family.
let private composeAlgorithms (options: AlgorithmsOptions) (app: ServerApp) : ServerApp =
    // Eager — duplicate ids throw here, at compose.
    let registry = AlgorithmProviderRegistry(options.Providers)
    let catalog = AlgorithmCatalog(registry) :> IAlgorithmCatalog
    let dispatcher = AlgorithmDispatcher(registry) :> IAlgorithmDispatcher

    let algorithmsServiceConfig (services: IServiceCollection) =
        services
            .AddSingleton<AlgorithmProviderRegistry>(registry)
            .AddSingleton<IAlgorithmCatalog>(catalog)
            .AddSingleton<IAlgorithmDispatcher>(dispatcher)
            .AddSingleton<IDevDiagnosticsContributor>(
                AlgorithmCatalogContributor(catalog) :> IDevDiagnosticsContributor
            )

    let withRoutes =
        if options.ExposeCatalogApi then
            app
            |> ServerApp.addContribution (
                Contribution.routes algorithmsCompanion [
                    makeApi (fun (ctx: HttpContext) -> algorithmCatalogApi catalog ctx)
                ]
            )
        else
            app

    let withServices =
        withRoutes
        |> ServerApp.addContribution (Contribution.service algorithmsCompanion algorithmsServiceConfig)

    let withTools =
        if options.ExposeAITools then
            let toolModule =
                ServerModule.create AlgorithmAITools.SourceModule
                |> ServerModule.withAITools (AlgorithmAITools.toolsFor registry.Algorithms)

            ServerApp.addModule toolModule withServices
        else
            withServices

    withTools |> ServerApp.withCompanionMarker "ToolUp.Algorithms"

/// **Sets** the algorithms companion: the catalog, dispatcher, catalog API
/// and AI tool family `options` describes are composed once, when the
/// deployment resolves, onto the deployment as every builder left it. A
/// second call replaces the first, so register every provider in one
/// `AlgorithmsOptions`.
///
/// Example — algorithms alongside AI:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> ServerApp.withStorage storage
///     |> AlgorithmsCompose.withAlgorithms (AlgorithmsOptions.create () |> AlgorithmsOptions.withProvider myProvider)
///     |> AICompose.withAI (AIOptions.create factory profile |> AIOptions.withAIConfig assistant)
///     |> ServerApp.run
let withAlgorithms (options: AlgorithmsOptions) (app: ServerApp) : ServerApp =
    ServerApp.withCompanion algorithmsCompanion (composeAlgorithms options) app