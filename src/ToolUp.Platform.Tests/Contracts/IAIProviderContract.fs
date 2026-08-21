// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

module ToolUp.Platform.Tests.Contracts.IAIProviderContract

// ─── IAIProvider conformance pack (Phase 67b) ─────────────────────
//
// Exercises the *backward-compat contract* of
// `IAIProvider.SendStructuredMessage`:
//
//   • Implementers that lack native structured-output delegate to
//     `IAIProviderDefaults.sendStructuredViaFallback`. The fallback
//     prepends the schema as a system-prompt instruction, calls
//     `SendMessage`, and post-validates the response is parseable
//     JSON.
//   • A JSON response is preserved verbatim in `AIProviderResponse.
//     Content` so callers can `JsonDocument.Parse` directly.
//   • A non-JSON or empty response surfaces as
//     `AIProviderError.SchemaUnsupported("structured-output", _)`.
//   • The fallback does NOT validate structurally against the
//     schema beyond "is this parseable JSON" — that's the contract
//     surface a non-native provider can honour without an external
//     schema-validator dependency.
//
// Live-API tests against the three shipped providers (Claude,
// OpenAI, Gemini) live in the per-provider integration TIDY-UP
// bundle (`Per-provider integration test pack for AIProviders
// companions`) — gated on real API keys, not part of this
// stub-driven contract pack.

open Expecto
open ToolUp.Platform
open ToolUp.Platform.AI

// ─── Stub provider ───────────────────────────────────────────────

/// `IAIProvider` whose `SendMessage` returns a canned response.
/// `SendStructuredMessage` delegates to the fallback helper — this
/// is the documented one-line adoption shape for non-native
/// implementers and the surface the contract exercises.
type private StubProvider(cannedContent: string) =
    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = false
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "stub-67b-contract"
            Model = "stub-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            return
                Ok {
                    Content = cannedContent
                    ToolCalls = []
                    StopReason = "end_turn"
                    Usage = None
                }
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

/// Stub that always returns Error — used to confirm the fallback
/// propagates non-Schema errors unchanged.
type private FailingProvider() =
    interface IAIProvider with
        member _.Capabilities = {
            Streaming = false
            ToolUse = false
            Vision = false
            SupportsPromptCaching = false
            SupportsTriage = false
            TriageModelId = None
            ProviderName = "stub-67b-contract-fail"
            Model = "stub-model"
        }

        member _.SendMessage(_messages, _tools, _systemPrompt, _onStream, _retryPolicy) = async {
            return Error(TransientNetwork "stub network failure")
        }

        member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
            IAIProviderDefaults.sendStructuredViaFallback
                (this :> IAIProvider)
                messages
                tools
                systemPrompt
                schema
                retryPolicy

// ─── Tests ───────────────────────────────────────────────────────

let private trivialSchema =
    """{"type":"object","properties":{"ok":{"type":"boolean"}},"required":["ok"]}"""

let tests: Test =
    testList "IAIProvider — SendStructuredMessage fallback contract" [
        testCaseAsync "JSON response → Ok with Content preserved as parseable JSON"
        <| async {
            let provider = StubProvider("""{"ok": true}""") :> IAIProvider

            let! result = provider.SendStructuredMessage([], [], None, trivialSchema, RetryPolicy.defaults)

            match result with
            | Ok r ->
                use doc = System.Text.Json.JsonDocument.Parse(r.Content)
                Expect.isTrue (doc.RootElement.GetProperty("ok").GetBoolean()) "ok=true round-trips"
            | Error e -> failtestf "expected Ok; got %s" (AIProviderError.toMessage e)
        }

        testCaseAsync "JSON with leading/trailing whitespace → trimmed and accepted"
        <| async {
            let provider = StubProvider("\n  {\"ok\": false}\n  ") :> IAIProvider

            let! result = provider.SendStructuredMessage([], [], None, trivialSchema, RetryPolicy.defaults)

            match result with
            | Ok r ->
                use doc = System.Text.Json.JsonDocument.Parse(r.Content)
                Expect.isFalse (doc.RootElement.GetProperty("ok").GetBoolean()) "ok=false round-trips"
            | Error e -> failtestf "expected Ok for whitespace-padded JSON; got %s" (AIProviderError.toMessage e)
        }

        testCaseAsync "Non-JSON response → Error SchemaUnsupported"
        <| async {
            let provider = StubProvider("This is plain prose, not JSON.") :> IAIProvider

            let! result = provider.SendStructuredMessage([], [], None, trivialSchema, RetryPolicy.defaults)

            match result with
            | Ok r ->
                failtestf "expected Error SchemaUnsupported for non-JSON content; got Ok with Content=%s" r.Content
            | Error(SchemaUnsupported(feature, _detail)) ->
                Expect.equal feature "structured-output" "feature label = structured-output"
            | Error other ->
                failtestf "expected SchemaUnsupported for non-JSON content; got %s" (AIProviderError.toMessage other)
        }

        testCaseAsync "Empty response → Error SchemaUnsupported"
        <| async {
            let provider = StubProvider("") :> IAIProvider

            let! result = provider.SendStructuredMessage([], [], None, trivialSchema, RetryPolicy.defaults)

            match result with
            | Ok r -> failtestf "expected Error SchemaUnsupported for empty content; got Ok with Content=%s" r.Content
            | Error(SchemaUnsupported(feature, _detail)) ->
                Expect.equal feature "structured-output" "feature label = structured-output"
            | Error other ->
                failtestf "expected SchemaUnsupported for empty content; got %s" (AIProviderError.toMessage other)
        }

        testCaseAsync "Whitespace-only response → Error SchemaUnsupported (treated as empty)"
        <| async {
            let provider = StubProvider("   \n\t  ") :> IAIProvider

            let! result = provider.SendStructuredMessage([], [], None, trivialSchema, RetryPolicy.defaults)

            match result with
            | Ok r ->
                failtestf "expected Error SchemaUnsupported for whitespace content; got Ok with Content=%s" r.Content
            | Error(SchemaUnsupported _) -> ()
            | Error other ->
                failtestf "expected SchemaUnsupported for whitespace content; got %s" (AIProviderError.toMessage other)
        }

        testCaseAsync "Transport errors from SendMessage propagate unchanged"
        <| async {
            let provider = FailingProvider() :> IAIProvider

            let! result = provider.SendStructuredMessage([], [], None, trivialSchema, RetryPolicy.defaults)

            match result with
            | Ok r -> failtestf "expected Error TransientNetwork; got Ok with Content=%s" r.Content
            | Error(TransientNetwork _) -> ()
            | Error other ->
                failtestf
                    "expected TransientNetwork (not SchemaUnsupported — non-schema errors must pass through unchanged); got %s"
                    (AIProviderError.toMessage other)
        }

        test "SchemaUnsupported is NOT retryable" {
            Expect.isFalse
                (AIProviderError.isRetryable (SchemaUnsupported("structured-output", "test")))
                "schema-conformance failures must not trigger another retry attempt"
        }

        test "SchemaUnsupported renders a human-readable message" {
            let msg =
                AIProviderError.toMessage (SchemaUnsupported("oneOf", "provider does not support oneOf"))

            Expect.stringContains msg "oneOf" "feature label appears in message"
            Expect.stringContains msg "provider does not support oneOf" "detail appears in message"
        }
    ]