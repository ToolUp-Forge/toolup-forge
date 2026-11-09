module ToolUp.AI.FastPathTriageResolver

open ToolUp.Platform.Transport
open System
open System.Diagnostics
open System.Text.Json
open Microsoft.AspNetCore.Http
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Metrics
open ToolUp.AI
open ToolUp.Platform.Infra
open ToolUp.Platform.Infra.Metrics
open ToolUp.Platform.Compose

// ─── Tier 3: small-model triage ─────────────────────
//
// The third tier of the fast-path ladder. Tier 1 (a declarative
// pattern match, client-side, microseconds) has already missed; the
// alternative is Tier 4, the full agent loop — 5–15 s and a frontier
// model's tokens — for an instruction whose whole content is "set
// country to UK". Tier 3 spends one cheap, tool-free,
// schema-constrained model call (~500 ms) deciding between the two:
// either the instruction maps onto exactly one declared field (or,
// an ordered list of them, or one declared navigation
// target), or it does not and the full loop runs unchanged.
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
// exactly as the sequencer beacons do. One event per
// ATTEMPT, carrying the outcome — not one event per outcome kind —
// so the attempt count is the row count and a rollup cannot
// double-count.

[<Literal>]
let FastPathSourceModule = "_platform.ai.fastpath"

[<Literal>]
let TriageEventType = "FastPathTriageAttempt"

/// How one triage attempt ended. `Token` is the telemetry string a row and a
/// metric tag carry.
[<RequireQualifiedAccess>]
type TriageOutcome =
    /// Triage resolved the instruction — one declared field, an ordered list
    /// of them, or a declared navigation target (the row's `VerdictShape`
    /// says which) — and the action(s) were emitted. The full agent loop did
    /// NOT run.
    | Hit
    /// The model itself judged the instruction to need the full agent. The
    /// healthy majority of misses; distinct from every other case, all of
    /// which are triage failing rather than triage working.
    | NeedsFullAgent
    /// The model's answer did not parse as the required shape.
    | Unparseable
    /// The model named a field the active surface does not declare.
    | UnknownField
    /// The model's own confidence was under the configured floor.
    | LowConfidence
    /// The model asked to CLEAR a field whose declared `ValueType` is not
    /// optional. Kept separate from `UnknownField` because it points at a
    /// declaration gap, not a hallucination.
    | ClearUnsupported
    /// The triage provider call failed — a call that CAME BACK and said no,
    /// not one that never came back inside the configured budget.
    | ProviderError
    /// The triage call did not complete within `FastPathTriageConfig`'s
    /// configured `TimeoutMs`: the resolver's OWN wall-clock guarantee
    /// firing, the property that bounds what triage can cost a turn whether
    /// or not the provider honours cancellation. Paired with
    /// `TimeoutBudgetMs` on the telemetry row so a dashboard can tell a
    /// near-miss from a hang.
    | Timeout

    member this.Token =
        match this with
        | Hit -> "hit"
        | NeedsFullAgent -> "needs-full-agent"
        | Unparseable -> "unparseable"
        | UnknownField -> "unknown-field"
        | LowConfidence -> "low-confidence"
        | ClearUnsupported -> "clear-unsupported"
        | ProviderError -> "provider-error"
        | Timeout -> "timeout"

module TriageOutcome =
    /// Every outcome, in the order a rollup presents them.
    let all: TriageOutcome list = [
        TriageOutcome.Hit
        TriageOutcome.NeedsFullAgent
        TriageOutcome.Unparseable
        TriageOutcome.UnknownField
        TriageOutcome.LowConfidence
        TriageOutcome.ClearUnsupported
        TriageOutcome.ProviderError
        TriageOutcome.Timeout
    ]

    /// The outcome a telemetry token names; `None` for a token this
    /// vocabulary does not hold.
    let ofToken (token: string) : TriageOutcome option =
        all |> List.tryFind (fun o -> o.Token = token)

    /// The turn continued into the full agent loop: everything but a hit,
    /// including the healthy `NeedsFullAgent`, because the question this
    /// answers is "what fraction of triage spend bought a resolution".
    let isFallThrough (outcome: TriageOutcome) : bool = outcome <> TriageOutcome.Hit

/// Which path the triage call took to a model.
[<RequireQualifiedAccess>]
type TriageRoute =
    /// `config.TriageProvider` served — the explicit escape hatch.
    | TriageProvider
    /// The turn provider served on its configured model, no override
    /// requested.
    | TurnProvider
    /// The turn provider served through the per-call model override at its
    /// declared `TriageModelId` (also recorded when the override call FAILED
    /// before the provider could say whether it honoured the id).
    | Override
    /// The override was requested and the provider served its configured
    /// model instead.
    | OverrideFallback
    /// The override path reported serving the configured model.
    | Configured

    member this.Token =
        match this with
        | TriageProvider -> "triage-provider"
        | TurnProvider -> "turn-provider"
        | Override -> "override"
        | OverrideFallback -> "override-fallback"
        | Configured -> "configured"

module TriageRoute =
    /// The route the per-call override reported.
    let ofOverride (outcome: ModelOverrideOutcome) : TriageRoute =
        match outcome with
        | ConfiguredModel _ -> TriageRoute.Configured
        | OverrideHonoured _ -> TriageRoute.Override
        | OverrideFellBack _ -> TriageRoute.OverrideFallback

// ─── Configuration ───────────────────────────────────────────────

/// The action key the emitted `Notification.ModuleAction` carries.
/// Locked design decision: Tier 3 reuses the existing ModuleAction
/// infrastructure and the client `ActionDecoder` the Tier-1/Tier-2
/// paths already dispatch through. There is no Tier-3 wire shape.
[<Literal>]
let SetFieldActionKey = "_ui.set-field"

