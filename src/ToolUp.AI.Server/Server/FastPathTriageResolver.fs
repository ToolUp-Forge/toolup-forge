module ToolUp.AI.FastPathTriageResolver

open System
open System.Diagnostics
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Metrics
open ToolUp.AI

// ─── Phase 6j.B — Tier 3: small-model triage ─────────────────────
//
// The third tier of the fast-path ladder. Tier 1 (a declarative
// pattern match, client-side, microseconds) has already missed; the
// alternative is Tier 4, the full agent loop — 5–15 s and a frontier
// model's tokens — for an instruction whose whole content is "set
// country to UK". Tier 3 spends one cheap, tool-free,
// schema-constrained model call (~500 ms) deciding between the two:
// either the instruction maps onto exactly one declared field, or it
// does not and the full loop runs unchanged.
//
// ─── The three properties that make this safe ────────────────────
//
//   1. **Opt-in, and off by default.** No `FastPathTriageConfig` in
//      DI ⇒ nothing here executes, no metric series is allocated, no
//      event is written (GP 13). A composed config with
//      `Enabled = false` is the same. This is a team-level decision:
//      triage trades a small ongoing token cost for latency, and a
//      deployment that has not measured its Tier-1 miss rate should
//      not be paying it silently (GP 11).
//
//   2. **Fall-through is never an error.** Every failure mode —
//      unparseable output, a field id the surface does not declare,
//      confidence under the floor, a provider error, a timeout —
//      resolves to "run the full agent loop", which is exactly what
//      would have happened without triage. The user never sees a
//      triage failure; they see the ordinary answer, slightly later.
//      That is why the outcome vocabulary below is stratified: the
//      failures are indistinguishable to the user and must not be
//      indistinguishable to the operator.
//
//   3. **Biased hard towards the full agent.** The prompt instructs
//      the model to answer `needs_full_agent` whenever it is not
//      certain, and the plan stage independently re-checks the
//      proposed field against the declared set and the confidence
//      against the floor. This is the mitigation for the
//      inspect-regression risk (R1 in the phase plan): an instruction
//      that actually needed `inspect_active_module`-grade reasoning
//      about live module state must not be swallowed by a one-shot
//      field set. Cheap triage that is wrong is far worse than
//      expensive reasoning that is right.
//
// ─── Context continuity ──────────────────────────────────────────
//
// A resolved turn appends a synthetic assistant turn recapping what
// happened. The agent engine returns `messages @ [ synthetic ]`, so
// the ordinary persistence path in `AIAssistantHandler` writes the
// user instruction + the recap into BOTH the provider-history blob
// and the UI conversation blob — the same pair
// `FastPathBeaconHandler` writes for a Tier-1 resolution, reached
// through the existing path rather than a second one. The next
// complex turn therefore replays a history in which the field change
// is visible, so the model is not reasoning from a gap.

// ─── Telemetry vocabulary ────────────────────────────────────────
//
// Rides the existing `_platform.ai.fastpath` event source (the same
// source `/dev/ai-fastpath` already reads) under its own `EventType`,
// exactly as the Phase 6j.G sequencer beacons do. One event per
// ATTEMPT, carrying the outcome — not one event per outcome kind —
// so the attempt count is the row count and a rollup cannot
// double-count.

[<Literal>]
let FastPathSourceModule = "_platform.ai.fastpath"

[<Literal>]
let TriageEventType = "FastPathTriageAttempt"

/// Triage resolved the instruction to a single declared field and an
/// action was emitted. The full agent loop did NOT run.
[<Literal>]
let OutcomeHit = "hit"

/// The model itself judged the instruction to need the full agent.
/// The healthy majority of misses; distinct from every other token
/// below, all of which are triage failing rather than triage working.
[<Literal>]
let OutcomeNeedsFullAgent = "needs-full-agent"

/// The model's answer did not parse as the required shape.
[<Literal>]
let OutcomeUnparseable = "unparseable"

