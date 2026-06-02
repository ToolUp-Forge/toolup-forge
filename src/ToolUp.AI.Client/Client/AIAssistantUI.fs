// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.Client.AIAssistantUI

open System
open ToolUp.Elmish
open Fable.Core
open Fable.Core.JsInterop
open Fable.SimpleJson
open Feliz
open Toolup.UIToolkit
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.Client

// ─── Model ────────────────────────────────────────────────────────

type Model = {
    Conversations: Conversation list
    ActiveConversation: Guid option
    Messages: ConversationMessage list
    AvailableTools: AIToolDefinition list
    /// Configured BYOK provider instances surfaced from
    /// `AISettingsApi.GetMyConfig`. Drives the per-conversation
    /// picker dropdown — empty list → dropdown is hidden because
    /// account-default is the only option.
    ConfiguredProviders: AIProviderInstanceView list
    CurrentTask: AITask option
    /// Accumulates streaming content from SSE MessageDelta events
    StreamingContent: string
    ErrorMessage: string option
    /// When true, tool invocations, tool results, and agent-loop task
    /// failures are surfaced inline in the conversation as synthetic
    /// assistant messages prefixed with `[debug]`. Toggled by the user
    /// typing `Debug on` / `Debug off` in the chat (intercepted
    /// client-side — these commands are never sent to the provider).
    /// Always starts false; session-scoped (not persisted).
    DebugMode: bool
    /// Watchdog token — see the matching field on SidePanelModel in
    /// AIClientConfig.fs for the full contract. Catches silent failures
    /// where the server never produces a final `MessageComplete`.
    WatchdogToken: Guid option
}

type Msg =
    | LoadConversations of ApiCall<unit, Conversation list>
    | LoadTools of ApiCall<unit, AIToolDefinition list>
    | LoadProviders of ApiCall<unit, AIUserConfigView>
    | SelectConversation of Guid
    | LoadMessages of ApiCall<Guid, ConversationMessage list>
    | SubmitMessage of string
    | MessageSubmitted of ApiCall<Guid * string, AITask>
    | NewConversation
    | DeleteConversation of ApiCall<Guid, Result<unit, string>>
    /// User changed the per-conversation provider override via the
    /// dropdown. `None` → fall back to account default.
    | SetProviderOverride of string option
    | ProviderOverrideSet of Result<unit, string>
    | SSEEvent of AIStreamEvent
    | ApiError of string
    | DismissError
    | WatchdogFired of token: Guid
    /// Phase 6h: user clicked Cancel on the in-flight task. Fires
    /// a fetch POST to /api/ai/cancel/{taskId}; the server flips
    /// the matching CancellationToken; the agent loop bails at the
    /// next turn boundary with a StreamCancelled SSE event.
    | CancelTask of taskId: Guid

let private aiApi =
    Api.makeProxy<AIAssistantApi> (customOptions = UserSession.withRequestHeaders)

let private aiSettingsApi =
    Api.makeProxy<AISettingsApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init ─────────────────────────────────────────────────────────

// ─── Debug-mode helpers ──────────────────────────────────────────
//
// `Debug on` / `Debug off` are intercepted client-side and never sent to
// the provider. When debug mode is on, SSE events for tool calls, tool
// results, and task failures surface inline as synthetic assistant
// messages with a `[debug]` prefix. Mirrors the side-panel helpers in
// AIClientConfig.fs — kept local here so each module page owns its
// own debug surface without cross-module coupling.

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
}

let private truncateForDebug (limit: int) (s: string) =
    if isNull s then ""
    elif s.Length <= limit then s
    else s.Substring(0, limit) + "… (truncated)"

// ─── Stream-idle watchdog ────────────────────────────────────────
// Mirrors the side-panel watchdog in AIClientConfig.fs. See that file's
// comment for the token-based-invalidation rationale.

let private watchdogTimeoutMs = 60_000

let private scheduleWatchdog (token: Guid) : Cmd<Msg> =
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
        $"[No response after {watchdogTimeoutMs / 1000} seconds] The connection to the AI service may have dropped. "
        + "Try resending your message. If this keeps happening, check the server logs or restart the dev server."
    Timestamp = DateTime.UtcNow
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = ""
}

