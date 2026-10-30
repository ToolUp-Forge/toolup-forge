// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open ToolUp.Platform
open ToolUp.AI.AICompose

/// Composing the render tools and the pipeline tool.
[<RequireQualifiedAccess>]
module PresentationCompose =

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