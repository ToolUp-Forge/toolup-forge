// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.InProcess.FastPathTriageResolverTests

open System
open System.Threading
open Expecto
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.FastPathTriageResolver

// ─── Phase 6j.B — Tier-3 triage ──────────────────────────────────
//
// Two bars, and they are different in kind.
//
// The PURE bar is `planTriage` + `parseTriageDecision` +
// `isEligibleInstruction`: everything that decides whether a model's
// answer becomes a state mutation. It is tested directly because it is
// the whole safety argument — a wrong hit changes the user's screen
// and hides their request from the agent that could have handled it.
//
// The LOOP bar is `AIAgentEngine.runAgentLoop`: the assertion that the
// intercept sits BEFORE any provider call, that it is inert unless
// deliberately turned on, and that a hit emits the action, appends the
// recap, and does not run the loop. Every provider in this file counts
// its own calls, so "the full loop did not run" is asserted against a
// number rather than inferred from an absence.

// ─── Test-only fakes ─────────────────────────────────────────────

/// Field registry over a fixed snapshot. `None` models a surface the
/// companion knows nothing about.
type private StubFieldRegistry(snapshot: AIFieldSnapshot option) =
    interface IAIFieldRegistry with
        member _.Describe(_moduleId, _page) = async { return snapshot }

