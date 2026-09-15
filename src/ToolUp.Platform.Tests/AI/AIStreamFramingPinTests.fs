// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AI.AIStreamFramingPinTests

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.AI
open ToolUp.Remoting.Server
open ToolUp.Remoting.Json.SystemTextJson

// ─── Phase 69c.F — the legacy AI channel is byte-pinned across the migration ──
//
// Phase 69c.F makes the chat turn ONE implementation with two sinks: the
// typed `AIStreamingApi.StreamChatV2` (per-call SSE, `event: chunk`
// frames) and the legacy `SubmitMessage` + `/api/ai/events` pair (the
// per-user push channel toolup-app's AI side panel reads with
// `EventSource.onmessage`). The acceptance is that the legacy channel
// carries EXACTLY the bytes it carried before — a consumer that never
// adopts the typed endpoint must see no change.
//
// These tests pin that claim at the wire, in three parts:
//
//   1. the legacy frames for a representative turn are a LITERAL golden
//      string, recorded from the pre-69c.F code before the handler was
//      touched (a refactor that changes one byte of the legacy framing
//      fails here);
//   2. the typed channel's `data:` payloads for the same events are the
//      legacy `data:` payloads byte-for-byte — both sides serialise with
//      `FableConverters`, so a client can move to the typed endpoint and
//      parse each chunk with the parser it already has;
//   3. one producer driven through the `AsyncStream.fromCallback` bridge
//      (the typed shape) and through the legacy sink (`SSEHandler.sendEvent`)
//      delivers the same event sequence to both — which is the
//      structural fact 69c.F relies on when `SubmitMessage` and
//      `StreamChatV2` share the turn and differ only in the sink.

/// Records every frame the manager writes to one connection.
type private CapturingSink() =
    let buffer = new MemoryStream()
    member _.Text = Encoding.UTF8.GetString(buffer.ToArray())

    interface IConnectionSink with
        member _.DisconnectToken = CancellationToken.None

        member _.WriteFrame(bytes, _) = async {
            buffer.Write(bytes, 0, bytes.Length)
            return ()
        }

let private conversationId = Guid "0a3d3e4c-1a2b-4c5d-8e9f-000000000001"
let private taskId = Guid "0a3d3e4c-1a2b-4c5d-8e9f-000000000002"
let private toolCallId = Guid "0a3d3e4c-1a2b-4c5d-8e9f-000000000003"
let private messageId = Guid "0a3d3e4c-1a2b-4c5d-8e9f-000000000004"

/// A representative chat turn: status flip, deltas (one carrying a
/// literal newline and a quote — the two characters SSE framing and JSON
/// escaping both care about), a tool round-trip, completion, terminal.
let private turn: AIStreamEvent list = [
    TaskStatusChanged(taskId, InProgress)
    MessageDelta(conversationId, "Hel")
    MessageDelta(conversationId, "lo\n\"world\"")
    ToolCallStarted(conversationId, "search", toolCallId)
    ToolCallCompleted(conversationId, toolCallId, "{\"n\":1}")
    MessageComplete(conversationId, messageId)
    TaskStatusChanged(taskId, AITaskCompleted)
]

/// A refused turn — the ownership-refusal path emits a single terminal.
let private refusedTurn: AIStreamEvent list = [
    TaskStatusChanged(taskId, AITaskFailed "Conversation does not belong to the current user — refusing to append.")
]

/// The typed stream's terminal predicate (mirrors
/// `AIAssistantHandler.isTerminalStreamEvent`).
let private isTerminal =
    function
    | TaskStatusChanged(_, AITaskCompleted)
    | TaskStatusChanged(_, AITaskFailed _) -> true
    | _ -> false

/// Drive `events` through the legacy sink exactly as the handler does —
/// `SSEHandler.sendEvent` over a real `SSEConnectionManager` with one
/// registered connection — and return the bytes that connection saw.
let private legacyWire (scopeId: string) (events: AIStreamEvent list) : string =
    use manager = new SSEConnectionManager()
    let sink = CapturingSink()

    match manager.Add(scopeId, sink) with
    | Ok() -> ()
    | Error _ -> failtest "capture connection refused"

    for e in events do
        SSEHandler.sendEvent manager scopeId e

    sink.Text

