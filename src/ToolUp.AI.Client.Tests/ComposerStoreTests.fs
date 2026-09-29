// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ComposerStoreTests

// ─── Phase 910 — the sliced store reaches every composer ────────────────
//
// Pinned on the transpiled code, in a real React 19 tree in jsdom:
//
//   910.A  the HMR module passes both store bindings through, and a hot
//          reload (the bundler's dispose, then the next build's run) keeps
//          the store binding and its slices: the reloaded build re-renders
//          by slice, and the torn-down build publishes nothing.
//   910.B  hydration parity: the store-bound shell (`Hydration.run`'s
//          prerendered branch) adopts the markup the whole-tree shell
//          renders, and then updates it in place.
//   910.C  the AI composer hands the shell a stable dispatch, so the
//          shell's two dispatch caches hit; and over its own store it
//          re-renders only the slice a message moved.
//   910.D  the chrome's context values are memoised per input: a chrome
//          message re-renders no reader of flags, branding, tiles or the
//          catalog.

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Elmish
open ToolUp.Elmish.React
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.Client
open ToolUp.AI.Client.Tests.NodeTest
open ToolUp.AI.Client.Tests.RenderScope

[<Import("createElement", from = "react")>]
let private createElement (elementType: obj, props: obj) : ReactElement = jsNative

[<Import("memo", from = "react")>]
let private memo (render: obj) : obj = jsNative

[<Import("renderToString", from = "react-dom/server")>]
let private renderToString (element: ReactElement) : string = jsNative

[<Emit("Object.defineProperty(globalThis, $0, { value: $1, configurable: true, writable: true })")>]
let private defineGlobal (name: string) (value: obj) : unit = jsNative

[<Emit("delete globalThis[$0]")>]
let private deleteGlobal (name: string) : unit = jsNative

[<Emit("globalThis[$0]")>]
let private readGlobal (name: string) : obj = jsNative

/// A variadic JS function handing `sink` all its arguments joined — how a
/// React warning's format string and its substitutions (the diff) arrive.
[<Emit("(function () { $0(Array.from(arguments).map(function (a) { return String(a); }).join(' ')); })")>]
let private allArguments (sink: string -> unit) : obj = jsNative

// ─── 910.A — HMR pass-throughs ─────────────────────────────────────────

type private PairMsg =
    | BumpA
    | BumpB

