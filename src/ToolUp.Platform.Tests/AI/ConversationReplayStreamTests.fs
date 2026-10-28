module ToolUp.Platform.Tests.AI.ConversationReplayStreamTests

open System
open System.Collections.Generic
open System.Threading
open System.Text
open System.Text.Json
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.Wire.Conformance
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

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

let private content (parts: AIContentPart list) (role: string) (text: string) : AIMessageContent = {
    Role = role
    Content = text
    ToolCalls = []
    ToolResults = []
    Parts = parts
}

let private turn (parts: AIContentPart list) (conversationId: string) (role: string) (text: string) : ConversationTurn = {
    TurnId = ""
    ConversationId = conversationId
    SchemaVersion = 1
    Role = role
    Content = content parts role text
    Timestamp = DateTime.UtcNow
    TokensIn = Some 5
    TokensOut = Some 5
    ContentDigest = "digest"
}

/// An in-memory store holding one conversation with `userTurns` user
/// turns, each already answered, every user turn carrying `userParts`.
let private seededStoreWith (userParts: AIContentPart list) (userTurns: int) =
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
            let parts = if role = "user" then userParts else []

            writer.AppendTurn(scopeId, conversationId, turn parts conversationId role (sprintf "%s-%d" role i))
            |> Async.RunSynchronously
            |> function
                | Ok() -> ()
                | Error e -> failwithf "seed turn failed: %A" e

    store, conversationId

/// An in-memory store holding one conversation with `userTurns` plain-text
/// user turns, each already answered.
let private seededStore (userTurns: int) = seededStoreWith [] userTurns

// ─── Phase 1128 — the conversation blob, through the real read path ──
//
// The blob is the one the assistant handler writes
// (`ai-conversations/{id}.json`, System.Text.Json with the remoting
// converters) and `GetConversation` is the read the panel makes, so a
// message holding block parts is asserted to survive the persisted format
// and the read path the browser actually calls.

let private blobOptions = FableConverters.create ()

let private blobContainer = "replay-blocks"

let private blobMessage (conversationId: Guid) (participant: ParticipantType) (text: string) parts =
    {
        Id = Guid.Parse "6f9619ff-8b86-d011-b42d-00c04fc964ff"
        ConversationId = conversationId
        Participant = participant
        Content = text
        Timestamp = DateTime(2026, 10, 26, 9, 30, 0, DateTimeKind.Utc)
        ToolCalls = []
        RetrievedSources = []
        Parts = parts
        CreatedBy = operator
        BeaconId = ""
        Verification = None
    }
    : ConversationMessage

/// `GetConversation` over `storage`, as `operator` in a personal scope.
let private getConversation (storage: IBlobStorage) (conversationId: Guid) : ConversationMessage list =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore
    services.AddSingleton<IAIProviderFactory>(factoryOver (provider None)) |> ignore

    services.AddSingleton<AIToolRegistry.AIToolRegistry>(AIToolRegistry.AIToolRegistry())
    |> ignore

    services.AddSingleton<ClientToolDispatch.ClientToolDispatchRegistry>(
        ClientToolDispatch.ClientToolDispatchRegistry()
    )
    |> ignore

    services.AddSingleton<AICancellationRegistry.AICancellationRegistry>(
        AICancellationRegistry.AICancellationRegistry()
    )
    |> ignore

    services.AddSingleton<AccessContext>(accessContext) |> ignore
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()

    ctx.Items["ToolUp.StorageScope"] <-
        box {
            ScopeId = operator
            Container = blobContainer
            Persist = true
        }

    ctx.Items["ToolUp.UserId"] <- box operator
    let manager = new SSEConnectionManager()

    let api, _ =
        AIAssistantHandler.aiAssistantApi None Map.empty manager (ctx :> HttpContext)

    api.GetConversation conversationId |> Async.RunSynchronously

/// Store `bytes` as conversation `conversationId`'s blob.
let private uploadConversation (storage: IBlobStorage) (conversationId: Guid) (bytes: byte[]) =
    storage.Upload(blobContainer, $"ai-conversations/{conversationId}.json", bytes)
    |> Async.RunSynchronously
    |> ignore