/// The action key a `navigate` verdict's
/// `Notification.ModuleAction` carries, addressed to the active module
/// exactly as `_ui.set-field` is, with the payload
/// `{"target":…,"source":"triage"}`. Only ever emitted on a surface that
/// DECLARES navigation (see `NavigationFieldId`), which is the owning
/// companion's statement that its `ActionDecoder` handles this key — a
/// surface that declares no navigation never receives one.
[<Literal>]
let NavigateActionKey = "_ui.navigate"

/// The reserved `AIFieldDescriptor.FieldId` through which a
/// surface declares its navigation targets. The same `_navigation`
/// token the Tier-1 beacon already records for a navigation resolution
/// (`FastPathBeaconHandler`), so the two tiers name navigation alike.
/// The descriptor reuses the existing seam rather than widening
/// `AIFieldSnapshot`: `Description` says what navigating means on this
/// surface, `InstructionPatterns` are its phrasings, and `ValueAliases`
/// map spoken names onto the canonical target ids the decoder accepts
/// (a target folds through them exactly as a field value does, and the
/// module's decoder stays the arbiter of which targets exist). It is
/// never a settable field: `set_field` / `set_fields` naming it is
/// refused as `unknown-field`, and it is listed to the model in its own
/// prompt section rather than among the declared fields.
[<Literal>]
let NavigationFieldId = "_navigation"

