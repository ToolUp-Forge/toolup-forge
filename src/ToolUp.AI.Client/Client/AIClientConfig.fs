// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.AIClientConfig

// Wraps the ToolUp.Platform client shell (Client.program) with the AI
// assistant's own MVU: side-panel state, SSE subscription, chat toggle
// button, and conversation panel. The shell itself has no knowledge of
// AI — it only exposes init/update/view + an ExtraChrome slot, and this
// module supplies the AI-specific chrome.
//
// Typical usage in an application's Client.fs:
//
//     open ToolUp.AI
//     open ToolUp.AI.Client
//
//     let aiSlots = {
//         AISlotFills.defaults with
//             AIAssistant =
//                 SlotFill.Configured {
//                     Name = "Claude"
//                     Icon = ToolUp.AIProviders.Claude.Icons.claude
//                     ShowSidePanel = true
//                 }
//     }
//     AIClientConfig.run aiSlots config modules
//
// Apps that do not want AI skip this module entirely and call
// Client.run directly.

open Fuaran.MVU
open Fuaran.MVU.React
open System
open ToolUp.Elmish
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform
open ToolUp.AI

/// ─── Side-panel MVU ───────────────────────────────────────────────

type SidePanelModel = {
    Open: bool
    Messages: ConversationMessage list
    Streaming: string
    ConversationId: Guid
    /// When true, tool invocations and results are surfaced inline in the
    /// conversation as synthetic assistant messages prefixed with `[debug]`.
    /// Toggled by the user typing `Debug on` / `Debug off` in the chat
    /// (intercepted client-side — never sent to the provider). Always starts
    /// false; session-scoped (not persisted).
    DebugMode: bool
    /// The idle watchdog's live token — `Some` while a turn is in flight.
    /// Every stream event of the live turn replaces it, and a firing whose
    /// token no longer matches is ignored, so the watchdog measures idleness
    /// (`ChatStream.fire`).
    WatchdogToken: Guid option
    /// The in-flight task, from `Submitted` until the turn ends. Drives the
    /// Cancel button and the watchdog's status poll.
    CurrentTask: AITask option
    /// The tool the live turn is running, from `ToolCallStarted`
    /// until its `ToolCallCompleted`. Names what the wait line says.
    RunningTool: string option
    /// Parts a tool attached to the reply streaming in; they ride it into
    /// the committed message.
    InFlightParts: ToolUp.Platform.AI.AIContentPart list
}

type SidePanelMsg =
    | Toggle
    | SSEEvent of AIStreamEvent
    | Send of
        activeModule: string *
        activePage: string option *
        activeNarrative: ToolUp.Platform.Narrative.NarrativeDocument option *
        content: string
    /// The fast-path resolver's answer for a sent message: either the turn is
    /// answered on the client, or `request` goes to the server.
    | FastPathAnswered of request: AIMessageRequest * outcome: FastPathBridge.ResolveOutcome
    | Submitted of AITask
    | ApiError of exn
    | WatchdogFired of token: Guid
    /// The answer to a `GetTaskStatus` poll made when the stream went quiet.
    /// `None` when the server no longer knows the task or the poll failed.
    | TaskStatusPolled of token: Guid * status: AITask option
    /// The conversation re-read after its task finished while the stream was
    /// silent.
    | ConversationReloaded of ConversationMessage list
    /// Emitted by the KB inventory badge in the side-panel header.
    /// `outerUpdate` translates it to `Client.ModuleSelected
    /// "KnowledgeBase"` so the user lands on the KB module's default
    /// page. Lives on the side-panel Msg DU rather than the shell's
    /// because the badge is rendered from the side-panel chrome — the
    /// shell has no knowledge of KB.
    | NavigateToKnowledgeBase
    /// User-initiated end-of-conversation. Resets `Messages` and
    /// `Streaming`, generates a fresh `ConversationId` so the next
    /// `Send` opens a new server-side conversation. Use to purge
    /// in-conversation references to deleted KB files or to start over.
    | ClearSession
    /// User clicked Cancel on the in-flight task (`AIStream.cancel`); the
    /// agent loop bails at the next turn boundary with a StreamCancelled
    /// event.
    | CancelTask of taskId: Guid