/// Counts calls on BOTH provider entry points separately. The agent
/// loop only ever calls `SendMessage`; triage only ever calls
/// `SendStructuredMessage`. Keeping the counters apart is what lets a
/// test say "the loop did not run" without relying on the absence of
/// an SSE event.
type private ScriptedProvider(supportsTriage: bool, structuredReply: string, agentReply: string) =
    let mutable structuredCalls = 0
    let mutable sendCalls = 0
    member _.StructuredCalls = structuredCalls
    member _.SendCalls = sendCalls

    /// The system prompt the last triage call was given — the only way
    /// to check that the declared surface actually reached the model.
    member val LastStructuredSystemPrompt: string option = None with get, set

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = supportsTriage
            TriageModelId = if supportsTriage then Some "test-cheap-model" else None
            ProviderName = "test-triage"
            Model = "test-triage-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            sendCalls <- sendCalls + 1

            return
                Ok {
                    Content = agentReply
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member this.SendStructuredMessage(_messages, _tools, systemPrompt, _schema, _retryPolicy) = async {
            structuredCalls <- structuredCalls + 1
            this.LastStructuredSystemPrompt <- systemPrompt

            return
                Ok {
                    Content = structuredReply
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

/// Provider whose triage call fails. Exercises the `provider-error`
/// stratum — the class that must still fall through cleanly.
type private FailingTriageProvider() =
    let mutable sendCalls = 0
    member _.SendCalls = sendCalls

    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = true
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = true
            TriageModelId = Some "test-cheap-model"
            ProviderName = "test-triage"
            Model = "test-triage-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            sendCalls <- sendCalls + 1

            return
                Ok {
                    Content = "agent answered"
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member _.SendStructuredMessage(_messages, _tools, _systemPrompt, _schema, _retryPolicy) = async {
            return Error(SchemaUnsupported("structured-output", "test-only failure"))
        }

/// Records every `Notification.ModuleAction` published, so the locked
/// "no new wire shape — reuse ModuleAction" decision is asserted on the
/// value that actually goes to the client.
type private RecordingNotificationChannel() =
    let published = ResizeArray<string * string * string>()
    member _.Published = published |> List.ofSeq

    interface INotificationChannel with
        member _.Publish(_scopeId, notification) = async {
            match notification with
            | Notification.ModuleAction(moduleId, actionKey, payloadJson) ->
                lock published (fun () -> published.Add((moduleId, actionKey, payloadJson)))
            | _ -> ()
        }

        member _.Subscribe(_scopeId, _handler) = async { return Guid.NewGuid() }
        member _.Unsubscribe(_subscriptionId) = async { return () }

// ─── Fixtures ────────────────────────────────────────────────────

let private countryField: AIFieldDescriptor = {
    FieldId = "country"
    Description = "the country filter applied to the dataset"
    ValueType = "string-option"
    InstructionPatterns = [ "set country to {value}"; "clear country" ]
    ValueAliases = [ "britain", "UK"; "great britain", "UK" ]
}

let private periodField: AIFieldDescriptor = {
    FieldId = "period"
    Description = "the reporting period"
    ValueType = "string"
    InstructionPatterns = [ "set period to {value}" ]
    ValueAliases = []
}

let private snapshot: AIFieldSnapshot = {
    ModuleId = "sales"
    Page = Some "/dashboard"
    Fields = [ countryField; periodField ]
    StateSummary = "country=(none), period=2024-Q1"
}

let private config =
    FastPathTriageConfig.create (StubFieldRegistry(Some snapshot) :> IAIFieldRegistry)

let private decision (d: string) (fieldId: string option) (value: string option) (confidence: float) = {
    Decision = d
    FieldId = fieldId
    Value = value
    Confidence = confidence
    Reason = ""
}

// ─── Harness ─────────────────────────────────────────────────────

/// The caller identity the harness seeds. Load-bearing twice over:
/// `ToolContext.emitAction` refuses to publish without a resolved,
/// non-anonymous user id, and `resolveScope` falls back to it when no
/// `ToolUp.StorageScope` is seeded — so it is also the scope the
/// telemetry rows land under.
let private HarnessUserId = "alice"

let private buildContext
    (triageConfig: FastPathTriageConfig option)
    (eventStore: IEventStore)
    (channel: INotificationChannel)
    : HttpContext =
    let services = ServiceCollection()
    services.AddSingleton<IEventStore>(eventStore) |> ignore
    services.AddSingleton<INotificationChannel>(channel) |> ignore

    match triageConfig with
    | Some c -> services.AddSingleton<FastPathTriageConfig>(c) |> ignore
    | None -> ()

    let ctx = DefaultHttpContext()
    ctx.RequestServices <- services.BuildServiceProvider()
    ctx.Items["ToolUp.UserId"] <- box HarnessUserId
    ctx :> HttpContext

/// Run the agent loop with a given triage configuration and report
/// everything a test might assert on.
let private runLoop (triageConfig: FastPathTriageConfig option) (provider: IAIProvider) (instruction: string) = async {
    let eventStore = InMemoryEventStore.InMemoryEventStore() :> IEventStore
    let channel = RecordingNotificationChannel()
    let ctx = buildContext triageConfig eventStore (channel :> INotificationChannel)
    let events = ResizeArray<AIStreamEvent>()

    let! finalMessages =
        AIAgentEngine.runAgentLoop
            provider
            (AIToolRegistry.AIToolRegistry())
            (ClientToolDispatch.ClientToolDispatchRegistry())
            ctx
            (Guid.NewGuid())
            (Guid.NewGuid())
            AISurface.FullPage
            (Some "sales")
            (Some "/dashboard")
            CancellationToken.None
            [ AIProviderMessage.text "user" instruction ]
            None
            (fun evt -> lock events (fun () -> events.Add evt))

    // The harness seeds no `ToolUp.StorageScope`, so `resolveScope`
    // falls back to the resolved user id — `alice`, not `anonymous`.
    // Reading the wrong scope here would report zero rows for a tier
    // that wrote them correctly, which is exactly how a telemetry
    // assertion passes vacuously in the other direction.
    let! triageRows = eventStore.ReadBySource(HarnessUserId, FastPathSourceModule)

    return {|
        Final = finalMessages
        Actions = channel.Published
        Events = lock events (fun () -> events.ToArray() |> Array.toList)
        TriageRows = triageRows |> Seq.filter (fun e -> e.EventType = TriageEventType) |> List.ofSeq
    |}
}

// ─── Pure-stage tests ────────────────────────────────────────────

let private pureTests =
    testList "the decision, before any IO" [
        testCase "a confident, declared, plainly-valued set_field is a hit"
        <| fun _ ->
            match planTriage config snapshot (decision "set_field" (Some "country") (Some "UK") 0.97) with
            | TriageSetField(field, value, _) ->
                Expect.equal field.FieldId "country" "resolves to the declared field"
                Expect.equal value (Some "UK") "carries the value"
            | TriageFallThrough o -> failtestf "expected a hit, fell through as %s" o

        testCase "needs_full_agent falls through, however confident"
        <| fun _ ->
            // Confidence is not a licence: the model's own decision is
            // the primary signal and a high number attached to
            // `needs_full_agent` says "certainly not triageable".
            let plan = planTriage config snapshot (decision "needs_full_agent" None None 0.99)
            Expect.equal plan (TriageFallThrough OutcomeNeedsFullAgent) "the model's own refusal is honoured"

        testCase "a field the surface does not declare is refused, not guessed"
        <| fun _ ->
            // The regression this guards: a model that hallucinates a
            // plausible id ("region") must not have it forwarded to a
            // decoder that might partially match it.
            let plan =
                planTriage config snapshot (decision "set_field" (Some "region") (Some "EMEA") 0.99)

            Expect.equal plan (TriageFallThrough OutcomeUnknownField) "an undeclared id never reaches the wire"

        testCase "confidence under the floor falls through"
        <| fun _ ->
            let plan =
                planTriage config snapshot (decision "set_field" (Some "country") (Some "UK") 0.5)

            Expect.equal plan (TriageFallThrough OutcomeLowConfidence) "an unsure hit is not a hit"

        testCase "a missing confidence reads as zero, not as certainty"
        <| fun _ ->
            // The schema requires `confidence`; a provider that omitted
            // it did not honour the schema, and that is the last moment
            // to start trusting its field choice.
            match parseTriageDecision """{"decision":"set_field","fieldId":"country","value":"UK"}""" with
            | None -> failtest "the answer is well-formed JSON and must parse"
            | Some d ->
                Expect.equal d.Confidence 0.0 "absent confidence is zero"

                Expect.equal
                    (planTriage config snapshot d)
                    (TriageFallThrough OutcomeLowConfidence)
                    "so it falls through"

        testCase "clearing is allowed on an optional field and refused on a required one"
        <| fun _ ->
            match planTriage config snapshot (decision "set_field" (Some "country") None 0.99) with
            | TriageSetField(_, None, _) -> ()
            | other -> failtestf "clearing a string-option field must be a hit, got %A" other

            Expect.equal
                (planTriage config snapshot (decision "set_field" (Some "period") None 0.99))
                (TriageFallThrough OutcomeClearUnsupported)
                "a non-optional field cannot be cleared"

        testCase "declared aliases are resolved before the value reaches the wire"
        <| fun _ ->
            match planTriage config snapshot (decision "set_field" (Some "Country") (Some "britain") 0.99) with
            | TriageSetField(field, value, _) ->
                Expect.equal value (Some "UK") "the alias is folded to its canonical value"
                Expect.equal field.FieldId "country" "and the DECLARED id is used, not the model's casing"
            | other -> failtestf "expected a hit, got %A" other

        testCase "a fenced code block still parses"
        <| fun _ ->
            let fenced =
                "```json\n{\"decision\":\"set_field\",\"fieldId\":\"country\",\"value\":\"UK\",\"confidence\":0.95}\n```"

            match parseTriageDecision fenced with
            | Some d -> Expect.equal d.FieldId (Some "country") "the envelope is wrong but the content is right"
            | None -> failtest "a fenced answer must not be discarded as unparseable"

        testCase "prose is unparseable rather than throwing"
        <| fun _ ->
            Expect.isNone (parseTriageDecision "I think you want to set the country.") "prose does not parse"
            Expect.isNone (parseTriageDecision "") "nor does empty content"
            Expect.isNone (parseTriageDecision "[1,2,3]") "nor does a non-object"

        testCase "questions are refused before the model is ever called"
        <| fun _ ->
            // The inspect-regression mitigation, at its cheapest point.
            // These need live module state and real reasoning; triaging
            // them is how a fast path starts giving wrong answers.
            let max = FastPathTriageConfig.DefaultMaxInstructionChars
            Expect.isFalse (isEligibleInstruction max "what is the country filter set to?") "a question"
            Expect.isFalse (isEligibleInstruction max "why did revenue drop") "an analysis request"
            Expect.isFalse (isEligibleInstruction max "explain the period column") "an explanation request"
            Expect.isTrue (isEligibleInstruction max "set country to UK") "a plain command is eligible"

            Expect.isFalse
                (isEligibleInstruction max (String.replicate 40 "long instruction "))
                "and prose is over the length ceiling"

        testCase "only a plain trailing user turn is a triage candidate"
        <| fun _ ->
            Expect.equal
                (lastUserInstruction [ AIProviderMessage.text "user" "set country to UK" ])
                (Some "set country to UK")
                "a plain user turn is the instruction"

            Expect.isNone
                (lastUserInstruction [
                    AIProviderMessage.text "user" "set country to UK"
                    AIProviderMessage.text "assistant" "done"
                ])
                "a trailing assistant turn is not"

            Expect.isNone
                (lastUserInstruction [
                    {
                        (AIProviderMessage.text "user" "") with
                            ToolResults = [ { ToolCallId = "1"; Content = "{}" } ]
                    }
                ])
                "nor is a tool-result carrier"

        testCase "the action payload carries the declared id and a JSON null for a clear"
        <| fun _ ->
            Expect.equal
                (actionPayloadJson countryField (Some "UK"))
                """{"field":"country","value":"UK","source":"triage"}"""
                "a set carries the value as a JSON string"

            Expect.equal
                (actionPayloadJson countryField None)
                """{"field":"country","value":null,"source":"triage"}"""
                "a clear carries JSON null, not the string \"null\""

        testCase "the prompt carries the declared phrasings, so the two tiers cannot drift"
        <| fun _ ->
            let prompt = buildTriagePrompt snapshot
            Expect.stringContains prompt "country" "the field id"
            Expect.stringContains prompt "set country to {value}" "the author's declared phrasing"
            Expect.stringContains prompt "britain->UK" "the declared alias"
            Expect.stringContains prompt "country=(none), period=2024-Q1" "the state summary"
            Expect.stringContains prompt "needs_full_agent" "and the bias instruction"
    ]

// ─── Loop-intercept tests ────────────────────────────────────────

let private hitReply =
    """{"decision":"set_field","fieldId":"country","value":"UK","confidence":0.96}"""

let private interceptTests =
    testList "the intercept in AIAgentEngine.runAgentLoop" [
        testCaseAsync "no triage config — the full loop runs, untouched"
        <| async {
            // GP 11 in one assertion: a deployment that never opts in
            // must reach the provider exactly as it did pre-6j.B, and
            // must not appear in the tier's telemetry at all.
            let provider = ScriptedProvider(true, hitReply, "agent answered")
            let! r = runLoop None (provider :> IAIProvider) "set country to UK"

            Expect.equal provider.StructuredCalls 0 "triage never called the provider"
            Expect.equal provider.SendCalls 1 "the full agent loop ran"
            Expect.isEmpty r.Actions "and no ModuleAction was emitted"
            Expect.isEmpty r.TriageRows "a turn that was never a candidate is not counted as an attempt"
        }

        testCaseAsync "config present but disabled — the full loop runs"
        <| async {
            let provider = ScriptedProvider(true, hitReply, "agent answered")
            let! r = runLoop (Some { config with Enabled = false }) (provider :> IAIProvider) "set country to UK"

            Expect.equal provider.StructuredCalls 0 "the switch is honoured"
            Expect.equal provider.SendCalls 1 "the full agent loop ran"
            Expect.isEmpty r.TriageRows "and nothing was recorded"
        }

        testCaseAsync "provider does not support triage — the full loop runs"
        <| async {
            // The provider-side gate. A frontier-only connector gains
            // nothing from triage and must not be made to pay for it.
            let provider = ScriptedProvider(false, hitReply, "agent answered")
            let! r = runLoop (Some config) (provider :> IAIProvider) "set country to UK"

            Expect.equal provider.StructuredCalls 0 "SupportsTriage = false is a hard gate"
            Expect.equal provider.SendCalls 1 "the full agent loop ran"
            Expect.isEmpty r.TriageRows "and no attempt was recorded"
        }

        testCaseAsync "the registry knows nothing about the surface — the full loop runs"
        <| async {
            let provider = ScriptedProvider(true, hitReply, "agent answered")
            let emptyRegistry = StubFieldRegistry(None) :> IAIFieldRegistry

            let! r =
                runLoop (Some { config with Registry = emptyRegistry }) (provider :> IAIProvider) "set country to UK"

            Expect.equal provider.StructuredCalls 0 "no snapshot, no prompt, no call"
            Expect.equal provider.SendCalls 1 "the full agent loop ran"
            Expect.isEmpty r.TriageRows "and no attempt was recorded"
        }

        testCaseAsync "a hit emits the ModuleAction, appends the recap, and does NOT run the loop"
        <| async {
            // The acceptance criterion, whole.
            let provider = ScriptedProvider(true, hitReply, "agent answered")
            let! r = runLoop (Some config) (provider :> IAIProvider) "set country to UK"

            Expect.equal provider.StructuredCalls 1 "triage ran"
            Expect.equal provider.SendCalls 0 "and the full agent loop did NOT — that is the whole point of the tier"

            // (1) The locked wire decision: an existing ModuleAction,
            // addressed to the active module, under `_ui.set-field`.
            Expect.equal (List.length r.Actions) 1 "exactly one action was published"
            let moduleId, actionKey, payload = r.Actions.Head
            Expect.equal moduleId "sales" "addressed to the active module"
            Expect.equal actionKey SetFieldActionKey "under the existing _ui.set-field key — no new wire shape"
            Expect.stringContains payload "\"field\":\"country\"" "naming the declared field"
            Expect.stringContains payload "\"value\":\"UK\"" "and the resolved value"

            // (2) Context continuity: the returned history is the user's
            // real instruction plus a synthetic assistant recap, which is
            // what `AIAssistantHandler` persists into both blobs.
            Expect.equal (List.length r.Final) 2 "the pair: the user instruction and the synthetic reply"
            Expect.equal (r.Final[0].Role) "user" "the user's own turn is preserved"
            Expect.equal (r.Final[0].Content) "set country to UK" "verbatim"
            Expect.equal (r.Final[1].Role) "assistant" "followed by the synthetic assistant turn"
            Expect.stringContains (r.Final[1].Content) "country" "whose recap names the field"

            Expect.stringContains
                (r.Final[1].Content)
                "UK"
                "and the value, so the next turn is not reasoning from a gap"

            // (3) The user sees it. Without the MessageComplete the
            // client's streaming buffer is never committed and the reply
            // silently vanishes.
            let completes =
                r.Events
                |> List.filter (function
                    | MessageComplete _ -> true
                    | _ -> false)

            Expect.equal (List.length completes) 1 "the synthetic turn is committed to the chat"
        }

        testCaseAsync "the triage prompt is built from the registry snapshot"
        <| async {
            let provider = ScriptedProvider(true, hitReply, "agent answered")
            let! _ = runLoop (Some config) (provider :> IAIProvider) "set country to UK"

            match provider.LastStructuredSystemPrompt with
            | Some p ->
                Expect.stringContains p "set country to {value}" "the declared phrasing reached the model"
                Expect.stringContains p "sales" "as did the active module"
            | None -> failtest "triage must send a system prompt — without it the model has no field vocabulary"
        }

        testCaseAsync "needs_full_agent falls through to the loop and is recorded as such"
        <| async {
            let provider =
                ScriptedProvider(true, """{"decision":"needs_full_agent","confidence":0.9}""", "agent answered")

            let! r = runLoop (Some config) (provider :> IAIProvider) "set country to the biggest market"

            Expect.equal provider.StructuredCalls 1 "triage ran"
            Expect.equal provider.SendCalls 1 "and the full loop ran after it"
            Expect.isEmpty r.Actions "no action was emitted"
            Expect.equal (List.length r.TriageRows) 1 "the attempt was recorded"
            Expect.stringContains (r.TriageRows.Head.Payload) OutcomeNeedsFullAgent "under the needs-full-agent stratum"
        }

        testCaseAsync "unparseable output falls through — never an error to the user"
        <| async {
            let provider =
                ScriptedProvider(true, "sure, I'll set that for you!", "agent answered")

            let! r = runLoop (Some config) (provider :> IAIProvider) "set country to UK"

            Expect.equal provider.SendCalls 1 "the full loop ran"
            Expect.isEmpty r.Actions "nothing was dispatched on unparseable output"
            Expect.stringContains (r.TriageRows.Head.Payload) OutcomeUnparseable "recorded in its own stratum"

            let errors =
                r.Events
                |> List.filter (function
                    | StreamError _ -> true
                    | _ -> false)

            Expect.isEmpty errors "a triage failure is invisible to the user — they get the ordinary answer"
        }

        testCaseAsync "a failing triage provider falls through"
        <| async {
            let provider = FailingTriageProvider()
            let! r = runLoop (Some config) (provider :> IAIProvider) "set country to UK"

            Expect.equal provider.SendCalls 1 "the full loop ran"
            Expect.stringContains (r.TriageRows.Head.Payload) OutcomeProviderError "recorded as a provider error"
        }

        testCaseAsync "a question is not triaged at all"
        <| async {
            let provider = ScriptedProvider(true, hitReply, "agent answered")
            let! r = runLoop (Some config) (provider :> IAIProvider) "what is the country set to?"

            Expect.equal provider.StructuredCalls 0 "refused before the call — the cheapest possible mitigation"
            Expect.equal provider.SendCalls 1 "the full agent loop handled it"
            Expect.isEmpty r.TriageRows "and it is not counted against the tier's hit rate"
        }

        testCaseAsync "every attempt writes exactly one stratified telemetry row"
        <| async {
            let provider = ScriptedProvider(true, hitReply, "agent answered")
            let! r = runLoop (Some config) (provider :> IAIProvider) "set country to UK"

            Expect.equal (List.length r.TriageRows) 1 "one row per attempt — the denominator cannot be inflated"
            let row = r.TriageRows.Head
            Expect.equal row.SourceModule FastPathSourceModule "on the existing fast-path source"
            Expect.stringContains row.Payload OutcomeHit "carrying the outcome"
            Expect.stringContains row.Payload "\"FieldId\":\"country\"" "the resolved field"
            Expect.stringContains row.Payload "test-cheap-model" "and the declared triage model id"
        }
    ]

// ─── Rollup ──────────────────────────────────────────────────────

let private rollupTests =
    testList "the /dev/ai-fastpath triage rollup" [
        testCase "counts every declared outcome, including the ones with no rows"
        <| fun _ ->
            let attempt (outcome: string) (latency: float) : TriageEventPayload = {
                ConversationId = Guid.NewGuid()
                TaskId = Guid.NewGuid()
                ModuleId = "sales"
                Outcome = outcome
                FieldId = ""
                ProviderName = "test"
                ProviderModel = "test"
                TriageModelId = "test-cheap-model"
                LatencyMs = latency
                InstructionChars = 17
            }

            let rollup =
                FastPathTelemetryHandler.computeTriageRollup [
                    attempt OutcomeHit 400.0
                    attempt OutcomeHit 600.0
                    attempt OutcomeNeedsFullAgent 500.0
                    attempt OutcomeUnparseable 500.0
                ]

            Expect.equal rollup.TriageAttempts 4 "every attempt is in the denominator"
            Expect.equal rollup.TriageHits 2 "only resolutions are in the numerator"
            Expect.equal rollup.TriageHitRate 0.5 "hit rate"
            Expect.equal rollup.TriageMeanLatencyMs 500.0 "mean attempt latency"

            Expect.equal
                (List.length rollup.TriageOutcomes)
                (List.length outcomes)
                "the whole vocabulary is reported, so a failure class is visible as present-and-zero"

            Expect.equal
                (rollup.TriageOutcomes
                 |> List.find (fun (t, _) -> t = OutcomeLowConfidence)
                 |> snd)
                0
                "an unseen class reports zero rather than vanishing"

        testCase "an empty window does not divide by zero"
        <| fun _ ->
            let rollup = FastPathTelemetryHandler.computeTriageRollup []
            Expect.equal rollup.TriageAttempts 0 "no attempts"
            Expect.equal rollup.TriageHitRate 0.0 "and no NaN"
            Expect.equal rollup.TriageMeanLatencyMs 0.0 "nor here"
    ]

let tests =
    testList "Phase 6j.B — Tier-3 fast-path triage" [ pureTests; interceptTests; rollupTests ]