let init () =
    {
        Conversations = []
        ActiveConversation = None
        Messages = []
        AvailableTools = []
        ConfiguredProviders = []
        CurrentTask = None
        StreamingContent = ""
        ErrorMessage = None
        DebugMode = false
        WatchdogToken = None
    },
    Cmd.batch [
        Cmd.OfRemoting.call aiApi.ListConversations () (Finished >> LoadConversations) (fun ex -> ApiError ex.Message)
        Cmd.OfRemoting.call aiApi.GetAvailableTools () (Finished >> LoadTools) (fun ex -> ApiError ex.Message)
        Cmd.OfRemoting.call aiSettingsApi.GetMyConfig () (Finished >> LoadProviders) (fun ex -> ApiError ex.Message)
        // Subscribe to SSE events for streaming AI responses
        Cmd.ofEffect (fun dispatch -> SSEClient.subscribe (SSEEvent >> dispatch) |> ignore)
    ]

// ─── Update ───────────────────────────────────────────────────────

let update msg model =
    match msg with
    | LoadConversations action ->
        match action with
        | Start() ->
            model,
            Cmd.OfRemoting.call aiApi.ListConversations () (Finished >> LoadConversations) (fun ex ->
                ApiError ex.Message)
        | Finished convos -> { model with Conversations = convos }, Cmd.none

    | LoadTools action ->
        match action with
        | Start() ->
            model,
            Cmd.OfRemoting.call aiApi.GetAvailableTools () (Finished >> LoadTools) (fun ex -> ApiError ex.Message)
        | Finished tools -> { model with AvailableTools = tools }, Cmd.none

    | LoadProviders action ->
        match action with
        | Start() ->
            model,
            Cmd.OfRemoting.call aiSettingsApi.GetMyConfig () (Finished >> LoadProviders) (fun ex -> ApiError ex.Message)
        | Finished view ->
            {
                model with
                    ConfiguredProviders = view.ConfiguredProviders
            },
            Cmd.none

    | SelectConversation conversationId ->
        {
            model with
                ActiveConversation = Some conversationId
                Messages = []
                StreamingContent = ""
        },
        Cmd.OfRemoting.call aiApi.GetConversation conversationId (Finished >> LoadMessages) (fun ex ->
            ApiError ex.Message)

    | LoadMessages action ->
        match action with
        | Start conversationId ->
            model,
            Cmd.OfRemoting.call aiApi.GetConversation conversationId (Finished >> LoadMessages) (fun ex ->
                ApiError ex.Message)
        | Finished messages -> { model with Messages = messages }, Cmd.none

    | SubmitMessage content ->
        let conversationId = model.ActiveConversation |> Option.defaultWith Guid.NewGuid

        let userMsg: ConversationMessage = {
            Id = Guid.NewGuid()
            ConversationId = conversationId
            Participant = User
            Content = content
            Timestamp = DateTime.UtcNow
            ToolCalls = []
            RetrievedSources = []
            Parts = []
            CreatedBy = ""
        }

        // Debug-mode toggles are intercepted client-side and never reach
        // the provider. Surface an ack in the chat so the user can see
        // the state change inline.
        match tryDebugCommand content with
        | Some enabled ->
            let ack = debugAck conversationId enabled

            {
                model with
                    ActiveConversation = Some conversationId
                    Messages = model.Messages @ [ userMsg; ack ]
                    DebugMode = enabled
            },
            Cmd.none
        | None ->
            // Phase 6j.A: client-side fast-path resolver. Trivial UI
            // instructions ("go to sales analysis") resolve here
            // before the chat request leaves the page. Full-page
            // assistant has ActiveModule = None, so only
            // module-switch patterns can match — field patterns
            // require an active analytical module (side-panel
            // surface). A companion that ships a fast-path resolver
            // registers it into FastPathBridge at module load; when
            // no resolver is registered the bridge returns
            // `NoResolution` and the existing path runs.
            let fastPathRequest: FastPathBridge.FastPathRequest = {
                ConversationId = conversationId
                Instruction = content
                ActiveModule = None
                ActivePage = None
            }

            match FastPathBridge.tryResolve fastPathRequest with
            | FastPathBridge.Resolved(synthAssist, dispatchAction, sendBeacon) ->
                let dispatchCmd = Cmd.ofEffect (fun _ -> dispatchAction ())
                let beaconCmd = Cmd.ofEffect (fun _ -> sendBeacon ())

                {
                    model with
                        ActiveConversation = Some conversationId
                        Messages = model.Messages @ [ userMsg; synthAssist ]
                        StreamingContent = ""
                        WatchdogToken = None
                },
                Cmd.batch [ dispatchCmd; beaconCmd ]
            | FastPathBridge.NoResolution ->
                let watchdogId = Guid.NewGuid()

                {
                    model with
                        ActiveConversation = Some conversationId
                        Messages = model.Messages @ [ userMsg ]
                        StreamingContent = ""
                        WatchdogToken = Some watchdogId
                },
                // ActiveModule = None: the user is currently viewing the AI
                // assistant's own module page, not an analytical module. The
                // shell's side panel handles the active-module case separately.
                //
                // Per-conversation provider override: pull the stored
                // `OverrideProviderLabel` from the local conversations list
                // (kept in sync with the persisted server-side metadata).
                // Server defensively re-reads on submit, so no UI race
                // around dropdown change → first message.
                let storedOverride =
                    model.Conversations
                    |> List.tryFind (fun c -> c.Id = conversationId)
                    |> Option.bind _.OverrideProviderLabel

                let request: AIMessageRequest = {
                    ConversationId = conversationId
                    Content = content
                    ActiveModule = None
                    ActivePage = None
                    ActivePageNarrative = None
                    OverrideProviderLabel = storedOverride
                    // Full-page AI assistant module — Mode 1 + Mode 2.
                    // Client-resident `_platform.ui.*` tools are visible
                    // here when a companion that registers them is wired in.
                    Surface = FullPage
                }

                Cmd.batch [
                    Cmd.OfRemoting.call aiApi.SubmitMessage request (Finished >> MessageSubmitted) (fun ex ->
                        ApiError ex.Message)
                    scheduleWatchdog watchdogId
                ]

    | MessageSubmitted action ->
        match action with
        | Start _ -> model, Cmd.none
        | Finished task -> { model with CurrentTask = Some task }, Cmd.none

    | NewConversation ->
        {
            model with
                ActiveConversation = None
                Messages = []
                StreamingContent = ""
                CurrentTask = None
        },
        Cmd.none

    | DeleteConversation action ->
        match action with
        | Start conversationId ->
            model,
            Cmd.OfRemoting.call aiApi.DeleteConversation conversationId (Finished >> DeleteConversation) (fun ex ->
                ApiError ex.Message)
        | Finished(Ok()) ->
            {
                model with
                    ActiveConversation = None
                    Messages = []
            },
            Cmd.ofMsg (LoadConversations(Start()))
        | Finished(Error err) -> { model with ErrorMessage = Some err }, Cmd.none

    | SetProviderOverride newLabel ->
        match model.ActiveConversation with
        | Some convId ->
            // Optimistic local update — server is the source of truth
            // but the dropdown UI shouldn't flicker while the round-trip
            // is in flight. `ProviderOverrideSet (Error _)` reverts via
            // the explicit error surface; success is a no-op.
            let updatedConversations =
                model.Conversations
                |> List.map (fun c ->
                    if c.Id = convId then
                        {
                            c with
                                OverrideProviderLabel = newLabel
                        }
                    else
                        c)

            {
                model with
                    Conversations = updatedConversations
            },
            Cmd.OfRemoting.call aiApi.SetConversationOverride (convId, newLabel) ProviderOverrideSet (fun ex ->
                ApiError ex.Message)
        | None -> model, Cmd.none

    | ProviderOverrideSet result ->
        match result with
        | Ok() -> model, Cmd.none
        | Error err ->
            // Server rejected the override (unknown label, scope
            // doesn't permit persistence, …). Reload the canonical
            // server view so the dropdown re-syncs.
            { model with ErrorMessage = Some err }, Cmd.ofMsg (LoadConversations(Start()))

    | SSEEvent event ->
        match event with
        | MessageDelta(convId, content) ->
            match model.ActiveConversation with
            | Some activeId when activeId = convId ->
                {
                    model with
                        StreamingContent = model.StreamingContent + content
                },
                Cmd.none
            | _ -> model, Cmd.none

        | MessageComplete(convId, _msgId) ->
            match model.ActiveConversation with
            | Some activeId when activeId = convId ->
                let assistantMsg: ConversationMessage = {
                    Id = Guid.NewGuid()
                    ConversationId = convId
                    Participant = AIAssistant
                    Content = model.StreamingContent
                    Timestamp = DateTime.UtcNow
                    ToolCalls = []
                    RetrievedSources = []
                    Parts = []
                    CreatedBy = ""
                }

                // Clear the watchdog — the response arrived within the
                // timeout. A stale `WatchdogFired` scheduled earlier
                // will see `WatchdogToken = None` and no-op.
                {
                    model with
                        Messages = model.Messages @ [ assistantMsg ]
                        StreamingContent = ""
                        WatchdogToken = None
                },
                Cmd.none
            | _ -> model, Cmd.none

        | TaskStatusChanged(_taskId, status) ->
            let taskUpdate =
                match model.CurrentTask with
                | Some task ->
                    Some {
                        task with
                            Status = status
                            CompletedAt =
                                match status with
                                | AITaskCompleted
                                | AITaskFailed _ -> Some DateTime.UtcNow
                                | _ -> task.CompletedAt
                    }
                | None -> None

            // In debug mode, surface task failures inline so the chat
            // shows why an agent loop bailed (rate-limit, max turns
            // exceeded, malformed provider response, etc.) without the
            // operator having to consult the server log.
            match status, model.DebugMode, model.ActiveConversation with
            | AITaskFailed reason, true, Some convId ->
                let note = debugNote convId $"⚠️ Agent-loop failure: {reason}"

                {
                    model with
                        CurrentTask = taskUpdate
                        Messages = model.Messages @ [ note ]
                        StreamingContent = ""
                },
                Cmd.none
            | _ -> { model with CurrentTask = taskUpdate }, Cmd.none

        | ToolCallStarted(convId, toolName, toolCallId) ->
            match model.ActiveConversation with
            | Some activeId when activeId = convId ->
                if model.DebugMode then
                    let note = debugNote convId $"🔧 Calling tool `{toolName}` (id {toolCallId})"

                    {
                        model with
                            Messages = model.Messages @ [ note ]
                    },
                    Cmd.none
                else
                    // Non-debug view — keep the inline ephemeral marker
                    // in the streaming buffer so the user sees the tool
                    // is running.
                    {
                        model with
                            StreamingContent = model.StreamingContent + $"\n[Calling {toolName}...]"
                    },
                    Cmd.none
            | _ -> model, Cmd.none

        | ToolCallCompleted(convId, _toolCallId, content) ->
            match model.ActiveConversation, model.DebugMode with
            | Some activeId, true when activeId = convId ->
                let note = debugNote convId $"↳ Tool result: {truncateForDebug 500 content}"

                {
                    model with
                        Messages = model.Messages @ [ note ]
                },
                Cmd.none
            | _ -> model, Cmd.none

        | StreamError errorMsg ->
            let withNote =
                match model.DebugMode, model.ActiveConversation with
                | true, Some convId ->
                    let note = debugNote convId $"⚠️ Stream error: {errorMsg}"

                    {
                        model with
                            Messages = model.Messages @ [ note ]
                    }
                | _ -> model

            {
                withNote with
                    ErrorMessage = Some errorMsg
            },
            Cmd.none

        // Phase 6g.A: ClientToolInvoke is intercepted by SSEClient and
        // routed into ClientToolRuntime — never reaches the Elmish
        // dispatch. Match arm exists only to satisfy exhaustiveness.
        | ClientToolInvoke(_, _, _, _, _, _) -> model, Cmd.none

        // Phase 6h: user clicked Cancel and the server has confirmed
        // the agent loop is bailing. Commit whatever was streaming
        // (so the user keeps the partial response) and clear the
        // watchdog so it doesn't fire stale.
        | StreamCancelled convId ->
            match model.ActiveConversation with
            | Some activeId when activeId = convId ->
                let committed =
                    if model.StreamingContent <> "" then
                        let assistantMsg: ConversationMessage = {
                            Id = Guid.NewGuid()
                            ConversationId = convId
                            Participant = AIAssistant
                            Content = model.StreamingContent + "\n\n*[Cancelled by user]*"
                            Timestamp = DateTime.UtcNow
                            ToolCalls = []
                            RetrievedSources = []
                            Parts = []
                            CreatedBy = ""
                        }

                        model.Messages @ [ assistantMsg ]
                    else
                        model.Messages

                {
                    model with
                        Messages = committed
                        StreamingContent = ""
                        WatchdogToken = None
                },
                Cmd.none
            | _ -> model, Cmd.none

    | ApiError errorMsg ->
        // Clear the watchdog so a stale timer doesn't fire after the
        // error is already reported via `ErrorMessage`.
        {
            model with
                ErrorMessage = Some errorMsg
                WatchdogToken = None
        },
        Cmd.none
    | DismissError -> { model with ErrorMessage = None }, Cmd.none

    | CancelTask taskId ->
        // Fire-and-forget POST to /api/ai/cancel/{taskId}. The server
        // confirms cancellation by emitting StreamCancelled on the
        // SSE stream; the matching arm above commits the streaming
        // buffer and clears state. Failures (404 task already done)
        // are silent — the user's intent is already satisfied.
        let postCancel =
            Cmd.ofEffect (fun _ ->
                let userId = UserSession.getUserId ()

                let opts =
                    createObj [ "method" ==> "POST"; "headers" ==> createObj [ "X-User-Id" ==> userId ] ]

                Browser.Dom.window?fetch ($"/api/ai/cancel/{taskId}", opts) |> ignore)

        model, postCancel

    | WatchdogFired token ->
        // Token-based invalidation: only fire when the live token still
        // matches. `None` (response arrived) and mismatched Guid (newer
        // stream has started) both collapse to "stale — ignore".
        match model.WatchdogToken, model.ActiveConversation with
        | Some liveToken, Some convId when liveToken = token ->
            let note = watchdogNote convId

            {
                model with
                    Messages = model.Messages @ [ note ]
                    StreamingContent = ""
                    WatchdogToken = None
            },
            Cmd.none
        | _ -> model, Cmd.none

