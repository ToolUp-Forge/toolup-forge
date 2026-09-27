// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.ListActiveActionsToolTests

// ─── Phase 542 — `ToolUp.AI.UiAwareness` companion, `list_active_actions` server half ──
//
// Same shape as `UiAwarenessInspectToolTests.fs` (Phase 537), for the
// narrower sibling tool. The browser executor
// (`ToolUp.AI.UiAwareness.Client.ListActiveActions`) is Fable and is pinned
// in the Fable-tier harness (`ToolUp.AI.Client.Tests/ListActiveActionsTests.fs`);
// what is pinned here is everything the agent loop decides before the
// browser is asked:
//
//   1. Compose shape — `AICompose.register` appends it alongside the
//      inspect tool, is idempotent, and an app that does not compose the
//      companion carries neither tool (GP 11 / GP 13).
//   2. The surface filter — visible on `SidePanel` AND `FullPage`.
//   3. The per-module RBAC gate — the whole `ToolGate.decide`, including the
//      read-only effect ceiling.
//   4. The Phase 14r framing recognises it as a live-interface tool.
//   5. The `IClientToolAuthorizer` seam — the Phase 46.A dispatch pack bound
//      to this tool's name, with a simulator answering in the executor's
//      result shape from the event's active module.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.AI
open ToolUp.AI.AICompose
open ToolUp.AI.UiAwareness
open ToolUp.AI.UiAwareness.Server
open ToolUp.RAG
open ToolUp.Platform.Tests.Contracts

type private StubProviderFactory() =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Error(ProviderResolutionError.NoProviderConfigured) }

        member _.TryResolveByLabel(_, _) = async { return Error(ProviderResolutionError.NoProviderConfigured) }

        member _.BuildPlatform(_providerId, _apiKey, _model) = None

type private StubProviderProfile() =
    interface IProviderProfile with
        member _.Get _ = async { return None }
        member _.Set(_, _) = async { return Ok() }
        member _.Clear _ = async { return () }
        member _.ResolveEntry(_, _, _) = async { return None }
        member _.SetEntryHealth(_, _, _) = async { return Ok() }

let private baseApp () : AIServerApp =
    AIServerApp.create (StubProviderFactory() :> IAIProviderFactory) (StubProviderProfile() :> IProviderProfile)

let private listDefs (app: AIServerApp) =
    app.Base.AITools
    |> List.map fst
    |> List.filter (fun d -> d.Name = ListActiveActionsToolName)

let private accessWith (perms: (string * ModulePermission list) list) : AccessContext = {
    AccessContext.unrestricted (AuthenticatedUser "alice") with
        ModulePermissions = Map.ofList perms
}

let private def = ListActiveActionsTool.toolDefinition

let private gate access =
    AIToolRegistry.ToolGate.decide access (fun _ -> true) AIToolRegistry.ToolPolicy.readOnly def
    |> AIToolRegistry.ToolGate.admits

let private composeTests =
    testList "compose" [
        test "register appends list_active_actions once, client-resident, read-only, parameterless" {
            let app = baseApp () |> AICompose.register

            match listDefs app with
            | [ d ] ->
                Expect.equal d.Location ClientResident "the body runs in the browser"
                Expect.equal d.Surface Both "read-only awareness is offered on both surfaces"
                Expect.equal d.SourceModule UiAwarenessSourceModule "sourced from the SDK-reserved _platform.ui"
                Expect.isEmpty d.Parameters "the module inspected is the request's, never a model argument"
                Expect.isNone d.EmitsActions "read-only: it publishes no client action"
                Expect.equal d.Effects ToolEffectDeclaration.readFacts "declares ReadFacts and nothing else"
            | other -> failtestf "expected exactly one list_active_actions tool, found %d" (List.length other)
        }

        test "register also still appends the inspect tool — both companion tools land together" {
            let app = baseApp () |> AICompose.register
            let names = app.Base.AITools |> List.map (fun (d, _) -> d.Name)
            Expect.contains names InspectActiveModuleToolName "inspect_active_module is unaffected"
            Expect.contains names ListActiveActionsToolName "list_active_actions is appended"
        }

        test "register is idempotent" {
            let once = baseApp () |> AICompose.register
            let twice = once |> AICompose.register
            Expect.equal (List.length (listDefs twice)) 1 "a second register appends nothing"
            Expect.equal (List.length twice.Base.AITools) (List.length once.Base.AITools) "tool count unchanged"
        }

        test "an app that does not compose the companion carries no list_active_actions tool" {
            Expect.isEmpty (listDefs (baseApp ())) "composeAI must not auto-wire the companion (GP 13)"
        }
    ]

let private surfaceTests =
    testList "surface filter" [
        test "visible on the side panel" {
            Expect.isTrue (AIToolRegistry.isToolVisibleOnSurface SidePanel def) "SidePanel"
        }
        test "visible on the full page" {
            Expect.isTrue (AIToolRegistry.isToolVisibleOnSurface FullPage def) "FullPage"
        }
    ]

let private rbacTests =
    testList "per-module RBAC gate (ToolGate.decide under the read-only ceiling)" [
        test "an unrestricted caller is admitted" { Expect.isTrue (gate (accessWith [])) "no permission map" }

        test "a permission map that does not name _platform.ui admits it (reserved source)" {
            Expect.isTrue (gate (accessWith [ "Sales", [ ModulePermission.Read ] ])) "reserved source exempt"
        }

        test "a permission map naming _platform.ui without Read refuses it" {
            Expect.isFalse
                (gate (accessWith [ UiAwarenessSourceModule, [] ]))
                "the deployment gated the source deliberately"
        }

        test "a permission map granting Read on _platform.ui admits it" {
            Expect.isTrue (gate (accessWith [ UiAwarenessSourceModule, [ ModulePermission.Read ] ])) "granted"
        }
    ]

let private framingTests =
    testList "Phase 14r framing" [
        test "the list_active_actions tool switches on the live-interface framing" {
            let framing = RAGPromptBuilder.ToolFraming.fromTools [ def ]
            Expect.isTrue framing.HasLiveUiTools "a pure-forge deployment's framing is now exercisable"
        }
    ]

/// Allows the tool, denies one other name — the two anchors the dispatch
/// pack needs.
type private ListActionsAuthorizer() =
    interface IClientToolAuthorizer with
        member _.Authorize(toolName, _argsJson, _activeModule, _activePage) =
            if toolName = ListActiveActionsToolName then
                Allow
            else
                Deny $"ui-awareness test policy denied '{toolName}'"

/// Stands in for the browser executor: answers in its result shape, for
/// the event's active module.
let private listActionsSimulator (evt: AIStreamEvent) : string option =
    match evt with
    | ClientToolInvoke(_, _, _, _, Some moduleId, _) ->
        Some $"""{{"moduleId":"{moduleId}","actions":[{{"id":"export","enabled":true}}]}}"""
    | ClientToolInvoke _ -> Some $"""{{"status":"{InspectStatus.NoActiveModule}"}}"""
    | _ -> None

let private dispatchBinding =
    IClientToolDispatchContract.tests {
        Name = "ToolUp.AI.UiAwareness (list_active_actions)"
        Authorizer = ListActionsAuthorizer() :> IClientToolAuthorizer
        AllowedToolName = ListActiveActionsToolName
        DeniedToolName = "_platform.ui.denied_probe"
        Simulator = listActionsSimulator
    }

let tests =
    testList "Phase 542 — ToolUp.AI.UiAwareness list_active_actions" [
        composeTests
        surfaceTests
        rbacTests
        framingTests
        dispatchBinding
    ]