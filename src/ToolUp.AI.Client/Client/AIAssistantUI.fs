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
    /// When true, tool invocations and results are surfaced inline in the
    /// conversation as synthetic assistant messages prefixed with `[debug]`.
    /// Toggled by the user typing `Debug on` / `Debug off` in the chat
    /// (intercepted client-side — never sent to the provider). Always starts
    /// false; session-scoped (not persisted).
    DebugMode: bool
    /// The idle watchdog's live token — `Some` while a turn is in flight
    /// (`ChatStream.fire`). Catches silent failures where the server never
    /// produces a final `MessageComplete`.
    WatchdogToken: Guid option
    /// The tool the live turn is running, between its start and completion.
    RunningTool: string option
    /// Parts a tool attached to the reply streaming in; they ride it into
    /// the committed message.
    InFlightParts: ToolUp.Platform.AI.AIContentPart list
    /// The search the conversation list is filtered by;
    /// `""` lists everything.
    ConversationSearch: string
    /// The cursor for the next page of the list; `None`
    /// when every matching conversation is loaded.
    ConversationsCursor: string option
    /// How many conversations match the current search
    /// across all pages.
    ConversationTotal: int
}

/// Non-deterministic inputs for a chat submission — generated in the
/// `Cmd` layer (MVU purity: `Guid.NewGuid()` / `DateTime.UtcNow` never
/// run inside the pure `update`) and folded back into the state via
/// `SubmitMessagePrepared`.
type PreparedSubmission = {
    /// The user's chat input, unchanged from `SubmitMessage`.
    Content: string
    /// The active conversation id, or a freshly-generated one when no
    /// conversation is active.
    ConversationId: Guid
    /// Id for the user's `ConversationMessage`.
    MessageId: Guid
    /// Timestamp for the user's `ConversationMessage`.
    Timestamp: DateTime
    /// Token for the stream-idle watchdog (used only on the
    /// non-fast-path branch).
    WatchdogId: Guid
}

type Msg =
    | LoadConversations of ApiCall<unit, Conversation list>
    | LoadTools of ApiCall<unit, AIToolDefinition list>
    | LoadProviders of ApiCall<unit, AIUserConfigView>
    | SelectConversation of Guid
    | LoadMessages of ApiCall<Guid, ConversationMessage list>
    | SubmitMessage of string
    /// Second leg of `SubmitMessage`: the `Cmd` layer generated the
    /// ids/timestamp; this folds the prepared submission into state.
    | SubmitMessagePrepared of PreparedSubmission
    /// The fast-path resolver's answer for a submitted message: either the
    /// turn is answered on the client, or `request` goes to the server.
    | FastPathAnswered of request: AIMessageRequest * outcome: FastPathBridge.ResolveOutcome
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
    /// User clicked Cancel on the in-flight task (`AIStream.cancel`); the
    /// agent loop bails at the next turn boundary with a StreamCancelled
    /// event.
    | CancelTask of taskId: Guid
    /// Fetch a page of the conversation list. A query with
    /// no cursor replaces the list; one with a cursor appends to it.
    | LoadConversationPage of ConversationListQuery
    /// A page arrived for `search`. Ignored when the user
    /// has since searched for something else.
    | ConversationPageLoaded of search: string * append: bool * page: ConversationPage
    /// The user submitted a search (`""` clears it).
    | SearchConversations of string
    /// The user asked for the next page.
    | LoadMoreConversations
    /// The answer to a `GetTaskStatus` poll made when the
    /// stream went quiet. `None` when the server no longer knows the task
    /// or the poll failed.
    | TaskStatusPolled of token: Guid * status: AITask option

let private aiApi =
    Api.makeProxy<AIAssistantApi> (customOptions = UserSession.withRequestHeaders)

let private aiSettingsApi =
    Api.makeProxy<AISettingsApi> (customOptions = UserSession.withRequestHeaders)

// ─── Init ─────────────────────────────────────────────────────────

// ─── The stream ───────────────────────────────────────────────────

