# ToolUp.EmbeddingProviders.OpenAI

OpenAI `text-embedding-3-small` `IEmbeddingProvider` for the `ToolUp.RAG` companion. API key resolved per call from the injected `ISecretStore`, scoped to `_platform`.

Licensed under Apache-2.0.

Part of the ToolUp Platform SDK — see [github.com/ToolUp-Forge/toolup-forge](https://github.com/ToolUp-Forge/toolup-forge) for full documentation.

## Quick start

```fsharp
open OpenAIEmbeddingProvider

// Default provider: text-embedding-3-small, 1536 dims, with the
// resilience defaults below (30 s timeout, 3-attempt retry, no breaker).
let embedder = create secretStore

// Preflight validator (presence-only) — fails the deploy if the secret
// is missing. Register alongside the embedder.
let validator = createValidator secretStore

// Readiness health probe (presence-only).
let health = OpenAIEmbeddingProviderHealth.create secretStore
```

## Resilience contract

API-backed embedding providers share a resilience contract defined by the SDK's `IEmbeddingProvider` module (`EmbedderResilience` / `EmbedderRetryPolicy` / `EmbedderCircuitBreaker`). Any future API-backed provider (Cohere, Voyage, Anthropic) inherits this pattern by consuming the same config types and classification helpers.

### Request timeout

Each provider instance sets `HttpClient.Timeout` to `EmbedderResilience.RequestTimeout` (**default 30 s**, replacing the BCL default of 100 s). A hung connection surfaces as a *retryable timeout* instead of stalling an ingest slot for a minute and a half.

### Retry + failure classification

Every call classifies a non-success response through the shared `EmbedderFailureClass` taxonomy and retries only what is retry-worthy:

| Condition | Class | Behaviour |
|---|---|---|
| `429` rate-limited | Transient | Retry, honouring the `Retry-After` header (capped at `MaxBackoff`) |
| `5xx` server error | Transient | Retry with exponential backoff + jitter |
| Timeout / network error | Transient | Retry with exponential backoff + jitter |
| `401` / `403` auth failure | Permanent | **No retry.** Emit a `KnowledgeEmbeddingProviderUnavailable` audit (when an event sink is wired) and raise `EmbeddingProviderUnavailableException` |
| Other `4xx` (400 / 404 …) | Permanent | **No retry.** Raise with the status + body |

Retry is expressed as data (`EmbedderRetryPolicy`) — `MaxAttempts` (inclusive of the first attempt; `1` = no retries), `InitialBackoff`, `MaxBackoff`, and a `JitterFactor` (default ±20 %) that de-synchronises a fleet of ingest workers so they don't hammer a recovering provider in lockstep.

When transient retries exhaust `MaxAttempts`, the call raises `EmbeddingProviderRetriesExhaustedException(attempts, lastError)`.

### Circuit breaker (opt-in)

Off by default. Opt in with `withEmbedderCircuitBreaker`:

```fsharp
open ToolUp.Platform.IEmbeddingProvider

let embedder =
    OpenAIEmbeddingOptions.defaults
    |> withEmbedderCircuitBreaker EmbedderCircuitBreaker.defaults   // open after 5 consecutive failures, 30 s cooldown
    |> createWithOptions secretStore
```

The breaker trips OPEN after `FailureThreshold` consecutive failed calls and fast-fails every call (raising `EmbeddingProviderCircuitOpenException`) for `Cooldown`, then allows a HALF-OPEN probe — success closes it, failure re-opens for another cooldown.

> **Statefulness.** Enabling the breaker makes the provider *stateful across calls*, so a breaker-enabled provider is **single-process only** (the documented portability rule-4 exception, alongside `LocalEmbeddingProvider`). With no breaker — the default — the provider is stateless per call and distributed-ready.

### Latency telemetry

Wire an `IMetricsSink` to emit per-call latency as the histogram `embedder.openai.latency_ms`, tagged `model` and `outcome` (`success` / `failure` / `timeout` / `network_error`):

```fsharp
let embedder =
    OpenAIEmbeddingOptions.defaults
    |> withEmbedderMetrics metricsSink
    |> createWithOptions secretStore
```

### Live probes (revoked-key detection)

The presence-only validator / health check confirm the secret resolves to a non-empty value — cheap, but they cannot tell a valid key from a revoked-but-non-empty one. The **live** variants issue one real `embeddings.create` (a single ~1-token input) to catch revoked / wrong keys and model-access mismatches:

```fsharp
// Preflight: WARNS (does not abort the deploy) on a revoked key — a key
// can be rotated in without a restart. Only an absent secret is a hard Error.
let liveValidator = createLiveValidator secretStore

// Readiness: Unhealthy naming the status ("embeddings.create returned 401")
// on a definitive auth failure; Degraded (not Unhealthy) on a transient blip
// so a brief OpenAI slowdown doesn't flip /ready to 503.
let liveHealth = OpenAIEmbeddingProviderHealth.createLive secretStore
```

## Configuration reference

Build a tuned provider from `OpenAIEmbeddingOptions.defaults` via the `with*` helpers, then `createWithOptions secretStore`:

| Helper | Effect |
|---|---|
| `withEmbedderModel model dims` | Explicit model + native dimension (validated) |
| `withEmbedderBatchSize n` | Per-call batch size for `GenerateEmbeddings` (default 64) |
| `withEmbedderTimeout t` | Per-call request timeout (default 30 s) |
| `withEmbedderRetryPolicy p` | Replace the retry policy |
| `withEmbedderResilience r` | Replace the whole resilience config |
| `withEmbedderCircuitBreaker cb` | Opt in to the circuit breaker |
| `withEmbedderMetrics sink` | Emit `embedder.openai.latency_ms` |
| `withEmbedderAudit eventStore` | Emit the platform-scoped unavailable audit on an auth failure |

The simple `create` / `createWithModel` / `createWithBatchSize` factories remain and apply the resilience defaults with no metrics / audit sink.