let private hmrTests =
    testList "910.A - HMR keeps the store binding and its slices" [

        testCaseDeferred "a hot reload keeps the store binding and its slices" 600
        <| fun () ->
            let document = installDom "hmr-store"

            // A bundler HMR API (webpack's `module.hot` shape), so the HMR
            // module takes its hot-reload branch exactly as a dev build does.
            let disposers = ResizeArray<unit -> unit>()

            defineGlobal
                "module"
                (createObj [
                    "hot"
                    ==> createObj [ "dispose" ==> (fun (callback: unit -> unit) -> disposers.Add callback) ]
                ])

            let readerRenders = Array.create 4 0 // build 1 A, build 1 B, build 2 A, build 2 B

            let build (index: int) (store: ModelStore<int * int, PairMsg>) (capture: (PairMsg -> unit) -> unit) =
                // Each reader reads ONE half of the model through the store.
                let reader (slot: int) (select: int * int -> int) : obj =
                    memo (
                        box (fun (_: obj) ->
                            let value = ModelStore.useSelector store select ModelStore.refEquals
                            readerRenders[slot] <- readerRenders[slot] + 1
                            Html.span [ prop.className (sprintf "half-%d" slot); prop.text (string value) ])
                    )

                let readerA = reader (2 * (index - 1)) fst
                let readerB = reader (2 * (index - 1) + 1) snd

                Program.mkProgram
                    (fun () -> (0, 0), Cmd.none)
                    (fun msg (a, b) ->
                        match msg with
                        | BumpA -> (a + 1, b), Cmd.none
                        | BumpB -> (a, b + 1), Cmd.none)
                    (fun _ d ->
                        capture d

                        Html.div [
                            Html.p [ prop.id "hmr-build"; prop.text (sprintf "build %d" index) ]
                            createElement (readerA, null)
                            createElement (readerB, null)
                        ])
                |> ToolUp.Elmish.HMR.Program.withReactStore store "hmr-store"
                |> ToolUp.Elmish.HMR.Program.run

            let store1 = ModelStore.create<int * int, PairMsg> ()
            let store2 = ModelStore.create<int * int, PairMsg> ()
            let mutable dispatch1: (PairMsg -> unit) option = None
            let mutable dispatch2: (PairMsg -> unit) option = None
            let marks = ResizeArray<int array>()
            let mutable hmrBranch = false

            build 1 store1 (fun d -> dispatch1 <- Some d)
            hmrBranch <- disposers.Count = 1

            JS.setTimeout
                (fun () ->
                    dispatch1.Value BumpA

                    JS.setTimeout
                        (fun () ->
                            // The hot replace: the bundler calls the build's
                            // dispose callback, then evaluates the next build.
                            for dispose in List.ofSeq disposers do
                                dispose ()

                            build 2 store2 (fun d -> dispatch2 <- Some d)

                            JS.setTimeout
                                (fun () ->
                                    marks.Add(Array.copy readerRenders)
                                    dispatch2.Value BumpA
                                    // The torn-down build's dispatch is inert.
                                    dispatch1.Value BumpB

                                    JS.setTimeout (fun () -> marks.Add(Array.copy readerRenders)) 80 |> ignore)
                                80
                            |> ignore)
                        80
                    |> ignore)
                80
            |> ignore

            fun () ->
                deleteGlobal "module"
                Expect.isTrue hmrBranch "the HMR module took its hot-reload branch (it registered a dispose)"

                Expect.equal
                    ((document?getElementById "hmr-build")?textContent: string)
                    "build 2"
                    "the reloaded build is on screen"

                let afterReload = marks[0]
                let afterMessage = marks[1]
                Expect.equal (afterReload[2], afterReload[3]) (1, 1) "the reloaded build's readers mounted once each"

                Expect.equal
                    (afterMessage[2] - afterReload[2], afterMessage[3] - afterReload[3])
                    (1, 0)
                    "a reloaded-build message re-rendered the reader of the slice it moved, and not the other"

                Expect.equal store2.Snapshot.Value.Model (1, 0) "the reloaded build's store holds its model"

                Expect.equal
                    store1.Snapshot.Value.Model
                    (1, 0)
                    "the torn-down build's store published nothing after the reload"
    ]

// ─── 910.B — hydration parity ──────────────────────────────────────────

/// What one hydrating mount did.
type private Hydrated = {
    /// Hydration warnings, with dnd-kit's describedby counter normalised:
    /// that id comes from a module-level counter, so a server render and a
    /// client render in ONE process (this harness) always differ by it —
    /// on either render path; in a browser the server's process is not the
    /// client's. Normalised, the reports compare the TREES.
    Reports: string list
    AdoptedRoot: bool
    AdoptedModule: bool
    /// The module's node is still the server's after a module message.
    PatchedInPlace: bool
    TextAfter: string
}

[<Emit("$0.replace(/DndDescribedBy-[0-9]+/g, 'DndDescribedBy-N')")>]
let private normaliseDndIds (text: string) : string = jsNative