/// Triage opt-in. Absent from DI ⇒ no triage at all.
type FastPathTriageConfig = {
    /// Master switch. `false` is the same as not composing the config
    /// — kept as a field so a deployment can leave the registry and
    /// provider wiring in place while turning the tier off.
    Enabled: bool
    /// The declared-field seam. Required: triage cannot build a prompt
    /// without knowing what the surface exposes.
    Registry: IAIFieldRegistry
    /// The provider that serves the triage call — the explicit escape
    /// hatch, and it wins outright when set. `None` ⇒ the turn's own
    /// provider serves, and it serves at its declared
    /// `Capabilities.TriageModelId` through the per-call model
    /// override, so the cheap tier is reached with nothing wired here.
    /// Populate this only to route triage somewhere the turn provider's
    /// own family cannot reach (another vendor, a dedicated key).
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

/// Upper bound on the clauses of one `set_fields` verdict. An
/// instruction short enough to be eligible cannot plausibly name more;
/// a longer list is a model misbehaving, and is refused whole rather
/// than truncated.
[<Literal>]
let MaxFieldClauses = 8

/// One clause of a `set_fields` verdict. `Value = None` clears, under
/// the same optional-typed-field rule as a single `set_field`.
type TriageFieldClause = {
    /// The field id as the model gave it; validated against the surface
    /// by `planTriage`, exactly as a single `set_field`'s is.
    FieldId: string
    /// The value to set, or `None` to clear an optional-typed field.
    Value: string option
}

/// Which grammar shape an action verdict took, recorded on every attempt
/// row whose answer parsed as one — so an absorption dashboard can attribute
/// hits (and whole-verdict rejections) to the single-field grammar or to its
/// two extensions.
[<RequireQualifiedAccess>]
type VerdictShape =
    /// A `set_field` verdict.
    | Single
    /// A `set_fields` verdict.
    | Multi
    /// A `navigate` verdict.
    | Navigate

    member this.Token =
        match this with
        | Single -> "single"
        | Multi -> "multi"
        | Navigate -> "navigate"

module VerdictShape =
    /// Every shape, in presentation order.
    let all: VerdictShape list = [ VerdictShape.Single; VerdictShape.Multi; VerdictShape.Navigate ]

    /// The shape a telemetry token names; `None` for `""` and any token this
    /// vocabulary does not hold.
    let ofToken (token: string) : VerdictShape option =
        all |> List.tryFind (fun s -> s.Token = token)

// The verdict tokens of the answer grammar: the `decision` strings the model
// answers with and the schema enumerates. Read only through `Token` and the
// parser below.
module private VerdictToken =
    [<Literal>]
    let SetField = "set_field"

    [<Literal>]
    let SetFields = "set_fields"

    [<Literal>]
    let Navigate = "navigate"

    [<Literal>]
    let NeedsFullAgent = "needs_full_agent"

/// The model's verdict, the four of the answer grammar. Each carries exactly
/// its own members, so a verdict carrying another verdict's members (a
/// "mixed" verdict) cannot be built.
[<RequireQualifiedAccess>]
type TriageVerdict =
    /// Set one field. `fieldId` is the id as the model gave it (`None` when
    /// it gave none), `value = None` the CLEAR case.
    | SetField of fieldId: string option * value: string option
    /// An ordered list of two or more field sets, applied all-or-nothing.
    | SetFields of clauses: TriageFieldClause list
    /// Go to one target of the surface's declared navigation.
    | Navigate of target: string
    /// The model's own decline: the full agent loop should handle this.
    | NeedsFullAgent

    member this.Token =
        match this with
        | SetField _ -> VerdictToken.SetField
        | SetFields _ -> VerdictToken.SetFields
        | Navigate _ -> VerdictToken.Navigate
        | NeedsFullAgent -> VerdictToken.NeedsFullAgent

module TriageVerdict =
    /// The grammar shape of an action verdict; `None` for `NeedsFullAgent`.
    let shape (verdict: TriageVerdict) : VerdictShape option =
        match verdict with
        | TriageVerdict.SetField _ -> Some VerdictShape.Single
        | TriageVerdict.SetFields _ -> Some VerdictShape.Multi
        | TriageVerdict.Navigate _ -> Some VerdictShape.Navigate
        | TriageVerdict.NeedsFullAgent -> None

    /// The one consistency rule, read by the parser when it builds a verdict
    /// and by the planner for a verdict built anywhere else (a cached
    /// calibration answer, a test): a list has two to `MaxFieldClauses`
    /// clauses, and a navigation names a target.
    let wellFormed (verdict: TriageVerdict) : bool =
        match verdict with
        | TriageVerdict.SetFields clauses ->
            not (isNull (box clauses))
            && clauses.Length >= 2
            && clauses.Length <= MaxFieldClauses
        | TriageVerdict.Navigate target -> not (String.IsNullOrWhiteSpace target)
        | TriageVerdict.SetField _
        | TriageVerdict.NeedsFullAgent -> true

/// JSON Schema handed to `IAIProvider.SendStructuredMessage`. Kept
/// deliberately small: one closed enum, one shallow list, nothing a
/// provider's native structured-output mode can fail on — and the
/// post-parse below stays total and strict whatever the provider does
/// with the schema.
let triageSchema =
    let verdicts =
        [
            VerdictToken.SetField
            VerdictToken.SetFields
            VerdictToken.Navigate
            VerdictToken.NeedsFullAgent
        ]
        |> List.map (fun t -> $"\"{t}\"")
        |> String.concat ","

    """{"type":"object","additionalProperties":false,"properties":{"decision":{"type":"string","enum":[VERDICTS]},"fieldId":{"type":["string","null"]},"value":{"type":["string","null"]},"fields":{"type":["array","null"],"items":{"type":"object","additionalProperties":false,"properties":{"fieldId":{"type":"string"},"value":{"type":["string","null"]}},"required":["fieldId","value"]}},"target":{"type":["string","null"]},"confidence":{"type":"number"},"reason":{"type":"string"}},"required":["decision","confidence"]}"""
        .Replace("VERDICTS", verdicts)

/// The model's answer, after parsing and before validation against the
/// surface.
type TriageModelDecision = {
    Decision: TriageVerdict
    Confidence: float
    Reason: string
}

/// What the resolver decided to do, after checking the model's answer
/// against the declared surface.
type TriagePlan =
    /// Emit a `_ui.set-field` action for this declared field.
    /// `Value = None` clears an optional field.
    | TriageSetField of field: AIFieldDescriptor * value: string option * confidence: float
    /// Emit one `_ui.set-field` action per clause, in order. Only ever built
    /// when EVERY clause validated: there is no partial plan (partial
    /// resolvability stays a Tier-1 sequencer capability).
    | TriageSetFields of clauses: (AIFieldDescriptor * string option) list * confidence: float
    /// Emit one `_ui.navigate` action for this target, on a surface whose
    /// declared `_navigation` descriptor is carried here.
    | TriageNavigate of navigation: AIFieldDescriptor * target: string * confidence: float
    /// Run the full agent loop. Carries the telemetry outcome, not a
    /// user-facing message — the user sees the agent's answer, never this.
    | TriageFallThrough of outcome: TriageOutcome

// ─── Pure stages ─────────────────────────────────────────────────
//
// Everything below `jsonOptions` down to `syntheticReply` is pure and
// separately testable. The async orchestration at the bottom does IO
// and nothing else.

let private jsonOptions = PlatformJson.options

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

/// Which surface signal marked an instruction as a question.
[<RequireQualifiedAccess>]
type ReadSignal =
    /// The instruction contains a `?`.
    | QuestionMark
    /// The instruction opens with a question word (`questionOpeners`).
    | QuestionOpener

    member this.Token =
        match this with
        | QuestionMark -> "question-mark"
        | QuestionOpener -> "question-opener"

/// Whether an instruction is a plausible trivial UI instruction at all, and
/// if not, why. Cheap, local, and decided BEFORE any model call — a turn
/// refused here costs nothing and is not counted as a triage attempt.
[<RequireQualifiedAccess>]
type Eligibility =
    | Eligible
    /// Empty or whitespace.
    | Empty
    /// Longer than the configured `MaxInstructionChars`.
    | TooLong
    /// A QUESTION about the surface rather than a command to change it —
    /// the read-path class this tier deliberately never serves. One token
    /// covers both surface signals, because they are the same refusal for
    /// the same reason; `signal` says which fired.
    | ReadShaped of signal: ReadSignal

    member this.Token =
        match this with
        | Eligible -> "eligible"
        | Empty -> "empty"
        | TooLong -> "too-long"
        | ReadShaped _ -> "read-shaped"

module Eligibility =
    /// Assess `instruction` against the pre-filter, in order: empty, too
    /// long, a `?`, a leading question word.
    let assess (maxChars: int) (instruction: string) : Eligibility =
        if String.IsNullOrWhiteSpace instruction then
            Eligibility.Empty
        else
            let trimmed = instruction.Trim()

            if trimmed.Length > maxChars then
                Eligibility.TooLong
            elif trimmed.Contains "?" then
                Eligibility.ReadShaped ReadSignal.QuestionMark
            else
                let lowered = trimmed.ToLowerInvariant()

                if
                    questionOpeners
                    |> List.exists (fun (opener: string) -> lowered.StartsWith opener)
                then
                    Eligibility.ReadShaped ReadSignal.QuestionOpener
                else
                    Eligibility.Eligible

    /// The instruction may be triaged.
    let isEligible (eligibility: Eligibility) : bool = eligibility = Eligibility.Eligible

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

/// A fixed few-shot pack teaching the OUTPUT GRAMMAR, not
/// this snapshot's fields. Each pair is an instruction and the exact
/// JSON verdict `parseTriageDecision` accepts for it, so the pack is a
/// contract with the parser as much as with the model: every entry
/// here is asserted, verbatim, to round-trip through
/// `parseTriageDecision` in `FastPathTriageResolverTests.fs`. The field
/// ids used (`region`, `units`, `country`, `showComparisons`) are
/// illustrative only — they are not declared on every surface, and the
/// "Declared fields" section above is what actually constrains a real
/// answer; the model is told so explicitly below. Four `set_field`
/// hits show a plain value, an aliased value, an optional-field clear,
/// and a boolean-shaped value; two `needs_full_agent` declines show the
/// one-line `reason` form for an instruction that needs a lookup and
/// one that names no single field plainly.
///
/// Two more follow, one per new verdict: an ordered
/// two-clause `set_fields` and a `navigate`. The `navigate` exemplar is
/// in `navigateExemplars` and reaches the prompt only on a surface that
/// declares navigation (`triageExemplarsFor`) — telling a model about a
/// verdict the surface cannot honour would cost tokens to invite a
/// refusal.
let private coreExemplars: (string * string) list = [
    "set the region to EMEA", """{"decision":"set_field","fieldId":"region","value":"EMEA","confidence":0.95}"""
    "switch to imperial units", """{"decision":"set_field","fieldId":"units","value":"imperial","confidence":0.9}"""
    "clear the country filter", """{"decision":"set_field","fieldId":"country","value":null,"confidence":0.93}"""
    "turn showComparisons off",
    """{"decision":"set_field","fieldId":"showComparisons","value":"false","confidence":0.88}"""
    "set the summary to reflect the trends this quarter",
    """{"decision":"needs_full_agent","confidence":0.97,"reason":"the value requires analysis, not a value stated plainly in the instruction"}"""
    "adjust the dashboard for the new quarter",
    """{"decision":"needs_full_agent","confidence":0.96,"reason":"no single declared field is named plainly"}"""
    "set region to EMEA and clear the country",
    """{"decision":"set_fields","fields":[{"fieldId":"region","value":"EMEA"},{"fieldId":"country","value":null}],"confidence":0.9}"""
]

let private navigateExemplars: (string * string) list = [
    "go to the reports page", """{"decision":"navigate","target":"reports","confidence":0.92}"""
]

/// The whole exemplar pack, every verdict shape included.
let triageExemplars: (string * string) list = coreExemplars @ navigateExemplars

/// The surface's declared navigation descriptor (`NavigationFieldId`),
/// when it declares one.
let tryFindNavigation (snapshot: AIFieldSnapshot) : AIFieldDescriptor option =
    AIFieldSnapshot.tryFindField NavigationFieldId snapshot

/// Look up a SETTABLE field — every declared descriptor except the
/// reserved navigation one, which a field set may never name.
let tryFindSettableField (fieldId: string) (snapshot: AIFieldSnapshot) : AIFieldDescriptor option =
    AIFieldSnapshot.tryFindField fieldId snapshot
    |> Option.filter (fun f -> not (String.Equals(f.FieldId, NavigationFieldId, StringComparison.OrdinalIgnoreCase)))

/// The exemplars the prompt for this surface carries: the whole pack
/// when the surface declares navigation, the pack less its `navigate`
/// exemplar otherwise.
let triageExemplarsFor (snapshot: AIFieldSnapshot) : (string * string) list =
    match tryFindNavigation snapshot with
    | Some _ -> triageExemplars
    | None -> coreExemplars

/// Build the triage system prompt from the declared surface. The
/// declared instruction patterns are the exemplars — the SAME
/// declarations Tier 1 pattern-matches on, so the two tiers cannot
/// drift apart in what they believe a field is called.
let buildTriagePrompt (snapshot: AIFieldSnapshot) : string =
    let describe (f: AIFieldDescriptor) =
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

        $"  - id: {f.FieldId}\n    type: {f.ValueType}\n    purpose: {f.Description}{patterns}{aliases}"

    let fieldLines =
        snapshot.Fields
        |> List.filter (fun f -> (tryFindSettableField f.FieldId snapshot).IsSome)
        |> List.map describe
        |> String.concat "\n"

    let navigation = tryFindNavigation snapshot

    let navigationRules =
        match navigation with
        | None -> []
        | Some nav -> [
            "Answer `navigate` ONLY when the instruction asks to go to exactly one navigation target named"
            "plainly, and asks for nothing else; put that target in `target`. An instruction that both"
            "navigates and sets fields is `needs_full_agent`."
            ""
            "Navigation (its value aliases name the targets `navigate` may use):"
            describe nav
            ""
          ]

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
        (match navigation with
         | Some _ -> "to set declared fields, or to go to one navigation target, on the screen in front of them."
         | None -> "to set declared fields on the screen in front of them.")
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
        "Answer `set_fields` when the instruction sets TWO OR MORE declared fields and EVERY one of them"
        "meets all three conditions above; list them in `fields`, in the order the instruction states"
        "them. If even one does not qualify, answer `needs_full_agent` for the whole instruction."
        ""
        yield! navigationRules
        "Answer `needs_full_agent` for EVERYTHING else, and in particular whenever you are not certain."
        "A wrong `set_field` changes the user's screen incorrectly and hides their request from the"
        "assistant that could have handled it properly; a `needs_full_agent` you did not have to give"
        "costs almost nothing. When the two are close, choose `needs_full_agent`."
        ""
        "To CLEAR a field, answer `set_field` with a null value — but only for an optional-typed field,"
        "one whose declared type name ends in `-option` (such as `string-option` or `enum-option`)."
        "Any other field's value must be a non-empty string."
        ""
        "`confidence` is your own probability, 0 to 1, that your answer is exactly what the user asked"
        "for — for `set_fields`, ALL of it. Report it honestly; a well-calibrated low number is more"
        "useful than a confident guess."
        ""
        "Examples of the exact answer grammar (the field ids and targets below are illustrative only —"
        "never name one unless it is also declared above):"
        (triageExemplarsFor snapshot
         |> List.map (fun (instruction, verdict) -> $"  instruction: \"{instruction}\"\n  answer: {verdict}")
         |> String.concat "\n")
    ]

