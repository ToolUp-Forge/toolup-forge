// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open ToolUp.Platform
open ToolUp.AI.AICompose
open ToolUp.Platform.Compose

/// Composing the render tools, the pipeline tool, the page request
/// resolvers and the layout tools.
///
/// The tool builders take and return the deployment together with the
/// options of the AI tier the tools join (`ServerApp * AIOptions`): a tool
/// rides `AIOptions.AIAnsweringTools`, while its gate reads the deployment's
/// configuration and its endpoint or runtime belongs to the deployment.
/// Compose the AI tier from the options they return.
[<RequireQualifiedAccess>]
module PresentationCompose =

    /// The presentation companion's identity; the page request registrations
    /// are made under it.
    let private owner: ComponentId =
        ComponentId.forCompanionSlot "ToolUp.Facts.Presentation"

    /// Compose `render_chart` and `render_table` onto the AI composition,
    /// beside the three fact tools the fact store declares. The tools ride
    /// `AIOptions.AIAnsweringTools`, which `composeAI` registers through
    /// the record-form funnel, so they arm only when the AI companion is
    /// composed; and only under `FeatureMode.Enabled`, since they read the fact
    /// tables. Under `FeatureMode.Disabled` both are returned unchanged.
    ///
    /// Idempotent: a tool already composed is not composed again. A
    /// deployment that never calls this registers neither tool and carries no
    /// Fuaran-UI dependency.
    ///
    /// ```fsharp skip=fragment
    /// let app, ai = (app, AIOptions.create factory providerProfile) |> PresentationCompose.withRenderTools
    /// app |> AICompose.withAI ai
    /// ```
    let withRenderTools (app: ServerApp, ai: AIOptions) : ServerApp * AIOptions =
        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app, ai
        | FeatureMode.Enabled ->
            let composed =
                ai.AIAnsweringTools
                |> List.map (fun (definition, _) -> definition.Name)
                |> Set.ofList

            match
                RenderTools.tools
                |> List.filter (fun (definition, _) -> not (composed.Contains definition.Name))
            with
            | [] -> app, ai
            | missing -> app, AIOptions.withAIAnsweringTools missing ai

    /// Compose `evaluate_pipeline` onto the AI composition in the answering
    /// form, beside the render tools, and mount the re-run a reader's panel
    /// draws its block from (`IPipelineBlockApi`, at
    /// `/api/_facts/pipeline-block/*`). Only under `FeatureMode.Enabled`; under
    /// `FeatureMode.Disabled` both are returned unchanged.
    ///
    /// Idempotent: when the tool is already composed both are returned
    /// unchanged, so the endpoint is mounted once. A deployment that never
    /// calls this registers no pipeline tool and mounts no endpoint. **Adds**
    /// one answering tool and one handler.
    ///
    /// ```fsharp skip=fragment
    /// let app, ai =
    ///     (app, AIOptions.create factory providerProfile)
    ///     |> PresentationCompose.withRenderTools
    ///     |> PresentationCompose.withPipelineTool
    /// app |> AICompose.withAI ai
    /// ```
    let withPipelineTool (app: ServerApp, ai: AIOptions) : ServerApp * AIOptions =
        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app, ai
        | FeatureMode.Enabled when
            ai.AIAnsweringTools
            |> List.exists (fun (definition, _) -> definition.Name = ToolUp.Facts.PipelineTool.Name)
            ->
            app, ai
        | FeatureMode.Enabled ->
            let mounted = {
                app with
                    Extensions.Handlers = app.Extensions.Handlers @ [ PipelineAnsweringTool.handler ]
            }

            mounted, AIOptions.withAIAnsweringTools [ PipelineAnsweringTool.tool ] ai

    /// Compose the resolution a stored page renders through: registers
    /// `PageRequestResolvers` (the resolver for a request's caller, a
    /// presentation request and a pipeline request each answered under the
    /// caller's gate) and the per-session `PageResolutionCache`, and arms the
    /// fact-table change signal the cache is invalidated by. Only under
    /// `FeatureMode.Enabled`; under `FeatureMode.Disabled` the app is returned unchanged
    /// and nothing is registered. Call it after `FactsCompose.withFactStore`,
    /// which composes the signal. Idempotent. **Adds** its registrations and
    /// one handler: the resolution route
    /// (`PageRequestRoute.Path`, `POST`), answering a request set through the
    /// same resolver, under the surface requirement the composition declares
    /// for the path (the strict `userOrTeam` fallback when it declares none).
    ///
    /// ```fsharp skip=fragment
    /// ServerApp.empty
    /// |> FactsCompose.withFactStore
    /// |> PresentationCompose.withPageRequests
    /// |> ServerApp.run
    /// ```
    let withPageRequests (app: ServerApp) : ServerApp =
        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app
        | FeatureMode.Enabled when app.Contributions |> List.exists (fun c -> c.Owner = owner) -> app
        | FeatureMode.Enabled ->
            {
                app with
                    Extensions.Handlers = app.Extensions.Handlers @ [ PageRequestRoute.handler ]
            }
            |> ServerApp.addContribution (Contribution.service owner PageRequestResolvers.register)

    /// The layout tools' runtime is contributed under its own slot, so that
    /// composing it never reads as composing the page requests.
    let private layoutOwner: ComponentId =
        ComponentId.forCompanionSlot (nameof LayoutToolsRuntime)

    /// Compose the eight `layout_*` tools and `layout_save` onto the AI
    /// composition, beside the render tools: the eight propose an edit of a
    /// stored page as ops over the caller's folded page, validated and
    /// previewed, and `layout_save` lands a proposal on a layer under the page
    /// store's gate and audit. Registers the tools' runtime (the page store,
    /// the page request resolvers and the proposals, resolved in its
    /// registration factory) and composes the page request resolvers
    /// (`withPageRequests`) when they are not composed.
    ///
    /// Only under `FeatureMode.Enabled`, and only when the page store is composed
    /// (`PageDocumentStoreCompose.withPageDocumentStore`, called before this):
    /// otherwise both are returned unchanged and no tool is registered, so
    /// a deployment that composes neither has the tool list it had before.
    /// A page store registered by hand, rather than through its compose
    /// builder, is not seen. Idempotent. **Adds** nine answering tools and one
    /// registration.
    ///
    /// ```fsharp skip=fragment
    /// let app, ai =
    ///     (app |> PageDocumentStoreCompose.withPageDocumentStore PageStoreOptions.defaults,
    ///      AIOptions.create factory providerProfile)
    ///     |> PresentationCompose.withLayoutTools
    /// app |> AICompose.withAI ai
    /// ```
    let withLayoutTools (app: ServerApp, ai: AIOptions) : ServerApp * AIOptions =
        let pageStore =
            app.Contributions
            |> List.exists (fun c -> c.Owner = PageDocumentStoreCompose.componentId)

        let composed =
            ai.AIAnsweringTools
            |> List.exists (fun (definition, _) -> definition.Name = LayoutTools.name LayoutTool.Save)

        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app, ai
        | FeatureMode.Enabled when not pageStore || composed -> app, ai
        | FeatureMode.Enabled ->
            let withRuntime =
                withPageRequests app
                |> ServerApp.addContribution (Contribution.service layoutOwner LayoutTools.register)

            withRuntime, AIOptions.withAIAnsweringTools LayoutTools.tools ai

    /// Let `evaluate_pipeline` assert a result column as facts of a registered
    /// derived metric: the tool gains the `assert` argument, and the write it
    /// may make into the fact store is declared beside its read. Composes the
    /// pipeline tool first when it is not composed (`withPipelineTool`). Only
    /// under `FeatureMode.Enabled`; under `FeatureMode.Disabled` both are returned
    /// unchanged. A deployment that never calls this offers no `assert`
    /// argument, and a request carrying one is refused. **Sets** the composed
    /// tool's declaration and executor; idempotent.
    ///
    /// ```fsharp skip=fragment
    /// let app, ai =
    ///     (app, AIOptions.create factory providerProfile)
    ///     |> PresentationCompose.withPipelineTool
    ///     |> PresentationCompose.withDerivedAssertions
    /// app |> AICompose.withAI ai
    /// ```
    let withDerivedAssertions (app: ServerApp, ai: AIOptions) : ServerApp * AIOptions =
        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app, ai
        | FeatureMode.Enabled ->
            let withTool, composed = withPipelineTool (app, ai)

            withTool,
            {
                composed with
                    AIAnsweringTools =
                        composed.AIAnsweringTools
                        |> List.map (fun (definition, answer) ->
                            if definition.Name = ToolUp.Facts.PipelineTool.Name then
                                PipelineAnsweringTool.assertingTool
                            else
                                definition, answer)
            }

    /// Offer comparison day offsets to the assistant: `render_chart`'s
    /// `compareTo`, and that of `layout_add_block` and `layout_set_request`
    /// when the layout tools are composed, list a `daysBefore:<days>` baseline
    /// per offset (7 is the same day last week, 364 the same weekday last
    /// year). Composes the render tools first when they are not composed
    /// (`withRenderTools`); call it after `withLayoutTools` for the layout
    /// tools to take it. Only under `FeatureMode.Enabled`; under
    /// `FeatureMode.Disabled` both are returned unchanged. A deployment that
    /// never calls this has the tool schemas it had before, byte for byte.
    /// The offering shapes what the model is told only: a stored request, a
    /// report or a tool argument naming any offset in bounds is drawn either
    /// way. **Sets** the composed tools' declarations, keeping their
    /// executors; a later call replaces the offering. Raises
    /// `ArgumentException` naming an offset out of bounds or offered twice.
    ///
    /// ```fsharp skip=fragment
    /// let app, ai =
    ///     (app, AIOptions.create factory providerProfile)
    ///     |> PresentationCompose.withRenderTools
    ///     |> PresentationCompose.withComparisonOffsets [ 7; 364 ]
    /// app |> AICompose.withAI ai
    /// ```
    let withComparisonOffsets (offsets: int list) (app: ServerApp, ai: AIOptions) : ServerApp * AIOptions =
        match app.Config.Stores.FactStore with
        | FeatureMode.Disabled -> app, ai
        | FeatureMode.Enabled ->
            let offsets = RenderTools.checkOffsets offsets
            let withTools, composed = withRenderTools (app, ai)

            let offering (definition: AIToolDefinition) =
                if definition.Name = RenderTools.ChartToolName then
                    Some(RenderTools.definitionOffering offsets RenderTool.Chart)
                else
                    LayoutTools.all
                    |> List.tryFind (fun tool -> LayoutTools.name tool = definition.Name)
                    |> Option.map (LayoutTools.definitionOffering offsets)

            withTools,
            {
                composed with
                    AIAnsweringTools =
                        composed.AIAnsweringTools
                        |> List.map (fun (definition, answer) ->
                            match offering definition with
                            | Some offered -> offered, answer
                            | None -> definition, answer)
            }