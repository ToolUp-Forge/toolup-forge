// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.Tests.ModuleHostTests

// The shell hosts every module through one function: `initModule` decides
// whether a module may init now and addresses its command to it, and
// `stepModule` delivers an inbound message, initialising first. These drive
// the real `Client.update` under Node, the way the Elmish loop does, and pin
// the three hosting answers: a late reply reaches the module that asked, a
// module first reached by an event runs its init command, and an inbound
// action is admitted by the navigation decision and nothing else.

open Feliz
open ToolUp.Elmish
open ToolUp.Platform
open ToolUp.AI.Client.Tests.NodeTest

type private AskMsg =
    | Ask of string
    | Answered of string

type private AskModel = { Answer: string; Updates: int }

/// Every command the module started, with the dispatch its answer comes
/// back on. Answered only when a test answers it.
type private Pending = ResizeArray<string * (AskMsg -> unit)>

let private askerId = "_test1071.asker"
let private otherId = "_test1071.other"
let private adminId = "_test1071.admin"
let private topic = "test1071.ask"

let private asker
    (pending: Pending)
    (bootAsk: bool)
    (configure: ClientModule<AskModel, AskMsg> -> ClientModule<AskModel, AskMsg>)
    (id: string)
    =
    let ask question : Cmd<AskMsg> =
        Cmd.ofEffect (fun dispatch -> pending.Add(question, dispatch))

    ClientModule.create {
        Init = fun () -> { Answer = "none"; Updates = 0 }, (if bootAsk then ask "boot" else Cmd.none)
        Update =
            fun msg m ->
                match msg with
                | Ask question -> { m with Updates = m.Updates + 1 }, ask question
                | Answered answer ->
                    {
                        Answer = answer
                        Updates = m.Updates + 1
                    },
                    Cmd.none
        Name = id
        Icon = Html.none
    }
    |> ClientModule.withView (fun _ _ -> PageContent.SplitPanel(Html.none, Html.none))
    |> ClientModule.withId id
    |> ClientModule.withActionDecoder (fun (key, payload) ->
        match key with
        | "ask" -> Some(Ask payload)
        | _ -> None)
    |> ClientModule.withEventSubscription topic Ask
    |> configure
    |> ClientModule.register

let private shellModel (activeId: string) : Client.Model = {
    ActiveModuleId = activeId
    ActivePageRoute = None
    Hosts = Map.empty
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
    AwaitingFirstInit = Set.empty
    InitPhase = Client.Ready
    Degradations = []
    AppAccessRefused = false
    CommandPalette = CommandPaletteNav.closed
    LocaleOverride = None
}

/// Every message through `update`, every command's effects onto one queue,
/// drained — the Elmish loop, without a renderer.
type private Loop(config: ClientConfig, modules: ErasedModule list, start: Client.Model) =
    let queue = System.Collections.Generic.Queue<Client.Msg>()
    let mutable model = start

    member _.Model = model

    member _.Drain() =
        while queue.Count > 0 do
            let next, cmd =
                Client.update config Unchecked.defaultof<IModuleQueryBus> modules (queue.Dequeue()) model

            model <- next

            for effect in cmd do
                effect queue.Enqueue

    member this.Send(msg: Client.Msg) =
        queue.Enqueue msg
        this.Drain()

    member this.Answer(dispatch: AskMsg -> unit, answer: string) =
        dispatch (Answered answer)
        this.Drain()

let private stateOf (id: string) (loop: Loop) : AskModel option =
    loop.Model |> Client.moduleState id |> Option.map unbox<AskModel>

let private dispatchFor (question: string) (pending: Pending) =
    pending |> Seq.find (fun (q, _) -> q = question) |> snd

/// A visibility profile that governs `id` and selects nothing.
let private excluding (id: string) : AccessibleModulesResponse = {
    Managed = []
    Accessible = []
    VisibilityProfile =
        Some {
            GovernedModuleIds = [ id ]
            SelectedModuleIds = []
            ExcludedEntryIds = []
            ContributingScopes = [ FlagScope.Platform ]
        }
}

