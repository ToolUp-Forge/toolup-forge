// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 663 — the calibration tool's plumbing, driven by a fake provider:
/// the eligibility short-circuit, one call re-scored at four floors, the
/// failure strata, and the result-file schema round-trip. No network, no
/// key.
module ToolUp.TriageCalibration.Tests.Tests

open System
open System.Threading
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.AI
open ToolUp.AI.FastPathTriageResolver
open ToolUp.TriageCalibration.Calibration

// ─── The fake provider ────────────────────────────────────────────

type private Reply =
    | Content of string
    | Fail of AIProviderError
    | Hang

/// Answers each instruction from a script and counts every call. A
/// `Hang` reply ignores the retry policy's timeout entirely, so only the
/// tool's own wall-clock backstop can end it — the shape a misbehaving
/// connector has.
type private FakeProvider(script: Map<string, Reply>) =
    let mutable calls = 0
    let mutable systemPrompts: string list = []
    member _.Calls = calls
    member _.SystemPrompts = systemPrompts

    interface IAIProvider with
        member _.Capabilities = {
            AIProviderCapabilities.unknown with
                SupportsTriage = true
                ProviderName = "fake"
                Model = "fake-model"
        }

        member _.SendMessage(_, _, _, _, _) = async {
            return Error(UnsupportedCapability("SendMessage", "the calibration tool must use the structured send"))
        }

        member _.SendStructuredMessage(messages, _, systemPrompt, schema, _) = async {
            Interlocked.Increment &calls |> ignore
            systemPrompts <- defaultArg systemPrompt "" :: systemPrompts

            if schema <> triageSchema then
                failwith "the tool must send the resolver's own triage schema"

            let instruction = (List.last messages).Content

            match Map.tryFind instruction script with
            | Some(Content c) ->
                return
                    Ok {
                        Content = c
                        ToolCalls = []
                        StopReason = "end_turn"
                        Usage =
                            Some {
                                PromptTokens = 120
                                CachedPromptTokens = 0
                                OutputTokens = 30
                                CacheCreationTokens = None
                            }
                    }
            | Some(Fail e) -> return Error e
            | Some Hang ->
                do! Async.Sleep 30_000
                return Error(TransientNetwork "unreachable")
            | None -> return failwith $"unscripted instruction '{instruction}'"
        }

let private country = {
    FieldId = "country"
    Description = "the country filter"
    ValueType = "enum"
    InstructionPatterns = [ "set country to {value}" ]
    ValueAliases = [ "britain", "UK" ]
}

let private region = {
    FieldId = "region"
    Description = "the region filter"
    ValueType = "string-option"
    InstructionPatterns = []
    ValueAliases = []
}

let private snapshot = {
    ModuleId = "sales"
    Page = Some "overview"
    Fields = [ country; region ]
    StateSummary = "country=FR"
}

let private setField fieldId value = {
    Decision = DecisionSetField
    FieldId = Some fieldId
    Value = value
}

let private decline = {
    Decision = DecisionNeedsFullAgent
    FieldId = None
    Value = None
}

let private case id instruction expected = {
    Id = id
    Instruction = instruction
    Snapshot = snapshot
    Expected = expected
}

let private answer decision (fieldId: string option) (value: string option) (confidence: float) =
    let str =
        function
        | Some(s: string) -> $"\"{s}\""
        | None -> "null"

    $"""{{"decision":"{decision}","fieldId":{str fieldId},"value":{str value},"confidence":{confidence},"reason":"r"}}"""

let private settings = {
    Floors = defaultFloors
    MaxInstructionChars = FastPathTriageConfig.DefaultMaxInstructionChars
    TimeoutMs = 2_000
}

let private runWith (script: (string * Reply) list) (cases: CalibrationCase list) =
    let fake = FakeProvider(Map.ofList script)

    let result =
        run (fake :> IAIProvider) AIProviderCallOptions.none settings "0" cases
        |> Async.RunSynchronously

    fake, result

// ─── Tests ────────────────────────────────────────────────────────