// ─── View ─────────────────────────────────────────────────────────

let private conversationList (conversations: Conversation list) activeId dispatch =
    Html.div [
        prop.className "space-y-1"
        prop.children [
            // New conversation button
            Html.button [
                prop.className
                    "w-full text-left px-3 py-2 text-sm font-medium text-violet-600 hover:bg-violet-50 rounded-lg transition-colors"
                prop.onClick (fun _ -> dispatch NewConversation)
                prop.text "+ New conversation"
            ]

            for conv in conversations do
                let isActive = activeId = Some conv.Id

                Html.div [
                    prop.className [
                        "px-3 py-2 rounded-lg cursor-pointer text-sm transition-colors"
                        if isActive then
                            "bg-violet-50 text-violet-700"
                        else
                            "text-gray-600 hover:bg-gray-50"
                    ]
                    prop.onClick (fun _ -> dispatch (SelectConversation conv.Id))
                    prop.children [
                        Html.span [ prop.text (conv.Title |> Option.defaultValue (conv.Id.ToString()[..7])) ]
                    ]
                ]
        ]
    ]

let private messageView (msg: ConversationMessage) =
    let isUser = msg.Participant = User

    Html.div [
        prop.className [
            "max-w-[80%] rounded-lg px-4 py-3 text-sm"
            if isUser then
                "ml-auto bg-violet-500 text-white"
            else
                "mr-auto bg-gray-100 text-gray-800"
        ]
        prop.children [
            Toolup.Markdown.render msg.Content

            if not msg.ToolCalls.IsEmpty then
                Html.div [
                    prop.className "mt-2 pt-2 border-t border-gray-200 space-y-1"
                    prop.children [
                        for tc in msg.ToolCalls do
                            ConversationPanel.ToolCallPill tc
                    ]
                ]

            if not isUser && not msg.RetrievedSources.IsEmpty then
                ConversationPanel.MessageSources msg.RetrievedSources msg.Content
        ]
    ]