/// The model named a field the active surface does not declare.
[<Literal>]
let OutcomeUnknownField = "unknown-field"

/// The model's own confidence was under the configured floor.
[<Literal>]
let OutcomeLowConfidence = "low-confidence"

/// The model asked to CLEAR a field whose declared `ValueType` is not
/// optional. Kept separate from `unknown-field` because it points at a
/// declaration gap, not a hallucination.
[<Literal>]
let OutcomeClearUnsupported = "clear-unsupported"

/// The triage provider call failed or timed out.
[<Literal>]
let OutcomeProviderError = "provider-error"

/// Every outcome token, in the order a rollup should present them.
let outcomes: string list = [
    OutcomeHit
    OutcomeNeedsFullAgent
    OutcomeUnparseable
    OutcomeUnknownField
    OutcomeLowConfidence
    OutcomeClearUnsupported
    OutcomeProviderError
]

/// Outcomes that mean the turn continued into the full agent loop.
/// Everything that is not a hit — including the healthy
/// `needs-full-agent` — because the operator question this answers is
/// "what fraction of triage spend bought a resolution".
let fallThroughOutcomes: Set<string> =
    outcomes |> List.filter (fun o -> o <> OutcomeHit) |> Set.ofList

// ─── Configuration ───────────────────────────────────────────────

/// The action key the emitted `Notification.ModuleAction` carries.
/// Locked design decision: Tier 3 reuses the existing ModuleAction
/// infrastructure and the client `ActionDecoder` the Tier-1/Tier-2
/// paths already dispatch through. There is no Tier-3 wire shape.
[<Literal>]
let SetFieldActionKey = "_ui.set-field"

/// Triage opt-in. Absent from DI ⇒ no triage at all.
type FastPathTriageConfig = {
    /// Master switch. `false` is the same as not composing the config
    /// — kept as a field so a deployment can leave the registry and
    /// provider wiring in place while turning the tier off.
    Enabled: bool
    /// The declared-field seam. Required: triage cannot build a prompt
    /// without knowing what the surface exposes.
    Registry: IAIFieldRegistry
    /// The provider that serves the triage call. `None` ⇒ the turn's
    /// own provider — correct, but it pays the frontier model's price
    /// for a decision a cheap model makes just as well. Read the
    /// primary provider's `Capabilities.TriageModelId` and build a
    /// second instance at that model to populate this.
    TriageProvider: IAIProvider option
    /// Instructions longer than this are not triaged. A trivial UI
    /// instruction is short; a long one is prose, and prose is what
    /// the full agent loop is for. Bounds the prompt too.
    MaxInstructionChars: int
    /// Minimum model-reported confidence for a hit. Below it the turn
    /// falls through. Deliberately high by default — see property (3)
    /// above.
    ConfidenceFloor: float
    /// Wall-clock cap on the triage call, including any provider
    /// retry. Past it the turn falls through, so the ceiling on what
    /// triage can COST a missed turn is this value, not the provider
    /// default.
    TimeoutMs: int
}

