module ToolUp.AI.AIAssistantHandler

open System
open System.Text
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.BlobStorage
open ToolUp.Platform.StorageScopeResolver
open ToolUp.Platform.VectorKnowledgeTypes
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.AIAgentEngine
open ToolUp.AI.SSEHandler

// ─── JSON helpers ────────────────────────────────────────────────
// Uses FableJsonConverter (from ToolUp.Remoting.Json) so that persisted
// conversations with F# DUs serialize in Fable-compatible format.

let private jsonOptions = FableConverters.create ()

let private toJson obj =
    JsonSerializer.Serialize(obj, jsonOptions)

let private fromJson<'T> (bytes: byte[]) =
    let json = Encoding.UTF8.GetString(bytes)
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

// ─── Phase 6h follow-up — bounded waits on the chat path ─────────
//
// Provider resolution, history loads, and conversation loads each
// touch I/O (blob storage, secret store, config store). None had
// timeouts before, so a wedged backend would hang the agent loop
// indefinitely and present to the user as a silent 60-second
// watchdog timeout. Bounding each call to 10 s converts the hang
// into a thrown exception, which the surrounding `bgWork`'s
// `try/with | ex -> AITaskFailed ex.Message` surfaces inline in
// chat (now ungated by `DebugMode` on the client side panel).

[<Literal>]
let private ChatPathIoTimeoutMs = 10_000

