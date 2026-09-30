// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FastPathBeaconIdempotencyTests

open System
open Expecto
open ToolUp.Platform
open ToolUp.AI
open ToolUp.AI.FastPathBeaconHandler
open ToolUp.Platform.Tests.Contracts.InMemoryBlobStorage

// ─── Phase 6j.E — beacon endpoint idempotency ────────────────────
//
// A page reload mid-flight, a browser network blip, or any client retry
// re-POSTs the same beacon. The append path is not naturally idempotent:
// without a key the synthetic `User + AIAssistant` pair lands twice and
// every subsequent agent-loop turn reads the same instruction twice over.
//
// The load-bearing decision is `planBeaconAppend` — pure, and the SINGLE
// gate over both persisted blobs (the handler returns before either
// write, so they cannot disagree about whether a beacon landed). Testing
// it directly is therefore testing the acceptance criterion, not a proxy
// for it: the HTTP layer around it is the same scope / ownership /
// status-code glue Phase 6j.D's tests already reason about.
//
// `Id` and `Timestamp` are fresh per construction by design, so "the
// sequence is identical" is asserted over the fields that carry meaning
// — participant, content, tool-call name, and the stamped key.

let private beacon (beaconId: string) (instruction: string) : FastPathBeacon = {
    ConversationId = Guid.NewGuid()
    Tier = 1
    ModuleId = "sales"
    FieldName = "country"
    Instruction = instruction
    SyntheticReply = "Set country to UK."
    PatternMatched = "set {field} to {value}"
    LatencyMs = 3.5
    JsonFragment = "\"UK\""
    BeaconId = beaconId
}

/// The meaning-bearing projection of a persisted turn: everything except
/// the deliberately-fresh `Id` / `Timestamp` / `ConversationId`.
let private shape (m: ConversationMessage) =
    m.Participant, m.Content, (m.ToolCalls |> List.map _.ToolName), m.CreatedBy, m.BeaconId

let private shapes = List.map shape

/// Apply a beacon the way the handler does: append the planned turns, or
/// leave the conversation untouched when the plan is `None`.
let private applyBeacon (existing: ConversationMessage list) (b: FastPathBeacon) =
    match planBeaconAppend existing "alice" b with
    | Some turns -> existing @ turns
    | None -> existing

