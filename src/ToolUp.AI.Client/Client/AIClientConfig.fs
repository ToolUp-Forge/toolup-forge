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
//     let aiMode =
//         ConfiguredAIAssistant {
//             Name = "Claude"
//             Icon = ToolUp.AIProviders.Claude.Icons.claude
//             ShowSidePanel = true
//         }
//     AIClientConfig.withAIAssistant aiMode config modules
//     |> Program.withReactSynchronous "elmish-app"
//     |> Program.run
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

// ─── Side-panel MVU ───────────────────────────────────────────────
// (Previously lived in Client.Model / Client.Msg in the shell.)

type SidePanelModel = {
    Open: bool
    Messages: ConversationMessage list
    Streaming: string
    ConversationId: Guid
    /// When true, tool invocations, tool results, and agent-loop task
    /// failures are surfaced inline in the conversation as synthetic
    /// assistant messages prefixed with `[debug]`. Toggled by the user
    /// typing `Debug on` / `Debug off` in the chat (intercepted
    /// client-side — these commands are never sent to the provider).
    /// Always starts false; session-scoped (not persisted).
    DebugMode: bool
    /// Watchdog token — set to `Some guid` when a submission is in
    /// flight, back to `None` when the final `MessageComplete` arrives.
    /// A `WatchdogFired` message scheduled at submit time carries the
    /// same token; if the token still matches when it fires, the stream
    /// has been idle for the full timeout and the client surfaces a
    /// "no response" error rather than leaving the user staring at a
    /// perpetually-streaming spinner (network disconnect, SSE drop,
    /// server crash mid-agent-loop all surface here).
    WatchdogToken: Guid option
    /// Phase 6h: in-flight task Id captured from the `Submitted`
    /// event. `Some` while the agent loop is running; `None` after
    /// `MessageComplete` / `StreamCancelled` / `TaskStatusChanged
    /// AITaskFailed`. Drives the Cancel button (which shows only
    /// when this is `Some`) and seeds the `CancelTask` POST.
    CurrentTaskId: Guid option
}

type SidePanelMsg =
    | Toggle
    | SSEEvent of AIStreamEvent
    | Send of
        activeModule: string *
        activePage: string option *
        activeNarrative: ToolUp.Platform.Narrative.NarrativeDocument option *
        content: string
    | Submitted of AITask
    | ApiError of exn
    | WatchdogFired of token: Guid
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
    /// Phase 6h: user clicked Cancel on the in-flight task. Fires
    /// a fetch POST to /api/ai/cancel/{taskId}; the server flips
    /// the matching CancellationToken; the agent loop bails at the
    /// next turn boundary with a StreamCancelled SSE event.
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
    CurrentTaskId = None
}

// ─── Stream-idle watchdog ────────────────────────────────────────
//
// Server-side hardening (max-turns bound, 429 backoff, confusion and
// consecutive-error early stops, empty-response placeholder) covers the
// common "loop went sideways" cases. The client watchdog is the last
// line of defence: it catches the situations where the server fails
// silently from the client's point of view — SSE connection dropped,
// server process crashed mid-agent-loop, network hung between chunks
// — and surfaces a clear timeout message instead of leaving the user
// watching a permanent "working" spinner.
//
// Token-based invalidation: each `Send` stamps a fresh Guid on the
// model and schedules a `WatchdogFired` with the same Guid after the
// timeout. When `MessageComplete` arrives, the token is cleared to
// `None`. If the watchdog fires but the token has since changed (new
// stream started) or been cleared (response already committed), it's
// ignored. This avoids the "false timeout after a legitimately slow
// response" failure mode a bare sleep-then-check would have.

let private watchdogTimeoutMs = 60_000

let private scheduleWatchdog (token: Guid) : Cmd<SidePanelMsg> =
    Cmd.OfAsync.perform
        (fun () -> async {
            do! Async.Sleep watchdogTimeoutMs
            return token
        })
        ()
        WatchdogFired

let private watchdogNote (conversationId: Guid) : ConversationMessage = {
    Id = Guid.NewGuid()
    ConversationId = conversationId
    Participant = AIAssistant
    Content =
        $"⚠️ No response after {watchdogTimeoutMs / 1000} seconds — the connection may have dropped. "
        + "Try again, or check the server logs. (If a more specific error appeared above, that's the real cause.)"
    Timestamp = DateTime.UtcNow
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = ""
    Verification = None
}