module FastPathTriageConfig =
    /// The floor a hit must clear. High on purpose: a wrong hit
    /// mutates the user's screen and hides the instruction from the
    /// model that could have handled it properly, whereas a wrong miss
    /// costs one cheap call.
    [<Literal>]
    let DefaultConfidenceFloor = 0.85

    /// Instruction-length ceiling. "Set the country filter to United
    /// Kingdom and clear the date range" is ~60 chars; 240 is generous
    /// headroom without admitting a paragraph.
    [<Literal>]
    let DefaultMaxInstructionChars = 240

    /// Triage's whole value proposition is that it is fast. A call
    /// that has not answered in 3 s has already lost the argument
    /// against the full loop, so stop paying for it.
    [<Literal>]
    let DefaultTimeoutMs = 3_000

    /// Enabled config over a registry, with the tuned defaults and no
    /// separate triage provider (the turn's own provider serves).
    let create (registry: IAIFieldRegistry) : FastPathTriageConfig = {
        Enabled = true
        Registry = registry
        TriageProvider = None
        MaxInstructionChars = DefaultMaxInstructionChars
        ConfidenceFloor = DefaultConfidenceFloor
        TimeoutMs = DefaultTimeoutMs
    }

    /// Point triage at a cheap-model provider instance (typically one
    /// built at the primary provider's `Capabilities.TriageModelId`).
    let withTriageProvider (provider: IAIProvider) (config: FastPathTriageConfig) : FastPathTriageConfig = {
        config with
            TriageProvider = Some provider
    }

    /// Resolve the effective confidence floor. A nonsensical override
    /// (outside 0..1) falls back to the default rather than admitting
    /// every answer (`<= 0`) or none (`> 1`).
    let effectiveFloor (config: FastPathTriageConfig) : float =
        if config.ConfidenceFloor > 0.0 && config.ConfidenceFloor <= 1.0 then
            config.ConfidenceFloor
        else
            DefaultConfidenceFloor

    let effectiveMaxInstructionChars (config: FastPathTriageConfig) : int =
        if config.MaxInstructionChars > 0 then
            config.MaxInstructionChars
        else
            DefaultMaxInstructionChars

    let effectiveTimeoutMs (config: FastPathTriageConfig) : int =
        if config.TimeoutMs > 0 then
            config.TimeoutMs
        else
            DefaultTimeoutMs

// ─── Model contract ──────────────────────────────────────────────

/// JSON Schema handed to `IAIProvider.SendStructuredMessage`. Kept
/// deliberately tiny: four fields, one closed enum, no nesting. A
/// provider translating this to its native structured-output mode has
/// nothing to fail on, and the post-parse below stays total.
let triageSchema =
    """{"type":"object","additionalProperties":false,"properties":{"decision":{"type":"string","enum":["set_field","needs_full_agent"]},"fieldId":{"type":["string","null"]},"value":{"type":["string","null"]},"confidence":{"type":"number"},"reason":{"type":"string"}},"required":["decision","confidence"]}"""

/// The model's answer, after parsing and before validation against the
/// surface. `Value = None` is the CLEAR case (the model returned JSON
/// `null`), which is only honoured for an optional-typed field.
type TriageModelDecision = {
    Decision: string
    FieldId: string option
    Value: string option
    Confidence: float
    Reason: string
}

/// What the resolver decided to do, after checking the model's answer
/// against the declared surface.
type TriagePlan =
    /// Emit a `_ui.set-field` action for this declared field.
    /// `Value = None` clears an optional field.
    | TriageSetField of field: AIFieldDescriptor * value: string option * confidence: float
    /// Run the full agent loop. Carries the telemetry outcome token,
    /// not a user-facing message — the user sees the agent's answer,
    /// never this.
    | TriageFallThrough of outcome: string

// ─── Pure stages ─────────────────────────────────────────────────
//
// Everything below `jsonOptions` down to `syntheticReply` is pure and
// separately testable. The async orchestration at the bottom does IO
// and nothing else.

let private jsonOptions = FableConverters.create ()

let private toJson (value: obj) =
    JsonSerializer.Serialize(value, jsonOptions)

/// JSON string literal for `s`, escaping included.
let private jsonString (s: string) =
    JsonSerializer.Serialize((if isNull s then "" else s), jsonOptions)

/// Markers that an instruction is a QUESTION about the surface rather
/// than a command to change it. Questions are precisely the class that
/// needs live module state and real reasoning — the
/// `inspect_active_module` class — so they are refused before the
/// triage call rather than gambled on it. Matched case-insensitively
/// as a leading word (or anywhere, for `?`), so "set country to what
/// the report says" is not caught by the substring "what".
let private questionOpeners = [
    "what"
    "why"
    "how"
    "which"
    "who"
    "when"
    "where"
    "explain"
    "summarise"
    "summarize"
    "compare"
    "analyse"
    "analyze"
    "tell me"
    "show me"
    "describe"
]

