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

open System
open ToolUp.Elmish
open ToolUp.Elmish.React
open Fable.Core
open Fable.Core.JsInterop
open Feliz
open ToolUp.Platform
open ToolUp.AI

// HMR is always open; the ToolUp.Elmish.HMR overrides detect the absence of
// Vite's `import.meta.hot` at runtime and become identity functions in
// production builds. Mirrors the SDK.Client.fs change made when
// ToolUp.AI stopped carrying compile-time `#if DEBUG` gates.
open ToolUp.Elmish.HMR

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

/// Write the reduced stream state back and apply its effects the side
/// panel's way. An unchanged state with nothing to do leaves the model as it
/// was, by reference.
let private applyStream
    (model: SidePanelModel)
    (before: ChatStream.ChatStreamState)
    (after: ChatStream.ChatStreamState, effects: ChatStream.StreamEffect list)
    : SidePanelModel * Cmd<SidePanelMsg> =
    if obj.ReferenceEquals(before, after) && List.isEmpty effects then
        model, Cmd.none
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
            (fun (m: SidePanelModel, cmds) effect ->
                match effect with
                | ChatStream.Commit message
                | ChatStream.Note message ->
                    {
                        m with
                            Messages = m.Messages @ [ message ]
                    },
                    cmds
                | ChatStream.Fault message ->
                    {
                        m with
                            Messages = m.Messages @ [ errorNote m.ConversationId message ]
                    },
                    cmds
                | ChatStream.RefreshList -> m, cmds
                | ChatStream.Arm token ->
                    m, cmds @ [ Cmd.OfAsync.perform ChatStream.watchdogDelay token WatchdogFired ]
                | ChatStream.Poll(token, taskId) ->
                    m,
                    cmds
                    @ [
                        Cmd.OfRemoting.call
                            aiApi.GetTaskStatus
                            taskId
                            (fun status -> TaskStatusPolled(token, status))
                            (fun _ -> TaskStatusPolled(token, None))
                    ]
                | ChatStream.Reload conversationId ->
                    m,
                    cmds
                    @ [
                        Cmd.OfRemoting.call aiApi.GetConversation conversationId ConversationReloaded ApiError
                    ])
            (written, [])
        |> fun (m, cmds) -> m, Cmd.batch cmds

let private reduceStream
    (step: ChatStream.ChatStreamState -> ChatStream.ChatStreamState * ChatStream.StreamEffect list)
    (model: SidePanelModel)
    =
    let before = streamOf model
    applyStream model before (step before)

let private sidePanelUpdate (msg: SidePanelMsg) (model: SidePanelModel) : SidePanelModel * Cmd<SidePanelMsg> =
    match msg with
    | Toggle -> { model with Open = not model.Open }, Cmd.none

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
            Cmd.none
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

            let sent, armCmd =
                {
                    model with
                        Messages = model.Messages @ [ userMsg ]
                }
                |> reduceStream (ChatStream.start (Guid.NewGuid()))

            sent,
            Cmd.batch [
                armCmd
                Cmd.OfAsync.perform FastPathBridge.resolve fastPathRequest (fun outcome ->
                    FastPathAnswered(request, outcome))
            ]

    | FastPathAnswered(request, _) when request.ConversationId <> model.ConversationId -> model, Cmd.none

    | FastPathAnswered(_, FastPathBridge.Resolved(synthAssist, dispatchAction, sendBeacon)) ->
        let settled, _ = model |> reduceStream (fun s -> ChatStream.settle s, [])

        {
            settled with
                Messages = settled.Messages @ [ synthAssist ]
        },
        Cmd.batch [
            Cmd.ofEffect (fun _ -> dispatchAction ())
            Cmd.ofEffect (fun _ -> sendBeacon ())
        ]

    | FastPathAnswered(request, FastPathBridge.NoResolution) ->
        model, Cmd.OfRemoting.call aiApi.SubmitMessage request Submitted ApiError

    | Submitted task -> { model with CurrentTask = Some task }, Cmd.none

    | CancelTask taskId -> model, Cmd.ofEffect (fun _ -> ChatStream.AIStream.cancel taskId)

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
        Cmd.none

    | WatchdogFired token -> model |> reduceStream (ChatStream.fire token)

    | TaskStatusPolled(token, status) -> model |> reduceStream (ChatStream.polled token status)

    | ConversationReloaded messages -> { model with Messages = messages }, Cmd.none

    // Intercepted by `outerUpdate` and translated to a shell Msg before
    // it reaches here; this branch is only present to keep the match
    // exhaustive.
    | NavigateToKnowledgeBase -> model, Cmd.none

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
        Cmd.none

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

