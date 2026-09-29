// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 910 — how much of each composition route a message re-renders,
/// measured on the transpiled code in a real React tree.
///
/// `RenderScope` measures the SDK shell alone. This measures the routes
/// Phase 852 left on the whole tree, and the shell's shared contexts:
///
///   * the AI assistant's composer (`AIClientConfig`), mounted the way
///     `AIClientConfig.withSidePanel` renders it (the whole tree per
///     message, any binding) and the way `AIClientConfig.run` renders it
///     since Phase 910 (the sliced shell over the composer's own store);
///   * the store-bound shell `Client.run` mounts, for the context readers.
///
/// Each mount is driven through the same three messages — a MODULE message
/// (changes only the active module's state), a CHROME message (opens the
/// command palette: a shell field, never an input of the sidebar or of any
/// context value), and an EXTRA message (the composer's side-panel toggle;
/// on the shell, dismissing the palette again).
///
/// Seven counters. CHROME, MODULE and SIDEBAR count as `RenderScope`
/// counts them (a `GlobalOverlays` thunk, the module's view function, the
/// catalog's `Sidebar.Reorder` formatter for one named row). FLAGS,
/// BRANDING, TILES and CATALOG count renders of four probe components the
/// module's view mounts, each reading ONE of the shell's contexts
/// (declared flag keys, branding, administration tiles, message catalog).
/// A probe sits inside the module's memo boundary, so on the store-bound
/// routes it re-renders on a chrome message only when its context's VALUE
/// changed identity — which is exactly what memoising the context values
/// per input removes.
///
/// Shared by `ComposerStoreTests` (which asserts the scope) and
/// `ClientBench` (which reports it).
module ToolUp.AI.Client.Tests.ComposerScope

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Elmish
open ToolUp.Elmish.React
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.Client

[<Import("createElement", from = "react")>]
let private createElement (elementType: obj, props: obj) : ReactElement = jsNative

[<Import("useContext", from = "react")>]
let private useContext (context: obj) : obj = jsNative

[<Emit("setTimeout($0, $1)")>]
let private after (callback: unit -> unit) (ms: int) : unit = jsNative

type private ProbeMsg = | Increment

type private ProbeModel = { Count: int }

/// Render counts at one point of a run.
type Counts = {
    Chrome: int
    Module: int
    Sidebar: int
    Flags: int
    Branding: int
    Tiles: int
    Catalog: int
}

/// The route a run mounts.
[<RequireQualifiedAccess>]
type Route =
    /// `Client.run`'s store-bound shell.
    | ShellStore
    /// The AI composer, whole tree per message (`withSidePanel`).
    | ComposerWholeTree
    /// The AI composer over its own store (`AIClientConfig.run`).
    | ComposerStore

/// One mount driven through the three messages.
type Run = {
    Route: string
    AfterMount: Counts
    AfterModuleMsg: Counts
    AfterChromeMsg: Counts
    AfterExtraMsg: Counts
    /// The module's rendered text after the module message.
    ModuleText: string
}

[<Literal>]
let ModuleId = "_scope910.counter"

[<Literal>]
let private OtherModuleId = "_scope910.other"

[<Literal>]
let private CountedRow = "Composer counter"

[<Literal>]
let private StepMs = 80

/// Total wall-clock a run takes, for a caller that waits on it.
[<Literal>]
let RunMs = 480