/// Is this text a plausible trivial UI instruction at all? Cheap,
/// local, and applied BEFORE any model call — a turn refused here
/// costs nothing and is not counted as a triage attempt.
let isEligibleInstruction (maxChars: int) (instruction: string) : bool =
    if String.IsNullOrWhiteSpace instruction then
        false
    else
        let trimmed = instruction.Trim()

        if trimmed.Length > maxChars then
            false
        elif trimmed.Contains "?" then
            false
        else
            let lowered = trimmed.ToLowerInvariant()

            questionOpeners
            |> List.exists (fun (opener: string) -> lowered.StartsWith opener)
            |> not

/// The last user instruction in the replayed history — the text this
/// turn is about. `None` when the tail is not a plain user turn (a
/// tool-result carrier, an assistant turn, an empty history), which is
/// every shape triage has no business looking at.
let lastUserInstruction (messages: AIProviderMessage list) : string option =
    match List.tryLast messages with
    | Some m when
        m.Role = "user"
        && List.isEmpty m.ToolResults
        && not (String.IsNullOrWhiteSpace m.Content)
        ->
        Some(m.Content.Trim())
    | _ -> None

/// Cap on the state summary interpolated into the prompt. The
/// implementation owns the summary's content; the resolver owns its
/// cost.
[<Literal>]
let private MaxStateSummaryChars = 2000

/// Build the triage system prompt from the declared surface. The
/// declared instruction patterns are the exemplars — the SAME
/// declarations Tier 1 pattern-matches on, so the two tiers cannot
/// drift apart in what they believe a field is called.
let buildTriagePrompt (snapshot: AIFieldSnapshot) : string =
    let fieldLines =
        snapshot.Fields
        |> List.map (fun f ->
            let patterns =
                if List.isEmpty f.InstructionPatterns then
                    ""
                else
                    "\n    phrasings: " + (f.InstructionPatterns |> String.concat " | ")

            let aliases =
                if List.isEmpty f.ValueAliases then
                    ""
                else
                    "\n    value aliases: "
                    + (f.ValueAliases |> List.map (fun (a, c) -> $"{a}->{c}") |> String.concat ", ")

            $"  - id: {f.FieldId}\n    type: {f.ValueType}\n    purpose: {f.Description}{patterns}{aliases}")
        |> String.concat "\n"

    let stateSummary =
        let s =
            if isNull snapshot.StateSummary then
                ""
            else
                snapshot.StateSummary

        if s.Length > MaxStateSummaryChars then
            s.Substring(0, MaxStateSummaryChars) + "…"
        else
            s

    let pageLabel = defaultArg snapshot.Page "(single page)"

    String.concat "\n" [
        "You are a triage classifier for a user interface. You do not answer questions and you do not"
        "perform analysis. Your ONLY job is to decide whether the user's instruction is a trivial request"
        "to set exactly one declared field on the screen in front of them."
        ""
        $"Active module: {snapshot.ModuleId}"
        $"Active page: {pageLabel}"
        $"Current state: {stateSummary}"
        ""
        "Declared fields (you may name NO other id):"
        fieldLines
        ""
        "Answer with `set_field` ONLY when ALL of these hold:"
        "  * the instruction names exactly one of the declared fields above, and"
        "  * the value to set is stated plainly in the instruction, and"
        "  * carrying it out requires no reasoning about data, no calculation, no lookup, and no"
        "    inspection of anything not shown in `Current state` above."
        ""
        "Answer `needs_full_agent` for EVERYTHING else, and in particular whenever you are not certain."
        "A wrong `set_field` changes the user's screen incorrectly and hides their request from the"
        "assistant that could have handled it properly; a `needs_full_agent` you did not have to give"
        "costs almost nothing. When the two are close, choose `needs_full_agent`."
        ""
        "To CLEAR a field, answer `set_field` with a null value — but only for an optional-typed field,"
        "one whose declared type name ends in `-option` (such as `string-option` or `enum-option`)."
        "Any other field's value must be a non-empty string."
        ""
        "`confidence` is your own probability, 0 to 1, that a `set_field` answer is exactly what the"
        "user asked for. Report it honestly; a well-calibrated low number is more useful than a"
        "confident guess."
    ]