// ─── Debug-mode helpers ──────────────────────────────────────────
//
// `Debug on` / `Debug off` are intercepted client-side and never sent to
// the provider. When debug mode is on, SSE events for tool calls,
// tool results, and task failures surface inline as synthetic assistant
// messages with a `[debug]` prefix — so an operator can see in the chat
// itself why a tool is failing, looping, or returning unexpected output,
// without tailing the server console. The helpers below recognise the
// commands case-insensitively and trim surrounding whitespace so
// "Debug on", "debug on", "  DEBUG ON  " all work.

let private tryDebugCommand (content: string) =
    match content.Trim().ToLowerInvariant() with
    | "debug on" -> Some true
    | "debug off" -> Some false
    | _ -> None

let private debugAck (conversationId: Guid) (enabled: bool) : ConversationMessage =
    let body =
        if enabled then
            "[debug] Debug mode enabled. Tool invocations, results, and agent-loop failures will appear inline in this chat. Type `Debug off` to disable."
        else
            "[debug] Debug mode disabled."

    {
        Id = Guid.NewGuid()
        ConversationId = conversationId
        Participant = AIAssistant
        Content = body
        Timestamp = DateTime.UtcNow
        ToolCalls = []
        RetrievedSources = []
        Parts = []
        CreatedBy = ""
        Verification = None
    }

let private debugNote (conversationId: Guid) (body: string) : ConversationMessage = {
    Id = Guid.NewGuid()
    ConversationId = conversationId
    Participant = AIAssistant
    Content = $"[debug] {body}"
    Timestamp = DateTime.UtcNow
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = ""
    Verification = None
}

// Inline error renderer for the side panel. The full-page module
// renders errors in a dedicated `ErrorMessage` banner; the side panel
// has no such surface, so failures appear inline as an assistant-styled
// note with a ⚠️ prefix. Ungated by `DebugMode` — without this, any
// environmental failure (provider down, agent-loop exception, SSE
// stream error) is silently swallowed and presents as a 60-second
// watchdog timeout with no diagnostic detail.
let private errorNote (conversationId: Guid) (body: string) : ConversationMessage = {
    Id = Guid.NewGuid()
    ConversationId = conversationId
    Participant = AIAssistant
    Content = $"⚠️ {body}"
    Timestamp = DateTime.UtcNow
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = ""
    Verification = None
}

let private truncateForDebug (limit: int) (s: string) =
    if isNull s then ""
    elif s.Length <= limit then s
    else s.Substring(0, limit) + "… (truncated)"

