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
    Hosts =
        states
        |> List.map (fun (id, state) -> id, ({ State = state; Generation = 0 }: ModuleHost))
        |> Map.ofList
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
    AwaitingFirstInit = Client.firstInitGates
    InitPhase = Client.Ready
    Degradations = []
    AppAccessRefused = false
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
    Snapshot = Remote.NotAsked
    EditingDefaults = None
    SelectedMember = None
    EditingMembers = Map.empty
    SaveInFlight = false
    Banner = None
    OnAccessibleModulesChanged = None
    Messages = MessageCatalog.english.PermissionsAdmin
}

// ─── A module's commands reach that module ───────────────────────────

/// A module whose messages start a read and take its answer, the shape of
/// the fact browse module's `OpenTable`. `TableAnswered` has the same tag and
/// field as `HomeRenamed`, which is what a misdelivered message is read as
/// under Fable: a tag and fields, never a type.
type private ReadMsg =
    | OpenTable of string
    | TableAnswered of string

type private ReadModel = { Detail: string }

type private HomeMsg =
    | HomeRefreshed
    | HomeRenamed of string

type private HomeModel = { Title: string; Updates: int }

/// Every read a module started, oldest first: the table it asked for and the
/// `dispatch` its answer comes back on. Unanswered until a test answers it.
type private ReadsInFlight = ResizeArray<string * (ReadMsg -> unit)>

let private readTopic = "test1250.open-table"
let private readId = "_test1250.reads"
let private homeId = "_test1250.home"

