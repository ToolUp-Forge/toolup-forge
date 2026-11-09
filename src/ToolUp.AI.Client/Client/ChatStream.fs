// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The AI stream on the client: one reducer semantics for every chat surface,
/// and one connection per tab.
///
/// `ChatStream.reduce` folds an `AIStreamEvent` into the stream-facing slice of
/// a surface's model and says what else must happen as `StreamEffect`s; a
/// surface projects its model to `ChatStreamState`, reduces, writes the state
/// back and applies the effects its own way (the side panel shows a fault as
/// an inline note, the full page in its banner). So every surface fails, times
/// out and commits alike. `AIStream` is the tab's one subscription: it routes
/// the out-of-band cases once and fans the rest out to the mounted surfaces.
module ToolUp.AI.Client.ChatStream

open System
open ToolUp.AI

/// The stream-facing slice of a chat surface's model.
type ChatStreamState = {
    /// The conversation the surface shows; events of any other are ignored.
    Conversation: Guid option
    /// The reply streaming in.
    Streaming: string
    /// Parts a tool attached to the reply streaming in, in arrival order;
    /// they ride the reply into the committed message.
    Parts: ToolUp.Platform.AI.AIContentPart list
    /// The task the turn runs as, once the server has accepted it.
    Task: AITask option
    /// The tool the turn is running, between its start and its completion.
    RunningTool: string option
    /// The idle watchdog's live token: `Some` while a turn is in flight.
    Watchdog: Guid option
    /// Whether tool calls are shown inline as `[debug]` notes.
    Debug: bool
}

/// What a surface does beyond writing the state back.
type StreamEffect =
    /// Append the finished reply.
    | Commit of ConversationMessage
    /// Append a note (a debug line, the watchdog's dropped-connection note).
    | Note of ConversationMessage
    /// Show the reader a failure, in the surface's own way.
    | Fault of message: string
    /// The conversation list changed (a turn finished).
    | RefreshList
    /// Schedule the watchdog's firing for this token (`watchdogDelay`).
    | Arm of token: Guid
    /// Ask the server where the task got to before reporting a silent stream.
    | Poll of token: Guid * taskId: Guid
    /// Re-read the conversation: the task finished while the stream was silent.
    | Reload of conversationId: Guid

/// An event the tab handles once, outside every surface's model: a suspended
/// server-side call waiting on the browser belongs to the tab, not to whichever
/// surface happens to be mounted.
type OutOfBand =
    | ClientTool of
        taskId: Guid *
        toolCallId: Guid *
        toolName: string *
        argsJson: string *
        activeModule: string option *
        activePage: string option
    | Consent of
        taskId: Guid *
        consentId: Guid *
        conversationId: Guid *
        toolName: string *
        targetModule: string *
        intendedQueryKey: string *
        redactedPayloadPreview: string
    | Approval of
        taskId: Guid *
        approvalId: Guid *
        conversationId: Guid *
        toolName: string *
        sourceModule: string *
        summary: string *
        detail: string *
        redactedArgumentsPreview: string

[<RequireQualifiedAccess>]
type Routed =
    | OutOfBand of OutOfBand
    | ToReducer of AIStreamEvent

/// How long a live turn may be silent before the watchdog fires.
[<Literal>]
let WatchdogTimeoutMs = 60_000

/// The watchdog's timer: completes with `token` after the timeout. A surface
/// maps an `Arm token` effect to `Cmd.OfAsync.perform watchdogDelay token`.
let watchdogDelay (token: Guid) : Async<Guid> = async {
    do! Async.Sleep WatchdogTimeoutMs
    return token
}

/// The state of a surface with no turn in flight.
let idle (conversation: Guid option) (debug: bool) : ChatStreamState = {
    Conversation = conversation
    Streaming = ""
    Parts = []
    Task = None
    RunningTool = None
    Watchdog = None
    Debug = debug
}

/// What a running turn is doing while it has no text to show: the watchdog
/// token is the in-flight flag, so the line is up from sending until the
/// first text arrives or the turn ends.
let turnStatus (state: ChatStreamState) : string option =
    match state.Watchdog, state.Streaming, state.RunningTool with
    | Some _, "", Some tool -> Some $"Calling {tool}…"
    | Some _, "", None -> Some "Working…"
    | _ -> None

// ─── Debug display ───────────────────────────────────────────────

/// `Debug on` / `Debug off`, recognised case-insensitively and trimmed, are
/// intercepted on the client and never sent to the provider.
let debugCommand (content: string) : bool option =
    match content.Trim().ToLowerInvariant() with
    | "debug on" -> Some true
    | "debug off" -> Some false
    | _ -> None

/// The acknowledgement a debug command is answered with.
let debugAck (conversationId: Guid) (enabled: bool) : ConversationMessage =
    ConversationMessage.note
        AIAssistant
        conversationId
        (if enabled then
             "[debug] Debug mode enabled. Tool invocations, results, and agent-loop failures will appear inline in this chat. Type `Debug off` to disable."
         else
             "[debug] Debug mode disabled.")

