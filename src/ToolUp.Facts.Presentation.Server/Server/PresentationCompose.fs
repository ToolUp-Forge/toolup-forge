// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open ToolUp.Platform
open ToolUp.AI.AICompose

/// Composing the render tools and the pipeline tool.
[<RequireQualifiedAccess>]
module PresentationCompose =

    /// The presentation companion's identity; the page request registrations
    /// are made under it.
    let private owner: ComponentId =
        ComponentId.forCompanionSlot "ToolUp.Facts.Presentation"

    /// Compose `render_chart` and `render_table` onto the AI composition,
    /// beside the three fact tools the fact store declares. The tools ride
    /// `AIServerApp.AIAnsweringTools`, which `composeAI` registers through
    /// the record-form funnel, so they arm only when the AI companion is
    /// composed; and only under `EnabledFactStore`, since they read the fact
    /// tables. Under `NoFactStore` the app is returned unchanged.
    ///
    /// Idempotent: a tool already composed is not composed again. A
    /// deployment that never calls this registers neither tool and carries no
    /// Fuaran-UI dependency.
    ///
    /// ```fsharp skip=fragment
    /// AIServerApp.createFrom factory providerProfile app
    /// |> PresentationCompose.withRenderTools
    /// |> composeAI
    /// ```
    let withRenderTools (app: AIServerApp) : AIServerApp =
        match app.Base.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let composed =
                app.AIAnsweringTools
                |> List.map (fun (definition, _) -> definition.Name)
                |> Set.ofList

            match
                RenderTools.tools
                |> List.filter (fun (definition, _) -> not (composed.Contains definition.Name))
            with
            | [] -> app
            | missing -> AIServerApp.withAIAnsweringTools missing app

    /// Compose `evaluate_pipeline` onto the AI composition in the answering
    /// form, beside the render tools, and mount the re-run a reader's panel
    /// draws its block from (`IPipelineBlockApi`, at
    /// `/api/_facts/pipeline-block/*`). Only under `EnabledFactStore`; under
    /// `NoFactStore` the app is returned unchanged.
    ///
    /// Idempotent: when the tool is already composed the app is returned
    /// unchanged, so the endpoint is mounted once. A deployment that never
    /// calls this registers no pipeline tool and mounts no endpoint. **Adds**
    /// one answering tool and one handler.
    ///
    /// ```fsharp skip=fragment
    /// AIServerApp.createFrom factory providerProfile app
    /// |> PresentationCompose.withRenderTools
    /// |> PresentationCompose.withPipelineTool
    /// |> composeAI
    /// ```
    let withPipelineTool (app: AIServerApp) : AIServerApp =
        match app.Base.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore when
            app.AIAnsweringTools
            |> List.exists (fun (definition, _) -> definition.Name = ToolUp.Facts.PipelineTool.Name)
            ->
            app
        | EnabledFactStore ->
            let mounted = {
                app with
                    Base = {
                        app.Base with
                            Extensions.Handlers = app.Base.Extensions.Handlers @ [ PipelineAnsweringTool.handler ]
                    }
            }

            AIServerApp.withAIAnsweringTools [ PipelineAnsweringTool.tool ] mounted

    /// Compose the resolution a stored page renders through: registers
    /// `PageRequestResolvers` (the resolver for a request's caller, a
    /// presentation request and a pipeline request each answered under the
    /// caller's gate) and the per-session `PageResolutionCache`, and arms the
    /// fact-table change signal the cache is invalidated by. Only under
    /// `EnabledFactStore`; under `NoFactStore` the app is returned unchanged
    /// and nothing is registered. Call it after `FactsCompose.withFactStore`,
    /// which composes the signal. **Adds** its registrations.
    ///
    /// ```fsharp skip=fragment
    /// ServerApp.empty
    /// |> FactsCompose.withFactStore
    /// |> PresentationCompose.withPageRequests
    /// |> ServerApp.run
    /// ```
    let withPageRequests (app: ServerApp) : ServerApp =
        match app.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore -> ServerApp.addContribution (Contribution.service owner PageRequestResolvers.register) app

    /// Let `evaluate_pipeline` assert a result column as facts of a registered
    /// derived metric: the tool gains the `assert` argument, and the write it
    /// may make into the fact store is declared beside its read. Composes the
    /// pipeline tool first when it is not composed (`withPipelineTool`). Only
    /// under `EnabledFactStore`; under `NoFactStore` the app is returned
    /// unchanged. A deployment that never calls this offers no `assert`
    /// argument, and a request carrying one is refused. **Sets** the composed
    /// tool's declaration and executor; idempotent.
    ///
    /// ```fsharp skip=fragment
    /// AIServerApp.createFrom factory providerProfile app
    /// |> PresentationCompose.withPipelineTool
    /// |> PresentationCompose.withDerivedAssertions
    /// |> composeAI
    /// ```
    let withDerivedAssertions (app: AIServerApp) : AIServerApp =
        match app.Base.Config.FactStore with
        | NoFactStore -> app
        | EnabledFactStore ->
            let composed = withPipelineTool app

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