/// Enumerate an `IAsyncEnumerable` to a list (the dispatcher's loop, minus
/// the framing).
let private collect (source: IAsyncEnumerable<'T>) : 'T list =
    task {
        let enumerator = source.GetAsyncEnumerator CancellationToken.None
        let acc = ResizeArray<'T>()
        let mutable go = true

        while go do
            let! moved = enumerator.MoveNextAsync()

            if moved then acc.Add enumerator.Current else go <- false

        do! enumerator.DisposeAsync()
        return List.ofSeq acc
    }
    |> fun t -> t.Result

let private fableJson = FableConverters.create ()

/// The typed channel's payload for one event: what the dispatcher writes
/// inside the `event: chunk` frame (`JsonSerializer.Serialize` with the
/// `Remoting.createApi ()` default options — the same `FableConverters`).
let private typedPayload (e: AIStreamEvent) : string = JsonSerializer.Serialize(e, fableJson)

// ─── The golden bytes ───────────────────────────────────────────────────
//
// Recorded from `SSEHandler.sendEvent` at forge ff8e323c (2026-09-15),
// BEFORE Phase 69c.F touched `AIAssistantHandler`. Do not regenerate this
// from the code under test: the value IS the contract the legacy client
// reads, and a change here is a wire break for every `/api/ai/events`
// consumer.

let private legacyGolden =
    "data: {\"TaskStatusChanged\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000002\",\"InProgress\"]}\n\n"
    + "data: {\"MessageDelta\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000001\",\"Hel\"]}\n\n"
    + "data: {\"MessageDelta\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000001\",\"lo\\n\\\"world\\\"\"]}\n\n"
    + "data: {\"ToolCallStarted\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000001\",\"search\",\"0a3d3e4c-1a2b-4c5d-8e9f-000000000003\"]}\n\n"
    + "data: {\"ToolCallCompleted\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000001\",\"0a3d3e4c-1a2b-4c5d-8e9f-000000000003\",\"{\\\"n\\\":1}\"]}\n\n"
    + "data: {\"MessageComplete\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000001\",\"0a3d3e4c-1a2b-4c5d-8e9f-000000000004\"]}\n\n"
    + "data: {\"TaskStatusChanged\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000002\",\"AITaskCompleted\"]}\n\n"

let private refusedGolden =
    "data: {\"TaskStatusChanged\":[\"0a3d3e4c-1a2b-4c5d-8e9f-000000000002\",{\"AITaskFailed\":\"Conversation does not belong to the current user — refusing to append.\"}]}\n\n"

[<Tests>]
let tests =
    testList "Phase 69c.F — legacy AI SSE channel byte pin" [

        test "the legacy /api/ai/events frames for a chat turn are the recorded bytes" {
            let actual = legacyWire "user-1" turn

            if actual <> legacyGolden then
                // Render both so a diff is readable in the failure, then fail.
                failtestf "legacy AI SSE framing changed.\n--- expected ---\n%s\n--- actual ---\n%s" legacyGolden actual
        }

        test "the legacy refusal terminal is the recorded bytes" {
            Expect.equal (legacyWire "user-1" refusedTurn) refusedGolden "ownership-refusal terminal frame"
        }

        test "legacy frames are default `message` events — the client reads them with onmessage" {
            let frames = SseFrame.parse (legacyWire "user-1" turn)
            Expect.equal frames.Length turn.Length "one frame per event"

            Expect.all
                frames
                (fun f -> f.Event = "message" && f.Id.IsNone)
                "no event: and no id: line on the legacy channel"
        }

        test "the typed channel's chunk payloads are the legacy data payloads byte-for-byte" {
            let legacyPayloads = SseFrame.parse (legacyWire "user-1" turn) |> List.map _.Data

            let typedPayloads =
                turn
                |> List.map (typedPayload >> Streaming.formatChunk >> Encoding.UTF8.GetString)
                |> String.concat ""
                |> SseFrame.parse
                |> List.map _.Data

            Expect.equal typedPayloads legacyPayloads "same serializer on both channels ⇒ same payload bytes per event"
        }

        test "one producer, two sinks: the typed bridge and the legacy sink see the same turn" {
            // The typed shape: the producer emits through the bridge's sink.
            let typed =
                AsyncStream.fromCallback isTerminal (fun emit -> async {
                    for e in turn do
                        emit e
                })
                |> collect

            Expect.equal typed turn "the typed stream yields every event, ending on the terminal"

            // The legacy shape: the SAME producer emits through the legacy sink.
            use manager = new SSEConnectionManager()
            let sink = CapturingSink()
            manager.Add("user-1", sink) |> ignore
            let legacySink = SSEHandler.sendEvent manager "user-1"

            for e in turn do
                legacySink e

            Expect.equal sink.Text legacyGolden "the legacy sink over the same producer writes the pinned bytes"
        }
    ]