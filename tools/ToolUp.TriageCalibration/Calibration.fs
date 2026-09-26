// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 663 — corpus-driven calibration of the Tier-3 fast-path triage
/// tier. A case file (JSON, supplied by path) is run through the SAME pure
/// decision path the resolver uses — `isEligibleInstruction`,
/// `buildTriagePrompt`, `parseTriageDecision`, `planTriage` — with one
/// structured provider call per eligible case, and the one answer is
/// re-scored at every confidence floor. See README.md for both schemas.
module ToolUp.TriageCalibration.Calibration

open System
open System.Diagnostics
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.FastPathTriageResolver

// ─── Schema identifiers ───────────────────────────────────────────

/// The case-file schema this tool reads. A file naming another schema is
/// refused rather than guessed at.
[<Literal>]
let CaseSchema = "toolup.triage-calibration.cases/1"

/// The result-file schema this tool writes. It is a wire contract for
/// downstream analysis: members are only ever ADDED under `/1`; a rename,
/// removal or change of meaning is a new schema id.
[<Literal>]
let ResultSchema = "toolup.triage-calibration.result/1"

/// The confidence floors every eligible case is re-scored at. The model is
/// called ONCE per case; each floor is a re-run of `planTriage` over the
/// cached answer, so the sweep costs one pass, not four.
let defaultFloors: float list = [ 0.70; 0.78; 0.85; 0.92 ]

// ─── Eligibility reasons ──────────────────────────────────────────

/// The instruction passed the pre-filter and the snapshot declares fields:
/// the case reaches the model.
[<Literal>]
let ReasonEligible = "eligible"

[<Literal>]
let ReasonEmpty = "empty"

[<Literal>]
let ReasonTooLong = "too-long"

[<Literal>]
let ReasonQuestionMark = "question-mark"

[<Literal>]
let ReasonQuestionOpener = "question-opener"

/// The resolver refused the instruction for a reason this tool does not
/// recognise — the pre-filter has gained a rule since this tool was
/// written. Reported rather than mislabelled.
[<Literal>]
let ReasonUnclassified = "unclassified"

/// The instruction is eligible but the snapshot declares no fields; the
/// resolver does not attempt triage on an empty surface.
[<Literal>]
let ReasonNoDeclaredFields = "no-declared-fields"

/// The leading words the resolver's pre-filter treats as a question. The
/// resolver's own list is private; this copy is used ONLY to NAME a refusal
/// the resolver has already made (`isEligibleInstruction` is always the
/// verdict). If the two drift, a refusal this list does not explain is
/// reported as `unclassified` — never attributed to the wrong rule.
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

/// The verdict comes from the resolver; the reason is derived by
/// elimination over the pre-filter's documented rules, in the resolver's
/// own order.
let eligibilityReason (maxChars: int) (instruction: string) : string =
    if isEligibleInstruction maxChars instruction then
        ReasonEligible
    elif String.IsNullOrWhiteSpace instruction then
        ReasonEmpty
    else
        let trimmed = instruction.Trim()

        if trimmed.Length > maxChars then
            ReasonTooLong
        elif trimmed.Contains "?" then
            ReasonQuestionMark
        elif
            questionOpeners
            |> List.exists (fun o -> trimmed.ToLowerInvariant().StartsWith(o, StringComparison.Ordinal))
        then
            ReasonQuestionOpener
        else
            ReasonUnclassified

// ─── The case file ────────────────────────────────────────────────

[<Literal>]
let DecisionSetField = "set_field"

[<Literal>]
let DecisionNeedsFullAgent = "needs_full_agent"

/// What the case author says the right answer is. `Value = None` with
/// `set_field` is a clear.
type ExpectedVerdict = {
    Decision: string
    FieldId: string option
    Value: string option
}

type CalibrationCase = {
    Id: string
    Instruction: string
    Snapshot: AIFieldSnapshot
    Expected: ExpectedVerdict
}

let private optString (el: JsonElement) (name: string) : string option =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

let private reqString (ctx: string) (el: JsonElement) (name: string) : Result<string, string> =
    match optString el name with
    | Some s -> Ok s
    | None -> Error $"{ctx}: missing string member '{name}'"

