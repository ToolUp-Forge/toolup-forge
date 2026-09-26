// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 514 — the conversation / prompt eval harness.
///
/// Phase 53's `ConversationReplay` re-runs a persisted conversation and
/// reports an operator-readable delta: no assertions, no score, no gate,
/// and — by its own header — no tool dispatch. This module is the
/// regression-gate half: a fixture declares prompt versions, tools,
/// recorded tool results, cases with rubric assertions, and (for the
/// offline arm) the recorded provider behaviour under each prompt
/// version. `evaluate` replays every case through the full agent loop —
/// provider call, tool dispatch against the RECORDED results, next call —
/// scores the transcript against the rubric, and `gate` turns the report
/// into a pass / fail verdict, optionally against a committed baseline
/// with a tolerance. The shape mirrors `ToolUp.RAG.Evaluation`
/// (fixtures, `--baseline`, non-zero exit); the metric is rubric pass
/// rate rather than retrieval quality.
///
/// **Why the offline arm replays a RECORDING rather than a model.** GP 12:
/// replay is deterministic data. A fixture's `recordings` block is what
/// the model did under each prompt version — the cassette — and
/// `ScriptedProvider` serves it call by call. That makes the rubric, the
/// tool loop and the baseline gate provable on a fresh checkout with no
/// key (GP 11). It does NOT make a prompt edit self-checking: the
/// recording is evidence about the prompt it was captured under, so a
/// changed prompt is re-evaluated with `--provider live` and its
/// recording refreshed. The live arm and the LLM-judge arm take the same
/// `IAIProvider`, so nothing here is vendor-specific.
module ToolUp.AI.Evaluation.ConversationEval

open System
open System.Collections.Generic
open System.IO
open System.Text
open System.Text.Json
open ToolUp.Remoting.Json.SystemTextJson
open ToolUp.Platform
open ToolUp.Platform.AI

// ─── Fixture model ────────────────────────────────────────────────

/// A tool result the replay feeds back when the model calls `Tool` with
/// arguments containing every string in `ArgumentsContain` (ordinal
/// substring match over the raw arguments JSON). The first matching
/// entry wins, so list specific entries before general ones.
type RecordedToolResult = {
    Tool: string
    ArgumentsContain: string list
    Content: string
}

/// One deterministic rubric assertion over a replayed case.
type RubricAssertion =
    /// The model called `tool`, with arguments containing every string.
    | ToolCalled of tool: string * argumentsContain: string list
    /// The model never called `tool` in this case.
    | ToolNotCalled of tool: string
    /// The final answer is a JSON object carrying every named field.
    | StructuredOutput of requiredFields: string list
    /// The final answer declines (contains any marker, case-insensitive)
    /// and the case called no tool — the out-of-scope refusal.
    | Refusal of markers: string list
    /// No assistant text in the case contains any phrase
    /// (case-insensitive). CRITICAL: a failure fails the gate whatever
    /// the baseline says, as a RAG filter leak does.
    | NoForbiddenContent of phrases: string list
    /// The final answer contains the phrase (case-insensitive).
    | Contains of phrase: string

/// The optional LLM-judge arm for a case. Scored only when a judge
/// provider is configured; otherwise the case reports it Pending.
type JudgeSpec = { Criteria: string; MinScore: float }

type EvalCase = {
    Id: string
    /// The user turns, in order. Each runs the agent loop to completion
    /// (until the model answers without calling a tool).
    Turns: string list
    /// When set, every provider call in the case goes through
    /// `SendStructuredMessage` with this JSON Schema.
    ResponseSchema: string option
    Assertions: RubricAssertion list
    Judge: JudgeSpec option
}

type ConversationFixture = {
    Name: string
    Description: string
    /// The prompt version `evaluate` runs when the caller names none.
    DefaultPrompt: string
    /// Prompt version → system prompt text.
    Prompts: Map<string, string>
    Tools: AIProviderToolDef list
    ToolResults: RecordedToolResult list
    /// Tool-dispatch rounds allowed per user turn before the case is
    /// failed as a runaway loop.
    MaxToolRounds: int
    Cases: EvalCase list
    /// Prompt version → case id → the provider responses, in call order.
    Recordings: Map<string, Map<string, AIProviderResponse list>>
}

// ─── Fixture loading ──────────────────────────────────────────────