/// The clauses of a `set_fields` verdict, strictly and in
/// order. ALL-OR-NOTHING: the list is refused whole (`None`) unless it
/// is an array of two to `MaxFieldClauses` objects, each carrying
/// exactly a non-blank string `fieldId` and a `value` that is a string
/// or JSON `null` (a clear), with no field named twice. There is no
/// "the valid clauses, less the bad one": a partially-valid list is an
/// instruction triage did not understand, and the full agent loop
/// hears it whole.
let private parseFieldClauses (fields: JsonElement) : TriageFieldClause list option =
    if fields.ValueKind <> JsonValueKind.Array then
        None
    else
        let items = fields.EnumerateArray() |> List.ofSeq

        if items.Length < 2 || items.Length > MaxFieldClauses then
            None
        else
            let parseClause (item: JsonElement) : TriageFieldClause option =
                if item.ValueKind <> JsonValueKind.Object then
                    None
                elif
                    item.EnumerateObject()
                    |> Seq.exists (fun p -> p.Name <> "fieldId" && p.Name <> "value")
                then
                    None
                else
                    let fieldId =
                        match item.TryGetProperty "fieldId" with
                        | true, v when
                            v.ValueKind = JsonValueKind.String
                            && not (String.IsNullOrWhiteSpace(v.GetString()))
                            ->
                            Some(v.GetString().Trim())
                        | _ -> None

                    let value =
                        match item.TryGetProperty "value" with
                        | true, v when v.ValueKind = JsonValueKind.String -> Some(Some(v.GetString()))
                        | true, v when v.ValueKind = JsonValueKind.Null -> Some None
                        | _ -> None

                    match fieldId, value with
                    | Some f, Some v -> Some({ FieldId = f; Value = v }: TriageFieldClause)
                    | _ -> None

            let parsed = items |> List.map parseClause

            if parsed |> List.exists Option.isNone then
                None
            else
                let clauses = parsed |> List.choose id

                let distinctIds =
                    clauses
                    |> List.map (fun c -> c.FieldId.ToLowerInvariant())
                    |> List.distinct
                    |> List.length

                if distinctIds <> clauses.Length then None else Some clauses

