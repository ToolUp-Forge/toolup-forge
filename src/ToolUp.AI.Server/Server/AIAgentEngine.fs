module ToolUp.AI.AIAgentEngine

open System
open System.Diagnostics
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Metrics
open ToolUp.AI
open ToolUp.AI.AIToolRegistry

/// Classification of tool-invocation failures. Converts the previously
/// free-form exception messages into a discriminated shape so the agent
/// loop, the SSE event stream, and future audit/dead-letter handling can
/// all reason about errors without string-matching.
type ToolInvocationError =
    /// Model requested a tool the registry doesn't know about.
    | UnknownTool of toolName: string
    /// Tool threw synchronously or returned Error from its Execute body.
    | ToolThrew of toolName: string * message: string
    /// Tool failed to parse its JSON arguments.
    | InvalidArguments of toolName: string * detail: string
    /// Server-side allowlist denied this ClientResident invocation
    /// before dispatch. Distinct from InvalidArguments so the model is
    /// told the action is not permitted rather than "fix your args".
    | Denied of toolName: string * reason: string

module ToolInvocationError =
    /// Human-readable rendering suitable to pass back to the AI model as a
    /// tool result — the model uses this to decide whether to retry with
    /// different args, apologise to the user, or continue.
    let toToolResultContent (err: ToolInvocationError) =
        match err with
        | UnknownTool name -> $"Unknown tool: {name}"
        | ToolThrew(name, msg) -> $"Tool '{name}' failed: {msg}"
        | InvalidArguments(name, detail) -> $"Tool '{name}' received invalid arguments: {detail}"
        | Denied(name, reason) -> $"Tool '{name}' was denied: {reason}"

/// Maximum number of outer retries applied at the agent-loop level, on top
/// of the provider's own RetryPolicy budget. Kicks in only for
/// `RetriesExhausted` — other catastrophic classes (PermanentClient,
/// MalformedResponse, StreamingAborted) propagate immediately. Keeps a lid
/// on runaway retries during extended outages while still offering one
/// more attempt after the provider's backoff chain has completed.
let private AgentLoopOuterRetries = 1

/// Delay between agent-loop-level retries. Longer than the provider's
/// per-call backoff — by the time we get here the provider has already
/// paused ~InitialBackoff * (2^0 + 2^1 + ...) across its attempts.
let private AgentLoopOuterRetryDelayMs = 2000

/// Hard ceiling on agent-loop turns within a single `SubmitMessage` call.
/// A well-behaved model completes the user's request in a handful of tool
/// calls; crossing this threshold indicates a looping model, an oversized
/// tool result the model can't digest, or an unbounded plan-and-execute
/// strategy that would never terminate. When exceeded the loop bails with
/// a user-visible `AITaskFailed`-style error rather than hanging the
/// conversation and burning provider spend. Increase only if a legitimate
/// multi-step workflow needs it; a deployment that genuinely needs 20+
/// turns should refactor into smaller, composable tools.
let private AgentLoopMaxTurns = 15

/// Phrases in assistant text that indicate the model can't make progress
/// without human input. Matched case-insensitively as substrings. When one
/// fires AND the model is still trying to call tools, the agent loop
/// stops early — retrying is unlikely to help, and the user is better
/// served by a clear message naming the block than by burning quota on
/// speculative tool calls the model itself has flagged as uncertain.
///
/// Deliberately conservative. Each phrase is something a fluent model
/// would only say if it genuinely couldn't proceed. Phrases like "I think"
/// or "not sure" on their own are intentionally excluded because they
/// occur naturally in exploratory reasoning. Extend cautiously.
let private confusionMarkers = [
    "i don't know what to"
    "i don't know how to"
    "i cannot determine"
    "i can't determine"
    "i'm unable to"
    "i am unable to"
    "i don't have enough information"
    "i don't have sufficient"
    "unable to proceed"
    "cannot proceed"
    "i cannot find"
    "i can't find"
    "no way to"
    "without more information"
    "please provide more"
    "i need more information"
    "need additional information"
]

/// Maximum consecutive turns where every tool call errored before the
/// agent loop gives up. A single errored turn is normal recovery; two in
/// a row strongly suggests the model is attempting the same flawed path
/// repeatedly rather than adapting to the error.
let private MaxConsecutiveErroredTurns = 2

/// Scan the model's response text for explicit "I can't proceed" markers.
/// Returns `Some marker` when a confusion phrase fires, `None` otherwise.
let private detectConfusion (content: string) : string option =
    if System.String.IsNullOrWhiteSpace content then
        None
    else
        let lowered = content.ToLowerInvariant()

        confusionMarkers |> List.tryFind (fun marker -> lowered.Contains marker)

/// Identify tool results that our own invocation-error rendering produced
/// (see `ToolInvocationError.toToolResultContent`). Matching by prefix
/// keeps this in lockstep with that function; if a new invocation-error
/// case is added, update both.
let private isErrorToolResult (content: string) =
    if isNull content then
        true
    elif content.StartsWith "Unknown tool:" then
        true
    elif content.StartsWith "Tool '" then
        content.Contains "' failed:"
        || content.Contains "' received invalid arguments:"
        || content.Contains "' was denied:"
    else
        false