/// Render the whole-tree shell for `init`'s model to a string (what the
/// prerender pass emits), then hydrate it on the chosen path and send one
/// module message.
let private hydrate (sliced: bool) (report: Hydrated -> unit) : unit =
    let placeholder = if sliced then "hydrate-sliced" else "hydrate-whole"
    let document = installDom placeholder
    let modules, config = ComposerScope.testComposition ()
    let model0 = ComposerScope.shellModel0 ()
    let host = document?getElementById placeholder
    host?innerHTML <- renderToString (Client.viewWithSignIn config modules Client.emptyChrome model0 ignore)
    let serverModule = document?getElementById "composer-module"
    let serverFirst = host?firstChild

    let reported = ResizeArray<string>()
    let console: obj = readGlobal "console"
    let priorConsoleError = console?error

    console?error <-
        allArguments (fun text ->
            if text.Contains "ydrat" then
                reported.Add(normaliseDndIds text))

    let mutable dispatch: (Client.Msg -> unit) option = None

    let capture view =
        fun model d ->
            dispatch <- Some d
            view model d

    let update msg model =
        Client.update config Unchecked.defaultof<IModuleQueryBus> modules msg model

    if sliced then
        let store = ModelStore.create<Client.Model, Client.Msg> ()

        Program.mkProgram
            (fun () -> model0, Cmd.none)
            update
            (capture (Client.viewSliced store config modules Client.emptyChrome))
        |> Program.withReactStoreHydrate store placeholder
        |> Program.run
    else
        Program.mkProgram
            (fun () -> model0, Cmd.none)
            update
            (capture (Client.viewWithSignIn config modules Client.emptyChrome))
        |> Program.withReactHydrate placeholder
        |> Program.run

    JS.setTimeout
        (fun () ->
            let adoptedModule =
                obj.ReferenceEquals(document?getElementById "composer-module", serverModule)

            let adoptedRoot = obj.ReferenceEquals(host?firstChild, serverFirst)
            dispatch.Value(Client.ModuleMsg(ComposerScope.moduleIncrement ()))

            JS.setTimeout
                (fun () ->
                    console?error <- priorConsoleError
                    let now = document?getElementById "composer-module"

                    report {
                        Reports = List.ofSeq reported
                        AdoptedRoot = adoptedRoot
                        AdoptedModule = adoptedModule
                        PatchedInPlace = obj.ReferenceEquals(now, serverModule)
                        TextAfter = if isNull now then "<absent>" else now?textContent
                    })
                80
            |> ignore)
        100
    |> ignore

/// True when a hydration warning's diff differs ONLY by dnd-kit's
/// describedby counter: after normalising, its `+` lines and its `-` lines
/// are the same lines.
let private onlyDndIdsDiffer (report: string) : bool =
    // The diff follows the warning's explanation (whose bullets also begin
    // with `-`), after its link.
    let marker = "hydration-mismatch"
    let at = report.IndexOf marker

    let diff =
        if at < 0 then
            report
        else
            report.Substring(at + marker.Length)

    let lines = diff.Split('\n')

    let side (marker: char) =
        lines
        |> Array.filter (fun l -> l.Length > 0 && l[0] = marker)
        |> Array.map (fun l -> l.Substring(1).Trim())
        |> Array.sort
        |> List.ofArray

    let added = side '+'
    not added.IsEmpty && added = side '-'

let private hydrationTests =
    testList "910.B - the store-bound shell hydrates the whole-tree markup" [

        testCaseDeferred
            "the store-bound shell adopts the whole-tree shell's server markup and updates it in place"
            1000
        <| fun () ->
            let mutable whole: Hydrated option = None
            let mutable sliced: Hydrated option = None

            // One after the other: each installs its own document. The
            // whole-tree mount is the route `Hydration.run` took before
            // Phase 910 (`Program.withReactHydrate`); it is measured and
            // printed here; Phase 931's 931.B asserts that it adopts too.
            hydrate false (fun w ->
                whole <- Some w
                hydrate true (fun s -> sliced <- Some s))

            fun () ->
                let w = whole.Value
                let s = sliced.Value

                printfn
                    "[910] hydration: whole-tree binding adopted root %b module %b, %d warning(s); store binding adopted root %b module %b, %d warning(s)"
                    w.AdoptedRoot
                    w.AdoptedModule
                    w.Reports.Length
                    s.AdoptedRoot
                    s.AdoptedModule
                    s.Reports.Length

                Expect.equal
                    (s.AdoptedRoot, s.AdoptedModule)
                    (true, true)
                    "the store-bound shell adopts the server's nodes"

                Expect.equal
                    (s.Reports |> List.filter (onlyDndIdsDiffer >> not))
                    []
                    "and hydration reports no mismatch (beyond dnd-kit's process-wide id counter)"

                Expect.isTrue s.PatchedInPlace "a module message then patched the server's node in place"
                Expect.equal s.TextAfter "count 1" "with the new state"
    ]

// ─── 910.C — the AI composer ───────────────────────────────────────────

