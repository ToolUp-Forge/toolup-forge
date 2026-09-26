// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

/// Phase 514 — the conversation eval harness's two entry points.
///
///   * No arguments (or Expecto arguments): the Expecto self-test pack
///     `VerifyAll` runs. Every case in it is offline and deterministic —
///     committed fixtures replayed from their recordings, checked against
///     their committed baselines — except the two live arms, which report
///     Pending unless their env vars are set (GP 11: a fresh checkout is
///     green with no key).
///   * `eval [fixtures…] [options]`: the regression gate itself, shaped
///     like the RAG eval — non-zero exit on a failing gate.
///
///       dotnet run --project src/ToolUp.AI.Evaluation -- eval
///       dotnet run --project src/ToolUp.AI.Evaluation -- eval --prompt v2-regressed \
///           --baseline src/ToolUp.AI.Evaluation/fixtures/baselines
///       dotnet run --project src/ToolUp.AI.Evaluation -- eval --provider live --sdk 0.9.0 \
///           --out artifacts/ai-eval
///
///   Options: `--prompt <version>` (default: each fixture's defaultPrompt),
///   `--provider recorded|live` (default recorded), `--sdk <label>`,
///   `--baseline <dir|file.json>`, `--tolerance <n>` (default 0.05),
///   `--out <dir|file.json>`.
///
/// Env vars (all optional; nothing here reads a key unless asked to):
///   `TOOLUP_AI_EVAL_PROVIDER` = claude | openai | gemini — the live replay
///     provider for `--provider live`; `TOOLUP_AI_EVAL_MODEL` pins its model.
///   `TOOLUP_AI_EVAL_JUDGE` = claude | openai | gemini — arms the LLM-judge;
///     `TOOLUP_AI_EVAL_JUDGE_MODEL` pins its model.
///   The key comes from the provider's usual variable: `ANTHROPIC_API_KEY`,
///   `OPENAI_API_KEY`, `GEMINI_API_KEY`.
module ToolUp.AI.Evaluation.Program

open System
open System.IO
open System.Reflection
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.ConversationStore
open ToolUp.Platform.Tests.Support
open ToolUp.AI.Evaluation.ConversationEval

// ─── Live providers (env-gated) ───────────────────────────────────

let private liveProviders: (string * string * (string -> IAIProvider) * (string -> string -> IAIProvider)) list = [
    "claude", "ANTHROPIC_API_KEY", ClaudeAIProvider.createWithApiKey, ClaudeAIProvider.createWithApiKeyAndModel
    "openai", "OPENAI_API_KEY", OpenAIProvider.createWithApiKey, OpenAIProvider.createWithApiKeyAndModel
    "gemini", "GEMINI_API_KEY", GeminiAIProvider.createWithApiKey, GeminiAIProvider.createWithApiKeyAndModel
]

let private env (name: string) =
    match Environment.GetEnvironmentVariable name with
    | null -> None
    | v when String.IsNullOrWhiteSpace v -> None
    | v -> Some(v.Trim())

/// `None` when `selectorVar` is unset — the arm is off. `Some(Error _)`
/// when it is set but cannot be honoured: an unknown provider or a missing
/// key is a misconfiguration to report, never a silent skip.
let resolveLiveProvider (selectorVar: string) (modelVar: string) : Result<IAIProvider, string> option =
    env selectorVar
    |> Option.map (fun selector ->
        match
            liveProviders
            |> List.tryFind (fun (id, _, _, _) -> id = selector.ToLowerInvariant())
        with
        | None ->
            Error(
                sprintf
                    "%s=%s is not a known provider (known: %s)"
                    selectorVar
                    selector
                    (liveProviders |> List.map (fun (id, _, _, _) -> id) |> String.concat ", ")
            )
        | Some(_, keyVar, create, createWithModel) ->
            match env keyVar with
            | None -> Error(sprintf "%s=%s but %s is not set" selectorVar selector keyVar)
            | Some key ->
                match env modelVar with
                | Some model -> Ok(createWithModel key model)
                | None -> Ok(create key))

let private judgeMode () : Result<JudgeMode, string> =
    match
        resolveLiveProvider
            ToolUp.Platform.ConfigKeys.Names.aiEvalJudge
            ToolUp.Platform.ConfigKeys.Names.aiEvalJudgeModel
    with
    | None -> Ok(JudgeOff "TOOLUP_AI_EVAL_JUDGE not set")
    | Some(Ok judge) -> Ok(JudgeWith judge)
    | Some(Error e) -> Error e

// ─── The `eval` gate ──────────────────────────────────────────────