let private planTests =
    testList "planBeaconAppend — Phase 6j.E append decision" [
        testCase "single POST appends the synthetic User + AIAssistant pair"
        <| fun _ ->
            let after = applyBeacon [] (beacon "b-1" "set country to UK")

            Expect.equal (List.length after) 2 "one beacon appends exactly two turns"
            Expect.equal (after[0].Participant) User "first turn is the synthetic user instruction"
            Expect.equal (after[1].Participant) AIAssistant "second turn is the synthetic reply"
            Expect.equal (after[0].Content) "set country to UK" "the instruction is persisted verbatim"

        testCase "duplicate POST of the same key does not double-append"
        <| fun _ ->
            // The acceptance criterion, stated directly: N POSTs of one
            // beacon leave exactly the conversation one POST leaves.
            let b = beacon "b-dup" "set country to UK"
            let once = applyBeacon [] b
            let twice = applyBeacon once b
            let thrice = applyBeacon twice b

            Expect.equal (List.length twice) 2 "the second POST appends nothing"
            Expect.equal (List.length thrice) 2 "nor does a third"
            Expect.equal (shapes twice) (shapes once) "the sequence after two POSTs is the sequence after one"
            Expect.equal (shapes thrice) (shapes once) "and after three"

        testCase "the duplicate decision is None, not a discarded pair"
        <| fun _ ->
            // Why this matters separately: an implementation that built
            // the turns and then dropped them would pass the sequence
            // assertion above while still reading and rewriting the
            // provider-history blob. `None` is what makes the handler
            // return before either write.
            let b = beacon "b-none" "set country to UK"
            let once = applyBeacon [] b

            Expect.isNone (planBeaconAppend once "alice" b) "a repeat plans no work at all"

        testCase "distinct keys append normally"
        <| fun _ ->
            let after =
                []
                |> fun s -> applyBeacon s (beacon "b-1" "set country to UK")
                |> fun s -> applyBeacon s (beacon "b-2" "set country to France")

            Expect.equal (List.length after) 4 "two distinct beacons append two pairs"
            Expect.equal (after[0].Content) "set country to UK" "first instruction survives"
            Expect.equal (after[2].Content) "set country to France" "second instruction is appended after it"

        testCase "both turns of the pair carry the key"
        <| fun _ ->
            // The window is counted in MESSAGES, so a pair straddling its
            // edge would be half-invisible if only one turn were stamped.
            let after = applyBeacon [] (beacon "b-both" "set country to UK")

            Expect.equal (after[0].BeaconId) "b-both" "user turn carries the key"
            Expect.equal (after[1].BeaconId) "b-both" "assistant turn carries it too"

        testCase "an empty key is admitted every time — pre-6j.E client behaviour is unchanged"
        <| fun _ ->
            // An older client sends no key, so there is nothing to
            // deduplicate on. Refusing or suppressing it would break the
            // fast path on the upgrade; it must append exactly as before.
            let b = beacon "" "set country to UK"
            let once = applyBeacon [] b
            let twice = applyBeacon once b

            Expect.equal (List.length once) 2 "an unkeyed beacon appends"
            Expect.equal (List.length twice) 4 "and a second unkeyed beacon appends again — no dedup"

        testCase "a null key behaves as an empty key"
        <| fun _ ->
            // A missing JSON property deserialises to `null` under
            // FableConverters, which is what a pre-6j.E client actually
            // produces on the wire — `""` is the F#-authored stand-in.
            let b = beacon null "set country to UK"
            let once = applyBeacon [] b
            let twice = applyBeacon once b

            Expect.equal (List.length twice) 4 "a null-keyed beacon is never a duplicate"
            Expect.equal (once[0].BeaconId) "" "and the persisted key is normalised to empty, never null"
    ]

let private scanTests =
    testList "isDuplicateBeacon — Phase 6j.E tail scan" [
        testCase "empty conversation — nothing to match"
        <| fun _ -> Expect.isFalse (isDuplicateBeacon [] "b-1") "an empty conversation holds no keys"

        testCase "key present in the tail — duplicate"
        <| fun _ ->
            let existing = applyBeacon [] (beacon "b-1" "set country to UK")
            Expect.isTrue (isDuplicateBeacon existing "b-1") "the stamped key is found"

        testCase "different key — not a duplicate"
        <| fun _ ->
            let existing = applyBeacon [] (beacon "b-1" "set country to UK")
            Expect.isFalse (isDuplicateBeacon existing "b-2") "an unrelated key does not match"

        testCase "legacy messages carrying no key never suppress a live beacon"
        <| fun _ ->
            // Pre-6j.E blobs deserialise the absent field to `null`. A
            // keyless persisted message must not match a keyless — or any
            // — incoming beacon, else legacy history would swallow live
            // fast-path turns.
            let legacy: ConversationMessage = {
                Id = Guid.NewGuid()
                ConversationId = Guid.NewGuid()
                Participant = User
                Content = "set country to UK"
                Timestamp = DateTime.UtcNow
                ToolCalls = []
                RetrievedSources = []
                Parts = []
                CreatedBy = "alice"
                BeaconId = null
                Verification = None
            }

            Expect.isFalse (isDuplicateBeacon [ legacy ] "") "keyless against keyless is not a match"
            Expect.isFalse (isDuplicateBeacon [ legacy ] "b-1") "nor keyless against a real key"
            Expect.equal (List.length (applyBeacon [ legacy ] (beacon "b-1" "x"))) 3 "the live beacon still appends"

        testCase "a key older than the window is no longer a duplicate"
        <| fun _ ->
            // Deliberate: a key resurfacing long after its turn is a
            // replay, not a retry — a different event, which should
            // append rather than silently vanish. The window is what
            // keeps the scan's cost independent of conversation length.
            let b = beacon "b-old" "set country to UK"
            let mutable convo = applyBeacon [] b

            // Each subsequent beacon adds two messages; push the original
            // pair clear of the window.
            for i in 1 .. (BeaconDedupWindow / 2) do
                convo <- applyBeacon convo (beacon $"b-filler-{i}" $"set country to C{i}")

            Expect.isGreaterThan (List.length convo) BeaconDedupWindow "the conversation now exceeds the window"
            Expect.isFalse (isDuplicateBeacon convo "b-old") "the evicted key no longer matches"

        testCase "a key at the edge of the window is still a duplicate"
        <| fun _ ->
            // The complement of the case above: eviction must happen at
            // the boundary, not before it. Filling to exactly the window
            // size keeps the original pair inside.
            let b = beacon "b-edge" "set country to UK"
            let mutable convo = applyBeacon [] b

            for i in 1 .. (BeaconDedupWindow / 2 - 1) do
                convo <- applyBeacon convo (beacon $"b-filler-{i}" $"set country to C{i}")

            Expect.equal (List.length convo) BeaconDedupWindow "the conversation exactly fills the window"
            Expect.isTrue (isDuplicateBeacon convo "b-edge") "the oldest in-window key still matches"
    ]