module private Json =
    let prop (name: string) (e: JsonElement) : JsonElement option =
        match e.TryGetProperty name with
        | true, v when v.ValueKind <> JsonValueKind.Null -> Some v
        | _ -> None

    let req (ctx: string) (name: string) (e: JsonElement) : JsonElement =
        match prop name e with
        | Some v -> v
        | None -> failwithf "%s: missing required field '%s'" ctx name

    let str (ctx: string) (name: string) (e: JsonElement) : string = (req ctx name e).GetString()

    let strOr (fallback: string) (name: string) (e: JsonElement) : string =
        prop name e |> Option.map _.GetString() |> Option.defaultValue fallback

    let items (name: string) (e: JsonElement) : JsonElement list =
        match prop name e with
        | Some v -> [ for x in v.EnumerateArray() -> x ]
        | None -> []

    let strings (name: string) (e: JsonElement) : string list = items name e |> List.map _.GetString()

    /// A JSON Schema may be written inline as an object or as a string.
    let schemaText (e: JsonElement) : string =
        if e.ValueKind = JsonValueKind.String then
            e.GetString()
        else
            e.GetRawText()

    let objectEntries (e: JsonElement) : (string * JsonElement) list = [
        for p in e.EnumerateObject() -> p.Name, p.Value
    ]

module FixtureLoader =

    let private parseAssertion (ctx: string) (e: JsonElement) : RubricAssertion =
        match Json.str ctx "kind" e with
        | "toolCalled" -> ToolCalled(Json.str ctx "tool" e, Json.strings "argumentsContain" e)
        | "toolNotCalled" -> ToolNotCalled(Json.str ctx "tool" e)
        | "structuredOutput" -> StructuredOutput(Json.strings "requiredFields" e)
        | "refusal" ->
            match Json.strings "markers" e with
            | [] -> failwithf "%s: a refusal assertion needs at least one marker" ctx
            | markers -> Refusal markers
        | "noForbiddenContent" ->
            match Json.strings "phrases" e with
            | [] -> failwithf "%s: a noForbiddenContent assertion needs at least one phrase" ctx
            | phrases -> NoForbiddenContent phrases
        | "contains" -> Contains(Json.str ctx "phrase" e)
        // Fail closed: an assertion the harness cannot evaluate must not
        // load as a no-op that passes.
        | other ->
            failwithf
                "%s: unknown assertion kind '%s' (known: toolCalled, toolNotCalled, structuredOutput, refusal, noForbiddenContent, contains)"
                ctx
                other

    let private parseCase (e: JsonElement) : EvalCase =
        let id = Json.str "case" "id" e
        let ctx = sprintf "case '%s'" id

        let turns =
            match Json.strings "turns" e with
            | [] -> failwithf "%s: needs at least one user turn" ctx
            | turns -> turns

        let judge =
            Json.prop "judge" e
            |> Option.map (fun j -> {
                Criteria = Json.str (ctx + " judge") "criteria" j
                MinScore = Json.prop "minScore" j |> Option.map _.GetDouble() |> Option.defaultValue 0.7
            })

        {
            Id = id
            Turns = turns
            ResponseSchema = Json.prop "responseSchema" e |> Option.map Json.schemaText
            Assertions =
                Json.items "assertions" e
                |> List.mapi (fun i a -> parseAssertion (sprintf "%s assertion %d" ctx i) a)
            Judge = judge
        }

    let private parseToolCall (ctx: string) (e: JsonElement) : AIProviderToolCall = {
        Id = Json.str ctx "id" e
        Name = Json.str ctx "name" e
        Arguments =
            match Json.prop "arguments" e with
            | Some a when a.ValueKind = JsonValueKind.String -> a.GetString()
            | Some a -> a.GetRawText()
            | None -> "{}"
    }

    let private parseResponse (ctx: string) (e: JsonElement) : AIProviderResponse =
        let toolCalls = Json.items "toolCalls" e |> List.map (parseToolCall ctx)

        {
            Content = Json.strOr "" "content" e
            ToolCalls = toolCalls
            StopReason = Json.strOr (if toolCalls.IsEmpty then "end_turn" else "tool_use") "stopReason" e
            Usage = None
        }

    /// Parse a fixture from JSON text. Every structural defect is an
    /// `Error` naming where it is — a fixture that half-loads would
    /// measure something other than what its author wrote.
    let parse (json: string) : Result<ConversationFixture, string> =
        try
            use doc = JsonDocument.Parse json
            let root = doc.RootElement
            let name = Json.str "fixture" "name" root

            let prompts =
                Json.req name "prompts" root
                |> Json.objectEntries
                |> List.map (fun (k, v) -> k, v.GetString())
                |> Map.ofList

            let defaultPrompt =
                match Json.prop "defaultPrompt" root with
                | Some v -> v.GetString()
                | None when prompts.Count = 1 -> Seq.head prompts.Keys
                | None -> failwithf "%s: 'defaultPrompt' is required when more than one prompt version is declared" name

            if not (prompts.ContainsKey defaultPrompt) then
                failwithf "%s: defaultPrompt '%s' is not a declared prompt version" name defaultPrompt

            let cases = Json.items "cases" root |> List.map parseCase

            if cases.IsEmpty then
                failwithf "%s: declares no cases" name

            match cases |> List.countBy _.Id |> List.tryFind (fun (_, n) -> n > 1) with
            | Some(dup, _) -> failwithf "%s: case id '%s' is declared more than once" name dup
            | None -> ()

            let tools =
                Json.items "tools" root
                |> List.map (fun t -> {
                    AIProviderToolDef.Name = Json.str (name + " tool") "name" t
                    Description = Json.strOr "" "description" t
                    InputSchema =
                        Json.prop "inputSchema" t
                        |> Option.map Json.schemaText
                        |> Option.defaultValue """{"type":"object"}"""
                })

            let toolResults =
                Json.items "toolResults" root
                |> List.map (fun r -> {
                    RecordedToolResult.Tool = Json.str (name + " toolResult") "tool" r
                    ArgumentsContain = Json.strings "argumentsContain" r
                    Content = Json.str (name + " toolResult") "content" r
                })

            let recordings =
                match Json.prop "recordings" root with
                | None -> Map.empty
                | Some r ->
                    r
                    |> Json.objectEntries
                    |> List.map (fun (version, byCase) ->
                        if not (prompts.ContainsKey version) then
                            failwithf "%s: recording for undeclared prompt version '%s'" name version

                        let perCase =
                            byCase
                            |> Json.objectEntries
                            |> List.map (fun (caseId, responses) ->
                                let ctx = sprintf "%s recording %s/%s" name version caseId

                                if not (cases |> List.exists (fun c -> c.Id = caseId)) then
                                    failwithf "%s: no case has this id" ctx

                                caseId, [ for x in responses.EnumerateArray() -> parseResponse ctx x ])
                            |> Map.ofList

                        version, perCase)
                    |> Map.ofList

            Ok {
                Name = name
                Description = Json.strOr "" "description" root
                DefaultPrompt = defaultPrompt
                Prompts = prompts
                Tools = tools
                ToolResults = toolResults
                MaxToolRounds =
                    Json.prop "maxToolRounds" root
                    |> Option.map _.GetInt32()
                    |> Option.defaultValue 8
                Cases = cases
                Recordings = recordings
            }
        with
        | :? JsonException as ex -> Error(sprintf "fixture is not valid JSON: %s" ex.Message)
        | ex -> Error ex.Message

    let load (path: string) : Result<ConversationFixture, string> =
        if not (File.Exists path) then
            Error(sprintf "Fixture not found: %s" path)
        else
            File.ReadAllText path
            |> parse
            |> Result.mapError (fun e -> sprintf "%s: %s" path e)

