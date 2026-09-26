// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ListActiveActionsTests

// ─── Phase 542 — `_platform.ui.list_active_actions`, browser half ────
//
// The executor `ToolUp.AI.UiAwareness.Client.ListActiveActions` runs,
// transpiled, exactly as the browser runs it: the live report comes from
// `ModuleStateObserver` after a real `publishState`, and the result is the
// JSON the runtime POSTs back to the agent loop. The server half (compose,
// surface filter, RBAC, authorizer seam) is pinned .NET-side in
// `ToolUp.Platform.Tests/InProcess/ListActiveActionsToolTests.fs`.
// Module ids are unique to this file: the observer table is a per-process
// global shared with every other pack.

open ToolUp.Platform
open ToolUp.AI.Client
open ToolUp.AI.UiAwareness
open ToolUp.AI.UiAwareness.Client
open ToolUp.AI.Client.Tests.NodeTest

let private context (activeModule: string option) : ClientToolRuntime.ClientToolContext = {
    ActiveModule = activeModule
    ActivePage = None
}

let private publish (moduleId: string) (report: UiStateReport) =
    ModuleStateObserver.publishState (Some(fun (_: obj) -> report)) moduleId (box ())

let private respondLive = ListActiveActions.respond ModuleStateObserver.tryInspect

let tests =
    testList "UI-awareness list_active_actions (Phase 542)" [
        testCase "reports enabled and disabled actions accurately" (fun () ->
            let id = "_test542.sales"

            let report =
                UiStateReport.empty
                |> UiStateReport.withField "region" "\"EMEA\"" // present in the report; not in this tool's answer
                |> UiStateReport.withAction "export" true
                |> UiStateReport.withAction "delete" false

            publish id report

            Expect.equal
                (respondLive (context (Some id)))
                """{"moduleId":"_test542.sales","actions":[{"id":"delete","enabled":false},{"id":"export","enabled":true}]}"""
                "moduleId and every action's enabled state, sorted by key, with no field/selection leakage")

        testCase "empty when the module declares no inspectable actions" (fun () ->
            let id = "_test542.no-actions"
            publish id (UiStateReport.empty |> UiStateReport.withField "region" "\"EMEA\"")

            Expect.equal
                (respondLive (context (Some id)))
                """{"moduleId":"_test542.no-actions","actions":[]}"""
                "an empty actions list is a real answer, not omitted")

        testCase "reads only the active module, never another module's actions" (fun () ->
            publish "_test542.other" (UiStateReport.empty |> UiStateReport.withAction "other-only-action" true)
            publish "_test542.active" (UiStateReport.empty |> UiStateReport.withAction "export" true)

            let result = respondLive (context (Some "_test542.active"))
            Expect.isTrue (result.Contains "\"export\"") "the active module's actions"
            Expect.isFalse (result.Contains "other-only-action") "no other module's actions")

        testCase "no active module answers no-active-module" (fun () ->
            Expect.equal
                (respondLive (context None))
                """{"status":"no-active-module"}"""
                "no module in the request context"

            Expect.equal
                (respondLive (context (Some "  ")))
                """{"status":"no-active-module"}"""
                "a blank module id is no module")

        testCase "a module with no observable state answers no-observable-state" (fun () ->
            Expect.equal
                (respondLive (context (Some "_test542.never-published")))
                """{"status":"no-observable-state","moduleId":"_test542.never-published"}"""
                "never published / never declared withInspectState")

    ]