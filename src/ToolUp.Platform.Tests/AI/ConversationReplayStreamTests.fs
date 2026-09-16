module ToolUp.Platform.Tests.AI.ConversationReplayStreamTests

open System
open System.Collections.Generic
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

// ─── Phase 69c.tail B — conversation replay as a typed stream ────
//
// `ConversationReplay.replay` returns once EVERY user turn has been
// re-run through the provider, so a replay of an N-turn conversation is
// N provider calls of silence followed by one result.
// `ConversationReplay.replayStream` is the same run over the Phase 69c
// typed-streaming shape, and these cases pin the two properties that
// make it usable rather than merely present:
//
//   * the stream ENDS. Every exit path of the one shared implementation
//     emits a terminal event, and `AsyncStream.fromCallback` closes on
//     it — including the paths that fail BEFORE the replay conversation
//     exists (an unreadable original, an unresolvable provider), which
//     are exactly the paths a producer forgets and which leave a
//     consumer hanging on an open connection rather than showing an
//     error;
//   * `replay` and `replayStream` are ONE implementation. The result
//     `replay` returns is asserted to agree with what the stream
//     reported, so the sink cannot drift from the run it describes.

// ─── Fakes ───────────────────────────────────────────────────────

let private scopeId = "team-replay"
let private operator = "op-1"

let private accessContext =
    AccessContext.unrestricted (TeamMember(operator, scopeId))

let private response (text: string) : AIProviderResponse = {
    Content = text
    ToolCalls = []
    StopReason = "end_turn"
    Usage =
        Some {
            PromptTokens = 11
            CachedPromptTokens = 0
            OutputTokens = 7
            CacheCreationTokens = None
        }
}

/// A provider whose every call succeeds, unless `failFrom` says this
/// call index (zero-based) and every later one fails.
let private provider (failFrom: int option) =
    let mutable calls = 0

    { new IAIProvider with
        member _.Capabilities = AIProviderCapabilities.unknown

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            let index = calls
            calls <- calls + 1

            match failFrom with
            | Some n when index >= n -> return Error(TransientNetwork "the vendor went away")
            | _ -> return Ok(response (sprintf "reply-%d" index))
        }

        member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = async {
            return Ok(response "structured")
        }
    }

let private factoryOver (p: IAIProvider) =
    { new IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ctx = async { return Ok p }
        member _.TryResolveByLabel(_ctx, _label) = async { return Ok p }
        member _.BuildPlatform(_providerId, _apiKey, _model) = Some p
    }

/// A factory that resolves nothing — the "no provider configured"
/// failure, which lands BEFORE the replay conversation is created.
let private emptyFactory =
    { new IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ctx = async { return Error NoProviderConfigured }
        member _.TryResolveByLabel(_ctx, _label) = async { return Error NoProviderConfigured }
        member _.BuildPlatform(_providerId, _apiKey, _model) = None
    }

// ─── The original conversation ───────────────────────────────────

let private content (role: string) (text: string) : AIMessageContent = {
    Role = role
    Content = text
    ToolCalls = []
    ToolResults = []
    Parts = []
}

let private turn (conversationId: string) (role: string) (text: string) : ConversationTurn = {
    TurnId = ""
    ConversationId = conversationId
    SchemaVersion = 1
    Role = role
    Content = content role text
    Timestamp = DateTime.UtcNow
    TokensIn = Some 5
    TokensOut = Some 5
    ContentDigest = "digest"
}

/// An in-memory store holding one conversation with `userTurns` user
/// turns, each already answered.
let private seededStore (userTurns: int) =
    let store = ConversationStore.InMemoryConversationStore() :> IConversationStore
    let writer = store :> IConversationWriter
    let conversationId = Guid.NewGuid().ToString("N")

    let header: ToolUp.Platform.Conversation = {
        ConversationId = conversationId
        SchemaVersion = 1
        CreatedAt = DateTime.UtcNow
        CreatedBy = operator
        ScopeId = scopeId
        Provider = "original-provider"
        ModelName = "original-model"
        SystemPromptDigest = "original-digest"
        SdkVersion = "0.0.0"
    }

    writer.BeginConversation(scopeId, header)
    |> Async.RunSynchronously
    |> function
        | Ok() -> ()
        | Error e -> failwithf "seed failed: %A" e

    for i in 0 .. userTurns - 1 do
        for role in [ "user"; "assistant" ] do
            writer.AppendTurn(scopeId, conversationId, turn conversationId role (sprintf "%s-%d" role i))
            |> Async.RunSynchronously
            |> function
                | Ok() -> ()
                | Error e -> failwithf "seed turn failed: %A" e

    store, conversationId

// ─── Draining the stream ─────────────────────────────────────────

