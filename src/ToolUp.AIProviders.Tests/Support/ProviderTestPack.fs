// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.AIProviders.Tests.Support.ProviderTestPack

open System
open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI
open ToolUp.Platform.Providers
open ToolUp.Platform.Secrets
open ToolUp.AI
open ToolUp.AI.DefaultAIProviderFactory
open ToolUp.AIProviders.Tests.Support.InMemoryStores

// ─── Live-API integration pack ──────────────────────────────────────
//
// Parameterised on per-provider plumbing — env-var key, descriptor,
// `createWithApiKey` / `createWithApiKeyAndModel` helpers — so every
// shipped AIProvider companion exercises the same wire shape against
// the same canonical request. Closes Phase 67's deferred test-shaped
// tail without privileging Gemini.
//
// **Env-var gating.** Each pack reads its provider's API-key env var
// (`ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GEMINI_API_KEY`). When the
// key is missing the pack emits a single `pendingTestCase` — Expecto
// reports "Pending", not "Failed", so a fresh checkout is green
// without any per-provider credential. When the key is supplied the
// pack runs two live cases:
//
//   1. Direct round-trip — system prompt + user message + tool
//      definition + streaming. Asserts Ok response, populated usage,
//      and streaming-or-content evidence.
//   2. Factory round-trip — save a `ProviderEntry` to an
//      `InMemoryProviderProfile`, resolve via
//      `DefaultAIProviderFactory.Resolve`, hand the canonical request
//      to the resolved provider. Pins the factory's BYOK chain end to
//      end per provider (the blob-backed `IProviderProfile` is already
//      covered structurally by `IProviderProfileContract`; this pack
//      complements with the *factory* half of resolve-from-stored-entry).

// ─── Per-provider plumbing ─────────────────────────────────────────

/// Per-provider configuration for the parameterised pack. Each
/// provider's test file builds one of these and hands it to `tests`.
type ProviderSpec = {
    /// Human-readable label for the `testList` heading. Drives the
    /// Expecto report's per-provider grouping.
    DisplayName: string
    /// Env var carrying the live API key. `pendingTestCase` fires when
    /// it is absent or empty.
    EnvVarName: string
    /// Provider descriptor — `Id` doubles as the routing key for the
    /// factory round-trip's `ProviderEntry.ProviderId`.
    Descriptor: AIProviderDescriptor
    /// Direct construction path used by case 1 (no factory).
    CreateWithApiKey: string -> IAIProvider
    /// Curried `apiKey -> model -> IAIProvider`. Drops directly into
    /// `AIProviderBuilder.Build` for case 2.
    CreateWithApiKeyAndModel: string -> string -> IAIProvider
}

// ─── Canonical request shape ───────────────────────────────────────
//
// One small system prompt, one short user message, one trivial tool
// definition (presence-tested — the model may or may not call it), a
// streaming callback that accumulates emitted text. Identical bytes
// across providers so cross-provider conformance is the diff axis,
// not request-shape drift.

let private canonicalSystemPrompt =
    Some "You are a helpful assistant. Reply concisely in one short sentence."

let private canonicalMessages = [ AIProviderMessage.text "user" "Say hello in three words or fewer." ]

let private canonicalTools = [
    {
        Name = "get_current_time"
        Description = "Return the current UTC time as an ISO-8601 string."
        InputSchema = """{"type":"object","properties":{},"additionalProperties":false}"""
    }
]

/// Per-call timeout tuned for live API latency under a typical network.
/// 60 s comfortably covers a cold-start streaming response from any of
/// the three providers without making a stalled run blocked for the
/// whole `dotnet run` pipeline.
let private canonicalRetryPolicy = {
    RetryPolicy.defaults with
        MaxAttempts = 2
        Timeout = Some(TimeSpan.FromSeconds 60.0)
}

