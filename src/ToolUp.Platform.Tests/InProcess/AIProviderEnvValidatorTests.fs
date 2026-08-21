module ToolUp.Platform.Tests.InProcess.AIProviderEnvValidatorTests

open System
open Expecto
open ToolUp.Platform.AI
open ToolUp.Platform.ConfigValidation
open ToolUp.AI

// ─── Phase 9m.A AIProviderEnvValidator + AIModelEnvValidator ─────────
//
// Pure-function checks: every test snapshots-then-clears the relevant
// env vars in a try / finally so the runner stays isolated regardless
// of host env-var state. Validators run synchronously via
// `Async.RunSynchronously` — no network in either of the always-on
// validators.

let private noCaps: AIProviderCapabilities = {
    Streaming = false
    ToolUse = false
    Vision = false
    SupportsPromptCaching = false
    SupportsTriage = false
    TriageModelId = None
    ProviderName = ""
    Model = ""
}

let private descriptor (id: string) (defaultModel: string) (supported: string list) : AIProviderDescriptor = {
    Id = id
    DisplayName = id
    SupportedModels = supported
    DefaultModel = defaultModel
    Capabilities = {
        noCaps with
            ProviderName = id
            Model = defaultModel
    }
}

let private fakeFactory
    (available: AIProviderDescriptor list)
    (platform: AIProviderDescriptor option)
    : IAIProviderFactory =
    { new IAIProviderFactory with
        member _.Available = available
        member _.PlatformDescriptors = platform |> Option.toList
        member _.PlatformDescriptor = platform
        member _.Resolve _ctx = async { return Result.Error NoProviderConfigured }
        member _.TryResolveByLabel(_ctx, _label) = async { return Result.Error NoProviderConfigured }
        member _.BuildPlatform(_providerId, _apiKey, _model) = None
    }