let private streamOf (model: Model) : ChatStream.ChatStreamState = {
    Conversation = model.ActiveConversation
    Streaming = model.StreamingContent
    Parts = model.InFlightParts
    Task = model.CurrentTask
    RunningTool = model.RunningTool
    Watchdog = model.WatchdogToken
    Debug = model.DebugMode
}

/// Write the reduced stream state back and apply its effects the full
/// page's way (a fault in the error banner). An unchanged state with nothing
/// to do leaves the model as it was, by reference.
let private reduceStream
    (step: ChatStream.ChatStreamState -> ChatStream.ChatStreamState * ChatStream.StreamEffect list)
    (model: Model)
    : Model * Cmd<Msg> =
    let before = streamOf model
    let after, effects = step before

    if obj.ReferenceEquals(before, after) && List.isEmpty effects then
        model, Cmd.none
    else
        let written = {
            model with
                StreamingContent = after.Streaming
                InFlightParts = after.Parts
                CurrentTask = after.Task
                RunningTool = after.RunningTool
                WatchdogToken = after.Watchdog
                DebugMode = after.Debug
        }

        effects
        |> List.fold
            (fun (m: Model, cmds) effect ->
                match effect with
                | ChatStream.Commit message
                | ChatStream.Note message ->
                    {
                        m with
                            Messages = m.Messages @ [ message ]
                    },
                    cmds
                | ChatStream.Fault message -> { m with ErrorMessage = Some message }, cmds
                | ChatStream.RefreshList -> m, cmds @ [ Cmd.ofMsg (LoadConversations(Start())) ]
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
                        Cmd.OfRemoting.call aiApi.GetConversation conversationId (Finished >> LoadMessages) (fun ex ->
                            ApiError ex.Message)
                    ])
            (written, [])
        |> fun (m, cmds) -> m, Cmd.batch cmds

/// The first-page query for `search`.
let private firstPageFor (search: string) : ConversationListQuery = {
    ConversationListQuery.firstPage with
        Search =
            if String.IsNullOrWhiteSpace search then
                None
            else
                Some(search.Trim())
}

let private loadPage (query: ConversationListQuery) : Cmd<Msg> =
    let search = query.Search |> Option.defaultValue ""
    let append = query.Cursor.IsSome

    Cmd.OfRemoting.call
        aiApi.ListConversationsPage
        query
        (fun page -> ConversationPageLoaded(search, append, page))
        (fun ex -> ApiError ex.Message)

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
        RunningTool = None
        InFlightParts = []
        ConversationSearch = ""
        ConversationsCursor = None
        ConversationTotal = 0
    },
    Cmd.batch [
        loadPage ConversationListQuery.firstPage
        Cmd.OfRemoting.call aiApi.GetAvailableTools () (Finished >> LoadTools) (fun ex -> ApiError ex.Message)
        Cmd.OfRemoting.call aiSettingsApi.GetMyConfig () (Finished >> LoadProviders) (fun ex -> ApiError ex.Message)
    // The SSE stream subscription is NOT a boot-time `Cmd.ofEffect` —
    // that pattern discarded the unsubscribe handle, orphaning an
    // EventSource on every mount / HMR / navigation. It is owned by the
    // `SseSubscription` component (below), whose `useEffectOnce` disposer
    // closes the stream on unmount. See `AIClientConfig.fs` for the
    // matching shell-side lifetime-aware handle.
    ]

// ─── Update ───────────────────────────────────────────────────────