type EvalArgs = {
    Fixtures: string list
    Prompt: string option
    Live: bool
    Sdk: string
    Baseline: string option
    Tolerance: float
    Out: string option
}

let fixturesDir = Path.Combine(AppContext.BaseDirectory, "fixtures")
let baselinesDir = Path.Combine(fixturesDir, "baselines")

/// The committed fixtures: every `*.json` directly under `fixtures/`
/// (baselines live one level down and are not fixtures).
let committedFixtures () : string list =
    if Directory.Exists fixturesDir then
        Directory.GetFiles(fixturesDir, "*.json") |> Array.sort |> List.ofArray
    else
        []

/// A `.json` path names one file; anything else is a directory holding
/// `<FixtureName>.json`.
let private perFixturePath (path: string) (fixtureName: string) =
    if path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) then
        path
    else
        Path.Combine(path, fixtureName + ".json")

let private printReport (report: EvalReport) =
    printfn ""

    printfn
        "═══ Conversation eval: %s  (prompt %s, provider %s/%s, sdk %s) ═══"
        report.FixtureName
        report.PromptVersion
        report.Provider
        report.Model
        report.SdkAnnotation

    printfn "  Cases:          %d / %d passed" report.CasesPassed report.CaseCount
    printfn "  Assertions:     %d / %d passed" report.AssertionsPassed report.AssertionCount
    printfn "  Pass rate:      %.3f" report.PassRate
    printfn "  Critical fails: %d" report.CriticalFailures

    match report.JudgeMeanScore with
    | Some s -> printfn "  Judge mean:     %.3f" s
    | None -> printfn "  Judge mean:     —"

    for c in report.PerCase do
        printfn
            "  [%s] %s  calls=%d  judge=%s"
            (if c.Passed then "PASS" else "FAIL")
            c.CaseId
            c.ProviderCalls
            c.JudgeStatus

        for a in c.Assertions |> List.filter (fun a -> not a.Passed) do
            printfn "        ✗ %s%s — %s" a.Description (if a.Critical then " (critical)" else "") a.Detail

    printfn ""

/// Run the gate over every fixture and return the process exit code:
/// 0 green, 1 a failing gate, 2 a run that could not start (a bad
/// fixture, an unknown prompt, a missing baseline, a live arm asked for
/// but not configured). Every failure is printed; none is swallowed.
let runEval (args: EvalArgs) : int =
    let fail (message: string) =
        eprintfn "✗ %s" message
        2

    let source =
        if not args.Live then
            Ok Recorded
        else
            match
                resolveLiveProvider
                    ToolUp.Platform.ConfigKeys.Names.aiEvalProvider
                    ToolUp.Platform.ConfigKeys.Names.aiEvalModel
            with
            | None -> Error "--provider live needs TOOLUP_AI_EVAL_PROVIDER (claude | openai | gemini)"
            | Some(Ok p) -> Ok(Live p)
            | Some(Error e) -> Error e

    match source, judgeMode () with
    | Error e, _
    | _, Error e -> fail e
    | Ok source, Ok judge ->
        let fixtures =
            if args.Fixtures.IsEmpty then
                committedFixtures ()
            else
                args.Fixtures

        if fixtures.IsEmpty then
            fail (sprintf "no fixtures given and none found under %s" fixturesDir)
        else
            let mutable exitCode = 0

            for path in fixtures do
                match FixtureLoader.load path with
                | Error e -> exitCode <- max exitCode (fail e)
                | Ok fixture ->
                    let prompt = defaultArg args.Prompt fixture.DefaultPrompt

                    match evaluate source judge args.Sdk prompt fixture |> Async.RunSynchronously with
                    | Error e -> exitCode <- max exitCode (fail e)
                    | Ok report ->
                        printReport report

                        args.Out
                        |> Option.iter (fun out ->
                            let target = perFixturePath out fixture.Name
                            Report.write target report
                            printfn "Report written to %s" target)

                        let baseline = args.Baseline |> Option.map (fun b -> perFixturePath b fixture.Name)

                        match baseline with
                        | Some b when not (File.Exists b) ->
                            // Fail closed: a baseline gate asked for and not
                            // run is not a pass.
                            exitCode <- max exitCode (fail (sprintf "baseline not found: %s" b))
                        | _ ->
                            let reasons = gate args.Tolerance (baseline |> Option.map Report.read) report

                            match reasons with
                            | [] ->
                                match baseline with
                                | Some b -> printfn "✓ %s: no regression vs baseline (%s)" fixture.Name b
                                | None -> printfn "✓ %s: every case passed" fixture.Name
                            | _ ->
                                for r in reasons do
                                    eprintfn "✗ %s" r

                                exitCode <- max exitCode 1

            exitCode