/// A provider that records every history it is sent.
let private capturingProvider (sent: ResizeArray<AIProviderMessage list>) =
    { new IAIProvider with
        member _.Capabilities = AIProviderCapabilities.unknown

        member _.SendMessage(messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            sent.Add messages
            return Ok(response "a reply")
        }

        member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = async {
            return Ok(response "structured")
        }
    }

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

        testList "Phase 1128 — a block part persists, reloads and replays unchanged" [

            test "the conversation-blob writer writes the pinned literal the browser reads" {
                let written = JsonSerializer.Serialize(BlockPartFixtures.parts, blobOptions)

                Expect.equal
                    written
                    BlockPartFixtures.partsJson
                    "the server's bytes are the literal the Fable pack decodes"

                let read =
                    JsonSerializer.Deserialize<AIContentPart list>(BlockPartFixtures.partsJson, blobOptions)

                Expect.equal read BlockPartFixtures.parts "the literal reads back to the same parts"
            }

            test "a message holding block parts reloads through GetConversation byte-identically" {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let conversationId = Guid.NewGuid()

                let messages = [
                    blobMessage conversationId User "Show me revenue by site." []
                    blobMessage conversationId AIAssistant BlockPartFixtures.Prose BlockPartFixtures.parts
                ]

                let bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(messages, blobOptions))
                uploadConversation storage conversationId bytes

                let loaded = getConversation storage conversationId

                Expect.equal loaded messages "the reloaded conversation equals what was written, blocks included"

                Expect.equal
                    (JsonSerializer.Serialize(loaded, blobOptions))
                    (Encoding.UTF8.GetString bytes)
                    "and re-serialises to the same bytes"
            }

            test "a blob written before block parts existed reads as before, with no block" {
                let storage = InMemoryBlobStorage() :> IBlobStorage
                let conversationId = Guid.NewGuid()

                // The shape a pre-6o writer left: no `Parts` member at all
                // (nor the later additive members) — and a pre-1128 multipart
                // turn whose parts are text only.
                let legacy =
                    sprintf
                        """[{"Id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","ConversationId":"%O","Participant":"User","Content":"hello","Timestamp":"2026-10-26T09:30:00.0000000Z","ToolCalls":[],"RetrievedSources":[]},{"Id":"6f9619ff-8b86-d011-b42d-00c04fc964ff","ConversationId":"%O","Participant":"AIAssistant","Content":"hi","Timestamp":"2026-10-26T09:31:00.0000000Z","ToolCalls":[],"RetrievedSources":[],"Parts":[{"TextPart":"hi"}]}]"""
                        conversationId
                        conversationId

                uploadConversation storage conversationId (Encoding.UTF8.GetBytes legacy)

                let loaded = getConversation storage conversationId

                Expect.equal (loaded |> List.map _.Content) [ "hello"; "hi" ] "both turns read"

                Expect.equal
                    (loaded |> List.map _.Parts)
                    [ []; [ TextPart "hi" ] ]
                    "an absent Parts reads as empty and a text-only Parts as written — no block appears"
            }

            test "the replay carries a user turn's block parts unchanged" {
                let userParts = [ TextPart "Explain this table."; BlockPartFixtures.tableBlock ]
                let store, originalId = seededStoreWith userParts 1
                let sent = ResizeArray<AIProviderMessage list>()

                let events =
                    ConversationReplay.replayStream
                        store
                        (factoryOver (capturingProvider sent))
                        None
                        accessContext
                        scopeId
                        originalId
                        ConversationReplayOptions.empty
                    |> drain

                match List.last events with
                | ReplayCompleted(replayId, 1, _) ->
                    match
                        (store :> IConversationReader).GetConversation(scopeId, replayId)
                        |> Async.RunSynchronously
                    with
                    | Ok(_, turns, _) ->
                        let userTurn = turns |> List.find (fun t -> t.Role = "user")

                        Expect.equal
                            userTurn.Content.Parts
                            userParts
                            "the replayed user turn holds the original parts, block included"
                    | Error e -> failwithf "the replay conversation should be readable, got %A" e

                    match List.ofSeq sent with
                    | [ history ] ->
                        Expect.equal
                            (history |> List.map _.Parts)
                            [ userParts ]
                            "the provider is handed the turn as recorded; its wire builder sends the summary"
                    | other -> failwithf "expected exactly one provider call, got %d" other.Length
                | other -> failwithf "expected a completed one-turn replay, got %A" other
            }
        ]
    ]