let tests =
    testList "The module host (Phase 1071)" [
        testCase "(a) a reply landing after navigation reaches the module that asked, not the active one" (fun () ->
            let pending = Pending()
            let modules = [ asker pending false id askerId; asker (Pending()) false id otherId ]
            let loop = Loop(ClientConfig.defaults, modules, shellModel askerId)

            loop.Send(Client.ModuleSelected askerId)
            loop.Send(Client.ModuleSelected otherId)
            loop.Send(Client.ModuleSelected askerId)
            loop.Send(Client.ModuleEventPublished(topic, "q"))
            loop.Send(Client.ModuleSelected otherId)
            Expect.equal loop.Model.ActiveModuleId otherId "the asker was left"

            let otherBefore = stateOf otherId loop
            loop.Answer(dispatchFor "q" pending, "a")

            Expect.equal (stateOf otherId loop) otherBefore "the active module's state is unchanged"
            Expect.equal (stateOf askerId loop |> Option.map _.Answer) (Some "a") "the asker took its answer")

        testCase "(b) a module with no host reached by an event runs its init command" (fun () ->
            let pending = Pending()
            let modules = [ asker pending true id askerId; asker (Pending()) false id otherId ]
            let loop = Loop(ClientConfig.defaults, modules, shellModel otherId)

            loop.Send(Client.ModuleSelected otherId)
            loop.Send(Client.ModuleEventPublished(topic, "q"))

            Expect.equal
                (pending |> Seq.map fst |> Seq.sort |> List.ofSeq)
                [ "boot"; "q" ]
                "the init command and the update command both ran"

            loop.Answer(dispatchFor "boot" pending, "booted")
            Expect.equal (stateOf askerId loop |> Option.map _.Answer) (Some "booted") "the init reply landed")

        testCase "(c) an action for a module the visibility profile excludes is refused" (fun () ->
            let pending = Pending()
            let modules = [ asker pending false id askerId; asker (Pending()) false id otherId ]

            let loop =
                Loop(
                    ClientConfig.defaults,
                    modules,
                    {
                        shellModel otherId with
                            AccessibleModules = Some(excluding askerId)
                    }
                )

            loop.Send(Client.ModuleActionReceived(askerId, "ask", "q"))

            Expect.isNone (stateOf askerId loop) "the excluded module was not initialised"
            Expect.equal pending.Count 0 "and none of its commands went out")

        testCase
            "(c) an action for a PlatformAdminOnly module is refused to a non-admin, admitted to an admin"
            (fun () ->
                let pending = Pending()

                let modules = [
                    asker pending false (ClientModule.withNavRole NavRole.PlatformAdminOnly) adminId
                    asker (Pending()) false id otherId
                ]

                let member' = Loop(ClientConfig.defaults, modules, shellModel otherId)
                member'.Send(Client.ModuleActionReceived(adminId, "ask", "q"))
                Expect.isNone (stateOf adminId member') "refused to a non-admin"
                Expect.equal pending.Count 0 "no command went out"

                let admin =
                    Loop(
                        ClientConfig.defaults,
                        modules,
                        {
                            shellModel otherId with
                                PlatformRole = Some PlatformRole.PlatformAdmin
                        }
                    )

                admin.Send(Client.ModuleActionReceived(adminId, "ask", "q"))
                Expect.equal (stateOf adminId admin |> Option.map _.Updates) (Some 1) "admitted to an admin"
                Expect.equal pending.Count 1 "its command went out")

        testCase "an event for a refused subscriber reaches the admitted ones only" (fun () ->
            let refused = Pending()
            let admitted = Pending()

            let modules = [
                asker refused false (ClientModule.withNavRole NavRole.PlatformAdminOnly) adminId
                asker admitted false id askerId
            ]

            let loop = Loop(ClientConfig.defaults, modules, shellModel otherId)
            loop.Send(Client.ModuleEventPublished(topic, "q"))

            Expect.isNone (stateOf adminId loop) "the refused subscriber was not initialised"
            Expect.equal admitted.Count 1 "the admitted subscriber took the event")

        testCase "a reset re-initialises the module and advances its generation" (fun () ->
            let modules = [ asker (Pending()) false id askerId ]
            let loop = Loop(ClientConfig.defaults, modules, shellModel askerId)

            loop.Send(Client.ModuleSelected askerId)
            loop.Send(Client.ModuleActionReceived(askerId, "ask", "q"))
            Expect.equal (stateOf askerId loop |> Option.map _.Updates) (Some 1) "the module moved"

            loop.Send(Client.ResetModule askerId)
            Expect.equal (stateOf askerId loop |> Option.map _.Updates) (Some 0) "back to its initial state"

            Expect.equal
                (loop.Model.Hosts |> Map.tryFind askerId |> Option.map _.Generation)
                (Some 1)
                "the generation, part of the boundary's key, advanced")

        testCase "an Init that throws leaves no host and is reported as a command, not from update" (fun () ->
            let reported = ResizeArray<string>()

            let config = {
                ClientConfig.defaults with
                    Composition.OnError = Some(fun error -> reported.Add error.ModuleId)
            }

            let throwing =
                ClientModule.create {
                    Init = fun () -> failwith "Init exploded"
                    Update = fun (_: AskMsg) (m: AskModel) -> m, Cmd.none
                    Name = askerId
                    Icon = Html.none
                }
                |> ClientModule.withView (fun _ _ -> PageContent.SplitPanel(Html.none, Html.none))
                |> ClientModule.withId askerId
                |> ClientModule.register

            let next, cmd =
                Client.update
                    config
                    Unchecked.defaultof<IModuleQueryBus>
                    [ throwing ]
                    (Client.ModuleSelected askerId)
                    (shellModel otherId)

            Expect.isFalse (next.Hosts |> Map.containsKey askerId) "no host"
            Expect.equal reported.Count 0 "update reported nothing itself"

            for effect in cmd do
                effect ignore

            Expect.equal (List.ofSeq reported) [ askerId ] "the command reports it")
    ]