// ─── Outer Program model/msg ─────────────────────────────────────
// The AI wrapper's Program is a product of the shell's model and the
// side-panel model. Messages are tagged with which sub-MVU they
// target so update can dispatch to the appropriate handler.

type OuterModel = {
    Shell: Client.Model
    SidePanel: SidePanelModel
}

type OuterMsg =
    | ShellMsg of Client.Msg
    | SidePanelMsg of SidePanelMsg

// ─── Side-panel MVU implementation ───────────────────────────────

let private aiApi =
    Api.makeProxy<AIAssistantApi> (customOptions = UserSession.withRequestHeaders)

let private sidePanelInit () : SidePanelModel = {
    Open = false
    Messages = []
    Streaming = ""
    ConversationId = Guid.NewGuid()
    DebugMode = false
    WatchdogToken = None
    CurrentTask = None
    RunningTool = None
    InFlightParts = []
}

let private streamOf (model: SidePanelModel) : ChatStream.ChatStreamState = {
    Conversation = Some model.ConversationId
    Streaming = model.Streaming
    Parts = model.InFlightParts
    Task = model.CurrentTask
    RunningTool = model.RunningTool
    Watchdog = model.WatchdogToken
    Debug = model.DebugMode
}

/// What the panel says while a turn runs with no text to show.
let internal turnStatus (model: SidePanelModel) : string option = ChatStream.turnStatus (streamOf model)

// Inline error renderer for the side panel. The full-page module renders
// errors in a dedicated `ErrorMessage` banner; the side panel has no such
// surface, so a failure appears inline as an assistant-styled note with a
// ⚠️ prefix, whatever the debug display.
let private errorNote (conversationId: Guid) (body: string) : ConversationMessage =
    ConversationMessage.note AIAssistant conversationId $"⚠️ {body}"

/// The side panel's step list: its remote calls and operations as values.
type internal SidePanelSteps = Step<SidePanelMsg, RemoteEffect<SidePanelMsg>> list

/// Write the reduced stream state back and apply its effects the side
/// panel's way. An unchanged state with nothing to do leaves the model as it
/// was, by reference.
let private applyStream
    (model: SidePanelModel)
    (before: ChatStream.ChatStreamState)
    (after: ChatStream.ChatStreamState, effects: ChatStream.StreamEffect list)
    : SidePanelModel * SidePanelSteps =
    if obj.ReferenceEquals(before, after) && List.isEmpty effects then
        model, []
    else
        let written = {
            model with
                Streaming = after.Streaming
                InFlightParts = after.Parts
                CurrentTask = after.Task
                RunningTool = after.RunningTool
                WatchdogToken = after.Watchdog
                DebugMode = after.Debug
        }

        effects
        |> List.fold
            (fun (m: SidePanelModel, steps) effect ->
                match effect with
                | ChatStream.Commit message
                | ChatStream.Note message ->
                    {
                        m with
                            Messages = m.Messages @ [ message ]
                    },
                    steps
                | ChatStream.Fault message ->
                    {
                        m with
                            Messages = m.Messages @ [ errorNote m.ConversationId message ]
                    },
                    steps
                | ChatStream.RefreshList -> m, steps
                | ChatStream.Arm token ->
                    m,
                    steps
                    @ [
                        Do(
                            RemoteEffect.await
                                "ChatStream.watchdogDelay"
                                ChatStream.watchdogDelay
                                token
                                (fun outcome -> WatchdogFired(Result.defaultValue token outcome))
                        )
                    ]
                | ChatStream.Poll(token, taskId) ->
                    m,
                    steps
                    @ [
                        Do(
                            RemoteEffect.call "AIAssistantApi.GetTaskStatus" aiApi.GetTaskStatus taskId (fun reply ->
                                TaskStatusPolled(token, Result.defaultValue None reply))
                        )
                    ]
                | ChatStream.Reload conversationId ->
                    m,
                    steps
                    @ [
                        Do(
                            RemoteEffect.call
                                "AIAssistantApi.GetConversation"
                                aiApi.GetConversation
                                conversationId
                                (fun reply ->
                                    match reply with
                                    | Ok messages -> ConversationReloaded messages
                                    | Error ex -> ApiError ex)
                        )
                    ])
            (written, [])