/// Run `work` with a wall-clock cap. On timeout, raise
/// `TimeoutException` carrying `label` so the surrounding handler's
/// catch can surface it to the user via `AITaskFailed`.
let private withTimeout (label: string) (timeoutMs: int) (work: Async<'T>) : Async<'T> = async {
    let workTask = work |> Async.StartAsTask
    let timeoutTask = Task.Delay(timeoutMs)
    let! winner = Task.WhenAny(workTask :> Task, timeoutTask) |> Async.AwaitTask

    if winner = (workTask :> Task) then
        return! workTask |> Async.AwaitTask
    else
        return raise (TimeoutException $"{label} timed out after {timeoutMs / 1000}s")
}

// ─── Conversation persistence ────────────────────────────────────
//
// Each conversation has two sibling blobs:
//
//   ai-conversations/{id}.json          UI-facing `ConversationMessage list`
//                                        (user/assistant text only — what the
//                                        chat panel renders).
//   ai-conversations/{id}.history.json  Full `AIProviderMessage list` — every
//                                        tool_use / tool_result pair intact,
//                                        round-trippable back to the AI
//                                        provider on the next turn.
//
// Why two blobs instead of one:
// The provider round-trip requires the full history (tool_use in assistant
// messages paired with tool_result in user messages — Claude rejects
// requests that break pairing with HTTP 400). The UI does not display
// those intermediate turns and the shared `ConversationMessage` type does
// not carry tool_use / tool_result shapes. Keeping the two stores parallel
// means:
//   - The UI contract (`GetConversation` → `ConversationMessage list`) is
//     unchanged; clients see the same shape they always have.
//   - The agent loop gets the complete history every turn; no more orphaned
//     tool_result blocks.
//   - Pre-existing conversations migrate gracefully: if the history blob
//     is missing we start from an empty provider history, which is the
//     same behaviour as today (just without broken round-tripping).

let private conversationBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.json"

let private providerHistoryBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.history.json"

// Per-conversation metadata sibling. Carries server-only fields the
// UI doesn't render directly (currently the BYOK provider override).
// Kept separate from the UI-facing message blob so list-page and
// chat-page reads don't pull either side's payload unnecessarily.
let private conversationMetaBlobName (conversationId: Guid) =
    $"ai-conversations/{conversationId}.meta.json"

type private ConversationMeta = { OverrideProviderLabel: string option }

module private ConversationMeta =
    let empty = { OverrideProviderLabel = None }

let private saveConversation
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (messages: ConversationMessage list)
    =
    async {
        let bytes = toJson messages |> Encoding.UTF8.GetBytes
        let! _ = storage.Upload(container, conversationBlobName conversationId, bytes)
        return ()
    }

let private loadConversation (logger: ILogger) (storage: IBlobStorage) (container: string) (conversationId: Guid) = async {
    let! result = storage.Download(container, conversationBlobName conversationId)

    match result with
    | Ok bytes ->
        try
            return fromJson<ConversationMessage list> bytes
        with ex ->
            // Present-but-corrupt ≠ absent. Silently returning []
            // makes the conversation appear to "forget" everything
            // with no signal — log so the operator can tell a
            // corrupt blob from a genuinely new conversation.
            logger.Warn
                $"AI conversation {conversationId} blob is present but unparseable ({ex.Message}); treating as empty for this read."

            return []
    | Error _ -> return []
}

let private saveProviderHistory
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (messages: AIProviderMessage list)
    =
    async {
        let bytes = toJson messages |> Encoding.UTF8.GetBytes
        let! _ = storage.Upload(container, providerHistoryBlobName conversationId, bytes)
        return ()
    }

let private loadProviderHistory (logger: ILogger) (storage: IBlobStorage) (container: string) (conversationId: Guid) = async {
    let! result = storage.Download(container, providerHistoryBlobName conversationId)

    match result with
    | Ok bytes ->
        try
            return fromJson<AIProviderMessage list> bytes
        with ex ->
            logger.Warn
                $"AI provider-history blob for conversation {conversationId} is present but unparseable ({ex.Message}); the next turn loses prior tool context."

            return []
    | Error _ -> return []
}

let private saveConversationMeta
    (storage: IBlobStorage)
    (container: string)
    (conversationId: Guid)
    (meta: ConversationMeta)
    =
    async {
        let bytes = toJson meta |> Encoding.UTF8.GetBytes
        let! _ = storage.Upload(container, conversationMetaBlobName conversationId, bytes)
        return ()
    }

let private loadConversationMeta (logger: ILogger) (storage: IBlobStorage) (container: string) (conversationId: Guid) = async {
    let! result = storage.Download(container, conversationMetaBlobName conversationId)

    match result with
    | Ok bytes ->
        try
            return fromJson<ConversationMeta> bytes
        with ex ->
            logger.Warn
                $"AI conversation-meta blob for {conversationId} is present but unparseable ({ex.Message}); falling back to defaults (provider-label override lost)."

            return ConversationMeta.empty
    | Error _ -> return ConversationMeta.empty
}

// ─── Provider message conversion ─────────────────────────────────

let private toProviderMessage (msg: ConversationMessage) : AIProviderMessage = {
    Role =
        match msg.Participant with
        | User -> "user"
        | AIAssistant -> "assistant"
        | System -> "system"
    Content = msg.Content
    ToolCalls =
        msg.ToolCalls
        |> List.map (fun tc -> {
            AIProviderToolCall.Id = string tc.ToolCallId
            Name = tc.ToolName
            Arguments = tc.Arguments
        })
    ToolResults = []
    // Phase 6o follow-up B: round-trip multipart content through
    // history replay. A multipart user turn persisted on a prior
    // request reaches the provider as the same content-block array
    // on the next turn; plain-text turns continue to carry
    // `Parts = []` and the provider emits the legacy string shape.
    Parts = msg.Parts
}

let private toConversationMessage
    (conversationId: Guid)
    (participant: ParticipantType)
    (content: string)
    (toolCalls: ToolCallRecord list)
    (retrievedSources: RetrievedSource list)
    (parts: AIContentPart list)
    (createdBy: string)
    : ConversationMessage =
    {
        Id = Guid.NewGuid()
        ConversationId = conversationId
        Participant = participant
        Content = content
        Timestamp = DateTime.UtcNow
        ToolCalls = toolCalls
        RetrievedSources = retrievedSources
        Parts = parts
        // Phase 6j.D — populated for the first persisted user message of
        // a conversation; subsequent appends are gated against it by the
        // ownership check inside `bgWork`. Empty for assistant turns
        // (only the first message's `CreatedBy` is authoritative).
        CreatedBy = createdBy
    }

// ─── Background context ──────────────────────────────────────────

/// Create a synthetic HttpContext with a new DI scope for background work.
/// The original request HttpContext is disposed after the response is sent,
/// so background work (the agent loop) needs its own scope.
///
/// Copies the pre-resolved scope / user / permission items from the
/// original request into the background context's `Items` dictionary so
/// any tool that reads them (`ToolUp.StorageScope` in particular) hits
/// the same store the Data Manager API used on the originating request.
/// Without this copy the agent loop falls back to `getStoreForUser`
/// which resolves a different container in every mode EXCEPT the one
/// where it happens to match — in Anonymous mode the user's files live
/// in `session-{sessionId}` and the fallback looks in `user-{userId}`,
/// which is always empty, so tools see no uploaded files at all.
let private createBackgroundContext (ctx: HttpContext) (userId: string) =
    let scopeFactory = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>()

    let scope = scopeFactory.CreateScope()
    let bgCtx = DefaultHttpContext()
    bgCtx.RequestServices <- scope.ServiceProvider
    // Preserve user identity headers so scope resolvers work
    bgCtx.Request.Headers.Append("X-User-Id", userId)

    // Carry forward the resolved scope / user / permission state from the
    // middleware-decorated original context. Keys are the exact strings
    // populated by `ScopeResolutionMiddleware` in `SDK.Server.fs`; keeping
    // the list in lockstep with that middleware is part of the
    // "Phase 6a extract SSE into a generic notification channel" work —
    // a typed `RequestContext` would remove this stringly-typed coupling.
    let itemsToCopy = [ "ToolUp.StorageScope"; "ToolUp.UserId"; "ToolUp.ModulePermissions" ]

    for key in itemsToCopy do
        match ctx.Items.TryGetValue key with
        | true, value -> bgCtx.Items[key] <- value
        | _ -> ()

    bgCtx, scope

// ─── API implementation ──────────────────────────────────────────

/// Build the AIAssistantApi Fable.Remoting handler.
/// Mirrors the pattern from fileManagementApi in FileManagement.fs.
let aiAssistantApi
    (config: SystemPromptBuilder.AIAssistantServerConfig option)
    (moduleContexts: Map<string, ModuleAIContext>)
    (sseManager: SSEConnectionManager)
    (ctx: HttpContext)
    : AIAssistantApi =
    let providerFactory =
        ctx.RequestServices.GetService(typeof<IAIProviderFactory>) :?> IAIProviderFactory

    let registry =
        ctx.RequestServices.GetService(typeof<AIToolRegistry>) :?> AIToolRegistry

    // Phase 6g.A: per-process registry of pending client-resident tool
    // calls. The agent loop suspends on a TCS keyed by toolCallId; the
    // /api/ai/tool-result POST handler completes it. Singleton — same
    // instance everywhere it's resolved.
    let dispatchRegistry =
        ctx.RequestServices.GetService(typeof<ClientToolDispatch.ClientToolDispatchRegistry>)
        :?> ClientToolDispatch.ClientToolDispatchRegistry

    // Phase 6h: per-task `CancellationTokenSource` registry for
    // cancel-mid-stream. The handler registers a CTS before
    // kicking off the agent loop; /api/ai/cancel/{taskId} cancels
    // it; the handler's try/finally unregisters on natural
    // completion.
    let cancellationRegistry =
        ctx.RequestServices.GetService(typeof<AICancellationRegistry.AICancellationRegistry>)
        :?> AICancellationRegistry.AICancellationRegistry

    let storage = ctx.RequestServices.GetService(typeof<IBlobStorage>) :?> IBlobStorage

    // Observational logger; silent no-op when DI has none (tests
    // bypassing `compose`). Must not log prompt content or API keys.
    let logger: ILogger =
        match ctx.RequestServices.GetService(typeof<ILogger>) with
        | :? ILogger as l -> l
        | _ ->
            { new ILogger with
                member _.Debug _ = ()
                member _.Info _ = ()
                member _.Warn _ = ()
                member _.Error(_, _) = ()
            }

    // Phase 6j.D — `IAuditLog` for the BeaconRejected emission on the
    // symmetric ownership-gate path. `None` in test compositions that
    // bypass `compose` (no DI registration); the gate stays effective
    // — only the audit-trail row is skipped.
    let auditLogOpt =
        match ctx.RequestServices.GetService(typeof<IAuditLog>) with
        | :? IAuditLog as a -> Some a
        | _ -> None

    // Phase 53 — opt-in conversation persistence. Resolves
    // `IConversationStore` once per handler invocation; `null`
    // (the default, `ServerConfig.ConversationStore =
    // NoConversationStore`) gates every substrate-hook closure
    // below to a no-op so the pre-Phase-53 path runs byte-for-byte.
    // The `ConvBridge` closures stringify the existing `Guid`-typed
    // `conversationId` at the seam — see `ConversationTypes.fs`
    // for the identity-by-value rationale.
    let conversationStore =
        match ctx.RequestServices.GetService(typeof<IConversationStore>) with
        | :? IConversationStore as s -> Some s
        | _ -> None

    let convId (id: Guid) : ConversationId = id.ToString("N")

    let sha256OfText (text: string) : string =
        let bytes = System.Text.Encoding.UTF8.GetBytes text
        let hash = System.Security.Cryptography.SHA256.HashData bytes
        Convert.ToHexString(hash).ToLowerInvariant()

    let digestOfContent (content: AIMessageContent) : string =
        ConversationStore.computeContentDigest content

    /// Begin the substrate's conversation record on first turn. No-op
    /// when the store is absent OR the conversation already exists
    /// (idempotent — the store's `BeginConversation` returns Ok in
    /// either case so we never double-emit `ConversationStarted`).
    let convBeginIfNeeded
        (conversationId: Guid)
        (userIdLocal: string)
        (scopeIdLocal: string)
        (providerName: string)
        (modelName: string)
        (systemPrompt: string option)
        : Async<unit> =
        async {
            match conversationStore with
            | None -> return ()
            | Some store ->
                let id = convId conversationId

                let writer = store :> IConversationWriter
                let reader = store :> IConversationReader

                // Read-then-create. The store's BeginConversation
                // is idempotent, but we avoid the BeginConversation
                // audit emit on a re-open by gating on existence
                // first.
                let! existing = reader.GetConversation(scopeIdLocal, id)

                match existing with
                | Ok _ -> return ()
                | Error _ ->
                    let conversation: ToolUp.Platform.Conversation = {
                        ConversationId = id
                        SchemaVersion = 1
                        CreatedAt = DateTime.UtcNow
                        CreatedBy = userIdLocal
                        ScopeId = scopeIdLocal
                        Provider = providerName
                        ModelName = modelName
                        SystemPromptDigest =
                            match systemPrompt with
                            | Some text -> sha256OfText text
                            | None -> ""
                        SdkVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString()
                    }

                    let! result = writer.BeginConversation(scopeIdLocal, conversation)

                    match result with
                    | Ok() -> return ()
                    | Error err ->
                        logger.Warn
                            $"Conversation substrate BeginConversation failed: {ConversationError.toMessage err}"

                        return ()
        }

    let convAppendTurn
        (scopeIdLocal: string)
        (conversationId: Guid)
        (role: string)
        (content: AIMessageContent)
        (tokensIn: int option)
        (tokensOut: int option)
        : Async<unit> =
        async {
            match conversationStore with
            | None -> return ()
            | Some store ->
                let writer = store :> IConversationWriter
                let id = convId conversationId

                let turn: ConversationTurn = {
                    TurnId = "" // canonical id assigned by the store
                    ConversationId = id
                    SchemaVersion = 1
                    Role = role
                    Content = content
                    Timestamp = DateTime.UtcNow
                    TokensIn = tokensIn
                    TokensOut = tokensOut
                    ContentDigest = digestOfContent content
                }

                let! result = writer.AppendTurn(scopeIdLocal, id, turn)

                match result with
                | Ok() -> return ()
                | Error err ->
                    logger.Warn $"Conversation substrate AppendTurn failed: {ConversationError.toMessage err}"
                    return ()
        }

    let convMarkStatus (scopeIdLocal: string) (conversationId: Guid) (status: ConversationStatus) : Async<unit> = async {
        match conversationStore with
        | None -> return ()
        | Some store ->
            let writer = store :> IConversationWriter
            let id = convId conversationId
            let! result = writer.MarkStatus(scopeIdLocal, id, status)

            match result with
            | Ok() -> return ()
            | Error err ->
                logger.Warn $"Conversation substrate MarkStatus failed: {ConversationError.toMessage err}"
                return ()
    }

    // Scope and user are pre-resolved asynchronously by
    // `ScopeResolutionMiddleware`. If the middleware did not run or scope
    // resolution failed (Error case), fall back to an anonymous user-scoped
    // container. Full error propagation lives in a later phase.
    let scope =
        match ctx.Items.TryGetValue "ToolUp.StorageScope" with
        | true, (:? StorageScope as s) -> s
        | _ ->
            let fallback =
                match ctx.Items.TryGetValue "ToolUp.UserId" with
                | true, (:? string as id) -> id
                | _ -> "anonymous"

            {
                ScopeId = fallback
                Container = $"user-{fallback}"
                Persist = true
            }

    let userId =
        match ctx.Items.TryGetValue "ToolUp.UserId" with
        | true, (:? string as id) -> id
        | _ -> "anonymous"

    let accessContext =
        match ctx.RequestServices.GetService(typeof<AccessContext>) with
        | :? AccessContext as ac -> ac
        | _ ->
            // Fallback: construct from Items. Only used when the AccessContext
            // DI factory didn't run (tests bypassing compose).
            let teamId =
                match ctx.Items.TryGetValue "ToolUp.StorageScope" with
                | true, (:? StorageScope as s) when s.Container.StartsWith "team-" -> Some s.ScopeId
                | _ -> None

            AccessContext.unrestricted (AnonymousSession userId)

    let promptBuilder = config |> Option.bind _.SystemPrompt

    {
        SubmitMessage =
            fun (request: AIMessageRequest) -> async {
                let conversationId = request.ConversationId
                let content = request.Content
                let taskId = Guid.NewGuid()

                let task: AITask = {
                    TaskId = taskId
                    ConversationId = conversationId
                    Prompt = content
                    Status = Queued
                    CreatedAt = DateTime.UtcNow
                    CompletedAt = None
                }

                // Build the system prompt for this request. The builder
                // sees the resolved access context plus the active module
                // reported by the client, so platform, team, and module
                // layers can all contribute per-request.
                //
                // `retrievedSourcesCell` is a fresh-per-request mutable cell
                // that RAG-style builders (e.g. `withRetrieval`) populate
                // with structured `RetrievedSource`s alongside the prompt
                // text they return. We read it after the builder resolves
                // so the assistant `ConversationMessage` can carry the
                // sources to the client without leaking RAG types into
                // `SystemPromptBuilder`'s return type.
                let retrievedSourcesCell: RetrievedSource list ref = ref []

                // Per-request server-side short-circuit channel. A
                // prompt builder (RAG under `StrictlyGrounded` on a
                // retrieval miss) sets this to refuse the turn without
                // a provider call; checked after the builder resolves.
                let shortCircuitCell: string option ref = ref None

                let! systemPromptText =
                    match promptBuilder with
                    | Some builder ->
                        builder {
                            Access = accessContext
                            ActiveModule = request.ActiveModule
                            ActivePage = request.ActivePage
                            ActivePageNarrative = request.ActivePageNarrative
                            ModuleContexts = moduleContexts
                            CurrentMessage = Some request.Content
                            RetrievedSources = retrievedSourcesCell
                            ShortCircuit = shortCircuitCell
                        }
                    | None -> async { return "" }

                let systemPrompt =
                    if systemPromptText = "" then
                        None
                    else
                        Some systemPromptText

                // Create a background context with its own DI scope.
                // The original HttpContext is disposed after this response.
                let bgCtx, bgScope = createBackgroundContext ctx userId

                // Fire the agent loop on a background thread.
                // Results stream to the client via SSE.
                // bgScope is disposed after completion or failure.
                //
                // `userCancelToken` is hoisted above the try so the
                // `with` branch can disambiguate user-initiated cancel
                // (real `cancelToken.IsCancellationRequested`) from
                // every other `OperationCanceledException` source —
                // provider per-call timeout, I/O timeout, framework
                // shutdown. CancellationToken is a struct; `None` is
                // a safe default until the real token is registered.
                let mutable userCancelToken = System.Threading.CancellationToken.None

                let bgWork = async {
                    try
                        // Phase 6h follow-up — Workstream B. Trace the
                        // user/task fingerprint at bgWork entry so a
                        // `TOOLUP_TRACE_CATEGORIES=ai.agent` operator can
                        // correlate this run's userId with the SSE-trace
                        // panel's broadcast destination scopeId — the
                        // exact data needed to spot a userId/scopeId
                        // mismatch without hand-rolling diagnostic logs.
                        Logger.trace
                            logger
                            "ai.agent"
                            $"bgWork start (userId={userId}, taskId={taskId}, conversation={conversationId})"

                        // Phase 6j.D — symmetric ownership gate. In
                        // shared-container modes (`Team` / `MultiTeam`,
                        // `Container = team-{teamId}`) a buggy or hostile
                        // member could target another member's
                        // conversation in the same container; the gate
                        // refuses cross-user appends to a conversation
                        // whose first persisted message recorded a
                        // different owner. Mirrors the gate the beacon
                        // handler applies for fast-path resolutions; same
                        // legacy semantics (empty `CreatedBy` ⇒ accept).
                        //
                        // Load happens BEFORE the InProgress SSE so a
                        // refused turn doesn't briefly flash as in-flight
                        // on the client; the agent loop never starts.
                        let! gateExisting =
                            loadConversation logger storage scope.Container conversationId
                            |> withTimeout "Conversation load (ownership gate)" ChatPathIoTimeoutMs

                        // Inline copy of `FastPathBeaconHandler.checkOwnership`
                        // — that module compiles AFTER this one in
                        // `ToolUp.AI.Server.fsproj`, so the symbol isn't
                        // reachable here. Both copies must move together
                        // when the gate semantics change.
                        let ownershipResult =
                            match gateExisting with
                            | [] -> Ok()
                            | first :: _ ->
                                let owner = first.CreatedBy

                                if System.String.IsNullOrEmpty owner then Ok()
                                elif owner = userId then Ok()
                                else Error owner

                        match ownershipResult with
                        | Error ownerOfRecord ->
                            logger.Warn
                                $"AI SubmitMessage rejected: caller '{userId}' is not the owner of conversation {conversationId} (owner '{ownerOfRecord}', scope '{scope.ScopeId}')."

                            // Emit BeaconRejected via IAuditLog when
                            // available. The audit-trail row exists
                            // independent of which surface fired the
                            // gate — operators query the trail by
                            // `BeaconRejected` regardless of whether
                            // the rejection came from the beacon POST
                            // or the SubmitMessage path; `Surface`
                            // disambiguates.
                            match auditLogOpt with
                            | Some auditLog ->
                                let payload: BeaconRejectedPayload = {
                                    ConversationId = conversationId
                                    Caller = userId
                                    Owner = ownerOfRecord
                                    Surface = "submit"
                                }

                                try
                                    do! auditLog.Record(scope.ScopeId, BeaconRejected payload)
                                with ex ->
                                    logger.Warn
                                        $"BeaconRejected audit write failed (submit; conversation {conversationId}): {ex.Message}"
                            | None -> ()

                            // Surface the refusal through the existing
                            // SSE failure channel — same shape as a
                            // provider resolution failure or an
                            // agent-loop exception. The Fable.Remoting
                            // SubmitMessage call already returned the
                            // queued task; the failure event flips it
                            // to AITaskFailed client-side.
                            sendEvent
                                sseManager
                                userId
                                (TaskStatusChanged(
                                    taskId,
                                    AITaskFailed
                                        "Conversation does not belong to the current user — refusing to append."
                                ))

                            bgScope.Dispose()
                            return ()
                        | Ok() -> ()

                        sendEvent sseManager userId (TaskStatusChanged(taskId, InProgress))

                        // Resolve the provider once per user message. The
                        // agent loop may make several SendMessage calls
                        // across tool-use turns; all share this provider
                        // so the user's active configuration can't shift
                        // mid-loop. A user switching providers takes effect
                        // on their next message.
                        //
                        // Per-conversation default: stored override
                        // recorded by the module-page picker. The UI
                        // also sends it as `request.OverrideProviderLabel`
                        // on each submit, but reading the persisted
                        // value here is the defensive fallback for
                        // tabs that haven't loaded the dropdown yet
                        // and is the source of truth on reload.
                        //
                        // Resolution priority: request override (one-off,
                        // explicit per-message), then conversation
                        // override (per-conversation default), then the
                        // factory's active-label policy.
                        let! storedMeta =
                            loadConversationMeta logger storage scope.Container conversationId
                            |> withTimeout "Conversation metadata load" ChatPathIoTimeoutMs

                        let resolvedOverrideLabel =
                            match request.OverrideProviderLabel with
                            | Some _ -> request.OverrideProviderLabel
                            | None -> storedMeta.OverrideProviderLabel

                        let! providerResult =
                            match resolvedOverrideLabel with
                            | Some label -> providerFactory.TryResolveByLabel(accessContext, label)
                            | None -> providerFactory.Resolve accessContext
                            |> withTimeout "AI provider resolution" ChatPathIoTimeoutMs

                        let provider =
                            match providerResult with
                            | Ok p ->
                                // Log provider/model for ops traceability.
                                // No key material is ever included.
                                logger.Debug
                                    $"AI provider resolved for user {accessContext.UserId} (conversation={conversationId}, override={resolvedOverrideLabel}): {p.Capabilities.ProviderName}/{p.Capabilities.Model}"

                                p
                            | Error err ->
                                logger.Warn
                                    $"AI provider resolution failed for user {accessContext.UserId} (conversation={conversationId}, override={resolvedOverrideLabel}): {ProviderResolutionError.toMessage err}"
                                // Catastrophic configuration failure.
                                // Surfaces via the outer try/with below
                                // as AITaskFailed; keeps a single error
                                // routing path for the handler.
                                failwith (ProviderResolutionError.toMessage err)

                        // Round-trippable provider history — includes every
                        // tool_use / tool_result from prior turns. Falls back
                        // to rebuilding from the UI-facing conversation
                        // blob when the history sibling is absent (e.g.
                        // conversations created before this feature
                        // landed). The rebuild path carries no tool context
                        // forward, which is the pre-fix behaviour — safe
                        // but degraded for one turn until the new history
                        // blob is written.
                        let! providerHistory =
                            loadProviderHistory logger storage scope.Container conversationId
                            |> withTimeout "Conversation history load" ChatPathIoTimeoutMs

                        let! providerMessages =
                            if List.isEmpty providerHistory then
                                async {
                                    let! existing =
                                        loadConversation logger storage scope.Container conversationId
                                        |> withTimeout "Conversation load" ChatPathIoTimeoutMs

                                    return existing |> List.map toProviderMessage
                                }
                            else
                                async.Return providerHistory

                        let userMsg: AIProviderMessage = {
                            Role = "user"
                            Content = content
                            ToolCalls = []
                            ToolResults = []
                            Parts = []
                        }

                        // Phase 53 substrate hook — begin the
                        // conversation record on first turn
                        // (idempotent) and append the user turn.
                        // Both no-op when ConversationStore =
                        // NoConversationStore (the default).
                        do!
                            convBeginIfNeeded
                                conversationId
                                userId
                                scope.ScopeId
                                provider.Capabilities.ProviderName
                                provider.Capabilities.Model
                                systemPrompt

                        do!
                            convAppendTurn
                                scope.ScopeId
                                conversationId
                                "user"
                                {
                                    Role = "user"
                                    Content = content
                                    ToolCalls = []
                                    ToolResults = []
                                    Parts = []
                                }
                                None
                                None

                        // Bound the replayed history. Sending the entire
                        // prior message list verbatim grows per-turn cost
                        // without limit and eventually hard-fails with an
                        // opaque provider context-overflow error. Keep the
                        // most recent N; log when older turns are dropped.
                        let maxHistory =
                            SystemPromptBuilder.AIAssistantServerConfig.effectiveMaxHistory config

                        let trimmedHistory =
                            let total = List.length providerMessages

                            if total > maxHistory then
                                let dropped = total - maxHistory

                                logger.Warn(
                                    sprintf
                                        "AI conversation %O: provider history is %d messages; replaying only the most recent %d (dropping %d oldest). Tune via AIAssistantServerConfig.MaxHistoryMessages."
                                        conversationId
                                        total
                                        maxHistory
                                        dropped
                                )

                                providerMessages |> List.skip dropped
                            else
                                providerMessages

                        let allProviderMessages = trimmedHistory @ [ userMsg ]

                        // Phase 6h: register a CancellationTokenSource for
                        // this task. The cancel POST handler flips it; the
                        // agent loop checks at turn boundaries and bails
                        // cleanly via OperationCanceledException. We always
                        // unregister in the surrounding finally to free
                        // the entry whether cancellation fired or not.
                        let cancelToken = cancellationRegistry.Register(taskId)
                        userCancelToken <- cancelToken

                        let! finalMessages =
                            match shortCircuitCell.Value with
                            | Some refusal ->
                                // Server-side grounding guard: a prompt
                                // builder (RAG `StrictlyGrounded` on a
                                // retrieval miss) demanded refusal
                                // WITHOUT a provider call. Synthesize the
                                // assistant turn so the existing
                                // persistence / SSE / history path runs
                                // verbatim — no model call, no duplicated
                                // finalisation.
                                async {
                                    return
                                        allProviderMessages
                                        @ [
                                            {
                                                Role = "assistant"
                                                Content = refusal
                                                ToolCalls = []
                                                ToolResults = []
                                                Parts = []
                                            }
                                        ]
                                }
                            | None ->
                                runAgentLoop
                                    provider
                                    registry
                                    dispatchRegistry
                                    bgCtx
                                    taskId
                                    conversationId
                                    request.Surface
                                    request.ActiveModule
                                    request.ActivePage
                                    cancelToken
                                    allProviderMessages
                                    systemPrompt
                                    (fun evt -> sendEvent sseManager userId (evt))

                        // Persist the full round-trippable history first —
                        // this is what the next turn will load to maintain
                        // tool_use / tool_result pairing with the provider.
                        do! saveProviderHistory storage scope.Container conversationId finalMessages

                        // Persist the UI-facing conversation blob: user
                        // prompt + final assistant text (intermediate
                        // tool_use / tool_result steps are not rendered in
                        // the chat history, though they remain inspectable
                        // via the history blob for audit or future UI).
                        let! existingUiMessages = loadConversation logger storage scope.Container conversationId
                        let userConvMsg = toConversationMessage conversationId User content [] [] [] userId

                        let modelEmittedContent =
                            finalMessages
                            |> List.tryFindBack (fun m -> m.Role = "assistant")
                            |> Option.map _.Content
                            |> Option.defaultValue ""

                        // Phase 6q follow-up A — post-stream citation
                        // normalisation. Resolves `ICitationNormaliser`
                        // from DI; when registered (RAG composed with
                        // a non-Off policy) and the turn produced both
                        // retrieved sources AND assistant text, run the
                        // normaliser, emit one audit event per
                        // `CitationEvent`, and persist the rewritten
                        // text. Non-RAG deployments resolve `null` →
                        // byte-for-byte pre-Phase-6q behaviour.
                        let citationNormaliserOpt =
                            match bgCtx.RequestServices.GetService(typeof<ICitationNormaliser>) with
                            | :? ICitationNormaliser as n -> Some n
                            | _ -> None

                        let providerName = provider.Capabilities.ProviderName
                        let providerModel = provider.Capabilities.Model

                        let citationEventStore =
                            match bgCtx.RequestServices.GetService(typeof<IEventStore>) with
                            | :? IEventStore as store -> Some store
                            | _ -> None

                        let! finalContent =
                            match citationNormaliserOpt with
                            | Some normaliser when
                                not (List.isEmpty retrievedSourcesCell.Value)
                                && not (System.String.IsNullOrEmpty modelEmittedContent)
                                  ->
                                  async {
                                      let result =
                                          normaliser.Normalise(
                                              retrievedSourcesCell.Value,
                                              modelEmittedContent,
                                              providerName,
                                              providerModel
                                          )

                                      // Emit one audit event per recognised
                                      // citation event so operators can correlate
                                      // model drift with prompt changes. Best-
                                      // effort: a wedged event store must never
                                      // crash the conversation. Same shape as the
                                      // denial-audit emission in AIAgentEngine.fs.
                                      match citationEventStore with
                                      | Some store ->
                                          for evt in result.Events do
                                              let sourceModule, eventType, sourceIndexJson =
                                                  match evt.Action with
                                                  | NormalisedToCanonical idx ->
                                                      "_platform.ai.citation.normalised", "Normalised", Some idx
                                                  | StrippedPhantom ->
                                                      "_platform.ai.citation.stripped", "Stripped", None
                                                  | UnverifiedTagged ->
                                                      "_platform.ai.citation.stripped", "UnverifiedTagged", None

                                              let payload = {|
                                                  TaskId = taskId
                                                  ConversationId = conversationId
                                                  ProviderName = providerName
                                                  ProviderModel = providerModel
                                                  Variant = evt.Variant
                                                  Digit = evt.Digit
                                                  Action = eventType
                                                  SourceIndex = sourceIndexJson
                                              |}

                                              let evtRecord: ModuleEvent = {
                                                  Id = Guid.NewGuid()
                                                  OccurredAt = DateTime.UtcNow
                                                  ScopeId = scope.ScopeId
                                                  SourceModule = sourceModule
                                                  EventType = eventType
                                                  Payload = toJson payload
                                              }

                                              try
                                                  do! store.Write evtRecord
                                              with ex ->
                                                  logger.Warn
                                                      $"AI citation-audit write failed (taskId={taskId}, action={eventType}): {ex.Message}. Record dropped; conversation unaffected."
                                      | None -> ()

                                      return result.Text
                                  }
                            | _ -> async.Return modelEmittedContent

                        let assistantConvMsg =
                            toConversationMessage
                                conversationId
                                AIAssistant
                                finalContent
                                []
                                retrievedSourcesCell.Value
                                []
                                ""

                        let updatedConversation = existingUiMessages @ [ userConvMsg; assistantConvMsg ]

                        do! saveConversation storage scope.Container conversationId updatedConversation

                        // Phase 53 substrate hook — append the
                        // assistant turn + mark the conversation
                        // Completed. No-op when ConversationStore =
                        // NoConversationStore.
                        do!
                            convAppendTurn
                                scope.ScopeId
                                conversationId
                                "assistant"
                                {
                                    Role = "assistant"
                                    Content = finalContent
                                    ToolCalls =
                                        finalMessages
                                        |> List.tryFindBack (fun m -> m.Role = "assistant")
                                        |> Option.map _.ToolCalls
                                        |> Option.defaultValue []
                                    ToolResults = []
                                    Parts = []
                                }
                                None
                                None

                        do! convMarkStatus scope.ScopeId conversationId ConversationStatus.Completed

                        sendEvent sseManager userId (TaskStatusChanged(taskId, AITaskCompleted))

                        // Phase 6h: free the cancellation entry on
                        // natural completion. The cancel POST handler
                        // already removes the entry on cancellation,
                        // so this is the no-cancel path.
                        cancellationRegistry.Unregister(taskId)
                        bgScope.Dispose()
                    with
                    | :? System.OperationCanceledException when userCancelToken.IsCancellationRequested ->
                        // Phase 6h: user-initiated cancel. The agent
                        // loop already emitted StreamCancelled; the
                        // task ends gracefully (not as a failure).
                        //
                        // Phase 6h follow-up: gate this branch on the
                        // user-cancel token actually being flipped. Any
                        // OTHER OperationCanceledException — provider
                        // per-call timeout (RetryPolicy.Timeout CTS),
                        // I/O timeout, etc. — must fall through to the
                        // generic failure branch and surface as
                        // AITaskFailed. Without this guard, F# async's
                        // habit of propagating OCE through `with`
                        // clauses meant timeout-induced cancellations
                        // were being silently treated as user cancels:
                        // no error event, no chat-inline ⚠️, just a
                        // 60 s watchdog timeout.
                        convMarkStatus scope.ScopeId conversationId ConversationStatus.Cancelled
                        |> Async.Start

                        sendEvent sseManager userId (TaskStatusChanged(taskId, AITaskCompleted))
                        cancellationRegistry.Unregister(taskId)
                        bgScope.Dispose()
                    | ex ->
                        convMarkStatus scope.ScopeId conversationId (ConversationStatus.Errored ex.Message)
                        |> Async.Start

                        sendEvent sseManager userId (TaskStatusChanged(taskId, AITaskFailed ex.Message))
                        cancellationRegistry.Unregister(taskId)
                        bgScope.Dispose()
                }

                Async.Start bgWork

                return task
            }

        GetConversation =
            fun conversationId -> async { return! loadConversation logger storage scope.Container conversationId }

        ListConversations =
            fun () -> async {
                let! blobs = storage.List(scope.Container, "ai-conversations/")

                let conversationIds =
                    blobs
                    |> List.choose (fun blobName ->
                        // Sibling blobs (`{id}.history.json`, `{id}.meta.json`)
                        // strip-and-trim to `"{id}.history"` / `"{id}.meta"`
                        // which `Guid.TryParse` rejects, so they fall through.
                        let fileName = blobName.Replace("ai-conversations/", "").Replace(".json", "")

                        match Guid.TryParse(fileName) with
                        | true, id -> Some id
                        | _ -> None)

                let! conversations =
                    conversationIds
                    |> List.map (fun id -> async {
                        let! meta = loadConversationMeta logger storage scope.Container id

                        return {
                            Id = id
                            Title = None
                            CreatedAt = DateTime.MinValue
                            UpdatedAt = DateTime.MinValue
                            MessageCount = 0
                            OverrideProviderLabel = meta.OverrideProviderLabel
                        }
                    })
                    |> Async.Parallel

                return conversations |> Array.toList
            }

        GetAvailableTools = fun () -> async { return registry.GetAll() |> List.map _.Definition }

        GetTaskStatus =
            fun _taskId -> async {
                // Task status is ephemeral — delivered via SSE events.
                // This endpoint can be used for polling fallback.
                return None
            }

        DeleteConversation =
            fun conversationId -> async {
                // Delete EVERY sibling blob. Deleting only `.json` left
                // `.history.json` (the full provider round-trip incl.
                // tool inputs/outputs) and `.meta.json` behind — a
                // right-to-erasure / privacy gap. `IBlobStorage.Delete`
                // is idempotent (missing blob ⇒ Ok), so deleting all
                // three unconditionally is safe; surface the first
                // failure so an incomplete erasure is retryable rather
                // than silently partial.
                let! r1 = storage.Delete(scope.Container, conversationBlobName conversationId)
                let! r2 = storage.Delete(scope.Container, providerHistoryBlobName conversationId)
                let! r3 = storage.Delete(scope.Container, conversationMetaBlobName conversationId)

                return
                    [ r1; r2; r3 ]
                    |> List.tryPick (function
                        | Error e -> Some(Error e)
                        | Ok() -> None)
                    |> Option.defaultValue (Ok())
            }

        SetConversationOverride =
            fun (conversationId, label) -> async {
                try
                    do! saveConversationMeta storage scope.Container conversationId { OverrideProviderLabel = label }

                    return Ok()
                with ex ->
                    return Error ex.Message
            }
    }