/// Drive a live provider through one streaming round-trip and assert
/// the response carries either streamed content or post-stream
/// `ToolCalls` / `Content`. Shared by both cases.
let private runCanonicalRoundTrip (provider: IAIProvider) = async {
    let acc = ref ""
    let onStream = Some(fun (chunk: string) -> acc.Value <- acc.Value + chunk)

    let! result =
        provider.SendMessage(canonicalMessages, canonicalTools, canonicalSystemPrompt, onStream, canonicalRetryPolicy)

    match result with
    | Error err -> failtestf "expected Ok; got %s" (AIProviderError.toMessage err)
    | Ok response ->
        // The model may answer in text OR by calling the tool. Either
        // surface is a healthy round-trip; what we're pinning is the
        // wire shape — request constructed cleanly, provider returned
        // a parseable AIProviderResponse.
        let hasContent = not (String.IsNullOrWhiteSpace response.Content)
        let hasToolCall = not (List.isEmpty response.ToolCalls)
        let streamingFired = acc.Value.Length > 0

        Expect.isTrue
            (hasContent || hasToolCall)
            (sprintf
                "response carries neither text content nor a tool call (Content=%s, ToolCalls=%d, StopReason=%s)"
                response.Content
                response.ToolCalls.Length
                response.StopReason)

        // Streaming MAY emit zero callbacks when the model returns
        // only tool_use blocks (no text deltas). Assert streaming
        // fired only when textual content actually came back.
        if hasContent then
            Expect.isTrue
                streamingFired
                "streaming callback should have fired at least once for a text-bearing response"

        Expect.isSome
            response.Usage
            "provider should populate AIProviderResponse.Usage on a healthy response (caching-capable providers always do)"
}

// ─── Factory round-trip plumbing ───────────────────────────────────

/// Standard test-user scope. `AccessContext.unrestricted
/// (AuthenticatedUser ...)`'s `configScope` resolves to
/// `{ Container = "user-test-aiproviders"; ... }` — the same string
/// becomes the `ISecretStore` scope id below.
let private testUserId = "test-aiproviders"

let private testScopeContainer = $"user-{testUserId}"

/// Build a factory that resolves the supplied provider via the
/// BYOK chain. `StrictBYOK` + no platform providers means missing
/// configuration surfaces immediately as `NoProviderConfigured` — the
/// factory only succeeds when the routing rule + secret are both
/// populated, which is exactly the round-trip we want to pin.
let private buildFactory (spec: ProviderSpec) (profile: IProviderProfile) (secrets: ISecretStore) =
    let builder = {
        Descriptor = spec.Descriptor
        Build = spec.CreateWithApiKeyAndModel
    }

    DefaultAIProviderFactory.create [ builder ] profile secrets StrictBYOK [] None

// ─── Pack ──────────────────────────────────────────────────────────

let tests (spec: ProviderSpec) : Test =
    let groupName = $"{spec.DisplayName} — live integration"

    match Environment.GetEnvironmentVariable spec.EnvVarName with
    | null
    | "" ->
        // Single Pending case so the Expecto report lists the
        // provider explicitly as skipped rather than absent.
        testList groupName [ ptestCase $"skipped — {spec.EnvVarName} not set" <| fun _ -> () ]
    | apiKey ->
        testList groupName [
            testCaseAsync "Direct round-trip — system + user + tool + streaming"
            <| async {
                let provider = spec.CreateWithApiKey apiKey
                do! runCanonicalRoundTrip provider
            }

            testCaseAsync "Factory round-trip — InMemoryProviderProfile entry + DefaultAIProviderFactory.Resolve"
            <| async {
                let secrets = InMemorySecretStore() :> ISecretStore
                let profile = InMemoryProviderProfile() :> IProviderProfile

                let scope = {
                    ScopeId = testUserId
                    Container = testScopeContainer
                    Persist = true
                }

                let secretKeyName = $"{spec.Descriptor.Id}/primary"

                match! secrets.SetSecret(testScopeContainer, secretKeyName, apiKey) with
                | Error e -> failtestf "secret-store seed failed: %s" e
                | Ok() -> ()

                let entry: ProviderEntry = {
                    Label = "primary"
                    ProviderId = spec.Descriptor.Id
                    Model = None
                    SecretKeyName = secretKeyName
                    Tags = []
                    Origin = CredentialOrigin.PastedKey
                    Health = ProviderHealth.unknown
                    UpdatedAt = DateTime.UtcNow
                }

                let initial = ProviderProfile.empty ()

                let profileBlob =
                    { initial with Entries = [ entry ] }
                    |> ProviderProfile.withRoute AIProviderSurface.aiAssistant None "primary"

                match! profile.Set(scope, profileBlob) with
                | Error e -> failtestf "provider-profile seed failed: %s" e
                | Ok() -> ()

                let factory = buildFactory spec profile secrets

                let ctx = AccessContext.unrestricted (AuthenticatedUser testUserId)

                match! factory.Resolve ctx with
                | Error err ->
                    failtestf
                        "factory failed to resolve a configured BYOK entry: %s"
                        (ProviderResolutionError.toMessage err)
                | Ok provider -> do! runCanonicalRoundTrip provider
            }
        ]