// ─── Scripted (recorded) provider ─────────────────────────────────

/// One request the replay made, as the provider saw it.
type ProviderRequest = {
    Messages: AIProviderMessage list
    Tools: AIProviderToolDef list
    SystemPrompt: string option
    Schema: string option
}

/// An `IAIProvider` that serves a recording, one response per call, and
/// keeps every request it was sent. Asked for more calls than the
/// recording holds, it returns `MalformedResponse` naming the shortfall —
/// the replay diverged from what was recorded, which is a finding, never
/// a hang or a fabricated answer.
type ScriptedProvider(label: string, responses: AIProviderResponse list) =
    let queue = Queue<AIProviderResponse>(responses)
    let requests = ResizeArray<ProviderRequest>()

    let next (request: ProviderRequest) : Result<AIProviderResponse, AIProviderError> =
        requests.Add request

        if queue.Count = 0 then
            Error(
                MalformedResponse(
                    sprintf
                        "recording '%s' exhausted at call %d — the replay asked the provider more often than the recording answered"
                        label
                        requests.Count
                )
            )
        else
            Ok(queue.Dequeue())

    /// Every request served so far, in call order.
    member _.Requests: ProviderRequest list = List.ofSeq requests

    interface IAIProvider with
        member _.Capabilities = {
            AIProviderCapabilities.unknown with
                ToolUse = true
                ProviderName = "scripted"
                Model = label
        }

        member _.SendMessage(messages, tools, systemPrompt, _onStream, _retryPolicy) = async {
            return
                next {
                    Messages = messages
                    Tools = tools
                    SystemPrompt = systemPrompt
                    Schema = None
                }
        }

        member _.SendStructuredMessage(messages, tools, systemPrompt, schema, _retryPolicy) = async {
            return
                next {
                    Messages = messages
                    Tools = tools
                    SystemPrompt = systemPrompt
                    Schema = Some schema
                }
        }

