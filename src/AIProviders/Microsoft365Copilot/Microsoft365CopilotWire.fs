// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// The pure half of the Microsoft 365 Copilot provider: how a stateless
/// `IAIProvider` turn becomes a Work IQ Chat API request, how a
/// `copilotConversation` response becomes an `AIProviderResponse`, and how
/// the `chatOverStream` event stream is framed. No I/O here, so every rule
/// is testable offline.
module Microsoft365CopilotWire

open System
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform.AI
open ToolUp.AI.Wire

// ─── Request shaping ─────────────────────────────────────────────────
//
// The Chat API is stateful on the server (a conversation id carries the
// history), but `IAIProvider` is stateless: every call hands over the whole
// transcript. The provider therefore opens a fresh conversation per call
// and sends:
//
//   * `message.text`       — the LAST user message (the turn's question);
//   * `additionalContext`  — the system prompt, then the earlier
//                            transcript, each as one grounding text item;
//   * `locationHint`       — the configured IANA time zone (required);
//   * `contextualResources`— OneDrive / SharePoint file URIs and the web
//                            grounding toggle, when configured.
//
// Holding no conversation id between calls is what keeps the provider
// stateless (portability rule 4) and lets any instance serve any turn.

/// What one call asks Copilot: the question, plus the grounding text items
/// that carry the instructions and the earlier conversation.
type CopilotTurn = {
    Prompt: string
    AdditionalContext: string list
}

/// Why a turn cannot be put to Copilot at all. Both are refusals of the
/// input, never transport failures, so they are not retried.
type TurnRefusal =
    /// No message has role "user", so there is no question to ask.
    | NoUserMessage
    /// A message carries an image part; the Chat API takes text only.
    | ImageInput

let private renderToolCalls (calls: AIProviderToolCall list) =
    calls
    |> List.map (fun c -> sprintf "[tool call %s: %s %s]" c.Id c.Name c.Arguments)

let private renderToolResults (results: AIProviderToolResult list) =
    results
    |> List.map (fun r -> sprintf "[tool result %s: %s]" r.ToolCallId r.Content)

/// One transcript line per message: "Role: text", with any tool calls or
/// results the message carried written out after it, so a turn that went
/// through a tool loop on another provider still reads coherently here.
let private transcriptLine (message: AIProviderMessage) : string =
    let role =
        match message.Role with
        | "assistant" -> "Assistant"
        | "system" -> "System"
        | _ -> "User"

    let body =
        [
            if not (String.IsNullOrWhiteSpace message.Content) then
                message.Content
            yield! renderToolCalls message.ToolCalls
            yield! renderToolResults message.ToolResults
        ]
        |> String.concat "\n"

    sprintf "%s: %s" role body

/// Split a stateless transcript into the question and its grounding.
/// The question is the last user message; everything before it becomes
/// one "conversation so far" context item, after the system prompt.
let buildTurn (systemPrompt: string option) (messages: AIProviderMessage list) : Result<CopilotTurn, TurnRefusal> =
    if messages |> List.exists AIProviderMessage.isMultimodal then
        Error ImageInput
    else
        match messages |> List.tryFindIndexBack (fun m -> m.Role = "user") with
        | None -> Error NoUserMessage
        | Some index ->
            let question = messages[index]
            let earlier = messages |> List.take index

            let instructions =
                systemPrompt
                |> Option.filter (String.IsNullOrWhiteSpace >> not)
                |> Option.map (sprintf "Instructions for this conversation:\n%s")

            let history =
                if earlier.IsEmpty then
                    None
                else
                    earlier
                    |> List.map transcriptLine
                    |> String.concat "\n\n"
                    |> sprintf "The conversation so far:\n\n%s"
                    |> Some

            // A question that arrived only as tool results (the agent loop's
            // continuation shape) still needs text; render its results.
            let prompt =
                if String.IsNullOrWhiteSpace question.Content then
                    renderToolResults question.ToolResults |> String.concat "\n"
                else
                    question.Content

            if String.IsNullOrWhiteSpace prompt then
                Error NoUserMessage
            else
                Ok {
                    Prompt = prompt
                    AdditionalContext = List.choose id [ instructions; history ]
                }

/// The JSON body for `chat` / `chatOverStream`.
let chatBody (timeZone: string) (webSearch: bool option) (files: string list) (turn: CopilotTurn) : string =
    let body = JsonObject()
    body["message"] <- JsonObject(dict [ "text", JsonValue.Create(turn.Prompt) :> JsonNode ])

    if not turn.AdditionalContext.IsEmpty then
        let items = JsonArray()

        for text in turn.AdditionalContext do
            items.Add(JsonObject(dict [ "text", JsonValue.Create(text) :> JsonNode ]))

        body["additionalContext"] <- items

    body["locationHint"] <- JsonObject(dict [ "timeZone", JsonValue.Create(timeZone) :> JsonNode ])

    if not files.IsEmpty || webSearch.IsSome then
        let resources = JsonObject()

        if not files.IsEmpty then
            let fileArray = JsonArray()

            for uri in files do
                fileArray.Add(JsonObject(dict [ "uri", JsonValue.Create(uri) :> JsonNode ]))

            resources["files"] <- fileArray

        match webSearch with
        | Some enabled ->
            resources["webContext"] <- JsonObject(dict [ "isWebEnabled", JsonValue.Create(enabled) :> JsonNode ])
        | None -> ()

        body["contextualResources"] <- resources

    body.ToJsonString()