[<Tests>]
let eligibilityTests =
    testList "eligibility short-circuit" [
        test "a refused instruction costs no call and carries its reason" {
            let cases = [
                case "q-word" "what is the country" decline
                case "q-mark" "country UK?" decline
                case "long" (String('x', 300)) decline
                case "blank" "   " decline
            ]

            let fake, result = runWith [] cases

            Expect.equal fake.Calls 0 "the pre-filter is free: no provider call"

            Expect.equal
                (result.Cases |> List.map _.Eligibility.Reason)
                [ ReasonQuestionOpener; ReasonQuestionMark; ReasonTooLong; ReasonEmpty ]
                "each refusal names its rule"

            for c in result.Cases do
                Expect.isFalse c.Eligibility.Eligible $"{c.Id} is ineligible"
                Expect.equal c.Latency.Stage StagePreFilter $"{c.Id} is billed to the pre-filter stage"
                Expect.isNone c.Call $"{c.Id} made no call"
                Expect.isEmpty c.Floors $"{c.Id} reached no floor"

            Expect.equal result.Summary.ModelCalls 0 "the summary agrees"
            Expect.equal result.Summary.Ineligible 4 "four refusals"
        }

        test "an eligible instruction over a surface with no fields is refused before the call" {
            let empty = {
                case "no-fields" "set country to UK" decline with
                    Snapshot = { snapshot with Fields = [] }
            }

            let fake, result = runWith [] [ empty ]
            Expect.equal fake.Calls 0 "no call"
            Expect.equal result.Cases.Head.Eligibility.Reason ReasonNoDeclaredFields "named"
        }

        test "the verdict is always the resolver's, and every rule this tool names is one it refuses by" {
            let max = FastPathTriageConfig.DefaultMaxInstructionChars

            for instruction in [ "what now"; "Explain this"; "show me sales"; "x?"; String('y', max + 1); "" ] do
                Expect.isFalse
                    (isEligibleInstruction max instruction)
                    $"the resolver refuses '{instruction}', so the tool's reason cannot be a guess"

                Expect.notEqual (eligibilityReason max instruction) ReasonEligible "and the tool agrees"

            Expect.equal (eligibilityReason max "set country to UK") ReasonEligible "an instruction passes"
        }
    ]

[<Tests>]
let rescoringTests =
    testList "one call, four floors" [
        test "a single answer is re-scored at every floor without a second call" {
            let fake, result =
                runWith [
                    "set country to britain", Content(answer "set_field" (Some "country") (Some "britain") 0.80)
                ] [ case "c1" "set country to britain" (setField "country" (Some "UK")) ]

            Expect.equal fake.Calls 1 "exactly one provider call for four floors"
            let c = result.Cases.Head
            Expect.equal c.Latency.Stage StageModelCall "billed to the model-call stage"
            Expect.equal (c.Floors |> List.map _.Floor) defaultFloors "one verdict per floor, in order"

            Expect.equal
                (c.Floors |> List.map _.Outcome)
                [ OutcomeHit; OutcomeHit; OutcomeLowConfidence; OutcomeLowConfidence ]
                "0.80 clears 0.70 and 0.78, not 0.85 or 0.92"

            Expect.equal
                (c.Floors |> List.map _.Score)
                [ ScoreCorrectResolve; ScoreCorrectResolve; ScoreMissed; ScoreMissed ]
                "scored against the expectation"

            Expect.equal c.Floors.Head.Value (Some "UK") "the alias folds through planTriage, as in production"

            Expect.equal
                c.Call.Value.RawContent
                (Some(answer "set_field" (Some "country") (Some "britain") 0.80))
                "the raw content is cached verbatim"

            Expect.equal c.Call.Value.InputTokens (Some 120) "usage rides with the call"
            Expect.stringContains fake.SystemPrompts.Head "Declared fields" "the resolver's own prompt was sent"
        }

        test "false fires, wrong resolves and correct declines are told apart" {
            let _, result =
                runWith [
                    "set region to north", Content(answer "set_field" (Some "region") (Some "north") 0.99)
                    "set country to spain", Content(answer "set_field" (Some "country") (Some "FR") 0.99)
                    "set country and region", Content(answer "needs_full_agent" None None 0.10)
                ] [
                    case "ff" "set region to north" decline
                    case "wr" "set country to spain" (setField "country" (Some "ES"))
                    case "cd" "set country and region" decline
                ]

            let scoreAt0 (id: string) =
                (result.Cases |> List.find (fun c -> c.Id = id)).Floors.Head.Score

            Expect.equal (scoreAt0 "ff") ScoreFalseFire "a hit where the agent was expected"
            Expect.equal (scoreAt0 "wr") ScoreWrongResolve "a hit on the wrong value"
            Expect.equal (scoreAt0 "cd") ScoreCorrectDecline "a decline where one was expected"

            let floor = result.Summary.Floors.Head
            Expect.equal floor.Hits 2 "two hits at 0.70"
            Expect.equal floor.FalseFireRate (Some 1.0) "both hits were wrong"
            Expect.equal floor.ResolveRate (Some 0.0) "the one resolvable case was not resolved"
        }

        test "an ineligible case counts as a fall-through at every floor in the summary" {
            let _, result =
                runWith [] [ case "q" "which country" (setField "country" (Some "UK")) ]

            for f in result.Summary.Floors do
                Expect.equal f.Missed 1 $"missed at {f.Floor}"
                Expect.equal f.Hits 0 "no hit"
                Expect.equal f.FalseFireRate None "no hits, no rate"
        }
    ]