// ─── Phase 862 — the beacon cannot truncate a conversation ───────
//
// The assistant handler and the beacon handler share two blobs per
// conversation. Before Phase 862 the beacon read the provider history
// through a private shadow of `AIProviderMessage` whose `ToolResults` was
// a tuple list and which had no `Parts`: any history holding a tool turn
// failed to decode, the failure read as an EMPTY history, and the beacon
// wrote `[] @ [user; assistant]` over it. These cases drive the REAL
// `beaconHandler` over an in-memory store, seeding the blobs with exactly
// the bytes the assistant handler writes (the canonical wire types through
// `FableConverters`) and reading them back the way it reads them.

let private wireOptions =
    ToolUp.Remoting.Json.SystemTextJson.FableConverters.create ()

let private historyScope: StorageScope = {
    ScopeId = "t862"
    Container = "team-t862"
    Persist = true
}

let private conversationBlob (id: Guid) = $"ai-conversations/{id}.json"
let private historyBlob (id: Guid) = $"ai-conversations/{id}.history.json"

let private encode (value: 'T) =
    System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(value, wireOptions))

let private decode<'T> (bytes: byte[]) =
    System.Text.Json.JsonSerializer.Deserialize<'T>(System.Text.Encoding.UTF8.GetString bytes, wireOptions)

/// A store whose writes to one named blob fail — the failed-save case.
type private FailingWrites(inner: ToolUp.Platform.BlobStorage.IBlobStorage, failing: string) =
    interface ToolUp.Platform.BlobStorage.IBlobStorage with
        member _.CanComposeFrom = inner.CanComposeFrom

        member _.Upload(container, blobName, content) =
            if blobName = failing then
                async.Return(Error "injected write failure")
            else
                inner.Upload(container, blobName, content)

        member _.Download(container, blobName) = inner.Download(container, blobName)
        member _.Delete(container, blobName) = inner.Delete(container, blobName)
        member _.List(container, prefix) = inner.List(container, prefix)
        member _.Exists(container, blobName) = inner.Exists(container, blobName)
        member _.GetMetadata(container, blobName) = inner.GetMetadata(container, blobName)

        member _.DownloadRange(container, blobName, offset, length) =
            inner.DownloadRange(container, blobName, offset, length)

        member _.ComposeFrom(container, blobName, parts) =
            inner.ComposeFrom(container, blobName, parts)

        member _.Erase(container, prefix, policy, dryRun) =
            inner.Erase(container, prefix, policy, dryRun)

