// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.BrowserHistoryTests

// ─── Phase 1240 — browser history and a URL per view ───────────────────
//
// These drive the real `Client.init` and `Client.update` under Node against a
// small model of the browser's session history, because what matters is what
// happens to that history: how many entries a navigation adds, that a Back
// adds none, and what a URL restores. The stub fires `popstate` on `back` and
// `forward` as a browser does, after the URL has moved. It also holds what
// outlives a reload in one tab, `sessionStorage` and the token (Phase 1241).

open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

// ─── The browser ─────────────────────────────────────────────────────

/// Install a session history holding one entry, with an empty token store so
/// the boot path does not depend on what another pack in this run stored.
/// `window` is shared with every other pack, so `uninstall` puts it back.
/// `storageFailure` makes `sessionStorage` refuse: `quota` throws from each call,
/// `denied` throws from the property itself, as a sandboxed page does.
[<Emit("""(() => {
    globalThis.window = globalThis.window || {};
    const w = globalThis.window;
    const browser = {
        entries: [$0], index: 0, pushes: 0, replaces: 0, listeners: [],
        token: null, session: new Map(), storageCalls: 0, storageFailure: null,
        saved: { location: w.location, history: w.history, add: w.addEventListener, remove: w.removeEventListener, store: w.localStorage, session: Object.getOwnPropertyDescriptor(w, 'sessionStorage') },
        hash() { return browser.entries[browser.index]; },
        go(by) { browser.index += by; browser.listeners.slice().forEach(l => l({})); },
        storedViews() { return Array.from(browser.session.values()); },
    };
    const call = (body) => {
        browser.storageCalls += 1;
        if (browser.storageFailure === 'quota') throw new Error('quota exceeded');
        return body();
    };
    const session = {
        getItem: (k) => call(() => browser.session.has(k) ? browser.session.get(k) : null),
        setItem: (k, v) => call(() => { browser.session.set(k, v); }),
        removeItem: (k) => call(() => { browser.session.delete(k); }),
    };
    Object.defineProperty(w, 'sessionStorage', {
        configurable: true,
        get() {
            if (browser.storageFailure === 'denied') throw new Error('storage denied');
            return session;
        },
    });
    w.location = { get hash() { return browser.hash(); } };
    w.history = {
        state: null,
        pushState(state, title, url) {
            browser.entries = browser.entries.slice(0, browser.index + 1);
            browser.entries.push(url);
            browser.index += 1;
            browser.pushes += 1;
        },
        replaceState(state, title, url) { browser.entries[browser.index] = url; browser.replaces += 1; },
    };
    w.addEventListener = (name, l) => { if (name === 'popstate') browser.listeners.push(l); };
    w.removeEventListener = (name, l) => { if (name === 'popstate') browser.listeners = browser.listeners.filter(x => x !== l); };
    w.localStorage = { getItem: (k) => k === 'toolup-auth-token' ? browser.token : null, setItem: (k, v) => {}, removeItem: (k) => {} };
    return browser;
})()""")>]
let private install (hash: string) : obj = jsNative

[<Emit("""(() => {
    const w = globalThis.window, s = $0.saved;
    w.location = s.location; w.history = s.history; w.addEventListener = s.add; w.removeEventListener = s.remove; w.localStorage = s.store;
    if (s.session) Object.defineProperty(w, 'sessionStorage', s.session); else delete w.sessionStorage;
})()""")>]
let private uninstall (browser: obj) : unit = jsNative

let private withBrowser (hash: string) (body: obj -> unit) : unit =
    let browser = install hash

    try
        body browser
    finally
        uninstall browser

let private hash (browser: obj) : string = browser?hash ()
let private entryCount (browser: obj) : int = browser?entries?length
let private pushes (browser: obj) : int = browser?pushes
let private replaces (browser: obj) : int = browser?replaces
let private listenerCount (browser: obj) : int = browser?listeners?length
let private back (browser: obj) : unit = browser?go (-1)
let private forward (browser: obj) : unit = browser?go (1)
let private storedViews (browser: obj) : string list = List.ofArray (browser?storedViews ())
let private storageCalls (browser: obj) : int = browser?storageCalls
let private signIn (browser: obj) : unit = browser?token <- "a-token"