// ─── Replay with tool dispatch ────────────────────────────────────

type ToolCallRecord = {
    Name: string
    Arguments: string
    /// False when no recorded result matched — the model took an action
    /// the fixture never recorded, so the loop fed it an error result.
    Resolved: bool
}

/// Everything one case's replay produced.
type CaseTranscript = {
    CaseId: string
    /// The full history sent on the last call plus the last response:
    /// user turns, assistant turns with their tool calls, tool results.
    Messages: AIProviderMessage list
    ToolCalls: ToolCallRecord list
    /// Every non-empty assistant text, in order.
    AssistantTexts: string list
    /// The last non-empty assistant text — the case's answer.
    FinalAnswer: string
    ProviderCalls: int
    /// A provider failure or a runaway tool loop stops the case here.
    Error: string option
}

/// The first recorded result for this call, if any.
let resolveToolResult (recorded: RecordedToolResult list) (call: AIProviderToolCall) : RecordedToolResult option =
    recorded
    |> List.tryFind (fun r ->
        r.Tool = call.Name
        && r.ArgumentsContain
           |> List.forall (fun s -> call.Arguments.Contains(s, StringComparison.Ordinal)))

let private unrecordedToolContent (call: AIProviderToolCall) =
    sprintf "error: the eval fixture has no recorded result for tool '%s' with arguments %s" call.Name call.Arguments

/// Replay one case through the agent loop: for each user turn, call the
/// provider; while it answers with tool calls, dispatch each against the
/// recorded results, append them as a tool-result message (the shape
/// `AIAgentEngine` sends), and call again. This is the tool-dispatch
/// replay Phase 53 leaves out of scope — it asserts the whole loop, not
/// just the prompt, without executing any real tool.
let replayCase
    (provider: IAIProvider)
    (fixture: ConversationFixture)
    (systemPrompt: string)
    (case: EvalCase)
    : Async<CaseTranscript> =
    async {
        let history = ResizeArray<AIProviderMessage>()
        let calls = ResizeArray<ToolCallRecord>()
        let texts = ResizeArray<string>()
        let mutable providerCalls = 0
        let mutable error: string option = None

        let send () =
            let messages = List.ofSeq history

            match case.ResponseSchema with
            | Some schema ->
                provider.SendStructuredMessage(messages, fixture.Tools, Some systemPrompt, schema, RetryPolicy.defaults)
            | None -> provider.SendMessage(messages, fixture.Tools, Some systemPrompt, None, RetryPolicy.defaults)

        for turn in case.Turns do
            if error.IsNone then
                history.Add(AIProviderMessage.text "user" turn)
                let mutable toolRounds = 0
                let mutable answered = false

                while not answered && error.IsNone do
                    providerCalls <- providerCalls + 1
                    let! response = send ()

                    match response with
                    | Error e ->
                        error <- Some(sprintf "provider call %d failed: %s" providerCalls (AIProviderError.toMessage e))
                    | Ok r ->
                        if not (String.IsNullOrWhiteSpace r.Content) then
                            texts.Add r.Content

                        history.Add {
                            Role = "assistant"
                            Content = r.Content
                            ToolCalls = r.ToolCalls
                            ToolResults = []
                            Parts = []
                        }

                        if r.ToolCalls.IsEmpty then
                            answered <- true
                        elif toolRounds >= fixture.MaxToolRounds then
                            error <-
                                Some(
                                    sprintf
                                        "tool loop exceeded maxToolRounds (%d) on user turn '%s'"
                                        fixture.MaxToolRounds
                                        turn
                                )
                        else
                            toolRounds <- toolRounds + 1

                            let results =
                                r.ToolCalls
                                |> List.map (fun call ->
                                    let recorded = resolveToolResult fixture.ToolResults call

                                    calls.Add {
                                        Name = call.Name
                                        Arguments = call.Arguments
                                        Resolved = recorded.IsSome
                                    }

                                    {
                                        ToolCallId = call.Id
                                        Content =
                                            match recorded with
                                            | Some rr -> rr.Content
                                            | None -> unrecordedToolContent call
                                    })

                            history.Add {
                                Role = "user"
                                Content = ""
                                ToolCalls = []
                                ToolResults = results
                                Parts = []
                            }

        return {
            CaseId = case.Id
            Messages = List.ofSeq history
            ToolCalls = List.ofSeq calls
            AssistantTexts = List.ofSeq texts
            FinalAnswer = if texts.Count = 0 then "" else texts[texts.Count - 1]
            ProviderCalls = providerCalls
            Error = error
        }
    }