// ─── Response reading ────────────────────────────────────────────────

/// The conversation id from a `POST /conversations` response.
let tryConversationId (json: string) : string option =
    try
        use doc = JsonDocument.Parse json

        match doc.RootElement.TryGetProperty "id" with
        | true, id when id.ValueKind = JsonValueKind.String ->
            id.GetString()
            |> Option.ofObj
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
        | _ -> None
    with :? JsonException ->
        None

/// Copilot's reply text from a `copilotConversation`. The response echoes
/// the user's message as well as answering it, so the reply is the LAST
/// message whose text is not the prompt. `None` when the document is not a
/// conversation; `Some ""` when it is one that holds no reply yet (an early
/// stream event).
let tryReplyText (prompt: string) (json: string) : string option =
    try
        use doc = JsonDocument.Parse json

        match doc.RootElement.TryGetProperty "messages" with
        | true, messages when messages.ValueKind = JsonValueKind.Array ->
            messages.EnumerateArray()
            |> Seq.choose (fun m ->
                match m.TryGetProperty "text" with
                | true, t when t.ValueKind = JsonValueKind.String -> Option.ofObj (t.GetString())
                | _ -> None)
            |> Seq.filter (fun text -> text <> prompt)
            |> Seq.tryLast
            |> Option.defaultValue ""
            |> Some
        | _ -> None
    with :? JsonException ->
        None

/// The completed provider response for a reply. The Chat API reports no
/// token counts, so `Usage` is `None`, and it never asks for a tool.
let toResponse (reply: string) : AIProviderResponse = {
    Content = reply
    ToolCalls = []
    StopReason = StopReason.EndTurn
    Usage = None
}

// ─── Streaming ───────────────────────────────────────────────────────
//
// `chatOverStream` answers with `text/event-stream`; every event's data is
// a whole `copilotConversation` SNAPSHOT, so the reply text grows across
// events rather than arriving as deltas. The reader below frames events by
// the SSE rules (a blank line ends an event; `data:` lines join with "\n";
// comments and `event:` / `id:` / `retry:` fields are skipped). It also
// accepts a data payload whose JSON continues on unprefixed lines, which
// is how Microsoft's documentation prints the stream.

/// The data lines gathered for the event being read.
type SseFrame = { Pending: string list }

module SseFrame =
    let empty = { Pending = [] }

    let private flush (frame: SseFrame) : SseFrame * string option =
        match frame.Pending with
        | [] -> empty, None
        | lines -> empty, Some(lines |> List.rev |> String.concat "\n")

    /// Feed one line; returns the next frame and, when the line ended an
    /// event, that event's data.
    let feed (frame: SseFrame) (line: string) : SseFrame * string option =
        if line.Length = 0 then
            flush frame
        elif line.StartsWith ':' then
            frame, None
        elif line.StartsWith "data:" then
            let value = line.Substring 5
            let value = if value.StartsWith ' ' then value.Substring 1 else value
            { Pending = value :: frame.Pending }, None
        elif line.StartsWith "event:" || line.StartsWith "id:" || line.StartsWith "retry:" then
            frame, None
        elif frame.Pending.IsEmpty then
            frame, None
        else
            { Pending = line :: frame.Pending }, None

    /// End of stream: the data of an event the stream did not terminate.
    let finish (frame: SseFrame) : string option = snd (flush frame)

/// The text to hand the stream callback when the reply grows from
/// `previous` to `current`. A snapshot that rewrites earlier text rather
/// than extending it yields nothing: what was already streamed cannot be
/// taken back, and the final response carries the rewritten text.
let streamDelta (previous: string) (current: string) : string option =
    if
        current.Length > previous.Length
        && current.StartsWith(previous, StringComparison.Ordinal)
    then
        Some(current.Substring previous.Length)
    else
        None

/// The `chatOverStream` assembly as a `StreamProcessor`. Each completed SSE
/// event carries the conversation so far; the reply is its last message that
/// is not the prompt, and what is forwarded is how far that reply grew.
type Microsoft365CopilotStream = {
    /// The SSE event being framed.
    Frame: SseFrame
    /// The reply text forwarded so far.
    Streamed: string
    /// The latest reply seen.
    Latest: string
}

/// The `chatOverStream` fold.
module Microsoft365CopilotStream =
    /// The state before the first line.
    let initial = {
        Frame = SseFrame.empty
        Streamed = ""
        Latest = ""
    }

    let private accept (prompt: string) (state: Microsoft365CopilotStream) (data: string option) =
        match data |> Option.bind (tryReplyText prompt) with
        | Some reply when reply <> "" ->
            match streamDelta state.Streamed reply with
            | Some delta ->
                {
                    state with
                        Latest = reply
                        Streamed = reply
                },
                Some delta
            | None -> { state with Latest = reply }, None
        | _ -> state, None

    /// The processor for a call whose question is `prompt`.
    let processor (prompt: string) : StreamProcessor<Microsoft365CopilotStream> = {
        Initial = initial
        Step =
            fun state line ->
                let frame, completed = SseFrame.feed state.Frame line
                accept prompt { state with Frame = frame } completed
        Finish =
            fun state ->
                let state, trailing = accept prompt state (SseFrame.finish state.Frame)
                trailing, toResponse state.Latest
    }