/// A reload of `/`: the one entry left has no fragment, and the JavaScript
/// holding the shell is gone, while `sessionStorage` and the token remain.
let private reloadOntoRoot (browser: obj) : unit =
    browser?entries <- [| "" |]
    browser?index <- 0

// ─── The shell ───────────────────────────────────────────────────────

let private bus = Unchecked.defaultof<IModuleQueryBus>

let private on = {
    ClientConfig.defaults with
        Shell.BrowserHistory = HashHistory
}

let private singlePage (id: string) (inits: ResizeArray<string>) =
    ClientModule.create {
        Init =
            fun () ->
                inits.Add id
                0, Cmd.none
        Update = fun (_: unit) m -> m, Cmd.none
        Name = id
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> PageContent.SplitPanel(Html.none, Html.none))
    |> ClientModule.withId id
    |> ClientModule.register

let private page (route: string) : PageConfig * (int -> RenderContext<unit> -> PageContent) =
    {
        Route = route
        Title = route
        Icon = Html.none
    },
    (fun _ _ -> Custom Html.none)

let private multiPage (id: string) =
    ClientModule.create {
        Init = fun () -> 0, Cmd.none
        Update = fun (_: unit) m -> m, Cmd.none
        Name = id
        Icon = Html.none
    }
    |> ClientModule.withPages [ page "/first"; page "/second" ]
    |> ClientModule.withId id
    |> ClientModule.register

let private adminOnly (id: string) (inits: ResizeArray<string>) =
    ClientModule.create {
        Init =
            fun () ->
                inits.Add id
                0, Cmd.none
        Update = fun (_: unit) m -> m, Cmd.none
        Name = id
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> PageContent.SplitPanel(Html.none, Html.none))
    |> ClientModule.withId id
    |> ClientModule.withNavRole NavRole.PlatformAdminOnly
    |> ClientModule.register

let private fixture () =
    let inits = ResizeArray<string>()

    inits,
    [
        singlePage "home" inits
        singlePage "reports" inits
        multiPage "paged"
        adminOnly "secret" inits
    ]

let private boot (config: ClientConfig) (modules: ErasedModule list) : Client.Model =
    fst (Client.init config bus modules ())

/// The write `init` appends is its last command; the ones before it are the
/// boot fetches, which these tests have no server for.
let private runFirstEntryWrite (config: ClientConfig) (modules: ErasedModule list) : unit =
    let _, cmd = Client.init config bus modules ()
    (List.last cmd) ignore

/// A running shell: `init`, then an elmish loop over `update`, with the shell's
/// own `browser-history` effect started on it.
type private Running = {
    Model: unit -> Client.Model
    Dispatch: Client.Msg -> unit
    Stop: unit -> unit
}

let private run (config: ClientConfig) (modules: ErasedModule list) : Running =
    runFirstEntryWrite config modules
    let mutable model = boot config modules

    let rec dispatch (msg: Client.Msg) =
        let next, cmd = Client.update config bus modules msg model
        model <- next

        for effect in cmd do
            effect dispatch

    let subscription =
        (Client.programLifetimeEffects config
         |> List.find (fun e -> e.Id = "browser-history"))
            .Start
            dispatch

    {
        Model = fun () -> model
        Dispatch = dispatch
        Stop = subscription.Dispose
    }

let private activeView (shell: Running) =
    let model = shell.Model()
    BrowserHistory.viewId model.ActiveModuleId model.ActivePageRoute

/// The configs and flags pair arriving, which promotes a shell waiting on them.
let private settle (config: ClientConfig) (modules: ErasedModule list) (model: Client.Model) =
    let waiting = {
        model with
            InitPhase = Client.Prefetching
            Hosts = Map.empty
    }

    let configs, _ =
        Client.update config bus modules (Client.ConfigsLoaded Map.empty) waiting

    fst (Client.update config bus modules (Client.FlagsLoaded Map.empty) configs)

/// One `update`, running only the command the shell's history adds, which is its
/// last: the ones before it are fetches these tests have no server for.
let private updateAndWrite (config: ClientConfig) (modules: ErasedModule list) (msg: Client.Msg) (model: Client.Model) =
    let next, cmd = Client.update config bus modules msg model
    (List.last cmd) ignore
    next

