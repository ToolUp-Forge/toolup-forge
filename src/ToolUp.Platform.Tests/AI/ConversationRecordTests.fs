// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.AI.ConversationRecordTests

// ─── One conversation record, one rule, one write order ─────────────
//
// A conversation is four sibling blobs — the UI messages, the provider
// history, the metadata and the consent record. Every one is read under
// the same rule: absent is empty, present-but-unreadable is an error and
// is never written over. And the two writers of an exchange — a full turn
// and a fast-path beacon — write in ONE order, history then UI, so the
// only half-written shape a reader can meet is a history one exchange
// ahead of the UI. Observed through the shipped handlers over a recording
// store.

open System
open System.Text
open System.Text.Json
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.AI
open ToolUp.AI.AIAssistantHandler
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

let private wire = FableConverters.create ()

let private scope: StorageScope = {
    ScopeId = "alice"
    Container = "user-alice"
    Persist = true
}

let private uiBlob (id: Guid) = $"ai-conversations/{id}.json"
let private historyBlob (id: Guid) = $"ai-conversations/{id}.history.json"
let private metaBlob (id: Guid) = $"ai-conversations/{id}.meta.json"
let private consentBlob (id: Guid) = $"ai-conversations/{id}.consent.json"

/// Bytes no decoder reads as the record they stand for.
let private garbage = Encoding.UTF8.GetBytes "{ not json"

/// A store that records every upload, in order.
type private RecordingStorage(inner: IBlobStorage) =
    let uploads = ResizeArray<string>()

    member _.Uploads = lock uploads (fun () -> List.ofSeq uploads)

    interface IBlobStorage with
        member _.CanComposeFrom = false
        member _.ComposeFrom(_, _, _) = composeNotSupported "test double"

        member _.Upload(container, blobName, content) =
            lock uploads (fun () -> uploads.Add blobName)
            inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

let private put (storage: IBlobStorage) (blobName: string) (bytes: byte[]) =
    match storage.Upload(scope.Container, blobName, bytes) |> Async.RunSynchronously with
    | Ok _ -> ()
    | Error e -> failtestf "seeding %s failed: %s" blobName e

let private bytesOf (storage: IBlobStorage) (blobName: string) =
    match storage.Download(scope.Container, blobName) |> Async.RunSynchronously with
    | Ok bytes -> bytes
    | Error e -> failtestf "reading %s failed: %s" blobName e

let private message (conversationId: Guid) (participant: ParticipantType) (content: string) : ConversationMessage = {
    Id = Guid.NewGuid()
    ConversationId = conversationId
    Participant = participant
    Content = content
    Timestamp = DateTime.UtcNow
    ToolCalls = []
    RetrievedSources = []
    Parts = []
    CreatedBy = "alice"
    BeaconId = ""
    Verification = None
}

/// Seed alice's conversation: one exchange in the UI blob.
let private seedConversation (storage: IBlobStorage) =
    let id = Guid.NewGuid()

    put
        storage
        (uiBlob id)
        (Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize([ message id User "hello"; message id AIAssistant "hi" ], wire)
        ))

    id

// ─── The assistant handler over a store ──────────────────────────────