let private composerTests =
    let mutable whole: ComposerScope.Run option = None
    let mutable stored: ComposerScope.Run option = None

    testList "910.C - the AI composer hands the shell a stable dispatch and adopts the store" [

        testCase "the composer's shell dispatch is the same function every render of one loop"
        <| fun () ->
            let loop1: AIClientConfig.OuterMsg -> unit = fun _ -> ()
            let loop2: AIClientConfig.OuterMsg -> unit = fun _ -> ()
            let first = AIClientConfig.composerDispatchersFor loop1
            let again = AIClientConfig.composerDispatchersFor loop1

            Expect.isTrue (obj.ReferenceEquals(first.Shell, again.Shell)) "the shell dispatch is stable"
            Expect.isTrue (obj.ReferenceEquals(first.SidePanel, again.SidePanel)) "the side-panel dispatch is stable"

            Expect.isTrue
                (obj.ReferenceEquals(Client.moduleDispatchFor first.Shell, Client.moduleDispatchFor again.Shell))
                "so the shell's module-dispatch cache hits"

            Expect.isTrue
                (obj.ReferenceEquals(Client.sidebarDispatchersFor first.Shell, Client.sidebarDispatchersFor again.Shell))
                "and so does its sidebar-callback cache"

            let other = AIClientConfig.composerDispatchersFor loop2
            Expect.isFalse (obj.ReferenceEquals(first.Shell, other.Shell)) "a different loop gets its own"

        testCaseDeferred
            "whole tree (withSidePanel): the sidebar boundary holds for a message it does not read"
            (ComposerScope.RunMs + 100)
        <| fun () ->
            ComposerScope.run ComposerScope.Route.ComposerWholeTree (fun r -> whole <- Some r)

            fun () ->
                let r = whole.Value
                printfn "[910] %s" (ComposerScope.describe r)
                Expect.equal r.ModuleText "count 1" "the module message reached the screen"
                Expect.equal (r.AfterModuleMsg.Chrome - r.AfterMount.Chrome) 1 "the whole tree re-renders the chrome"
                Expect.equal (r.AfterModuleMsg.Module - r.AfterMount.Module) 1 "and the module"

                Expect.equal
                    (r.AfterModuleMsg.Sidebar - r.AfterMount.Sidebar)
                    0
                    "but NOT the sidebar for a module message (its callbacks are the loop's own)"

                Expect.equal (r.AfterChromeMsg.Sidebar - r.AfterModuleMsg.Sidebar) 0 "nor for a chrome message"

                Expect.equal (r.AfterExtraMsg.Sidebar - r.AfterChromeMsg.Sidebar) 0 "nor for a side-panel message"

        testCaseDeferred
            "over its store (run): each message re-renders only the slice it moved"
            (ComposerScope.RunMs + 100)
        <| fun () ->
            ComposerScope.run ComposerScope.Route.ComposerStore (fun r -> stored <- Some r)

            fun () ->
                let r = stored.Value
                printfn "[910] %s" (ComposerScope.describe r)

                Expect.equal
                    (r.AfterMount.Chrome, r.AfterMount.Module)
                    (1, 1)
                    "one chrome and one module render to mount"

                Expect.equal r.ModuleText "count 1" "the module message reached the screen"

                Expect.equal
                    (r.AfterModuleMsg.Chrome - r.AfterMount.Chrome,
                     r.AfterModuleMsg.Module - r.AfterMount.Module,
                     r.AfterModuleMsg.Sidebar - r.AfterMount.Sidebar)
                    (0, 1, 0)
                    "a module message: the module, and no chrome or sidebar"

                Expect.equal
                    (r.AfterChromeMsg.Chrome - r.AfterModuleMsg.Chrome,
                     r.AfterChromeMsg.Module - r.AfterModuleMsg.Module,
                     r.AfterChromeMsg.Sidebar - r.AfterModuleMsg.Sidebar)
                    (1, 0, 0)
                    "a chrome message: the chrome, and no module or sidebar"

                Expect.equal
                    (r.AfterExtraMsg.Chrome - r.AfterChromeMsg.Chrome,
                     r.AfterExtraMsg.Module - r.AfterChromeMsg.Module,
                     r.AfterExtraMsg.Sidebar - r.AfterChromeMsg.Sidebar)
                    (1, 0, 0)
                    "a side-panel message: the chrome (it carries the panel), and no module or sidebar"
    ]

// ─── 910.D — the chrome's context values ───────────────────────────────