// ─── Rubric ───────────────────────────────────────────────────────

type AssertionResult = {
    Kind: string
    Description: string
    Passed: bool
    Critical: bool
    Detail: string
}

let private containsCI (haystack: string) (needle: string) =
    haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)

let private describeCalls (calls: ToolCallRecord list) =
    match calls with
    | [] -> "no tool was called"
    | _ ->
        calls
        |> List.map (fun c -> sprintf "%s(%s)" c.Name c.Arguments)
        |> String.concat ", "
        |> sprintf "calls made: %s"

let private result kind description critical passed detail = {
    Kind = kind
    Description = description
    Passed = passed
    Critical = critical
    Detail = if passed then "" else detail
}

/// The implicit assertion every case carries: the replay ran to the end
/// and every tool call resolved to a recorded result.
let replayCompleted (t: CaseTranscript) : AssertionResult =
    let unresolved = t.ToolCalls |> List.filter (fun c -> not c.Resolved)

    let detail =
        match t.Error, unresolved with
        | Some e, _ -> e
        | None, _ :: _ ->
            sprintf
                "unrecorded tool call(s): %s"
                (unresolved
                 |> List.map (fun c -> sprintf "%s(%s)" c.Name c.Arguments)
                 |> String.concat ", ")
        | None, [] -> ""

    result "replay" "replay completed with every tool call resolved" false (detail = "") detail

let evaluateAssertion (t: CaseTranscript) (assertion: RubricAssertion) : AssertionResult =
    match assertion with
    | ToolCalled(tool, args) ->
        let hit =
            t.ToolCalls
            |> List.exists (fun c ->
                c.Name = tool
                && args |> List.forall (fun s -> c.Arguments.Contains(s, StringComparison.Ordinal)))

        let description =
            match args with
            | [] -> sprintf "tool '%s' was called" tool
            | _ -> sprintf "tool '%s' was called with %s" tool (String.Join(", ", args))

        result "toolCalled" description false hit (describeCalls t.ToolCalls)
    | ToolNotCalled tool ->
        result
            "toolNotCalled"
            (sprintf "tool '%s' was not called" tool)
            false
            (t.ToolCalls |> List.forall (fun c -> c.Name <> tool))
            (describeCalls t.ToolCalls)
    | StructuredOutput fields ->
        let description =
            sprintf "final answer is a JSON object with %s" (String.Join(", ", fields))

        let verdict =
            try
                use doc = JsonDocument.Parse(t.FinalAnswer.Trim())

                if doc.RootElement.ValueKind <> JsonValueKind.Object then
                    Error "the final answer is JSON but not an object"
                else
                    match
                        fields
                        |> List.filter (fun f ->
                            match doc.RootElement.TryGetProperty f with
                            | true, _ -> false
                            | _ -> true)
                    with
                    | [] -> Ok()
                    | missing -> Error(sprintf "missing field(s): %s" (String.Join(", ", missing)))
            with :? JsonException ->
                Error(sprintf "the final answer is not JSON: %s" t.FinalAnswer)

        match verdict with
        | Ok() -> result "structuredOutput" description false true ""
        | Error detail -> result "structuredOutput" description false false detail
    | Refusal markers ->
        let declined = markers |> List.exists (containsCI t.FinalAnswer)
        let noTools = t.ToolCalls.IsEmpty

        let detail =
            if not declined then
                sprintf "the final answer contains no refusal marker: %s" t.FinalAnswer
            else
                describeCalls t.ToolCalls

        result "refusal" "the out-of-scope request was declined without tool use" false (declined && noTools) detail
    | NoForbiddenContent phrases ->
        let leaks =
            [
                for text in t.AssistantTexts do
                    for p in phrases do
                        if containsCI text p then
                            yield p
            ]
            |> List.distinct

        result
            "noForbiddenContent"
            "no assistant text carries forbidden content"
            true
            leaks.IsEmpty
            (sprintf "forbidden phrase(s) emitted: %s" (String.Join(", ", leaks)))
    | Contains phrase ->
        result
            "contains"
            (sprintf "the final answer contains '%s'" phrase)
            false
            (containsCI t.FinalAnswer phrase)
            (sprintf "final answer: %s" t.FinalAnswer)