/// Parse the model's answer. Total: any shape that is not the
/// contract returns `None`, and the caller records `unparseable`.
/// Deliberately lenient about *casing* and about a fenced code block,
/// because those are the two ways a model that got the content right
/// gets the envelope wrong — and deliberately STRICT
/// about everything else in the two new verdicts: a `set_fields` list
/// is all-or-nothing (see `parseFieldClauses`), and a verdict carrying
/// another verdict's members is refused as mixed.
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

                    let str (name: string) =
                        tryProp name
                        |> Option.filter (fun v -> v.ValueKind = JsonValueKind.String)
                        |> Option.map (fun v -> v.GetString())

                    // A verdict carrying another shape's members is MIXED,
                    // and a mixed verdict is not one this parser guesses
                    // at: it is refused whole. JSON `null` members count as
                    // absent (a provider honouring the schema's nullable
                    // types emits them). `needs_full_agent`, and any
                    // decision string the grammar does not know, read as
                    // the model's decline.
                    let has (name: string) = (tryProp name).IsSome

                    let verdict =
                        match d with
                        | VerdictToken.SetField ->
                            if has "target" || has "fields" then
                                None
                            else
                                Some(TriageVerdict.SetField(str "fieldId", str "value"))
                        | VerdictToken.Navigate ->
                            if has "fieldId" || has "value" || has "fields" then
                                None
                            else
                                str "target" |> Option.map (fun t -> TriageVerdict.Navigate(t.Trim()))
                        | VerdictToken.SetFields ->
                            if has "fieldId" || has "value" || has "target" then
                                None
                            else
                                tryProp "fields"
                                |> Option.bind parseFieldClauses
                                |> Option.map TriageVerdict.SetFields
                        | _ -> Some TriageVerdict.NeedsFullAgent

                    verdict
                    |> Option.filter TriageVerdict.wellFormed
                    |> Option.map (fun v -> {
                        Decision = v
                        Confidence = confidence
                        Reason = str "reason" |> Option.defaultValue ""
                    })
        with _ ->
            None