/// Parse the model's answer. Total: any shape that is not the
/// contract returns `None`, and the caller records `unparseable`.
/// Deliberately lenient about *casing* and about a fenced code block,
/// because those are the two ways a model that got the content right
/// gets the envelope wrong.
let parseTriageDecision (content: string) : TriageModelDecision option =
    if String.IsNullOrWhiteSpace content then
        None
    else
        let stripped =
            let t = content.Trim()

            if t.StartsWith "```" then
                // ```json\n{...}\n``` — drop the fence lines.
                let lines =
                    t.Split('\n') |> Array.filter (fun l -> not (l.TrimStart().StartsWith "```"))

                String.Join("\n", lines).Trim()
            else
                t

        try
            use doc = JsonDocument.Parse stripped
            let root = doc.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                None
            else
                let tryProp (name: string) =
                    match root.TryGetProperty name with
                    | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
                    | _ -> None

                let decision =
                    tryProp "decision"
                    |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
                    |> Option.map (fun v -> v.GetString().Trim().ToLowerInvariant())

                match decision with
                | None -> None
                | Some d ->
                    let confidence =
                        tryProp "confidence"
                        |> Option.bind (fun v ->
                            match v.ValueKind with
                            | JsonValueKind.Number ->
                                match v.TryGetDouble() with
                                | true, n -> Some n
                                | _ -> None
                            | _ -> None)
                        // A missing confidence is treated as zero, not as
                        // certainty: the schema requires it, so its absence
                        // means the provider did not honour the schema, and
                        // that is not a moment to trust a hit.
                        |> Option.defaultValue 0.0

                    Some {
                        Decision = d
                        FieldId =
                            tryProp "fieldId"
                            |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
                            |> Option.map (fun v -> v.GetString())
                        Value =
                            tryProp "value"
                            |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
                            |> Option.map (fun v -> v.GetString())
                        Confidence = confidence
                        Reason =
                            tryProp "reason"
                            |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
                            |> Option.map (fun v -> v.GetString())
                            |> Option.defaultValue ""
                    }
        with _ ->
            None

/// Validate the model's answer against the declared surface and the
/// configured floor. This is the second half of the
/// bias-towards-the-agent property: the prompt asks the model to be
/// cautious, and this stage does not take its word for it.
///
/// Pure, and the single decision point — the orchestration below emits
/// an action if and only if this returns `TriageSetField`, so the
/// action, the synthetic turn, and the `hit` telemetry row cannot
/// disagree about whether triage resolved.
let planTriage (config: FastPathTriageConfig) (snapshot: AIFieldSnapshot) (decision: TriageModelDecision) : TriagePlan =
    if decision.Decision <> "set_field" then
        TriageFallThrough OutcomeNeedsFullAgent
    elif decision.Confidence < FastPathTriageConfig.effectiveFloor config then
        TriageFallThrough OutcomeLowConfidence
    else
        match
            decision.FieldId
            |> Option.bind (fun id -> AIFieldSnapshot.tryFindField id snapshot)
        with
        | None -> TriageFallThrough OutcomeUnknownField
        | Some field ->
            match decision.Value with
            | None
            | Some "" ->
                // Clearing. Only an optional-typed field can be cleared — a
                // value type whose name ends in `-option` (`string-option`,
                // `enum-option`, or any future `X-option` convention). A clear
                // sets the field to its empty state; the field's own decoder
                // stays the final arbiter of whether that empty value is legal,
                // so the resolver only decides whether the clear is honoured or
                // handed to the agent. Asking to clear a non-optional field is a
                // declaration gap or a misread instruction, and either way the
                // agent should see it.
                let valueType =
                    if isNull field.ValueType then
                        ""
                    else
                        field.ValueType.Trim().ToLowerInvariant()

                if valueType.EndsWith("-option", StringComparison.Ordinal) then
                    TriageSetField(field, None, decision.Confidence)
                else
                    TriageFallThrough OutcomeClearUnsupported
            | Some raw -> TriageSetField(field, Some(AIFieldDescriptor.resolveAlias field raw), decision.Confidence)

