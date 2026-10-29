// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Facts.Presentation

open ToolUp.Platform
open ToolUp.AI.AICompose

/// Composing the render tools (Phase 1132).
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