let private taskStatusBadge (task: AITask) =
    let (text, color) =
        match task.Status with
        | Queued -> "Queued", "bg-gray-100 text-gray-600"
        | InProgress -> "Running...", "bg-blue-100 text-blue-700"
        | AITaskCompleted -> "Complete", "bg-green-100 text-green-700"
        | AITaskFailed err -> $"Failed: {err}", "bg-red-100 text-red-700"

    Html.span [
        prop.className $"inline-block px-2 py-1 rounded text-xs font-medium {color}"
        prop.text text
    ]

// ─── Knowledge Base ingestion banner ─────────────────────────────
//
// Subscribes to `NotificationClient` for `CustomNotification` envelopes
// keyed `KnowledgeBase.IngestionStatus`. When a document finishes
// ingesting (success or failure), shows a transient banner above the
// chat so the user sees the document is now searchable from the
// assistant. Self-contained React state — not threaded through the
// Elmish model. The wire payload mirrors KB's `IngestionStatusUpdate`;
// kept locally to avoid a cross-module reference (AI is a companion,
// not a downstream consumer of the KB module).

[<Literal>]
let private kbIngestionStatusKey = "KnowledgeBase.IngestionStatus"

type private KbStatusPayload = {
    DocumentId: string
    FileName: string
    Outcome: string
    ChunkCount: int
    ErrorReason: string
    UploadedBy: string
}