let private stringList (el: JsonElement) (name: string) : string list =
    match el.TryGetProperty name with
    | true, v when v.ValueKind = JsonValueKind.Array -> [
        for item in v.EnumerateArray() do
            if item.ValueKind = JsonValueKind.String then
                yield item.GetString()
      ]
    | _ -> []

let private decodeField (ctx: string) (el: JsonElement) : Result<AIFieldDescriptor, string> =
    match reqString ctx el "fieldId", reqString ctx el "valueType" with
    | Error e, _
    | _, Error e -> Error e
    | Ok fieldId, Ok valueType ->
        let aliases =
            match el.TryGetProperty "valueAliases" with
            | true, v when v.ValueKind = JsonValueKind.Object -> [
                for p in v.EnumerateObject() do
                    if p.Value.ValueKind = JsonValueKind.String then
                        yield p.Name, p.Value.GetString()
              ]
            | _ -> []

        Ok {
            FieldId = fieldId
            Description = defaultArg (optString el "description") ""
            ValueType = valueType
            InstructionPatterns = stringList el "instructionPatterns"
            ValueAliases = aliases
        }

let private decodeCase (index: int) (el: JsonElement) : Result<CalibrationCase, string> =
    let ctx = $"cases[{index}]"

    if el.ValueKind <> JsonValueKind.Object then
        Error $"{ctx}: not an object"
    else
        match reqString ctx el "id", reqString ctx el "instruction" with
        | Error e, _
        | _, Error e -> Error e
        | Ok id, Ok instruction ->
            let ctx = $"{ctx} ('{id}')"

            let snapshotEl =
                match el.TryGetProperty "snapshot" with
                | true, v when v.ValueKind = JsonValueKind.Object -> Ok v
                | _ -> Error $"{ctx}: missing object member 'snapshot'"

            let fields =
                match el.TryGetProperty "fields" with
                | true, v when v.ValueKind = JsonValueKind.Array ->
                    v.EnumerateArray()
                    |> Seq.mapi (fun i f -> decodeField $"{ctx}.fields[{i}]" f)
                    |> Seq.fold
                        (fun acc r ->
                            match acc, r with
                            | Error e, _ -> Error e
                            | Ok xs, Ok f -> Ok(f :: xs)
                            | Ok _, Error e -> Error e)
                        (Ok [])
                    |> Result.map List.rev
                | _ -> Error $"{ctx}: missing array member 'fields'"

            let expected =
                match el.TryGetProperty "expected" with
                | true, v when v.ValueKind = JsonValueKind.Object ->
                    match optString v "decision" with
                    | Some d when d = DecisionSetField || d = DecisionNeedsFullAgent ->
                        Ok {
                            Decision = d
                            FieldId = optString v "fieldId"
                            Value = optString v "value"
                        }
                    | _ -> Error $"{ctx}: expected.decision must be '{DecisionSetField}' or '{DecisionNeedsFullAgent}'"
                | _ -> Error $"{ctx}: missing object member 'expected'"

            match snapshotEl, fields, expected with
            | Error e, _, _
            | _, Error e, _
            | _, _, Error e -> Error e
            | Ok s, Ok fields, Ok expected ->
                match reqString ctx s "moduleId" with
                | Error e -> Error e
                | Ok moduleId ->
                    Ok {
                        Id = id
                        Instruction = instruction
                        Snapshot = {
                            ModuleId = moduleId
                            Page = optString s "page"
                            Fields = fields
                            StateSummary = defaultArg (optString s "stateSummary") ""
                        }
                        Expected = expected
                    }

/// Decode a case file. Total: every malformation is an `Error` naming the
/// case it is in.
let decodeCases (json: string) : Result<CalibrationCase list, string> =
    let decodeRoot (root: JsonElement) =
        match optString root "schema" with
        | Some s when s = CaseSchema ->
            match root.TryGetProperty "cases" with
            | true, v when v.ValueKind = JsonValueKind.Array ->
                let decoded = v.EnumerateArray() |> Seq.mapi decodeCase |> List.ofSeq

                let firstError =
                    decoded
                    |> List.tryPick (fun r ->
                        match r with
                        | Error e -> Some e
                        | Ok _ -> None)

                match firstError with
                | Some e -> Error e
                | None ->
                    let cases =
                        decoded
                        |> List.choose (fun r ->
                            match r with
                            | Ok c -> Some c
                            | Error _ -> None)

                    match cases |> List.countBy _.Id |> List.tryFind (fun (_, n) -> n > 1) with
                    | Some(id, _) -> Error $"case id '{id}' appears more than once"
                    | None -> Ok cases
            | _ -> Error "missing array member 'cases'"
        | Some other -> Error $"unknown case-file schema '{other}' (this tool reads '{CaseSchema}')"
        | None -> Error $"missing 'schema' member (expected '{CaseSchema}')"

    try
        // A UTF-8 byte-order mark survives decoding as U+FEFF; the parser refuses it.
        use doc = JsonDocument.Parse(json.TrimStart(char 0xFEFF))

        if doc.RootElement.ValueKind <> JsonValueKind.Object then
            Error "case file root is not a JSON object"
        else
            decodeRoot doc.RootElement
    with :? JsonException as ex ->
        Error $"case file is not valid JSON: {ex.Message}"