/// The `Notification.ModuleAction` payload. Mirrors the field/value
/// shape the Tier-1 beacon records so a history reader sees one
/// consistent record across tiers; `source` distinguishes which tier
/// produced it, which is what makes cross-tier consistency analysis
/// possible at all.
let actionPayloadJson (field: AIFieldDescriptor) (value: string option) : string =
    let encoded =
        match value with
        | None -> "null"
        | Some v -> jsonString v

    $"""{{"field":{jsonString field.FieldId},"value":{encoded},"source":"triage"}}"""

/// The recap appended to both conversation blobs. Written for the
/// NEXT turn's model as much as for the user: it states the field, the
/// value, and that no agent turn ran, so a follow-up ("now do the same
/// for France") is not reasoning from a gap.
let syntheticReply (snapshot: AIFieldSnapshot) (field: AIFieldDescriptor) (value: string option) : string =
    match value with
    | None -> $"Cleared **{field.FieldId}** on {snapshot.ModuleId}."
    | Some v -> $"Set **{field.FieldId}** to `{v}` on {snapshot.ModuleId}."

// ─── Telemetry ───────────────────────────────────────────────────

/// One row per triage ATTEMPT. `Outcome` is the stratification; the
/// row exists whether triage resolved or fell through, so
/// hits / attempts is computable and cannot be inflated by a tier that
/// only reports its successes.
type TriageEventPayload = {
    ConversationId: Guid
    TaskId: Guid
    ModuleId: string
    Outcome: string
    /// The resolved field on a hit; `""` on every fall-through
    /// (including `unknown-field`, where the model's proposed id is
    /// deliberately NOT echoed — it is unvalidated model output and
    /// this payload is read back by a dev endpoint).
    FieldId: string
    ProviderName: string
    ProviderModel: string
    /// The provider's declared cheap-model id, when it declares one.
    /// Recorded so an operator can tell a triage served by a genuinely
    /// cheap model from one quietly served by the frontier model.
    TriageModelId: string
    /// Wall-clock for the whole attempt, provider call included.
    LatencyMs: float
    /// Length of the instruction, for tuning `MaxInstructionChars`.
    /// The instruction TEXT is not recorded here — the Tier-1 beacon
    /// records instructions because the client already showed them;
    /// this row is emitted server-side on every attempt including the
    /// ones that fall through to a full agent turn, and it is not the
    /// place to accumulate a second copy of user prose.
    InstructionChars: int
}

/// Metric names, registered in `AILatencyMetrics.registrations` so the
/// sink pre-allocates the series.
[<Literal>]
let TriageAttemptsMetric = "toolup.ai.triage.attempts"

[<Literal>]
let TriageOutcomesMetric = "toolup.ai.triage.outcomes"

[<Literal>]
let TriageDurationMsMetric = "toolup.ai.triage.duration.ms"

// ─── Orchestration ───────────────────────────────────────────────

let private resolveLogger (ctx: HttpContext) : ILogger =
    match ctx.RequestServices.GetService(typeof<ILogger>) with
    | :? ILogger as l -> l
    | _ ->
        { new ILogger with
            member _.Debug _ = ()
            member _.Info _ = ()
            member _.Warn _ = ()
            member _.Error(_, _) = ()
        }

