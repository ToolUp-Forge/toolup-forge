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

// ─── Live-API behavioural-conformance pack ──────────────────────────
//
// Parameterised on per-provider plumbing — env-var key, descriptor,
// `createWithApiKey` / `createWithApiKeyAndModel` helpers, a non-vision
// model id — so every shipped AIProvider companion (and any future
// fourth provider) exercises the SAME conformance set against the same
// canonical inputs, with zero copied assertions. Closes Phase 67's
// deferred test-shaped tail (wire shape) AND adds Phase 181's
// behavioural-contract bar (structured output + capability gating)
// without privileging any one provider.
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
    /// A model id this provider classifies as NON-vision-capable
    /// (`Capabilities.SupportsVisionInput = false`). Drives the
    /// capability-gating conformance arm: a multimodal message routed
    /// to this model must be rejected synchronously with
    /// `UnsupportedCapability("vision", _)` BEFORE any vendor round-trip,
    /// so the arm makes no network call and never validates the key.
    /// (Need not be a currently-available model — the rejection fires
    /// purely from the provider's `isVisionCapable` classifier.)
    NonVisionModel: string
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

// ─── Behavioural-conformance arms (Phase 181) ──────────────────────
//
// The two arms below lift the pack from a *wire-shape* check (request
// constructed cleanly, response parses) to a *behavioural-contract*
// check over the interface surface `IAIProvider` documents:
// structured-output (Phase 67b) and capability-gating (Phase 6o).
// Both are threaded through `ProviderSpec`, so every shipped provider
// — and any future fourth provider — gets the identical bar with zero
// copied assertions.

/// A trivial object schema: typed root, every property required,
/// `additionalProperties:false`. This is the shape every native
/// structured-output mode accepts (OpenAI strict `json_schema`,
/// Gemini `responseSchema`, Claude's forced-tool `input_schema`).
let private trivialObjectSchema =
    """{"type":"object","properties":{"greeting":{"type":"string"}},"required":["greeting"],"additionalProperties":false}"""

/// A schema whose ROOT is a `oneOf` union rather than a typed object.
/// The native structured-output modes require an object root, so this
/// is the "schema the native mode cannot honour" probe. How a provider
/// rejects it varies — and that variance IS the native-vs-fallback
/// boundary Phase 67b documents:
///   • a provider that pre-validates surfaces `SchemaUnsupported`;
///   • a provider that ships the schema to the vendor surfaces the
///     vendor's HTTP 4xx as `PermanentClient`.
/// Both are clean, terminal (non-retryable) rejections. The conformance
/// bar forbids only the two failure modes: a silent `Ok` carrying
/// non-conformant content, or a retryable error that would churn the
/// retry budget on an un-fixable request. (A provider that legitimately
/// honours a `oneOf` root is accepted too — provided its `Ok` still
/// carries parseable JSON.)
let private nonConformantSchema =
    """{"oneOf":[{"type":"object","properties":{"a":{"type":"string"}},"required":["a"]},{"type":"object","properties":{"b":{"type":"integer"}},"required":["b"]}]}"""

/// Structured-output conformance: drive `SendStructuredMessage` against
/// a conformable schema (expect `Ok` with JSON-parseable `Content`) and
/// against a non-conformable schema (expect a clean terminal rejection,
/// or a conformant `Ok` — never silent non-JSON, never a retry-storm).
let private runStructuredOutputConformance (provider: IAIProvider) = async {
    let! okResult =
        provider.SendStructuredMessage(
            [ AIProviderMessage.text "user" "Return a short greeting." ],
            [],
            Some "You return only the requested JSON document — no prose, no Markdown fences.",
            trivialObjectSchema,
            canonicalRetryPolicy
        )

    match okResult with
    | Error err -> failtestf "structured-output (object schema): expected Ok; got %s" (AIProviderError.toMessage err)
    | Ok response ->
        Expect.isFalse
            (String.IsNullOrWhiteSpace response.Content)
            "structured-output response should carry Content (the JSON document)"

        try
            use _ = System.Text.Json.JsonDocument.Parse(response.Content)
            ()
        with ex ->
            failtestf "structured-output Content is not parseable JSON (%s): %s" ex.Message response.Content

    let! badResult =
        provider.SendStructuredMessage(
            [ AIProviderMessage.text "user" "Return a value." ],
            [],
            None,
            nonConformantSchema,
            canonicalRetryPolicy
        )

    match badResult with
    | Ok response ->
        // A provider that genuinely supports a oneOf root is acceptable,
        // but its Ok must still carry conformant (parseable) JSON.
        try
            use _ = System.Text.Json.JsonDocument.Parse(response.Content)
            ()
        with _ ->
            failtestf "non-conformant schema: an Ok must still carry parseable JSON; got Content=%s" response.Content
    | Error(SchemaUnsupported _)
    | Error(PermanentClient _) -> () // the two expected clean-rejection shapes
    | Error err ->
        // Any other error is acceptable only if it is terminal — a
        // retryable error against an un-fixable schema is the failure
        // mode the bar exists to catch.
        Expect.isFalse
            (AIProviderError.isRetryable err)
            (sprintf
                "non-conformant schema: expected a clean terminal rejection (SchemaUnsupported / PermanentClient) or a conformant Ok; got retryable %s"
                (AIProviderError.toMessage err))
}

/// Bytes for the capability-gating probe. Tiny + all-zero — the image
/// never leaves the process (the vision pre-check fails synchronously
/// before any wire encoding), so neither size nor validity matters.
let private capabilityProbeImageBytes = Array.create 64 0uy

/// Capability-gating conformance: a multimodal message routed to a
/// non-vision model must fail synchronously with
/// `UnsupportedCapability("vision", _)` — no vendor HTTP 400. Makes no
/// network call, so it costs nothing even with a live key set.
let private runCapabilityGatingConformance (spec: ProviderSpec) (apiKey: string) = async {
    let visionMessage =
        AIProviderMessage.multipart "user" [
            TextPart "Describe this image."
            ImagePart {
                MediaType = "image/png"
                Source = Base64Bytes capabilityProbeImageBytes
            }
        ]

    let provider = spec.CreateWithApiKeyAndModel apiKey spec.NonVisionModel

    let! result = provider.SendMessage([ visionMessage ], [], None, None, canonicalRetryPolicy)

    match result with
    | Error(UnsupportedCapability("vision", _)) -> ()
    | Error err ->
        failtestf
            "capability-gating: expected UnsupportedCapability(\"vision\", _) against non-vision model '%s'; got %s"
            spec.NonVisionModel
            (AIProviderError.toMessage err)
    | Ok _ ->
        failtestf
            "capability-gating: non-vision model '%s' accepted image input (expected a synchronous UnsupportedCapability rejection)"
            spec.NonVisionModel
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

            // ── Phase 181 behavioural-conformance arms ──
            testCaseAsync "Conformance — structured output (Ok JSON + non-conformant schema rejected cleanly)"
            <| async {
                let provider = spec.CreateWithApiKey apiKey
                do! runStructuredOutputConformance provider
            }

            testCaseAsync "Conformance — capability gating (vision rejected synchronously against a non-vision model)"
            <| async { do! runCapabilityGatingConformance spec apiKey }
        ]