// ─── The result file ──────────────────────────────────────────────

/// Latency stage: the pre-filter refused the case locally (free).
[<Literal>]
let StagePreFilter = "pre-filter"

/// Latency stage: the case cost a provider call (not free).
[<Literal>]
let StageModelCall = "model-call"

/// Call outcome for a call that returned parseable content. The other
/// call outcomes are the resolver's own tokens: `OutcomeUnparseable`,
/// `OutcomeProviderError`, `OutcomeTimeout`.
[<Literal>]
let CallAnswered = "answered"

[<Literal>]
let ScoreCorrectResolve = "correct-resolve"

[<Literal>]
let ScoreWrongResolve = "wrong-resolve"

[<Literal>]
let ScoreFalseFire = "false-fire"

[<Literal>]
let ScoreMissed = "missed"

[<Literal>]
let ScoreCorrectDecline = "correct-decline"

type Eligibility = { Eligible: bool; Reason: string }

type Latency = { Stage: string; Ms: float }

type ModelCall = {
    /// `answered` | `unparseable` | `provider-error` | `timeout`.
    Outcome: string
    /// `ModelOverrideOutcome.route` vocabulary.
    Route: string
    ServedModel: string
    /// The raw content, cached; `None` when the call returned nothing.
    RawContent: string option
    Error: string option
    Decision: TriageModelDecision option
    InputTokens: int option
    OutputTokens: int option
}

type FloorVerdict = {
    Floor: float
    /// `hit` or a resolver fall-through token.
    Outcome: string
    FieldId: string option
    Value: string option
    Score: string
}

type CaseResult = {
    Id: string
    Instruction: string
    Expected: ExpectedVerdict
    Eligibility: Eligibility
    Latency: Latency
    Call: ModelCall option
    /// One per floor for a case that reached the model; empty otherwise.
    Floors: FloorVerdict list
}

type FloorSummary = {
    Floor: float
    Hits: int
    CorrectResolve: int
    WrongResolve: int
    FalseFire: int
    Missed: int
    CorrectDecline: int
    /// `CorrectResolve / cases expecting set_field`; `None` when there are none.
    ResolveRate: float option
    /// `(FalseFire + WrongResolve) / Hits`; `None` when there are no hits.
    FalseFireRate: float option
}

type RunSummary = {
    Cases: int
    Ineligible: int
    ModelCalls: int
    Answered: int
    Unparseable: int
    ProviderErrors: int
    Timeouts: int
    PreFilterMsTotal: float
    ModelCallMsTotal: float
    ModelCallMsP50: float option
    ModelCallMsP95: float option
    Floors: FloorSummary list
}

type ProviderInfo = {
    Name: string
    ConfiguredModel: string
    SupportsTriage: bool
    /// The model named on each call through the per-call override, if any.
    RequestedModel: string option
}

type RunSettings = {
    Floors: float list
    MaxInstructionChars: int
    TimeoutMs: int
}

type CalibrationResult = {
    Schema: string
    CorpusSha256: string
    Provider: ProviderInfo
    Settings: RunSettings
    Cases: CaseResult list
    Summary: RunSummary
}

// ─── Scoring ──────────────────────────────────────────────────────

let private sameText (a: string option) (b: string option) =
    let norm (s: string option) =
        s |> Option.map (fun v -> v.Trim()) |> Option.filter (fun v -> v <> "")

    match norm a, norm b with
    | None, None -> true
    | Some x, Some y -> String.Equals(x, y, StringComparison.OrdinalIgnoreCase)
    | _ -> false