let private sidePanelUpdate (msg: SidePanelMsg) (model: SidePanelModel) : SidePanelModel * Cmd<SidePanelMsg> =
    match msg with
    | Toggle -> { model with Open = not model.Open }, Cmd.none

    | SSEEvent event ->
        match event with
        | MessageDelta(convId, content) when convId = model.ConversationId ->
            {
                model with
                    Streaming = model.Streaming + content
            },
            Cmd.none

        | MessageComplete(convId, _) when convId = model.ConversationId ->
            let assistantMsg: ConversationMessage = {
                Id = Guid.NewGuid()
                ConversationId = convId
                Participant = AIAssistant
                Content = model.Streaming
                Timestamp = DateTime.UtcNow
                ToolCalls = []
                RetrievedSources = []
                Parts = []
                CreatedBy = ""
                Verification = None
            }

            // Clear the watchdog token — response arrived before the
            // timeout. The scheduled `WatchdogFired` may still fire
            // later; it'll see `WatchdogToken = None` (or a newer
            // token, if a subsequent Send happened) and be ignored.
            // Phase 6h: also clear `CurrentTaskId` — task is done,
            // cancel button hides.
            {
                model with
                    Messages = model.Messages @ [ assistantMsg ]
                    Streaming = ""
                    WatchdogToken = None
                    CurrentTaskId = None
            },
            Cmd.none

        // Phase 6h: user clicked Cancel and the server confirmed the
        // agent loop is bailing. Commit whatever was streaming with a
        // cancelled footer so the user keeps the partial response;
        // clear watchdog + task id.
        | StreamCancelled convId when convId = model.ConversationId ->
            let committed =
                if model.Streaming <> "" then
                    let msg: ConversationMessage = {
                        Id = Guid.NewGuid()
                        ConversationId = convId
                        Participant = AIAssistant
                        Content = model.Streaming + "\n\n*[Cancelled by user]*"
                        Timestamp = DateTime.UtcNow
                        ToolCalls = []
                        RetrievedSources = []
                        Parts = []
                        CreatedBy = ""
                        Verification = None
                    }

                    model.Messages @ [ msg ]
                else
                    model.Messages

            {
                model with
                    Messages = committed
                    Streaming = ""
                    WatchdogToken = None
                    CurrentTaskId = None
            },
            Cmd.none

        | ToolCallStarted(convId, toolName, toolCallId) when model.DebugMode && convId = model.ConversationId ->
            let note = debugNote convId $"🔧 Calling tool `{toolName}` (id {toolCallId})"

            {
                model with
                    Messages = model.Messages @ [ note ]
            },
            Cmd.none

        | ToolCallCompleted(convId, _toolCallId, content) when model.DebugMode && convId = model.ConversationId ->
            let note = debugNote convId $"↳ Tool result: {truncateForDebug 500 content}"

            {
                model with
                    Messages = model.Messages @ [ note ]
            },
            Cmd.none

        | TaskStatusChanged(_taskId, AITaskFailed reason) ->
            // Ungated: server-emitted agent-loop failures must surface
            // to the user. Clearing watchdog + task id stops the 60-second
            // safety-net note from also firing on top of the real error.
            let note = errorNote model.ConversationId $"The agent failed: {reason}"

            {
                model with
                    Messages = model.Messages @ [ note ]
                    Streaming = ""
                    WatchdogToken = None
                    CurrentTaskId = None
            },
            Cmd.none

        | StreamError reason ->
            // Ungated: SSE stream errors must surface inline. The watchdog
            // is left running — a stream error often precedes a fatal
            // timeout, and the watchdog's note is harmless if the request
            // does eventually complete.
            let note = errorNote model.ConversationId $"Stream error: {reason}"

            {
                model with
                    Messages = model.Messages @ [ note ]
            },
            Cmd.none

        | _ -> model, Cmd.none

    | Send(activeModule, activePage, activeNarrative, content) ->
        // Debug-mode toggles are intercepted client-side and never reach
        // the provider. Surface an ack in the chat so the user can see
        // the state change inline.
        match tryDebugCommand content with
        | Some enabled ->
            let userMsg: ConversationMessage = {
                Id = Guid.NewGuid()
                ConversationId = model.ConversationId
                Participant = User
                Content = content
                Timestamp = DateTime.UtcNow
                ToolCalls = []
                RetrievedSources = []
                Parts = []
                CreatedBy = ""
                Verification = None
            }

            let ack = debugAck model.ConversationId enabled

            {
                model with
                    Messages = model.Messages @ [ userMsg; ack ]
                    DebugMode = enabled
            },
            Cmd.none
        | None ->
            let userMsg: ConversationMessage = {
                Id = Guid.NewGuid()
                ConversationId = model.ConversationId
                Participant = User
                Content = content
                Timestamp = DateTime.UtcNow
                ToolCalls = []
                RetrievedSources = []
                Parts = []
                CreatedBy = ""
                Verification = None
            }

            // Phase 6j.A: client-side fast-path resolver. Trivial UI
            // instructions ("set country to UK") resolve here before
            // the chat request leaves the page. Side-panel surface
            // carries the active analytical module so field patterns
            // can match in addition to navigation patterns.
            // A companion that ships a fast-path resolver registers
            // it into FastPathBridge at module load.
            let fastPathRequest: FastPathBridge.FastPathRequest = {
                ConversationId = model.ConversationId
                Instruction = content
                ActiveModule = Some activeModule
                ActivePage = activePage
            }

            match FastPathBridge.tryResolve fastPathRequest with
            | FastPathBridge.Resolved(synthAssist, dispatchAction, sendBeacon) ->
                let dispatchCmd = Cmd.ofEffect (fun _ -> dispatchAction ())
                let beaconCmd = Cmd.ofEffect (fun _ -> sendBeacon ())

                {
                    model with
                        Messages = model.Messages @ [ userMsg; synthAssist ]
                        Streaming = ""
                        WatchdogToken = None
                },
                Cmd.batch [ dispatchCmd; beaconCmd ]
            | FastPathBridge.NoResolution ->
                // Attach the user's currently active module so the agent can
                // pull in that module's domain context and tailor its response.
                // Module contexts are registered at compose time on the server;
                // the client only reports the name.
                let request: AIMessageRequest = {
                    ConversationId = model.ConversationId
                    Content = content
                    ActiveModule = Some activeModule
                    ActivePage = activePage
                    ActivePageNarrative = activeNarrative
                    // Per-conversation provider override — wired in a
                    // follow-up commit; for now always None so the factory
                    // resolves via the user's active label.
                    OverrideProviderLabel = None
                    // Side-panel chat — Mode 1 only. Tools declared as
                    // FullPageOnly are filtered out for this request.
                    Surface = SidePanel
                }

                let watchdogId = Guid.NewGuid()

                {
                    model with
                        Messages = model.Messages @ [ userMsg ]
                        Streaming = ""
                        WatchdogToken = Some watchdogId
                },
                Cmd.batch [
                    Cmd.OfRemoting.call aiApi.SubmitMessage request Submitted ApiError
                    scheduleWatchdog watchdogId
                ]

    | Submitted task ->
        // Phase 6h: capture the in-flight taskId so the Cancel
        // button can target /api/ai/cancel/{taskId}. Cleared on
        // MessageComplete / StreamCancelled / TaskStatusChanged
        // failure.
        {
            model with
                CurrentTaskId = Some task.TaskId
        },
        Cmd.none

    | CancelTask taskId ->
        // Fire-and-forget POST. The server confirms via the
        // StreamCancelled SSE event handled above; failures (404 task
        // already done) are silent — the user's intent is already
        // satisfied.
        let postCancel =
            Cmd.ofEffect (fun _ ->
                let userId = UserSession.getUserId ()

                let opts =
                    createObj [ "method" ==> "POST"; "headers" ==> createObj [ "X-User-Id" ==> userId ] ]

                Browser.Dom.window?fetch ($"/api/ai/cancel/{taskId}", opts) |> ignore)

        model, postCancel

    | ApiError ex ->
        // Submit itself failed (e.g. network error before the server ever
        // saw the request, or a 500 from the Remoting handler). Surface
        // inline so the user sees what went wrong instead of waiting for
        // the 60-second watchdog. Mirrors the full-page module's
        // ErrorMessage banner — the side panel has no banner surface so
        // the error renders as an assistant note.
        let reason =
            if isNull ex || isNull ex.Message then
                "unknown error"
            else
                ex.Message

        let note = errorNote model.ConversationId $"Couldn't reach the AI service: {reason}"

        {
            model with
                Messages = model.Messages @ [ note ]
                Streaming = ""
                WatchdogToken = None
                CurrentTaskId = None
        },
        Cmd.none

    | WatchdogFired token ->
        // Token-based invalidation: only fire when the live token still
        // matches. `None` (response arrived) and mismatched Guid (newer
        // stream has started) both collapse to "stale — ignore".
        match model.WatchdogToken with
        | Some liveToken when liveToken = token ->
            let note = watchdogNote model.ConversationId

            {
                model with
                    Messages = model.Messages @ [ note ]
                    Streaming = ""
                    WatchdogToken = None
            },
            Cmd.none
        | _ -> model, Cmd.none

    // Intercepted by `outerUpdate` and translated to a shell Msg before
    // it reaches here; this branch is only present to keep the match
    // exhaustive.
    | NavigateToKnowledgeBase -> model, Cmd.none

    | ClearSession ->
        // Reset to a fresh conversation. The server-side conversation
        // history isn't actively deleted — the user simply starts a new
        // one — but from the user's perspective the chat is wiped and
        // any in-conversation references to deleted KB files are gone.
        // Watchdog token is cleared so a stale timer from the previous
        // conversation can't surface a misleading "no response" note.
        {
            model with
                Messages = []
                Streaming = ""
                ConversationId = Guid.NewGuid()
                WatchdogToken = None
        },
        Cmd.none