let private reduceStream
    (step: ChatStream.ChatStreamState -> ChatStream.ChatStreamState * ChatStream.StreamEffect list)
    (model: SidePanelModel)
    =
    let before = streamOf model
    applyStream model before (step before)

/// The side panel's transitions, as a step list.
let internal sidePanelStep (msg: SidePanelMsg) (model: SidePanelModel) : SidePanelModel * SidePanelSteps =
    match msg with
    | Toggle -> { model with Open = not model.Open }, []

    | SSEEvent event -> model |> reduceStream (fun s -> ChatStream.reduce s event)

    | Send(activeModule, activePage, activeNarrative, content) ->
        let userMsg = ConversationMessage.note User model.ConversationId content

        match ChatStream.debugCommand content with
        | Some enabled ->
            {
                model with
                    Messages = model.Messages @ [ userMsg; ChatStream.debugAck model.ConversationId enabled ]
                    DebugMode = enabled
            },
            []
        | None ->
            // The side panel carries the active analytical module, so a
            // fast-path resolver can match field patterns as well as
            // navigation patterns.
            let fastPathRequest: FastPathBridge.FastPathRequest = {
                ConversationId = model.ConversationId
                Instruction = content
                ActiveModule = Some activeModule
                ActivePage = activePage
            }

            // The user's active module rides the request so the agent can
            // pull in that module's domain context. Side-panel chat is Mode 1
            // only: tools declared FullPageOnly are filtered out for it.
            let request: AIMessageRequest = {
                ConversationId = model.ConversationId
                Content = content
                ActiveModule = Some activeModule
                ActivePage = activePage
                ActivePageNarrative = activeNarrative
                OverrideProviderLabel = None
                Surface = SidePanel
                RetrievalFilters = None
            }

            let sent, armSteps =
                {
                    model with
                        Messages = model.Messages @ [ userMsg ]
                }
                |> reduceStream (ChatStream.start (Guid.NewGuid()))

            sent,
            armSteps
            @ [
                Do(
                    RemoteEffect.await "FastPathBridge.resolve" FastPathBridge.resolve fastPathRequest (fun outcome ->
                        FastPathAnswered(request, Result.defaultValue FastPathBridge.NoResolution outcome))
                )
            ]

    | FastPathAnswered(request, _) when request.ConversationId <> model.ConversationId -> model, []

    | FastPathAnswered(_, FastPathBridge.Resolved(synthAssist, dispatchAction, sendBeacon)) ->
        let settled, _ = model |> reduceStream (fun s -> ChatStream.settle s, [])

        {
            settled with
                Messages = settled.Messages @ [ synthAssist ]
        },
        [
            Do(RemoteEffect.signal "AIClientConfig.fastPathAction" (fun () -> dispatchAction ()))
            Do(RemoteEffect.signal "AIClientConfig.fastPathBeacon" (fun () -> sendBeacon ()))
        ]

    | FastPathAnswered(request, FastPathBridge.NoResolution) ->
        model,
        [
            Do(
                RemoteEffect.call "AIAssistantApi.SubmitMessage" aiApi.SubmitMessage request (fun reply ->
                    match reply with
                    | Ok task -> Submitted task
                    | Error ex -> ApiError ex)
            )
        ]

    | Submitted task -> { model with CurrentTask = Some task }, []

    | CancelTask taskId ->
        model,
        [
            Do(RemoteEffect.signal "AIClientConfig.cancel" (fun () -> ChatStream.AIStream.cancel taskId))
        ]

    | ApiError ex ->
        // Submit itself failed (a network error before the server saw the
        // request, or a 500 from the Remoting handler): surface it inline
        // rather than waiting for the watchdog.
        let reason =
            if isNull ex || isNull ex.Message then
                "unknown error"
            else
                ex.Message

        let ended, _ =
            model |> reduceStream (fun s -> { ChatStream.settle s with Task = None }, [])

        {
            ended with
                Messages =
                    ended.Messages
                    @ [ errorNote model.ConversationId $"Couldn't reach the AI service: {reason}" ]
        },
        []

    | WatchdogFired token -> model |> reduceStream (ChatStream.fire token)

    | TaskStatusPolled(token, status) -> model |> reduceStream (ChatStream.polled token status)

    | ConversationReloaded messages -> { model with Messages = messages }, []

    // Intercepted by `outerUpdate` and translated to a shell Msg before
    // it reaches here; this branch is only present to keep the match
    // exhaustive.
    | NavigateToKnowledgeBase -> model, []

    | ClearSession ->
        // A fresh conversation. The server-side history is not deleted — the
        // user simply starts a new one. Clearing the watchdog token stops a
        // stale timer from the previous conversation surfacing a note.
        {
            model with
                Messages = []
                Streaming = ""
                InFlightParts = []
                ConversationId = Guid.NewGuid()
                WatchdogToken = None
                CurrentTask = None
                RunningTool = None
        },
        []

