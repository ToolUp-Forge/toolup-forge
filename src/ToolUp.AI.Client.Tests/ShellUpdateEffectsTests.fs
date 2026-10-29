// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ShellUpdateEffectsTests

// ─── Phase 1069 — the shell's `update` emits effects, it does not run them ──
//
// The shell's `update` is documented as pure; before Phase 1069 it was
// not. Five sidebar-preference arms called `SidebarPreferences.save` (an
// unguarded `localStorage.setItem`, the audit's B13), three arms
// published module state to `ModuleStateObserver` inline, and two
// published a toast through `NotificationClient.publishLocal` inline.
//
// These drive the real `Client.update` under Node (the
// `BootDegradationTests` precedent) and pin the effect boundary:
//   • a throwing store never throws out of `update` — the arm returns a
//     `Cmd`, and running it dispatches the `"sidebar-prefs"` boot row;
//   • `update` publishes nothing to observers — the post-update hook
//     (`Client.observeModuleStates`, installed through `withTrace`) does;
//   • the two toasts are `Cmd`s, published only when the effect runs.
//
// Plus the permissions-admin banner polarity (a successful exposure
// change is not an error banner), which is the same "the arm says the
// wrong thing" class.

open Fable.Core
open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

// ─── Substrate stubs ─────────────────────────────────────────────────

/// Swap in a store whose every write throws (a full quota, a private
/// window), returning the prior store so the test can put it back — the
/// `window` global is shared with every other pack in this run.
[<Emit("""(() => {
    globalThis.window = globalThis.window || {};
    const prior = globalThis.window.localStorage;
    globalThis.window.localStorage = {
        getItem: (k) => null,
        setItem: (k, v) => { throw new Error("QuotaExceededError: the stub store refuses every write"); },
        removeItem: (k) => {},
    };
    return prior;
})()""")>]
let private installThrowingStore () : obj = jsNative

[<Emit("globalThis.window.localStorage = $0")>]
let private restoreStore (prior: obj) : unit = jsNative

/// `NotificationClient.subscribe` opens the shared `EventSource`; Node has
/// none, so a do-nothing one is installed when no other pack has.
[<Emit("""(() => {
    if (typeof globalThis.EventSource === 'undefined') {
        globalThis.EventSource = class {
            constructor(url) { this.url = url; this.readyState = 0; }
            addEventListener(name, handler) {}
            close() { this.readyState = 2; }
        };
    }
})()""")>]
let private ensureEventSource () : unit = jsNative

