module ToolUp.Platform.Tests.InProcess.AIProviderProbeValidatorTests

open System
open Expecto
open ToolUp.Platform.AI
open ToolUp.Platform.ConfigValidation
open ToolUp.AI
open ToolUp.AI.AIProviderProbeValidator

// ─── Phase 9m.A AIProviderProbeValidator unit tests ──────────────────
//
// Real network is never reached — production HTTP behaviour is fed
// through `ProviderProbeSpec.Fetch`, which tests substitute with a
// pure function returning a canned `ProbeOutcome`. The
// `tryFromEnv` gate (probe-disabled-skips) is tested via real env-var
// snapshotting; everything past that registration gate is exercised
// against the test-seam constructor.

let private noCaps: AIProviderCapabilities = {
    Streaming = false
    ToolUse = false
    Vision = false
    SupportsPromptCaching = false
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
        member _.PlatformDescriptor = platform
        member _.Resolve _ctx = async { return Result.Error NoProviderConfigured }
        member _.TryResolveByLabel(_ctx, _label) = async { return Result.Error NoProviderConfigured }
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

let private spec (id: string) (envVar: string) (outcome: ProbeOutcome) : ProviderProbeSpec = {
    ProviderId = id
    ApiKeyEnvVar = envVar
    Fetch = fun _key -> async { return outcome }
}

let private specMap (specs: ProviderProbeSpec list) : Map<string, ProviderProbeSpec> =
    specs |> List.map (fun s -> s.ProviderId, s) |> Map.ofList

[<Tests>]
let tests =
    testList "AIProviderProbeValidator" [

        test "tryFromEnv — TOOLUP_AI_PROBE_ON_STARTUP unset → None (probe disabled)" {
            let result =
                withEnv "TOOLUP_AI_PROBE_ON_STARTUP" None (fun () ->
                    AIProviderProbeValidator.tryFromEnv (fakeFactory [] None))

            Expect.isNone result "probe must not register when env var unset"
        }

        test "tryFromEnv — TOOLUP_AI_PROBE_ON_STARTUP=0 → None (probe disabled)" {
            let result =
                withEnv "TOOLUP_AI_PROBE_ON_STARTUP" (Some "0") (fun () ->
                    AIProviderProbeValidator.tryFromEnv (fakeFactory [] None))

            Expect.isNone result "explicit '0' must keep the probe off"
        }

        test "tryFromEnv — TOOLUP_AI_PROBE_ON_STARTUP=1 → Some validator (probe enabled)" {
            let result =
                withEnv "TOOLUP_AI_PROBE_ON_STARTUP" (Some "1") (fun () ->
                    AIProviderProbeValidator.tryFromEnv (fakeFactory [] (Some claude)))

            Expect.isSome result "probe must register when env var = 1"
        }

        test "Nothing to probe — empty factory + no provider env → Ok (skip silently)" {
            let v = AIProviderProbeValidator.create (fakeFactory [] None) (specMap [])

            let result =
                withEnv "TOOLUP_AI_PROVIDER" None (fun () -> withEnv "ANTHROPIC_API_KEY" None (fun () -> runValidate v))

            Expect.equal result Ok "no descriptor + no env target → nothing to probe"
        }

        test "Unknown provider id → Warning (probe does not know how to query)" {
            let custom = descriptor "custom-llm" "v1" [ "v1" ]
            let v = AIProviderProbeValidator.create (fakeFactory [] (Some custom)) (specMap [])

            let result = runValidate v

            match result with
            | Warning msg ->
                Expect.stringContains msg "custom-llm" "names the unknown provider"
                Expect.stringContains msg "probe does not know" "explains the limitation"
                Expect.stringContains msg "TOOLUP_AI_PROBE_ON_STARTUP" "tells the operator how to silence"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "API-key env var unset → Warning (cannot probe)" {
            let testSpec =
                spec "anthropic-claude" "ANTHROPIC_API_KEY" (ProbeOk [ "claude-haiku-4-5-20251001" ])

            let v =
                AIProviderProbeValidator.create (fakeFactory [] (Some claude)) (specMap [ testSpec ])

            let result = withEnv "ANTHROPIC_API_KEY" None (fun () -> runValidate v)

            match result with
            | Warning msg ->
                Expect.stringContains msg "ANTHROPIC_API_KEY" "names the env var the probe expected"
                Expect.stringContains msg "anthropic-claude" "names the provider"
                Expect.stringContains msg "cannot probe" "explains the limitation"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Probe reachable + model in returned list → Ok" {
            let testSpec =
                spec
                    "anthropic-claude"
                    "ANTHROPIC_API_KEY"
                    (ProbeOk [ "claude-haiku-4-5-20251001"; "claude-sonnet-4-20250514" ])

            let v =
                AIProviderProbeValidator.create (fakeFactory [] (Some claude)) (specMap [ testSpec ])

            let result = withEnv "ANTHROPIC_API_KEY" (Some "test-key") (fun () -> runValidate v)

            Expect.equal result Ok "DefaultModel is in the returned list — Ok"
        }

        test "Probe reachable + empty returned list → Ok (don't punish minimal-permission keys)" {
            let testSpec = spec "anthropic-claude" "ANTHROPIC_API_KEY" (ProbeOk [])

            let v =
                AIProviderProbeValidator.create (fakeFactory [] (Some claude)) (specMap [ testSpec ])

            let result = withEnv "ANTHROPIC_API_KEY" (Some "test-key") (fun () -> runValidate v)

            // Empty list is treated as "endpoint responded but visibility
            // is restricted" — don't manufacture a Warning when we can't
            // be sure the list reflects what the chat path can call.
            Expect.equal result Ok "empty list → Ok (probe reachable, list-permission opaque)"
        }

        test "Probe reachable + model NOT in returned list → Warning (model not in access list)" {
            let testSpec =
                spec "anthropic-claude" "ANTHROPIC_API_KEY" (ProbeOk [ "claude-opus-4-20250514" ])

            let v =
                AIProviderProbeValidator.create (fakeFactory [] (Some claude)) (specMap [ testSpec ])

            let result = withEnv "ANTHROPIC_API_KEY" (Some "test-key") (fun () -> runValidate v)

            match result with
            | Warning msg ->
                Expect.stringContains msg "claude-haiku-4-5-20251001" "names the configured model"
                Expect.stringContains msg "not in the list" "explains the mismatch"
                Expect.stringContains msg "TOOLUP_AI_MODEL" "tells the operator how to fix"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Probe reachable + 401 → Warning (key refused)" {
            let testSpec =
                spec "anthropic-claude" "ANTHROPIC_API_KEY" (ProbeAuthError "HTTP 401")

            let v =
                AIProviderProbeValidator.create (fakeFactory [] (Some claude)) (specMap [ testSpec ])

            let result = withEnv "ANTHROPIC_API_KEY" (Some "test-key") (fun () -> runValidate v)

            match result with
            | Warning msg ->
                Expect.stringContains msg "anthropic-claude" "names the provider"
                Expect.stringContains msg "refused" "explains the auth failure"
                Expect.stringContains msg "ANTHROPIC_API_KEY" "names the env var the key came from"
            | other -> failtestf "expected Warning, got %A" other
        }

        test "Probe unreachable (DNS / network / 5xx) → Error (clear deploy failure)" {
            let testSpec =
                spec "anthropic-claude" "ANTHROPIC_API_KEY" (ProbeUnreachable "No such host is known")

            let v =
                AIProviderProbeValidator.create (fakeFactory [] (Some claude)) (specMap [ testSpec ])

            let result = withEnv "ANTHROPIC_API_KEY" (Some "test-key") (fun () -> runValidate v)

            match result with
            | Error msg ->
                Expect.stringContains msg "anthropic-claude" "names the provider"
                Expect.stringContains msg "unreachable" "explains the failure mode"
                Expect.stringContains msg "No such host is known" "passes through the underlying error"
            | other -> failtestf "expected Error, got %A" other
        }

        test "TOOLUP_AI_PROVIDER picks BYOK descriptor over platform default" {
            let byok = descriptor "openai-gpt" "gpt-4o" [ "gpt-4o" ]

            let testSpec = spec "openai-gpt" "OPENAI_API_KEY" (ProbeOk [ "gpt-4o" ])

            let v =
                AIProviderProbeValidator.create (fakeFactory [ byok ] (Some claude)) (specMap [ testSpec ])

            let result =
                withEnv "TOOLUP_AI_PROVIDER" (Some "openai-gpt") (fun () ->
                    withEnv "OPENAI_API_KEY" (Some "test-key") (fun () -> runValidate v))

            Expect.equal result Ok "TOOLUP_AI_PROVIDER override → probes openai-gpt, not claude"
        }
    ]