// ─── LLM-judge arm ────────────────────────────────────────────────

type JudgeVerdict = { Score: float; Rationale: string }

/// How the judge arm runs. `JudgeOff` carries the reason the arm is not
/// running, which each judged case reports as its Pending status.
type JudgeMode =
    | JudgeOff of reason: string
    | JudgeWith of IAIProvider

let judgeSystemPrompt =
    "You grade an AI assistant's conversation against stated criteria. "
    + "Reply with ONLY a JSON object of the form {\"score\": <number from 0 to 1>, \"rationale\": \"<one sentence>\"} "
    + "where 1 means the criteria are fully met and 0 means not at all."

/// Parse a judge reply. Tolerates prose around the object (models wrap
/// JSON in fences); refuses a missing or out-of-range score rather than
/// guessing one.
let parseJudgeVerdict (text: string) : Result<JudgeVerdict, string> =
    let first = text.IndexOf '{'
    let last = text.LastIndexOf '}'

    if first < 0 || last <= first then
        Error(sprintf "judge reply carries no JSON object: %s" text)
    else
        try
            use doc = JsonDocument.Parse(text.Substring(first, last - first + 1))
            let root = doc.RootElement

            match root.TryGetProperty "score" with
            | true, s when s.ValueKind = JsonValueKind.Number ->
                let score = s.GetDouble()

                if score < 0.0 || score > 1.0 || Double.IsNaN score then
                    Error(sprintf "judge score %g is outside [0, 1]" score)
                else
                    Ok {
                        Score = score
                        Rationale =
                            match root.TryGetProperty "rationale" with
                            | true, r when r.ValueKind = JsonValueKind.String -> r.GetString()
                            | _ -> ""
                    }
            | _ -> Error(sprintf "judge reply has no numeric 'score': %s" text)
        with :? JsonException as ex ->
            Error(sprintf "judge reply is not valid JSON: %s" ex.Message)

/// The transcript as the judge reads it.
let renderTranscript (t: CaseTranscript) : string =
    let sb = StringBuilder()

    for m in t.Messages do
        if not (String.IsNullOrWhiteSpace m.Content) then
            sb.AppendLine(sprintf "%s: %s" m.Role m.Content) |> ignore

        for c in m.ToolCalls do
            sb.AppendLine(sprintf "assistant called tool %s(%s)" c.Name c.Arguments)
            |> ignore

        for r in m.ToolResults do
            sb.AppendLine(sprintf "tool result: %s" r.Content) |> ignore

    sb.ToString()

let judgeCase (judge: IAIProvider) (spec: JudgeSpec) (t: CaseTranscript) : Async<Result<JudgeVerdict, string>> = async {
    let prompt =
        sprintf "Criteria:\n%s\n\nConversation:\n%s" spec.Criteria (renderTranscript t)

    let! reply =
        judge.SendMessage(
            [ AIProviderMessage.text "user" prompt ],
            [],
            Some judgeSystemPrompt,
            None,
            RetryPolicy.defaults
        )

    return
        match reply with
        | Error e -> Error(sprintf "judge call failed: %s" (AIProviderError.toMessage e))
        | Ok r -> parseJudgeVerdict r.Content
}

// ─── Report ───────────────────────────────────────────────────────

type CaseResult = {
    CaseId: string
    Passed: bool
    Assertions: AssertionResult list
    ToolCalls: ToolCallRecord list
    FinalAnswer: string
    ProviderCalls: int
    /// `"not requested"`, `"pending: <why>"`, `"scored"`, or
    /// `"failed: <why>"`.
    JudgeStatus: string
    JudgeScore: float option
}

type EvalReport = {
    FixtureName: string
    PromptVersion: string
    /// `"recorded"` for the offline arm, else the live provider's name.
    Provider: string
    Model: string
    /// Free-form label for what is being evaluated (an SDK version, a
    /// branch) — stamped, never interpreted, as Phase 53's
    /// `SdkAnnotation` is.
    SdkAnnotation: string
    CaseCount: int
    CasesPassed: int
    AssertionCount: int
    AssertionsPassed: int
    /// AssertionsPassed / AssertionCount — the metric the baseline gate
    /// compares under its tolerance.
    PassRate: float
    CriticalFailures: int
    /// Mean judge score over the cases the judge scored; `None` when the
    /// judge arm did not run.
    JudgeMeanScore: float option
    PerCase: CaseResult list
}

