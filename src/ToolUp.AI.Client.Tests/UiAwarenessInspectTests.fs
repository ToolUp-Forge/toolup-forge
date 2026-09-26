// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.UiAwarenessInspectTests

// ─── Phase 537 — `_platform.ui.inspect_active_module`, browser half ──
//
// The executor `ToolUp.AI.UiAwareness.Client.InspectActiveModule` runs,
// transpiled, exactly as the browser runs it: the live report comes from
// `ModuleStateObserver` after a real `publishState`, and the result is the
// JSON the runtime POSTs back to the agent loop. The server half (compose,
// surface filter, RBAC, authorizer seam) is pinned .NET-side in
// `ToolUp.Platform.Tests/InProcess/UiAwarenessInspectToolTests.fs`.
// Module ids are unique to this file: the observer table is a per-process
// global shared with every other pack.

open ToolUp.Platform
open ToolUp.AI.Client
open ToolUp.AI.UiAwareness
open ToolUp.AI.UiAwareness.Client
open ToolUp.AI.Client.Tests.NodeTest

let private context (activeModule: string option) (activePage: string option) : ClientToolRuntime.ClientToolContext = {
    ActiveModule = activeModule
    ActivePage = activePage
}

let private filters (region: string) : UiStateReport =
    UiStateReport.empty
    |> UiStateReport.withField "region" $"\"{region}\""
    |> UiStateReport.withField "minRevenue" "1000"
    |> UiStateReport.withSelection "rows" [ "sku-1"; "sku-2" ]
    |> UiStateReport.withAction "export" true

let private publish (moduleId: string) (report: UiStateReport) =
    ModuleStateObserver.publishState (Some(fun (_: obj) -> report)) moduleId (box ())

let private respondLive = InspectActiveModule.respond ModuleStateObserver.tryInspect

let tests =
    testList "UI-awareness inspect_active_module (Phase 537)" [
        testCase "returns the live report for the request's active module" (fun () ->
            let id = "_test537.sales"
            publish id (filters "EMEA")

            Expect.equal
                (respondLive (context (Some id) (Some "dashboard")))
                """{"moduleId":"_test537.sales","activePage":"dashboard","snapshot":{"fields":{"minRevenue":1000,"region":"EMEA"},"selections":{"rows":["sku-1","sku-2"]},"actions":{"export":true}}}"""
                "moduleId, activePage and the snapshot, with field leaves embedded as JSON"

            publish id (filters "APAC")

            Expect.equal
                (respondLive (context (Some id) None))
                """{"moduleId":"_test537.sales","activePage":null,"snapshot":{"fields":{"minRevenue":1000,"region":"APAC"},"selections":{"rows":["sku-1","sku-2"]},"actions":{"export":true}}}"""
                "a later publish is what the next call reads; an absent page is null")

        testCase "reads only the active module, never another module's state" (fun () ->
            publish "_test537.other" (filters "LATAM")
            publish "_test537.active" (filters "NA")

            let result = respondLive (context (Some "_test537.active") None)
            Expect.isTrue (result.Contains "\"NA\"") "the active module's report"
            Expect.isFalse (result.Contains "LATAM") "no other module's report")

        testCase "no active module answers no-active-module" (fun () ->
            Expect.equal
                (respondLive (context None (Some "dashboard")))
                """{"status":"no-active-module"}"""
                "no module in the request context"

            Expect.equal
                (respondLive (context (Some "  ") None))
                """{"status":"no-active-module"}"""
                "a blank module id is no module")

        testCase "a module with no observable state answers no-observable-state" (fun () ->
            Expect.equal
                (respondLive (context (Some "_test537.never-published") None))
                """{"status":"no-observable-state","moduleId":"_test537.never-published"}"""
                "never published / never declared withInspectState")

        testCase "a leaf that is not JSON is embedded as a string and strings are escaped" (fun () ->
            let report = UiStateReport.empty |> UiStateReport.withField "note" "say \"hi\"" // not a JSON leaf

            Expect.equal
                (InspectActiveModule.respond (fun _ -> Some report) (context (Some "m\"x") None))
                """{"moduleId":"m\"x","activePage":null,"snapshot":{"fields":{"note":"say \"hi\""},"selections":{},"actions":{}}}"""
                "the envelope stays valid JSON whatever the report or module id carries")

    ]