/// Stand-in for a composer's outer message type (`OuterMsg.ShellMsg`).
type private Outer = Wrap of Client.Msg

let tests =
    testList "Browser history (Phase 1240)" [
        testList "multi-page routes" [
            testCase "a Composition page opens from its sidebar entry" (fun () ->
                let inspector = CompositionInspectorUI.create None
                let modules = [ singlePage "home" (ResizeArray()); inspector ]
                let model = boot ClientConfig.defaults modules

                for page in inspector.Definition.Pages do
                    let sidebarId = $"{inspector.Definition.Id}{page.Route}"

                    let next, _ =
                        Client.update ClientConfig.defaults bus modules (Client.ModuleSelected sidebarId) model

                    Expect.equal next.ActiveModuleId inspector.Definition.Id $"{page.Title}: the module opens"
                    Expect.equal next.ActivePageRoute (Some page.Route) $"{page.Title}: on the page clicked"

                    Expect.isTrue
                        (inspector.PageViews.Value.ContainsKey page.Route)
                        $"{page.Title}: and the route has a view")

            testCase "a route the sidebar could not address is refused at registration" (fun () ->
                let refused =
                    try
                        ClientModule.create {
                            Init = fun () -> 0, Cmd.none
                            Update = fun (_: unit) m -> m, Cmd.none
                            Name = "unaddressable"
                            Icon = Html.none
                        }
                        |> ClientModule.withPages [ page "/first"; page "second" ]
                        |> ClientModule.withId "unaddressable"
                        |> ClientModule.register
                        |> ignore

                        None
                    with e ->
                        Some e.Message

                Expect.isFalse refused.IsNone "registration fails"
                Expect.isTrue (refused.Value.Contains "second") "and names the route")

            testCase "a lone page may keep a bare route, which nothing has to address" (fun () ->
                let registered =
                    ClientModule.create {
                        Init = fun () -> 0, Cmd.none
                        Update = fun (_: unit) m -> m, Cmd.none
                        Name = "lone"
                        Icon = Html.none
                    }
                    |> ClientModule.withPages [ page "only" ]
                    |> ClientModule.withId "lone"
                    |> ClientModule.register

                Expect.isTrue registered.PageViews.IsSome "registers as before")
        ]

        testList "the URL" [
            testCase "a fragment round-trips a view's sidebar id"
            <| fun () ->
                for id in [ "reports"; "paged/second"; "_sdk.TeamManager"; "a b/c d" ] do
                    Expect.equal (BrowserHistory.parseHash (BrowserHistory.formatHash id)) (Some id) id

            testCase "a fragment that does not name a view is not read as one"
            <| fun () ->
                for fragment in [ ""; "#"; "#/"; "#section"; "#/%E0%A4%A" ] do
                    Expect.isNone (BrowserHistory.parseHash fragment) $"'{fragment}'"

            testCase "an id is checked against the registered modules"
            <| fun () ->
                let entries: RegisteredModules.ModuleEntry list = [
                    {
                        ModuleId = "single"
                        ModuleName = "Single"
                        PageRoutes = []
                    }
                    {
                        ModuleId = "paged"
                        ModuleName = "Paged"
                        PageRoutes = [ "/first"; "/second" ]
                    }
                ]

                let resolve = BrowserHistory.resolve entries
                Expect.equal (resolve "single") (Some "single") "a single-page module"
                Expect.equal (resolve "paged/second") (Some "paged/second") "a declared page"
                Expect.equal (resolve "paged") (Some "paged/first") "a bare id lands on the first page"
                Expect.equal (resolve "paged/gone") (Some "paged/first") "so does an undeclared page"
                Expect.equal (resolve "single/gone") (Some "single") "a single-page module has no pages to name"
                Expect.isNone (resolve "gone") "an unknown module is refused"
                Expect.isNone (resolve Toolup.Sidebar.AdminAreaId) "and so is a reserved area id"
        ]

        testList "push" [
            testCase "a navigation adds one history entry"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    let shell = run on modules

                    Expect.equal (hash browser) "#/home" "the first entry names its view"
                    Expect.equal (entryCount browser) 1 "without adding an entry"

                    shell.Dispatch(Client.ModuleSelected "reports")
                    Expect.equal (hash browser) "#/reports" "the URL names the view"
                    Expect.equal (entryCount browser) 2 "one entry added"

                    shell.Dispatch(Client.ModuleSelected "paged/second")
                    Expect.equal (hash browser) "#/paged/second" "a page has its own URL"
                    Expect.equal (entryCount browser) 3 "one more entry"

                    shell.Stop())

            testCase "selecting the view already showing adds nothing"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    let shell = run on modules

                    shell.Dispatch(Client.ModuleSelected "reports")
                    shell.Dispatch(Client.ModuleSelected "reports")

                    Expect.equal (pushes browser) 1 "one view change, one entry"
                    shell.Stop())
        ]

        testList "pop" [
            testCase "Back and Forward move between views without adding entries"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    let shell = run on modules

                    shell.Dispatch(Client.ModuleSelected "reports")
                    shell.Dispatch(Client.ModuleSelected "paged/second")
                    Expect.equal (pushes browser) 2 "two navigations"

                    back browser
                    Expect.equal (activeView shell) "reports" "Back returns to the previous view"

                    back browser
                    Expect.equal (activeView shell) "home" "and to the first one"

                    forward browser
                    Expect.equal (activeView shell) "reports" "Forward moves on again"

                    forward browser
                    Expect.equal (activeView shell) "paged/second" "including to a page"

                    Expect.equal (pushes browser) 2 "the moves pushed nothing"
                    Expect.equal (entryCount browser) 3 "and the forward entries survive"

                    shell.Stop())

            testCase "a URL no module owns moves nothing"
            <| fun () ->
                withBrowser "#/gone" (fun browser ->
                    let _, modules = fixture ()
                    let shell = run on modules
                    Expect.equal (activeView shell) "home" "an unknown id boots the default view"

                    browser?entries?push ("#/also-gone")
                    forward browser

                    Expect.equal (activeView shell) "home" "and an unknown id on a pop is ignored"
                    Expect.equal (hash browser) "#/also-gone" "the URL it could not read is left alone"
                    shell.Stop())

            testCase "stopping the effect removes its listener"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    let shell = run on modules
                    Expect.equal (listenerCount browser) 1 "listening"
                    shell.Stop()
                    Expect.equal (listenerCount browser) 0 "no longer listening")
        ]

        testList "restore on boot" [
            testCase "a URL restores its view"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let inits, modules = fixture ()
                    let model = boot on modules

                    Expect.equal model.ActiveModuleId "reports" "the URL's module"
                    Expect.isTrue (model.Hosts |> Map.containsKey "reports") "initialised"
                    Expect.equal (List.ofSeq inits) [ "reports" ] "and only it")

            testCase "a URL restores a page, and the first mount keeps it"
            <| fun () ->
                withBrowser "#/paged/second" (fun _ ->
                    let _, modules = fixture ()

                    let restored = boot on modules
                    Expect.equal restored.ActivePageRoute (Some "/second") "restored"

                    let settled = settle on modules restored
                    Expect.equal settled.InitPhase Client.Ready "promoted"
                    Expect.equal settled.ActivePageRoute (Some "/second") "the first mount keeps the page"

                    // The same promotion without the option lands on the first
                    // page, so the assertion above is measuring the change.
                    let unchanged = settle ClientConfig.defaults modules restored
                    Expect.equal unchanged.ActivePageRoute (Some "/first") "the default still resets")

            testCase "a bare module URL is corrected to the page it landed on"
            <| fun () ->
                withBrowser "#/paged" (fun browser ->
                    let _, modules = fixture ()
                    let shell = run on modules

                    Expect.equal (activeView shell) "paged/first" "the first page"
                    Expect.equal (hash browser) "#/paged/first" "the URL says so"
                    Expect.equal (entryCount browser) 1 "without an entry"
                    shell.Stop())

            testCase "a URL cannot reach a view its reader may not see"
            <| fun () ->
                withBrowser "#/secret" (fun _ ->
                    let inits, modules = fixture ()
                    let model = boot on modules

                    Expect.equal model.ActiveModuleId "secret" "the selection moves, so the denial can name it"
                    Expect.isEmpty (Map.toList model.Hosts) "the module holds no state"
                    Expect.isEmpty (List.ofSeq inits) "and its Init never ran")

            testCase "a Back onto such a view is refused the same way"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let inits, modules = fixture ()
                    let shell = run on modules
                    inits.Clear()

                    browser?entries?push ("#/secret")
                    forward browser

                    Expect.equal (activeView shell) "secret" "the selection moves"
                    Expect.isFalse ((shell.Model()).Hosts |> Map.containsKey "secret") "no state"
                    Expect.isEmpty (List.ofSeq inits) "no Init"
                    shell.Stop())
        ]

        testList "the no-active-team gate" [
            let gated = {
                on with
                    Surfaces = Surfaces.team
                    Chrome.NoActiveTeamLanding = ChromeFill.On(NoActiveTeamLandingFill.Module "landing")
            }

            let gatedModules () =
                let inits = ResizeArray<string>()

                [
                    singlePage "home" inits
                    singlePage "landing" inits
                    singlePage "reports" inits
                ]

            testCase "a restored view waits for the team, and stays when one arrives"
            <| fun () ->
                withBrowser "#/reports" (fun _ ->
                    let modules = gatedModules ()

                    // The race the hold exists for: the configs and flags pair
                    // promotes the shell before the active-team fetch answers.
                    let waiting = {
                        boot gated modules with
                            InitPhase = Client.Prefetching
                            Hosts = Map.empty
                    }

                    let configs, _ =
                        Client.update gated bus modules (Client.ConfigsLoaded Map.empty) waiting

                    let promoted, _ =
                        Client.update gated bus modules (Client.FlagsLoaded Map.empty) configs

                    Expect.equal promoted.InitPhase Client.Ready "promoted with the team still unknown"
                    Expect.equal promoted.ActiveModuleId "reports" "the view is held, not sent to the landing"
                    Expect.isFalse (promoted.Hosts |> Map.containsKey "reports") "the guard holds its Init back"

                    let withTeam, _ =
                        Client.update gated bus modules (Client.ActiveTeamLoaded(Some "team-1")) promoted

                    Expect.equal withTeam.ActiveModuleId "reports" "the URL's view stands"
                    Expect.isTrue (withTeam.Hosts |> Map.containsKey "reports") "and is mounted now")

            testCase "a redirect replaces the entry rather than adding one"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let modules = gatedModules ()
                    let model = boot gated modules

                    let noTeam, cmd =
                        Client.update gated bus modules (Client.ActiveTeamLoaded None) model

                    Expect.equal noTeam.ActiveModuleId "landing" "no team, so the gate forces the landing"

                    (List.last cmd) ignore

                    Expect.equal (hash browser) "#/landing" "the URL follows"
                    Expect.equal (pushes browser) 0 "no entry for Back to bounce off"
                    Expect.equal (replaces browser) 1 "the entry is replaced")
        ]

        testList "across sign-in (Phase 1241)" [
            let secured = { on with Surfaces = Surfaces.team }

            testCase "a signed-out reader on a view signs in and lands on it"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let _, modules = fixture ()
                    let signedOut = run secured modules
                    Expect.equal (activeView signedOut) "reports" "the URL's view"
                    Expect.equal (storedViews browser) [ "reports" ] "kept for the trip"
                    signedOut.Stop()

                    signIn browser
                    reloadOntoRoot browser

                    let signedIn = run secured modules
                    Expect.equal (activeView signedIn) "reports" "the view returns"
                    Expect.equal (hash browser) "#/reports" "and the URL names it again"
                    Expect.equal (entryCount browser) 1 "without an entry"
                    Expect.isEmpty (storedViews browser) "nothing is left kept"
                    signedIn.Stop())

            testCase "the kept view follows the reader while signed out"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let _, modules = fixture ()
                    let signedOut = run secured modules

                    signedOut.Dispatch(Client.ModuleSelected "paged/second")
                    Expect.equal (storedViews browser) [ "paged/second" ] "a navigation moves it"

                    back browser
                    Expect.equal (storedViews browser) [ "reports" ] "and so does a Back"

                    forward browser
                    signedOut.Stop()

                    signIn browser
                    reloadOntoRoot browser

                    let signedIn = run secured modules
                    Expect.equal (activeView signedIn) "paged/second" "the page they were on returns"
                    signedIn.Stop())

            testCase "a signed-out boot with no view in its URL keeps what is being carried"
            <| fun () ->
                // The issuer's callback: the shell boots signed out with no
                // fragment, shows its default view, and the token arrives just
                // before the reload onto `/`.
                withBrowser "#/reports" (fun browser ->
                    let _, modules = fixture ()
                    (run secured modules).Stop()
                    reloadOntoRoot browser

                    let callback = run secured modules
                    Expect.equal (activeView callback) "home" "signed out, nothing is taken"
                    Expect.equal (storedViews browser) [ "reports" ] "and nothing is overwritten"

                    signIn browser

                    updateAndWrite secured modules Client.AuthTokenAcquired (callback.Model())
                    |> ignore

                    Expect.equal (storedViews browser) [ "reports" ] "the token arriving leaves it too"
                    callback.Stop()

                    reloadOntoRoot browser
                    let signedIn = run secured modules
                    Expect.equal (activeView signedIn) "reports" "so the trip still lands on it"
                    signedIn.Stop())

            testCase "a signed-out boot on no view has nothing to keep"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    let signedOut = run secured modules
                    Expect.equal (activeView signedOut) "home" "the default view"
                    Expect.isEmpty (storedViews browser) "not kept, so the default stays the default"
                    signedOut.Stop())

            testCase "a sign-in that does not leave the page carries nothing"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let _, modules = fixture ()
                    let signedOut = run secured modules
                    signIn browser

                    let signedIn =
                        updateAndWrite secured modules Client.AuthTokenAcquired (signedOut.Model())

                    Expect.equal signedIn.ActiveModuleId "reports" "the reader stays"
                    Expect.isEmpty (storedViews browser) "and the kept view goes"
                    signedOut.Stop())

            testCase "a navigation while signed in keeps nothing"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    signIn browser

                    boot secured modules
                    |> updateAndWrite secured modules (Client.ModuleSelected "reports")
                    |> ignore

                    Expect.equal (hash browser) "#/reports" "the URL still moves"
                    Expect.isEmpty (storedViews browser) "nothing is stored")

            testCase "a URL view wins over a kept one, which is removed"
            <| fun () ->
                withBrowser "#/paged/second" (fun browser ->
                    let _, modules = fixture ()
                    BrowserHistory.store "reports"
                    signIn browser

                    let shell = run secured modules
                    Expect.equal (activeView shell) "paged/second" "the URL's view"
                    Expect.isEmpty (storedViews browser) "the kept one cannot resurface"
                    shell.Stop())

            testCase "a kept view is taken once"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    BrowserHistory.store "reports"
                    signIn browser

                    let first = run secured modules
                    Expect.equal (activeView first) "reports" "the first visit takes it"
                    first.Stop()

                    reloadOntoRoot browser
                    let second = run secured modules
                    Expect.equal (activeView second) "home" "a later visit to `/` lands on the default"
                    second.Stop())

            testCase "a kept unknown id is ignored, and removed"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    BrowserHistory.store "gone"
                    signIn browser

                    let shell = run secured modules
                    Expect.equal (activeView shell) "home" "the default view"
                    Expect.isEmpty (storedViews browser) "it does not linger"
                    shell.Stop())

            testCase "a kept undeclared page falls back to the module's first page"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let _, modules = fixture ()
                    BrowserHistory.store "paged/gone"
                    signIn browser

                    let shell = run secured modules
                    Expect.equal (activeView shell) "paged/first" "the same check a URL gets"
                    Expect.equal (hash browser) "#/paged/first" "and the URL says so"
                    shell.Stop())

            testCase "a kept view its reader may not see renders the denial, with no Init"
            <| fun () ->
                withBrowser "" (fun browser ->
                    let inits, modules = fixture ()
                    BrowserHistory.store "secret"
                    signIn browser

                    let settled = settle secured modules (boot secured modules)

                    Expect.equal settled.InitPhase Client.Ready "promoted"
                    Expect.equal settled.ActiveModuleId "secret" "the selection moves, so the denial can name it"
                    Expect.isFalse (settled.Hosts |> Map.containsKey "secret") "the module holds no state"
                    Expect.isFalse (inits.Contains "secret") "and its Init never ran")

            for failure in [ "quota"; "denied" ] do
                testCase $"a store that refuses ({failure}) changes nothing else"
                <| fun () ->
                    withBrowser "#/reports" (fun browser ->
                        let _, modules = fixture ()
                        browser?storageFailure <- failure

                        let signedOut = run secured modules
                        Expect.equal (activeView signedOut) "reports" "the URL still restores"

                        signedOut.Dispatch(Client.ModuleSelected "paged/second")
                        Expect.equal (activeView signedOut) "paged/second" "a navigation still happens"
                        Expect.equal (hash browser) "#/paged/second" "and the URL follows"

                        signIn browser

                        updateAndWrite secured modules Client.AuthTokenAcquired (signedOut.Model())
                        |> ignore

                        signedOut.Stop()
                        reloadOntoRoot browser

                        let signedIn = run secured modules
                        Expect.equal (activeView signedIn) "home" "the view is simply not carried"
                        signedIn.Stop())
        ]

        testList "the default" [
            testCase "sessionStorage is not touched"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let _, modules = fixture ()

                    let defaults = {
                        ClientConfig.defaults with
                            Surfaces = Surfaces.team
                    }

                    let walk () =
                        let next, cmd =
                            Client.update
                                defaults
                                bus
                                modules
                                (Client.ModuleSelected "paged/second")
                                (boot defaults modules)

                        for effect in cmd do
                            effect ignore

                        Client.update defaults bus modules Client.AuthTokenAcquired next |> ignore

                    // Signed out `init` issues no fetch, so all of its commands can run.
                    for effect in snd (Client.init defaults bus modules ()) do
                        effect ignore

                    walk ()
                    signIn browser
                    reloadOntoRoot browser
                    walk ()

                    Expect.equal (storageCalls browser) 0 "signed out or signed in")

            testCase "an anonymous deployment does not touch sessionStorage either"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let _, modules = fixture ()
                    let shell = run on modules
                    shell.Dispatch(Client.ModuleSelected "paged/second")
                    shell.Stop()

                    Expect.equal (storageCalls browser) 0 "no sign-in, so nothing to carry")

            testCase "the URL is neither read nor written"
            <| fun () ->
                withBrowser "#/reports" (fun browser ->
                    let _, modules = fixture ()
                    let defaults = ClientConfig.defaults

                    let model = boot defaults modules
                    Expect.equal model.ActiveModuleId "home" "the URL chooses nothing"

                    let next, cmd =
                        Client.update defaults bus modules (Client.ModuleSelected "paged/second") model

                    for effect in cmd do
                        effect ignore

                    Expect.equal next.ActiveModuleId "paged" "the navigation still happens"
                    Expect.equal (pushes browser) 0 "no entry"
                    Expect.equal (replaces browser) 0 "no replace"
                    Expect.equal (hash browser) "#/reports" "the URL is as it was")

            testCase "init emits no write"
            <| fun () ->
                withBrowser "" (fun _ ->
                    let inits = ResizeArray<string>()
                    let modules = [ singlePage "home" inits ]

                    // Signed out on an authenticated surface `init` issues no
                    // boot fetch, so its command is exactly what was added.
                    let config = {
                        ClientConfig.defaults with
                            Surfaces = Surfaces.team
                    }

                    let _, off = Client.init config bus modules ()

                    let _, onCmd =
                        Client.init
                            {
                                config with
                                    Shell.BrowserHistory = HashHistory
                            }
                            bus
                            modules
                            ()

                    Expect.isEmpty off "no command by default"
                    Expect.equal (List.length onCmd) 1 "one when the option is on")

            testCase "no effect is attached"
            <| fun () ->
                let ids = Client.programLifetimeEffects ClientConfig.defaults |> List.map _.Id

                Expect.isFalse (List.contains "browser-history" ids) "the shell does not listen"
        ]

        testCase "a composed shell attaches the subscription too"
        <| fun () ->
            // Every composer attaches through `withShellLifetimeEffects` and
            // updates through `Client.update`, so it takes both halves.
            let outer: Program<unit, int, Outer, unit> =
                Program.mkProgram (fun () -> 0, Cmd.none) (fun (_: Outer) model -> model, Cmd.none) (fun _ _ -> ())

            let composed = outer |> Client.withShellLifetimeEffects on Wrap

            Expect.isTrue (Program.effectIds composed |> List.contains "browser-history") "present on the outer program"
    ]