let private contextTests =
    let mutable shell: ComposerScope.Run option = None
    let mutable composer: ComposerScope.Run option = None

    let noContextRenders (label: string) (a: ComposerScope.Counts) (b: ComposerScope.Counts) =
        Expect.equal
            (b.Flags - a.Flags, b.Branding - a.Branding, b.Tiles - a.Tiles, b.Catalog - a.Catalog)
            (0, 0, 0, 0)
            (sprintf "%s re-rendered no reader of flags, branding, tiles or the catalog" label)

    testList "910.D - a chrome message re-renders no context reader" [

        testCaseDeferred "the store-bound shell (Client.run)" (ComposerScope.RunMs + 100)
        <| fun () ->
            ComposerScope.run ComposerScope.Route.ShellStore (fun r -> shell <- Some r)

            fun () ->
                let r = shell.Value
                printfn "[910] %s" (ComposerScope.describe r)

                Expect.equal
                    (r.AfterMount.Flags, r.AfterMount.Branding, r.AfterMount.Tiles, r.AfterMount.Catalog)
                    (1, 1, 1, 1)
                    "each reader rendered once to mount"

                Expect.equal
                    (r.AfterChromeMsg.Chrome - r.AfterModuleMsg.Chrome)
                    1
                    "the chrome message rendered the chrome"

                noContextRenders "the chrome message" r.AfterModuleMsg r.AfterChromeMsg
                noContextRenders "dismissing the palette" r.AfterChromeMsg r.AfterExtraMsg

        testCaseDeferred "the AI composer over its store (AIClientConfig.run)" (ComposerScope.RunMs + 100)
        <| fun () ->
            ComposerScope.run ComposerScope.Route.ComposerStore (fun r -> composer <- Some r)

            fun () ->
                let r = composer.Value
                noContextRenders "the chrome message" r.AfterModuleMsg r.AfterChromeMsg
                noContextRenders "the side-panel message" r.AfterChromeMsg r.AfterExtraMsg
    ]

// ─── Phase 931 — AI deployments wire the shell dispatcher ───────────────
//
//   931.A  in a shell `AIClientConfig.run` composed and mounted, a module's
//          `OnTeamSwitched` and `OnAccessibleModulesChanged` callbacks
//          reach the shell's update (and stop reaching it once the program
//          is torn down).
//   931.B  the whole-tree hydrating binding (`Program.withReactHydrate`)
//          adopts the server's markup, as the store binding does.
//   931.C  the conversation panel's fact link, mounted: shown only when the
//          fact browse module is composed, and a click publishes the fact
//          and requests the browse module's rows page.

type private CallbackMsg = | NoOp

type private CallbackModel = { Seen: int }

/// What the mounted AI shell reported for the two callbacks.
type private CallbackRun = {
    /// The probe module was initialised with a context carrying both.
    HadCallbacks: bool
    /// Shell messages the composed update traced after each invocation.
    TeamSwitchTraced: bool
    AccessibleModulesTraced: bool
    /// Shell messages traced after the program was torn down.
    TracedAfterTeardown: int
    Traced: string list
}

[<Emit("new $0($1)")>]
let private newEvent (ctor: obj) (kind: string) : obj = jsNative

