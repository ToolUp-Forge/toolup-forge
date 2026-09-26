# Migration — Phase 661: per-call model override on `IAIProvider`

**Status:** **additive.** No existing `IAIProvider` member changes; no shipped record widens on the
connector contract. Every existing connector, decorator and test double compiles byte-for-byte, and a
deployment that never names a model per call emits exactly the request bytes it emitted before.
One server-side telemetry record (`FastPathTriageResolver.TriageEventPayload`) gains two fields;
rows written before this phase decode with them filled in.

## What changes

`ToolUp.AI.Wire` (`Contract.fs`) gains the per-call vocabulary:

```fsharp skip=fragment
type AIProviderCallOptions = { Model: string option }          // None ⇒ the configured model, unchanged bytes
type ModelOverrideOutcome =
    | ConfiguredModel of model: string
    | OverrideHonoured of model: string
    | OverrideFellBack of requested: string * served: string * reason: string
type AIProviderCallResponse = { Response: AIProviderResponse; Model: ModelOverrideOutcome }
```

`ToolUp.Platform.Core` (`IAIProvider.fs`) gains an **optional** second interface and two extension
members on `IAIProvider`:

```fsharp skip=fragment
type IAIProviderModelOverride =
    abstract SendMessageWith: options * messages * tools * systemPrompt * onStream * retryPolicy -> Async<Result<AIProviderCallResponse, AIProviderError>>
    abstract SendStructuredMessageWith: options * messages * tools * systemPrompt * schema * retryPolicy -> Async<Result<AIProviderCallResponse, AIProviderError>>

// On every IAIProvider (type extension): dispatches to the interface above when the provider
// implements it, otherwise serves on the configured model and reports `OverrideFellBack`.
provider.SendMessageWith(AIProviderCallOptions.forModel "claude-haiku-4-5-20251001", messages, tools, systemPrompt, onStream, retryPolicy)
provider.SendStructuredMessageWith(options, messages, tools, systemPrompt, schema, retryPolicy)
```

Why an optional interface rather than two new abstract members: widening `IAIProvider` retypes every
shipped connector, decorator and test double — the break GP 11 forbids and the reason Phase 791.C put
the `ModelInput`-typed entry on a type extension. A caller never tests for the interface; the extension
does.

The shipped connectors (Claude, OpenAI, Copilot, Gemini) implement the interface. Each resolves the
requested id through `ModelOverrideOutcome.resolve` with its own family check (`ModelIdFamily`:
Anthropic serves `claude-*`, Gemini `gemini-*`/`gemma-*`, OpenAI and Copilot anything that carries
neither prefix) and serves the call through a sibling instance bound to that model. A provider that
**cannot** serve the named id falls back to its configured model and says so in the response —
the call is never failed for an unservable id. The shipped decorators (`MeteringProvider`,
`QuotaEnforcingProvider`, `AIFailoverProvider`, `QuotaGatedAIProvider`) forward the override path with
their own rule applied; metering bills the model that **served**.

`FastPathTriageResolver` now honours `Capabilities.TriageModelId` by itself. Precedence:

1. `FastPathTriageConfig.TriageProvider` set — the explicit escape hatch; it wins outright.
2. Otherwise, the turn provider's declared `TriageModelId` is named on the triage call through the
   override. No second provider instance is wired.
3. Neither — the plain structured send, as before.

`TriageEventPayload` gains `Route` (`triage-provider` | `turn-provider` | `override` |
`override-fallback` | `configured`) and `ServedModel`.

## Consumer action

**None required.** Existing code compiles unchanged.

- **You built a second provider instance for triage** (the pre-661 recipe in `AICompose.withFastPathTriage`'s
  doc): you can stop. Leave `TriageProvider = None`; the turn provider's declared `TriageModelId`
  is now honoured per call. Keep `withTriageProvider` only to route triage somewhere the turn
  provider's family cannot reach.
- **You wrote an `IAIProvider` connector:** optionally implement `IAIProviderModelOverride` so callers'
  per-call model requests reach your vendor. Until you do, calls through `SendMessageWith` are served on
  your configured model and reported as `OverrideFellBack` — nothing fails.
- **You wrote an `IAIProvider` decorator:** implement `IAIProviderModelOverride` and forward through
  `inner.SendMessageWith` / `inner.SendStructuredMessageWith` (the extensions), or the override is
  reported as fallen back at your layer whatever the inner provider can do.
- **You read triage telemetry rows** (`/dev/ai-fastpath`): rows now carry `Route` and `ServedModel`;
  older rows decode as `turn-provider` / the recorded `ProviderModel` via
  `TriageEventPayload.coerceLegacy`.

## Verification

- `ToolUp.Platform.Tests` → `Phase 661 — per-call model override` (resolution rule, dispatch,
  decorator forwarding) and `Phase 6j.B — Tier-3 fast-path triage` → `Phase 661 — which model serves
  the triage call` (selection order, fallback reporting, legacy-row decode).
- `ToolUp.AIProviders.Tests` → `Phase 661 — per-call model override (offline, per connector)` (each
  connector's family check; every connector implements the interface).

## Rollback

Revert the phase commit. Nothing persisted is written in a shape the previous code cannot read:
the two new `TriageEventPayload` fields are extra JSON members the pre-661 decoder ignores.