// ─── The composer's view, with one set of dispatchers per loop ──
//
// The shell holds two memo boundaries that key on its dispatch wrappers by
// reference — the active module's dispatcher and the sidebar's callbacks —
// and both come from the `Client.Dispatchers` set the shell's view is
// handed. Built inline in the view — `ShellMsg >> dispatch` — the set would
// be a fresh one on every render and neither boundary would hold in this
// composer. The loop's own `dispatch` is one function for the loop's life,
// so the program builds the composer's routes from it once
// (`Program.withStableDispatch`, through `Client.programOver`).

/// The two message routes the composer's view hands out, for one loop.
type internal ComposerDispatchers = {
    /// The loop's own dispatcher.
    Dispatch: OuterMsg -> unit
    Shell: Client.Dispatchers
    SidePanel: SidePanelMsg -> unit
}

/// The composer's routes over a loop's `dispatch` — the `build` the
/// composer's program hands `Program.withStableDispatch`.
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
/// shell rendered with the loop's stable shell dispatchers. `shellView` is
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

/// The composer's update: shell messages to the shell, side-panel messages
/// to the side panel, and the knowledge-base badge's navigation re-issued
/// against the shell.
let internal composerUpdate
    (config: ClientConfig)
    (queryBus: IModuleQueryBus)
    (allModules: ErasedModule list)
    (msg: OuterMsg)
    (model: OuterModel)
    : OuterModel * Cmd<OuterMsg> =
    match msg with
    | ShellMsg m ->
        let s, c = Client.update config queryBus allModules m model.Shell
        { model with Shell = s }, Cmd.map ShellMsg c
    // The KB-inventory badge is owned by the side panel but its click
    // navigates the shell. Intercept here and re-issue against the
    // shell so the side panel's Msg surface stays free of shell
    // concerns. Falls through to the generic side-panel handler for
    // every other Msg.
    | SidePanelMsg NavigateToKnowledgeBase ->
        let s, c =
            Client.update config queryBus allModules (Client.ModuleSelected "KnowledgeBase") model.Shell

        { model with Shell = s }, Cmd.map ShellMsg c
    | SidePanelMsg m ->
        let s, c = sidePanelUpdate m model.SidePanel
        { model with SidePanel = s }, Cmd.map SidePanelMsg c

/// A fresh side-panel model, as the composer's `init` starts it.
let internal composerSidePanelInit () : SidePanelModel = sidePanelInit ()

// ─── Outer Program construction ──────────────────────────────────

/// The side panel's subscription to the tab's AI stream.
let internal streamSubscription (dispatch: OuterMsg -> unit) : IDisposable =
    ChatStream.AIStream.subscribe (SSEEvent >> SidePanelMsg >> dispatch)