/// Score one plan against the case's expectation.
let score (expected: ExpectedVerdict) (plan: TriagePlan) : string =
    match plan with
    | TriageSetField(field, value, _) ->
        if expected.Decision <> DecisionSetField then
            ScoreFalseFire
        elif sameText expected.FieldId (Some field.FieldId) && sameText expected.Value value then
            ScoreCorrectResolve
        else
            ScoreWrongResolve
    | TriageFallThrough _ ->
        if expected.Decision = DecisionSetField then
            ScoreMissed
        else
            ScoreCorrectDecline

/// Re-score one cached decision at every floor: `planTriage` alone, no
/// provider involved.
let rescore
    (registry: IAIFieldRegistry)
    (floors: float list)
    (snapshot: AIFieldSnapshot)
    (expected: ExpectedVerdict)
    (decision: TriageModelDecision)
    : FloorVerdict list =
    floors
    |> List.map (fun floor ->
        let config = {
            FastPathTriageConfig.create registry with
                ConfidenceFloor = floor
        }

        let plan = planTriage config snapshot decision

        match plan with
        | TriageSetField(field, value, _) -> {
            Floor = floor
            Outcome = OutcomeHit
            FieldId = Some field.FieldId
            Value = value
            Score = score expected plan
          }
        | TriageFallThrough outcome -> {
            Floor = floor
            Outcome = outcome
            FieldId = None
            Value = None
            Score = score expected plan
          })

/// A call that produced no decision falls through identically at every
/// floor, carrying the call's own outcome token.
let private fallThroughAt (floors: float list) (expected: ExpectedVerdict) (outcome: string) : FloorVerdict list =
    floors
    |> List.map (fun floor -> {
        Floor = floor
        Outcome = outcome
        FieldId = None
        Value = None
        Score = score expected (TriageFallThrough outcome)
    })

// ─── Running ──────────────────────────────────────────────────────

/// `planTriage` reads only the floor from its config; the registry is never
/// consulted. This stand-in exists to satisfy the record.
let private noRegistry =
    { new IAIFieldRegistry with
        member _.Describe(_, _) = async { return None }
    }

let private roundMs (ms: float) = Math.Round(ms, 3)

/// One structured triage call, shaped exactly as the resolver shapes it:
/// the same prompt, the same schema, one attempt, and the resolver's own
/// wall-clock backstop. Returns the call record and its elapsed ms.
let callOnce
    (provider: IAIProvider)
    (options: AIProviderCallOptions)
    (timeoutMs: int)
    (snapshot: AIFieldSnapshot)
    (instruction: string)
    : Async<ModelCall * float> =
    async {
        let policy = {
            RetryPolicy.defaults with
                MaxAttempts = 1
                Timeout = Some(TimeSpan.FromMilliseconds(float timeoutMs))
        }

        // The builder id matches the resolver's, so the rendered request is
        // byte-identical to the one production sends.
        let input =
            ModelInput.ofSystemPrompt "FastPathTriageResolver" (Some(buildTriagePrompt snapshot)) [
                AIProviderMessage.text "user" instruction
            ]

        let routeGuess =
            match options.Model with
            | Some _ -> "override"
            | None -> "configured"

        let sw = Stopwatch.StartNew()

        let! outcome = async {
            try
                let! child =
                    Async.StartChild(
                        provider.SendStructuredMessageWith(options, input, [], triageSchema, policy),
                        timeoutMs
                    )

                let! r = child
                return Some r
            with :? TimeoutException ->
                return None
        }

        sw.Stop()
        let elapsed = roundMs sw.Elapsed.TotalMilliseconds

        let record =
            match outcome with
            | None -> {
                Outcome = OutcomeTimeout
                Route = routeGuess
                ServedModel = provider.Capabilities.Model
                RawContent = None
                Error = Some $"call did not complete within the {timeoutMs} ms budget"
                Decision = None
                InputTokens = None
                OutputTokens = None
              }
            | Some(Error err) -> {
                Outcome = OutcomeProviderError
                Route = routeGuess
                ServedModel = provider.Capabilities.Model
                RawContent = None
                Error = Some(AIProviderError.toMessage err)
                Decision = None
                InputTokens = None
                OutputTokens = None
              }
            | Some(Ok call) ->
                let decision = parseTriageDecision call.Response.Content

                {
                    Outcome =
                        match decision with
                        | Some _ -> CallAnswered
                        | None -> OutcomeUnparseable
                    Route = ModelOverrideOutcome.route call.Model
                    ServedModel = ModelOverrideOutcome.served call.Model
                    RawContent = Some call.Response.Content
                    Error = None
                    Decision = decision
                    InputTokens = call.Response.Usage |> Option.map (fun u -> u.PromptTokens)
                    OutputTokens = call.Response.Usage |> Option.map (fun u -> u.OutputTokens)
                }

        return record, elapsed
    }