// ─── Side-panel chrome ───────────────────────────────────────────

let private isAiEnabled (mode: AIAssistantMode) =
    match mode with
    | NoAIAssistant -> false
    | ConfiguredAIAssistant cfg -> cfg.ShowSidePanel
    | _ -> true

let private sidePanelView
    (mode: AIAssistantMode)
    (activeModule: string)
    (activePage: string option)
    (activeNarrative: ToolUp.Platform.Narrative.NarrativeDocument option)
    (model: SidePanelModel)
    (dispatch: SidePanelMsg -> unit)
    : ReactElement option =
    if isAiEnabled mode && model.Open then
        // Phase 6h: capture the in-flight taskId in the cancel
        // closure so the component can fire the cancel without
        // knowing about Guid types.
        let onCancel =
            model.CurrentTaskId
            |> Option.map (fun tid -> fun () -> dispatch (CancelTask tid))

        Some(
            ConversationPanel.View
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
    (mode: AIAssistantMode)
    (model: SidePanelModel)
    (dispatch: SidePanelMsg -> unit)
    : ReactElement option =
    if isAiEnabled mode then
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

// ─── Module-list transformation ──────────────────────────────────

/// Append the built-in AI assistant + AI settings module pages based
/// on mode. No-op when mode is NoAIAssistant.
///
/// AISettings carries `withGroup "Admin"`, so its position in the
/// module list determines whether the Admin sidebar section sits
/// above or below the app's declared groups. Appending puts AISettings
/// after the app's modules — the SDK's `prepareModules` then appends
/// the rest of its Admin built-ins after that, so AISettings joins
/// the trailing Admin block at the bottom of the sidebar rather than
/// dragging the whole Admin section up to the top.
///
/// AIAssistant is ungrouped and always lands in the `_other` section
/// (rendered last by `Sidebar.buildSections`), so its position in the
/// list doesn't affect section ordering — appended for symmetry with
/// the function name and AISettings.
///
/// The settings module always appears alongside the assistant (not
/// gated on deployment policy) — the UI itself detects PlatformOnly
/// via an empty `ListAvailable` response and renders a
/// managed-by-deployment notice in that case. Keeping the module
/// present in the sidebar means enabling BYOK in the future is a
/// policy flip, not a module-list change.
let appendAssistantModule (mode: AIAssistantMode) (modules: ErasedModule list) : ErasedModule list =
    // Phase 70 — Platform Admin keys module is appended alongside the
    // assistant + settings modules. Visibility filtering at sidebar-
    // render time hides it from non-admin callers, so the
    // unconditional append is safe regardless of deployment shape.
    match mode with
    | NoAIAssistant -> modules
    | DefaultAIAssistant ->
        modules
        @ [
            AIAssistantUI.create None
            AISettingsUI.create ()
            PlatformAIKeysAdminUI.create ()
        ]
    | ConfiguredAIAssistant cfg ->
        modules
        @ [
            AIAssistantUI.create (Some cfg)
            AISettingsUI.create ()
            PlatformAIKeysAdminUI.create ()
        ]

// ─── Outer Program construction ──────────────────────────────────

/// Wrap the shell Program with side-panel state, SSE subscription, and
/// AI chrome. Pass-through behaviour when mode is NoAIAssistant — the
/// wrapper is still built (so types line up) but the SSE subscription
/// and chrome are both disabled.
///
/// Note: this builds an outer Program from the shell's init/update/view
/// directly rather than composing over an already-built Program. Elmish
/// does not expose Program's internal pieces post-construction, so the
/// shell pieces need to be stitched into the outer Program at build time.
let withSidePanel
    (mode: AIAssistantMode)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : Program<unit, OuterModel, OuterMsg, ReactElement> =

    // Phase 13a — `Client.boot` performs the boot-time composition
    // seam setup that every entry point (including `Client.program`)
    // shares: handler-cache population, validators,
    // `UserSession.configure` / `configureDevDefault`, request-guard
    // install with composed seam thunks, CSRF prefetch, auth-bridge
    // install, and the boot-line composition summary log. Idempotent
    // — the underlying setters guard against double-init.
    Client.boot config modules

    let allModules = Client.prepareModules config modules
    let queryBus = Client.buildQueryBus allModules

    // 0.4.1 — the SSE subscription registers as a lifetime-aware
    // `EffectHandle` instead of a boot-time `Cmd.ofEffect`. The runtime
    // disposes it on `IDispatcher.Terminate()` (React's `beforeunload`
    // hook + HMR's hot-reload terminator), so the EventSource is
    // explicitly closed across page-navigation and hot-reload rather
    // than leaking into the next page / next build. The previous
    // pattern (`Cmd.ofEffect (fun dispatch -> SSEClient.subscribe ... |> ignore)`)
    // discarded the unsubscribe handle, which is exactly the leak
    // 0.4.0's README says the fork addresses.
    let sseEffect: EffectHandle<OuterMsg> option =
        if isAiEnabled mode then
            Some(
                EffectHandle.programLifetime "ai-sse-events" (fun dispatch ->
                    let unsubscribe = SSEClient.subscribe (SSEEvent >> SidePanelMsg >> dispatch)

                    { new System.IDisposable with
                        member _.Dispose() = unsubscribe ()
                    })
            )
        else
            None

    let outerInit () : OuterModel * Cmd<OuterMsg> =
        let shellModel, shellCmd = Client.init config queryBus allModules ()

        {
            Shell = shellModel
            SidePanel = sidePanelInit ()
        },
        Cmd.map ShellMsg shellCmd

    let outerUpdate (msg: OuterMsg) (model: OuterModel) : OuterModel * Cmd<OuterMsg> =
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

    let outerView (model: OuterModel) (dispatch: OuterMsg -> unit) : ReactElement =
        let sidePanelDispatch = SidePanelMsg >> dispatch

        let chrome: Client.ExtraChrome = {
            HeaderAction = sidePanelHeaderAction mode model.SidePanel sidePanelDispatch
            SidePanel =
                sidePanelView
                    mode
                    (Client.activeModuleId model.Shell)
                    (Client.activePageRoute model.Shell)
                    (Client.currentNarrative allModules model.Shell)
                    model.SidePanel
                    sidePanelDispatch
        }

        // Phase 3b.B — route through `viewWithSignIn` (not the raw
        // `Client.view`) so the configured `AuthUI` gates this composer's
        // shell too. Calling `Client.view` directly bypassed
        // `AuthUIProvider.gate` entirely, so a deployment that opted into
        // OIDC sign-in via `ClientConfig.AuthUI = OidcAuthUI _` while
        // wiring AI via `AIClientConfig.run` never rendered the sign-in
        // screen — the shell loaded straight through for unauthenticated
        // visitors and every Remoting call 401'd behind it.
        Client.viewWithSignIn config allModules chrome model.Shell (ShellMsg >> dispatch)

    // 0.4.1 — structured `withErrorReporter` replaces the obsoleted
    // `withConsoleTrace` here too. Trace and error routing follow the
    // shape Client.program already uses (see SDK.Client.fs program/run).
    let elmishLog = Logger.forCategory "client.elmish"

    let elmishReporter (ctx: ErrorContext) =
        match config.OnElmishError with
        | Some sink ->
            try
                sink ctx
            with ex ->
                elmishLog.Error("OnElmishError sink raised", Some ex)
        | None -> elmishLog.Error(ctx.Message, Some ctx.Exception)

    let traceLog = Logger.forCategory "client.elmish.trace"

    let traceUpdate (msg: OuterMsg) (model: OuterModel) =
        let nextModel, nextCmd = outerUpdate msg model
        // Format defensively — a message may carry a large payload (e.g. an
        // uploaded file's raw contents) and `sprintf "%A"` on multi-MB data
        // blows the JS call stack. Tracing must never crash message handling.
        traceLog.Debug("outerMsg=" + ToolUp.Elmish.Program.safeMsgRepr msg)
        nextModel, nextCmd

    let prog =
        Program.mkProgram
            outerInit
            (if config.EnableElmishConsoleTrace then
                 traceUpdate
             else
                 outerUpdate)
            outerView

    let progWithReporter = prog |> Program.withErrorReporter elmishReporter

    match sseEffect with
    | Some effect -> progWithReporter |> Program.withEffect effect
    | None -> progWithReporter

/// Build the Elmish `Program` with the AI assistant module prepended,
/// the side-panel state machine wrapped around the shell, the SSE
/// subscription wired, and the chrome rendered alongside the module
/// view. Use this when you want to layer additional chrome (custom
/// metrics overlay, debug panel, error boundary) before mounting React.
/// For the common case, use `AIClientConfig.run` instead.
let program
    (mode: AIAssistantMode)
    (config: ClientConfig)
    (modules: ErasedModule list)
    : Program<unit, OuterModel, OuterMsg, ReactElement> =
    let modules' = appendAssistantModule mode modules
    withSidePanel mode config modules'

/// Build the AI-augmented Program and run it against React mounted at
/// `"elmish-app"`. Mirrors `Client.run` for the AI-enabled case so the
/// composition reads the same regardless of which companions are imported.
///
/// Consults `ClientConfig.PublicEntryDispatchers` first; any dispatcher
/// that returns `true` short-circuits the full AI shell bootstrap (the
/// dispatcher has rendered its own minimal program).
let run (mode: AIAssistantMode) (config: ClientConfig) (modules: ErasedModule list) : unit =
    // Phase 13a — install the request seam BEFORE consulting
    // PublicEntryDispatchers so dispatchers that fire authenticated
    // requests get the same header attachment any other request gets.
    // Mirrors `Client.run`; see the comment there.
    Client.installRequestSeam config

    if Client.tryDispatchPublicEntry config then
        ()
    else
        program mode config modules
        |> Program.withReactSynchronous "elmish-app"
        |> Program.run