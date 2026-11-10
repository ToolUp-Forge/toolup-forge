module ToolUp.AI.AIAgentEngine

open ToolUp.Platform.Transport
open System
open System.Diagnostics
open System.Runtime.ExceptionServices
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Http
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Metrics
open ToolUp.AI
open ToolUp.AI.AIToolRegistry
open ToolUp.AI.SystemPromptBuilder
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Jobs
open ToolUp.Platform.Audit
open ToolUp.Platform.Compose

/// Classification of tool-invocation failures, as the loop observes them.
/// Each case is a `ToolOutcome` (`ToolInvocationError.toOutcome`), which is
/// what the loop's rails read and what the model is shown.
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
    /// The outcome the error is: a refusal for `Denied`, a fault otherwise.
    let toOutcome (err: ToolInvocationError) : ToolOutcome =
        match err with
        | UnknownTool name -> ToolOutcome.Faulted(ToolFault.UnknownTool name)
        | ToolThrew(name, msg) -> ToolOutcome.Faulted(ToolFault.Threw(name, msg))
        | InvalidArguments(name, detail) -> ToolOutcome.Faulted(ToolFault.InvalidArguments(name, detail))
        | Denied(name, reason) -> ToolOutcome.Refused(ToolRefusal.Denied(name, reason))

    /// The tool result the model is shown — `ToolOutcome.toModel` of the
    /// error's outcome. The model uses it to decide whether to retry with
    /// different args, apologise to the user, or continue.
    let toToolResultContent (err: ToolInvocationError) = ToolOutcome.toModel (toOutcome err)

/// Delay before the outer retry. Longer than the provider's per-call
/// backoff: by the time the loop retries, the provider has already paused
/// across its own attempts.
let private AgentLoopOuterRetryDelayMs = 2000

/// Phrases in assistant text that indicate the model can't make progress
/// without human input. Matched case-insensitively as substrings. When one
/// fires AND the model is still trying to call tools, the agent loop
/// stops early: the calls are speculative by the model's own account.
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

/// Scan the model's response text for explicit "I can't proceed" markers.
let private detectConfusion (content: string) : string option =
    if String.IsNullOrWhiteSpace content then
        None
    else
        let lowered = content.ToLowerInvariant()
        confusionMarkers |> List.tryFind (fun marker -> lowered.Contains marker)

/// Read a client-resident tool's posted result. A fault the browser
/// reported arrives as `ToolOutcome.toModel` rendered it, and is read back
/// as that fault only when re-rendering it reproduces the posted bytes
/// exactly; anything else is content the tool produced.
let private ofClientResult (posted: string) : ToolOutcome =
    let fault =
        try
            use doc = JsonDocument.Parse posted
            let root = doc.RootElement

            let field (name: string) =
                match root.TryGetProperty name with
                | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
                | _ -> None

            if root.ValueKind <> JsonValueKind.Object then
                None
            else
                match field "error", field "tool", field "detail" with
                | Some "ToolThrew", Some tool, Some detail -> Some(ToolFault.ClientThrew(tool, detail))
                | Some "ClientWatchdogTimeout", Some tool, Some detail -> Some(ToolFault.ClientTimedOut(tool, detail))
                | Some "UnknownClientTool", Some tool, Some detail ->
                    Some(ToolFault.ClientToolUnregistered(tool, detail))
                | _ -> None
        with _ ->
            None

    match fault with
    | Some f when ToolOutcome.toModel (ToolOutcome.Faulted f) = posted -> ToolOutcome.Faulted f
    | _ -> ToolOutcome.Produced posted

/// Cap on a telemetry or audit write through `IEventStore`. The loop awaits
/// these on the response path, so a wedged backend must not stall the
/// conversation after the model has finished.
[<Literal>]
let private EventWriteTimeoutMs = 5_000

// ─── The caller's call budget ───────────────────────────────────

/// The budget every provider call one agent loop makes runs under. The
/// interactive budget is right for chat, where a client watchdog is
/// waiting; a background run nobody waits on passes its own, still finite,
/// since `RetryPolicy.clampTimeoutMs` bounds every per-call timeout.
type AgentCallBudget = {
    /// The per-call cap on `provider.SendMessage`, carried to the provider
    /// as the call's `RetryPolicy.Timeout`.
    CallTimeout: TimeSpan
    /// The reply-length budget (output tokens) carried through the call as
    /// `AIProviderCallOptions.MaxTokens`. `None` leaves the provider's own
    /// cap, and the call is sent exactly as before this budget existed.
    MaxTokens: int option
    /// How a deployment raises this budget, named in the error when a call
    /// runs out of it. `None` (the interactive budget) keeps the provider's
    /// error as the loop's error, unchanged.
    RaiseWith: string option
}

/// Building and describing a call budget.
module AgentCallBudget =
    /// The interactive budget under `limits`: `ProviderSendTimeout` a call,
    /// the provider's own reply cap.
    let ofLimits (limits: AgentLoopLimits) : AgentCallBudget = {
        CallTimeout = limits.ProviderSendTimeout
        MaxTokens = None
        RaiseWith = None
    }

    /// The interactive assistant's default budget: 50 s a call, the
    /// provider's own reply cap.
    let interactive: AgentCallBudget = ofLimits AgentLoopLimits.defaults

    /// The timeout a provider actually applies for this budget.
    let effectiveTimeout (budget: AgentCallBudget) : TimeSpan =
        TimeSpan.FromMilliseconds(float (RetryPolicy.clampTimeoutMs (int budget.CallTimeout.TotalMilliseconds)))

    /// One phrase naming the budget, for an error or a log line.
    let describe (budget: AgentCallBudget) : string =
        let reply =
            match budget.MaxTokens with
            | Some n -> sprintf " and a reply of up to %d tokens" n
            | None -> ""

        sprintf "a call budget of %.0f s%s" (effectiveTimeout budget).TotalSeconds reply

    /// The message for a call that ran out of `budget` after `elapsed`,
    /// naming the budget and how to raise it.
    let exceeded (budget: AgentCallBudget) (elapsed: TimeSpan) (detail: string) : string =
        sprintf
            "the model was still writing when the call reached %s (it ran %.0f s): %s. To give it longer, %s."
            (describe budget)
            elapsed.TotalSeconds
            detail
            (budget.RaiseWith |> Option.defaultValue "raise the call budget")

/// A provider call reached the end of a background caller's call budget
/// with the reply still streaming. Raised by the positional entry points
/// for `LoopStop.CallBudgetExceeded`; another attempt under the same budget
/// meets the same cap, so a caller treats it as permanent.
exception AgentCallBudgetExceeded of budget: AgentCallBudget * elapsed: TimeSpan * detail: string with
    override this.Message = AgentCallBudget.exceeded this.budget this.elapsed this.detail

