// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.UiAwareness.Server.AICompose

// ─── The UI-awareness companion's compose surface ────────
//
// Opt-in, by explicit composition: `composeAI` never calls this, so a
// deployment that does not compose the companion registers no tool and
// is byte-for-byte what it was (GP 11 / GP 13).
//
//     app |> ToolUp.AI.UiAwareness.Server.AICompose.register
//
// The client half is `ToolUp.AI.UiAwareness.Client.InspectActiveModule.install ()`
// at client boot; without it the loop's dispatch answers the model with
// the runtime's `UnknownClientTool` envelope rather than hanging.
//
// Every UI-awareness tool is one entry in `tools`; later tools append
// there and `register` picks them up with no change to its shape.

open ToolUp.Platform

/// The companion's tools, as (definition, server executor) pairs in the
/// shape `ServerApp.AITools` holds. Append new UI-awareness tools here.
let tools: (AIToolDefinition * (Microsoft.AspNetCore.Http.HttpContext -> string -> Async<string>)) list = [
    InspectActiveModuleTool.toolDefinition, InspectActiveModuleTool.serverExecutor
    ListActiveActionsTool.toolDefinition, ListActiveActionsTool.serverExecutor
]

/// **Adds** the companion's tools to the deployment's AI tools, which the AI
/// tier registers when it is composed. Idempotent: a
/// tool whose name is already registered is not appended again, so
/// composing the companion twice (two feature modules each opting in)
/// leaves one registration per tool rather than a duplicate the provider
/// would reject.
let register (app: ServerApp) : ServerApp =
    let registered = app.AITools |> List.map (fun (def, _) -> def.Name) |> Set.ofList

    let missing =
        tools |> List.filter (fun (def, _) -> not (registered.Contains def.Name))

    if List.isEmpty missing then
        app
    else
        {
            app with
                AITools = app.AITools @ missing
        }