// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Algorithms.AlgorithmsCompose

open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.RemotingHelpers
open ToolUp.Platform.Server
open ToolUp.Algorithms.AlgorithmCatalogApiHandler

// ─── Phase 11.E.2 — composition root ────────────────────────────────
//
// `withAlgorithms` follows the additive companion-set shape
// (`FormsCompose.withForms`): it stacks the algorithms contribution
// onto an existing `ServerApp` pipeline rather than demanding a
// terminal `run`, so a deployment can compose algorithms alongside AI,
// RAG and Forms in one fluent chain.
//
// **The registry is built EAGERLY here**, not lazily from the built
// container. A duplicate algorithm id must fail at compose — before the
// server binds a port — because the alternative (first-request failure,
// or worse, silent resolution by registration order) reintroduces the
// invisible-default problem this whole companion exists to remove. The
// cost is that `DeclareAlgorithms` must be synchronous, which is why
// `IAlgorithmProvider` declares it that way.
//
// **Zero cost when unused (GP 13).** A deployment that never calls
// `withAlgorithms` registers no service, mounts no route, and adds no
// tool. A deployment that calls it with no providers gets an empty
// catalog and an enumeration tool that says so — still no execution
// path, still no vendor dependency anywhere in the graph.

/// Compose-time state for the algorithms companion.
type AlgorithmsServerApp = {
    Base: ServerApp
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

module AlgorithmsServerApp =

    /// Fresh state over an empty `ServerApp`.
    let create () : AlgorithmsServerApp = {
        Base = ServerApp.empty
        Providers = []
        ExposeAITools = true
        ExposeCatalogApi = true
    }

    /// State over an existing pipeline — what `withAlgorithms` uses.
    let createFrom (app: ServerApp) : AlgorithmsServerApp = {
        Base = app
        Providers = []
        ExposeAITools = true
        ExposeCatalogApi = true
    }

    /// Register one algorithm provider. Append-only: a second provider
    /// declaring an id the first already declared fails compose.
    let withProvider (provider: IAlgorithmProvider) (app: AlgorithmsServerApp) : AlgorithmsServerApp = {
        app with
            Providers = app.Providers @ [ provider ]
    }

    /// Register several providers in one call.
    let withProviders (providers: IAlgorithmProvider list) (app: AlgorithmsServerApp) : AlgorithmsServerApp = {
        app with
            Providers = app.Providers @ providers
    }

    /// Compose the catalog and dispatcher without exposing the
    /// algorithms to the AI agent loop.
    let withoutAITools (app: AlgorithmsServerApp) : AlgorithmsServerApp = { app with ExposeAITools = false }

    /// Compose without mounting the read-only catalog remoting
    /// endpoint (server-side consumers only).
    let withoutCatalogApi (app: AlgorithmsServerApp) : AlgorithmsServerApp = { app with ExposeCatalogApi = false }

    /// Fold the algorithms contribution into the base pipeline.
    ///
    /// Registers `IAlgorithmCatalog`, `IAlgorithmDispatcher` and the
    /// `/dev/inspect` contributor as DI singletons over one eagerly-
    /// built registry; mounts the catalog API; and (by default) adds a
    /// `_algorithms` `ServerModule` carrying the tool family.
    let composeAlgorithms (app: AlgorithmsServerApp) : ServerApp =
        // Eager — duplicate ids throw here, at compose.
        let registry = AlgorithmProviderRegistry(app.Providers)
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

        let handlers =
            if app.ExposeCatalogApi then
                [ makeApi (fun (ctx: HttpContext) -> algorithmCatalogApi catalog ctx) ]
            else
                []

        let baseExt = app.Base.Extensions

        let mergedExt: ComposeExtensions = {
            baseExt with
                Handlers = baseExt.Handlers @ handlers
                ServiceConfig =
                    match baseExt.ServiceConfig with
                    | None -> Some algorithmsServiceConfig
                    | Some baseFn -> Some(fun s -> algorithmsServiceConfig (baseFn s))
        }

        let withExtensions = { app.Base with Extensions = mergedExt }

        let withTools =
            if app.ExposeAITools then
                let toolModule =
                    ServerModule.create AlgorithmAITools.SourceModule
                    |> ServerModule.withAITools (AlgorithmAITools.toolsFor registry.Algorithms)

                ServerApp.addModule toolModule withExtensions
            else
                withExtensions

        withTools |> ServerApp.withCompanionMarker "ToolUp.Algorithms"

    /// Terminal composition for a deployment whose only companion is
    /// algorithms. Stacking with other companions uses `withAlgorithms`
    /// instead.
    let run (app: AlgorithmsServerApp) : int = composeAlgorithms app |> ServerApp.run

/// Stack the algorithms companion onto an existing `ServerApp`
/// pipeline. The configurator registers providers and sets the
/// algorithms-specific switches; base configuration belongs on the
/// outer pipeline.
///
/// Example — algorithms alongside AI:
///
///     ServerApp.empty
///     |> ServerApp.withConfig config
///     |> ServerApp.withStorage storage
///     |> AlgorithmsCompose.withAlgorithms (fun a ->
///         a |> AlgorithmsServerApp.withProvider myProvider)
///     |> AICompose.withAI factory profile (fun ai ->
///         ai |> AIServerApp.withAIConfig assistant)
///     |> ServerApp.run
///
/// Calling it twice on one pipeline composes the companion twice — the
/// second call builds a second registry over its own providers and
/// re-registers the DI singletons, so the LAST registration wins and
/// the first provider set is silently unreachable. Register every
/// provider in one call.
let withAlgorithms (configure: AlgorithmsServerApp -> AlgorithmsServerApp) (app: ServerApp) : ServerApp =
    AlgorithmsServerApp.createFrom app
    |> configure
    |> AlgorithmsServerApp.composeAlgorithms