let private withEnv (name: string) (value: string option) (body: unit -> 'a) : 'a =
    let prior = Environment.GetEnvironmentVariable name

    try
        Environment.SetEnvironmentVariable(name, Option.toObj value)
        body ()
    finally
        Environment.SetEnvironmentVariable(name, prior)

let private runValidate (v: IConfigValidator) : ValidationResult = v.Validate() |> Async.RunSynchronously

let private claude =
    descriptor "anthropic-claude" "claude-haiku-4-5-20251001" [
        "claude-haiku-4-5-20251001"
        "claude-sonnet-4-20250514"
    ]

let private openai = descriptor "openai-gpt" "gpt-4o" [ "gpt-4o"; "gpt-4o-mini" ]

[<Tests>]
let providerTests =
    testList "AIProviderEnvValidator" [

        test "TOOLUP_AI_PROVIDER unset → Ok (silent skip, GP 13)" {
            let v = AIProviderEnvValidator.create (fakeFactory [ claude ] (Some claude))

            let result = withEnv "TOOLUP_AI_PROVIDER" None (fun () -> runValidate v)

            Expect.equal result Ok "no env var, no signal"
        }

        test "TOOLUP_AI_PROVIDER matches platform descriptor → Ok" {
            let v = AIProviderEnvValidator.create (fakeFactory [] (Some claude))

            let result =
                withEnv "TOOLUP_AI_PROVIDER" (Some "anthropic-claude") (fun () -> runValidate v)

            Expect.equal result Ok "platform-descriptor id matches"
        }

        test "TOOLUP_AI_PROVIDER matches BYOK Available entry → Ok" {
            let v = AIProviderEnvValidator.create (fakeFactory [ claude; openai ] None)

            let result =
                withEnv "TOOLUP_AI_PROVIDER" (Some "openai-gpt") (fun () -> runValidate v)

            Expect.equal result Ok "Available-list entry matches"
        }

        test "Typo'd TOOLUP_AI_PROVIDER → Warning naming actual available providers" {
            let v = AIProviderEnvValidator.create (fakeFactory [ openai ] (Some claude))

            let result = withEnv "TOOLUP_AI_PROVIDER" (Some "claud") (fun () -> runValidate v)

            match result with
            | Warning msg ->
                Expect.stringContains msg "TOOLUP_AI_PROVIDER" "names the env var"
                Expect.stringContains msg "claud" "quotes the typo'd value"
                Expect.stringContains msg "anthropic-claude" "names the platform descriptor"
                Expect.stringContains msg "openai-gpt" "names the BYOK provider"
                Expect.stringContains msg "silently ignored" "explains the runtime consequence"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Typo'd TOOLUP_AI_PROVIDER with empty factory → Warning explaining no providers registered" {
            let v = AIProviderEnvValidator.create (fakeFactory [] None)

            let result = withEnv "TOOLUP_AI_PROVIDER" (Some "claude") (fun () -> runValidate v)

            match result with
            | Warning msg -> Expect.stringContains msg "no platform provider" "explains empty-factory state"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Whitespace-only TOOLUP_AI_PROVIDER → Warning (trimmed to empty, still doesn't match any provider)" {
            let v = AIProviderEnvValidator.create (fakeFactory [ claude ] None)

            let result = withEnv "TOOLUP_AI_PROVIDER" (Some "   ") (fun () -> runValidate v)

            // Pin the trim semantics: a whitespace-only value is read
            // as Some "" (after Trim) and falls through to the
            // unknown-provider Warning. Future authors changing the
            // trim/empty semantics should re-read this assertion.
            match result with
            | Warning _ -> ()
            | other -> failtestf "expected Warning on whitespace-only env var, got %A" other
        }
    ]

[<Tests>]
let modelTests =
    testList "AIModelEnvValidator" [

        test "TOOLUP_AI_MODEL unset → Ok (silent skip, GP 13)" {
            let v = AIModelEnvValidator.create (fakeFactory [ claude ] (Some claude))

            let result = withEnv "TOOLUP_AI_MODEL" None (fun () -> runValidate v)

            Expect.equal result Ok "no env var, no signal"
        }

        test "TOOLUP_AI_MODEL matches SupportedModels of platform descriptor → Ok" {
            let v = AIModelEnvValidator.create (fakeFactory [] (Some claude))

            let result =
                withEnv "TOOLUP_AI_MODEL" (Some "claude-sonnet-4-20250514") (fun () -> runValidate v)

            Expect.equal result Ok "model is in platform descriptor's SupportedModels"
        }

        test "TOOLUP_AI_MODEL matches DefaultModel (which may not appear in SupportedModels)" {
            let oddball = descriptor "oddball" "default-only-model" [ "another-supported" ]

            let v = AIModelEnvValidator.create (fakeFactory [] (Some oddball))

            let result =
                withEnv "TOOLUP_AI_MODEL" (Some "default-only-model") (fun () -> runValidate v)

            Expect.equal result Ok "DefaultModel is accepted even when absent from SupportedModels"
        }

        test "Mismatched TOOLUP_AI_MODEL with TOOLUP_AI_PROVIDER set → Warning scoped to that provider" {
            let v = AIModelEnvValidator.create (fakeFactory [ claude; openai ] None)

            let result =
                withEnv "TOOLUP_AI_PROVIDER" (Some "openai-gpt") (fun () ->
                    withEnv "TOOLUP_AI_MODEL" (Some "gpt-99") (fun () -> runValidate v))

            match result with
            | Warning msg ->
                Expect.stringContains msg "TOOLUP_AI_MODEL" "names the env var"
                Expect.stringContains msg "gpt-99" "quotes the typo'd value"
                Expect.stringContains msg "openai-gpt" "names the scoped provider"
                Expect.stringContains msg "gpt-4o" "enumerates that provider's known models"
                // Make sure Claude models are NOT enumerated when scope
                // resolved to OpenAI — otherwise the Warning is noisier
                // than the operator's mental model.
                Expect.isFalse
                    (msg.Contains "claude-sonnet")
                    "Claude models should not appear when scope resolved to openai-gpt"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Mismatched TOOLUP_AI_MODEL with TOOLUP_AI_PROVIDER unset → Warning across known providers" {
            let v = AIModelEnvValidator.create (fakeFactory [ claude; openai ] None)

            let result =
                withEnv "TOOLUP_AI_PROVIDER" None (fun () ->
                    withEnv "TOOLUP_AI_MODEL" (Some "made-up") (fun () -> runValidate v))

            match result with
            | Warning msg ->
                Expect.stringContains msg "made-up" "quotes the typo'd value"
                Expect.stringContains msg "across known providers" "unscoped Warning wording"
                Expect.stringContains msg "claude-haiku-4-5-20251001" "Claude models enumerated"
                Expect.stringContains msg "gpt-4o" "OpenAI models enumerated"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Mismatched TOOLUP_AI_MODEL with unknown TOOLUP_AI_PROVIDER → fallback to all-known-providers list" {
            let v = AIModelEnvValidator.create (fakeFactory [ claude; openai ] None)

            let result =
                withEnv "TOOLUP_AI_PROVIDER" (Some "totally-unknown") (fun () ->
                    withEnv "TOOLUP_AI_MODEL" (Some "made-up") (fun () -> runValidate v))

            match result with
            | Warning msg ->
                Expect.stringContains msg "made-up" "quotes the typo'd value"
                Expect.stringContains msg "across known providers" "unknown provider falls back to global enumeration"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Empty descriptor list → Warning explains nothing-to-validate-against state" {
            let v = AIModelEnvValidator.create (fakeFactory [] None)

            let result = withEnv "TOOLUP_AI_MODEL" (Some "anything") (fun () -> runValidate v)

            match result with
            | Warning msg -> Expect.stringContains msg "no provider descriptors" "explains zero-descriptor state"
            | other -> failtestf "expected Warning, got %A" other
        }
    ]