/// Run a command's effects against a recording dispatch.
let private runCmd (cmd: Cmd<'msg>) : 'msg list =
    let dispatched = ResizeArray<'msg>()

    for effect in cmd do
        effect dispatched.Add

    List.ofSeq dispatched

// ─── Fixtures ────────────────────────────────────────────────────────

type private CounterMsg = | Increment

type private CounterModel = { Count: int }

let private counterModule (id: string) =
    ClientModule.create {
        Init = fun () -> { Count = 0 }, Cmd.none
        Update =
            fun msg m ->
                match msg with
                | Increment -> { m with Count = m.Count + 1 }, Cmd.none
        Name = id
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId id
    |> ClientModule.withActionDecoder (fun (key, _) ->
        match key with
        | "inc" -> Some Increment
        | _ -> None)
    |> ClientModule.withEventSubscription "test1069.topic" (fun _ -> Increment)
    |> ClientModule.register

let private shellModel (activeId: string) (states: (string * obj) list) : Client.Model = {
    ActiveModuleId = activeId
    ActivePageRoute = None
    ModuleStates = Map.ofList states
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

/// The five arms that change the user's sidebar arrangement.
let private sidebarArms: Client.Msg list = [
    Client.SidebarGroupToggled "Analytics"
    Client.SidebarModulePinToggled "m1"
    Client.SidebarModuleReordered("Analytics", [ "m2"; "m1" ])
    Client.SidebarModuleExpandToggled "m1"
    Client.SidebarEntryHideToggled "m3"
]

/// Record every observer publish for the named module ids. The observer
/// list is a per-process global, so ids are unique to this file.
let private recordPublishes () =
    let published = ResizeArray<string>()

    ModuleStateObserver.register (fun moduleId _ ->
        if moduleId.StartsWith "_test1069." then
            published.Add moduleId)

    published

let private permissionsModel: PermissionsAdminUI.Model = {
    ActiveTab = PermissionsAdminUI.ModulesTab
    Snapshot = PermissionsAdminUI.NotLoaded
    EditingDefaults = None
    SelectedMember = None
    EditingMembers = Map.empty
    SaveInFlight = false
    Banner = None
    OnAccessibleModulesChanged = None
}

let tests =
    testList "Shell update effects (Phase 1069)" [
        testList "sidebar preferences (B13)" [
            testCase "a throwing store never throws out of update — each arm returns a Cmd" (fun () ->
                let prior = installThrowingStore ()

                try
                    for msg in sidebarArms do
                        let model, cmd = update [] msg (shellModel "m1" [])

                        Expect.isFalse (List.isEmpty cmd) $"{msg} persists through a Cmd"

                        Expect.isTrue
                            (obj.ReferenceEquals(model.SidebarPrefs, SidebarPreferences.UserSidebarPreferences.empty)
                             |> not)
                            $"{msg} still applies the change in memory"
                finally
                    restoreStore prior)

            testCase "running the persist Cmd against a throwing store raises the sidebar-prefs boot row" (fun () ->
                let prior = installThrowingStore ()

                try
                    for msg in sidebarArms do
                        let _, cmd = update [] msg (shellModel "m1" [])

                        let failures =
                            runCmd cmd
                            |> List.choose (function
                                | Client.BootLoadFailed(source, _) -> Some source
                                | _ -> None)

                        Expect.equal failures [ "sidebar-prefs" ] $"{msg}'s failed write is a banner row, not a throw"
                finally
                    restoreStore prior)

            testCase "the sidebar-prefs row is shown without a Retry button" (fun () ->
                let model, _ =
                    update [] (Client.BootLoadFailed("sidebar-prefs", "QuotaExceededError")) (shellModel "m1" [])

                match model.Degradations with
                | [ row ] ->
                    Expect.equal row.Source "sidebar-prefs" "one row for the failed write"
                    Expect.isFalse row.Retryable "a failed write has no load to re-issue"
                    Expect.equal row.Label "Sidebar preferences" "labelled from the catalog"
                | other -> Expect.isTrue false $"expected one degradation row, got %A{other}")
        ]

        testList "module-state observer" [
            testCase "update publishes nothing; the post-update hook publishes the active module" (fun () ->
                let id = "_test1069.active"
                let erased = counterModule id
                let published = recordPublishes ()
                let before = shellModel id [ id, box { Count = 0 } ]

                let after, _ = update [ erased ] (Client.ModuleMsg(box Increment)) before
                Expect.equal (List.ofSeq published) [] "update itself publishes nothing"

                Client.observeModuleStates [ erased ] (Client.ModuleMsg(box Increment)) before.ModuleStates after
                Expect.equal (List.ofSeq published) [ id ] "the hook publishes the changed module once")

            testCase
                "the hook publishes an action target and an event subscriber, and nothing a gate dropped"
                (fun () ->
                    let target = "_test1069.target"
                    let erased = counterModule target
                    let published = recordPublishes ()
                    let before = shellModel "_test1069.elsewhere" [ target, box { Count = 0 } ]

                    let afterAction, _ =
                        update [ erased ] (Client.ModuleActionReceived(target, "inc", "")) before

                    Client.observeModuleStates
                        [ erased ]
                        (Client.ModuleActionReceived(target, "inc", ""))
                        before.ModuleStates
                        afterAction

                    let afterEvent, _ =
                        update [ erased ] (Client.ModuleEventPublished("test1069.topic", "")) afterAction

                    Client.observeModuleStates
                        [ erased ]
                        (Client.ModuleEventPublished("test1069.topic", ""))
                        afterAction.ModuleStates
                        afterEvent

                    // An action no decoder accepts leaves the state untouched.
                    let afterDropped, _ =
                        update [ erased ] (Client.ModuleActionReceived(target, "unknown", "")) afterEvent

                    Client.observeModuleStates
                        [ erased ]
                        (Client.ModuleActionReceived(target, "unknown", ""))
                        afterEvent.ModuleStates
                        afterDropped

                    Expect.equal (List.ofSeq published) [ target; target ] "two state changes, two publishes")
        ]

        testList "toasts" [
            testCase "a session-store reset toasts through a Cmd, not inline" (fun () ->
                ensureEventSource ()
                let seen = ResizeArray<NotificationEnvelope>()
                let unsubscribe = NotificationClient.subscribe seen.Add

                try
                    let notice: ProcessedDataTypes.SessionStoreResetNotification = {
                        Container = "c"
                        PreviousEpoch = None
                        CurrentEpoch = System.Guid.NewGuid()
                        Reason = ProcessedDataTypes.SessionStoreResetReasonEvicted
                    }

                    let _, cmd = update [] (Client.SessionStoreReset notice) (shellModel "m1" [])
                    Expect.equal seen.Count 0 "update publishes no toast"

                    runCmd cmd |> ignore
                    Expect.equal seen.Count 1 "the Cmd publishes it"
                finally
                    unsubscribe ())

            testCase "an action for an inactive module toasts through a Cmd, not inline" (fun () ->
                ensureEventSource ()
                let target = "_test1069.toast"
                let erased = counterModule target
                let seen = ResizeArray<NotificationEnvelope>()
                let unsubscribe = NotificationClient.subscribe seen.Add

                try
                    let before = shellModel "_test1069.elsewhere" [ target, box { Count = 0 } ]

                    let _, cmd =
                        update [ erased ] (Client.ModuleActionReceived(target, "inc", "")) before

                    Expect.equal seen.Count 0 "update publishes no toast"

                    runCmd cmd |> ignore
                    Expect.equal seen.Count 1 "the Cmd publishes the results-available toast"
                finally
                    unsubscribe ())
        ]

        testList "permissions-admin banner polarity" [
            testCase "a successful exposure change shows the non-error banner" (fun () ->
                let model, _ =
                    PermissionsAdminUI.update
                        (PermissionsAdminUI.ExposureResult(
                            "Reports",
                            ModuleExposure.Hidden,
                            ModuleExposure.Available,
                            Ok()
                        ))
                        permissionsModel

                Expect.equal (model.Banner |> Option.map fst) (Some false) "success is not an error")

            testCase "a failed exposure change shows the error banner" (fun () ->
                let model, _ =
                    PermissionsAdminUI.update
                        (PermissionsAdminUI.ExposureResult(
                            "Reports",
                            ModuleExposure.Hidden,
                            ModuleExposure.Available,
                            Error "denied"
                        ))
                        permissionsModel

                Expect.equal (model.Banner |> Option.map fst) (Some true) "failure is an error")
        ]
    ]