let private debugNote (conversationId: Guid) (body: string) =
    Note(ConversationMessage.note AIAssistant conversationId $"[debug] {body}")

let private truncateForDebug (limit: int) (s: string) =
    if isNull s then ""
    elif s.Length <= limit then s
    else s.Substring(0, limit) + "… (truncated)"

// ─── The turn's lifecycle ────────────────────────────────────────

/// A turn was sent to the server: the reply starts empty and the watchdog
/// is armed with `token`.
let start (token: Guid) (state: ChatStreamState) : ChatStreamState * StreamEffect list =
    {
        state with
            Streaming = ""
            Parts = []
            RunningTool = None
            Watchdog = Some token
    },
    [ Arm token ]

/// The turn ended on the client (the fast path answered it): nothing is in
/// flight.
let settle (state: ChatStreamState) : ChatStreamState = {
    state with
        Streaming = ""
        Parts = []
        RunningTool = None
        Watchdog = None
}

/// The turn is over, however it ended.
let private finish (state: ChatStreamState) : ChatStreamState = { settle state with Task = None }

let private droppedNote (conversationId: Guid) =
    Note(
        ConversationMessage.note
            AIAssistant
            conversationId
            ($"⚠️ No response after {WatchdogTimeoutMs / 1000} seconds — the connection to the AI service may have dropped. "
             + "Try again, or check the server logs. (If a more specific error appeared above, that's the real cause.)")
    )

/// A live turn's event restarts the idle clock: a fresh token replaces the
/// live one, and the firing the previous token had pending finds it stale.
let private rearm (state: ChatStreamState, effects: StreamEffect list) =
    match state.Watchdog with
    | Some _ ->
        let token = Guid.NewGuid()
        { state with Watchdog = Some token }, effects @ [ Arm token ]
    | None -> state, effects

let private reply (state: ChatStreamState) (conversationId: Guid) (content: string) = {
    ConversationMessage.note AIAssistant conversationId content with
        Parts = state.Parts
}

/// Fold one stream event. Every case of the grammar has its arm; the three
/// out-of-band cases never reach a surface (`route`) and change nothing here.
let reduce (state: ChatStreamState) (event: AIStreamEvent) : ChatStreamState * StreamEffect list =
    let ours (conversationId: Guid) =
        state.Conversation = Some conversationId

    match event with
    | MessageDelta(c, text) when ours c ->
        rearm (
            {
                state with
                    Streaming = state.Streaming + text
            },
            []
        )
    | MessageComplete(c, _) when ours c -> finish state, [ Commit(reply state c state.Streaming) ]
    | StreamCancelled c when ours c ->
        let committed =
            if state.Streaming <> "" then
                [ Commit(reply state c (state.Streaming + "\n\n*[Cancelled by user]*")) ]
            else
                []

        finish state, committed
    | ToolCallStarted(c, toolName, toolCallId) when ours c ->
        rearm (
            {
                state with
                    RunningTool = Some toolName
            },
            if state.Debug then
                [ debugNote c $"🔧 Calling tool `{toolName}` (id {toolCallId})" ]
            else
                []
        )
    | ToolCallCompleted(c, _, content) when ours c ->
        rearm (
            { state with RunningTool = None },
            if state.Debug then
                [ debugNote c $"↳ Tool result: {truncateForDebug 500 content}" ]
            else
                []
        )
    | AnswerVerified(c, _) when ours c -> rearm (state, [])
    | BlockAttached(c, part) when ours c ->
        rearm (
            {
                state with
                    Parts = state.Parts @ [ part ]
            },
            []
        )
    | TaskStatusChanged(taskId, status) ->
        let live = state.Task |> Option.forall (fun t -> t.TaskId = taskId)

        let task =
            state.Task
            |> Option.map (fun t ->
                if t.TaskId <> taskId then
                    t
                else
                    {
                        t with
                            Status = status
                            CompletedAt =
                                match status with
                                | AITaskCompleted
                                | AITaskFailed _ -> Some DateTime.UtcNow
                                | Queued
                                | InProgress -> t.CompletedAt
                    })

        match status with
        | Queued
        | InProgress when live -> rearm ({ state with Task = task }, [])
        | AITaskCompleted when live -> { state with Task = task }, [ RefreshList ]
        | AITaskFailed reason when live -> finish state, [ Fault $"The agent failed: {reason}" ]
        | Queued
        | InProgress
        | AITaskCompleted
        | AITaskFailed _ -> state, []
    | StreamError reason -> state, [ Fault $"Stream error: {reason}" ]
    | MessageDelta _
    | MessageComplete _
    | StreamCancelled _
    | ToolCallStarted _
    | ToolCallCompleted _
    | AnswerVerified _
    | BlockAttached _ -> state, []
    | ClientToolInvoke _
    | AIConsentRequired _
    | ToolApprovalRequired _ -> state, []

