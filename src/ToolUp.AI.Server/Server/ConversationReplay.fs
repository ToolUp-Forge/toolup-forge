// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AI.ConversationReplay

open System
open System.Security.Cryptography
open System.Text
open Newtonsoft.Json
open ToolUp.Remoting.Json
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI

// ─── Phase 53 — Conversation replay ──────────────────────────────
//
// Re-runs a persisted conversation against a chosen provider /
// system prompt / SDK annotation, producing a NEW conversation with
// the same shape but fresh assistant responses. The original is
// immutable (GP 5 immutable-by-default) — replays never mutate.
//
// **What this MVP supports.**
// - Re-running user turns through `IAIProviderFactory` to capture
//   fresh assistant responses under different prompt / provider /
//   model conditions.
// - Persisting the replay as a first-class `Conversation` with its
//   own id, linked to the original via the `ConversationReplayed`
//   audit row.
// - Recording an operator-readable delta summary (turn count +
//   per-turn token deltas) on the audit row.
//
// **What this MVP does NOT support.**
// - Full agent-loop replay including tool-call dispatch. The agent
//   loop (`AIAgentEngine.runAgentLoop`) wires SSE streaming,
//   per-task cancellation, `ClientToolDispatchRegistry`, system
//   prompt builders with retrieval cells — replaying through it
//   needs a synthetic `HttpContext` + a fresh `bgScope` per replay
//   call. That's a deeper refactor of the agent loop's
//   request-context coupling (currently a Phase 6a follow-up).
//   Until that lands, replay surfaces "what does this model say to
//   the same user prompts under the new prompt/provider?" — useful
//   for prompt-engineering A/Bs + model comparison, NOT for
//   "did the agent take the same actions?" The latter is a future
//   `replayWithAgentLoop` follow-up; both can coexist behind the
//   same `IConversationStore` substrate.
//
// **Provider-fidelity caveat (documented per phase body).**
// Replays are not bit-identical. LLM determinism is not guaranteed
// even at `temperature = 0` — vendor infrastructure changes,
// streaming-cancellation edge cases, and silent model-version
// roll-forwards all introduce divergence. The audit row's `Delta`
// field is operator-readable, not machine-comparable.

// ─── JSON + digest helpers (shared with ConversationStore) ────────

let private jsonSettings =
    let s = JsonSerializerSettings()
    s.Converters.Add(FableJsonConverter())
    s

let private sha256Hex (bytes: byte[]) : string =
    let hash = SHA256.HashData(bytes)
    Convert.ToHexString(hash).ToLowerInvariant()

let private digestSystemPrompt (text: string) : string =
    text |> Encoding.UTF8.GetBytes |> sha256Hex

let private digestContent (content: AIMessageContent) : string =
    JsonConvert.SerializeObject(content, jsonSettings)
    |> Encoding.UTF8.GetBytes
    |> sha256Hex

// ─── Audit emission ──────────────────────────────────────────────

let private emitReplayedAudit
    (eventStore: IEventStore option)
    (scopeId: string)
    (originalId: ConversationId)
    (replayId: ConversationId)
    (newSystemPromptDigest: string option)
    (newProvider: string option)
    (sdkAnnotation: string option)
    (delta: string)
    : Async<unit> =
    async {
        match eventStore with
        | None -> return ()
        | Some store ->
            let payload: ConversationReplayedPayload = {
                OriginalConversationId = originalId
                ReplayConversationId = replayId
                NewSystemPromptDigest = newSystemPromptDigest
                NewProvider = newProvider
                SdkAnnotation = sdkAnnotation
                Delta = delta
            }

            let event: ModuleEvent = {
                Id = Guid.NewGuid()
                OccurredAt = DateTime.UtcNow
                ScopeId = scopeId
                SourceModule = ConversationsSourceModule.value
                EventType = "ConversationReplayed"
                Payload = JsonConvert.SerializeObject(payload, jsonSettings)
            }

            do! store.Write event
    }

// ─── Delta summary ───────────────────────────────────────────────