/// POST one beacon through the real handler; returns the status code.
let private postBeacon (storage: ToolUp.Platform.BlobStorage.IBlobStorage) (b: FastPathBeacon) : int =
    let services = Microsoft.Extensions.DependencyInjection.ServiceCollection()

    Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<
        ToolUp.Platform.BlobStorage.IBlobStorage
     >(
        services,
        storage
    )
    |> ignore

    let ctx = Microsoft.AspNetCore.Http.DefaultHttpContext()

    ctx.RequestServices <-
        Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider
            services

    ctx.Items["ToolUp.UserId"] <- box "alice"
    ctx.Items["ToolUp.StorageScope"] <- box historyScope
    ctx.Request.Body <- new System.IO.MemoryStream(encode b)
    ctx.Response.Body <- new System.IO.MemoryStream()

    beaconHandler (fun c -> System.Threading.Tasks.Task.FromResult(Some c)) ctx
    |> Async.AwaitTask
    |> Async.RunSynchronously
    |> ignore

    ctx.Response.StatusCode

let private seed (storage: ToolUp.Platform.BlobStorage.IBlobStorage) (blobName: string) (bytes: byte[]) =
    match
        storage.Upload(historyScope.Container, blobName, bytes)
        |> Async.RunSynchronously
    with
    | Ok _ -> ()
    | Error e -> failtestf "seeding %s failed: %s" blobName e

let private read (storage: ToolUp.Platform.BlobStorage.IBlobStorage) (blobName: string) : byte[] =
    match storage.Download(historyScope.Container, blobName) |> Async.RunSynchronously with
    | Ok bytes -> bytes
    | Error e -> failtestf "reading %s failed: %s" blobName e

/// A provider history the assistant handler writes after a turn that
/// called a tool: the tool_use on the assistant turn, the tool_result on
/// the user turn that answers it.
let private toolTurnHistory: ToolUp.Platform.AI.AIProviderMessage list = [
    ToolUp.Platform.AI.AIProviderMessage.text "user" "what is the quarter's total?"
    {
        ToolUp.Platform.AI.AIProviderMessage.text "assistant" "" with
            ToolCalls = [
                {
                    Id = "toolu_1"
                    Name = "sales.sum"
                    Arguments = """{"quarter":"Q3"}"""
                }
            ]
    }
    {
        ToolUp.Platform.AI.AIProviderMessage.text "user" "" with
            ToolResults = [
                {
                    ToolCallId = "toolu_1"
                    Content = "42"
                }
            ]
    }
    ToolUp.Platform.AI.AIProviderMessage.text "assistant" "The quarter's total is 42."
]

let private priorConversation (conversationId: Guid) : ConversationMessage list = [
    {
        Id = Guid.NewGuid()
        ConversationId = conversationId
        Participant = User
        Content = "what is the quarter's total?"
        Timestamp = DateTime.UtcNow
        ToolCalls = []
        RetrievedSources = []
        Parts = []
        CreatedBy = "alice"
        BeaconId = ""
        Verification = None
    }
]