let parseEvalArgs (argv: string list) : Result<EvalArgs, string> =
    let rec go (acc: EvalArgs) args =
        match args with
        | [] ->
            Ok {
                acc with
                    Fixtures = List.rev acc.Fixtures
            }
        | "--prompt" :: v :: rest -> go { acc with Prompt = Some v } rest
        | "--provider" :: "recorded" :: rest -> go { acc with Live = false } rest
        | "--provider" :: "live" :: rest -> go { acc with Live = true } rest
        | "--provider" :: v :: _ -> Error(sprintf "--provider must be 'recorded' or 'live', not '%s'" v)
        | "--sdk" :: v :: rest -> go { acc with Sdk = v } rest
        | "--baseline" :: v :: rest -> go { acc with Baseline = Some v } rest
        | "--out" :: v :: rest -> go { acc with Out = Some v } rest
        | "--tolerance" :: v :: rest ->
            match Double.TryParse(v, Globalization.NumberStyles.Float, Globalization.CultureInfo.InvariantCulture) with
            | true, t when t >= 0.0 -> go { acc with Tolerance = t } rest
            | _ -> Error(sprintf "--tolerance must be a non-negative number, not '%s'" v)
        | flag :: [] when flag.StartsWith "--" -> Error(sprintf "%s needs a value" flag)
        | flag :: _ when flag.StartsWith "--" -> Error(sprintf "unknown option %s" flag)
        | fixture :: rest ->
            go
                {
                    acc with
                        Fixtures = fixture :: acc.Fixtures
                }
                rest

    go
        {
            Fixtures = []
            Prompt = None
            Live = false
            Sdk = "unlabelled"
            Baseline = None
            Tolerance = DefaultTolerance
            Out = None
        }
        argv

// ─── Self-test pack ───────────────────────────────────────────────

let private run source judge prompt fixture =
    match evaluate source judge "test" prompt fixture |> Async.RunSynchronously with
    | Ok report -> report
    | Error e -> failtestf "evaluate refused to start: %s" e

let private parseOk (json: string) =
    match FixtureLoader.parse json with
    | Ok f -> f
    | Error e -> failtestf "fixture did not load: %s" e

/// A one-tool fixture the tool-dispatch cases below vary.
let private toolFixture (recording: string) (maxToolRounds: int) =
    parseOk (
        sprintf
            """{
  "name": "tool-loop",
  "maxToolRounds": %d,
  "prompts": { "v1": "use the lookup tool" },
  "tools": [ { "name": "lookup", "inputSchema": { "type": "object" } } ],
  "toolResults": [ { "tool": "lookup", "argumentsContain": ["alpha"], "content": "ALPHA-RESULT" } ],
  "cases": [ { "id": "c1", "turns": ["find alpha"],
               "assertions": [ { "kind": "toolCalled", "tool": "lookup", "argumentsContain": ["alpha"] } ] } ],
  "recordings": { "v1": { "c1": %s } }
}"""
            maxToolRounds
            recording
    )

let private call id args =
    sprintf """{ "id": "%s", "name": "lookup", "arguments": %s }""" id args

let private transcript (answer: string) (calls: ToolCallRecord list) : CaseTranscript = {
    CaseId = "t"
    Messages = []
    ToolCalls = calls
    AssistantTexts = if answer = "" then [] else [ answer ]
    FinalAnswer = answer
    ProviderCalls = 1
    Error = None
}

let private weatherFixture () =
    match FixtureLoader.load (Path.Combine(fixturesDir, "weather-agent.json")) with
    | Ok f -> f
    | Error e -> failtestf "%s" e

