module ToolUp.Platform.IEmbeddingProvider

open System

/// Identity tuple for an embedding model. Stamped onto every chunk's
/// metadata at upsert time (`_embedProvider` / `_embedModel` / `_embedDim`)
/// so the `ReembeddingService` can detect chunks that no longer match the
/// active provider — e.g. after a model swap from `text-embedding-3-small`
/// to `text-embedding-3-large` — and re-embed them in the background.
/// Also forms part of the `IEmbeddingCache` key so two providers that
/// happen to share a `Dimensions` value never collide in the cache.
type EmbeddingVersion = {
    ProviderId: string
    ModelId: string
    Dimensions: int
}

module EmbeddingVersion =
    /// `_embedProvider` chunk-metadata key.
    [<Literal>]
    let MetadataProviderKey = "_embedProvider"

    /// `_embedModel` chunk-metadata key.
    [<Literal>]
    let MetadataModelKey = "_embedModel"

    /// `_embedDim` chunk-metadata key.
    [<Literal>]
    let MetadataDimensionsKey = "_embedDim"

/// Converts text into a dense float vector suitable for semantic similarity
/// search. Implementations are companion packages under `src/EmbeddingProviders/`;
/// the interface carries no provider-specific types or dependencies.
///
/// Satisfies Phase 9c portability rule 2 (async at every boundary) and
/// rule 4 (stateless per call — no in-memory state assumed between invocations).
type IEmbeddingProvider =
    /// Generate a dense embedding for the given text. The returned array has
    /// length `Dimensions`. Callers must not assume any particular normalisation;
    /// `IVectorStore` and `IRetrievalPipeline` implementations are responsible
    /// for normalising before cosine similarity if required.
    abstract GenerateEmbedding: text: string -> Async<float32 array>
    /// Generate dense embeddings for a sequence of texts in one call. The
    /// returned array is the same length and order as the input sequence;
    /// each inner array has length `Dimensions`. Implementations with native
    /// batch APIs (OpenAI, Cohere, Voyage, etc.) should issue a single HTTP
    /// call to amortise round-trip latency over N inputs — a 100-chunk
    /// document goes from ~15s of sequential HTTP calls to ~150ms of one
    /// batched call. Implementations without a batch path can delegate to
    /// `IEmbeddingProvider.batchedFallback` which fans out to N parallel
    /// `GenerateEmbedding` calls. Empty input yields an empty array.
    abstract GenerateEmbeddings: texts: string seq -> Async<float32 array array>
    /// The dimensionality of vectors produced by this provider. Must be constant
    /// for the lifetime of the provider instance. `IVectorStore` implementations
    /// may use this at initialisation to size index structures correctly.
    abstract Dimensions: int
    /// Stable identifier for the provider family — `"local"`, `"openai"`,
    /// `"cohere"`, etc. Combined with `ModelId` and `Dimensions` to form
    /// the `EmbeddingVersion` stamp; must not change for the lifetime of the
    /// instance, and should not vary across processes for the same provider.
    abstract ProviderId: string
    /// Model identifier within the provider — `"text-embedding-3-small"`,
    /// `"local-tfidf-v1"`, etc. Distinct values produce distinct cache keys
    /// and trigger background re-embedding when changed.
    abstract ModelId: string

/// Helper for `IEmbeddingProvider` implementations that don't have a native
/// batch endpoint. Fans the input sequence out to parallel single-text calls
/// and assembles the results in input order. Pass the provider's own
/// `GenerateEmbedding` member as `single` from inside its `GenerateEmbeddings`
/// implementation — the fan-out is bounded by the underlying async scheduler.
let batchedFallback (single: string -> Async<float32 array>) (texts: string seq) = async {
    let arr = texts |> Seq.toArray

    if arr.Length = 0 then
        return Array.empty
    else
        let! results = arr |> Array.map single |> Async.Parallel
        return results
}

// ─── Phase 14u — API-backed embedding-provider resilience ─────────
//
// Shared config surface for API-backed `IEmbeddingProvider`
// implementations (OpenAI today; Cohere / Voyage / Anthropic inherit
// the same contract). Pure data + typed exceptions only — no live
// framework handle, no `System.Net.Http` type, so this stays inside
// the SDK's minimal-dependency floor and Fable-compiles unchanged.
// The classification mirrors the Phase 14t ingestion-retry taxonomy so
// the two layers agree on what "transient" means: a 429 or 5xx is
// retry-worthy, an auth 4xx is not.