/// Emit a synthetic final assistant message carrying a stop-reason
/// explanation, then signal end-of-turn through the SSE stream so the
/// client commits it to the chat. Used when the agent-loop detects it
/// can't make progress and bails early — gives the user a clear,
/// visible message in the chat rather than a silent hang or an opaque
/// exception bubbled up as `AITaskFailed`.
let private emitEarlyStopMessage (onEvent: AIStreamEvent -> unit) (conversationId: System.Guid) (reason: string) =
    let body =
        $"[Agent stopped early] {reason}\n\n"
        + "Retrying the same request won't help — please clarify your instructions, "
        + "check the input data, or simplify the task and try again."

    onEvent (MessageDelta(conversationId, body))
    onEvent (MessageComplete(conversationId, System.Guid.NewGuid()))

/// Classification of an `AIProviderError` for the agent loop's handling.
type private ProviderErrorDisposition =
    /// Non-recoverable — surface immediately to the caller (AITaskFailed).
    | Fatal
    /// Budget exhaustion at the provider level — the agent loop may make
    /// one more attempt after a brief pause before giving up.
    | OuterRetryable

let private classifyForAgentLoop (err: AIProviderError) =
    match err with
    | RetriesExhausted _ -> OuterRetryable
    | PermanentClient _
    | MalformedResponse _
    | StreamingAborted _
    | TransientNetwork _
    | TransientServer _
    | UnsupportedCapability _ -> Fatal

/// Run the agent loop: send messages to AI, execute tool calls, repeat until done.
/// This runs entirely server-side, calling module functions in-process
/// with no HTTP round-trips. The onEvent callback streams progress to the client via SSE.
///
/// Tool errors are classified into `ToolInvocationError` values, rendered as
/// strings back to the model (so the model can react), and surfaced on the
/// SSE stream via `ToolCallCompleted`. Provider errors are classified into
/// `AIProviderError` by the provider's Result channel; this loop applies an
/// outer retry for `RetriesExhausted` (the provider's own budget having
/// lapsed) then propagates any remaining error to the caller, which
/// surfaces it at the `SubmitMessage` boundary as a failed `AITask`.
/// Phase 6g.A: server-side timeout for client-resident tool calls.
/// The browser is expected to run the tool body and POST the result
/// to `/api/ai/tool-result` within this window; otherwise the agent
/// loop aborts the call via `ClientToolDispatchRegistry.TryAbort`
/// and the recovery path surfaces a `ToolThrew` to the model. The
/// browser should set its own watchdog (default 30 s) inside this
/// envelope so the user-facing failure mode is "client gave up"
/// rather than "server timed out".
[<Literal>]
let private ClientResidentToolTimeoutMs = 90_000

/// Phase 6h follow-up: cap on `IEventStore.Write` for latency
/// telemetry. The agent loop awaits this on the response path; a
/// slow / wedged event store backend would otherwise stall the
/// conversation after the LLM has already finished.
[<Literal>]
let private LatencyWriteTimeoutMs = 5_000

/// Phase 6h follow-up: per-call cap on `provider.SendMessage`.
/// `RetryPolicy.defaults.Timeout` is `None` (provider default —
/// `HttpClient.Timeout` = 5 min instance-wide), which is far longer
/// than the client-side 60 s watchdog. A hung Anthropic call would
/// otherwise never surface as a chat-inline `AITaskFailed`; the user
/// would see only the generic 60 s "no response" watchdog with no
/// hint that the LLM call itself was the culprit. 50 s gives every
/// well-behaved call room to complete (typical streaming response
/// finishes in 5–30 s) while letting genuinely hung calls fail
/// before the watchdog so the chat shows the underlying error.
[<Literal>]
let private ProviderSendTimeoutMs = 50_000

// ─── Phase 6i.A — per-turn latency telemetry ─────────────────────
//
// One `AILatencyRecord` per agent-loop iteration is written through
// `IEventStore` under `AILatencyRecord.SourceModule`. Always-on (no
// config flag): one event-store write per turn is comfortably below
// the noise floor of a multi-second LLM call. The dev endpoint
// `/dev/ai-latency` reads these back via `ReadBySource` (Phase 9f
// `_by-source` index resolves in O(matches)) and rolls up p50/p95/p99
// over a 60-minute window.
//
// Phase 9e wire-up (shipped 2026-05-23, Tidy-Up #1): the same per-turn
// observations are emitted through `IMetricsSink` as `toolup.ai.turn.duration.ms`,
// `toolup.ai.ttft.ms`, and `toolup.ai.cached_prompt_ratio` — see the
// `metricsSinkOpt` branch inside `emitLatency` below. Registrations live
// in `AILatencyMetrics.fs` and are folded into `ServerApp.MetricRegistrations`
// by `AIServerApp.create`.

