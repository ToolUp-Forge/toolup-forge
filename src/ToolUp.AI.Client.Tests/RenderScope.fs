// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 852 — how much of the SDK shell a message re-renders, measured on
/// the transpiled code in a real React tree.
///
/// A minimal shell composition — one counter module, the default chrome —
/// is mounted into a fresh jsdom document twice: once the way every
/// `Client.program` composer renders it (`viewWithSignIn` under
/// `withReactSynchronous`, the whole tree per message), once the way
/// `Client.run` renders it since Phase 852 (`viewSliced` under
/// `withReactStore`). Each is then driven through the same three messages:
///
///   * a MODULE message (`ModuleMsg Increment`) — changes only the active
///     module's state;
///   * a CHROME message (`CommandPaletteOpened`) — changes only a shell
///     field, never the module's state and never an input of the sidebar;
///   * a NAVIGATION message (`ModuleSelected` of the second module) —
///     changes the active module, which the sidebar highlights.
///
/// Three counters measure the scope. The CHROME counter is a
/// `ClientConfig.GlobalOverlays` thunk, which the shell invokes exactly
/// once per construction of its chrome (the overlays are built in the same
/// pass as the sidebar's inputs, the header and the providers). The MODULE
/// counter is the active module's own view function. The SIDEBAR counter
/// (Phase 883) is the catalog's `Sidebar.Reorder` formatter, supplied
/// through `ClientConfig.MessageCatalogOverride`: the sidebar component
/// calls it while rendering each reorderable row, so counting the calls
/// made for ONE named row counts the sidebar component's renders — and
/// nothing else calls it (the chrome hands the sidebar its section titles,
/// never this formatter). So the counts are "how many times did the
/// shell's chrome render", "how many times did the module's view run" and
/// "how many times did the sidebar component render" — component renders,
/// not DOM mutations.
///
/// Shared by `SlicedStoreTests` (which asserts the scope) and `ClientBench`
/// (which reports it as the Phase 852 / 883 before/after).
module ToolUp.AI.Client.Tests.RenderScope

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Elmish
open ToolUp.Elmish.React
open ToolUp.Platform

[<Import("JSDOM", from = "jsdom")>]
[<AllowNullLiteral>]
type private JSDOM(html: string, options: obj) =
    member _.window: obj = jsNative

[<Emit("Object.defineProperty(globalThis, $0, { value: $1, configurable: true, writable: true })")>]
let private defineGlobal (name: string) (value: obj) : unit = jsNative

[<Emit("setTimeout($0, $1)")>]
let private after (callback: unit -> unit) (ms: int) : unit = jsNative

let private ambientGlobals = [
    "window"
    "document"
    "HTMLElement"
    "Element"
    "Node"
    "Event"
    "MouseEvent"
    "KeyboardEvent"
    "SVGElement"
    "getComputedStyle"
    "requestAnimationFrame"
    "cancelAnimationFrame"
    "navigator"
    "localStorage"
]

/// A fresh document holding one placeholder `<div id=placeholderId>`,
/// installed as the ambient browser globals the React adapter and the
/// shell read. The act-environment flag is cleared: these mounts render on
/// React's own scheduler, exactly as a browser does, and are observed after
/// it has run (never inside `act`).
let installDom (placeholderId: string) : obj =
    let dom =
        JSDOM(
            sprintf "<!doctype html><html><body><div id=\"%s\"></div></body></html>" placeholderId,
            createObj [ "pretendToBeVisual" ==> true; "url" ==> "http://localhost/" ]
        )

    for name in ambientGlobals do
        defineGlobal name (dom.window?(name))

    defineGlobal "IS_REACT_ACT_ENVIRONMENT" false
    dom.window?document

type private ScopeMsg = | Increment

type private ScopeModel = { Count: int }

/// Render counts at one point of a run.
type ScopeCounts = {
    Chrome: int
    Module: int
    /// Phase 883 — renders of the sidebar component.
    Sidebar: int
}

/// One mount driven through a module message and then a chrome message.
type ScopeRun = {
    /// Which render path: `"whole-tree"` or `"sliced"`.
    Path: string
    AfterMount: ScopeCounts
    AfterModuleMsg: ScopeCounts
    AfterChromeMsg: ScopeCounts
    /// Phase 883 — after the navigation message.
    AfterNavigation: ScopeCounts
    /// The module's rendered text after the module message — proof the
    /// message reached the screen, not only the counters.
    ModuleText: string
}

[<Literal>]
let ModuleId = "_scope852.counter"

/// Phase 883 — the module the navigation message selects.
[<Literal>]
let OtherModuleId = "_scope883.other"

/// The display name of the row whose renders the sidebar counter counts.
[<Literal>]
let private CountedRow = "Scope counter"

