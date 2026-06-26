// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform

open System

/// Cross-cutting retry policy for any SDK-level operation that retries
/// on transient failure. Phase 9c portability rule 3 — retry expressed
/// as data, not callbacks. Unified Phase 11.C.5 Tier 3: replaces the
/// pre-11.C.5 AI-side `RetryPolicy` (`{ MaxRetries; BackoffMs; TimeoutMs }`)
/// and audit-side `AuditReplicatorRetryPolicy`
/// (`{ MaxAttempts; InitialBackoff; MaxBackoff }`) with a single
/// definition consumers can reach across the SDK.
///
/// Domain-specific retry policies that carry extra fields stay separate:
/// `JobRetryPolicy` (JobTypes), `WebhookRetryPolicy` (WebhookTypes),
/// `TransactionalRetryPolicy` (INotificationSink). Those are explicitly
/// out of scope for the consolidation per the Phase 11.C.5 phase body.
type RetryPolicy = {
    /// Total attempts including the first. `MaxAttempts = 1` is "no
    /// retries"; `MaxAttempts = 0` is invalid and rejected by every
    /// consumer's validators. Semantic change from the pre-11.C.5
    /// AI-side `MaxRetries`: that field counted retries *after* the
    /// first attempt; this field counts the first attempt itself.
    /// Callers that today pass `MaxRetries = 3` (3 retries after first
    /// = 4 total attempts) migrate to `MaxAttempts = 4`.
    MaxAttempts: int
    /// Backoff before the second attempt. Subsequent attempts grow
    /// exponentially up to `MaxBackoff`.
    InitialBackoff: TimeSpan
    /// Cap on exponential growth so a wedged dependency doesn't park
    /// indefinitely.
    MaxBackoff: TimeSpan
    /// Optional per-call wall-clock timeout. `None` defers to the
    /// consumer's default (e.g. an AI provider's transport default,
    /// the audit replicator's batch-flush window). Set explicitly when
    /// the caller needs a tighter bound than the default. Carried on
    /// the policy record so a single `with`-binding (e.g.
    /// `{ RetryPolicy.defaults with Timeout = Some t }`) configures
    /// every relevant knob at one site.
    Timeout: TimeSpan option
}

module RetryPolicy =
    /// Conservative default suitable for interactive AI calls and
    /// transient-network-friendly background operations: 3 attempts,
    /// 500 ms initial backoff, 30 s cap, provider-default timeout.
    /// Audit replication and other long-running batch dispatchers
    /// override `MaxAttempts` / `MaxBackoff` per their own latency /
    /// budget envelopes.
    let defaults: RetryPolicy = {
        MaxAttempts = 3
        InitialBackoff = TimeSpan.FromMilliseconds 500.0
        MaxBackoff = TimeSpan.FromSeconds 30.0
        Timeout = None
    }

    /// No retries; fail fast on the first failure. Use for health
    /// checks, latency-critical paths, and probes that prefer a clear
    /// error over a slow recovery.
    let noRetry: RetryPolicy = {
        MaxAttempts = 1
        InitialBackoff = TimeSpan.Zero
        MaxBackoff = TimeSpan.Zero
        Timeout = None
    }

    /// Lower / upper bounds for a caller-supplied per-call timeout.
    /// `TimeSpan.Zero` or a negative value would make a
    /// `CancellationTokenSource` fire immediately (or throw); an
    /// absurdly large value never fires and silently defeats the
    /// timeout. Consumers clamp `Timeout` through `clampTimeoutMs`
    /// before constructing their per-call `CancellationTokenSource`.
    [<Literal>]
    let MinTimeoutMs = 1_000

    [<Literal>]
    let MaxTimeoutMs = 600_000

    /// Clamp a caller-supplied per-call timeout (ms) into the supported
    /// range. Pure integer math — safe to call from any consumer
    /// (including Fable).
    let clampTimeoutMs (ms: int) : int = max MinTimeoutMs (min MaxTimeoutMs ms)

    /// Human-readable description of the policy's `Timeout`, suitable
    /// for inclusion in transport error messages. Renders the millisecond
    /// value when `Some t` (e.g. "50000 ms"); renders "default timeout"
    /// when `None` (the consumer deferred to its transport's own
    /// internal timeout). Phase 11.C.5 Tier 3 — the AI providers'
    /// per-attempt timeout error messages call this to keep their
    /// format strings free of `Option<TimeSpan>` interpolation.
    let timeoutDescription (policy: RetryPolicy) : string =
        match policy.Timeout with
        | Some t -> sprintf "%d ms" (int t.TotalMilliseconds)
        | None -> "default timeout"

    /// `attemptNumber` is 1-indexed. `delayFor _ 1 = TimeSpan.Zero`
    /// (the first attempt fires immediately). Subsequent attempts wait
    /// `min(InitialBackoff * 2^(N-2), MaxBackoff)`.
    let delayFor (policy: RetryPolicy) (attemptNumber: int) : TimeSpan =
        if attemptNumber <= 1 then
            TimeSpan.Zero
        else
            let exponent = attemptNumber - 2
            let scaled = policy.InitialBackoff.TotalMilliseconds * (2.0 ** float exponent)
            let capped = min scaled policy.MaxBackoff.TotalMilliseconds
            TimeSpan.FromMilliseconds capped