/// Validate the model's answer against the declared surface and the
/// configured floor. This is the second half of the
/// bias-towards-the-agent property: the prompt asks the model to be
/// cautious, and this stage does not take its word for it.
///
/// Pure, and the single decision point — the orchestration below emits
/// actions if and only if this returns `TriageSetField`,
/// `TriageSetFields` or `TriageNavigate`, so the actions, the synthetic
/// turn, and the `hit` telemetry row cannot disagree about whether
/// triage resolved.
///
/// A `set_fields` verdict is planned clause by clause under
/// exactly the single-field rules, and the FIRST clause that fails
/// fails the whole verdict with that clause's outcome token — there is
/// no plan that applies some clauses and not others. A `navigate`
/// verdict needs the surface to declare the `_navigation` descriptor;
/// without it the verdict names something undeclared and is refused as
/// `unknown-field`.
let planTriage (config: FastPathTriageConfig) (snapshot: AIFieldSnapshot) (decision: TriageModelDecision) : TriagePlan =
    // Validate one field set against the declared surface. Shared by the
    // single and the list verdict, so the two cannot drift on what a
    // valid clause is.
    let planClause
        (fieldId: string option)
        (value: string option)
        : Result<AIFieldDescriptor * string option, TriageOutcome> =
        match fieldId |> Option.bind (fun id -> tryFindSettableField id snapshot) with
        | None -> Error TriageOutcome.UnknownField
        | Some field ->
            match value with
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
                    Ok(field, None)
                else
                    Error TriageOutcome.ClearUnsupported
            | Some raw -> Ok(field, Some(AIFieldDescriptor.resolveAlias field raw))

    let actionable =
        match decision.Decision with
        | TriageVerdict.NeedsFullAgent -> false
        | TriageVerdict.SetField _
        | TriageVerdict.SetFields _
        | TriageVerdict.Navigate _ -> true

    if actionable && not (TriageVerdict.wellFormed decision.Decision) then
        TriageFallThrough TriageOutcome.Unparseable
    elif actionable && decision.Confidence < FastPathTriageConfig.effectiveFloor config then
        TriageFallThrough TriageOutcome.LowConfidence
    else
        match decision.Decision with
        | TriageVerdict.SetField(fieldId, value) ->
            match planClause fieldId value with
            | Ok(field, value) -> TriageSetField(field, value, decision.Confidence)
            | Error outcome -> TriageFallThrough outcome
        | TriageVerdict.SetFields clauses ->
            let planned = clauses |> List.map (fun c -> planClause (Some c.FieldId) c.Value)

            match
                planned
                |> List.tryPick (function
                    | Error outcome -> Some outcome
                    | Ok _ -> None)
            with
            | Some outcome -> TriageFallThrough outcome
            | None ->
                let planned =
                    planned
                    |> List.choose (function
                        | Ok clause -> Some clause
                        | Error _ -> None)

                // Two clauses the parser saw as distinct can still land on one
                // declared field (an id spelled two ways the case-insensitive
                // lookup folds together). Setting a field twice in one verdict
                // is not an instruction this tier resolves.
                let distinctFields =
                    planned |> List.map (fun (f, _) -> f.FieldId) |> List.distinct |> List.length

                if distinctFields <> planned.Length then
                    TriageFallThrough TriageOutcome.Unparseable
                else
                    TriageSetFields(planned, decision.Confidence)
        | TriageVerdict.Navigate target ->
            match tryFindNavigation snapshot with
            | Some navigation ->
                TriageNavigate(navigation, AIFieldDescriptor.resolveAlias navigation target, decision.Confidence)
            | None -> TriageFallThrough TriageOutcome.UnknownField
        | TriageVerdict.NeedsFullAgent -> TriageFallThrough TriageOutcome.NeedsFullAgent

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

/// The `_ui.navigate` action payload. Same `source` marker
/// as the field-set payload, for the same cross-tier reason.
let navigatePayloadJson (target: string) : string =
    $"""{{"target":{jsonString target},"source":"triage"}}"""

/// The ModuleActions a resolved plan emits, as
/// `(actionKey, payloadJson)` pairs IN DISPATCH ORDER. Pure, so the
/// order and the all-or-nothing property are asserted on a value: a
/// fall-through plan emits nothing at all.
let planActions (plan: TriagePlan) : (string * string) list =
    match plan with
    | TriageSetField(field, value, _) -> [ SetFieldActionKey, actionPayloadJson field value ]
    | TriageSetFields(clauses, _) ->
        clauses
        |> List.map (fun (field, value) -> SetFieldActionKey, actionPayloadJson field value)
    | TriageNavigate(_, target, _) -> [ NavigateActionKey, navigatePayloadJson target ]
    | TriageFallThrough _ -> []

/// The recap for any resolved plan: the single-field text
/// unchanged, every clause of a `set_fields` in order (so the next
/// turn's model sees the whole sequence, not its last step), or the
/// navigation. `None` for a fall-through, which appends nothing.
let syntheticPlanReply (snapshot: AIFieldSnapshot) (plan: TriagePlan) : string option =
    match plan with
    | TriageSetField(field, value, _) -> Some(syntheticReply snapshot field value)
    | TriageSetFields(clauses, _) ->
        let steps =
            clauses
            |> List.map (fun (field, value) ->
                match value with
                | None -> $"cleared **{field.FieldId}**"
                | Some v -> $"set **{field.FieldId}** to `{v}`")
            |> String.concat ", then "

        let sentence =
            if steps.Length = 0 then
                steps
            else
                let first = steps.Substring(0, 1).ToUpperInvariant()
                first + steps.Substring 1

        Some $"{sentence} on {snapshot.ModuleId}."
    | TriageNavigate(_, target, _) -> Some $"Navigated to `{target}` on {snapshot.ModuleId}."
    | TriageFallThrough _ -> None

// ─── Telemetry ───────────────────────────────────────────────────