// ─── How a loop stops, and how a turn ends ──────────────────────

/// Why the agent loop stopped before the model finished on its own. A soft
/// stop (`Confusion`, `ConsecutiveErrors`) explains itself in the chat and
/// the turn completes; every other stop ends the turn as a failure.
[<RequireQualifiedAccess>]
type LoopStop =
    /// The turn ceiling: the model was still calling tools after `limit`
    /// turns — a looping model, an indigestible tool result, or a runaway
    /// plan.
    | MaxTurns of limit: int
    /// The model's narration named a confusion marker while it still had
    /// tool calls pending.
    | Confusion of marker: string
    /// `turns` consecutive turns had only failing tool calls; `tools` names
    /// the calls of the last one.
    | ConsecutiveErrors of turns: int * tools: string list
    /// The provider failed in a way another attempt will not mend.
    | ProviderFatal of error: AIProviderError
    /// A provider call reached the end of a caller's budget that names how
    /// to raise it, with the reply still streaming.
    | CallBudgetExceeded of budget: AgentCallBudget * elapsed: TimeSpan * detail: string
    /// The turn's cancellation token fired.
    | Cancelled

/// Reading a loop stop.
module LoopStop =
    /// A soft stop ends the loop with an explanation in the chat, and the
    /// turn completes.
    let isSoft (stop: LoopStop) =
        match stop with
        | LoopStop.Confusion _
        | LoopStop.ConsecutiveErrors _ -> true
        | LoopStop.MaxTurns _
        | LoopStop.ProviderFatal _
        | LoopStop.CallBudgetExceeded _
        | LoopStop.Cancelled -> false

    /// The stop in words: the failure text for a hard stop, the reason the
    /// chat is given for a soft one.
    let message (stop: LoopStop) : string =
        match stop with
        | LoopStop.MaxTurns limit ->
            $"Agent loop exceeded {limit} turns — stopping to avoid a runaway. "
            + "If this was a legitimate multi-step task, try breaking it into smaller requests, "
            + "or simplify the tools' return payloads so the model converges faster."
        | LoopStop.Confusion marker ->
            $"the model indicated it can't proceed (matched \"{marker}\") but was still planning to call tools. "
            + "Calls on an uncertain plan are unlikely to succeed."
        | LoopStop.ConsecutiveErrors(turns, tools) ->
            let attempted = String.concat ", " tools

            $"the last {turns} turns had only failing tool calls "
            + $"(last attempted: {attempted}). The model isn't recovering from the errors."
        | LoopStop.ProviderFatal error -> AIProviderError.toMessage error
        | LoopStop.CallBudgetExceeded(budget, elapsed, detail) -> AgentCallBudget.exceeded budget elapsed detail
        | LoopStop.Cancelled -> "Cancelled by user"

    /// The assistant message a soft stop commits to the chat.
    let earlyStopMessage (stop: LoopStop) : string =
        $"[Agent stopped early] {message stop}\n\n"
        + "Retrying the same request won't help — please clarify your instructions, "
        + "check the input data, or simplify the task and try again."

/// The positional entry points' rendering of a hard stop, whose `Message`
/// is the stop's text. The record entry returns the stop as a value.
exception AgentLoopStopped of stop: LoopStop with
    override this.Message = LoopStop.message this.stop

/// Why a turn did not complete.
[<RequireQualifiedAccess>]
type TurnFailure =
    /// The caller does not own the conversation it addressed.
    | Refused of ownerOfRecord: string
    /// No provider could be resolved for the caller.
    | ProviderUnresolved of ProviderResolutionError
    /// A conversation blob is present but cannot be read; it is left as it
    /// is. `detail` is the operator-facing account (blob and reason), never
    /// shown to the client.
    | RecordUnreadable of conversationId: Guid * detail: string
    /// The turn's exchange could not be written; `detail` as above.
    | RecordUnwritten of conversationId: Guid * detail: string
    /// The agent loop stopped the turn.
    | Stopped of LoopStop
    /// The host shut down with the turn in flight.
    | ShutDown
    /// Anything else. The one case that carries an exception, whose
    /// message is the failure text.
    | Faulted of exn

/// Rendering a turn's failure.
module TurnFailure =
    /// The failure text of a turn cut short by host shutdown.
    [<Literal>]
    let ShuttingDownReason = "Server is shutting down"

    /// The failure text of a turn refused by the ownership gate.
    [<Literal>]
    let RefusedReason =
        "Conversation does not belong to the current user — refusing to append."

    /// The failure in words, as the client is shown it.
    let message (failure: TurnFailure) : string =
        match failure with
        | TurnFailure.Refused _ -> RefusedReason
        | TurnFailure.ProviderUnresolved err -> ProviderResolutionError.toMessage err
        | TurnFailure.RecordUnreadable(conversationId, _) ->
            $"The stored history of conversation {conversationId} could not be read, so it was left untouched rather than overwritten."
        | TurnFailure.RecordUnwritten(conversationId, _) ->
            $"The reply for conversation {conversationId} could not be saved."
        | TurnFailure.Stopped stop -> LoopStop.message stop
        | TurnFailure.ShutDown -> ShuttingDownReason
        | TurnFailure.Faulted ex -> ex.Message

/// How a turn ended.
[<RequireQualifiedAccess>]
type TurnOutcome =
    /// The turn ran to its end and its exchange is saved.
    | Completed
    /// The turn did not complete, for this reason.
    | Failed of TurnFailure
    /// The user cancelled the turn; nothing went wrong.
    | CancelledByUser

/// Rendering a turn's outcome.
module TurnOutcome =
    /// The one rendering of a turn's end to the wire. A user's cancel ends
    /// the task as completed: nothing went wrong.
    let toStatus (outcome: TurnOutcome) : AITaskStatus =
        match outcome with
        | TurnOutcome.Completed
        | TurnOutcome.CancelledByUser -> AITaskCompleted
        | TurnOutcome.Failed failure -> AITaskFailed(TurnFailure.message failure)

// ─── What a turn runs with ──────────────────────────────────────

/// The services a turn reads, resolved once. `AICompose` registers the
/// resolution per DI scope; a context built without composition resolves
/// it from the context's own services, where each is optional.
type TurnServices = {
    /// DI-registered logger, or a silent one. Messages never carry prompt
    /// content or key material.
    Logger: ILogger
    /// Where latency rows and tool-denial audits are written.
    EventStore: IEventStore option
    /// Where the per-turn latency metrics are recorded.
    MetricsSink: IMetricsSink option
    /// The optional allowlist for client-resident tool invocations; absent
    /// means every invocation is allowed (GP 13).
    ClientToolAuthorizer: IClientToolAuthorizer option
    /// The sustained-denial rate monitor and the channel it alerts through.
    DenialRateMonitor: AIDenialRateMonitor.AIDenialRateMonitor option
    /// The channel the denial rate monitor alerts through.
    DenialAlertChannel: INotificationChannel option
    /// The post-stream citation normaliser, when RAG composed one.
    CitationNormaliser: ICitationNormaliser option
    /// The numeric-fidelity answer gate, when composed, with the metric
    /// registry it verifies against.
    AnswerGate: AnswerVerifier.AnswerGate option
    MetricRegistry: Grounding.IMetricRegistry option
    /// The audit log and provenance anchors the answer gate's row joins.
    AuditLog: IAuditLog
    AnswerAnchors: IAnswerProvenanceAnchors option
}