[<Literal>]
let private StepMs = 80

/// Total wall-clock a run takes, for a caller that waits on it.
[<Literal>]
let RunMs = 480

/// Mount the minimal shell on the chosen path, drive it, and hand the
/// counts to `report` once the last step has rendered (after `RunMs`).
let run (sliced: bool) (report: ScopeRun -> unit) : unit =
    let placeholder = if sliced then "scope-sliced" else "scope-whole"
    let document = installDom placeholder

    let mutable chrome = 0
    let mutable moduleRenders = 0
    let mutable sidebar = 0

    let counts () = {
        Chrome = chrome
        Module = moduleRenders
        Sidebar = sidebar
    }

    let counter =
        ClientModule.create {
            Init = fun () -> { Count = 0 }, Cmd.none
            Update =
                fun msg (m: ScopeModel) ->
                    match msg with
                    | Increment -> { m with Count = m.Count + 1 }, Cmd.none
            Name = CountedRow
            Icon = Html.none
        }
        |> ClientModule.withView (fun (m: ScopeModel) _ ->
            moduleRenders <- moduleRenders + 1
            Html.div [ prop.id "scope-module"; prop.text (sprintf "count %d" m.Count) ], Html.none)
        |> ClientModule.withId ModuleId
        // Phase 883 — the counted row must render in the narrow (at-rest)
        // rail, where an undeclared module's `_other` section collapses to
        // one group icon and renders no rows. The leading slot is always
        // visible in both rail widths.
        |> ClientModule.withPlacement Toolup.Sidebar.LeadingSlot
        |> ClientModule.register

    // Phase 883 — a second entry on the rail, for the navigation message
    // to select.
    let other =
        ClientModule.create {
            Init = fun () -> { Count = 0 }, Cmd.none
            Update = fun (_: ScopeMsg) (m: ScopeModel) -> m, Cmd.none
            Name = "Scope other"
            Icon = Html.none
        }
        |> ClientModule.withView (fun (_: ScopeModel) _ -> Html.div [ prop.id "scope-other" ], Html.none)
        |> ClientModule.withId OtherModuleId
        |> ClientModule.register

    let modules = [ counter; other ]

    let config = {
        ClientConfig.defaults with
            ToastCentre = NoToastCentre
            GlobalOverlays = [
                fun () ->
                    chrome <- chrome + 1
                    Html.none
            ]
            MessageCatalogOverride =
                Some(fun catalog -> {
                    catalog with
                        Sidebar = {
                            catalog.Sidebar with
                                Reorder =
                                    fun rowName ->
                                        if rowName = CountedRow then
                                            sidebar <- sidebar + 1

                                        catalog.Sidebar.Reorder rowName
                        }
                })
    }

    let model0: Client.Model = {
        ActiveModuleId = ModuleId
        ActivePageRoute = None
        ModuleStates = Map.ofList [ ModuleId, box { Count = 0 } ]
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

    let update msg model =
        Client.update config Unchecked.defaultof<IModuleQueryBus> modules msg model

    let mutable dispatch: (Client.Msg -> unit) option = None

    let capture (view: Client.Model -> (Client.Msg -> unit) -> ReactElement) =
        fun model d ->
            dispatch <- Some d
            view model d

    let program =
        if sliced then
            let store = ModelStore.create<Client.Model, Client.Msg> ()

            Program.mkProgram
                (fun () -> model0, Cmd.none)
                update
                (capture (Client.viewSliced store config modules Client.emptyChrome))
            |> Program.withReactStore store placeholder
        else
            Program.mkProgram
                (fun () -> model0, Cmd.none)
                update
                (capture (Client.viewWithSignIn config modules Client.emptyChrome))
            |> Program.withReactSynchronous placeholder

    Program.run program

    after
        (fun () ->
            let mounted = counts ()
            dispatch.Value(Client.ModuleMsg(box Increment))

            after
                (fun () ->
                    let afterModule = counts ()

                    let text: string =
                        match document?getElementById "scope-module" with
                        | null -> "<absent>"
                        | el -> el?textContent

                    dispatch.Value Client.CommandPaletteOpened

                    after
                        (fun () ->
                            let afterChrome = counts ()
                            dispatch.Value(Client.ModuleSelected OtherModuleId)

                            after
                                (fun () ->
                                    report {
                                        Path = if sliced then "sliced" else "whole-tree"
                                        AfterMount = mounted
                                        AfterModuleMsg = afterModule
                                        AfterChromeMsg = afterChrome
                                        AfterNavigation = counts ()
                                        ModuleText = text
                                    })
                                StepMs)
                        StepMs)
                StepMs)
        StepMs