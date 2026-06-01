# Phase 67b — `IAIProvider.SendStructuredMessage` (consumer migration)

**What changes.** `IAIProvider` gains a sibling abstract `SendStructuredMessage` for JSON-Schema-respecting structured output. Schema rides as a string parameter (same convention as `AIProviderToolDef.InputSchema`); providers translate to their native wire format (Gemini `responseSchema`, OpenAI `response_format: json_schema`, Claude tool-based workaround).

**Scope.** Forge SDK ships the interface change, the `IAIProviderDefaults.sendStructuredViaFallback` module helper for non-native providers, native implementations for the three shipped providers (Claude, OpenAI, Gemini), forwarding members on the three SDK-internal `IAIProvider` decorators (quota gate, metering, quota-enforcing), and the updated `docs/ai/api-reference.md` + `docs/ai/extending.md`. Existing `SendMessage`-only consumers are byte-identical post-upgrade — no migration action required.

**This is an additive opt-in.** Deployments that don't call `SendStructuredMessage` carry no extra IL beyond a vtable slot. The SDK-ADOPTION matrix row is all-⛔ N-A.

## Before / after — interface

```diff
 type IAIProvider =
     abstract Capabilities: AIProviderCapabilities
     abstract SendMessage:
         messages: AIProviderMessage list *
         tools: AIProviderToolDef list *
         systemPrompt: string option *
         onStream: (string -> unit) option *
         retryPolicy: RetryPolicy ->
             Async<Result<AIProviderResponse, AIProviderError>>
+    abstract SendStructuredMessage:
+        messages: AIProviderMessage list *
+        tools: AIProviderToolDef list *
+        systemPrompt: string option *
+        schema: string *
+        retryPolicy: RetryPolicy ->
+            Async<Result<AIProviderResponse, AIProviderError>>
+
+module IAIProviderDefaults =
+    val sendStructuredViaFallback:
+        provider: IAIProvider ->
+        messages: AIProviderMessage list ->
+        tools: AIProviderToolDef list ->
+        systemPrompt: string option ->
+        schema: string ->
+        retryPolicy: RetryPolicy ->
+            Async<Result<AIProviderResponse, AIProviderError>>
```

A new `AIProviderError` case is added; `SchemaUnsupported of feature: string * detail: string` is not-retryable.

```diff
 type AIProviderError =
     | TransientNetwork of message: string
     | TransientServer of statusCode: int * message: string
     | PermanentClient of statusCode: int * message: string
     | MalformedResponse of detail: string
     | StreamingAborted of partialText: string * detail: string
     | RetriesExhausted of attempts: int * lastError: AIProviderError
     | UnsupportedCapability of feature: string * detail: string
+    | SchemaUnsupported of feature: string * detail: string
```

## Per-provider native implementation strategy

### Gemini

`generationConfig.responseSchema` + `generationConfig.responseMimeType: "application/json"`. The forge-side wire layer already accepted a `structuredOutputSchema: JsonElement option` parameter — `SendStructuredMessage` parses the incoming schema string into a `JsonElement`, then dispatches a non-streaming `:generateContent` request with the schema attached.

### OpenAI

`response_format: { type: "json_schema", json_schema: { name: "structured_response", schema, strict: true } }`. Requires `gpt-4o-2024-08-06+` or `gpt-4o-mini`; older models reject with HTTP 400 (surfaced as `PermanentClient`). Wire-side `buildRequestBody` extended with the schema parameter; existing `SendMessage` call site passes `None` for a byte-identical legacy request body.

### Anthropic (Claude)

No native mode. The documented workaround is the tool-based pattern:
- Synthesise a tool whose `input_schema` is the supplied schema (`StructuredResponseToolName = "structured_response"`).
- Force `tool_choice = { type: "tool", name: "structured_response", disable_parallel_tool_use: true }`.
- The forced tool-call's `input` field IS the structured response — `SendStructuredMessage` extracts it and surfaces it as `AIProviderResponse.Content` for shape parity with the Gemini / OpenAI native paths.

**Limitation:** with `tool_choice` forcing the schema-tool, user-supplied tools become unreachable in the same turn. Canonical multi-turn pattern: run free-form tool-dispatch turns on `SendMessage` first, then a final `SendStructuredMessage` for the structured response.