let private fixtureTests =
    testList "committed fixtures" [
        testCase "every committed fixture loads, and there is at least one"
        <| fun _ ->
            let fixtures = committedFixtures ()
            Expect.isNonEmpty fixtures (sprintf "no fixtures under %s — the copy-to-output rule is broken" fixturesDir)

            for path in fixtures do
                match FixtureLoader.load path with
                | Ok f -> Expect.isNonEmpty f.Cases (sprintf "%s declares no cases" path)
                | Error e -> failtestf "%s" e

        testCase "every committed fixture passes its gate on its default prompt, with and without its baseline"
        <| fun _ ->
            for path in committedFixtures () do
                let fixture =
                    match FixtureLoader.load path with
                    | Ok f -> f
                    | Error e -> failtestf "%s" e

                let report = run Recorded (JudgeOff "self-test") fixture.DefaultPrompt fixture

                Expect.equal (gate DefaultTolerance None report) [] (sprintf "%s: gate without a baseline" fixture.Name)

                let baselinePath = Path.Combine(baselinesDir, fixture.Name + ".json")

                Expect.isTrue
                    (File.Exists baselinePath)
                    (sprintf "%s has no committed baseline at %s" fixture.Name baselinePath)

                Expect.equal
                    (gate DefaultTolerance (Some(Report.read baselinePath)) report)
                    []
                    (sprintf "%s: gate against its committed baseline" fixture.Name)

        testCase "the deliberately-regressed prompt fails the gate with a non-zero exit"
        <| fun _ ->
            let fixture = weatherFixture ()
            let report = run Recorded (JudgeOff "self-test") "v2-regressed" fixture

            Expect.isGreaterThan report.CriticalFailures 0 "the regressed prompt leaks its system prompt"

            Expect.isLessThan report.PassRate 0.95 "the regressed prompt drops the pass rate"

            let reasons =
                gate DefaultTolerance (Some(Report.read (Path.Combine(baselinesDir, "weather-agent.json")))) report

            Expect.isNonEmpty reasons "the gate must refuse the regressed prompt"

            let exitCode =
                runEval {
                    Fixtures = [ Path.Combine(fixturesDir, "weather-agent.json") ]
                    Prompt = Some "v2-regressed"
                    Live = false
                    Sdk = "test"
                    Baseline = Some baselinesDir
                    Tolerance = DefaultTolerance
                    Out = None
                }

            Expect.equal exitCode 1 "runEval exits 1 on a regression"

            let greenExit =
                runEval {
                    Fixtures = [ Path.Combine(fixturesDir, "weather-agent.json") ]
                    Prompt = None
                    Live = false
                    Sdk = "test"
                    Baseline = Some baselinesDir
                    Tolerance = DefaultTolerance
                    Out = None
                }

            Expect.equal greenExit 0 "the control: the default prompt exits 0 through the same path"

        testCase "a missing baseline fails closed rather than skipping the comparison"
        <| fun _ ->
            let exitCode =
                runEval {
                    Fixtures = [ Path.Combine(fixturesDir, "weather-agent.json") ]
                    Prompt = None
                    Live = false
                    Sdk = "test"
                    Baseline = Some(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")))
                    Tolerance = DefaultTolerance
                    Out = None
                }

            Expect.equal exitCode 2 "an absent baseline is a run that could not start"

        testCase "a report round-trips through its JSON and is no regression against itself"
        <| fun _ ->
            let report = run Recorded (JudgeOff "self-test") "v1" (weatherFixture ())

            let back = Report.deserialize (Report.serialize report)
            Expect.equal back report "round trip"
            Expect.equal (detectRegression 0.0 back report) (Ok()) "self-comparison"

        testCase "an unknown prompt version, or one with no recording, refuses to start"
        <| fun _ ->
            let fixture = weatherFixture ()

            match
                evaluate Recorded (JudgeOff "t") "t" "no-such-prompt" fixture
                |> Async.RunSynchronously
            with
            | Error e -> Expect.stringContains e "no-such-prompt" "names the version"
            | Ok _ -> failtest "an unknown prompt version must not evaluate"

            let unrecorded = {
                fixture with
                    Recordings = fixture.Recordings |> Map.remove "v1"
            }

            match evaluate Recorded (JudgeOff "t") "t" "v1" unrecorded |> Async.RunSynchronously with
            | Error e -> Expect.stringContains e "no recording" "says why"
            | Ok _ -> failtest "a prompt with no recording must not evaluate offline"
    ]

let private toolDispatchTests =
    testList "tool-dispatch replay" [
        testCase "a tool call is answered from the recorded result and the loop runs to the final answer"
        <| fun _ ->
            let fixture =
                toolFixture
                    (sprintf """[ { "toolCalls": [ %s ] }, { "content": "done" } ]""" (call "k1" "{\"q\":\"alpha\"}"))
                    8

            let provider = ScriptedProvider("c1", fixture.Recordings["v1"]["c1"])

            let t =
                replayCase provider fixture "use the lookup tool" fixture.Cases.Head
                |> Async.RunSynchronously

            Expect.equal t.ProviderCalls 2 "two provider calls: the tool call, then the answer"
            Expect.equal t.FinalAnswer "done" "final answer"
            Expect.equal t.Error None "no error"

            Expect.equal
                t.ToolCalls
                [
                    {
                        Name = "lookup"
                        Arguments = "{\"q\":\"alpha\"}"
                        Resolved = true
                    }
                ]
                "the dispatched call"

            let second = provider.Requests[1]
            let toolMessage = second.Messages |> List.last

            Expect.equal
                toolMessage.ToolResults
                [
                    {
                        ToolCallId = "k1"
                        Content = "ALPHA-RESULT"
                    }
                ]
                "the second call carries the RECORDED result against the call id"

            Expect.equal second.SystemPrompt (Some "use the lookup tool") "the prompt version under test is sent"
            Expect.equal (second.Tools |> List.map _.Name) [ "lookup" ] "the fixture's tools are offered"

        testCase "a tool call no recording answers fails the replay assertion, naming the call"
        <| fun _ ->
            let fixture =
                toolFixture
                    (sprintf """[ { "toolCalls": [ %s ] }, { "content": "done" } ]""" (call "k1" "{\"q\":\"beta\"}"))
                    8

            let report = run Recorded (JudgeOff "t") "v1" fixture
            let case = report.PerCase.Head
            Expect.isFalse case.Passed "the case fails"

            let replay = case.Assertions |> List.find (fun a -> a.Kind = "replay")
            Expect.isFalse replay.Passed "replay assertion"
            Expect.stringContains replay.Detail "beta" "names the unrecorded arguments"

        testCase "a runaway tool loop stops at maxToolRounds"
        <| fun _ ->
            let calls = [
                for i in 1..4 -> sprintf """{ "toolCalls": [ %s ] }""" (call (sprintf "k%d" i) "{\"q\":\"alpha\"}")
            ]

            let fixture = toolFixture (sprintf "[ %s ]" (String.Join(", ", calls))) 2
            let report = run Recorded (JudgeOff "t") "v1" fixture

            let replay =
                report.PerCase.Head.Assertions |> List.find (fun a -> a.Kind = "replay")

            Expect.isFalse replay.Passed "replay assertion"
            Expect.stringContains replay.Detail "maxToolRounds (2)" "names the bound"
            Expect.equal report.PerCase.Head.ProviderCalls 3 "two rounds dispatched, the third call refused"

        testCase "a replay that outruns its recording fails rather than inventing an answer"
        <| fun _ ->
            let fixture =
                toolFixture (sprintf """[ { "toolCalls": [ %s ] } ]""" (call "k1" "{\"q\":\"alpha\"}")) 8

            let report = run Recorded (JudgeOff "t") "v1" fixture

            let replay =
                report.PerCase.Head.Assertions |> List.find (fun a -> a.Kind = "replay")

            Expect.isFalse replay.Passed "replay assertion"
            Expect.stringContains replay.Detail "exhausted" "names the shortfall"

        testCase "a structured-output case goes through SendStructuredMessage with its schema"
        <| fun _ ->
            let fixture = weatherFixture ()
            let case = fixture.Cases |> List.find (fun c -> c.ResponseSchema.IsSome)
            let provider = ScriptedProvider("s", fixture.Recordings["v1"][case.Id])

            replayCase provider fixture fixture.Prompts["v1"] case
            |> Async.RunSynchronously
            |> ignore

            Expect.isTrue
                (provider.Requests |> List.forall (fun r -> r.Schema = case.ResponseSchema))
                "every call carries the case's schema"
    ]

let private rubricTests =
    let resolved name args = {
        Name = name
        Arguments = args
        Resolved = true
    }

    let passes t a = (evaluateAssertion t a).Passed

    testList "rubric assertions" [
        testCase "toolCalled / toolNotCalled"
        <| fun _ ->
            let t = transcript "ok" [ resolved "get_weather" """{"city":"London"}""" ]
            Expect.isTrue (passes t (ToolCalled("get_weather", [ "London" ]))) "called with London"
            Expect.isFalse (passes t (ToolCalled("get_weather", [ "Paris" ]))) "not called with Paris"
            Expect.isFalse (passes t (ToolCalled("other", []))) "other tool not called"
            Expect.isTrue (passes t (ToolNotCalled "other")) "other not called"
            Expect.isFalse (passes t (ToolNotCalled "get_weather")) "get_weather was called"

        testCase "structuredOutput"
        <| fun _ ->
            let a = StructuredOutput [ "city"; "tempC" ]
            Expect.isTrue (passes (transcript """{"city":"P","tempC":1}""" []) a) "object with both"
            Expect.isFalse (passes (transcript """{"city":"P"}""" []) a) "missing field"
            Expect.isFalse (passes (transcript "It is sunny." []) a) "prose"
            Expect.isFalse (passes (transcript "[1,2]" []) a) "array"

        testCase "refusal requires a marker AND no tool use"
        <| fun _ ->
            let a = Refusal [ "only help with weather" ]
            Expect.isTrue (passes (transcript "Sorry, I can ONLY help with weather." []) a) "declined"
            Expect.isFalse (passes (transcript "Here is a poem." []) a) "complied"

            Expect.isFalse
                (passes (transcript "I can only help with weather." [ resolved "get_weather" "{}" ]) a)
                "a refusal after a tool call is not a refusal"

        testCase "noForbiddenContent is critical and scans every assistant text"
        <| fun _ ->
            let a = NoForbiddenContent [ "secret" ]

            let t = {
                transcript "fine" [] with
                    AssistantTexts = [ "the SECRET is out"; "fine" ]
            }

            let r = evaluateAssertion t a
            Expect.isFalse r.Passed "an earlier turn leaked"
            Expect.isTrue r.Critical "critical"
            Expect.isTrue (passes (transcript "fine" []) a) "clean"

        testCase "contains is case-insensitive over the final answer"
        <| fun _ ->
            Expect.isTrue (passes (transcript "14°C and Light Rain" []) (Contains "light rain")) "hit"
            Expect.isFalse (passes (transcript "sunny" []) (Contains "rain")) "miss"

        testCase "the fixture loader fails closed on what it cannot evaluate"
        <| fun _ ->
            let expectError (label: string) (json: string) (fragment: string) =
                match FixtureLoader.parse json with
                | Ok _ -> failtestf "%s: loaded" label
                | Error e -> Expect.stringContains e fragment label

            let body (extra: string) =
                sprintf
                    """{ "name": "f", "prompts": { "v1": "p" }, "cases": [ { "id": "c", "turns": ["hi"], "assertions": [ %s ] } ] }"""
                    extra

            expectError "unknown kind" (body """{ "kind": "vibes" }""") "unknown assertion kind 'vibes'"
            expectError "empty refusal" (body """{ "kind": "refusal" }""") "at least one marker"

            expectError
                "undeclared default"
                """{ "name": "f", "defaultPrompt": "v9", "prompts": { "v1": "p" }, "cases": [ { "id": "c", "turns": ["hi"] } ] }"""
                "defaultPrompt 'v9'"

            expectError
                "duplicate case"
                """{ "name": "f", "prompts": { "v1": "p" }, "cases": [ { "id": "c", "turns": ["a"] }, { "id": "c", "turns": ["b"] } ] }"""
                "more than once"

            expectError
                "recording for an unknown case"
                """{ "name": "f", "prompts": { "v1": "p" }, "cases": [ { "id": "c", "turns": ["a"] } ], "recordings": { "v1": { "zz": [] } } }"""
                "no case has this id"

            expectError "not JSON" "{ nope" "not valid JSON"
    ]

let private baselineTests =
    let report (rate: float) (critical: int) (cases: (string * bool) list) (judge: float option) : EvalReport = {
        FixtureName = "f"
        PromptVersion = "v1"
        Provider = "recorded"
        Model = "v1"
        SdkAnnotation = "t"
        CaseCount = cases.Length
        CasesPassed = cases |> List.filter snd |> List.length
        AssertionCount = 10
        AssertionsPassed = int (rate * 10.0)
        PassRate = rate
        CriticalFailures = critical
        JudgeMeanScore = judge
        PerCase =
            cases
            |> List.map (fun (id, passed) -> {
                CaseId = id
                Passed = passed
                Assertions = []
                ToolCalls = []
                FinalAnswer = ""
                ProviderCalls = 1
                JudgeStatus = "not requested"
                JudgeScore = None
            })
    }

    testList "baseline gate" [
        testCase "a drop within the tolerance passes; beyond it fails, naming the newly failing case"
        <| fun _ ->
            let baseline = report 0.9 0 [ "a", true; "b", false ] None
            Expect.equal (detectRegression 0.05 baseline (report 0.9 0 [ "a", true; "b", false ] None)) (Ok()) "same"

            match detectRegression 0.05 baseline (report 0.7 0 [ "a", false; "b", false ] None) with
            | Ok() -> failtest "a 0.2 drop must regress"
            | Error e -> Expect.stringContains e "newly failing: a" "names the case"

        testCase "with a baseline, known failures within tolerance pass; without one, every case must pass"
        <| fun _ ->
            let current = report 0.9 0 [ "a", true; "b", false ] None
            Expect.equal (gate 0.05 (Some current) current) [] "baseline already records b failing"
            Expect.isNonEmpty (gate 0.05 None current) "no baseline: b fails the gate"

        testCase "a critical failure fails the gate whatever the baseline says"
        <| fun _ ->
            let current = report 0.9 1 [ "a", true ] None
            Expect.isNonEmpty (gate 1.0 (Some current) current) "critical"

        testCase "the judge mean is compared under the same tolerance when both runs have one"
        <| fun _ ->
            let b = report 1.0 0 [ "a", true ] (Some 0.9)
            Expect.isOk (detectRegression 0.05 b (report 1.0 0 [ "a", true ] None)) "judge off now: not compared"
            Expect.isError (detectRegression 0.05 b (report 1.0 0 [ "a", true ] (Some 0.5))) "judge dropped"

        testCase "a baseline for another fixture is refused"
        <| fun _ ->
            let b = {
                report 1.0 0 [] None with
                    FixtureName = "other"
            }

            Expect.isError (detectRegression 0.05 b (report 1.0 0 [] None)) "fixture mismatch"
    ]

let private storedConversationTests =
    testList "from the Phase 53 substrate" [
        testCaseAsync "a persisted conversation becomes a case that replays its own tool loop"
        <| async {
            let store = InMemoryConversationStore() :> IConversationStore
            let scope = "scope-514"
            let id = "conv-514"

            let content role text calls results : AIMessageContent = {
                Role = role
                Content = text
                ToolCalls = calls
                ToolResults = results
                Parts = []
            }

            let turn role c : ConversationTurn = {
                TurnId = ""
                ConversationId = id
                SchemaVersion = 1
                Role = role
                Content = c
                Timestamp = DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
                TokensIn = None
                TokensOut = None
                ContentDigest = ""
            }

            let weatherCall = {
                Id = "tc-1"
                Name = "get_weather"
                Arguments = """{"city":"London"}"""
            }

            let! begun =
                (store :> IConversationWriter)
                    .BeginConversation(
                        scope,
                        {
                            ConversationId = id
                            SchemaVersion = 1
                            CreatedAt = DateTime(2026, 9, 26, 0, 0, 0, DateTimeKind.Utc)
                            CreatedBy = "eval"
                            ScopeId = scope
                            Provider = "recorded"
                            ModelName = "m"
                            SystemPromptDigest = ""
                            SdkVersion = "t"
                        }
                    )

            Expect.isOk begun "begin"

            for t in
                [
                    turn "user" (content "user" "Weather in London?" [] [])
                    turn "assistant" (content "assistant" "" [ weatherCall ] [])
                    turn
                        "user"
                        (content "user" "" [] [
                            {
                                ToolCallId = "tc-1"
                                Content = "14C rain"
                            }
                        ])
                    turn "assistant" (content "assistant" "It is 14C with rain." [] [])
                ] do
                let! appended = (store :> IConversationWriter).AppendTurn(scope, id, t)
                Expect.isOk appended "append"

            let! read = (store :> IConversationReader).GetConversation(scope, id)

            let turns =
                match read with
                | Ok(_, turns, _) -> turns
                | Error e -> failtestf "read: %s" (ConversationError.toMessage e)

            let case, recording, toolResults = caseOfStoredTurns "stored" turns
            Expect.equal case.Turns [ "Weather in London?" ] "the tool-result turn is not a user turn"

            let fixture = {
                Name = "stored"
                Description = ""
                DefaultPrompt = "v1"
                Prompts = Map.ofList [ "v1", "p" ]
                Tools = []
                ToolResults = toolResults
                MaxToolRounds = 8
                Cases = [
                    {
                        case with
                            Assertions = [ ToolCalled("get_weather", [ "London" ]); Contains "14C" ]
                    }
                ]
                Recordings = Map.ofList [ "v1", Map.ofList [ "stored", recording ] ]
            }

            let! result = evaluate Recorded (JudgeOff "t") "t" "v1" fixture

            match result with
            | Error e -> failtestf "%s" e
            | Ok report ->
                Expect.equal report.CasesPassed 1 "the stored conversation replays green"
                Expect.equal report.PerCase.Head.FinalAnswer "It is 14C with rain." "same answer"
        }
    ]

let private judgeTests =
    let judged (reply: string) (minScore: float) =
        let fixture = weatherFixture ()

        let fixture = {
            fixture with
                Cases =
                    fixture.Cases
                    |> List.filter (fun c -> c.Judge.IsSome)
                    |> List.map (fun c -> {
                        c with
                            Judge = Some { Criteria = "c"; MinScore = minScore }
                    })
        }

        let judge =
            ScriptedProvider(
                "judge",
                [
                    for _ in fixture.Cases ->
                        {
                            Content = reply
                            ToolCalls = []
                            StopReason = "end_turn"
                            Usage = None
                        }
                ]
            )

        run Recorded (JudgeWith judge) "v1" fixture, judge

    testList "LLM-judge and live arms" [
        testCase "a judge reply is parsed strictly"
        <| fun _ ->
            Expect.equal
                (parseJudgeVerdict "```json\n{\"score\": 0.8, \"rationale\": \"ok\"}\n```")
                (Ok { Score = 0.8; Rationale = "ok" })
                "fenced"

            Expect.isError (parseJudgeVerdict """{"score": 1.5}""") "out of range"
            Expect.isError (parseJudgeVerdict """{"rationale": "no score"}""") "no score"
            Expect.isError (parseJudgeVerdict "I think it is fine") "no JSON"

        testCase "judge plumbing, offline: the judge sees the transcript and its score gates the case"
        <| fun _ ->
            let report, judge = judged """{"score": 0.9, "rationale": "good"}""" 0.7
            Expect.isNonEmpty report.PerCase "the committed fixture has a judged case"
            Expect.isTrue (report.PerCase |> List.forall _.Passed) "scored above the minimum"
            Expect.equal report.JudgeMeanScore (Some 0.9) "mean"

            Expect.stringContains
                judge.Requests.Head.Messages.Head.Content
                "get_weather"
                "the judge reads the tool loop"

            let low, _ = judged """{"score": 0.2, "rationale": "invented data"}""" 0.7
            Expect.isTrue (low.PerCase |> List.forall (fun c -> not c.Passed)) "below the minimum fails the case"

        testCase "with the judge off, a judged case reports Pending and does not fail"
        <| fun _ ->
            let report =
                run Recorded (JudgeOff "TOOLUP_AI_EVAL_JUDGE not set") "v1" (weatherFixture ())

            let judgedCases =
                report.PerCase |> List.filter (fun c -> c.JudgeStatus.StartsWith "pending")

            Expect.isNonEmpty judgedCases "pending reported"
            Expect.isTrue (judgedCases |> List.forall _.Passed) "pending is not a failure"
            Expect.equal report.JudgeMeanScore None "no mean"

        // The live arms: Pending unless configured, so VerifyAll stays green
        // with no key (GP 11). Configured, they run for real.
        match
            resolveLiveProvider
                ToolUp.Platform.ConfigKeys.Names.aiEvalJudge
                ToolUp.Platform.ConfigKeys.Names.aiEvalJudgeModel
        with
        | None -> ptestCase "live judge — skipped, TOOLUP_AI_EVAL_JUDGE not set" ignore
        | Some(Error e) -> testCase "live judge — misconfigured" (fun _ -> failtest e)
        | Some(Ok judge) ->
            testCase "live judge scores the committed fixture's judged cases"
            <| fun _ ->
                let report = run Recorded (JudgeWith judge) "v1" (weatherFixture ())
                Expect.isSome report.JudgeMeanScore "the live judge produced scores"

        match
            resolveLiveProvider
                ToolUp.Platform.ConfigKeys.Names.aiEvalProvider
                ToolUp.Platform.ConfigKeys.Names.aiEvalModel
        with
        | None -> ptestCase "live replay — skipped, TOOLUP_AI_EVAL_PROVIDER not set" ignore
        | Some(Error e) -> testCase "live replay — misconfigured" (fun _ -> failtest e)
        | Some(Ok provider) ->
            testCase "live replay runs every committed case through the tool loop"
            <| fun _ ->
                let report = run (Live provider) (JudgeOff "live replay") "v1" (weatherFixture ())

                printfn "live replay pass rate %.3f on %s" report.PassRate report.Model
                Expect.equal report.CaseCount (weatherFixture ()).Cases.Length "every case ran"
    ]

let private registeredTests =
    testList "ToolUp.AI.Evaluation" [
        fixtureTests
        toolDispatchTests
        rubricTests
        baselineTests
        storedConversationTests
        judgeTests
    ]

/// Phase 722 — the registered list plus the unregistered-`[<Tests>]`
/// guard, as every VerifyAll pack carries it.
let allTests =
    TestRegistrationGuard.withGuard (Assembly.GetExecutingAssembly()) 0 registeredTests

[<EntryPoint>]
let main argv =
    match List.ofArray argv with
    | "eval" :: rest ->
        // The report prints box-drawing and degree signs; a Windows console
        // on a legacy code page would otherwise mangle them.
        Console.OutputEncoding <- Text.Encoding.UTF8

        match parseEvalArgs rest with
        | Ok args -> runEval args
        | Error e ->
            eprintfn "✗ %s" e
            2
    // Sequenced by default, as every pack is (Phase 617): Expecto deadlocks
    // when parallel tests write to the console.
    | _ -> runTestsWithCLIArgs [ CLIArguments.Sequenced ] argv allTests