/// The shell model every run starts from (the one `RenderScope` uses):
/// the counter module active, its state initialised, the shell ready.
let shellModel0 () : Client.Model = {
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

/// Mutable counters for one run.
type private Counters() =
    member val Chrome = 0 with get, set
    member val Module = 0 with get, set
    member val Sidebar = 0 with get, set
    member val Flags = 0 with get, set
    member val Branding = 0 with get, set
    member val Tiles = 0 with get, set
    member val Catalog = 0 with get, set

    member c.Snapshot() : Counts = {
        Chrome = c.Chrome
        Module = c.Module
        Sidebar = c.Sidebar
        Flags = c.Flags
        Branding = c.Branding
        Tiles = c.Tiles
        Catalog = c.Catalog
    }

/// The two modules and the config every run mounts, wired to `counters`.
let private composition (counters: Counters) =
    // One probe component per context, created once per run so its
    // element type is stable across renders.
    let probe (read: unit -> unit) (bump: unit -> unit) : obj =
        box (fun (_: obj) ->
            read ()
            bump ()
            Html.none)

    let flagsProbe =
        probe (fun () -> FeatureFlags.useDeclaredKeys () |> ignore) (fun () -> counters.Flags <- counters.Flags + 1)

    let brandingProbe =
        probe (fun () -> BrandingProvider.useBranding () |> ignore) (fun () ->
            counters.Branding <- counters.Branding + 1)

    let tilesProbe =
        probe (fun () -> useContext AdminTileContext.Context |> ignore) (fun () -> counters.Tiles <- counters.Tiles + 1)

    let catalogProbe =
        probe (fun () -> MessageCatalogProvider.useMessages () |> ignore) (fun () ->
            counters.Catalog <- counters.Catalog + 1)

    let counter =
        ClientModule.create {
            Init = fun () -> { Count = 0 }, Cmd.none
            Update =
                fun msg (m: ProbeModel) ->
                    match msg with
                    | Increment -> { m with Count = m.Count + 1 }, Cmd.none
            Name = CountedRow
            Icon = Html.none
        }
        |> ClientModule.withView (fun (m: ProbeModel) _ ->
            counters.Module <- counters.Module + 1

            Html.div [
                Html.div [ prop.id "composer-module"; prop.text (sprintf "count %d" m.Count) ]
                createElement (flagsProbe, null)
                createElement (brandingProbe, null)
                createElement (tilesProbe, null)
                createElement (catalogProbe, null)
            ],
            Html.none)
        |> ClientModule.withId ModuleId
        // Rendered as a row in the narrow rail (see `RenderScope`).
        |> ClientModule.withPlacement Toolup.Sidebar.LeadingSlot
        |> ClientModule.register

    let other =
        ClientModule.create {
            Init = fun () -> { Count = 0 }, Cmd.none
            Update = fun (_: ProbeMsg) (m: ProbeModel) -> m, Cmd.none
            Name = "Composer other"
            Icon = Html.none
        }
        |> ClientModule.withView (fun (_: ProbeModel) _ -> Html.div [ prop.id "composer-other" ], Html.none)
        |> ClientModule.withId OtherModuleId
        |> ClientModule.register

    let config = {
        ClientConfig.defaults with
            ToastCentre = NoToastCentre
            GlobalOverlays = [
                fun () ->
                    counters.Chrome <- counters.Chrome + 1
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
                                            counters.Sidebar <- counters.Sidebar + 1

                                        catalog.Sidebar.Reorder rowName
                        }
                })
    }

    [ counter; other ], config