let private readingModuleWith
    (configure: ClientModule<ReadModel, ReadMsg> -> ClientModule<ReadModel, ReadMsg>)
    (bootRead: bool)
    (inFlight: ReadsInFlight)
    =
    let read table : Cmd<ReadMsg> =
        Cmd.ofEffect (fun dispatch -> inFlight.Add(table, dispatch))

    ClientModule.create {
        Init = fun () -> { Detail = "none" }, (if bootRead then read "boot" else Cmd.none)
        Update =
            fun msg m ->
                match msg with
                | OpenTable table -> { Detail = "Loading" }, read table
                | TableAnswered table -> { Detail = table }, Cmd.none
        Name = readId
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId readId
    |> ClientModule.withActionDecoder (fun (key, payload) ->
        match key with
        | "open" -> Some(OpenTable payload)
        | _ -> None)
    |> ClientModule.withEventSubscription readTopic OpenTable
    |> configure
    |> ClientModule.register

let private readingModule bootRead inFlight = readingModuleWith id bootRead inFlight

let private homeModule () =
    ClientModule.create {
        Init = fun () -> { Title = "home"; Updates = 0 }, Cmd.none
        Update =
            fun msg m ->
                match msg with
                | HomeRefreshed -> { m with Updates = m.Updates + 1 }, Cmd.none
                | HomeRenamed title ->
                    {
                        Title = title
                        Updates = m.Updates + 1
                    },
                    Cmd.none
        Name = homeId
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId homeId
    |> ClientModule.register

let private homeState = box { Title = "home"; Updates = 0 }

// ─── The module context carries the shell's catalog (Phase 1072) ─────

type private CatalogMsg = | Woken

/// What the module saw in `ClientModuleContext.Messages` at `Init`.
type private CatalogModel = { Seen: MessageCatalog }

let private catalogId = "_test1072.catalog"
let private catalogTopic = "test1072.wake"

let private catalogModule () =
    ClientModule.create {
        Init = fun () -> { Seen = MessageCatalog.english }, Cmd.none
        Update = fun Woken m -> m, Cmd.none
        Name = catalogId
        Icon = Html.none
    }
    |> ClientModule.withContextInit (fun ctx -> { Seen = ctx.Messages }, Cmd.none)
    |> ClientModule.withView (fun _ _ -> Html.none, Html.none)
    |> ClientModule.withId catalogId
    |> ClientModule.withEventSubscription catalogTopic (fun (_: string) -> Woken)
    |> ClientModule.register

let private readState = box { Detail = "none" }

/// Drives `Client.update` the way the Elmish loop does: every message goes
/// through `update`, its command's effects run against one queue, and the
/// queue drains. A test names no shell message for a module's command, so it
/// reads the same on any route the shell gives one.
type private ShellLoop(modules: ErasedModule list, start: Client.Model) =
    let queue = System.Collections.Generic.Queue<Client.Msg>()
    let mutable model = start

    member _.Model = model

    member _.Drain() =
        while queue.Count > 0 do
            let next, cmd = update modules (queue.Dequeue()) model
            model <- next

            for effect in cmd do
                effect queue.Enqueue

    member this.Send(msg: Client.Msg) =
        queue.Enqueue msg
        this.Drain()

    /// Run a read's `dispatch`, as its response arriving would.
    member this.Answer(dispatch: ReadMsg -> unit, table: string) =
        dispatch (TableAnswered table)
        this.Drain()

let private detailOf (loop: ShellLoop) =
    loop.Model
    |> Client.moduleState readId
    |> Option.map (fun s -> (unbox<ReadModel> s).Detail)

let private homeOf (loop: ShellLoop) =
    (loop.Model.Hosts |> Map.find homeId).State |> unbox<HomeModel>

let private tablesAsked (inFlight: ReadsInFlight) =
    inFlight |> Seq.map fst |> Seq.sort |> List.ofSeq

let private answerFor (table: string) (inFlight: ReadsInFlight) =
    inFlight |> Seq.find (fun (asked, _) -> asked = table) |> snd

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

                let after, _ = update [ erased ] (Client.ModuleMsg(id, box Increment)) before
                Expect.equal (List.ofSeq published) [] "update itself publishes nothing"

                Client.observeModuleStates [ erased ] (Client.ModuleMsg(id, box Increment)) before.Hosts after
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
                        before.Hosts
                        afterAction

                    let afterEvent, _ =
                        update [ erased ] (Client.ModuleEventPublished("test1069.topic", "")) afterAction

                    Client.observeModuleStates
                        [ erased ]
                        (Client.ModuleEventPublished("test1069.topic", ""))
                        afterAction.Hosts
                        afterEvent

                    // An action no decoder accepts leaves the state untouched.
                    let afterDropped, _ =
                        update [ erased ] (Client.ModuleActionReceived(target, "unknown", "")) afterEvent

                    Client.observeModuleStates
                        [ erased ]
                        (Client.ModuleActionReceived(target, "unknown", ""))
                        afterEvent.Hosts
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

        testList "a module's commands reach that module" [
            testCase "an event on an inactive module with state runs its read, and the answer lands" (fun () ->
                let inFlight = ReadsInFlight()
                let modules = [ readingModule false inFlight; homeModule () ]

                let loop =
                    ShellLoop(modules, shellModel homeId [ homeId, homeState; readId, readState ])

                loop.Send(Client.ModuleEventPublished(readTopic, "sales"))
                Expect.equal (detailOf loop) (Some "Loading") "the subscriber took the event"
                Expect.equal (tablesAsked inFlight) [ "sales" ] "the read went out"

                loop.Answer(answerFor "sales" inFlight, "sales")
                Expect.equal (detailOf loop) (Some "sales") "the answer reached the subscriber"
                Expect.equal (homeOf loop).Updates 0 "the active module's Update did not run")

            testCase "an event on an inactive module with no state runs its Init and Update commands" (fun () ->
                let inFlight = ReadsInFlight()
                let modules = [ readingModule true inFlight; homeModule () ]
                let loop = ShellLoop(modules, shellModel homeId [ homeId, homeState ])

                loop.Send(Client.ModuleEventPublished(readTopic, "sales"))
                Expect.equal (tablesAsked inFlight) [ "boot"; "sales" ] "both reads went out"

                loop.Answer(answerFor "sales" inFlight, "sales")
                Expect.equal (detailOf loop) (Some "sales") "the answer reached the subscriber"
                Expect.equal (homeOf loop).Updates 0 "the active module's Update did not run")

            testCase "an action on an inactive module with state runs its read, and the answer lands" (fun () ->
                ensureEventSource ()
                let inFlight = ReadsInFlight()
                let modules = [ readingModule false inFlight; homeModule () ]

                let loop =
                    ShellLoop(modules, shellModel homeId [ homeId, homeState; readId, readState ])

                loop.Send(Client.ModuleActionReceived(readId, "open", "sales"))
                Expect.equal (tablesAsked inFlight) [ "sales" ] "the read went out"

                loop.Answer(answerFor "sales" inFlight, "sales")
                Expect.equal (detailOf loop) (Some "sales") "the answer reached the target"
                Expect.equal (homeOf loop).Updates 0 "the active module's Update did not run")

            testCase "an action on an inactive module with no state runs its Init and Update commands" (fun () ->
                ensureEventSource ()
                let inFlight = ReadsInFlight()
                let modules = [ readingModule true inFlight; homeModule () ]
                let loop = ShellLoop(modules, shellModel homeId [ homeId, homeState ])

                loop.Send(Client.ModuleActionReceived(readId, "open", "sales"))
                Expect.equal (tablesAsked inFlight) [ "boot"; "sales" ] "both reads went out")

            testCase "a read answered after its module is left updates that module and no other" (fun () ->
                let inFlight = ReadsInFlight()
                let modules = [ readingModule false inFlight; homeModule () ]

                let loop =
                    ShellLoop(modules, shellModel readId [ homeId, homeState; readId, readState ])

                loop.Send(Client.ModuleEventPublished(readTopic, "sales"))
                Expect.equal inFlight.Count 1 "the read is in flight while its module is active"

                loop.Send(Client.ModuleSelected homeId)
                Expect.equal loop.Model.ActiveModuleId homeId "the reader left"

                loop.Answer(answerFor "sales" inFlight, "sales")

                // Delivered by the active module alone, this message reaches
                // Home's `Update` as `HomeRenamed "sales"`, and Data tables
                // stays on Loading with nothing left to end it.
                Expect.equal (homeOf loop) { Title = "home"; Updates = 0 } "Home's Update did not receive the answer"
                Expect.equal (detailOf loop) (Some "sales") "the module that asked took the answer"

                loop.Send(Client.ModuleSelected readId)
                Expect.equal (detailOf loop) (Some "sales") "going back finds the answer, not Loading")

            testCase "an answer for a module with no state changes nothing, whichever module is active" (fun () ->
                let inFlight = ReadsInFlight()
                let modules = [ readingModule false inFlight; homeModule () ]

                // The message the answer becomes, whatever route the shell gives it.
                let answer = ResizeArray<Client.Msg>()

                let _, routed =
                    update
                        modules
                        (Client.ModuleEventPublished(readTopic, "sales"))
                        (shellModel readId [ homeId, homeState; readId, readState ])

                for effect in routed do
                    effect answer.Add

                (answerFor "sales" inFlight) (TableAnswered "sales")
                let answerMsg = Seq.exactlyOne answer

                // What a re-initialisation leaves (a team switch): another
                // module active, the asker's state gone.
                let cleared = ShellLoop(modules, shellModel homeId [ homeId, homeState ])
                cleared.Send answerMsg
                Expect.isNone (detailOf cleared) "no state appears for the answer"
                Expect.equal (homeOf cleared).Updates 0 "the active module's Update did not run"

                // What a denied route leaves: the module active, with no state.
                let denied = ShellLoop(modules, shellModel readId [])
                denied.Send answerMsg
                Expect.isTrue denied.Model.Hosts.IsEmpty "a message with no state to land on changes nothing")

            testCase "a publish then a navigate opens the table, from a module never opened" (fun () ->
                let inFlight = ReadsInFlight()
                let modules = [ readingModule true inFlight; homeModule () ]
                let loop = ShellLoop(modules, shellModel homeId [ homeId, homeState ])

                loop.Send(Client.ModuleEventPublished(readTopic, "sales"))
                loop.Send(Client.ModuleSelected readId)

                loop.Answer(answerFor "sales" inFlight, "sales")
                Expect.equal (detailOf loop) (Some "sales") "the table opens")

            testCase "a publish then a navigate opens the table, from a module opened before" (fun () ->
                let inFlight = ReadsInFlight()
                let modules = [ readingModule false inFlight; homeModule () ]

                let loop =
                    ShellLoop(modules, shellModel homeId [ homeId, homeState; readId, readState ])

                loop.Send(Client.ModuleEventPublished(readTopic, "sales"))
                loop.Send(Client.ModuleSelected readId)

                loop.Answer(answerFor "sales" inFlight, "sales")
                Expect.equal (detailOf loop) (Some "sales") "the table opens")

            testCase "a navigate then a publish still opens the table" (fun () ->
                let inFlight = ReadsInFlight()
                let modules = [ readingModule false inFlight; homeModule () ]
                let loop = ShellLoop(modules, shellModel homeId [ homeId, homeState ])

                loop.Send(Client.ModuleSelected readId)
                loop.Send(Client.ModuleEventPublished(readTopic, "sales"))

                loop.Answer(answerFor "sales" inFlight, "sales")
                Expect.equal (detailOf loop) (Some "sales") "the table opens")

            testCase "a module the reader may not open gets no Init and no read from an event or an action" (fun () ->
                let inFlight = ReadsInFlight()

                let refused =
                    readingModuleWith (ClientModule.withNavRole NavRole.PlatformAdminOnly) true inFlight

                let loop =
                    ShellLoop([ refused; homeModule () ], shellModel homeId [ homeId, homeState ])

                loop.Send(Client.ModuleEventPublished(readTopic, "sales"))
                loop.Send(Client.ModuleActionReceived(readId, "open", "sales"))

                Expect.equal inFlight.Count 0 "no read goes out to come back as a 403"
                Expect.isNone (detailOf loop) "no state for a module the route guard refuses")
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

                Expect.isTrue
                    (match model.Banner with
                     | Some(Notice.Info _) -> true
                     | _ -> false)
                    "success is not an error")

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

                Expect.isTrue
                    (match model.Banner with
                     | Some(Notice.Problem _) -> true
                     | _ -> false)
                    "failure is an error")
        ]

        testList "the module context carries the shell's catalog (Phase 1072)" [
            testCase "a module the shell initialises reads the catalog the provider is handed" (fun () ->
                let modules = [ catalogModule (); homeModule () ]

                let start = {
                    shellModel homeId [ homeId, homeState ] with
                        LocaleOverride = Some "fr-FR"
                }

                let loop = ShellLoop(modules, start)
                loop.Send(Client.ModuleEventPublished(catalogTopic, "wake"))

                let seen =
                    ((loop.Model.Hosts |> Map.find catalogId).State |> unbox<CatalogModel>).Seen

                Expect.equal seen.Locale "fr-FR" "the module was initialised under the session's locale"

                // `view` mounts `MessageCatalogProvider.provider` with this
                // call over the same model; the memo makes it one value.
                let provided =
                    Client.resolveCatalog ClientConfig.defaults loop.Model.LocaleOverride loop.Model.PlatformConfig

                Expect.isTrue (obj.ReferenceEquals(seen, provided)) "the context and the provider hold one catalog")

            testCase "a module initialised under a French catalog authors its banner in French" (fun () ->
                let french (c: MessageCatalog) =
                    if c.Locale.StartsWith "fr" then
                        {
                            c with
                                PermissionsAdmin = {
                                    c.PermissionsAdmin with
                                        ExposureNowAvailable = fun name -> $"{name} est maintenant disponible."
                                }
                        }
                    else
                        c

                let ctx = {
                    ClientModuleContext.empty with
                        Messages = MessageCatalog.resolve "fr-FR" (Some french)
                }

                let initial, _ = PermissionsAdminUI.init ctx

                let model, _ =
                    PermissionsAdminUI.update
                        (PermissionsAdminUI.ExposureResult(
                            "Reports",
                            ModuleExposure.Hidden,
                            ModuleExposure.Available,
                            Ok()
                        ))
                        initial

                Expect.equal
                    model.Banner
                    (Some(Notice.Info "Reports est maintenant disponible."))
                    "the reducer read the catalog it was initialised with")
        ]
    ]