[<Tests>]
let failureTests =
    testList "failure strata" [
        test "a hung call is cut at the budget and carries the timeout token" {
            let fake = FakeProvider(Map.ofList [ "set country to UK", Hang ])

            let result =
                run (fake :> IAIProvider) AIProviderCallOptions.none { settings with TimeoutMs = 50 } "0" [
                    case "t" "set country to UK" (setField "country" (Some "UK"))
                ]
                |> Async.RunSynchronously

            let c = result.Cases.Head
            Expect.equal c.Call.Value.Outcome OutcomeTimeout "the 662 token, distinct from provider-error"
            Expect.equal c.Latency.Stage StageModelCall "a timeout is a model-call cost"
            Expect.isLessThan c.Latency.Ms 10_000.0 "cut by the tool's own backstop"
            Expect.isTrue (c.Floors |> List.forall (fun f -> f.Outcome = OutcomeTimeout)) "every floor falls through"
            Expect.equal result.Summary.Timeouts 1 "counted"
        }

        test "a provider error and an unparseable answer are their own strata" {
            let _, result =
                runWith [
                    "set country to UK", Fail(TransientServer(503, "busy"))
                    "set region to north", Content "not json"
                ] [ case "e" "set country to UK" decline; case "u" "set region to north" decline ]

            Expect.equal
                (result.Cases |> List.map _.Call.Value.Outcome)
                [ OutcomeProviderError; OutcomeUnparseable ]
                "each stratum named by the resolver's token"

            Expect.equal
                (result.Cases.[1].Call.Value.RawContent)
                (Some "not json")
                "unparseable content is still cached"

            Expect.equal result.Summary.ProviderErrors 1 "counted"
            Expect.equal result.Summary.Unparseable 1 "counted"
        }
    ]

let private caseFile =
    """{
  "schema": "toolup.triage-calibration.cases/1",
  "cases": [
    {
      "id": "c1",
      "instruction": "set country to britain",
      "snapshot": { "moduleId": "sales", "page": "overview", "stateSummary": "country=FR" },
      "fields": [
        { "fieldId": "country", "description": "the country filter", "valueType": "enum",
          "instructionPatterns": ["set country to {value}"], "valueAliases": { "britain": "UK" } }
      ],
      "expected": { "decision": "set_field", "fieldId": "country", "value": "UK" }
    }
  ]
}"""