/// Retry policy for a single API-backed embedding call. Mirrors the
/// Phase 14t `IngestionRetryPolicy` / the SDK's unified `RetryPolicy`
/// shape (retry expressed as data — Phase 9c portability rule 3), plus
/// a `JitterFactor` so a fleet of ingest workers doesn't resynchronise
/// its backoff into a thundering-herd against a recovering provider.
/// `MaxAttempts` is inclusive of the first attempt: `1` = no retries.
type EmbedderRetryPolicy = {
    /// Total attempts including the first. `1` is "no retries".
    MaxAttempts: int
    /// Backoff before the second attempt; grows exponentially up to
    /// `MaxBackoff`.
    InitialBackoff: TimeSpan
    /// Cap on exponential growth so a wedged provider doesn't park a
    /// retry for minutes.
    MaxBackoff: TimeSpan
    /// Proportional jitter applied to each computed backoff, in
    /// `[0.0, 1.0]`. `0.2` spreads each delay by ±20 %. `0.0` disables
    /// jitter (deterministic backoff, e.g. for tests).
    JitterFactor: float
}

module EmbedderRetryPolicy =
    /// SDK default: 3 attempts, 500 ms initial backoff, 30 s cap, ±20 %
    /// jitter. Tuned for OpenAI's per-key token-rate limiter, which
    /// typically clears a 429 window inside a few seconds.
    let defaults: EmbedderRetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromMilliseconds 500.0
        MaxBackoff = TimeSpan.FromSeconds 30.0
        JitterFactor = 0.2
    }

    /// Fail fast on the first failure — no retries. Use for latency-
    /// critical paths and probes that prefer a clear error over a slow
    /// recovery.
    let noRetry: EmbedderRetryPolicy = {
        MaxAttempts = 1
        InitialBackoff = TimeSpan.Zero
        MaxBackoff = TimeSpan.Zero
        JitterFactor = 0.0
    }

    /// Base (un-jittered) delay before attempt `attemptNumber`
    /// (1-indexed). `1` fires immediately (`TimeSpan.Zero`); subsequent
    /// attempts wait `min(InitialBackoff * 2^(N-2), MaxBackoff)`.
    let baseDelayFor (policy: EmbedderRetryPolicy) (attemptNumber: int) : TimeSpan =
        if attemptNumber <= 1 then
            TimeSpan.Zero
        else
            let exponent = attemptNumber - 2
            let scaled = policy.InitialBackoff.TotalMilliseconds * (2.0 ** float exponent)
            let capped = min scaled policy.MaxBackoff.TotalMilliseconds
            TimeSpan.FromMilliseconds capped

    /// Apply proportional jitter to a base delay. `rand` is a uniform
    /// draw in `[0.0, 1.0)` (the caller supplies it so the function
    /// stays pure and testable); the result is spread by
    /// `±JitterFactor` and clamped at zero.
    let jitter (policy: EmbedderRetryPolicy) (baseDelay: TimeSpan) (rand: float) : TimeSpan =
        if policy.JitterFactor <= 0.0 then
            baseDelay
        else
            let span = baseDelay.TotalMilliseconds * policy.JitterFactor
            // rand in [0,1) → offset in [-span, +span)
            let offset = (rand * 2.0 - 1.0) * span
            TimeSpan.FromMilliseconds(max 0.0 (baseDelay.TotalMilliseconds + offset))

/// Optional circuit breaker for an API-backed embedding provider. Off
/// by default (a provider is constructed with `CircuitBreaker = None`);
/// opt in via the provider's `withEmbedderCircuitBreaker`. When enabled
/// the provider trips OPEN after `FailureThreshold` consecutive failed
/// calls and fast-fails every call for `Cooldown`, then allows a single
/// HALF-OPEN probe — success closes it, failure re-opens for another
/// `Cooldown`. Enabling the breaker makes the provider *stateful across
/// calls*, so a breaker-enabled provider is single-process (Phase 9c
/// portability rule 4's documented exception, alongside
/// `LocalEmbeddingProvider`).
type EmbedderCircuitBreaker = {
    /// Consecutive failed calls that trip the breaker OPEN.
    FailureThreshold: int
    /// How long the breaker stays OPEN before allowing a HALF-OPEN
    /// probe.
    Cooldown: TimeSpan
}