/// Run one case: the pre-filter first (free, local), then — only for an
/// eligible case — ONE provider call whose answer is re-scored at every
/// floor.
let runCase
    (provider: IAIProvider)
    (options: AIProviderCallOptions)
    (settings: RunSettings)
    (case: CalibrationCase)
    : Async<CaseResult> =
    async {
        let sw = Stopwatch.StartNew()
        let reason = eligibilityReason settings.MaxInstructionChars case.Instruction

        let reason =
            if reason = ReasonEligible && List.isEmpty case.Snapshot.Fields then
                ReasonNoDeclaredFields
            else
                reason

        sw.Stop()

        if reason <> ReasonEligible then
            return {
                Id = case.Id
                Instruction = case.Instruction
                Expected = case.Expected
                Eligibility = { Eligible = false; Reason = reason }
                Latency = {
                    Stage = StagePreFilter
                    Ms = roundMs sw.Elapsed.TotalMilliseconds
                }
                Call = None
                Floors = []
            }
        else
            let! call, ms = callOnce provider options settings.TimeoutMs case.Snapshot (case.Instruction.Trim())

            let floors =
                match call.Decision with
                | Some decision -> rescore noRegistry settings.Floors case.Snapshot case.Expected decision
                | None -> fallThroughAt settings.Floors case.Expected call.Outcome

            return {
                Id = case.Id
                Instruction = case.Instruction
                Expected = case.Expected
                Eligibility = {
                    Eligible = true
                    Reason = ReasonEligible
                }
                Latency = { Stage = StageModelCall; Ms = ms }
                Call = Some call
                Floors = floors
            }
    }

/// Nearest-rank percentile over a non-empty sample.
let private percentile (p: float) (xs: float list) : float option =
    match List.sort xs with
    | [] -> None
    | sorted ->
        let rank = int (Math.Ceiling(p / 100.0 * float sorted.Length))
        Some(List.item (max 0 (rank - 1)) sorted)

/// Aggregate the per-case rows. An ineligible case never reaches a floor;
/// in production it falls through, so each floor counts it as a miss (if
/// a set was expected) or a correct decline.
let summarise (floors: float list) (cases: CaseResult list) : RunSummary =
    let calls = cases |> List.choose (fun c -> c.Call)

    let countCalls outcome =
        calls |> List.filter (fun c -> c.Outcome = outcome) |> List.length

    let modelMs =
        cases
        |> List.filter (fun c -> c.Latency.Stage = StageModelCall)
        |> List.map (fun c -> c.Latency.Ms)

    let expectingSet =
        cases
        |> List.filter (fun c -> c.Expected.Decision = DecisionSetField)
        |> List.length

    let floorSummary (floor: float) =
        let scores =
            cases
            |> List.map (fun c ->
                match c.Floors |> List.tryFind (fun f -> f.Floor = floor) with
                | Some f -> f.Outcome, f.Score
                | None -> c.Eligibility.Reason, score c.Expected (TriageFallThrough c.Eligibility.Reason))

        let n s =
            scores |> List.filter (fun (_, x) -> x = s) |> List.length

        let hits = scores |> List.filter (fun (o, _) -> o = OutcomeHit) |> List.length
        let correct = n ScoreCorrectResolve
        let wrong = n ScoreWrongResolve
        let falseFire = n ScoreFalseFire

        {
            Floor = floor
            Hits = hits
            CorrectResolve = correct
            WrongResolve = wrong
            FalseFire = falseFire
            Missed = n ScoreMissed
            CorrectDecline = n ScoreCorrectDecline
            ResolveRate =
                if expectingSet = 0 then
                    None
                else
                    Some(Math.Round(float correct / float expectingSet, 6))
            FalseFireRate =
                if hits = 0 then
                    None
                else
                    Some(Math.Round(float (falseFire + wrong) / float hits, 6))
        }

    {
        Cases = cases.Length
        Ineligible = cases |> List.filter (fun c -> not c.Eligibility.Eligible) |> List.length
        ModelCalls = calls.Length
        Answered = countCalls CallAnswered
        Unparseable = countCalls OutcomeUnparseable
        ProviderErrors = countCalls OutcomeProviderError
        Timeouts = countCalls OutcomeTimeout
        PreFilterMsTotal =
            cases
            |> List.filter (fun c -> c.Latency.Stage = StagePreFilter)
            |> List.sumBy (fun c -> c.Latency.Ms)
            |> roundMs
        ModelCallMsTotal = modelMs |> List.sum |> roundMs
        ModelCallMsP50 = percentile 50.0 modelMs
        ModelCallMsP95 = percentile 95.0 modelMs
        Floors = floors |> List.map floorSummary
    }