/// Mount `route` into a fresh document, drive it, and hand the counts to
/// `report` once the last step has rendered (after `RunMs`).
let run (route: Route) (report: Run -> unit) : unit =
    let placeholder =
        match route with
        | Route.ShellStore -> "composer-shell-store"
        | Route.ComposerWholeTree -> "composer-whole"
        | Route.ComposerStore -> "composer-store"

    let document = RenderScope.installDom placeholder
    let counters = Counters()
    let modules, config = composition counters
    let bus = Unchecked.defaultof<IModuleQueryBus>

    // Each route's dispatch, erased to the three messages it sends.
    let mutable send: (int -> unit) option = None

    match route with
    | Route.ShellStore ->
        let store = ModelStore.create<Client.Model, Client.Msg> ()

        let messages = [|
            Client.ModuleMsg(box Increment)
            Client.CommandPaletteOpened
            Client.CommandPaletteDismissed
        |]

        Program.mkProgram
            (fun () -> shellModel0 (), Cmd.none)
            (fun msg model -> Client.update config bus modules msg model)
            (fun model d ->
                send <- Some(fun i -> d messages[i])
                Client.viewSliced store config modules Client.emptyChrome model d)
        |> Program.withReactStore store placeholder
        |> Program.run
    | Route.ComposerWholeTree
    | Route.ComposerStore ->
        let messages = [|
            AIClientConfig.ShellMsg(Client.ModuleMsg(box Increment))
            AIClientConfig.ShellMsg Client.CommandPaletteOpened
            AIClientConfig.SidePanelMsg AIClientConfig.Toggle
        |]

        let init () : AIClientConfig.OuterModel * Cmd<AIClientConfig.OuterMsg> =
            {
                Shell = shellModel0 ()
                SidePanel = AIClientConfig.composerSidePanelInit ()
            },
            Cmd.none

        let update = AIClientConfig.composerUpdate config bus modules

        let capture shellView =
            let view = AIClientConfig.composerView NoAIAssistant modules shellView

            fun model d ->
                send <- Some(fun i -> d messages[i])
                view model d

        if route = Route.ComposerStore then
            let store = ModelStore.create<AIClientConfig.OuterModel, AIClientConfig.OuterMsg> ()

            let shell = Client.shellStore store _.Shell

            Program.mkProgram init update (capture (Client.viewSlicedOver shell config modules))
            |> Program.withReactStore store placeholder
            |> Program.run
        else
            Program.mkProgram init update (capture (Client.viewWithSignIn config modules))
            |> Program.withReactSynchronous placeholder
            |> Program.run

    let routeName =
        match route with
        | Route.ShellStore -> "shell store"
        | Route.ComposerWholeTree -> "composer whole-tree"
        | Route.ComposerStore -> "composer store"

    after
        (fun () ->
            let mounted = counters.Snapshot()
            send.Value 0

            after
                (fun () ->
                    let afterModule = counters.Snapshot()

                    let text: string =
                        match document?getElementById "composer-module" with
                        | null -> "<absent>"
                        | el -> el?textContent

                    send.Value 1

                    after
                        (fun () ->
                            let afterChrome = counters.Snapshot()
                            send.Value 2

                            after
                                (fun () ->
                                    report {
                                        Route = routeName
                                        AfterMount = mounted
                                        AfterModuleMsg = afterModule
                                        AfterChromeMsg = afterChrome
                                        AfterExtraMsg = counters.Snapshot()
                                        ModuleText = text
                                    })
                                StepMs)
                        StepMs)
                StepMs)
        StepMs

/// The per-message deltas of a run, as a plain object (the ClientBench
/// lever shape).
let deltas (r: Run) : obj =
    let delta (a: Counts) (b: Counts) =
        createObj [
            "chrome" ==> b.Chrome - a.Chrome
            "module" ==> b.Module - a.Module
            "sidebar" ==> b.Sidebar - a.Sidebar
            "flags" ==> b.Flags - a.Flags
            "branding" ==> b.Branding - a.Branding
            "tiles" ==> b.Tiles - a.Tiles
            "catalog" ==> b.Catalog - a.Catalog
        ]

    createObj [
        "moduleMessage" ==> delta r.AfterMount r.AfterModuleMsg
        "chromeMessage" ==> delta r.AfterModuleMsg r.AfterChromeMsg
        "extraMessage" ==> delta r.AfterChromeMsg r.AfterExtraMsg
    ]

/// One line per run, for a log.
let describe (r: Run) : string =
    let d (a: Counts) (b: Counts) =
        sprintf
            "%d chrome + %d module + %d sidebar; contexts flags %d branding %d tiles %d catalog %d"
            (b.Chrome - a.Chrome)
            (b.Module - a.Module)
            (b.Sidebar - a.Sidebar)
            (b.Flags - a.Flags)
            (b.Branding - a.Branding)
            (b.Tiles - a.Tiles)
            (b.Catalog - a.Catalog)

    sprintf
        "render scope (%s): module message -> %s | chrome message -> %s | extra message -> %s"
        r.Route
        (d r.AfterMount r.AfterModuleMsg)
        (d r.AfterModuleMsg r.AfterChromeMsg)
        (d r.AfterChromeMsg r.AfterExtraMsg)

/// The composition `run` mounts, for a test that mounts it itself (the
/// hydration parity case). Counts land in a fresh counter set it ignores.
let testComposition () : ErasedModule list * ClientConfig = composition (Counters())

/// The counter module's message, erased as the shell carries it.
let moduleIncrement () : obj = box Increment