// ─── Side-panel chrome ───────────────────────────────────────────

let private isAiEnabled (fills: AISlotFills) =
    match fills.AIAssistant with
    | SlotFill.Empty -> false
    | SlotFill.Configured branding -> branding.ShowSidePanel
    | SlotFill.Default
    | SlotFill.External _ -> true

let private sidePanelView
    (fills: AISlotFills)
    (activeModule: string)
    (activePage: string option)
    (activeNarrative: ToolUp.Platform.Narrative.NarrativeDocument option)
    (model: SidePanelModel)
    (dispatch: SidePanelMsg -> unit)
    : ReactElement option =
    if isAiEnabled fills && model.Open then
        // Capture the in-flight task in the cancel closure so the component
        // can fire the cancel without knowing about Guid types.
        let onCancel =
            model.CurrentTask
            |> Option.map (fun task -> fun () -> dispatch (CancelTask task.TaskId))

        Some(
            ConversationPanel.ViewWithStatus
                (turnStatus model)
                true
                "AI Chat"
                model.Messages
                model.Streaming
                (fun content -> dispatch (Send(activeModule, activePage, activeNarrative, content)))
                (fun () -> dispatch Toggle)
                (fun () -> dispatch ClearSession)
                onCancel
        )
    else
        None

let private sidePanelHeaderAction
    (fills: AISlotFills)
    (model: SidePanelModel)
    (dispatch: SidePanelMsg -> unit)
    : ReactElement option =
    if isAiEnabled fills then
        Some(
            Html.div [
                prop.className "flex items-center gap-2"
                prop.children [
                    // KB inventory badge — auto-hidden until the first
                    // `KnowledgeBase.InventoryUpdated` notification
                    // arrives, so deployments without KB silently render
                    // nothing. Click navigates the shell to the KB
                    // module via `NavigateToKnowledgeBase`.
                    ConversationPanel.KbInventoryBadge(fun () -> dispatch NavigateToKnowledgeBase)
                    Html.button [
                        prop.className [
                            "flex items-center gap-2 px-3 py-1.5 rounded-lg text-sm font-medium transition-colors"
                            if model.Open then
                                "bg-violet-100 text-violet-700"
                            else
                                "text-gray-500 hover:bg-gray-100 hover:text-gray-700"
                        ]
                        prop.onClick (fun _ -> dispatch Toggle)
                        prop.children [
                            Html.span [ prop.className "text-base"; prop.text "\u2728" ]
                            Html.span [ prop.text "AI Chat" ]
                        ]
                    ]
                ]
            ]
        )
    else
        None

// ─── The AI slots ─────────────────────────────────────────────────

/// Phase 1074 — the assistant family's slots, as the shell's companion
/// slot entries (`ShellSlotFills.Extensions`), in the order the pages
/// compose: the assistant, the AI settings, the platform-admin keys. The
/// family sits after the app's own modules and ahead of the shell's
/// administration built-ins — where the three pages always sat — so the
/// AI settings page (grouped "Admin") joins the trailing admin block rather
/// than dragging it to the top of the sidebar.
let slotExtensions (fills: AISlotFills) : SlotExtension list = [
    SlotExtension.ofFill
        "AIAssistant"
        (fun branding -> ShellSlots.builtIn (AIAssistantUI.builtIn branding) None)
        fills.AIAssistant
    SlotExtension.ofFill "AISettings" (ShellSlots.builtIn AISettingsUI.builtIn) fills.AISettings
    SlotExtension.ofFill "PlatformAIKeys" (ShellSlots.builtIn PlatformAIKeysAdminUI.builtIn) fills.PlatformAIKeys
]

