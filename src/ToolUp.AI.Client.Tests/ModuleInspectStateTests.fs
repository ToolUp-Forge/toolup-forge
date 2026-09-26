// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ModuleInspectStateTests

// ─── Phase 536 — native module inspect-state contract ───────────────
//
// A module declares its live UI state as a pure `'Model -> UiStateReport`
// projection (`ClientModule.withInspectState`); the shell records the
// latest report at its `ModuleStateObserver` publish points and a reader
// retrieves it with `ModuleStateObserver.tryInspect`.
//
// The shell `update` is driven directly with a hand-built `Client.Model`
// (the `BootDegradationTests` precedent) so the assertions pin the real
// publish points — `ModuleMsg` and `ModuleActionReceived` — rather than a
// re-statement of them. Module ids are unique to this file because the
// observer table is a per-process global shared with every other pack.
// Report maps are compared as `Map.toList`: node's deep-equal walks a
// transpiled `FSharpMap`'s comparer closures, so two equal maps built
// on different paths compare unequal.

open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

type private CounterMsg =
    | Increment
    | Select of string

type private CounterModel = { Count: int; Selected: string option }

let private project (m: CounterModel) : UiStateReport =
    UiStateReport.empty
    |> UiStateReport.withField "count" (string m.Count)
    |> UiStateReport.withSelection "row" (Option.toList m.Selected)
    |> UiStateReport.withAction "reset" (m.Count > 0)

let private counterModule (id: string) =
    ClientModule.create {
        Init = fun () -> { Count = 0; Selected = None }, Cmd.none
        Update =
            fun msg m ->
                match msg with
                | Increment -> { m with Count = m.Count + 1 }, Cmd.none
                | Select key -> { m with Selected = Some key }, Cmd.none
        Name = id
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId id
    |> ClientModule.withActionDecoder (fun (key, payload) ->
        match key with
        | "select" -> Some(Select payload)
        | _ -> None)

let private shellModel (activeId: string) (state: obj) : Client.Model = {
    ActiveModuleId = activeId
    ActivePageRoute = None
    ModuleStates = Map.ofList [ activeId, state ]
    AccessibleModules = None
    ShowAllModules = false
    ModuleConfigs = Map.empty
    PlatformConfig = Map.empty
    ResolvedFlags = Map.empty
    SidebarPrefs = SidebarPreferences.UserSidebarPreferences.empty
    ProcessedData = []
    PrefetchedProcessedData = []
    MyTeams = []
    ActiveTeamId = None
    ActiveTeamLoadCompleted = false
    PlatformRole = None
    ActiveTeamRole = None
    CurrentArea = ModuleArea.Product
    ConfigsPrefetch = Prefetch.none
    FlagsPrefetch = Prefetch.none
    ResetCounters = Map.empty
    InitPhase = Client.Ready
    Degradations = []
    CommandPalette = CommandPaletteNav.closed
    LocaleOverride = None
}

let private update (modules: ErasedModule list) msg model =
    Client.update ClientConfig.defaults Unchecked.defaultof<IModuleQueryBus> modules msg model

let tests =
    testList "Module inspect-state (Phase 536)" [
        testCase "a module with withInspectState publishes an up-to-date report after a ModuleMsg update" (fun () ->
            let id = "_test536.inspected"

            let erased =
                counterModule id
                |> ClientModule.withInspectState project
                |> ClientModule.register

            let initial = shellModel id (box { Count = 0; Selected = None })

            Expect.isTrue (ModuleStateObserver.tryInspect id).IsNone "nothing is reported before the first update"

            let afterOne, _ = update [ erased ] (Client.ModuleMsg(box Increment)) initial
            let report1 = ModuleStateObserver.tryInspect id

            Expect.equal
                (report1 |> Option.map (_.Fields >> Map.toList))
                (Some [ "count", "1" ])
                "the report projects the post-update model"

            Expect.equal
                (report1 |> Option.map (_.Actions >> Map.toList))
                (Some [ "reset", true ])
                "the action state follows the model"

            let _, _ = update [ erased ] (Client.ModuleMsg(box Increment)) afterOne

            Expect.equal
                (ModuleStateObserver.tryInspect id |> Option.map (_.Fields >> Map.toList))
                (Some [ "count", "2" ])
                "a second update replaces the report — the reader sees the latest state")

        testCase "a server-emitted ModuleActionReceived also refreshes the report" (fun () ->
            let id = "_test536.action"

            let erased =
                counterModule id
                |> ClientModule.withInspectState project
                |> ClientModule.register

            let initial = shellModel id (box { Count = 0; Selected = None })

            let _, _ =
                update [ erased ] (Client.ModuleActionReceived(id, "select", "sku-42")) initial

            Expect.equal
                (ModuleStateObserver.tryInspect id |> Option.map (_.Selections >> Map.toList))
                (Some [ "row", [ "sku-42" ] ])
                "the decoded action's state change is reported")

        testCase "a module without withInspectState reports no observable state" (fun () ->
            let id = "_test536.plain"
            let erased = counterModule id |> ClientModule.register

            Expect.isTrue erased.InspectState.IsNone "the default registration declares no projector"

            let initial = shellModel id (box { Count = 0; Selected = None })
            let _, _ = update [ erased ] (Client.ModuleMsg(box Increment)) initial

            Expect.isTrue
                (ModuleStateObserver.tryInspect id).IsNone
                "an update of an undeclared module records nothing — no observable state")

        testCase "a throwing projector reads as no observable state and never breaks the update" (fun () ->
            let id = "_test536.throwing"

            let erased =
                counterModule id
                |> ClientModule.withInspectState (fun _ -> failwith "projector bug")
                |> ClientModule.register

            let initial = shellModel id (box { Count = 0; Selected = None })
            let next, _ = update [ erased ] (Client.ModuleMsg(box Increment)) initial

            Expect.equal
                (next.ModuleStates |> Map.find id |> unbox<CounterModel> |> _.Count)
                1
                "the shell's update is unaffected by the projector"

            Expect.isTrue (ModuleStateObserver.tryInspect id).IsNone "the failed projection is reported as absent")
    ]