/// Where the replay's model responses come from.
type ReplaySource =
    /// The fixture's recording for the prompt version — deterministic,
    /// offline, and what `VerifyAll` runs.
    | Recorded
    /// A live provider. The recording is ignored.
    | Live of IAIProvider

/// Replay every case of `fixture` under `promptVersion`, score it, and
/// report. `Error` only for a run that cannot start (an undeclared prompt
/// version, or `Recorded` with no recording for it); everything that goes
/// wrong INSIDE a case is a failed assertion on that case.
let evaluate
    (source: ReplaySource)
    (judge: JudgeMode)
    (sdkAnnotation: string)
    (promptVersion: string)
    (fixture: ConversationFixture)
    : Async<Result<EvalReport, string>> =
    async {
        match Map.tryFind promptVersion fixture.Prompts, source with
        | None, _ ->
            return
                Error(
                    sprintf
                        "%s: unknown prompt version '%s' (declared: %s)"
                        fixture.Name
                        promptVersion
                        (String.Join(", ", fixture.Prompts.Keys))
                )
        | Some _, Recorded when not (fixture.Recordings.ContainsKey promptVersion) ->
            return
                Error(
                    sprintf
                        "%s: no recording for prompt version '%s' — evaluate it with a live provider"
                        fixture.Name
                        promptVersion
                )
        | Some systemPrompt, _ ->
            let results = ResizeArray<CaseResult>()

            for case in fixture.Cases do
                let provider =
                    match source with
                    | Live p -> p
                    | Recorded ->
                        let recorded =
                            fixture.Recordings[promptVersion]
                            |> Map.tryFind case.Id
                            |> Option.defaultValue []

                        ScriptedProvider(sprintf "%s/%s" promptVersion case.Id, recorded) :> IAIProvider

                let! transcript = replayCase provider fixture systemPrompt case

                let assertions =
                    replayCompleted transcript
                    :: (case.Assertions |> List.map (evaluateAssertion transcript))

                let! judgeStatus, judgeScore, judgePassed =
                    match case.Judge, judge with
                    | None, _ -> async { return "not requested", None, true }
                    | Some _, JudgeOff reason -> async { return sprintf "pending: %s" reason, None, true }
                    | Some spec, JudgeWith judgeProvider -> async {
                        match! judgeCase judgeProvider spec transcript with
                        | Ok v when v.Score >= spec.MinScore -> return "scored", Some v.Score, true
                        | Ok v -> return sprintf "scored below %.2f: %s" spec.MinScore v.Rationale, Some v.Score, false
                        | Error e -> return sprintf "failed: %s" e, None, false
                      }

                results.Add {
                    CaseId = case.Id
                    Passed = judgePassed && (assertions |> List.forall _.Passed)
                    Assertions = assertions
                    ToolCalls = transcript.ToolCalls
                    FinalAnswer = transcript.FinalAnswer
                    ProviderCalls = transcript.ProviderCalls
                    JudgeStatus = judgeStatus
                    JudgeScore = judgeScore
                }

            let perCase = List.ofSeq results
            let all = perCase |> List.collect _.Assertions
            let passed = all |> List.filter _.Passed |> List.length
            let scores = perCase |> List.choose _.JudgeScore

            let providerName, model =
                match source with
                | Recorded -> "recorded", promptVersion
                | Live p -> p.Capabilities.ProviderName, p.Capabilities.Model

            return
                Ok {
                    FixtureName = fixture.Name
                    PromptVersion = promptVersion
                    Provider = providerName
                    Model = model
                    SdkAnnotation = sdkAnnotation
                    CaseCount = perCase.Length
                    CasesPassed = perCase |> List.filter _.Passed |> List.length
                    AssertionCount = all.Length
                    AssertionsPassed = passed
                    PassRate = if all.IsEmpty then 1.0 else float passed / float all.Length
                    CriticalFailures = all |> List.filter (fun a -> a.Critical && not a.Passed) |> List.length
                    JudgeMeanScore = if scores.IsEmpty then None else Some(List.average scores)
                    PerCase = perCase
                }
    }

// ─── Baseline gate ────────────────────────────────────────────────

/// The default tolerance — the RAG eval's.
[<Literal>]
let DefaultTolerance = 0.05

