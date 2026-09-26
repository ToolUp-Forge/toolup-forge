// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.UiInspectionContractBindings

// ─── Phase 540 — `IUiInspectionContract` bindings ────────────────────
//
// Two providers run the pack:
//
//   1. The forge-native companion (Phase 537). Its tool is taken from
//      `ToolUp.AI.UiAwareness.Server.AICompose.tools` — the exact list
//      `AICompose.register` appends — so the definition under test is the
//      one a deployment composes, not a copy of it. Its browser executor,
//      `ToolUp.AI.UiAwareness.Client.InspectActiveModule.respond`, is Fable
//      code (it builds its answer with `JS.JSON`) and cannot run on .NET; the
//      binding answers through `answer` below, a BCL rendition of that same
//      function over the same reader. The Fable executor itself is held to
//      the same result shapes by `ToolUp.AI.Client.Tests/UiAwarenessInspectTests.fs`.
//
//   2. A reference provider declared differently on every axis the pack
//      reads from the declaration: its own name and source, and a
//      `SidePanelOnly` surface filter, so the pack's surface case is
//      exercised on a filter that EXCLUDES a surface rather than only on
//      `Both`. It answers through the same `answer`.

open System
open System.Text.Json.Nodes
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.UiAwareness
open ToolUp.AI.UiAwareness.Server
open ToolUp.Platform.Tests.Contracts
open ToolUp.Platform.Tests.Contracts.IUiInspectionContract

/// A report field's value is a JSON-encoded leaf by contract; embed it as
/// JSON, and a value that does not parse as the string it is.
let private leaf (encoded: string) : JsonNode =
    try
        match JsonNode.Parse encoded with
        | null -> JsonValue.Create(encoded: string) :> JsonNode
        | node -> node
    with _ ->
        JsonValue.Create(encoded: string) :> JsonNode

let private snapshotOf (report: UiStateReport) : JsonObject =
    let fields = JsonObject()

    for KeyValue(name, value) in report.Fields do
        fields[name] <- leaf value

    let selections = JsonObject()

    for KeyValue(name, keys) in report.Selections do
        let array = JsonArray()

        for key in keys do
            array.Add(JsonValue.Create(key: string))

        selections[name] <- array

    let actions = JsonObject()

    for KeyValue(name, enabled) in report.Actions do
        actions[name] <- JsonValue.Create(enabled: bool)

    let snapshot = JsonObject()
    snapshot["fields"] <- fields
    snapshot["selections"] <- selections
    snapshot["actions"] <- actions
    snapshot

/// The inspect answer for one invocation: the request's active module is
/// the one read (never one named in the arguments), a blank module is no
/// module, and a module with no report answers `no-observable-state`.
let answer (tryInspect: InspectReader) (invocation: InspectInvocation) : Async<string> = async {
    let result = JsonObject()

    match invocation.ActiveModule |> Option.filter (String.IsNullOrWhiteSpace >> not) with
    | None -> result["status"] <- JsonValue.Create(InspectStatus.NoActiveModule: string)
    | Some moduleId ->
        match tryInspect moduleId with
        | None ->
            result["status"] <- JsonValue.Create(InspectStatus.NoObservableState: string)
            result["moduleId"] <- JsonValue.Create(moduleId: string)
        | Some report ->
            result["moduleId"] <- JsonValue.Create(moduleId: string)

            result["activePage"] <-
                match invocation.ActivePage with
                | Some page -> JsonValue.Create(page: string) :> JsonNode
                | None -> null

            result["snapshot"] <- snapshotOf report

    return result.ToJsonString()
}

let private forgeNativeTool: AIToolRegistry.RegisteredTool =
    match
        AICompose.tools
        |> List.filter (fun (def, _) -> def.Name = InspectActiveModuleToolName)
    with
    | [ def, executor ] -> AIToolRegistry.createTool def executor
    | other ->
        failwithf "AICompose.tools must carry exactly one %s, found %d" InspectActiveModuleToolName (List.length other)

let private referenceTool: AIToolRegistry.RegisteredTool =
    AIToolRegistry.createTool
        {
            InspectActiveModuleTool.toolDefinition with
                Name = "contract.reference.inspect_active_module"
                SourceModule = "_contract.reference"
                Surface = SidePanelOnly
        }
        (fun _ _ -> async { return failwith "client-resident: the server executor must not run" })

let tests =
    testList "Phase 540 — IUiInspectionContract bindings" [
        IUiInspectionContract.tests {
            Name = "ToolUp.AI.UiAwareness (forge-native)"
            Tool = forgeNativeTool
            ClientExecutor = answer
        }
        IUiInspectionContract.tests {
            Name = "side-panel-only reference provider"
            Tool = referenceTool
            ClientExecutor = answer
        }
    ]