type private KbBanner = {
    Id: Guid
    Outcome: string
    Text: string
    DismissAt: DateTime
}

let private kbBannerText (p: KbStatusPayload) =
    match p.Outcome with
    | "Complete" ->
        sprintf
            "Knowledge Base · indexed \u201C%s\u201D (%d chunks). It's now searchable from chat."
            p.FileName
            p.ChunkCount
    | "Failed" -> sprintf "Knowledge Base · failed to index \u201C%s\u201D: %s" p.FileName p.ErrorReason
    | _ -> ""

let private kbBannerClasses (outcome: string) =
    match outcome with
    | "Complete" -> "bg-emerald-50 border-emerald-300 text-emerald-900"
    | _ -> "bg-amber-50 border-amber-400 text-amber-900"

[<ReactComponent>]
let private KbStatusBanner () =
    let banner, setBanner = React.useState<KbBanner option> None

    React.useEffectOnce (fun () ->
        let dispose =
            NotificationClient.subscribe (fun envelope ->
                match envelope.Notification with
                | Notification.CustomNotification(key, payloadJson) when key = kbIngestionStatusKey ->
                    try
                        let p = Json.parseAs<KbStatusPayload> payloadJson
                        let text = kbBannerText p

                        if text <> "" then
                            setBanner (
                                Some {
                                    Id = envelope.Id
                                    Outcome = p.Outcome
                                    Text = text
                                    DismissAt = DateTime.UtcNow.AddSeconds 6.0
                                }
                            )
                    with _ ->
                        ()
                | _ -> ())

        FsReact.createDisposable (fun () -> dispose ()))

    // Tick every second to expire the banner once `DismissAt` passes.
    // Mirrors the auto-dismiss tick in `ToastCentre`.
    React.useEffect (
        (fun () ->
            let intervalId =
                JS.setInterval
                    (fun () ->
                        match banner with
                        | Some b when b.DismissAt <= DateTime.UtcNow -> setBanner None
                        | _ -> ())
                    1000

            FsReact.createDisposable (fun () -> JS.clearInterval intervalId)),
        [| box banner |]
    )

    match banner with
    | None -> Html.none
    | Some b ->
        Html.div [
            prop.key (string b.Id)
            prop.className $"mb-3 px-3 py-2 rounded-lg border text-sm cursor-pointer {kbBannerClasses b.Outcome}"
            prop.onClick (fun _ -> setBanner None)
            prop.text b.Text
        ]