/// Phase 1074 — `config` with the AI slots filled by `fills`, after any
/// companion slots it already carries.
let withAISlots (fills: AISlotFills) (config: ClientConfig) : ClientConfig = {
    config with
        Slots.Extensions = config.Slots.Extensions @ slotExtensions fills
}

// ─── The composer's view, with one set of dispatchers per mount ──
//
// The shell holds two memo boundaries that key on its dispatch wrappers by
// reference — the active module's dispatcher and the sidebar's callbacks —
// and both come from the `Client.Dispatchers` set the shell's view is
// handed. Built inline in the view — `ShellMsg >> dispatch` — the set would
// be a fresh one on every render and neither boundary would hold in this
// composer. A mount hands its view one `dispatch` for its life, so the
// program builds the composer's routes from it once (`Client.programOver`'s
// `build`).

/// The two message routes the composer's view hands out, for one mount.
type internal ComposerDispatchers = {
    /// The mount's own dispatcher.
    Dispatch: OuterMsg -> unit
    Shell: Client.Dispatchers
    SidePanel: SidePanelMsg -> unit
}

/// The composer's routes over a mount's `dispatch` — the `build` the
/// composer's program hands `Client.programOver`.
let internal composerDispatchersFor (dispatch: OuterMsg -> unit) : ComposerDispatchers = {
    Dispatch = dispatch
    Shell = Client.dispatchers (ShellMsg >> dispatch)
    SidePanel = SidePanelMsg >> dispatch
}

// The side-panel chrome handed to the shell, memoised per input. The
// store-bound shell's chrome is a memo boundary that compares the chrome
// record by reference, so a record rebuilt on every render would re-render
// the chrome on a message that changed only a module's state. Every model
// input the chrome reads is in the key, compared by reference (the
// narrative too: a module that provides one re-renders the chrome when its
// extractor returns a new document, which is the chrome reading what it
// shows). The slot fills and the module list are fixed for the view's life, so
// the memo belongs to the view (`composerView`), not to the module.

let private sameInputs (a: obj array) (b: obj array) =
    a.Length = b.Length && Array.forall2 (fun x y -> obj.ReferenceEquals(x, y)) a b

/// The composer's view over a shell view: the side-panel chrome, and the
/// shell rendered with the mount's stable shell dispatchers. `shellView` is
/// the whole-tree `Client.viewWithSignIn` (`withSidePanel`) or the
/// store-bound `Client.viewSlicedOver` (`run`). Build it once per program:
/// the view it returns keeps the last chrome record it built.
let internal composerView
    (fills: AISlotFills)
    (allModules: ErasedModule list)
    (shellView: Client.ExtraChrome -> Client.Model -> Client.Dispatchers -> ReactElement)
    : OuterModel -> ComposerDispatchers -> ReactElement =
    let chromeOf =
        Memo.oneEntryBy
            (fun (key1: obj array, _) (key2, _) -> sameInputs key1 key2)
            (fun (_, build: unit -> Client.ExtraChrome) -> build ())

    let chromeFor (model: OuterModel) (sidePanelDispatch: SidePanelMsg -> unit) : Client.ExtraChrome =
        let activeModule = Client.activeModuleId model.Shell
        let activePage = Client.activePageRoute model.Shell
        let narrative = Client.currentNarrative allModules model.Shell

        let key: obj array = [|
            box model.SidePanel
            box activeModule
            box activePage
            box narrative
            box sidePanelDispatch
        |]

        chromeOf (
            key,
            fun () -> {
                HeaderAction = sidePanelHeaderAction fills model.SidePanel sidePanelDispatch
                SidePanel = sidePanelView fills activeModule activePage narrative model.SidePanel sidePanelDispatch
            }
        )

    fun (model: OuterModel) (dispatchers: ComposerDispatchers) ->
        shellView (chromeFor model dispatchers.SidePanel) model.Shell dispatchers.Shell