/// Mount the AI composer exactly as a deployment does (`AIClientConfig.run`,
/// at `elmish-app`) over a team-scoped config, invoke the two callbacks a
/// module was handed, and report what reached the composed update. The
/// update's own trace (`EnableElmishConsoleTrace`) is the observation: it
/// logs every message the outer loop processes.
let private runAIShell (report: CallbackRun -> unit) : unit =
    installDom "elmish-app" |> ignore

    let traced = ResizeArray<string>()
    let console: obj = readGlobal "console"
    let priorLog = console?log

    console?log <-
        allArguments (fun text ->
            if text.Contains "outerMsg=" then
                traced.Add text)

    let mutable context: ClientModuleContext option = None

    let probe =
        ClientModule.create {
            Init = fun () -> { Seen = 0 }, Cmd.none
            Update = fun (_: CallbackMsg) (m: CallbackModel) -> m, Cmd.none
            Name = "Callback probe"
            Icon = Html.none
        }
        |> ClientModule.withContextInit (fun ctx ->
            context <- Some ctx
            { Seen = 0 }, Cmd.none)
        |> ClientModule.withView (fun (_: CallbackModel) _ -> Html.div [ prop.id "callback-probe" ], Html.none)
        |> ClientModule.withId "_scope931.probe"
        // The boot validators refuse a module list with no declared group.
        |> ClientModule.withGroup "Probe"
        |> ClientModule.register

    let config = {
        ClientConfig.defaults with
            Surfaces = Surfaces.team
            ActiveModule = Some "_scope931.probe"
            ToastCentre = NoToastCentre
            EnableElmishConsoleTrace = true
    }

    AIClientConfig.run NoAIAssistant config [ probe ]

    let invokeBoth () =
        match context with
        | Some ctx ->
            ctx.OnTeamSwitched |> Option.iter (fun switch -> switch "team-931")
            ctx.OnAccessibleModulesChanged |> Option.iter (fun changed -> changed ())
        | None -> ()

    let hadCallbacks =
        match context with
        | Some ctx -> ctx.OnTeamSwitched.IsSome && ctx.OnAccessibleModulesChanged.IsSome
        | None -> false

    let tracedBefore = traced.Count
    invokeBoth ()

    JS.setTimeout
        (fun () ->
            let afterInvoke = traced |> Seq.skip tracedBefore |> List.ofSeq
            let window: obj = readGlobal "window"
            // The React binding tears the program down on `beforeunload`.
            window?dispatchEvent (newEvent (window?Event) "beforeunload") |> ignore
            let tracedAtTeardown = traced.Count
            invokeBoth ()

            JS.setTimeout
                (fun () ->
                    console?log <- priorLog

                    report {
                        HadCallbacks = hadCallbacks
                        TeamSwitchTraced = afterInvoke |> List.exists _.Contains("TeamSwitched")
                        AccessibleModulesTraced = afterInvoke |> List.exists _.Contains("RefreshAccessibleModules")
                        TracedAfterTeardown = traced.Count - tracedAtTeardown
                        Traced = afterInvoke
                    })
                60
            |> ignore)
        100
    |> ignore

let private dispatcherTests =
    testList "931.A - an AI-composed shell wires the shell dispatcher" [

        testCaseDeferred "a team switch and an accessible-modules change each reach the composed shell" 600
        <| fun () ->
            let mutable result: CallbackRun option = None
            runAIShell (fun r -> result <- Some r)

            fun () ->
                let r = result.Value
                printfn "[931] traced after the callbacks: %A" r.Traced
                Expect.isTrue r.HadCallbacks "a team-scoped module is handed both callbacks"
                Expect.isTrue r.TeamSwitchTraced "OnTeamSwitched dispatched TeamSwitched into the composed shell"

                Expect.isTrue
                    r.AccessibleModulesTraced
                    "OnAccessibleModulesChanged dispatched RefreshAccessibleModules into the composed shell"

                Expect.equal r.TracedAfterTeardown 0 "and neither reaches a torn-down program"
    ]

let private wholeTreeHydrationTests =
    testList "931.B - the whole-tree hydrating binding adopts the server markup" [

        testCaseDeferred "Program.withReactHydrate adopts the server's nodes and updates them in place" 600
        <| fun () ->
            let mutable whole: Hydrated option = None
            hydrate false (fun w -> whole <- Some w)

            fun () ->
                let w = whole.Value

                Expect.equal
                    (w.AdoptedRoot, w.AdoptedModule)
                    (true, true)
                    "the whole-tree binding adopts the server's nodes"

                Expect.equal
                    (w.Reports |> List.filter (onlyDndIdsDiffer >> not))
                    []
                    "and hydration reports no mismatch (beyond dnd-kit's process-wide id counter)"

                Expect.isTrue w.PatchedInPlace "a module message then patched the server's node in place"
                Expect.equal w.TextAfter "count 1" "with the new state"
    ]

// 931.C — the fact link. `MessageSources` keeps its expanded state in a
// hook, so the link is reachable only in a mounted tree: expand, then click.

[<Import("createRoot", from = "react-dom/client")>]
let private createRoot (container: obj) : obj = jsNative