[<ReactComponent>]
let private MessageInput (messages: ConversationMessage list) (onSubmit: string -> unit) =
    // Local React state for the input + history navigation. See the
    // matching component in `ConversationPanel.fs` for the detailed
    // rationale; this mirror exists because the side panel and the
    // full AI module page render distinct React component trees and
    // can't share the hook state.
    let inputText, setInputText = React.useState ""
    let historyIndex, setHistoryIndex = React.useState 0
    let savedDraft, setSavedDraft = React.useState ""

    let userHistory =
        messages |> List.filter (fun m -> m.Participant = User) |> List.rev

    let submitNow () =
        if inputText.Trim() <> "" then
            onSubmit (inputText.Trim())
            setInputText ""
            setHistoryIndex 0
            setSavedDraft ""

    Html.div [
        prop.className "flex gap-2"
        prop.children [
            Html.input [
                prop.className
                    "flex-1 border border-gray-300 rounded-lg px-4 py-2 text-sm focus:outline-none focus:ring-2 focus:ring-violet-400"
                prop.placeholder "Describe your analytical task..."
                prop.value inputText
                prop.onChange (fun (newText: string) ->
                    setInputText newText

                    if historyIndex <> 0 then
                        setHistoryIndex 0
                        setSavedDraft "")
                prop.onKeyDown (fun e ->
                    match e.key with
                    | "Enter" -> submitNow ()
                    | "ArrowUp" when not userHistory.IsEmpty && historyIndex < userHistory.Length ->
                        e.preventDefault ()

                        if historyIndex = 0 then
                            setSavedDraft inputText

                        let newIdx = historyIndex + 1
                        setHistoryIndex newIdx
                        setInputText userHistory[newIdx - 1].Content
                    | "ArrowDown" when historyIndex > 0 ->
                        e.preventDefault ()
                        let newIdx = historyIndex - 1
                        setHistoryIndex newIdx

                        if newIdx = 0 then
                            setInputText savedDraft
                        else
                            setInputText userHistory[newIdx - 1].Content
                    | _ -> ())
            ]
            Html.button [
                prop.className
                    "bg-violet-500 hover:bg-violet-600 text-white px-6 py-2 rounded-lg text-sm font-medium disabled:opacity-50"
                prop.disabled (inputText.Trim() = "")
                prop.onClick (fun _ -> submitNow ())
                prop.text "Send"
            ]
        ]
    ]