/// One row per triage ATTEMPT. `Outcome` is the stratification; the
/// row exists whether triage resolved or fell through, so
/// hits / attempts is computable and cannot be inflated by a tier that
/// only reports its successes.
type TriageEventPayload = {
    ConversationId: Guid
    TaskId: Guid
    ModuleId: string
    /// A `TriageOutcome` token (read through `TriageEventPayload.outcome`).
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
    /// A `TriageRoute` token (read through `TriageEventPayload.route`):
    /// which path reached a model. The
    /// operator question this answers is whether the triage spend
    /// actually moved to the cheap tier.
    Route: string
    /// The model that actually served. Equals
    /// `ProviderModel` except on an honoured override, where it is the
    /// declared `TriageModelId`.
    ServedModel: string
    /// Wall-clock for the whole attempt, provider call included.
    LatencyMs: float
    /// The configured `TimeoutMs` this attempt ran under
    /// (`FastPathTriageConfig.effectiveTimeoutMs`), on every row, not
    /// only a timed-out one. Pairs with `LatencyMs` so a dashboard can
    /// compute how close a non-timeout attempt ran to the budget (a
    /// near-miss) versus a `timeout` row, whose `LatencyMs` sits at the
    /// budget because this is the value that cut it off. A row written
    /// before the field existed carries no `TimeoutBudgetMs` property; STJ
    /// decodes the missing int field to `0`. Read a `0` as "budget
    /// unknown", never as "no budget was configured".
    TimeoutBudgetMs: int
    /// Length of the instruction, for tuning `MaxInstructionChars`.
    /// The instruction TEXT is not recorded here — the Tier-1 beacon
    /// records instructions because the client already showed them;
    /// this row is emitted server-side on every attempt including the
    /// ones that fall through to a full agent turn, and it is not the
    /// place to accumulate a second copy of user prose.
    InstructionChars: int
    /// Which grammar shape the parsed verdict took:
    /// a `VerdictShape` token, on a hit AND on
    /// a row whose action verdict was refused whole (an `unknown-field`
    /// multi, say), so a dashboard can attribute both the gain and the
    /// rejections to the extension that produced them. `""` when no
    /// action verdict parsed (`needs-full-agent`, `unparseable`,
    /// `provider-error`, `timeout`). On a `multi` hit `FieldId` holds the
    /// resolved ids comma-joined in dispatch order; on a `navigate` hit it
    /// holds `NavigationFieldId`. Read through `TriageEventPayload.shape`,
    /// which gives a row written before the field existed its old value.
    VerdictShape: string
}

/// The typed reading of a row. The string fields are the wire (written as a
/// union's `Token`); a row written before a field existed carries no
/// property for it, which the FableConverters path reads as `null`, and each
/// reading below gives that absence the value it had then — so a rollup over
/// a mixed window never meets a null.
module TriageEventPayload =
    /// The attempt's outcome; `None` for a token this vocabulary does not
    /// hold.
    let outcome (payload: TriageEventPayload) : TriageOutcome option = TriageOutcome.ofToken payload.Outcome

    /// The route; a row with none predates per-call routing, when the turn
    /// provider was the only path.
    let route (payload: TriageEventPayload) : TriageRoute =
        if isNull payload.Route then
            TriageRoute.TurnProvider
        else
            [
                TriageRoute.TriageProvider
                TriageRoute.TurnProvider
                TriageRoute.Override
                TriageRoute.OverrideFallback
                TriageRoute.Configured
            ]
            |> List.tryFind (fun r -> r.Token = payload.Route)
            |> Option.defaultValue TriageRoute.TurnProvider

    /// The model that served; a row with none was served by `ProviderModel`.
    let servedModel (payload: TriageEventPayload) : string =
        if isNull payload.ServedModel then
            payload.ProviderModel
        else
            payload.ServedModel

    /// The verdict's shape; a row with none predates the list and navigation
    /// verdicts, when a hit was always a single field.
    let shape (payload: TriageEventPayload) : VerdictShape option =
        if isNull payload.VerdictShape then
            if outcome payload = Some TriageOutcome.Hit then
                Some VerdictShape.Single
            else
                None
        else
            VerdictShape.ofToken payload.VerdictShape

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