/// Enumerate an `IAsyncEnumerable` to its end, refusing to hang: a
/// stream that never terminates fails the case by timeout rather than
/// stalling the pack.
let private drain (source: IAsyncEnumerable<'T>) : 'T list =
    let work = task {
        let acc = ResizeArray<'T>()
        let enumerator = source.GetAsyncEnumerator CancellationToken.None

        try
            let mutable moved = true

            while moved do
                let! next = enumerator.MoveNextAsync()
                moved <- next

                if moved then
                    acc.Add enumerator.Current
        finally
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult()

        return List.ofSeq acc
    }

    if not (work.Wait(TimeSpan.FromSeconds 30.0)) then
        failwith "the replay stream did not terminate within 30s — a missing terminal event hangs the consumer"

    work.Result

// ─── Cases ───────────────────────────────────────────────────────

[<Tests>]
let tests =
    testList "Phase 69c.tail B — conversation replay streaming" [

        test "the happy path reports start, one event per replayed turn, then completes" {
            let store, originalId = seededStore 3

            let events =
                ConversationReplay.replayStream
                    store
                    (factoryOver (provider None))
                    None
                    accessContext
                    scopeId
                    originalId
                    ConversationReplayOptions.empty
                |> drain

            match events with
            | ReplayStarted(replayId, original, _provider, _model) :: rest ->
                Expect.equal original originalId "the started event names the conversation being replayed"

                Expect.notEqual
                    replayId
                    originalId
                    "the replay is a NEW conversation (GP 5 — the original is immutable)"

                let replayed =
                    rest
                    |> List.choose (function
                        | ReplayTurnReplayed(_, index, tokensIn, tokensOut) -> Some(index, tokensIn, tokensOut)
                        | _ -> None)

                Expect.equal
                    (replayed |> List.map (fun (i, _, _) -> i))
                    [ 0; 1; 2 ]
                    "one event per replayed user turn, zero-based and in order"

                Expect.equal
                    (replayed |> List.map (fun (_, i, o) -> i, o))
                    [ Some 11, Some 7; Some 11, Some 7; Some 11, Some 7 ]
                    "each carries the provider's reported token usage"

                match List.last rest with
                | ReplayCompleted(id, turns, delta) ->
                    Expect.equal id replayId "the terminal names the same replay conversation"
                    Expect.equal turns 3 "the terminal carries the assistant-turn count"
                    Expect.isTrue (delta.Contains "Delta") "the operator-readable delta rides the terminal"
                | other -> failwithf "expected a terminal ReplayCompleted, got %A" other
            | other -> failwithf "expected ReplayStarted first, got %A" other
        }

        test "an unreadable original terminates the stream instead of leaving it open" {
            let store, _ = seededStore 1

            let events =
                ConversationReplay.replayStream
                    store
                    (factoryOver (provider None))
                    None
                    accessContext
                    scopeId
                    "no-such-conversation"
                    ConversationReplayOptions.empty
                |> drain

            match events with
            | [ ReplayFailed(replayId, reason) ] ->
                Expect.isNone replayId "no replay conversation was created, so none is named"
                Expect.isTrue (reason.Contains "not found") "the reason is the store's own"
            | other -> failwithf "expected exactly one terminal failure, got %A" other
        }

        test "an unresolvable provider terminates the stream before any conversation is created" {
            let store, originalId = seededStore 1

            let events =
                ConversationReplay.replayStream
                    store
                    emptyFactory
                    None
                    accessContext
                    scopeId
                    originalId
                    ConversationReplayOptions.empty
                |> drain

            match events with
            | [ ReplayFailed(None, _) ] -> ()
            | other -> failwithf "expected exactly one terminal failure naming no replay, got %A" other
        }

        test "a provider that fails part-way terminates as a failure, naming the partial replay" {
            let store, originalId = seededStore 3

            let events =
                ConversationReplay.replayStream
                    store
                    (factoryOver (provider (Some 1)))
                    None
                    accessContext
                    scopeId
                    originalId
                    ConversationReplayOptions.empty
                |> drain

            let replayedCount =
                events
                |> List.filter (function
                    | ReplayTurnReplayed _ -> true
                    | _ -> false)
                |> List.length

            Expect.equal replayedCount 1 "the turn that succeeded before the failure was still reported"

            match List.last events with
            | ReplayFailed(Some _, reason) ->
                Expect.isTrue
                    (reason.Contains "Provider call failed")
                    "the terminal carries the failure detail the replay recorded"
            | other -> failwithf "expected a terminal ReplayFailed naming the partial replay, got %A" other
        }

        test "replay and replayStream are one implementation — the result agrees with the stream" {
            let store, originalId = seededStore 2

            let streamed =
                ConversationReplay.replayStream
                    store
                    (factoryOver (provider None))
                    None
                    accessContext
                    scopeId
                    originalId
                    ConversationReplayOptions.empty
                |> drain

            let result =
                ConversationReplay.replay
                    store
                    (factoryOver (provider None))
                    None
                    accessContext
                    scopeId
                    originalId
                    ConversationReplayOptions.empty
                |> Async.RunSynchronously

            match result, List.last streamed with
            | Ok r, ReplayCompleted(_, turns, delta) ->
                Expect.equal r.NewAssistantTurns.Length turns "the stream's turn count is the result's turn count"
                Expect.equal r.Delta delta "the stream's delta is the result's delta, not a second rendering"
                Expect.equal r.OriginalConversationId originalId "the result still names the original"
            | other -> failwithf "expected an Ok result beside a completed stream, got %A" other
        }
    ]