/// The watchdog fired for `token`. A stale token (a later event replaced it,
/// or the turn ended) changes nothing. With the task on record the server is
/// asked first, so a client that merely missed the terminal event recovers
/// the reply; without one the turn is reported as dropped.
let fire (token: Guid) (state: ChatStreamState) : ChatStreamState * StreamEffect list =
    match state.Watchdog, state.Conversation, state.Task with
    | Some live, Some _, Some task when live = token -> state, [ Poll(token, task.TaskId) ]
    | Some live, Some c, None when live = token -> settle state, [ droppedNote c ]
    | _ -> state, []

/// The server's answer to the poll a firing made (`None`: it no longer knows
/// the task, or the poll failed).
let polled (token: Guid) (status: AITask option) (state: ChatStreamState) : ChatStreamState * StreamEffect list =
    match state.Watchdog, state.Conversation with
    | Some live, Some c when live = token ->
        match status with
        | Some task ->
            match task.Status with
            | AITaskCompleted -> finish state, [ Reload c; RefreshList ]
            | AITaskFailed reason -> finish state, [ Fault $"The agent failed: {reason}" ]
            | Queued
            | InProgress -> { state with Task = Some task }, [ Arm token ]
        | None -> settle state, [ droppedNote c ]
    | _ -> state, []

/// Which events a surface reduces, and which the tab handles once.
let route (event: AIStreamEvent) : Routed =
    match event with
    | ClientToolInvoke(taskId, toolCallId, toolName, argsJson, activeModule, activePage) ->
        Routed.OutOfBand(ClientTool(taskId, toolCallId, toolName, argsJson, activeModule, activePage))
    | AIConsentRequired(taskId, consentId, conversationId, toolName, targetModule, queryKey, preview) ->
        Routed.OutOfBand(Consent(taskId, consentId, conversationId, toolName, targetModule, queryKey, preview))
    | ToolApprovalRequired(taskId, approvalId, conversationId, toolName, sourceModule, summary, detail, preview) ->
        Routed.OutOfBand(Approval(taskId, approvalId, conversationId, toolName, sourceModule, summary, detail, preview))
    | MessageDelta _
    | ToolCallStarted _
    | ToolCallCompleted _
    | TaskStatusChanged _
    | MessageComplete _
    | StreamError _
    | StreamCancelled _
    | AnswerVerified _
    | BlockAttached _ -> Routed.ToReducer event

/// The tab's one subscription to the AI stream, shared by every mounted
/// surface.
module AIStream =
    // Sanctioned per-tab singleton (the `NotificationClient` precedent): the
    // surfaces subscribed, and the one connection they share.
    let private subscribers = ResizeArray<int * (AIStreamEvent -> unit)>()
    let mutable private nextSubscriber = 0
    let mutable private connection: (unit -> unit) option = None

    let private handle (outOfBand: OutOfBand) =
        match outOfBand with
        | ClientTool(taskId, toolCallId, toolName, argsJson, activeModule, activePage) ->
            ClientToolRuntime.handleInvoke taskId toolCallId toolName argsJson activeModule activePage
            |> Async.StartImmediate
        | Consent(taskId, consentId, conversationId, toolName, targetModule, queryKey, preview) ->
            ConsentDialog.handleRequired taskId consentId conversationId toolName targetModule queryKey preview
        | Approval(taskId, approvalId, conversationId, toolName, sourceModule, summary, detail, preview) ->
            ToolApprovalDialog.handleRequired
                taskId
                approvalId
                conversationId
                toolName
                sourceModule
                summary
                detail
                preview

    let private deliver (event: AIStreamEvent) =
        match route event with
        | Routed.OutOfBand outOfBand -> handle outOfBand
        | Routed.ToReducer event ->
            for _, onEvent in List.ofSeq subscribers do
                onEvent event

    /// Receive every event a surface reduces. The first subscriber opens the
    /// tab's connection (re-keyed on an identity change), the last one to
    /// dispose closes it.
    let subscribe (onEvent: AIStreamEvent -> unit) : IDisposable =
        let id = nextSubscriber
        nextSubscriber <- nextSubscriber + 1
        subscribers.Add((id, onEvent))

        if connection.IsNone then
            connection <- Some(SSEClient.subscribe deliver)

        { new IDisposable with
            member _.Dispose() =
                let index = subscribers.FindIndex(fun (k, _) -> k = id)

                if index >= 0 then
                    subscribers.RemoveAt index

                    if subscribers.Count = 0 then
                        connection |> Option.iter (fun close -> close ())
                        connection <- None
        }

    /// Ask the server to cancel the in-flight task. The server confirms with a
    /// `StreamCancelled` event; a cancel that finds the task already done is
    /// silent, since the reader's intent is already met.
    let cancel (taskId: Guid) : unit =
        ClientToolRuntime.post $"/api/ai/cancel/{taskId}" None |> Async.StartImmediate