let private factSource: VectorKnowledgeTypes.RetrievedSource = {
    DocumentId = ""
    DocumentName = "Elasticity by region"
    Snippet = "UK elasticity is -1.4"
    Score = 0.9
    Origin = VectorKnowledgeTypes.ChunkOrigin.Fact
    LocationHint = None
    OriginalRef = None
    Scope = None
    ChunkId = None
    FactId = Some "fact-931"
    FactRendering = Some "UK elasticity is -1.4"
    FactFreshness = None
    FactSupersededBy = None
    Span = None
}

type private FactLinkRun = {
    LinkShown: bool
    Navigated: string list
    Published: (string * string) list
}

[<Emit("Array.from($0.querySelectorAll('button')).find(function (b) { return b.textContent.indexOf($1) >= 0; }) || null")>]
let private buttonWithText (host: obj) (text: string) : obj = jsNative

/// Mount the sources footer for one fact source with the browse module
/// composed or not, expand it, click the fact link when there is one, and
/// report what reached the two buses.
let private mountFactLink (composed: bool) (report: FactLinkRun -> unit) : unit =
    let document = installDom "fact-link"
    let host = document?getElementById "fact-link"
    let before = RegisteredModules.snapshot ()

    let browseEntry: RegisteredModules.ModuleEntry = {
        ModuleId = VectorKnowledgeTypes.FactBrowseLinks.ModuleId
        ModuleName = "Facts"
        PageRoutes = []
    }

    RegisteredModules.publish (if composed then [ browseEntry ] else [])

    let navigated = ResizeArray<string>()
    let published = ResizeArray<string * string>()
    let stopNavigation = NavigationRequest.subscribe navigated.Add

    let stopEvents =
        ModuleEvents.subscribe (fun topic payload -> published.Add(topic, payload))

    let root = createRoot host

    root?render (ConversationPanel.MessageSources [ factSource ] "UK elasticity is -1.4")
    |> ignore

    let finish linkShown =
        stopNavigation ()
        stopEvents ()
        RegisteredModules.publish before
        root?unmount () |> ignore

        report {
            LinkShown = linkShown
            Navigated = List.ofSeq navigated
            Published = List.ofSeq published
        }

    JS.setTimeout
        (fun () ->
            let toggle = buttonWithText host "Sources ("

            if not (isNull toggle) then
                toggle?click () |> ignore

            JS.setTimeout
                (fun () ->
                    let link = buttonWithText host "Open fact row"

                    if isNull link then
                        finish false
                    else
                        link?click () |> ignore
                        finish true)
                50
            |> ignore)
        50
    |> ignore

let private factLinkTests =
    testList "931.C - the conversation panel's fact link, mounted" [

        testCaseDeferred "with the fact browse module composed, the link opens the fact's row" 400
        <| fun () ->
            let mutable result: FactLinkRun option = None
            mountFactLink true (fun r -> result <- Some r)

            fun () ->
                let r = result.Value
                Expect.isTrue r.LinkShown "the expanded fact source offers the link"

                Expect.equal
                    r.Published
                    [ VectorKnowledgeTypes.FactBrowseLinks.OpenFactTopic, "fact-931" ]
                    "a click publishes the cited fact on the event bus"

                Expect.equal
                    r.Navigated
                    [
                        VectorKnowledgeTypes.FactBrowseLinks.sidebarId VectorKnowledgeTypes.FactBrowseLinks.RowsRoute
                    ]
                    "and asks the shell to open the browse module's rows page"

        testCaseDeferred "without the module, the expanded fact source offers no link" 400
        <| fun () ->
            let mutable result: FactLinkRun option = None
            mountFactLink false (fun r -> result <- Some r)

            fun () ->
                let r = result.Value
                Expect.isFalse r.LinkShown "no link to a page the deployment does not have"
                Expect.equal (r.Navigated, r.Published) ([], []) "and nothing reaches either bus"
    ]

/// Every Phase 931 case.
let tests931 =
    testList "Phase 931 - AI deployments wire the shell dispatcher" [
        dispatcherTests
        wholeTreeHydrationTests
        factLinkTests
    ]

let tests =
    testList "Phase 910 - the sliced store reaches every composer" [
        hmrTests
        hydrationTests
        composerTests
        contextTests
    ]