/// Latency-record JSON serialiser. Mirrors `FastPathBeaconHandler` —
/// `FableConverters` round-trips F# DUs / `option` / records
/// losslessly so `/dev/ai-latency` reads back the same shape that
/// `IEventStore.Write` persisted.
let private latencyJsonOptions = FableConverters.create ()

/// Resolve the caller's storage scope from the request. Mirrors the
/// fallback shape used by `FastPathBeaconHandler` so latency records
/// land in the same scope the conversation-blob writes use.
let private resolveLatencyScope (ctx: HttpContext) : StorageScope =
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

let runAgentLoop
    (provider: IAIProvider)
    (registry: AIToolRegistry)
    (dispatchRegistry: ClientToolDispatch.ClientToolDispatchRegistry)
    (ctx: HttpContext)
    (taskId: Guid)
    (conversationId: Guid)
    (surface: AISurface)
    (activeModule: string option)
    (activePage: string option)
    (cancelToken: System.Threading.CancellationToken)
    (messages: AIProviderMessage list)
    (systemPrompt: string option)
    (onEvent: AIStreamEvent -> unit)
    : Async<AIProviderMessage list> =
    async {
        // Phase 6g.A: filter the per-turn tool list by the request's
        // surface. Tools declared as `SidePanelOnly` / `FullPageOnly`
        // are excluded when the chat is from the other surface; `Both`
        // (the default for existing tools) always passes through.
        let tools =
            registry.GetAll()
            |> List.filter (fun t -> isToolVisibleOnSurface surface t.Definition)
            |> List.map _.ProviderDef

        // Observational logging. DI-registered `ILogger` when available
        // (ConsoleLogger by default); silent no-op in tests that bypass
        // `compose`. Messages must not leak prompt content or API keys.
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

        let providerName = provider.Capabilities.ProviderName
        let providerModel = provider.Capabilities.Model

        // Capability gate: tools present but the provider can't tool-use
        // means every tool is silently inert (the model never emits a
        // tool call → client-resident dispatch and fast-path never fire)
        // while the request still "succeeds". Surface it loudly instead.
        if not (List.isEmpty tools) && not provider.Capabilities.ToolUse then
            logger.Warn(
                sprintf
                    "AI provider '%s' (model '%s') reports ToolUse = false but %d tool(s) are registered this turn. Tool calls will never fire (client-resident dispatch and fast-path inert); the model answers as if no tools exist. Configure a tool-capable provider/model if tool use is expected."
                    providerName
                    providerModel
                    (List.length tools)
            )

        let mutable turn = 0

        let mutable currentMessages = messages
        let mutable keepGoing = true

        // Phase 6i.A: per-turn latency telemetry. Resolved once at the
        // top of the loop — neither the event store nor the scope
        // changes mid-conversation. `eventStoreOpt` is `None` in tests
        // that bypass `compose`; emission silently no-ops in that case.
        let eventStoreOpt =
            match ctx.RequestServices.GetService(typeof<IEventStore>) with
            | :? IEventStore as s -> Some s
            | _ -> None

        // Phase 9e wire-up: companion-contributed `IMetricsSink`.
        // Resolved once at the top of the loop (peer to `eventStoreOpt`).
        // `IMetricsSink` is never `null` in DI when `compose` runs (the
        // `NoOpMetricsSink` is registered when `MetricsEndpoint = Disabled`)
        // — fall-through to `None` only covers tests that bypass `compose`.
        // The three `toolup.ai.*` metrics emitted by `emitLatency` are
        // registered against `ServerApp.MetricRegistrations` by
        // `AIServerApp.create` so `PrometheusMetricsSink` allocates their
        // series at compose time and the emissions flow rather than drop.
        let metricsSinkOpt =
            match ctx.RequestServices.GetService(typeof<IMetricsSink>) with
            | :? IMetricsSink as s -> Some s
            | _ -> None

        // G12 allowlist: optional, companion-supplied authorization seam
        // for ClientResident tool invocations. `None` when no companion
        // registered one — the consult below then no-ops to `Allow`, so a
        // deployment without a client-resident-tool companion is
        // behaviourally unchanged (GP 13).
        let authorizerOpt =
            match ctx.RequestServices.GetService(typeof<IClientToolAuthorizer>) with
            | :? IClientToolAuthorizer as a -> Some a
            | _ -> None

        let latencyScope = resolveLatencyScope ctx

        let emitLatency
            (turnNumber: int)
            (response: AIProviderResponse option)
            (ttftMs: float option)
            (toolTimings: ToolCallTiming list)
            (turnDurationMs: float)
            =
            async {
                match eventStoreOpt with
                | None -> return ()
                | Some store ->
                    let stopReason =
                        match response with
                        | Some r -> r.StopReason
                        | None -> ""

                    // Phase 6i.B: pull provider-reported token counts off
                    // `response.Usage`. None when the provider didn't
                    // surface a usage block (turn errored before reading
                    // the response, or the provider declares
                    // SupportsPromptCaching = false and reports nothing).
                    let usage = response |> Option.bind _.Usage

                    let payload: AILatencyRecord = {
                        TaskId = taskId
                        ConversationId = conversationId
                        TurnNumber = turnNumber
                        ProviderName = providerName
                        ProviderModel = providerModel
                        TtftMs = ttftMs
                        TurnDurationMs = turnDurationMs
                        ToolCalls = toolTimings
                        StopReason = stopReason
                        PromptTokens = usage |> Option.map _.PromptTokens
                        CachedPromptTokens = usage |> Option.map _.CachedPromptTokens
                        OutputTokens = usage |> Option.map _.OutputTokens
                        CacheCreationTokens = usage |> Option.bind _.CacheCreationTokens
                    }

                    let evt: ModuleEvent = {
                        Id = Guid.NewGuid()
                        OccurredAt = DateTime.UtcNow
                        ScopeId = latencyScope.ScopeId
                        SourceModule = AILatencyRecord.SourceModule
                        EventType = AILatencyRecord.EventType
                        Payload = JsonSerializer.Serialize(payload, latencyJsonOptions)
                    }

                    // Phase 6h follow-up: bound the event-store write so a
                    // slow / wedged backend can't stall the agent loop after
                    // the LLM finished. Latency telemetry must never crash
                    // OR block an in-progress conversation. Mirrors the
                    // Task.WhenAny envelope used for client-resident tool
                    // calls above.
                    try
                        let writeTask = store.Write evt |> Async.StartAsTask
                        let timeoutTask = Task.Delay(LatencyWriteTimeoutMs)

                        let! winner = Task.WhenAny(writeTask :> Task, timeoutTask) |> Async.AwaitTask

                        if winner = (writeTask :> Task) then
                            do! writeTask |> Async.AwaitTask
                        else
                            logger.Warn
                                $"AI latency event-store write timed out after {LatencyWriteTimeoutMs / 1000}s; dropping record (taskId={taskId}, turn={turnNumber})"
                    with ex ->
                        // Telemetry must never crash / block the loop, but a
                        // wedged event store should not be invisible either —
                        // log at Warn so the operator can correlate missing
                        // latency records with a backend problem.
                        logger.Warn
                            $"AI latency event-store write failed (taskId={taskId}, turn={turnNumber}): {ex.Message}. Record dropped; conversation unaffected."

                    // Phase 9e wire-up: emit the same per-turn observations
                    // through `IMetricsSink` so operator dashboards can
                    // chart p50 / p95 / p99 alongside the existing
                    // `/dev/ai-latency` event-store rollup. Hot-path
                    // emission, fire-and-forget — IMetricsSink is sync
                    // by design (documented exemption); a failing sink
                    // is swallowed inside the impl and never propagates
                    // through `Record`. Per the metric-tag allowlist,
                    // only `provider` + `model` reach the sink.
                    match metricsSinkOpt with
                    | None -> ()
                    | Some sink ->
                        try
                            let tags = Map.ofList [ "provider", providerName; "model", providerModel ]

                            sink.Record(AILatencyMetrics.TurnDurationMs, turnDurationMs, tags)

                            match ttftMs with
                            | Some t -> sink.Record(AILatencyMetrics.TtftMs, t, tags)
                            | None -> ()

                            match payload.PromptTokens, payload.CachedPromptTokens with
                            | Some prompt, Some cached when prompt > 0 ->
                                let ratio = float cached / float prompt

                                sink.Record(AILatencyMetrics.CachedPromptRatio, ratio, tags)
                            | _ -> ()
                        with ex ->
                            // Mirror the event-store posture — metrics
                            // never crash the loop. PrometheusMetricsSink
                            // already swallows exceptions inside Record /
                            // Increment, so the catch here is a belt-and-
                            // braces guard against companion sinks that
                            // forget the rule. Log at Warn so a defective
                            // sink is visible in the operator log.
                            logger.Warn
                                $"AI latency IMetricsSink.Record failed (taskId={taskId}, turn={turnNumber}): {ex.Message}. Metric dropped; conversation unaffected."
            }

        // G12 allowlist: server-originated audit of a ClientResident
        // tool invocation refused by the authorization seam. Mirrors
        // `emitLatency`'s event-store + bounded-write shape so denials
        // are observable in the same `IEventStore` sink as the existing
        // `_platform.ui.decode_error` audit. Never crashes / blocks the
        // loop — a wedged backend logs at Warn and the denial still
        // returns to the model.
        let writeDenialAudit (toolName: string) (reason: string) = async {
            match eventStoreOpt with
            | None -> return ()
            | Some store ->
                let payload = {|
                    ToolName = toolName
                    Reason = reason
                    ActiveModule = activeModule
                    ActivePage = activePage
                    TaskId = taskId
                    ConversationId = conversationId
                |}

                let evt: ModuleEvent = {
                    Id = Guid.NewGuid()
                    OccurredAt = DateTime.UtcNow
                    ScopeId = latencyScope.ScopeId
                    SourceModule = "_platform.ai.tool_allowlist_denial"
                    EventType = "ToolAllowlistDenied"
                    Payload = JsonSerializer.Serialize(payload, latencyJsonOptions)
                }

                try
                    let writeTask = store.Write evt |> Async.StartAsTask
                    let timeoutTask = Task.Delay(LatencyWriteTimeoutMs)

                    let! winner = Task.WhenAny(writeTask :> Task, timeoutTask) |> Async.AwaitTask

                    if winner = (writeTask :> Task) then
                        do! writeTask |> Async.AwaitTask
                    else
                        logger.Warn
                            $"AI tool-allowlist denial audit write timed out after {LatencyWriteTimeoutMs / 1000}s; dropping record (taskId={taskId})"
                with ex ->
                    logger.Warn
                        $"AI tool-allowlist denial audit write failed (taskId={taskId}): {ex.Message}. Record dropped; conversation unaffected."
        }

        // Counts turns where every tool call errored. Reset to 0 on any
        // turn that contained at least one successful tool call or
        // transitioned into the terminal branch. When it reaches
        // `MaxConsecutiveErroredTurns` the loop bails early — the model
        // is clearly flailing rather than adapting.
        let mutable consecutiveErroredTurns = 0

        // Inner helper: perform one SendMessage call with agent-loop-level
        // outer retry applied on RetriesExhausted. Raises on any fatal
        // AIProviderError so the handler's top-level try/with surfaces it
        // as AITaskFailed.
        //
        // Logs always include the full inner error message — without it the
        // operator has to dig into provider source to guess what class of
        // failure is firing (429 rate limit, 529 overloaded, timeout, etc).
        // The inner error is what informs whether to raise the
        // `RetryPolicy` budget, back off longer, or change tier.
        let sendWithOuterRetry (msgs: AIProviderMessage list) = async {
            // Phase 6i.A: TtftMs measurement. Stopwatch starts here so
            // the recorded time reflects everything the user perceives
            // as "wait before first character" — including any provider
            // retry pauses. `firstDeltaMs` stays `None` on tool-only
            // turns where the model never emits any narration.
            let sw = Stopwatch.StartNew()
            let mutable firstDeltaMs: float option = None

            let streamCb content =
                if firstDeltaMs.IsNone && not (System.String.IsNullOrEmpty content) then
                    firstDeltaMs <- Some sw.Elapsed.TotalMilliseconds

                onEvent (MessageDelta(conversationId, content))

            // Phase 6h follow-up: override the provider default so a
            // hung Anthropic call surfaces as `AITaskFailed` within
            // ~50 s rather than blocking until `HttpClient.Timeout`
            // (5 min instance-wide). The client watchdog is 60 s; we
            // want the provider error to win the race so chat shows
            // the real cause instead of the generic watchdog.
            let providerCallPolicy = {
                RetryPolicy.defaults with
                    Timeout = Some(TimeSpan.FromMilliseconds(float ProviderSendTimeoutMs))
            }

            let rec loop outerRetriesUsed = async {
                // Phase 6h follow-up — Workstream B. Trace each provider
                // call attempt under `ai.agent` so an operator running
                // with `TOOLUP_TRACE_CATEGORIES=ai.agent` can see exactly
                // when SendMessage is invoked, distinguishing "stuck in
                // provider" from "stuck in our agent loop logic." The
                // existing `Info` "AI agent turn N" log fires once per
                // turn at the top; this fires once per provider attempt
                // (1 + outer retries) so they're nested correctly.
                Logger.trace
                    logger
                    "ai.agent"
                    $"provider.SendMessage attempt {outerRetriesUsed + 1} (conversation={conversationId}, msgCount={msgs.Length}, toolCount={tools.Length})"

                let! result = provider.SendMessage(msgs, tools, systemPrompt, Some streamCb, providerCallPolicy)

                Logger.trace
                    logger
                    "ai.agent"
                    $"provider.SendMessage returned (conversation={conversationId}, ok={result.IsOk})"

                match result with
                | Ok response -> return response
                | Error err ->
                    match classifyForAgentLoop err with
                    | Fatal ->
                        logger.Error(
                            $"AI agent loop fatal provider error (conversation={conversationId}, provider={providerName}/{providerModel}): {AIProviderError.toMessage err}",
                            None
                        )
                        // Propagate as a typed message the caller can log.
                        // Partial streaming content is included in the
                        // StreamingAborted branch of toMessage.
                        return failwith (AIProviderError.toMessage err)
                    | OuterRetryable when outerRetriesUsed < AgentLoopOuterRetries ->
                        logger.Warn
                            $"AI agent loop outer retry {outerRetriesUsed + 1}/{AgentLoopOuterRetries} after provider budget exhausted (conversation={conversationId}, provider={providerName}/{providerModel}): {AIProviderError.toMessage err}"

                        do! Async.Sleep AgentLoopOuterRetryDelayMs
                        return! loop (outerRetriesUsed + 1)
                    | OuterRetryable ->
                        logger.Error(
                            $"AI agent loop giving up after {AgentLoopOuterRetries} outer retries (conversation={conversationId}, provider={providerName}/{providerModel}): {AIProviderError.toMessage err}",
                            None
                        )

                        return failwith (AIProviderError.toMessage err)
            }

            let! response = loop 0
            return (response, firstDeltaMs)
        }

        while keepGoing do
            // Phase 6h: cancellation check at turn boundary. The
            // user has clicked Cancel via /api/ai/cancel/{taskId};
            // emit StreamCancelled and raise OperationCanceledException
            // to break out of the loop cleanly. The outer try/with
            // catches it and returns the messages accumulated so far.
            // We don't try to cancel the in-flight provider call or
            // tool dispatch — the next turn boundary is the
            // predictable seam.
            if cancelToken.IsCancellationRequested then
                logger.Info
                    $"AI agent loop cancelled by user at turn {turn} (conversation={conversationId}, provider={providerName}/{providerModel})"

                onEvent (StreamCancelled conversationId)
                raise (System.OperationCanceledException("Cancelled by user"))

            turn <- turn + 1

            if turn > AgentLoopMaxTurns then
                // Safety rail — a well-behaved model answers in a handful
                // of turns. Crossing the ceiling means the model is
                // looping, a tool result is too large for it to digest,
                // or we've been cornered into a runaway plan-and-execute.
                // Bail with a typed error surfaced to the user rather
                // than burning provider spend silently.
                let msg =
                    $"Agent loop exceeded {AgentLoopMaxTurns} turns — stopping to avoid a runaway. "
                    + "If this was a legitimate multi-step task, try breaking it into smaller requests, "
                    + "or simplify the tools' return payloads so the model converges faster."

                logger.Error(
                    $"AI agent loop max-turns ({AgentLoopMaxTurns}) exceeded (conversation={conversationId}, provider={providerName}/{providerModel})",
                    None
                )

                failwith msg

            logger.Info
                $"AI agent turn {turn} (conversation={conversationId}, provider={providerName}/{providerModel}, tools={tools.Length})"

            // Phase 6i.A: per-turn stopwatch + tool-timing accumulator.
            // Both are local to the iteration body so they reset every
            // turn. `turnToolTimings` is populated only by the tool-use
            // branch; confusion-marker / end-turn branches leave it `[]`.
            let turnSw = Stopwatch.StartNew()
            let mutable turnToolTimings: ToolCallTiming list = []

            let! (response, ttftMs) = sendWithOuterRetry currentMessages

            // ───── Early-stop: confusion signal ─────────────────────
            // When the model's narration explicitly flags that it
            // can't proceed AND it's still trying to call tools, the
            // tool calls are speculative — the model itself has said
            // it doesn't know how to handle the situation. Continuing
            // burns provider quota for little gain. Emit the model's
            // current narration as its own assistant message so the
            // confusion text is visible, append a stop-reason message
            // naming the marker, and exit the loop.
            let confusionMarker =
                if response.ToolCalls.IsEmpty then
                    None // model said "I don't know" as its final answer — that's a normal end_turn
                else
                    detectConfusion response.Content

            match confusionMarker with
            | Some marker ->
                // Commit the narration that named the marker (the user
                // should see what the model said about being stuck).
                if not (System.String.IsNullOrWhiteSpace response.Content) then
                    let msgId = Guid.NewGuid()
                    onEvent (MessageComplete(conversationId, msgId))

                let reason =
                    $"the model indicated it can't proceed (matched \"{marker}\") but was still planning to call tools. "
                    + "Calls on an uncertain plan are unlikely to succeed."

                logger.Warn
                    $"AI agent loop early stop — confusion signal (conversation={conversationId}, provider={providerName}/{providerModel}): {marker}"

                emitEarlyStopMessage onEvent conversationId reason
                keepGoing <- false
            | None ->

                if response.ToolCalls.IsEmpty || response.StopReason = "end_turn" then
                    // Final assistant message — no more tool calls.
                    //
                    // Empty-content end_turn: the model chose to stop without
                    // saying anything. Before this guard the client would have
                    // committed an empty assistant message — "working" spinner
                    // clears but the chat shows nothing, and the user is left
                    // wondering if the request was received. Emit a
                    // placeholder via MessageDelta so the client's streaming
                    // buffer has content to commit, and log a warning so
                    // operators can correlate with provider behaviour.
                    //
                    // Legitimate causes for empty end_turn include: request
                    // too vague for the model to form a useful answer, context
                    // too large (the prompt itself exceeded the model's
                    // attention window), output-token limit hit before any
                    // user-facing text was generated, or a refusal pattern
                    // where the model declined to answer without explaining.
                    if System.String.IsNullOrWhiteSpace response.Content then
                        let placeholder =
                            "[The assistant produced no response. This can happen "
                            + "when the request is too vague, the context is too "
                            + "large, or the model hit an internal limit. Try "
                            + "rephrasing the question or breaking it into smaller "
                            + "steps.]"

                        logger.Warn
                            $"AI agent loop empty end_turn (conversation={conversationId}, provider={providerName}/{providerModel}) — surfacing placeholder to client"

                        onEvent (MessageDelta(conversationId, placeholder))

                    let assistantMsg = {
                        Role = "assistant"
                        Content = response.Content
                        ToolCalls = response.ToolCalls
                        ToolResults = []
                        Parts = []
                    }

                    currentMessages <- currentMessages @ [ assistantMsg ]
                    let msgId = Guid.NewGuid()
                    onEvent (MessageComplete(conversationId, msgId))
                    keepGoing <- false
                else
                    // Tool-use turn. Claude frequently narrates what it's
                    // about to do before calling a tool ("I'll load the
                    // data first...", "The data arrived but the cost column
                    // is missing — let me inspect the headers..."). Fire a
                    // MessageComplete so that narration lands in the chat
                    // as its own assistant message rather than piling into
                    // one giant blob at end_turn — or vanishing entirely
                    // when the agent loop later fails. Makes the model's
                    // reasoning visible turn-by-turn, which is critical for
                    // debugging ("why did it call that tool?") and gives
                    // users an obvious affordance to intervene when the
                    // model says "I'm not sure what to do here…".
                    //
                    // Skip the emit when the model chose to call a tool
                    // without any preamble — no point creating an empty
                    // message bubble.
                    if not (System.String.IsNullOrWhiteSpace response.Content) then
                        let msgId = Guid.NewGuid()
                        onEvent (MessageComplete(conversationId, msgId))

                    let assistantMsg = {
                        Role = "assistant"
                        Content = response.Content
                        ToolCalls = response.ToolCalls
                        ToolResults = []
                        Parts = []
                    }

                    currentMessages <- currentMessages @ [ assistantMsg ]

                    // Execute each tool call in parallel. Errors are classified
                    // into ToolInvocationError and rendered back to the model via
                    // the tool result content — the model can then retry, adjust
                    // arguments, or recover textually. Catastrophic provider
                    // failures propagate and are handled at the SubmitMessage
                    // boundary.
                    //
                    // Phase 6i.A: each parallel branch also returns a
                    // `ToolCallTiming` so the per-turn latency record
                    // can attribute time per tool. The location
                    // (server/client) is captured from the registry
                    // entry; unknown tools default to `ServerSide` (the
                    // value is informational, not load-bearing).
                    let! toolResultsAndTimings =
                        response.ToolCalls
                        |> List.map (fun tc -> async {
                            let toolSw = Stopwatch.StartNew()

                            let tcId =
                                match Guid.TryParse(tc.Id) with
                                | true, id -> id
                                | _ -> Guid.NewGuid()

                            onEvent (ToolCallStarted(conversationId, tc.Name, tcId))

                            let mutable resolvedLocation = ServerSide

                            let! result = async {
                                try
                                    match registry.FindByName tc.Name with
                                    | Some tool ->
                                        // Phase 6g.A: route by `Location`. ServerResident
                                        // — existing path (HttpContext + argsJson →
                                        // Async<resultJson>). ClientResident — emit
                                        // ClientToolInvoke SSE event, suspend on the
                                        // dispatch registry's TCS, await the matching
                                        // POST or timeout (90 s).
                                        match tool.Definition.Location with
                                        | ServerResident ->
                                            resolvedLocation <- ServerSide
                                            let! executed = tool.Execute ctx tc.Arguments
                                            return Ok executed
                                        | ClientResident ->
                                            resolvedLocation <- ClientSide

                                            // G12 allowlist: consult the optional
                                            // authorization seam BEFORE registering a
                                            // pending dispatch or emitting the
                                            // `ClientToolInvoke` SSE. A denied call is
                                            // never sent to the browser; the model is
                                            // told via a typed `Denied` tool-result and
                                            // the refusal is audited. No seam ⇒ `Allow`
                                            // (GP 13: unchanged behaviour).
                                            let decision =
                                                match authorizerOpt with
                                                | None -> Allow
                                                | Some auth ->
                                                    auth.Authorize(
                                                        tool.Definition.Name,
                                                        tc.Arguments,
                                                        activeModule,
                                                        activePage
                                                    )

                                            match decision with
                                            | Deny reason ->
                                                logger.Warn(
                                                    sprintf
                                                        "ClientResident tool '%s' denied by server-side allowlist (activeModule=%A): %s"
                                                        tool.Definition.Name
                                                        activeModule
                                                        reason
                                                )

                                                do! writeDenialAudit tool.Definition.Name reason
                                                return Error(Denied(tc.Name, reason))
                                            | Allow ->
                                                let pendingTask = dispatchRegistry.RegisterPending(tcId)

                                                // Phase 6g.A: SSE event carries the tool's
                                                // canonical name (`Definition.Name`, e.g.
                                                // `_platform.ui.inspect_active_module`), not
                                                // `tc.Name` which is the provider-sanitised
                                                // form (`_platform_ui_inspect_active_module`).
                                                // The client's `ClientToolRuntime.registry`
                                                // is keyed by the canonical name; using the
                                                // sanitised one here would miss every lookup.
                                                onEvent (
                                                    ClientToolInvoke(
                                                        taskId,
                                                        tcId,
                                                        tool.Definition.Name,
                                                        tc.Arguments,
                                                        activeModule,
                                                        activePage
                                                    )
                                                )

                                                let timeoutTask = Task.Delay(ClientResidentToolTimeoutMs)

                                                let! winner =
                                                    Task.WhenAny(pendingTask :> Task, timeoutTask) |> Async.AwaitTask

                                                if winner = (pendingTask :> Task) then
                                                    let! executed = pendingTask |> Async.AwaitTask
                                                    return Ok executed
                                                else
                                                    dispatchRegistry.TryAbort(
                                                        tcId,
                                                        $"Client did not respond within {ClientResidentToolTimeoutMs / 1000} seconds"
                                                    )
                                                    |> ignore

                                                    // Operator visibility for the silent-stall
                                                    // class: SSE stream paused (backgrounded
                                                    // tab), client registry has no handler for
                                                    // this canonical tool name, executor returned
                                                    // without POSTing /api/ai/tool-result, or
                                                    // multi-instance deployment dispatched the
                                                    // POST to a different process. Without a log
                                                    // line the symptom only surfaces as the
                                                    // model's tool-result message "Client did not
                                                    // respond …" and an operator cannot
                                                    // correlate it with anything serverside.
                                                    logger.Warn
                                                        $"AI ClientResident tool dispatch timed out after {ClientResidentToolTimeoutMs / 1000}s (conversation={conversationId}, tool={tool.Definition.Name}, toolCallId={tcId}, activeModule={activeModule}, activePage={activePage}). Likely causes: backgrounded client tab, unregistered tool, executor returned without POSTing tool-result, or multi-instance deployment without SSE/POST affinity."

                                                    return
                                                        Error(
                                                            ToolThrew(
                                                                tc.Name,
                                                                $"Client did not respond within {ClientResidentToolTimeoutMs / 1000} seconds"
                                                            )
                                                        )
                                    | None -> return Error(UnknownTool tc.Name)
                                with
                                | :? Text.Json.JsonException as ex ->
                                    return Error(InvalidArguments(tc.Name, ex.Message))
                                | :? ToolUp.AI.ToolHelpers.ToolArgumentError as ex ->
                                    return Error(InvalidArguments(tc.Name, ex.Message))
                                | ex -> return Error(ToolThrew(tc.Name, ex.Message))
                            }

                            let content =
                                match result with
                                | Ok r -> r
                                | Error err -> ToolInvocationError.toToolResultContent err

                            onEvent (ToolCallCompleted(conversationId, tcId, content))

                            toolSw.Stop()

                            let timing: ToolCallTiming = {
                                Name = tc.Name
                                Location = resolvedLocation
                                DurationMs = toolSw.Elapsed.TotalMilliseconds
                                Errored = isErrorToolResult content
                            }

                            let toolResult: AIProviderToolResult = {
                                ToolCallId = tc.Id
                                Content = content
                            }

                            return (toolResult, timing)
                        })
                        |> Async.Parallel

                    let toolResults = toolResultsAndTimings |> Array.map fst |> Array.toList

                    turnToolTimings <- toolResultsAndTimings |> Array.map snd |> Array.toList

                    // Append tool results as a user message for the next turn
                    let toolResultMsg = {
                        Role = "user"
                        Content = ""
                        ToolCalls = []
                        ToolResults = toolResults
                        Parts = []
                    }

                    currentMessages <- currentMessages @ [ toolResultMsg ]

                    // ───── Early-stop: consecutive failed-tool turns ────
                    // Track how many turns in a row contained only failing
                    // tool calls. A single errored turn is normal recovery
                    // (the model reads the error and adapts); two in a row
                    // strongly suggests it's attempting the same flawed
                    // path repeatedly rather than changing strategy. Reset
                    // to 0 the moment any tool in a turn succeeds.
                    let anySucceeded =
                        toolResults |> List.exists (fun tr -> not (isErrorToolResult tr.Content))

                    if anySucceeded then
                        consecutiveErroredTurns <- 0
                    else
                        consecutiveErroredTurns <- consecutiveErroredTurns + 1

                    if consecutiveErroredTurns >= MaxConsecutiveErroredTurns then
                        let failedNames =
                            response.ToolCalls |> List.map _.Name |> List.distinct |> String.concat ", "

                        let reason =
                            $"the last {consecutiveErroredTurns} turns had only failing tool calls "
                            + $"(last attempted: {failedNames}). The model isn't recovering from the errors."

                        logger.Warn
                            $"AI agent loop early stop — {consecutiveErroredTurns} consecutive errored turns (conversation={conversationId}, provider={providerName}/{providerModel}, last tools=[{failedNames}])"

                        emitEarlyStopMessage onEvent conversationId reason
                        keepGoing <- false

            // Phase 6i.A: end-of-turn latency emission. Runs after every
            // turn that completed `sendWithOuterRetry`, including the
            // confusion-marker / end-turn / tool-error early-stop
            // branches. Cancellation and max-turn exits leave via
            // `raise` / `failwith` and skip emission — they have no
            // useful timing to record.
            turnSw.Stop()
            do! emitLatency turn (Some response) ttftMs turnToolTimings turnSw.Elapsed.TotalMilliseconds

        return currentMessages
    }