let private formatDelta (originalTurns: ConversationTurn list) (replayAssistantTurns: ConversationTurn list) : string =
    let originalAssistant = originalTurns |> List.filter (fun t -> t.Role = "assistant")

    let originalUserCount =
        originalTurns |> List.filter (fun t -> t.Role = "user") |> List.length

    let replayCount = replayAssistantTurns.Length

    let tokenDelta (turns: ConversationTurn list) =
        turns
        |> List.sumBy (fun t -> (defaultArg t.TokensIn 0) + (defaultArg t.TokensOut 0))

    let originalTotal = tokenDelta originalAssistant
    let replayTotal = tokenDelta replayAssistantTurns

    sprintf
        "Original %d user turns / %d assistant turns (%d tokens); replay produced %d assistant turns (%d tokens). Delta = %+d tokens."
        originalUserCount
        originalAssistant.Length
        originalTotal
        replayCount
        replayTotal
        (replayTotal - originalTotal)

// ─── Replay surface ──────────────────────────────────────────────

/// Re-runs a conversation through the chosen provider / prompt /
/// SDK annotation. Reads the original via `IConversationReader`,
/// constructs a new `Conversation` with a fresh id, calls the
/// provider per user turn (no agent-loop / tool dispatch — see file
/// header), persists each assistant response as a new
/// `ConversationTurn`, marks the replay `Completed`, and emits one
/// `ConversationReplayed` audit row.
///
/// `accessContext` is the operator's identity for the replay run —
/// the new `Conversation.CreatedBy` field. The DSR audit row attributes
/// the replay to this user even when the original was a different
/// user's conversation (a privileged operator running an A/B test).
///
/// Provider override resolution: when `options.ProviderLabel = Some
/// label`, the factory's `TryResolveByLabel(accessContext, label)` is
/// used. When `None`, the operator's default provider (`Resolve
/// accessContext`) is used — NOT the original conversation's
/// provider (the original is dead; the replay runs under live config).
let replay
    (store: IConversationStore)
    (factory: IAIProviderFactory)
    (eventStore: IEventStore option)
    (accessContext: AccessContext)
    (scopeId: string)
    (originalId: ConversationId)
    (options: ConversationReplayOptions)
    : Async<Result<ConversationReplayResult, ConversationError>> =
    async {
        // 1. Read original conversation.
        let! originalResult = (store :> IConversationReader).GetConversation(scopeId, originalId)

        match originalResult with
        | Error e -> return Error e
        | Ok(originalConv, originalTurns, _) ->

            // 2. Resolve replay provider.
            let! providerResult =
                match options.ProviderLabel with
                | Some label -> factory.TryResolveByLabel(accessContext, label)
                | None -> factory.Resolve accessContext

            match providerResult with
            | Error err -> return Error(ConversationError.StoreUnreachable(ProviderResolutionError.toMessage err))
            | Ok provider ->

                // 3. Construct the new conversation header.
                let replayId = Guid.NewGuid().ToString("N")
                let now = DateTime.UtcNow

                let newSystemPrompt = options.SystemPrompt

                let resolvedProviderName = provider.Capabilities.ProviderName
                let resolvedModelName = provider.Capabilities.Model

                let newConversation: ToolUp.Platform.Conversation = {
                    ConversationId = replayId
                    SchemaVersion = 1
                    CreatedAt = now
                    CreatedBy = accessContext.UserId
                    ScopeId = scopeId
                    Provider = resolvedProviderName
                    ModelName = resolvedModelName
                    SystemPromptDigest =
                        match newSystemPrompt with
                        | Some text -> digestSystemPrompt text
                        | None -> originalConv.SystemPromptDigest
                    SdkVersion = defaultArg options.SdkAnnotation originalConv.SdkVersion
                }

                let writer = store :> IConversationWriter
                let! beginResult = writer.BeginConversation(scopeId, newConversation)

                match beginResult with
                | Error e -> return Error e
                | Ok() ->

                    // 4. Walk original turns. User turns: append
                    //    verbatim to the replay. Assistant turns:
                    //    skipped (replay regenerates them).
                    //
                    //    Each user turn triggers a fresh provider
                    //    call with the running replay-turn history;
                    //    the new assistant response is persisted as
                    //    a new replay turn.
                    let mutable replayHistory: AIProviderMessage list = []
                    let mutable replayAssistantTurns: ConversationTurn list = []
                    let mutable encounteredFailure = false
                    let mutable failureDetail = ""

                    for originalTurn in originalTurns do
                        if not encounteredFailure && originalTurn.Role = "user" then
                            // Append user turn verbatim.
                            let userContent = originalTurn.Content

                            let userTurnRecord: ConversationTurn = {
                                TurnId = "" // overwritten with canonical id on append
                                ConversationId = replayId
                                SchemaVersion = 1
                                Role = "user"
                                Content = userContent
                                Timestamp = DateTime.UtcNow
                                TokensIn = None
                                TokensOut = None
                                ContentDigest = digestContent userContent
                            }

                            let! userAppendResult = writer.AppendTurn(scopeId, replayId, userTurnRecord)

                            match userAppendResult with
                            | Error _ ->
                                encounteredFailure <- true
                                failureDetail <- "User-turn append failed"
                            | Ok() ->
                                let providerUserMsg: AIProviderMessage = {
                                    Role = "user"
                                    Content = userContent.Content
                                    ToolCalls = []
                                    ToolResults = []
                                    Parts = userContent.Parts
                                }

                                replayHistory <- replayHistory @ [ providerUserMsg ]

                                // Call provider for assistant response.
                                let! providerResponseResult =
                                    provider.SendMessage(replayHistory, [], newSystemPrompt, None, RetryPolicy.defaults)

                                match providerResponseResult with
                                | Error err ->
                                    encounteredFailure <- true
                                    failureDetail <- sprintf "Provider call failed: %A" err
                                | Ok providerResponse ->
                                    let assistantContent: AIMessageContent = {
                                        Role = "assistant"
                                        Content = providerResponse.Content
                                        ToolCalls = providerResponse.ToolCalls
                                        ToolResults = []
                                        Parts = []
                                    }

                                    let tokensIn = providerResponse.Usage |> Option.map _.PromptTokens

                                    let tokensOut = providerResponse.Usage |> Option.map _.OutputTokens

                                    let assistantTurn: ConversationTurn = {
                                        TurnId = ""
                                        ConversationId = replayId
                                        SchemaVersion = 1
                                        Role = "assistant"
                                        Content = assistantContent
                                        Timestamp = DateTime.UtcNow
                                        TokensIn = tokensIn
                                        TokensOut = tokensOut
                                        ContentDigest = digestContent assistantContent
                                    }

                                    let! appendResult = writer.AppendTurn(scopeId, replayId, assistantTurn)

                                    match appendResult with
                                    | Error _ ->
                                        encounteredFailure <- true
                                        failureDetail <- "Assistant-turn append failed"
                                    | Ok() ->
                                        replayAssistantTurns <- replayAssistantTurns @ [ assistantTurn ]

                                        let providerAssistantMsg: AIProviderMessage = {
                                            Role = "assistant"
                                            Content = providerResponse.Content
                                            ToolCalls = providerResponse.ToolCalls
                                            ToolResults = []
                                            Parts = []
                                        }

                                        replayHistory <- replayHistory @ [ providerAssistantMsg ]

                    // 5. Mark the replay terminal — `Completed` on
                    //    happy-path, `Errored` if any turn-write or
                    //    provider call failed.
                    let terminalStatus =
                        if encounteredFailure then
                            ConversationStatus.Errored failureDetail
                        else
                            ConversationStatus.Completed

                    let! _ = writer.MarkStatus(scopeId, replayId, terminalStatus)

                    // 6. Emit the replay audit row.
                    let delta = formatDelta originalTurns replayAssistantTurns

                    let newPromptDigest = options.SystemPrompt |> Option.map digestSystemPrompt

                    do!
                        emitReplayedAudit
                            eventStore
                            scopeId
                            originalId
                            replayId
                            newPromptDigest
                            options.ProviderLabel
                            options.SdkAnnotation
                            delta

                    return
                        Ok {
                            ReplayConversationId = replayId
                            OriginalConversationId = originalId
                            NewAssistantTurns = replayAssistantTurns
                            Delta = delta
                        }
    }