type private OneReplyProvider() =
    let mutable calls = 0

    member _.Calls = calls

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "one-reply"
            Model = "one-reply-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            calls <- calls + 1

            return
                Ok {
                    Content = "a reply"
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

type private OneProviderFactory(provider: IAIProvider) =
    interface IAIProviderFactory with
        member _.Available = []
        member _.PlatformDescriptors = []
        member _.PlatformDescriptor = None
        member _.Resolve _ = async { return Ok provider }
        member _.TryResolveByLabel(_, _) = async { return Ok provider }
        member _.BuildPlatform(_, _, _) = None

let private contextOver (storage: IBlobStorage) (provider: IAIProvider) : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore
    services.AddSingleton<IAIProviderFactory>(OneProviderFactory provider) |> ignore

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

    services.AddSingleton<AccessContext>(AccessContext.unrestricted (AnonymousSession "alice"))
    |> ignore

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Items["ToolUp.UserId"] <- box "alice"
    ctx :> HttpContext

let private assistantOver (storage: IBlobStorage) (provider: IAIProvider) : AIAssistantApi =
    fst (aiAssistantApi None Map.empty (new SSEConnectionManager()) (contextOver storage provider))

// ─── The read rule on the meta and consent siblings ──────────────────

let private readRuleTests =
    testList "absent is empty, present-but-unreadable is an error" [

        testCaseAsync "an override set on a conversation whose metadata cannot be read is refused, not written over"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let id = seedConversation storage
            put storage (metaBlob id) garbage

            let! result = (assistantOver storage (OneReplyProvider())).SetConversationOverride(id, Some "cheap-model")

            Expect.isError result "the override is refused"
            Expect.equal (bytesOf storage (metaBlob id)) garbage "the stored metadata is byte-identical"
        }

        testCaseAsync "an override on a conversation with no metadata yet is written"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let id = seedConversation storage

            let! result = (assistantOver storage (OneReplyProvider())).SetConversationOverride(id, Some "cheap-model")

            Expect.isOk result "absent metadata is empty metadata"

            let stored = Encoding.UTF8.GetString(bytesOf storage (metaBlob id))
            Expect.stringContains stored "cheap-model" "the override is stored"
        }

        testCaseAsync "a consent decision over an unreadable consent record leaves the record as it was"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let id = seedConversation storage
            put storage (consentBlob id) garbage

            do! AIConsentDispatch.recordDecision storage scope.Container id "Payroll" AllowForConversation

            Expect.equal (bytesOf storage (consentBlob id)) garbage "the consent record is byte-identical"
        }

        testCaseAsync "a consent decision on a conversation with no record yet is recorded"
        <| async {
            let storage = InMemoryBlobStorage() :> IBlobStorage
            let id = seedConversation storage

            do! AIConsentDispatch.recordDecision storage scope.Container id "Payroll" AllowForConversation

            let! state = AIConsentDispatch.loadState storage scope.Container id

            Expect.equal
                (state.CrossModuleAllowlist |> Map.tryFind "Payroll")
                (Some(AllowDecision.toToken AllowForConversation))
                "the decision is stored"
        }
    ]

// ─── One write order for both writers ────────────────────────────────

let private beaconFor (conversationId: Guid) : FastPathBeaconHandler.FastPathBeacon = {
    ConversationId = conversationId
    Tier = 1
    ModuleId = "sales"
    FieldName = "country"
    Instruction = "set country to UK"
    SyntheticReply = "Set country to UK."
    PatternMatched = "set {field} to {value}"
    LatencyMs = 3.5
    JsonFragment = "\"UK\""
    BeaconId = Guid.NewGuid().ToString()
}

let private postBeacon (storage: IBlobStorage) (beacon: FastPathBeaconHandler.FastPathBeacon) : int =
    let services = ServiceCollection()
    services.AddSingleton<IBlobStorage>(storage) |> ignore
    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box "alice"
    ctx.Items["ToolUp.StorageScope"] <- box scope
    ctx.Request.Body <- new IO.MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(beacon, wire)))
    ctx.Response.Body <- new IO.MemoryStream()

    FastPathBeaconHandler.beaconHandler (fun c -> Threading.Tasks.Task.FromResult(Some c)) ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

    ctx.Response.StatusCode

let private conversationUploads (storage: RecordingStorage) (id: Guid) =
    storage.Uploads
    |> List.filter (fun name -> name = uiBlob id || name = historyBlob id)

let private writeOrderTests =
    testList "both writers write history, then UI" [

        test "the fast-path beacon writes the history before the UI" {
            let storage = RecordingStorage(InMemoryBlobStorage())
            let id = seedConversation storage

            let status = postBeacon storage (beaconFor id)

            Expect.equal status 202 "the beacon was appended"

            Expect.equal
                (conversationUploads storage id)
                [ uiBlob id; historyBlob id; uiBlob id ]
                "seed, then history, then UI"
        }

        testCaseAsync "a full turn writes the history before the UI"
        <| async {
            let storage = RecordingStorage(InMemoryBlobStorage())
            let id = seedConversation storage
            let provider = OneReplyProvider()
            let assistant = assistantOver storage provider

            let! _ =
                assistant.SubmitMessage {
                    ConversationId = id
                    Content = "summarise this for me"
                    ActiveModule = None
                    ActivePage = None
                    ActivePageNarrative = None
                    OverrideProviderLabel = None
                    Surface = FullPage
                    RetrievalFilters = None
                }

            let mutable waited = 0

            while waited < 5000 && (conversationUploads storage id).Length < 3 do
                do! Async.Sleep 50
                waited <- waited + 50

            Expect.equal provider.Calls 1 "the turn ran"

            Expect.equal
                (conversationUploads storage id)
                [ uiBlob id; historyBlob id; uiBlob id ]
                "seed, then history, then UI"
        }
    ]

let tests =
    testList "Phase 1076 — the conversation record: one rule, one write order" [ readRuleTests; writeOrderTests ]