/// Resolving a turn's services.
module TurnServices =
    let private silentLogger =
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn _ = ()
            member _.Error(_, _) = ()
        }

    let private optional<'T> (services: IServiceProvider) : 'T option =
        match services.GetService(typeof<'T>) with
        | :? 'T as s -> Some s
        | _ -> None

    /// Resolve the turn's services from `services`.
    let resolve (services: IServiceProvider) : TurnServices = {
        Logger = optional<ILogger> services |> Option.defaultValue silentLogger
        EventStore = optional<IEventStore> services
        MetricsSink = optional<IMetricsSink> services
        ClientToolAuthorizer = optional<IClientToolAuthorizer> services
        DenialRateMonitor = optional<AIDenialRateMonitor.AIDenialRateMonitor> services
        DenialAlertChannel = optional<INotificationChannel> services
        CitationNormaliser = optional<ICitationNormaliser> services
        AnswerGate = optional<AnswerVerifier.AnswerGate> services
        MetricRegistry = optional<Grounding.IMetricRegistry> services
        AuditLog =
            optional<IAuditLog> services
            |> Option.defaultWith (fun () -> NoOpAuditLog() :> IAuditLog)
        AnswerAnchors = optional<IAnswerProvenanceAnchors> services
    }

    /// The services the request's scope composed, or resolved from it.
    let ofContext (ctx: HttpContext) : TurnServices =
        match optional<TurnServices> ctx.RequestServices with
        | Some services -> services
        | None -> resolve ctx.RequestServices

/// One turn of the agent loop: everything it runs with, resolved once.
type TurnContext = {
    /// The request the turn runs in, for its services and the grant stamps
    /// the grant gate reads.
    Request: HttpContext
    /// The task the client is watching.
    TaskId: Guid
    /// The conversation the turn appends to.
    ConversationId: Guid
    /// The surface the turn was asked from; it filters the offered tools.
    Surface: AISurface
    /// The module and page the user was on, as the client reported them.
    ActiveModule: string option
    ActivePage: string option
    /// The provider resolved for the turn; every call the loop makes uses it.
    Provider: IAIProvider
    /// The tools the turn may dispatch.
    Registry: AIToolRegistry
    /// Where a client-resident tool call waits for the browser's result.
    ClientDispatch: ClientToolDispatch.ClientToolDispatchRegistry
    /// The caller's access, reconstructed from the request's items once:
    /// permissions do not change mid-turn, so a parallel tool batch is
    /// judged by one decision.
    Access: AccessContext
    /// The grant / consent gate, resolved once for the same reason.
    GrantGate: string -> bool
    /// The tools offered to the provider: readable by the caller, under a
    /// live grant, visible on the surface.
    Tools: AIProviderToolDef list
    /// What the provider is shown besides the messages.
    Input: ModelInput
    /// The budget every provider call runs under.
    Budget: AgentCallBudget
    /// The loop's limits.
    Limits: AgentLoopLimits
    /// The services the turn reads.
    Services: TurnServices
    /// The turn's stream.
    Emit: AIStreamEvent -> unit
    /// The turn's cancellation: the user's cancel and the host's shutdown.
    /// It reaches the provider call and the tool body.
    Cancel: CancellationToken
}

/// Building a turn's context.
module TurnContext =
    /// Build a turn over `request`, resolving its access, grant gate,
    /// offered tools and services once.
    let create
        (provider: IAIProvider)
        (registry: AIToolRegistry)
        (clientDispatch: ClientToolDispatch.ClientToolDispatchRegistry)
        (request: HttpContext)
        (taskId: Guid)
        (conversationId: Guid)
        (surface: AISurface)
        (activeModule: string option)
        (activePage: string option)
        (input: ModelInput)
        (budget: AgentCallBudget)
        (limits: AgentLoopLimits)
        (emit: AIStreamEvent -> unit)
        (cancel: CancellationToken)
        : TurnContext =
        let access = reconstructAccessContext request
        let grantGate = moduleGrantGate request

        {
            Request = request
            TaskId = taskId
            ConversationId = conversationId
            Surface = surface
            ActiveModule = activeModule
            ActivePage = activePage
            Provider = provider
            Registry = registry
            ClientDispatch = clientDispatch
            Access = access
            GrantGate = grantGate
            Tools =
                registry.ListAccessible(access, grantGate)
                |> List.filter (fun t -> isToolVisibleOnSurface surface t.Definition)
                |> List.map _.ProviderDef
            Input = input
            Budget = budget
            Limits = limits
            Services = TurnServices.ofContext request
            Emit = emit
            Cancel = cancel
        }

    /// The turn's stream, as a gate or a tool asks its user on it.
    let channel (turn: TurnContext) : PromptChannel = {
        ConversationId = turn.ConversationId
        TaskId = turn.TaskId
        Emit = turn.Emit
    }

    /// What a tool called in this turn runs with.
    let toolContext (turn: TurnContext) : ToolContext = {
        Request = turn.Request
        Access = turn.Access
        Scope = AIRequest.scopeOf turn.Access
        Channel = Some(channel turn)
        ActiveModule = turn.ActiveModule
        ActivePage = turn.ActivePage
        Cancel = turn.Cancel
    }

/// What a turn does before any provider call: reply with text it already
/// has, or run the loop.
[<RequireQualifiedAccess>]
type TurnPlan =
    /// Reply with this text and make no provider call.
    | Reply of text: string
    /// Run the agent loop.
    | RunLoop

/// Deciding a turn's plan.
module TurnPlan =
    /// A prompt builder's short-circuit is a reply; otherwise the loop runs.
    let ofContribution (contribution: PromptContribution) : TurnPlan =
        match contribution.ShortCircuit with
        | Some text -> TurnPlan.Reply text
        | None -> TurnPlan.RunLoop

/// The loop's state, threaded through `Loop.run`.
type LoopState = {
    /// The turns taken so far.
    Turn: int
    /// The conversation as the provider will next be shown it.
    Messages: AIProviderMessage list
    /// Consecutive turns whose every tool call errored.
    ConsecutiveErrored: int
    /// The tool calls of the latest turn, for its latency row.
    Timings: ToolCallTiming list
    /// The soft stop that ended the loop, when one did.
    Stopped: LoopStop option
    /// The parts server-resident tools attached across the
    /// turn's tool rounds, in round order and, within a round, in
    /// tool-call order. The final assistant message carries them after
    /// its text (`LoopState.messageParts`).
    Attached: AIContentPart list
}

/// The loop's starting state.
module LoopState =
    /// The state before the first turn, over `messages`.
    let start (messages: AIProviderMessage list) : LoopState = {
        Turn = 0
        Messages = messages
        ConsecutiveErrored = 0
        Timings = []
        Stopped = None
        Attached = []
    }

    /// The parts of the final assistant message whose text is
    /// `text`: none when no tool attached a part, so a plain turn stays a
    /// plain-text message; otherwise the text part first, then the attached
    /// parts in order.
    let messageParts (text: string) (state: LoopState) : AIContentPart list =
        match state.Attached with
        | [] -> []
        | attached -> TextPart text :: attached

/// What one provider response means for the loop.
type StepVerdict =
    /// The model answered; the state carries its message.
    | Final of LoopState
    /// The model asked for these tools; the state carries its message.
    | ToolRound of LoopState * AIProviderToolCall list
    /// The loop stops on this response.
    | Stop of LoopStop

// ─── Effects ────────────────────────────────────────────────────

/// Run `work` under `cancel` as its ambient token, and stop waiting for it
/// the moment `cancel` fires, whether or not the work observes the token.
/// `None` is a cancelled run.
let private underCancel (cancel: CancellationToken) (work: Async<'T>) : Async<'T option> = async {
    let running = Async.StartAsTask(work, cancellationToken = cancel)
    let cancelled = Task.Delay(Timeout.Infinite, cancel)
    let! _ = Task.WhenAny(running :> Task, cancelled) |> Async.AwaitTask

    if running.IsCompletedSuccessfully then
        return Some running.Result
    elif running.IsFaulted && not cancel.IsCancellationRequested then
        ExceptionDispatchInfo.Capture(running.Exception.InnerException).Throw()
        return None
    else
        return None
}

module private Bounded =
    /// Write `evt` within `EventWriteTimeoutMs`. Never throws and never
    /// blocks past the cap: a slow or failing backend is logged at Warn,
    /// and the record is dropped.
    let write (logger: ILogger) (what: string) (store: IEventStore) (evt: ModuleEvent) : Async<unit> = async {
        try
            let writeTask = EventStore.writeOrRaise store evt |> Async.StartAsTask

            let! winner =
                Task.WhenAny(writeTask :> Task, Task.Delay EventWriteTimeoutMs)
                |> Async.AwaitTask

            if winner = (writeTask :> Task) then
                do! writeTask |> Async.AwaitTask
            else
                logger.Warn $"{what} timed out after {EventWriteTimeoutMs / 1000}s; record dropped."
        with ex ->
            logger.Warn $"{what} failed: {ex.Message}. Record dropped; conversation unaffected."
    }

module private Telemetry =
    let private jsonOptions = PlatformJson.options

    /// The caller's user id for `AILatencyRecord.UserId`; `""` (unattributed)
    /// when the request resolved no principal.
    let private userIdOf (turn: TurnContext) : string =
        match AIRequest.access turn.Request with
        | Some access -> access.UserId
        | None -> ""

    let private event (turn: TurnContext) (sourceModule: string) (eventType: string) (payload: string) : ModuleEvent = {
        Id = Guid.NewGuid()
        OccurredAt = DateTime.UtcNow
        ScopeId = (AIRequest.scopeOf turn.Access).ScopeId
        SourceModule = sourceModule
        EventType = eventType
        Payload = payload
    }

    /// One latency row per turn through `IEventStore`, and the same
    /// observations through `IMetricsSink`. Names the provider SERVING the
    /// turn (a failover chain's winner), read at the moment of writing.
    let latency
        (turn: TurnContext)
        (turnNumber: int)
        (response: AIProviderResponse)
        (ttftMs: float option)
        (timings: ToolCallTiming list)
        (durationMs: float)
        : Async<unit> =
        async {
            let logger = turn.Services.Logger
            let usage = response.Usage

            let record: AILatencyRecord = {
                TaskId = turn.TaskId
                ConversationId = turn.ConversationId
                UserId = UserId(userIdOf turn)
                TurnNumber = turnNumber
                ProviderName = turn.Provider.Capabilities.ProviderName
                ProviderModel = turn.Provider.Capabilities.Model
                TtftMs = ttftMs
                TurnDurationMs = durationMs
                ToolCalls = timings
                StopReason = response.StopReason.Token
                PromptTokens = usage |> Option.map _.PromptTokens
                CachedPromptTokens = usage |> Option.map _.CachedPromptTokens
                OutputTokens = usage |> Option.map _.OutputTokens
                CacheCreationTokens = usage |> Option.bind _.CacheCreationTokens
            }

            match turn.Services.EventStore with
            | None -> ()
            | Some store ->
                do!
                    Bounded.write
                        logger
                        $"AI latency event-store write (taskId={turn.TaskId}, turn={turnNumber})"
                        store
                        (event
                            turn
                            AILatencyRecord.SourceModule
                            AILatencyRecord.EventType
                            (JsonSerializer.Serialize(record, jsonOptions)))

            // Only `provider` and `model` reach the sink (the metric-tag
            // allowlist). A defective sink never crashes the loop.
            match turn.Services.MetricsSink with
            | None -> ()
            | Some sink ->
                try
                    let tags =
                        Map.ofList [ "provider", record.ProviderName; "model", record.ProviderModel ]

                    sink.Record(AILatencyMetrics.TurnDurationMs, durationMs, tags)
                    ttftMs |> Option.iter (fun t -> sink.Record(AILatencyMetrics.TtftMs, t, tags))

                    match record.PromptTokens, record.CachedPromptTokens with
                    | Some prompt, Some cached when prompt > 0 ->
                        sink.Record(AILatencyMetrics.CachedPromptRatio, float cached / float prompt, tags)
                    | _ -> ()
                with ex ->
                    logger.Warn
                        $"AI latency IMetricsSink.Record failed (taskId={turn.TaskId}, turn={turnNumber}): {ex.Message}. Metric dropped; conversation unaffected."
        }

    /// Record `refusal` on the stream its value names
    /// (`ToolOutcome.auditStream`), and log it.
    let denied (turn: TurnContext) (refusal: ToolRefusal) : Async<unit> = async {
        let toolName = ToolRefusal.toolName refusal
        let reason = ToolRefusal.reason refusal
        let stream = ToolOutcome.auditStream refusal

        turn.Services.Logger.Warn
            $"AI tool '{toolName}' refused at dispatch ({stream}, activeModule={turn.ActiveModule}): {reason}"

        match refusal with
        | ToolRefusal.Unreadable(_, moduleName) ->
            recordUnauthorizedToolDenial turn.Request turn.Access "agent-loop:tool-dispatch" toolName moduleName
        | _ -> ()

        match ToolAuditStream.source stream, ToolAuditStream.eventType stream, turn.Services.EventStore with
        | Some source, Some eventType, Some store ->
            let payload = {|
                ToolName = toolName
                Reason = reason
                ActiveModule = turn.ActiveModule
                ActivePage = turn.ActivePage
                TaskId = turn.TaskId
                ConversationId = turn.ConversationId
            |}

            do!
                Bounded.write
                    turn.Services.Logger
                    $"AI tool denial audit write ({source}, taskId={turn.TaskId})"
                    store
                    (event turn source eventType (JsonSerializer.Serialize(payload, jsonOptions)))
        | _ -> ()

        if stream = ToolAuditStream.PolicyDenial then
            do!
                AIDenialRateMonitor.observeAndAlert
                    turn.Services.DenialRateMonitor
                    turn.Services.DenialAlertChannel
                    (AIRequest.scopeOf turn.Access).ScopeId
                    toolName
    }

    /// Count one tool-result elision, and warn once per tool per registry.
    let elided (turn: TurnContext) (tool: RegisteredTool) (elidedChars: int) =
        let logger = turn.Services.Logger
        let name = tool.Definition.Name

        match turn.Services.MetricsSink with
        | None -> ()
        | Some sink ->
            try
                sink.Increment(AILatencyMetrics.ToolResultElided, Map.ofList [ "tool", name ])
            with ex ->
                logger.Warn
                    $"AI tool-result budget IMetricsSink.Increment failed (tool={name}): {ex.Message}. Metric dropped; elision still applied."

        if turn.Registry.TryClaimBudgetWarning name then
            let ceiling =
                match ToolResultBudget.limitFor tool.Definition.ResultBudget with
                | Some l -> string l
                | None -> "unbounded"

            logger.Warn
                $"AI tool '{name}' returned {elidedChars} characters, over its {ceiling}-character result budget; the result was replaced by the elision marker and the model was steered to narrow the request (conversation={turn.ConversationId}). This tool needs an aggregate or paginated shape, or a deliberate ResultBudget declaration. Logged once per tool; every occurrence is counted on {AILatencyMetrics.ToolResultElided}."

module private Dispatch =
    /// The gates the loop's dispatch runs, g1-g5, over the tool's own source
    /// module: the decision the offered list was filtered by
    /// (`proofs/ToolGate.fst`, `dispatch_never_wider_than_list`), so a tool
    /// it refuses was never offered and the name is forged, hallucinated or
    /// replayed; then the client-resident allowlist (which admits a
    /// server-resident tool); then the human approval for both locations.
    /// Queryability and consent gate a reach tool's TARGET module, inside it.
    let private gates (turn: TurnContext) : GateSet = {
        ToolGate = Gates.toolGate turn.GrantGate turn.Registry.Policy
        Allowlist =
            fun call -> async {
                match call.Tool.Location, turn.Services.ClientToolAuthorizer with
                | ClientResident, Some authorizer ->
                    match authorizer.Authorize(call.Tool.Name, call.Arguments, turn.ActiveModule, turn.ActivePage) with
                    | ClientToolAuthDecision.Allow -> return Ok()
                    | ClientToolAuthDecision.Deny reason ->
                        return Error(ToolRefusal.AllowlistDenied(call.Tool.Name, reason))
                | _ -> return Ok()
            }
        Approval =
            ToolApprovalDispatch.approve
                SuspendedPrompt.SuspendedDispatchTimeoutMs
                turn.Registry.Policy
                turn.Services.Logger
                (Some(TurnContext.channel turn))
                turn.ActiveModule
                turn.ActivePage
        Queryability = Gates.pass
        Consent = Gates.pass
    }

    /// Wait for the browser to post a client-resident tool's result, within
    /// the turn's `ClientToolTimeout`.
    let private awaitClient (turn: TurnContext) (tc: AIProviderToolCall) (tcId: Guid) (def: AIToolDefinition) = async {
        // The source module rides the pending call so `/api/ai/tool-result`
        // re-checks the POSTing caller's permission before completing it.
        let pending = turn.ClientDispatch.RegisterPending(tcId, Some def.SourceModule)

        // The canonical name, not the provider-sanitised `tc.Name`: the
        // client's registry is keyed by the canonical name.
        turn.Emit(ClientToolInvoke(turn.TaskId, tcId, def.Name, tc.Arguments, turn.ActiveModule, turn.ActivePage))

        let timeoutMs = int turn.Limits.ClientToolTimeout.TotalMilliseconds

        let! winner =
            Task.WhenAny(pending :> Task, Task.Delay(timeoutMs, turn.Cancel))
            |> Async.AwaitTask

        if winner = (pending :> Task) then
            let! posted = pending |> Async.AwaitTask

            // What the browser posts reaches the model through
            // the same door a server-resident result does.
            match ofClientResult posted with
            | ToolOutcome.Produced text ->
                let! gated = gateModuleToolResult turn.Request def.Name def.SourceModule text
                return ToolOutcome.Produced gated
            | outcome -> return outcome
        else
            let detail = $"Client did not respond within {timeoutMs / 1000} seconds"
            turn.ClientDispatch.TryAbort(tcId, detail) |> ignore

            // The silent-stall class: a backgrounded tab, an unregistered
            // tool, an executor that never posted, or a POST routed to
            // another process. Without this line the operator sees nothing.
            turn.Services.Logger.Warn
                $"AI ClientResident tool dispatch timed out after {timeoutMs / 1000}s (conversation={turn.ConversationId}, tool={def.Name}, toolCallId={tcId}, activeModule={turn.ActiveModule}, activePage={turn.ActivePage}). Likely causes: backgrounded client tab, unregistered tool, executor returned without POSTing tool-result, or multi-instance deployment without SSE/POST affinity."

            return ToolInvocationError.toOutcome (ToolThrew(tc.Name, detail))
    }

    /// A client-resident tool carrying the record form of the
    /// executor is refused by name before any gate runs: its result is the
    /// browser's, and a part is the server's judgement of a read it made.
    let private refuseAttach (turn: TurnContext) (tc: AIProviderToolCall) (def: AIToolDefinition) =
        turn.Services.Logger.Warn
            $"AI tool '{def.Name}' is client-resident and carries the record form of the executor; refused (conversation={turn.ConversationId}). Register it with createTool, or declare it ServerResident."

        ToolOutcome.Refused(
            ToolRefusal.Denied(tc.Name, "a client-resident tool cannot attach parts to the assistant message")
        )

    /// Run an admitted server-resident tool inside its declared effect
    /// envelope (an undeclared tool has none) and under the turn's
    /// cancellation: the typed executor when the tool has one, else the
    /// string contract over a request stamped with the turn's carry.
    let private runServer (turn: TurnContext) (tc: AIProviderToolCall) (tool: RegisteredTool) = async {
        let def = tool.Definition
        let context = TurnContext.toolContext turn

        let body: Async<ToolOutcome * AIContentPart list> =
            match tool.ExecuteTyped, tool.Answer with
            | Some execute, _ -> async {
                match ToolArgs.parse tc.Arguments with
                | Error detail -> return ToolOutcome.Faulted(ToolFault.InvalidArguments(tc.Name, detail)), []
                | Ok args ->
                    let! outcome = execute context args
                    return outcome, []
              }
            | None, Some answer -> async {
                ToolContext.stamp context
                let! answered = answer turn.Request tc.Arguments
                return ToolOutcome.Produced answered.Text, answered.Parts
              }
            | None, None -> async {
                ToolContext.stamp context
                let! content = tool.Execute turn.Request tc.Arguments
                return ToolOutcome.Produced content, []
              }

        let! executed =
            ToolEffectEnvelope.runWithin
                {
                    ToolName = def.Name
                    Effects = AIToolEffects.declaredOf def
                    OnDenied = fun denial -> Telemetry.denied turn (ToolRefusal.OutsideCeiling(def.Name, denial.Reason))
                }
                body
            |> underCancel turn.Cancel

        match executed with
        | Some result -> return result
        | None -> return ToolInvocationError.toOutcome (ToolThrew(tc.Name, "The turn was cancelled.")), []
    }

    /// Decide and run one tool call, with the parts it attached: the gates
    /// in their one order (`Gates.admit`), then the tool where it lives.
    /// Every failure becomes a `ToolOutcome` the model is shown and attaches
    /// nothing; nothing here ends the turn.
    let private decide (turn: TurnContext) (tc: AIProviderToolCall) (tcId: Guid) (toolOpt: RegisteredTool option) = async {
        try
            match toolOpt with
            | None -> return ToolInvocationError.toOutcome (UnknownTool tc.Name), []
            | Some tool when tool.Definition.Location = ClientResident && tool.Answer.IsSome ->
                return refuseAttach turn tc tool.Definition, []
            | Some tool ->
                let def = tool.Definition

                let call: GateCall = {
                    Request = turn.Request
                    Access = turn.Access
                    Tool = def
                    Arguments = tc.Arguments
                    Module = def.SourceModule
                }

                match! Gates.admit (gates turn) call with
                | Error refusal ->
                    do! Telemetry.denied turn refusal
                    return ToolOutcome.Refused refusal, []
                | Ok _ ->
                    match def.Location with
                    | ServerResident -> return! runServer turn tc tool
                    | ClientResident ->
                        let! outcome = awaitClient turn tc tcId def
                        return outcome, []
        with
        | :? JsonException as ex -> return ToolInvocationError.toOutcome (InvalidArguments(tc.Name, ex.Message)), []
        // The exception's own text, not `Message`, which F# renders as
        // the exception's structured form.
        | ToolHelpers.ToolArgumentError message ->
            return ToolInvocationError.toOutcome (InvalidArguments(tc.Name, message)), []
        | ToolHelpers.ToolFaulted fault -> return ToolOutcome.Faulted fault, []
        | ex -> return ToolInvocationError.toOutcome (ToolThrew(tc.Name, ex.Message)), []
    }

    /// Execute one tool call: the result the model is shown, the call's
    /// latency timing, its outcome for the rails, and the parts it attached
    ///, which ride only a produced outcome.
    ///
    /// Produced content passes the tool's result budget, the single choke
    /// point between a tool and model context — a client-resident result
    /// included — strictly after execution, so every disclosure gate inside
    /// the tool has already decided and the budget can only remove. A fault
    /// or refusal is the loop's own short rendering and is not bounded.
    let execute (turn: TurnContext) (tc: AIProviderToolCall) = async {
        let sw = Stopwatch.StartNew()

        let tcId =
            match Guid.TryParse tc.Id with
            | true, id -> id
            | _ -> Guid.NewGuid()

        turn.Emit(ToolCallStarted(turn.ConversationId, tc.Name, tcId))

        let toolOpt = turn.Registry.FindByName tc.Name

        let! outcome, attached = decide turn tc tcId toolOpt
        let rendered = ToolOutcome.toModel outcome

        let content =
            match outcome, toolOpt with
            | ToolOutcome.Produced _, Some tool ->
                let payload, elided =
                    ToolResultBudget.apply tool.Definition.Name tool.Definition.ResultBudget rendered

                elided |> Option.iter (Telemetry.elided turn tool)
                payload
            | _ -> rendered

        turn.Emit(ToolCallCompleted(turn.ConversationId, tcId, content))
        sw.Stop()

        let timing: ToolCallTiming = {
            Name = tc.Name
            Location =
                match toolOpt, outcome with
                | Some tool, (ToolOutcome.Produced _ | ToolOutcome.Faulted _) when
                    tool.Definition.Location = ClientResident
                    ->
                    ClientSide
                | _ -> ServerSide
            DurationMs = sw.Elapsed.TotalMilliseconds
            Errored = ToolOutcome.isError outcome
        }

        return
            {
                ToolCallId = tc.Id
                Content = content
            },
            timing,
            outcome,
            (if ToolOutcome.isError outcome then [] else attached)
    }

// ─── The loop ───────────────────────────────────────────────────

/// The agent loop: one pure step per provider response, its effects, and the run.
module Loop =
    let private assistant (response: AIProviderResponse) : AIProviderMessage = {
        Role = "assistant"
        Content = response.Content
        ToolCalls = response.ToolCalls
        ToolResults = []
        Parts = []
    }

    /// What one provider response means, decided purely. A confusion marker
    /// counts only while the model is still calling tools: said as a final
    /// answer it is an ordinary end of turn.
    let step (state: LoopState) (response: AIProviderResponse) : StepVerdict =
        match response.ToolCalls with
        | [] ->
            Final {
                state with
                    Messages = state.Messages @ [ assistant response ]
            }
        | calls ->
            match detectConfusion response.Content with
            | Some marker -> Stop(LoopStop.Confusion marker)
            | None when response.StopReason = StopReason.EndTurn ->
                Final {
                    state with
                        Messages = state.Messages @ [ assistant response ]
                }
            | None ->
                ToolRound(
                    {
                        state with
                            Messages = state.Messages @ [ assistant response ]
                    },
                    calls
                )

    /// Fold a tool round's results into the state, purely: the results
    /// become the next user message, the errored-turn count advances or
    /// resets, the consecutive-errors rail fires when it reaches the
    /// limit, and the round's attached parts extend `Attached` in
    /// tool-call order.
    let settle
        (turn: TurnContext)
        (state: LoopState)
        (calls: AIProviderToolCall list)
        (results: (AIProviderToolResult * ToolCallTiming * ToolOutcome * AIContentPart list) list)
        : LoopState =
        let toolResults = results |> List.map (fun (result, _, _, _) -> result)

        let errored =
            if
                results
                |> List.exists (fun (_, _, outcome, _) -> not (ToolOutcome.isError outcome))
            then
                0
            else
                state.ConsecutiveErrored + 1

        {
            state with
                Messages =
                    state.Messages
                    @ [
                        {
                            AIProviderMessage.text "user" "" with
                                ToolResults = toolResults
                        }
                    ]
                ConsecutiveErrored = errored
                Timings = results |> List.map (fun (_, timing, _, _) -> timing)
                Attached = state.Attached @ (results |> List.collect (fun (_, _, _, parts) -> parts))
                Stopped =
                    if errored >= turn.Limits.MaxConsecutiveErrored then
                        Some(LoopStop.ConsecutiveErrors(errored, calls |> List.map _.Name |> List.distinct))
                    else
                        None
        }

    /// Reply with `text` and no provider call: stream it, close the message
    /// so it lands in the chat, and append it to the history. The prompt
    /// builder's short-circuit and the triage intercept both reply here.
    let reply (turn: TurnContext) (messages: AIProviderMessage list) (text: string) : LoopState =
        turn.Emit(MessageDelta(turn.ConversationId, text))
        turn.Emit(MessageComplete(turn.ConversationId, Guid.NewGuid()))
        LoopState.start (messages @ [ AIProviderMessage.text "assistant" text ])

    /// One provider call, with the outer retry on `RetriesExhausted`, under
    /// the turn's budget and cancellation. The time to first streamed
    /// character rides back with the response.
    let private send (turn: TurnContext) (messages: AIProviderMessage list) = async {
        let logger = turn.Services.Logger
        let provider = turn.Provider
        let who = $"{provider.Capabilities.ProviderName}/{provider.Capabilities.Model}"
        let sw = Stopwatch.StartNew()
        let firstDelta: float option ref = ref None

        let onStream content =
            if firstDelta.Value.IsNone && not (String.IsNullOrEmpty content) then
                firstDelta.Value <- Some sw.Elapsed.TotalMilliseconds

            turn.Emit(MessageDelta(turn.ConversationId, content))

        let policy = {
            RetryPolicy.defaults with
                Timeout = Some turn.Budget.CallTimeout
        }

        let call (input: ModelInput) =
            match turn.Budget.MaxTokens with
            | None -> provider.SendMessage(input, turn.Tools, Some onStream, policy)
            | Some maxTokens -> async {
                let! sent =
                    provider.SendMessageWith(
                        AIProviderCallOptions.withMaxTokens maxTokens AIProviderCallOptions.none,
                        input,
                        turn.Tools,
                        Some onStream,
                        policy
                    )

                return sent |> Result.map _.Response
              }

        let rec attempt retriesUsed = async {
            Logger.trace
                logger
                "ai.agent"
                $"provider.SendMessage attempt {retriesUsed + 1} (conversation={turn.ConversationId}, msgCount={messages.Length}, toolCount={turn.Tools.Length})"

            let! sent = call (ModelInput.withMessages messages turn.Input) |> underCancel turn.Cancel

            match sent with
            | None -> return Error LoopStop.Cancelled
            | Some(Ok response) -> return Ok(response, firstDelta.Value)
            | Some(Error err) ->
                let detail = AIProviderError.toMessage err

                match err with
                | RetriesExhausted _ when retriesUsed < turn.Limits.OuterRetries ->
                    logger.Warn
                        $"AI agent loop outer retry {retriesUsed + 1}/{turn.Limits.OuterRetries} after provider budget exhausted (conversation={turn.ConversationId}, provider={who}): {detail}"

                    do! Async.Sleep AgentLoopOuterRetryDelayMs
                    return! attempt (retriesUsed + 1)
                | StreamingAborted _ when
                    turn.Budget.RaiseWith.IsSome
                    && sw.Elapsed >= AgentCallBudget.effectiveTimeout turn.Budget
                    ->
                    logger.Error(
                        $"AI agent loop call budget exceeded (conversation={turn.ConversationId}, provider={who}, {AgentCallBudget.describe turn.Budget}): {detail}",
                        None
                    )

                    return Error(LoopStop.CallBudgetExceeded(turn.Budget, sw.Elapsed, detail))
                | RetriesExhausted _ ->
                    logger.Error(
                        $"AI agent loop giving up after {turn.Limits.OuterRetries} outer retries (conversation={turn.ConversationId}, provider={who}): {detail}",
                        None
                    )

                    return Error(LoopStop.ProviderFatal err)
                | _ ->
                    logger.Error(
                        $"AI agent loop fatal provider error (conversation={turn.ConversationId}, provider={who}): {detail}",
                        None
                    )

                    return Error(LoopStop.ProviderFatal err)
        }

        return! attempt 0
    }

    /// Where a verdict left the loop once its effects ran.
    type private Acted =
        /// The model answered.
        | Answered of LoopState
        /// A tool round ran and settled; the loop goes on unless a rail fired.
        | Settled of LoopState
        /// The response stopped the loop.
        | Halted of LoopStop

    /// The effects of a verdict: the model's narration lands as its own
    /// message, a final answer is never committed empty, and a tool round
    /// runs its calls in parallel and settles.
    let private act (turn: TurnContext) (response: AIProviderResponse) (verdict: StepVerdict) = async {
        let complete () =
            turn.Emit(MessageComplete(turn.ConversationId, Guid.NewGuid()))

        match verdict with
        | Stop stop ->
            if not (String.IsNullOrWhiteSpace response.Content) then
                complete ()

            return Halted stop
        | Final state ->
            // An empty end of turn would commit an empty bubble; say why.
            if String.IsNullOrWhiteSpace response.Content then
                turn.Services.Logger.Warn
                    $"AI agent loop empty end_turn (conversation={turn.ConversationId}) — surfacing placeholder to client"

                turn.Emit(
                    MessageDelta(
                        turn.ConversationId,
                        "[The assistant produced no response. This can happen when the request is too vague, the context is too large, or the model hit an internal limit. Try rephrasing the question or breaking it into smaller steps.]"
                    )
                )

            complete ()
            return Answered state
        | ToolRound(state, calls) ->
            if not (String.IsNullOrWhiteSpace response.Content) then
                complete ()

            let! results = calls |> List.map (Dispatch.execute turn) |> Async.Parallel

            // The round is complete: each attached part goes to the stream
            // now, in tool-call order, before the model's next response.
            for _, _, _, parts in results do
                for part in parts do
                    turn.Emit(BlockAttached(turn.ConversationId, part))

            return Settled(settle turn state calls (List.ofArray results))
    }

    /// Run the loop from `messages` until the model answers or a stop ends
    /// it. A soft stop is explained in the chat and returns the state, with
    /// `Stopped` naming it; a hard stop is the error.
    let run (turn: TurnContext) (messages: AIProviderMessage list) : Async<Result<LoopState, LoopStop>> =
        let logger = turn.Services.Logger

        let who =
            $"{turn.Provider.Capabilities.ProviderName}/{turn.Provider.Capabilities.Model}"

        let endEarly (state: LoopState) (stop: LoopStop) =
            logger.Warn
                $"AI agent loop early stop (conversation={turn.ConversationId}, provider={who}): {LoopStop.message stop}"

            turn.Emit(MessageDelta(turn.ConversationId, LoopStop.earlyStopMessage stop))
            turn.Emit(MessageComplete(turn.ConversationId, Guid.NewGuid()))
            Ok { state with Stopped = Some stop }

        let cancelled () =
            logger.Info $"AI agent loop cancelled (conversation={turn.ConversationId}, provider={who})"
            turn.Emit(StreamCancelled turn.ConversationId)
            Error LoopStop.Cancelled

        let rec loop (state: LoopState) = async {
            if turn.Cancel.IsCancellationRequested then
                return cancelled ()
            elif state.Turn >= turn.Limits.MaxTurns then
                logger.Error(
                    $"AI agent loop max-turns ({turn.Limits.MaxTurns}) exceeded (conversation={turn.ConversationId}, provider={who})",
                    None
                )

                return Error(LoopStop.MaxTurns turn.Limits.MaxTurns)
            else
                let started = {
                    state with
                        Turn = state.Turn + 1
                        Timings = []
                }

                logger.Info
                    $"AI agent turn {started.Turn} (conversation={turn.ConversationId}, provider={who}, tools={turn.Tools.Length})"

                let sw = Stopwatch.StartNew()

                match! send turn state.Messages with
                | Error LoopStop.Cancelled -> return cancelled ()
                | Error stop -> return Error stop
                | Ok(response, ttftMs) ->
                    let! acted = act turn response (step started response)
                    sw.Stop()

                    let timings =
                        match acted with
                        | Settled next -> next.Timings
                        | Answered _
                        | Halted _ -> []

                    do! Telemetry.latency turn started.Turn response ttftMs timings sw.Elapsed.TotalMilliseconds

                    match acted with
                    | Answered next -> return Ok next
                    | Halted stop -> return endEarly started stop
                    | Settled next ->
                        match next.Stopped with
                        | Some stop -> return endEarly next stop
                        | None -> return! loop next
        }

        async {
            if not (List.isEmpty turn.Tools) && not turn.Provider.Capabilities.ToolUse then
                logger.Warn(
                    sprintf
                        "AI provider '%s' reports ToolUse = false but %d tool(s) are registered this turn. Tool calls will never fire (client-resident dispatch and fast-path inert); the model answers as if no tools exist. Configure a tool-capable provider/model if tool use is expected."
                        who
                        (List.length turn.Tools)
                )

            return! loop (LoopState.start messages)
        }

/// Run a turn: the triage intercept first, then the plan. A cheap opt-in
/// classifier gets one look before any frontier-model call; when it
/// resolves the turn (its action already published), the turn replies with
/// its recap. `None` from the resolver — not composed, not enabled, not
/// applicable — runs the loop unchanged (GP 11 / GP 13).
let run (turn: TurnContext) (plan: TurnPlan) (messages: AIProviderMessage list) : Async<Result<LoopState, LoopStop>> = async {
    match plan with
    | TurnPlan.Reply text -> return Ok(Loop.reply turn messages text)
    | TurnPlan.RunLoop ->
        let! triage =
            FastPathTriageResolver.tryTriage
                turn.Request
                turn.Provider
                turn.TaskId
                turn.ConversationId
                turn.ActiveModule
                turn.ActivePage
                messages

        match triage with
        | Some(FastPathTriageResolver.TriageResolved recap) -> return Ok(Loop.reply turn messages recap)
        | Some FastPathTriageResolver.TriageUnresolved
        | None -> return! Loop.run turn messages
}

/// The positional entry under a caller's budget, for callers that hold the
/// loop's result as messages: a hard stop is raised (`AgentLoopStopped`,
/// `AgentCallBudgetExceeded`, or `OperationCanceledException` for a cancel).
let runAgentLoopWithBudget
    (provider: IAIProvider)
    (registry: AIToolRegistry)
    (dispatchRegistry: ClientToolDispatch.ClientToolDispatchRegistry)
    (ctx: HttpContext)
    (taskId: Guid)
    (conversationId: Guid)
    (surface: AISurface)
    (activeModule: string option)
    (activePage: string option)
    (cancelToken: CancellationToken)
    (messages: AIProviderMessage list)
    (modelInput: ModelInput)
    (budget: AgentCallBudget)
    (onEvent: AIStreamEvent -> unit)
    : Async<AIProviderMessage list> =
    async {
        let turn =
            TurnContext.create
                provider
                registry
                dispatchRegistry
                ctx
                taskId
                conversationId
                surface
                activeModule
                activePage
                modelInput
                budget
                AgentLoopLimits.defaults
                onEvent
                cancelToken

        match! run turn TurnPlan.RunLoop messages with
        | Ok state -> return state.Messages
        | Error LoopStop.Cancelled -> return raise (OperationCanceledException(LoopStop.message LoopStop.Cancelled))
        | Error(LoopStop.CallBudgetExceeded(budget, elapsed, detail)) ->
            return raise (AgentCallBudgetExceeded(budget, elapsed, detail))
        | Error stop -> return raise (AgentLoopStopped stop)
    }

/// The positional entry under the interactive budget
/// (`AgentCallBudget.interactive`).
let runAgentLoopWithInput
    (provider: IAIProvider)
    (registry: AIToolRegistry)
    (dispatchRegistry: ClientToolDispatch.ClientToolDispatchRegistry)
    (ctx: HttpContext)
    (taskId: Guid)
    (conversationId: Guid)
    (surface: AISurface)
    (activeModule: string option)
    (activePage: string option)
    (cancelToken: CancellationToken)
    (messages: AIProviderMessage list)
    (modelInput: ModelInput)
    (onEvent: AIStreamEvent -> unit)
    : Async<AIProviderMessage list> =
    runAgentLoopWithBudget
        provider
        registry
        dispatchRegistry
        ctx
        taskId
        conversationId
        surface
        activeModule
        activePage
        cancelToken
        messages
        modelInput
        AgentCallBudget.interactive
        onEvent