[<Tests>]
let schemaTests =
    testList "schemas" [
        test "the documented case file decodes" {
            match decodeCases caseFile with
            | Error e -> failtest e
            | Ok [ c ] ->
                Expect.equal c.Snapshot.Page (Some "overview") "page"
                Expect.equal c.Snapshot.Fields.Head.ValueAliases [ "britain", "UK" ] "aliases"
                Expect.equal c.Expected (setField "country" (Some "UK")) "expected verdict"
            | Ok cs -> failtestf "expected one case, got %d" cs.Length
        }

        test "the shipped example case file decodes, and runs with one call per eligible case" {
            let path = IO.Path.Combine(AppContext.BaseDirectory, "examples", "cases.json")

            match loadCaseFile path with
            | Error e -> failtest e
            | Ok(cases, sha) ->
                Expect.equal
                    (cases |> List.map _.Id)
                    [ "set-country-alias"; "clear-region"; "multi-clause"; "question" ]
                    "all four"

                Expect.equal sha.Length 64 "the corpus is identified by its SHA-256"

                let clear = cases |> List.find (fun c -> c.Id = "clear-region")
                Expect.equal clear.Expected (setField "region" None) "a null value is a clear"

                let fake =
                    FakeProvider(
                        Map.ofList [
                            "set country to britain", Content(answer "set_field" (Some "country") (Some "britain") 0.9)
                            "clear the region", Content(answer "set_field" (Some "region") None 0.9)
                            "set country to UK and region to north", Content(answer "needs_full_agent" None None 0.2)
                        ]
                    )

                let result =
                    run (fake :> IAIProvider) AIProviderCallOptions.none settings sha cases
                    |> Async.RunSynchronously

                Expect.equal fake.Calls 3 "the question is refused locally; three calls"

                Expect.equal
                    (result.Summary.Floors |> List.map _.CorrectResolve)
                    [ 2; 2; 2; 0 ]
                    "both resolvable cases resolve at 0.90 until the 0.92 floor"
        }

        test "a malformed case file is refused with the case named" {
            let refused (json: string) (fragment: string) =
                match decodeCases json with
                | Ok _ -> failtestf "accepted: %s" json
                | Error e -> Expect.stringContains e fragment "the refusal says why"

            refused (caseFile.Replace("cases/1", "cases/9")) "unknown case-file schema"
            refused (caseFile.Replace("\"set_field\"", "\"maybe\"")) "expected.decision"
            refused (caseFile.Replace("\"moduleId\"", "\"module\"")) "moduleId"
            refused "[]" "not a JSON object"
            refused "{" "not valid JSON"
        }

        test "the result file round-trips through its documented schema" {
            let _, result =
                runWith [
                    "set country to britain", Content(answer "set_field" (Some "country") (Some "britain") 0.80)
                    "set region", Content(answer "set_field" (Some "region") None 0.95)
                    "set country to UK", Fail(TransientServer(503, "busy"))
                ] [
                    case "hit" "set country to britain" (setField "country" (Some "UK"))
                    case "clear" "set region" (setField "region" None)
                    case "err" "set country to UK" decline
                    case "q" "why" decline
                ]

            let json = encodeResult result

            match decodeResult json with
            | Error e -> failtest e
            | Ok decoded ->
                Expect.equal decoded result "decode(encode r) = r"
                Expect.equal (encodeResult decoded) json "and the bytes are stable"

            Expect.stringContains json $"\"schema\": \"{ResultSchema}\"" "the schema id is written"
        }

        test "a result naming another schema is refused, and unknown members are tolerated" {
            let _, result = runWith [] [ case "q" "why" decline ]
            let json = encodeResult result

            match decodeResult (json.Replace(ResultSchema, "toolup.triage-calibration.result/2")) with
            | Ok _ -> failtest "a different schema id must be refused"
            | Error e -> Expect.stringContains e "unknown result schema" "named"

            match decodeResult (json.Replace("\"corpusSha256\"", "\"addedLater\": 1,\n  \"corpusSha256\"")) with
            | Error e -> failtestf "an additive member must decode: %s" e
            | Ok decoded -> Expect.equal decoded result "the additive member is ignored"
        }
    ]