/// Mirrors the agent loop's latency-scope fallback (`AIAgentEngine`'s
/// private `scopeOf`) so triage rows land in
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
            Container = ScopeKey.container (ScopeKey.User(UserId fallback))
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
            do! EventStore.writeOrRaise store evt
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
/// `Some (TriageResolved reply)` ⇒ resolved. The plan's actions — one
/// `_ui.set-field` per field set, in order, or one `_ui.navigate` — are
/// already published to the caller's notification stream.
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
                | Some instruction when not (Eligibility.isEligible (Eligibility.assess maxChars instruction)) ->
                    return None
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

                        let effectiveTimeoutMs = FastPathTriageConfig.effectiveTimeoutMs config

                        let policy = {
                            RetryPolicy.defaults with
                                // One attempt. A triage call that failed
                                // has already lost its latency argument;
                                // retrying spends more to arrive at the
                                // same fall-through.
                                MaxAttempts = 1
                                Timeout = Some(TimeSpan.FromMilliseconds(float effectiveTimeoutMs))
                        }

                        let triageInput =
                            ModelInput.ofSystemPrompt "FastPathTriageResolver" (Some(buildTriagePrompt snapshot)) [
                                AIProviderMessage.text "user" instruction
                            ]

                        // The route/served-model this attempt
                        // reports if the call never comes back at all inside
                        // the budget. Mirrors each arm's own on-`Error` guess
                        // below exactly (the override arm still computes its
                        // own precise route from the response when the call
                        // DOES come back with a failure) — a timeout and a
                        // same-arm provider error report the identical
                        // route/servedModel pair, since neither can know more
                        // than "this is the arm that was attempted".
                        let routeGuess, servedModelGuess =
                            match config.TriageProvider, turnProvider.Capabilities.TriageModelId with
                            | Some explicitProvider, _ ->
                                TriageRoute.TriageProvider, explicitProvider.Capabilities.Model
                            | None, Some _ -> TriageRoute.Override, turnProvider.Capabilities.Model
                            | None, None -> TriageRoute.TurnProvider, turnProvider.Capabilities.Model

                        // Which model serves the triage call, in
                        // order of precedence:
                        //   1. `config.TriageProvider` — the explicit escape
                        //      hatch, a whole instance the composition root
                        //      chose. It wins outright, whatever the turn
                        //      provider declares.
                        //   2. the turn provider's declared
                        //      `Capabilities.TriageModelId` — served through
                        //      the per-call override, so the cheap model is
                        //      named on THIS call and no second instance is
                        //      wired. A provider that cannot honour the id
                        //      (or does not implement the override) serves on
                        //      its configured model and the route says so.
                        //   3. neither — the turn provider's plain structured
                        //      send, byte-identical to the pre-661 path.
                        let callAsync =
                            match config.TriageProvider, turnProvider.Capabilities.TriageModelId with
                            | Some explicitProvider, _ -> async {
                                let! r = explicitProvider.SendStructuredMessage(triageInput, [], triageSchema, policy)
                                return r, TriageRoute.TriageProvider, explicitProvider.Capabilities.Model
                              }
                            | None, Some cheapModel -> async {
                                let! r =
                                    turnProvider.SendStructuredMessageWith(
                                        AIProviderCallOptions.forModel cheapModel,
                                        triageInput,
                                        [],
                                        triageSchema,
                                        policy
                                    )

                                return
                                    match r with
                                    | Ok call ->
                                        Ok call.Response,
                                        TriageRoute.ofOverride call.Model,
                                        ModelOverrideOutcome.served call.Model
                                    | Error err -> Error err, TriageRoute.Override, turnProvider.Capabilities.Model
                              }
                            | None, None -> async {
                                let! r = turnProvider.SendStructuredMessage(triageInput, [], triageSchema, policy)
                                return r, TriageRoute.TurnProvider, turnProvider.Capabilities.Model
                              }

                        // The resolver's OWN wall-clock cutoff on
                        // the whole call, independent of whatever the
                        // provider does with `policy.Timeout` internally.
                        // `RetryPolicy.Timeout` asks the provider to honour
                        // the budget cooperatively (its own
                        // `CancellationTokenSource`, as every shipped
                        // connector does); this is the backstop for a
                        // provider — real, or in tests a deliberately slow
                        // fake that never looks at the policy at all — that
                        // does not. Without it a hang is indistinguishable
                        // from a legitimate long call and either blocks the
                        // turn indefinitely or, once it does return, is
                        // misrecorded as an ordinary `provider-error`.
                        let! timedOut, response, route, servedModel = async {
                            try
                                let! child = Async.StartChild(callAsync, effectiveTimeoutMs)
                                let! response, route, servedModel = child
                                return false, response, route, servedModel
                            with :? TimeoutException ->
                                return
                                    true,
                                    Error(
                                        TransientNetwork
                                            $"Triage call did not complete within the configured {effectiveTimeoutMs} ms budget"
                                    ),
                                    routeGuess,
                                    servedModelGuess
                        }

                        let plan, shape =
                            if timedOut then
                                logger.Warn
                                    $"FastPath triage timed out after {effectiveTimeoutMs}ms (conversation={conversationId}, provider={provider.Capabilities.ProviderName}/{servedModel}, route={route.Token}). Falling through to the full agent loop."

                                TriageFallThrough TriageOutcome.Timeout, None
                            else
                                match response with
                                | Error err ->
                                    logger.Warn
                                        $"FastPath triage provider call failed (conversation={conversationId}, provider={provider.Capabilities.ProviderName}/{servedModel}, route={route.Token}): {AIProviderError.toMessage err}. Falling through to the full agent loop."

                                    TriageFallThrough TriageOutcome.ProviderError, None
                                | Ok r ->
                                    match parseTriageDecision r.Content with
                                    | None -> TriageFallThrough TriageOutcome.Unparseable, None
                                    | Some decision ->
                                        planTriage config snapshot decision, TriageVerdict.shape decision.Decision

                        sw.Stop()

                        let outcome, fieldId =
                            match plan with
                            | TriageSetField(field, _, _) -> TriageOutcome.Hit, field.FieldId
                            | TriageSetFields(clauses, _) ->
                                TriageOutcome.Hit, clauses |> List.map (fun (f, _) -> f.FieldId) |> String.concat ","
                            | TriageNavigate _ -> TriageOutcome.Hit, NavigationFieldId
                            | TriageFallThrough o -> o, ""

                        let payload: TriageEventPayload = {
                            ConversationId = conversationId
                            TaskId = taskId
                            ModuleId = moduleId
                            Outcome = outcome.Token
                            FieldId = fieldId
                            ProviderName = provider.Capabilities.ProviderName
                            ProviderModel = provider.Capabilities.Model
                            TriageModelId = defaultArg provider.Capabilities.TriageModelId ""
                            Route = route.Token
                            ServedModel = servedModel
                            LatencyMs = sw.Elapsed.TotalMilliseconds
                            TimeoutBudgetMs = effectiveTimeoutMs
                            InstructionChars = instruction.Length
                            VerdictShape = shape |> Option.map _.Token |> Option.defaultValue ""
                        }

                        do! emitTelemetry ctx payload

                        match syntheticPlanReply snapshot plan with
                        | None -> return Some TriageUnresolved
                        | Some reply ->
                            // Locked design decision: the existing
                            // ModuleAction infrastructure carries every
                            // action, one ModuleAction per field set (in
                            // the verdict's order) or per navigation —
                            // exactly as the single-field path always
                            // has. `emitAction` is best-effort by
                            // contract — it silently no-ops without a
                            // notification channel or a resolved user id.
                            for actionKey, actionPayload in planActions plan do
                                do! ToolContext.emitAction ctx moduleId actionKey actionPayload

                            let elapsed = sprintf "%.0f" sw.Elapsed.TotalMilliseconds

                            let confidenceText =
                                match plan with
                                | TriageSetField(_, _, c)
                                | TriageSetFields(_, c)
                                | TriageNavigate(_, _, c) -> sprintf "%.2f" c
                                | TriageFallThrough _ -> ""

                            let shapeText = shape |> Option.map _.Token |> Option.defaultValue ""

                            logger.Info
                                $"FastPath triage resolved in {elapsed}ms (conversation={conversationId}, module={moduleId}, shape={shapeText}, field={fieldId}, confidence={confidenceText}) — full agent loop skipped"

                            return Some(TriageResolved reply)
    }