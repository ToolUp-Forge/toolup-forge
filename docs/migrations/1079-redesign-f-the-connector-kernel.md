# A provider call is one function, and a connector is data

**Ships in:** the 0.42.0 minor (the core group and the ai-providers group). The change is breaking-class: the five
AI connector packages lose their provider classes, and `AIProviderResponse.StopReason` changes type.
`RetryPolicy.RateLimitFloor` is additive in meaning but is a new record field.

**Affected:** code that constructs a vendor provider with its class constructor (`ClaudeAIProvider(…)`,
`OpenAIProvider(…)`, `GeminiAIProvider(…)`, `CopilotAIProvider(…)`, `Microsoft365CopilotProvider(…)`); code that
compares or builds `AIProviderResponse.StopReason` as a string; code that builds a `RetryPolicy` as a full record
literal. A deployment that builds its providers through the `create*` helpers or a descriptor's `Build` changes nothing.
A custom `IAIProvider` is unaffected, and may be re-expressed as a `Connector`.

## What changes

1. **The kernel.** `ToolUp.AI.Server` gains `ProviderCall.send`: one vision preflight, one credential step, one attempt
   (buffered or streaming, its timeout linked to the caller's `CancellationToken`), one classification and one retry
   over the policy. A vendor is a `Connector<'S>` record: capabilities, model vocabulary, client, credential, endpoint,
   body, parser, stream processor, rate-limit floor and pause. `ConnectorProvider(connector, model)` implements
   `IAIProvider` and `IAIProviderModelOverride` once. The ambient async cancellation token rides into the call, so a
   cancelled turn aborts the in-flight HTTP request.
2. **Each connector is its `connector` function plus `create*` helpers.** The helpers that existed keep their names
   and signatures. New ones cover what only a constructor reached:

   | Constructor | Helper |
   |---|---|
   | `ClaudeAIProvider(apiKey)` / `(apiKey, model)` | `ClaudeAIProvider.createWithApiKey` / `createWithApiKeyAndModel` |
   | `ClaudeAIProvider(apiKey, model, maxTokens, effort)` | `ClaudeAIProvider.createWithApiKeyModelMaxTokensAndEffort` |
   | `ClaudeAIProvider(secretStore[, model])` | `ClaudeAIProvider.create` / `createWithModel` |
   | `ClaudeAIProvider(secretStore, model, maxTokens, effort)` | `ClaudeAIProvider.createWithSecretStore` |
   | `OpenAIProvider(apiKey \| secretStore[, model])` | `OpenAIProvider.createWithApiKey[AndModel]` / `create[WithModel]` |
   | `GeminiAIProvider(apiKey \| secretStore[, model])` | `GeminiAIProvider.createWithApiKey[AndModel]` / `create[WithModel]` |
   | `CopilotAIProvider(endpoint, apiKey \| credential[, model])` | `CopilotAIProvider.createWithApiKey…` / `createWithCredential…` |
   | `CopilotAIProvider(…, apiVersion)` | `CopilotAIProvider.createWithAuth endpoint apiVersion auth model` |
   | `Microsoft365CopilotProvider(source, options[, client])` | `Microsoft365CopilotAIProvider.createWithTokenSource` / `createWithHttpClient` |

   A key read on every call is `createWithKeySource fetch …` (Claude, OpenAI, Gemini). A custom `HttpClient` is
   `ConnectorProvider(<Vendor>.connector client …, model)`. `CopilotAuth` is public.
3. **`StopReason` is a union:** `StopReason.EndTurn | ToolUse | MaxTokens | Other of token`. `.Token` (or
   `StopReason.token`) is the old string, and `StopReason.ofToken` reads one back. A vendor reason the SDK does not name
   (Anthropic's `stop_sequence`, `refusal`) is `Other`, kept verbatim. `AILatencyRecord.StopReason` stays the recorded
   string.
4. **`RetryPolicy.RateLimitFloor : TimeSpan option`.** A 429 waits at least the floor, scaled by the attempt (the floor
   before the first retry, twice it before the second), uncapped by `MaxBackoff` and never shorter than the ordinary
   backoff. `None` keeps a rate limit on the ordinary schedule. The Claude connector brings Anthropic's 15 s floor when
   the policy names none. `RetryClass.RateLimited`, `TransportRetry.runPausing` and `RetryRunner.runPausing` /
   `classify` are new.
5. **The wire mappers.** Every `parseResponse` returns `Result<AIProviderResponse, AIProviderError>`.
   `Streaming.fs` holds `StreamProcessor<'S>` and three pure folds (`ClaudeStreaming`, `OpenAIStreaming`,
   `GeminiStreaming`); the mutable `StreamState` records are gone. Request bytes do not change.

## What to do

- Run the codemod: `dotnet run --project Build.fsproj -- Codemod <your source dir> --phase 1079`. Review rules
  `1079-claude-constructor`, `1079-openai-constructor`, `1079-gemini-constructor`, `1079-copilot-constructor` and
  `1079-stop-reason-token` list each site, with the replacement above.
- Replace each constructor with its helper.
- Match a response's stop reason on the union: `| { StopReason = StopReason.ToolUse } -> …`, or read `.Token`.
- Add `RateLimitFloor = None` to a full `RetryPolicy` literal, or build it as `{ RetryPolicy.defaults with … }`.

## Verify

Build. `ToolUp.AIProviders.Tests` holds every connector's request bytes for the four call shapes to the
`ToolUp.AI.Wire.Conformance` goldens, the floor on every send of every connector, and the kernel's cancellation.

## Rollback

Pin the previous minor. Nothing persisted changes shape.