let update msg model =
    match msg with
    | LoadConversations action ->
        match action with
        // A refresh reloads the first page of the current
        // search rather than every conversation in the scope.
        | Start() -> model, loadPage (firstPageFor model.ConversationSearch)
        | Finished convos -> { model with Conversations = convos }, Cmd.none

    | LoadConversationPage query -> model, loadPage query

    | ConversationPageLoaded(search, append, page) ->
        if search <> model.ConversationSearch.Trim() then
            model, Cmd.none
        else
            {
                model with
                    Conversations =
                        if append then
                            model.Conversations @ page.Items
                        else
                            page.Items
                    ConversationsCursor = page.NextCursor
                    ConversationTotal = page.TotalCount
            },
            Cmd.none

    | SearchConversations term ->
        {
            model with
                ConversationSearch = term.Trim()
                ConversationsCursor = None
        },
        loadPage (firstPageFor term)

    | LoadMoreConversations ->
        match model.ConversationsCursor with
        | Some cursor ->
            model,
            loadPage {
                firstPageFor model.ConversationSearch with
                    Cursor = Some cursor
            }
        | None -> model, Cmd.none

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
        // MVU purity (2026-07 idiom sweep): `Guid.NewGuid()` /
        // `DateTime.UtcNow` are non-deterministic and belong in the
        // `Cmd` layer, not in the pure `update`. The effect generates
        // the ids/timestamp and immediately re-dispatches the prepared
        // submission; `SubmitMessagePrepared` below is the pure fold.
        model,
        Cmd.ofEffect (fun dispatch ->
            dispatch (
                SubmitMessagePrepared {
                    Content = content
                    ConversationId = model.ActiveConversation |> Option.defaultWith Guid.NewGuid
                    MessageId = Guid.NewGuid()
                    Timestamp = DateTime.UtcNow
                    WatchdogId = Guid.NewGuid()
                }
            ))

    | SubmitMessagePrepared prepared ->
        let content = prepared.Content
        let conversationId = prepared.ConversationId

        let userMsg = {
            ConversationMessage.note User conversationId content with
                Id = prepared.MessageId
                Timestamp = prepared.Timestamp
        }

        match ChatStream.debugCommand content with
        | Some enabled ->
            {
                model with
                    ActiveConversation = Some conversationId
                    Messages = model.Messages @ [ userMsg; ChatStream.debugAck conversationId enabled ]
                    DebugMode = enabled
            },
            Cmd.none
        | None ->
            // The full page has no active analytical module, so a fast-path
            // resolver can match only module-switch patterns here.
            let fastPathRequest: FastPathBridge.FastPathRequest = {
                ConversationId = conversationId
                Instruction = content
                ActiveModule = None
                ActivePage = None
            }

            // The per-conversation provider override is read from the local
            // conversations list (kept in sync with the persisted metadata);
            // the server re-reads it on submit, so a dropdown change racing
            // the first message is harmless.
            let storedOverride =
                model.Conversations
                |> List.tryFind (fun c -> c.Id = conversationId)
                |> Option.bind _.OverrideProviderLabel

            // Full-page assistant — Mode 1 + Mode 2, so client-resident
            // `_platform.ui.*` tools are visible when a companion registers
            // them. The built-in panel does not scope its own retrieval.
            let request: AIMessageRequest = {
                ConversationId = conversationId
                Content = content
                ActiveModule = None
                ActivePage = None
                ActivePageNarrative = None
                OverrideProviderLabel = storedOverride
                Surface = FullPage
                RetrievalFilters = None
            }

            let sent, armCmd =
                {
                    model with
                        ActiveConversation = Some conversationId
                        Messages = model.Messages @ [ userMsg ]
                }
                |> reduceStream (ChatStream.start prepared.WatchdogId)

            sent,
            Cmd.batch [
                armCmd
                Cmd.OfAsync.perform FastPathBridge.resolve fastPathRequest (fun outcome ->
                    FastPathAnswered(request, outcome))
            ]

    | FastPathAnswered(request, _) when model.ActiveConversation <> Some request.ConversationId -> model, Cmd.none

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
        model,
        Cmd.OfRemoting.call aiApi.SubmitMessage request (Finished >> MessageSubmitted) (fun ex -> ApiError ex.Message)

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

    | SSEEvent event -> model |> reduceStream (fun s -> ChatStream.reduce s event)

    | ApiError errorMsg ->
        // Clear the watchdog so a stale timer doesn't fire after the
        // error is already reported via `ErrorMessage`.
        {
            model with
                ErrorMessage = Some errorMsg
                WatchdogToken = None
                // Terminal: clear the running task so the spinner/Cancel don't hang.
                CurrentTask = None
        },
        Cmd.none
    | DismissError -> { model with ErrorMessage = None }, Cmd.none

    | CancelTask taskId -> model, Cmd.ofEffect (fun _ -> ChatStream.AIStream.cancel taskId)

    | WatchdogFired token -> model |> reduceStream (ChatStream.fire token)

    | TaskStatusPolled(token, status) -> model |> reduceStream (ChatStream.polled token status)