let sha256Hex (bytes: byte[]) : string =
    SHA256.HashData bytes |> Convert.ToHexString |> (fun s -> s.ToLowerInvariant())

/// Run every case, sequentially, and assemble the result.
let run
    (provider: IAIProvider)
    (options: AIProviderCallOptions)
    (settings: RunSettings)
    (corpusSha256: string)
    (cases: CalibrationCase list)
    : Async<CalibrationResult> =
    async {
        let results = ResizeArray()

        for case in cases do
            let! r = runCase provider options settings case
            results.Add r

        let caseResults = List.ofSeq results

        return {
            Schema = ResultSchema
            CorpusSha256 = corpusSha256
            Provider = {
                Name = provider.Capabilities.ProviderName
                ConfiguredModel = provider.Capabilities.Model
                SupportsTriage = provider.Capabilities.SupportsTriage
                RequestedModel = options.Model
            }
            Settings = settings
            Cases = caseResults
            Summary = summarise settings.Floors caseResults
        }
    }

// ─── Result encoding ──────────────────────────────────────────────

let private str (s: string) : JsonNode = JsonValue.Create s
let private num (f: float) : JsonNode = JsonValue.Create f
let private int' (i: int) : JsonNode = JsonValue.Create i
let private bool' (b: bool) : JsonNode = JsonValue.Create b

let private optStr (s: string option) : JsonNode =
    match s with
    | Some v -> str v
    | None -> null

let private optNum (f: float option) : JsonNode =
    match f with
    | Some v -> num v
    | None -> null

let private optInt (i: int option) : JsonNode =
    match i with
    | Some v -> int' v
    | None -> null

let private obj (members: (string * JsonNode) list) : JsonNode =
    let o = JsonObject()

    for k, v in members do
        o.Add(k, v)

    o

let private arr (items: JsonNode list) : JsonNode =
    let a = JsonArray()

    for i in items do
        a.Add i

    a

let private encodeExpected (e: ExpectedVerdict) =
    obj [
        "decision", str e.Decision
        "fieldId", optStr e.FieldId
        "value", optStr e.Value
    ]

let private encodeDecision (d: TriageModelDecision) =
    obj [
        "decision", str d.Decision
        "fieldId", optStr d.FieldId
        "value", optStr d.Value
        "confidence", num d.Confidence
        "reason", str d.Reason
    ]

let private encodeCall (c: ModelCall) =
    obj [
        "outcome", str c.Outcome
        "route", str c.Route
        "servedModel", str c.ServedModel
        "rawContent", optStr c.RawContent
        "error", optStr c.Error
        "decision", (c.Decision |> Option.map encodeDecision |> Option.defaultValue null)
        "inputTokens", optInt c.InputTokens
        "outputTokens", optInt c.OutputTokens
    ]

let private encodeFloor (f: FloorVerdict) =
    obj [
        "floor", num f.Floor
        "outcome", str f.Outcome
        "fieldId", optStr f.FieldId
        "value", optStr f.Value
        "score", str f.Score
    ]