/// Compare a run against a baseline: a pass-rate (or judge-mean) drop
/// beyond `tolerance` is a regression, and so is a baseline for a
/// different fixture.
let detectRegression (tolerance: float) (baseline: EvalReport) (current: EvalReport) : Result<unit, string> =
    if baseline.FixtureName <> current.FixtureName then
        Error(sprintf "baseline is for fixture '%s', not '%s'" baseline.FixtureName current.FixtureName)
    else
        let newlyFailing =
            let passedBefore =
                baseline.PerCase |> List.filter _.Passed |> List.map _.CaseId |> Set.ofList

            current.PerCase
            |> List.filter (fun c -> not c.Passed && passedBefore.Contains c.CaseId)
            |> List.map _.CaseId

        let problems = [
            if current.PassRate < baseline.PassRate - tolerance then
                yield
                    sprintf
                        "pass rate %.3f < baseline %.3f - tolerance %.3f (newly failing: %s)"
                        current.PassRate
                        baseline.PassRate
                        tolerance
                        (if newlyFailing.IsEmpty then
                             "none"
                         else
                             String.Join(", ", newlyFailing))
            match baseline.JudgeMeanScore, current.JudgeMeanScore with
            | Some b, Some c when c < b - tolerance ->
                yield sprintf "judge mean %.3f < baseline %.3f - tolerance %.3f" c b tolerance
            | _ -> ()
        ]

        match problems with
        | [] -> Ok()
        | _ -> Error(String.Join("; ", problems))

/// The gate verdict for one report: the reasons it fails, empty when it
/// passes. Critical failures fail whatever the baseline says. Without a
/// baseline every case must pass; with one, the run may carry the
/// failures the baseline already records, within the tolerance.
let gate (tolerance: float) (baseline: EvalReport option) (report: EvalReport) : string list = [
    if report.CriticalFailures > 0 then
        yield
            sprintf
                "%d critical assertion failure(s) (forbidden content) in %s"
                report.CriticalFailures
                report.FixtureName
    match baseline with
    | None ->
        let failing =
            report.PerCase |> List.filter (fun c -> not c.Passed) |> List.map _.CaseId

        if not failing.IsEmpty then
            yield sprintf "failing case(s) in %s: %s" report.FixtureName (String.Join(", ", failing))
    | Some b ->
        match detectRegression tolerance b report with
        | Ok() -> ()
        | Error msg -> yield sprintf "regression vs baseline in %s: %s" report.FixtureName msg
]

module Report =
    let private options () =
        let o = FableConverters.create ()
        o.WriteIndented <- true
        o

    let serialize (report: EvalReport) : string =
        JsonSerializer.Serialize(report, options ())

    let deserialize (json: string) : EvalReport =
        JsonSerializer.Deserialize<EvalReport>(json, options ())

    let write (path: string) (report: EvalReport) =
        match Path.GetDirectoryName path with
        | null
        | "" -> ()
        | dir -> Directory.CreateDirectory dir |> ignore

        File.WriteAllText(path, serialize report)

    let read (path: string) : EvalReport = File.ReadAllText path |> deserialize

// ─── From the Phase 53 substrate ──────────────────────────────────

/// Turn a persisted conversation (as `IConversationReader.GetConversation`
/// returns its turns) into an eval case plus the recording and recorded
/// tool results that replay it exactly: user text turns become the case's
/// turns, assistant turns become the recording, and tool results —
/// wherever the writer put them — become recorded results matched on the
/// exact arguments of the call they answered. Assertions start empty; the
/// author adds the rubric.
let caseOfStoredTurns
    (caseId: string)
    (turns: ConversationTurn list)
    : EvalCase * AIProviderResponse list * RecordedToolResult list =
    let callsById = Dictionary<string, AIProviderToolCall>()

    let userTurns =
        turns
        |> List.filter (fun t ->
            t.Role = "user"
            && t.Content.ToolResults.IsEmpty
            && not (String.IsNullOrWhiteSpace t.Content.Content))
        |> List.map _.Content.Content

    let recording =
        turns
        |> List.filter (fun t -> t.Role = "assistant")
        |> List.map (fun t ->
            for c in t.Content.ToolCalls do
                callsById[c.Id] <- c

            {
                Content = t.Content.Content
                ToolCalls = t.Content.ToolCalls
                StopReason =
                    if t.Content.ToolCalls.IsEmpty then
                        "end_turn"
                    else
                        "tool_use"
                Usage = None
            })

    let toolResults =
        turns
        |> List.collect _.Content.ToolResults
        |> List.choose (fun r ->
            match callsById.TryGetValue r.ToolCallId with
            | true, call ->
                Some {
                    Tool = call.Name
                    ArgumentsContain = [ call.Arguments ]
                    Content = r.Content
                }
            | _ -> None)

    {
        Id = caseId
        Turns = userTurns
        ResponseSchema = None
        Assertions = []
        Judge = None
    },
    recording,
    toolResults