let private historyTests =
    testList "beaconHandler — Phase 862 conversation history has one type and one writer" [
        testCase "a history holding a tool turn survives a beacon append, every message intact"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
            let b = beacon "b-862" "set country to UK"
            seed storage (conversationBlob b.ConversationId) (encode (priorConversation b.ConversationId))
            seed storage (historyBlob b.ConversationId) (encode toolTurnHistory)

            let status = postBeacon storage b
            Expect.equal status 202 "the beacon is accepted"

            let after =
                decode<ToolUp.Platform.AI.AIProviderMessage list> (read storage (historyBlob b.ConversationId))

            Expect.equal (List.length after) (List.length toolTurnHistory + 2) "prior history plus one exchange"
            Expect.equal (List.truncate 4 after) toolTurnHistory "every prior message, tool turn included, is intact"

            for m in after do
                Expect.isFalse (isNull (box m.Parts)) $"Parts is present on the '{m.Role}' turn '{m.Content}'"

            Expect.equal (after[4].Content) "set country to UK" "the beacon's instruction is appended"
            Expect.equal (after[5].Role) "assistant" "followed by its synthetic reply"

        testCase "a history entry written without Parts reads back with Parts empty"
        <| fun _ ->
            // The shape the pre-862 beacon wrote: no `Parts` member at all.
            let storage = InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
            let b = beacon "b-862-parts" "set country to UK"

            let legacy =
                """[{"Role":"user","Content":"hello","ToolCalls":[],"ToolResults":[]},{"Role":"assistant","Content":"hi","ToolCalls":[],"ToolResults":[]}]"""

            seed storage (historyBlob b.ConversationId) (System.Text.Encoding.UTF8.GetBytes legacy)

            Expect.equal (postBeacon storage b) 202 "the beacon is accepted"

            let after =
                decode<ToolUp.Platform.AI.AIProviderMessage list> (read storage (historyBlob b.ConversationId))

            Expect.equal (List.length after) 4 "both legacy messages survive"

            for m in after do
                Expect.equal m.Parts [] $"Parts is coerced to empty on the '{m.Role}' turn"

            Expect.isFalse
                (after |> List.exists ToolUp.Platform.AI.AIProviderMessage.isMultimodal)
                "and reads as text-only"

        testCase "an undecodable provider history is refused and left byte-identical"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
            let b = beacon "b-862-corrupt" "set country to UK"
            let conversationBytes = encode (priorConversation b.ConversationId)

            let corrupt =
                System.Text.Encoding.UTF8.GetBytes """[{"Role":"user","Content":"trunc"""

            seed storage (conversationBlob b.ConversationId) conversationBytes
            seed storage (historyBlob b.ConversationId) corrupt

            let status = postBeacon storage b
            Expect.equal status 409 "a history that cannot be decoded refuses the append"
            Expect.equal (read storage (historyBlob b.ConversationId)) corrupt "the history blob is byte-identical"

            Expect.equal
                (read storage (conversationBlob b.ConversationId))
                conversationBytes
                "and the conversation blob is untouched: the refusal precedes both writes"

        testCase "an undecodable conversation blob is refused and left byte-identical"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
            let b = beacon "b-862-corrupt-ui" "set country to UK"
            let corrupt = System.Text.Encoding.UTF8.GetBytes "{not a conversation"
            seed storage (conversationBlob b.ConversationId) corrupt

            let status = postBeacon storage b
            Expect.equal status 409 "a conversation that cannot be decoded refuses the append"
            Expect.equal (read storage (conversationBlob b.ConversationId)) corrupt "the blob is byte-identical"

            Expect.isFalse
                (storage.Exists(historyScope.Container, historyBlob b.ConversationId)
                 |> Async.RunSynchronously)
                "and no provider history was written beside it"

        testCase "an absent history is an empty history"
        <| fun _ ->
            let storage = InMemoryBlobStorage() :> ToolUp.Platform.BlobStorage.IBlobStorage
            let b = beacon "b-862-new" "set country to UK"

            Expect.equal (postBeacon storage b) 202 "a first beacon on a new conversation is accepted"

            let after =
                decode<ToolUp.Platform.AI.AIProviderMessage list> (read storage (historyBlob b.ConversationId))

            Expect.equal (after |> List.map _.Role) [ "user"; "assistant" ] "exactly the one exchange"

        testCase "a failed history write is reported, not acknowledged"
        <| fun _ ->
            let b = beacon "b-862-write" "set country to UK"

            let storage =
                FailingWrites(InMemoryBlobStorage(), historyBlob b.ConversationId)
                :> ToolUp.Platform.BlobStorage.IBlobStorage

            Expect.equal (postBeacon storage b) 500 "the beacon does not answer 202 over a write that failed"
    ]

let tests =
    testList "FastPathBeaconIdempotency" [ planTests; scanTests; historyTests ]