let private view model dispatch =
    let inputPanel =
        Layout.Panel.panel "Conversations" [
            conversationList model.Conversations model.ActiveConversation dispatch

            if model.AvailableTools.Length > 0 then
                Layout.Panel.panelSection "Available Tools" [
                    for tool in model.AvailableTools do
                        Html.div [
                            prop.className "text-xs text-gray-500 py-1"
                            prop.children [
                                Html.span [ prop.className "font-mono text-gray-700"; prop.text tool.Name ]
                                Html.span [ prop.text $" \u2014 {tool.Description}" ]
                            ]
                        ]
                ]
        ]

    let activeOverrideLabel =
        match model.ActiveConversation with
        | Some convId ->
            model.Conversations
            |> List.tryFind (fun c -> c.Id = convId)
            |> Option.bind _.OverrideProviderLabel
        | None -> None

    // Show the picker only when (a) the user has selected a
    // conversation (so an override can be persisted against an Id)
    // and (b) configured providers exist (otherwise account default
    // is the only resolvable option).
    let providerPicker =
        match model.ActiveConversation, model.ConfiguredProviders with
        | Some _, providers when not providers.IsEmpty ->
            Html.div [
                prop.className "mb-3 flex items-center gap-2 text-sm"
                prop.children [
                    Html.label [ prop.className "text-gray-600 font-medium"; prop.text "Provider:" ]
                    Html.select [
                        prop.className
                            "border border-gray-300 rounded-lg px-3 py-1.5 text-sm bg-white focus:outline-none focus:ring-2 focus:ring-violet-400"
                        prop.value (activeOverrideLabel |> Option.defaultValue "")
                        prop.onChange (fun (v: string) ->
                            let next = if v = "" then None else Some v
                            dispatch (SetProviderOverride next))
                        prop.children [
                            Html.option [ prop.value ""; prop.text "Account default" ]
                            for p in providers do
                                Html.option [ prop.value p.Label; prop.text p.Label ]
                        ]
                    ]
                ]
            ]
        | _ -> Html.none

    // Phase 6h: export the active conversation as Markdown / JSON.
    // Always rendered when a conversation is active (button auto-
    // disables when there are no messages yet); positioned to the
    // right of the provider picker with `ml-auto`.
    let exportMenu =
        match model.ActiveConversation with
        | Some convId ->
            let conversationName =
                model.Conversations
                |> List.tryFind (fun c -> c.Id = convId)
                |> Option.bind _.Title
                |> Option.defaultValue "Conversation"

            Html.div [
                prop.className "mb-3 flex justify-end"
                prop.children [ ConversationPanel.ExportMenu conversationName model.Messages ]
            ]
        | None -> Html.none

    let outputPanel =
        Layout.Panel.panel "AI Assistant" [
            providerPicker
            exportMenu

            // Transient banner: surfaced when a Knowledge Base document
            // finishes ingesting in the background, so the user sees it's
            // become searchable without waiting for a manual refresh.
            KbStatusBanner()

            // Messages
            Html.div [
                prop.className "space-y-3 mb-4 min-h-[200px] max-h-[60vh] overflow-y-auto"
                prop.children [
                    if model.Messages.IsEmpty && model.StreamingContent = "" then
                        Html.p [
                            prop.className "text-gray-400 text-sm text-center py-8"
                            prop.text "Start a conversation or select one from the left."
                        ]
                    else
                        for msg in model.Messages do
                            messageView msg

                        // Streaming content
                        if model.StreamingContent <> "" then
                            Html.div [
                                prop.className
                                    "mr-auto max-w-[80%] rounded-lg px-4 py-3 text-sm bg-gray-100 text-gray-800"
                                prop.children [
                                    Toolup.Markdown.render model.StreamingContent
                                    Html.span [ prop.className "inline-block w-2 h-4 bg-gray-400 animate-pulse ml-1" ]
                                ]
                            ]
                ]
            ]

            // Task status + Phase 6h cancel button (only while running)
            match model.CurrentTask with
            | Some task ->
                Html.div [
                    prop.className "mb-3 flex items-center gap-2"
                    prop.children [
                        taskStatusBadge task
                        match task.Status with
                        | Queued
                        | InProgress ->
                            Html.button [
                                prop.className [
                                    "px-2 py-1 rounded text-xs"
                                    "border border-gray-300 bg-white hover:bg-gray-50"
                                    "text-gray-700"
                                ]
                                prop.title
                                    "Cancel the in-flight AI response. Whatever has streamed so far will be kept."
                                prop.onClick (fun _ -> dispatch (CancelTask task.TaskId))
                                prop.text "✕ Cancel"
                            ]
                        | _ -> ()
                    ]
                ]
            | None -> ()

            // Input. `model.Messages` is passed so Up/Down arrows can
            // scroll through the user's prior messages in this
            // conversation (shell-history style).
            MessageInput model.Messages (fun content -> dispatch (SubmitMessage content))

            // Error display
            match model.ErrorMessage with
            | Some err ->
                Html.div [
                    prop.className
                        "mt-3 p-3 bg-red-50 border border-red-200 rounded text-red-700 text-sm cursor-pointer"
                    prop.onClick (fun _ -> dispatch DismissError)
                    prop.text err
                ]
            | None -> ()
        ]

    inputPanel, outputPanel