/// The shell's steps in the composer's message type.
let private liftShell
    (steps: Step<Client.Msg, RemoteEffect<Client.Msg>> list)
    : Step<OuterMsg, RemoteEffect<OuterMsg>> list =
    Step.mapAll ShellMsg (RemoteEffect.map ShellMsg) steps

/// The composer's update: shell messages to the shell, side-panel messages
/// to the side panel, and the knowledge-base badge's navigation re-issued
/// against the shell.
let internal composerUpdate
    (config: ClientConfig)
    (queryBus: IModuleQueryBus)
    (allModules: ErasedModule list)
    (msg: OuterMsg)
    (model: OuterModel)
    : OuterModel * Step<OuterMsg, RemoteEffect<OuterMsg>> list =
    match msg with
    | ShellMsg m ->
        let s, steps = Client.update config queryBus allModules m model.Shell
        { model with Shell = s }, liftShell steps
    // The KB-inventory badge is owned by the side panel but its click
    // navigates the shell. Intercept here and re-issue against the
    // shell so the side panel's Msg surface stays free of shell
    // concerns. Falls through to the generic side-panel handler for
    // every other Msg.
    | SidePanelMsg NavigateToKnowledgeBase ->
        let s, steps =
            Client.update config queryBus allModules (Client.ModuleSelected "KnowledgeBase") model.Shell

        { model with Shell = s }, liftShell steps
    | SidePanelMsg m ->
        let s, steps = sidePanelStep m model.SidePanel
        { model with SidePanel = s }, Step.mapAll SidePanelMsg (RemoteEffect.map SidePanelMsg) steps

/// A fresh side-panel model, as the composer's `init` starts it.
let internal composerSidePanelInit () : SidePanelModel = sidePanelInit ()

// ─── The composer's program ──────────────────────────────────────

/// The side panel's subscription to the tab's AI stream.
let internal streamSubscription (dispatch: OuterMsg -> unit) : IDisposable =
    ChatStream.AIStream.subscribe (SSEEvent >> SidePanelMsg >> dispatch)

/// The composer's resources: the shell's own, lifted into `ShellMsg`, and the
/// side panel's AI stream when the assistant is enabled. The shell's must
/// ride this composer: without them every seam the shell's subscriptions
/// carry is silently severed (the navigation bus has no subscriber, so an
/// admin-landing tile click is a no-op with no error), and a module's
/// `OnTeamSwitched` callback dispatches into nothing.
let internal composerResources (fills: AISlotFills) (config: ClientConfig) : Map<ResourceKey, Subscription<OuterMsg>> =
    let shell =
        Client.resources config
        |> Component.prefixResources "shell" (Subscription.map ShellMsg)

    if isAiEnabled fills then
        shell
        |> Map.add (ResourceKey.constant "ai-sse-events") (Subscription("ai-sse-events", streamSubscription))
    else
        shell

// The composer's program over a shell view (whole-tree or store-bound),
// built from the shell's init, update, resources and views.
let private composerProgram
    (fills: AISlotFills)
    (config: ClientConfig)
    (modules: ErasedModule list)
    (shellViewFor: ErasedModule list -> Client.ExtraChrome -> Client.Model -> Client.Dispatchers -> ReactElement)
    : Client.ShellProgram<OuterModel, OuterMsg> =

    // `Client.boot` performs the boot-time composition
    // seam setup that every entry point (including `Client.program`)
    // shares: handler-cache population, validators,
    // `UserSession.configure` / `configureDevDefault`, request-guard
    // install with composed seam thunks, CSRF prefetch, auth-bridge
    // install, and the boot-line composition summary log. Idempotent
    // — the underlying setters guard against double-init.
    Client.boot config modules

    let allModules = Client.prepareModules config modules
    let queryBus = Client.buildQueryBus allModules
    let resources = composerResources fills config

    let outerInit () : OuterModel * Step<OuterMsg, RemoteEffect<OuterMsg>> list =
        let shellModel, shellSteps = Client.init config queryBus allModules ()

        {
            Shell = shellModel
            SidePanel = composerSidePanelInit ()
        },
        liftShell shellSteps

    // The module-state observer runs after each shell message, over the
    // composer's `ShellMsg` / `Shell` projection, as it does for
    // `Client.program`, or no AI deployment would publish.
    let outerUpdate =
        composerUpdate config queryBus allModules
        |> Client.withModuleStateObserver
            allModules
            (function
            | ShellMsg m -> Some m
            | _ -> None)
            _.Shell

    // The shell view routes through the sign-in gate (both
    // `viewWithSignIn` and the store-bound `viewSlicedOver` do), so the
    // configured `AuthUI` gates this composer's shell too. Calling the raw
    // `Client.view` bypassed `AuthUIProvider.gate` entirely, so a
    // deployment that opted into OIDC sign-in while wiring AI via
    // `AIClientConfig.run` never rendered the sign-in screen.
    let outerView = composerView fills allModules (shellViewFor allModules)

    // The reporter, the trace and the stable dispatch are the shell's own
    // builder's (`Client.programOver`), so this composer and `Client.program`
    // route errors and trace updates alike.
    Client.programOver config outerInit outerUpdate (fun _ -> resources) composerDispatchersFor outerView