## Backward-compat contract for non-native providers

External providers (or future shipped providers added later) MAY delegate to the helper in one line:

```fsharp
interface IAIProvider with
    member _.Capabilities = ...
    member _.SendMessage(...) = ...
    member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
        IAIProviderDefaults.sendStructuredViaFallback
            (this :> IAIProvider)
            messages tools systemPrompt schema retryPolicy
```

The fallback:
1. Prepends the schema as a system-prompt instruction (`"You MUST respond with a single JSON document..."`).
2. Calls `provider.SendMessage` with the augmented prompt.
3. Post-validates the response `Content` is parseable JSON. Non-JSON / empty content returns `AIProviderError.SchemaUnsupported("structured-output", detail)`.

No external schema-validator dependency — `System.Text.Json.JsonDocument.Parse` is the only validator. Best-effort: a malformed JSON returns the error; schema-conformance against the supplied schema is not enforced by the fallback (only by native impls).

## Verification steps

1. Solution build:

   ```bash
   cd toolup-forge
   dotnet build ToolUp.Forge.sln
   ```

   Expected: 0 errors. Existing `IAIProvider` implementers will receive a `FS0366 No implementation was given for ... SendStructuredMessage` error — see "Implementer migration" below.

2. Existing `SendMessage` behaviour unchanged:

   ```bash
   # Run the AI test suites (Expecto console runners — never `dotnet test`).
   dotnet run --project src/ToolUp.Platform.Tests/ToolUp.Platform.Tests.fsproj
   ```

3. Native structured-output round-trip — three providers:

   ```fsharp
   let schema = """{ "type": "object",
                     "properties": { "ok": { "type": "boolean" } },
                     "required": ["ok"] }"""
   let messages = [ AIProviderMessage.text "user" "Reply per the schema." ]
   let! result =
       provider.SendStructuredMessage(
           messages, [], None, schema, RetryPolicy.defaults)
   match result with
   | Ok r ->
       use doc = JsonDocument.Parse(r.Content)
       assert (doc.RootElement.TryGetProperty("ok") |> fst)
   | Error err -> failwith (AIProviderError.toMessage err)
   ```

   Repeat against Claude / OpenAI / Gemini with valid API keys in `_platform` scope.

4. Fallback round-trip:

   ```fsharp
   // Wrap any IAIProvider as a non-native implementer that delegates to the helper.
   // The default behaviour: response.Content is JSON, or Error SchemaUnsupported.
   ```

## Rollback

Revert the interface commit (`1fedfa8` and follow-ups `f382e60` Claude / `ffe10b7` OpenAI). The change is additive — reverting the interface change reverts every implementer; no consumer-side data migration required.

## Implementer migration (external `IAIProvider` impls)

External impls — provider companions outside the forge repo, custom test mocks, decorators — break with `FS0366`. Two adoption paths:

**Option 1 — One-line fallback delegation** (recommended for non-native providers and test mocks):

```fsharp
member this.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
    IAIProviderDefaults.sendStructuredViaFallback
        (this :> IAIProvider)
        messages tools systemPrompt schema retryPolicy
```

**Option 2 — Native impl** (for providers whose vendor supports structured-output natively). Mirror one of the three shipped providers' shapes in the forge tree as a worked example.

**Decorators** (quota gates, metering, proxies) forward to inner:

```fsharp
member _.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy) =
    inner.SendStructuredMessage(messages, tools, systemPrompt, schema, retryPolicy)
```

…optionally adding pre-/post-call gating identical to whatever the decorator does for `SendMessage`.

## Out of scope (this phase)

- Streaming structured-output (Gemini and OpenAI both support it; deferred until a consumer demands it).
- Schema-feature parity audit (`oneOf` / `anyOf` / `$ref` support varies). Advanced features produce `ProviderError.SchemaUnsupported` rather than silent miss.
- An engine-level dispatch hook on `AIAgentEngine` — primary v1 consumers (closed-loop authoring orchestrator, eval suite) are one-shot structured calls. The canonical multi-turn pattern is documented in `docs/ai/extending.md`.