// The composer's Program over a shell view (whole-tree or store-bound).
// It builds an outer Program from the shell's init/update/view directly
// rather than composing over an already-built Program: Elmish does not
// expose a Program's pieces post-construction, so the shell pieces are
// stitched into the outer Program at build time.
let private composerProgram
    (fills: AISlotFills)
    (config: ClientConfig)
    (modules: ErasedModule list)
    (shellViewFor: ErasedModule list -> Client.ExtraChrome -> Client.Model -> Client.Dispatchers -> ReactElement)
    : Program<unit, OuterModel, OuterMsg, ReactElement> =

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

    // The stream subscription is a lifetime-aware `EffectHandle`: the
    // runtime disposes it on `IDispatcher.Terminate()` (React's
    // `beforeunload` hook + HMR's hot-reload terminator), so the panel's
    // subscription ends across page-navigation and hot-reload rather than
    // leaking into the next page / next build.
    let sseEffect: EffectHandle<OuterMsg> option =
        if isAiEnabled fills then
            Some(EffectHandle.programLifetime "ai-sse-events" streamSubscription)
        else
            None

    let outerInit () : OuterModel * Cmd<OuterMsg> =
        let shellModel, shellCmd = Client.init config queryBus allModules ()

        {
            Shell = shellModel
            SidePanel = composerSidePanelInit ()
        },
        Cmd.map ShellMsg shellCmd

    let outerUpdate = composerUpdate config queryBus allModules

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
    let prog =
        Client.programOver config outerInit outerUpdate composerDispatchersFor outerView

    // The module-state observer publish left the shell's
    // `update`; it is the shell program's post-update hook now, so a
    // composer that embeds the shell installs the same hook over its
    // `ShellMsg` / `Shell` projection, or no AI deployment would publish.
    let progWithReporter =
        prog
        |> Client.withModuleStateObserver
            allModules
            (function
            | ShellMsg m -> Some m
            | _ -> None)
            _.Shell

    // The shell's own program-lifetime effects — the NavigationRequest
    // bus (admin-landing tiles, Home tool cards, deep links, the
    // client-resident navigate AI tool), the cross-module event bus,
    // the notification stream, the auth-token-acquired watcher, and
    // the auth-bridge health observer — must ride this outer Program
    // too. This composer builds the Program from the shell's
    // init/update/view pieces, so nothing else attaches them: without
    // this fold every one of those seams is silently severed (the bus
    // has no subscriber, so e.g. an admin-landing tile click is a
    // no-op with no error). One shared definition site
    // (`Client.programLifetimeEffects`) keeps this composer and
    // `Client.program` attaching the same set.
    //
    // The same call captures this Program's dispatcher as the
    // shell's, lifted through `ShellMsg`, exactly as `Client.run` does for
    // the plain shell. Without it a module's `OnTeamSwitched` and
    // `OnAccessibleModulesChanged` callbacks dispatched into nothing in
    // every AI deployment, so a team switch never reached the shell.
    let progWithShellEffects =
        progWithReporter |> Client.withShellLifetimeEffects config ShellMsg

    match sseEffect with
    | Some effect -> progWithShellEffects |> Program.withEffect effect
    | None -> progWithShellEffects

/// Wrap the shell Program with side-panel state, SSE subscription, and
/// AI chrome, over `modules` as given: it adds no AI page (`program` and
/// `run` fill the AI slots first, `withAISlots`). With the assistant slot
/// `Empty` the wrapper is still built (so types line up) but the SSE
/// subscription and chrome are both disabled.
///
/// Its view renders the whole shell per message (`Client.viewWithSignIn`),
/// so any React binding can mount it; `run` mounts the store-bound
/// composition instead.
let withSidePanel
    (fills: AISlotFills)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : Program<unit, OuterModel, OuterMsg, ReactElement> =
    composerProgram fills config modules (fun allModules -> Client.viewWithSignIn config allModules)

/// Build the Elmish `Program` with the AI slots filled (`withAISlots`),
/// the side-panel state machine wrapped around the shell, the SSE
/// subscription wired, and the chrome rendered alongside the module
/// view. Use this when you want to layer additional chrome (custom
/// metrics overlay, debug panel, error boundary) before mounting React.
/// For the common case, use `AIClientConfig.run` instead.
let program
    (fills: AISlotFills)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : Program<unit, OuterModel, OuterMsg, ReactElement> =
    withSidePanel fills (withAISlots fills config) modules

/// Build the AI-augmented Program and run it against React mounted at
/// `"elmish-app"`. Mirrors `Client.run` for the AI-enabled case so the
/// composition reads the same regardless of which companions are imported.
///
/// Consults `ClientConfig.Composition.PublicEntryDispatchers` first; any dispatcher
/// that returns `true` short-circuits the full AI shell bootstrap (the
/// dispatcher has rendered its own minimal program).
let run (fills: AISlotFills) (config: ClientConfig) (modules: ErasedModule list) : unit =
    // Install the request seam BEFORE consulting
    // PublicEntryDispatchers so dispatchers that fire authenticated
    // requests get the same header attachment any other request gets.
    // Mirrors `Client.run`; see the comment there.
    Client.installRequestSeam config

    if Client.tryDispatchPublicEntry config then
        ()
    else
        // The store-bound composition, as `Client.run` mounts
        // the shell: the composer publishes its model to one store per
        // run, and the shell's chrome and active module read their slices
        // of `OuterModel.Shell` from it, so a module message re-renders
        // that module and no chrome, and a side-panel message re-renders
        // the chrome and no module.
        let store = ModelStore.create<OuterModel, OuterMsg> ()
        let shell = Client.shellStore store _.Shell

        composerProgram fills (withAISlots fills config) modules (fun allModules ->
            Client.viewSlicedOver shell config allModules)
        |> Program.withReactStore store "elmish-app"
        |> Program.run

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