/// Wrap the shell program with side-panel state, the AI stream and AI
/// chrome, over `modules` as given: it adds no AI page (`program` and
/// `run` fill the AI slots first, `withAISlots`). With the assistant slot
/// `Empty` the wrapper is still built (so types line up) but the AI stream
/// and chrome are both disabled.
///
/// Its view renders the whole shell per message (`Client.viewWithSignIn`);
/// `run` mounts the store-bound composition instead.
let withSidePanel
    (fills: AISlotFills)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : Client.ShellProgram<OuterModel, OuterMsg> =
    composerProgram fills config modules (fun allModules -> Client.viewWithSignIn config allModules)

/// Build the program with the AI slots filled (`withAISlots`), the
/// side-panel state machine wrapped around the shell, the AI stream listed
/// and the chrome rendered alongside the module view. Use this when you want
/// to mount it yourself (`Client.mountAt`). For the common case, use
/// `AIClientConfig.run` instead.
let program
    (fills: AISlotFills)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : Client.ShellProgram<OuterModel, OuterMsg> =
    withSidePanel fills (withAISlots fills config) modules

/// Build the AI-augmented program and mount it at `"elmish-app"`. Mirrors
/// `Client.run` for the AI-enabled case so the composition reads the same
/// regardless of which companions are imported.
///
/// Consults `ClientConfig.Composition.PublicEntryDispatchers` first; any dispatcher
/// that returns `true` short-circuits the full AI shell bootstrap (the
/// dispatcher has rendered its own page).
let run (fills: AISlotFills) (config: ClientConfig) (modules: ErasedModule list) : unit =
    // Install the request seam BEFORE consulting
    // PublicEntryDispatchers so dispatchers that fire authenticated
    // requests get the same header attachment any other request gets.
    // Mirrors `Client.run`; see the comment there.
    Client.installRequestSeam config

    if Client.tryDispatchPublicEntry config then
        ()
    else
        // The store-bound composition, as `Client.run` mounts the shell:
        // the composer publishes its model to one store per run, and the
        // shell's chrome and active module read their slices of
        // `OuterModel.Shell` from it, so a module message re-renders that
        // module and no chrome, and a side-panel message re-renders the
        // chrome and no module.
        let store = ModelStore<OuterModel, ElementView<OuterMsg>>()
        let shell = Client.shellStore store _.Shell

        composerProgram fills (withAISlots fills config) modules (fun allModules ->
            Client.viewSlicedOver shell config allModules)
        |> Client.mountAt "elmish-app" Fresh store
        |> ignore

/// Register the series read a conversation block part is
/// re-read through: the reader's own gated fact read. A client boot that
/// composes the fact store calls it once, before `run`, with the fact
/// client's resolver:
///
///     AIClientConfig.registerBlockSeriesResolver ToolUp.Facts.Client.BlockSeriesResolver.resolve
///
/// Without it a block part renders a placeholder saying no fact reader is
/// composed, and nothing else in the panel changes.
let registerBlockSeriesResolver (resolver: BlockRender.BlockSeriesResolver) : unit =
    BlockRender.setResolver (Some resolver)