module EmbedderCircuitBreaker =
    /// Conservative default: open after 5 consecutive failures, probe
    /// again after 30 s. Only in force when a provider is explicitly
    /// wired with a breaker.
    let defaults: EmbedderCircuitBreaker = {
        FailureThreshold = 5
        Cooldown = TimeSpan.FromSeconds 30.0
    }

/// Bundled resilience configuration threaded into an API-backed
/// embedding provider at construction. `RequestTimeout` replaces the
/// BCL `HttpClient` default of 100 s with a tighter bound so a hung
/// connection surfaces as a retryable timeout instead of stalling an
/// ingest slot; `Retry` governs the per-call retry loop; `CircuitBreaker`
/// is off unless opted in.
type EmbedderResilience = {
    RequestTimeout: TimeSpan
    Retry: EmbedderRetryPolicy
    CircuitBreaker: EmbedderCircuitBreaker option
}

module EmbedderResilience =
    /// SDK default: 30 s per-call timeout, `EmbedderRetryPolicy.defaults`
    /// retry, no circuit breaker.
    let defaults: EmbedderResilience = {
        RequestTimeout = TimeSpan.FromSeconds 30.0
        Retry = EmbedderRetryPolicy.defaults
        CircuitBreaker = None
    }

/// Classification of a non-success embedding-provider HTTP response.
/// Mirrors the Phase 14t ingestion-retry taxonomy so the provider and
/// the ingestion service agree on retryability: 429 / 5xx are
/// `Transient` (retry with backoff); every other 4xx — 401 / 403 auth,
/// 400 bad request, 404 model-not-found — is `Permanent` (the same
/// request fails identically, so retrying only burns the rate limiter).
[<RequireQualifiedAccess>]
type EmbedderFailureClass =
    | Transient
    | Permanent

module EmbedderFailureClass =
    /// Classify by HTTP status. 429 (rate-limited) or any 5xx →
    /// `Transient`; every other status → `Permanent`.
    let ofStatus (statusCode: int) : EmbedderFailureClass =
        if statusCode = 429 || statusCode >= 500 then
            EmbedderFailureClass.Transient
        else
            EmbedderFailureClass.Permanent

    /// Auth failures (401 / 403) are the permanent class that also
    /// warrants a `KnowledgeEmbeddingProviderUnavailable` audit — a
    /// revoked / wrong key, not a transient blip.
    let isAuthFailure (statusCode: int) : bool = statusCode = 401 || statusCode = 403

/// SDK-level audit event type emitted when an API-backed embedding
/// provider is definitively unavailable for auth reasons (a permanent
/// 401 / 403). Emitted under the reserved `_platform` scope by the
/// provider itself when an event sink is wired, and re-emitted with
/// tenant scope by the Phase 14t ingestion retry/dead-letter path.
/// Exposed as a `[<Literal>]` so both layers — and consumers building
/// alerting hooks — key off the same string.
[<Literal>]
let KnowledgeEmbeddingProviderUnavailableEvent =
    "KnowledgeEmbeddingProviderUnavailable"

/// Raised by an API-backed provider when a call fails permanently for
/// auth reasons (401 / 403) after classification — never retried. The
/// Phase 14t ingestion path catches this to dead-letter the chunk and
/// raise the tenant-scoped operator notification; a direct caller sees
/// an actionable message naming the status.
exception EmbeddingProviderUnavailableException of statusCode: int * body: string

/// Raised when an API-backed provider's circuit breaker is OPEN — the
/// call was fast-failed without hitting the network. Distinct from
/// `EmbeddingProviderUnavailableException` so a caller can tell "the
/// breaker is protecting a known-down provider" from "this specific
/// call was rejected".
exception EmbeddingProviderCircuitOpenException of message: string

/// Raised when transient failures exhaust the retry budget
/// (`EmbedderRetryPolicy.MaxAttempts`). Carries the attempt count and
/// the last underlying error message.
exception EmbeddingProviderRetriesExhaustedException of attempts: int * lastError: string

/// Metric-name helpers for API-backed embedding providers. The latency
/// histogram is `embedder.{providerId}.latency_ms` (Phase 9e
/// `IMetricsSink`), so operators see per-provider degradation without a
/// per-model cardinality explosion (`model` rides as a tag, not the
/// name).
module EmbedderMetrics =
    /// `embedder.{providerId}.latency_ms` — per-call latency histogram
    /// name for the given provider id.
    let latencyMetricName (providerId: string) : string =
        sprintf "embedder.%s.latency_ms" providerId