let private tryResolve<'T when 'T: not struct> (ctx: HttpContext) : 'T option =
    match ctx.RequestServices.GetService(typeof<'T>) with
    | :? 'T as s -> Some s
    | _ -> None

/// Mirrors `AIAgentEngine.resolveLatencyScope` so triage rows land in
/// the same scope the beacon rows and the latency rows use — a rollup
/// that read a different scope would silently report nothing.
let private resolveScope (ctx: HttpContext) : StorageScope =
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

/// Write the attempt row + the three metric emissions. Best-effort in
/// both directions: telemetry never crashes and never blocks the chat
/// path, but a wedged backend logs at Warn rather than developing
/// silent holes in the tier's own measurements.
let private emitTelemetry (ctx: HttpContext) (payload: TriageEventPayload) : Async<unit> = async {
    let logger = resolveLogger ctx

    match tryResolve<IEventStore> ctx with
    | None -> ()
    | Some store ->
        let evt: ModuleEvent = {
            Id = Guid.NewGuid()
            OccurredAt = DateTime.UtcNow
            ScopeId = (resolveScope ctx).ScopeId
            SourceModule = FastPathSourceModule
            EventType = TriageEventType
            Payload = toJson payload
        }

        try
            do! store.Write evt
        with ex ->
            logger.Warn
                $"FastPath triage telemetry write failed (conversation={payload.ConversationId}, outcome={payload.Outcome}): {ex.Message}. Row dropped; conversation unaffected."

    match tryResolve<IMetricsSink> ctx with
    | None -> ()
    | Some sink ->
        try
            let baseTags =
                Map.ofList [ "provider", payload.ProviderName; "model", payload.ProviderModel ]

            sink.Increment(TriageAttemptsMetric, baseTags)
            sink.Increment(TriageOutcomesMetric, baseTags |> Map.add "outcome" payload.Outcome)
            sink.Record(TriageDurationMsMetric, payload.LatencyMs, baseTags)
        with ex ->
            logger.Warn $"FastPath triage IMetricsSink emission failed: {ex.Message}. Metrics dropped."
}

/// What the agent engine gets back.
type TriageResult =
    /// Triage resolved: the action has been emitted and this is the
    /// recap to stream + append. The full agent loop must NOT run.
    | TriageResolved of reply: string
    /// Triage ran and did not resolve. The full agent loop runs, as it
    /// would have without triage.
    | TriageUnresolved

/// The seam the agent engine calls before its first provider turn.
///
/// `None` ⇒ triage did not run AT ALL (not composed, disabled, no
/// registry entry for the active surface, provider cannot triage, the
/// tail of the history is not a plain user instruction, or the
/// instruction is not the trivial-command shape). Nothing is emitted
/// and no attempt row is written — a turn that was never a triage
/// candidate must not appear in the tier's denominator.
///
/// `Some TriageUnresolved` ⇒ triage ran, spent its call, and fell
/// through. One attempt row, one of the fall-through outcomes.
///
/// `Some (TriageResolved reply)` ⇒ resolved. The `_ui.set-field`
/// action is already published to the caller's notification stream.
let tryTriage
    (ctx: HttpContext)
    (turnProvider: IAIProvider)
    (taskId: Guid)
    (conversationId: Guid)
    (activeModule: string option)
    (activePage: string option)
    (messages: AIProviderMessage list)
    : Async<TriageResult option> =
    async {
        match tryResolve<FastPathTriageConfig> ctx, activeModule with
        | None, _ -> return None
        | Some config, _ when not config.Enabled -> return None
        | _, None -> return None
        | Some config, Some moduleId ->
            let provider = config.TriageProvider |> Option.defaultValue turnProvider

            if not provider.Capabilities.SupportsTriage then
                return None
            else
                let maxChars = FastPathTriageConfig.effectiveMaxInstructionChars config

                match lastUserInstruction messages with
                | None -> return None
                | Some instruction when not (isEligibleInstruction maxChars instruction) -> return None
                | Some instruction ->
                    let logger = resolveLogger ctx

                    // A registry that throws is a defect in the
                    // companion, not a reason to fail the user's turn —
                    // but it is also not a triage attempt, so it is
                    // logged and the turn proceeds as if triage were
                    // absent.
                    let! snapshotOpt = async {
                        try
                            return! config.Registry.Describe(moduleId, activePage)
                        with ex ->
                            logger.Warn
                                $"IAIFieldRegistry.Describe threw for module '{moduleId}' (conversation={conversationId}): {ex.Message}. Triage skipped; the full agent loop runs."

                            return None
                    }

                    match snapshotOpt with
                    | None -> return None
                    | Some snapshot when List.isEmpty snapshot.Fields -> return None
                    | Some snapshot ->
                        let sw = Stopwatch.StartNew()

                        let policy = {
                            RetryPolicy.defaults with
                                // One attempt. A triage call that failed
                                // has already lost its latency argument;
                                // retrying spends more to arrive at the
                                // same fall-through.
                                MaxAttempts = 1
                                Timeout =
                                    Some(
                                        TimeSpan.FromMilliseconds(
                                            float (FastPathTriageConfig.effectiveTimeoutMs config)
                                        )
                                    )
                        }

                        let triageMessages = [ AIProviderMessage.text "user" instruction ]

                        let! response =
                            provider.SendStructuredMessage(
                                triageMessages,
                                [],
                                Some(buildTriagePrompt snapshot),
                                triageSchema,
                                policy
                            )

                        let plan =
                            match response with
                            | Error err ->
                                logger.Warn
                                    $"FastPath triage provider call failed (conversation={conversationId}, provider={provider.Capabilities.ProviderName}/{provider.Capabilities.Model}): {AIProviderError.toMessage err}. Falling through to the full agent loop."

                                TriageFallThrough OutcomeProviderError
                            | Ok r ->
                                match parseTriageDecision r.Content with
                                | None -> TriageFallThrough OutcomeUnparseable
                                | Some decision -> planTriage config snapshot decision

                        sw.Stop()

                        let outcome, fieldId =
                            match plan with
                            | TriageSetField(field, _, _) -> OutcomeHit, field.FieldId
                            | TriageFallThrough o -> o, ""

                        let payload: TriageEventPayload = {
                            ConversationId = conversationId
                            TaskId = taskId
                            ModuleId = moduleId
                            Outcome = outcome
                            FieldId = fieldId
                            ProviderName = provider.Capabilities.ProviderName
                            ProviderModel = provider.Capabilities.Model
                            TriageModelId = defaultArg provider.Capabilities.TriageModelId ""
                            LatencyMs = sw.Elapsed.TotalMilliseconds
                            InstructionChars = instruction.Length
                        }

                        do! emitTelemetry ctx payload

                        match plan with
                        | TriageFallThrough _ -> return Some TriageUnresolved
                        | TriageSetField(field, value, confidence) ->
                            // Locked design decision: the existing
                            // ModuleAction infrastructure carries this.
                            // `emitAction` is best-effort by contract —
                            // it silently no-ops without a notification
                            // channel or a resolved user id.
                            do! ToolContext.emitAction ctx moduleId SetFieldActionKey (actionPayloadJson field value)

                            let elapsed = sprintf "%.0f" sw.Elapsed.TotalMilliseconds
                            let confidenceText = sprintf "%.2f" confidence

                            logger.Info
                                $"FastPath triage resolved in {elapsed}ms (conversation={conversationId}, module={moduleId}, field={field.FieldId}, confidence={confidenceText}) — full agent loop skipped"

                            return Some(TriageResolved(syntheticReply snapshot field value))
    }