// ─── View ─────────────────────────────────────────────────────────

/// The conversation search box. Local state holds the
/// draft; the search runs on Enter (or when the box is cleared), so a
/// scan of the scope is not issued per keystroke.
[<ReactComponent>]
let private ConversationSearchBox (current: string) (onSearch: string -> unit) =
    let draft, setDraft = React.useState current

    Html.input [
        prop.className
            "w-full border border-gray-300 rounded-lg px-3 py-1.5 text-sm focus:outline-none focus:ring-2 focus:ring-violet-400"
        prop.placeholder "Search conversations\u2026"
        prop.value draft
        prop.onChange (fun (text: string) ->
            setDraft text

            if text = "" && current <> "" then
                onSearch "")
        prop.onKeyDown (fun e ->
            if e.key = "Enter" then
                onSearch draft)
    ]

/// One row's secondary line: message count and last activity.
let private conversationMeta (conv: Conversation) =
    let messages =
        if conv.MessageCount = 1 then
            "1 message"
        else
            sprintf "%d messages" conv.MessageCount

    if conv.UpdatedAt = DateTime.MinValue then
        messages
    else
        sprintf "%s \u00B7 %s" messages (conv.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"))

let private conversationList (model: Model) dispatch =
    let conversations = model.Conversations
    let activeId = model.ActiveConversation

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

            ConversationSearchBox model.ConversationSearch (SearchConversations >> dispatch)

            if conversations.IsEmpty && model.ConversationSearch <> "" then
                Html.div [
                    prop.className "px-3 py-2 text-xs text-gray-500"
                    prop.text (sprintf "No conversations match \u201C%s\u201D." model.ConversationSearch)
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
                        Html.div [
                            prop.className "truncate"
                            prop.text (conv.Title |> Option.defaultValue (conv.Id.ToString()[..7]))
                        ]
                        Html.div [ prop.className "text-xs text-gray-400"; prop.text (conversationMeta conv) ]
                    ]
                ]

            if model.ConversationsCursor.IsSome then
                Html.button [
                    prop.className
                        "w-full text-center px-3 py-1.5 text-xs text-violet-600 hover:bg-violet-50 rounded-lg transition-colors"
                    prop.onClick (fun _ -> dispatch LoadMoreConversations)
                    prop.text (sprintf "Load more (%d more)" (model.ConversationTotal - conversations.Length))
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

/// The full page's subscription to the tab's AI stream.
let internal streamSubscription (dispatch: Msg -> unit) : IDisposable =
    ChatStream.AIStream.subscribe (SSEEvent >> dispatch)

[<ReactComponent>]
let private SseSubscription (dispatch: Msg -> unit) =
    // The subscription lives as long as the page is mounted: the
    // `useEffectOnce` disposer ends it on unmount (navigation away / HMR).
    // Renders nothing — it exists purely for the lifecycle binding.
    React.useEffectOnce (fun () -> streamSubscription dispatch)

    Html.none

let private view model dispatch =
    let inputPanel =
        Layout.Panel.panel "Conversations" [
            conversationList model dispatch

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

    // Export the active conversation as Markdown / JSON.
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
            // Owns the SSE stream lifecycle for this module page —
            // subscribes on mount, closes the EventSource on unmount.
            SseSubscription dispatch

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

                        match ChatStream.turnStatus (streamOf model) with
                        | Some status ->
                            Html.div [
                                prop.role "status"
                                prop.className "mr-auto text-sm text-gray-500 italic"
                                prop.text status
                            ]
                        | None -> ()

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

            // Task status + cancel button (only while running)
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

            // Who can see what is typed above.
            ConversationPanel.ConversationVisibilityNotice()

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