// ─── Module creation ──────────────────────────────────────────────

/// Create the built-in AI assistant as an ErasedModule.
/// Mirrors the FileManagerUI.create pattern exactly.
let create (branding: AIAssistantClientBranding option) : ErasedModule =
    let name = branding |> Option.map _.Name |> Option.defaultValue "AI Assistant"

    let icon =
        branding |> Option.map _.Icon |> Option.defaultValue ToolUp.AI.Icons.aiAssistant

    let route = "/" + name.ToLower().Replace(" ", "-")

    // Companion-provided built-in — `Id` is reserved under the `_ai.`
    // namespace so it can never collide with an app's RBAC-managed
    // `ServerConfig.ModuleNames` key.
    ToolUp.Platform.ClientModule.create {
        Init = init
        Update = update
        Name = name
        Icon = icon
    }
    |> ToolUp.Platform.ClientModule.withId "_ai.AIAssistant"
    |> ToolUp.Platform.ClientModule.withView view
    // Declared "AI" group so the assistant lands in a positionable
    // section between the app's work groups and the Admin block —
    // ungrouped (`_other`) would force it to the very bottom of the
    // sidebar, below Admin.
    |> ToolUp.Platform.ClientModule.withGroup "AI"
    |> ToolUp.Platform.ClientModule.withVisibility ToolUp.Platform.Visibility.visibleToAuthenticated
    |> ToolUp.Platform.ClientModule.register