let private encodeCase (c: CaseResult) =
    obj [
        "id", str c.Id
        "instruction", str c.Instruction
        "expected", encodeExpected c.Expected
        "eligibility", obj [ "eligible", bool' c.Eligibility.Eligible; "reason", str c.Eligibility.Reason ]
        "latency", obj [ "stage", str c.Latency.Stage; "ms", num c.Latency.Ms ]
        "call", (c.Call |> Option.map encodeCall |> Option.defaultValue null)
        "floors", arr (c.Floors |> List.map encodeFloor)
    ]

let private encodeFloorSummary (f: FloorSummary) =
    obj [
        "floor", num f.Floor
        "hits", int' f.Hits
        "correctResolve", int' f.CorrectResolve
        "wrongResolve", int' f.WrongResolve
        "falseFire", int' f.FalseFire
        "missed", int' f.Missed
        "correctDecline", int' f.CorrectDecline
        "resolveRate", optNum f.ResolveRate
        "falseFireRate", optNum f.FalseFireRate
    ]

/// Encode a result as indented JSON. Member order is fixed.
let encodeResult (r: CalibrationResult) : string =
    let s = r.Summary

    let root =
        obj [
            "schema", str r.Schema
            "corpusSha256", str r.CorpusSha256
            "provider",
            obj [
                "name", str r.Provider.Name
                "configuredModel", str r.Provider.ConfiguredModel
                "supportsTriage", bool' r.Provider.SupportsTriage
                "requestedModel", optStr r.Provider.RequestedModel
            ]
            "settings",
            obj [
                "floors", arr (r.Settings.Floors |> List.map num)
                "maxInstructionChars", int' r.Settings.MaxInstructionChars
                "timeoutMs", int' r.Settings.TimeoutMs
            ]
            "cases", arr (r.Cases |> List.map encodeCase)
            "summary",
            obj [
                "cases", int' s.Cases
                "ineligible", int' s.Ineligible
                "modelCalls", int' s.ModelCalls
                "answered", int' s.Answered
                "unparseable", int' s.Unparseable
                "providerErrors", int' s.ProviderErrors
                "timeouts", int' s.Timeouts
                "preFilterMsTotal", num s.PreFilterMsTotal
                "modelCallMsTotal", num s.ModelCallMsTotal
                "modelCallMsP50", optNum s.ModelCallMsP50
                "modelCallMsP95", optNum s.ModelCallMsP95
                "floors", arr (s.Floors |> List.map encodeFloorSummary)
            ]
        ]

    root.ToJsonString(JsonSerializerOptions(WriteIndented = true))

// ─── Result decoding (the round-trip half of the contract) ────────

exception private DecodeError of string

let private field (o: JsonNode) (name: string) : JsonNode =
    match o with
    | :? JsonObject as jo ->
        let mutable v: JsonNode = null

        if jo.TryGetPropertyValue(name, &v) then
            v
        else
            raise (DecodeError $"missing member '{name}'")
    | _ -> raise (DecodeError $"expected an object holding '{name}'")

let private getStr o n =
    match field o n with
    | null -> raise (DecodeError $"'{n}' is null")
    | v -> v.GetValue<string>()

let private getOptStr o n =
    match field o n with
    | null -> None
    | v -> Some(v.GetValue<string>())

let private getNum o n =
    match field o n with
    | null -> raise (DecodeError $"'{n}' is null")
    | v -> v.GetValue<float>()

let private getOptNum o n =
    match field o n with
    | null -> None
    | v -> Some(v.GetValue<float>())

let private getInt o n =
    match field o n with
    | null -> raise (DecodeError $"'{n}' is null")
    | v -> v.GetValue<int>()

let private getOptInt o n =
    match field o n with
    | null -> None
    | v -> Some(v.GetValue<int>())

let private getBool o n =
    match field o n with
    | null -> raise (DecodeError $"'{n}' is null")
    | v -> v.GetValue<bool>()

let private getArr o n : JsonNode list =
    match field o n with
    | :? JsonArray as a -> List.ofSeq a
    | _ -> raise (DecodeError $"'{n}' is not an array")

let private decodeExpected o : ExpectedVerdict = {
    Decision = getStr o "decision"
    FieldId = getOptStr o "fieldId"
    Value = getOptStr o "value"
}

let private decodeDecision o : TriageModelDecision = {
    Decision = getStr o "decision"
    FieldId = getOptStr o "fieldId"
    Value = getOptStr o "value"
    Confidence = getNum o "confidence"
    Reason = getStr o "reason"
}

let private decodeCall o : ModelCall = {
    Outcome = getStr o "outcome"
    Route = getStr o "route"
    ServedModel = getStr o "servedModel"
    RawContent = getOptStr o "rawContent"
    Error = getOptStr o "error"
    Decision =
        match field o "decision" with
        | null -> None
        | d -> Some(decodeDecision d)
    InputTokens = getOptInt o "inputTokens"
    OutputTokens = getOptInt o "outputTokens"
}

let private decodeFloor o : FloorVerdict = {
    Floor = getNum o "floor"
    Outcome = getStr o "outcome"
    FieldId = getOptStr o "fieldId"
    Value = getOptStr o "value"
    Score = getStr o "score"
}

let private decodeCaseResult o : CaseResult =
    let e = field o "eligibility"
    let l = field o "latency"

    {
        Id = getStr o "id"
        Instruction = getStr o "instruction"
        Expected = decodeExpected (field o "expected")
        Eligibility = {
            Eligible = getBool e "eligible"
            Reason = getStr e "reason"
        }
        Latency = {
            Stage = getStr l "stage"
            Ms = getNum l "ms"
        }
        Call =
            match field o "call" with
            | null -> None
            | c -> Some(decodeCall c)
        Floors = getArr o "floors" |> List.map decodeFloor
    }

let private decodeFloorSummary o : FloorSummary = {
    Floor = getNum o "floor"
    Hits = getInt o "hits"
    CorrectResolve = getInt o "correctResolve"
    WrongResolve = getInt o "wrongResolve"
    FalseFire = getInt o "falseFire"
    Missed = getInt o "missed"
    CorrectDecline = getInt o "correctDecline"
    ResolveRate = getOptNum o "resolveRate"
    FalseFireRate = getOptNum o "falseFireRate"
}

/// Decode a result file. Refuses any schema id other than `ResultSchema`;
/// tolerates members it does not know (the schema only ever grows).
let decodeResult (json: string) : Result<CalibrationResult, string> =
    try
        let root = JsonNode.Parse json

        match getStr root "schema" with
        | s when s <> ResultSchema -> Error $"unknown result schema '{s}' (expected '{ResultSchema}')"
        | schema ->
            let p = field root "provider"
            let st = field root "settings"
            let s = field root "summary"

            Ok {
                Schema = schema
                CorpusSha256 = getStr root "corpusSha256"
                Provider = {
                    Name = getStr p "name"
                    ConfiguredModel = getStr p "configuredModel"
                    SupportsTriage = getBool p "supportsTriage"
                    RequestedModel = getOptStr p "requestedModel"
                }
                Settings = {
                    Floors = getArr st "floors" |> List.map (fun n -> n.GetValue<float>())
                    MaxInstructionChars = getInt st "maxInstructionChars"
                    TimeoutMs = getInt st "timeoutMs"
                }
                Cases = getArr root "cases" |> List.map decodeCaseResult
                Summary = {
                    Cases = getInt s "cases"
                    Ineligible = getInt s "ineligible"
                    ModelCalls = getInt s "modelCalls"
                    Answered = getInt s "answered"
                    Unparseable = getInt s "unparseable"
                    ProviderErrors = getInt s "providerErrors"
                    Timeouts = getInt s "timeouts"
                    PreFilterMsTotal = getNum s "preFilterMsTotal"
                    ModelCallMsTotal = getNum s "modelCallMsTotal"
                    ModelCallMsP50 = getOptNum s "modelCallMsP50"
                    ModelCallMsP95 = getOptNum s "modelCallMsP95"
                    Floors = getArr s "floors" |> List.map decodeFloorSummary
                }
            }
    with
    | DecodeError e -> Error e
    | :? JsonException as ex -> Error $"result file is not valid JSON: {ex.Message}"
    | :? InvalidOperationException as ex -> Error $"result file member has the wrong type: {ex.Message}"
    | :? FormatException as ex -> Error $"result file member has the wrong type: {ex.Message}"

/// Read a case file from disk: its cases and the SHA-256 of its bytes.
let loadCaseFile (path: string) : Result<CalibrationCase list * string, string> =
    if not (File.Exists path) then
        Error $"case file not found: {path}"
    else
        let bytes = File.ReadAllBytes path

        decodeCases (Encoding.UTF8.GetString bytes)
        |> Result.map (fun cases -> cases, sha256Hex bytes)