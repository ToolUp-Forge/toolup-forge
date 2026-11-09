// SPDX-License-Identifier: Apache-2.0
// Copyright (c) Andrew J. Willshire / ToolUp Analytics Ltd (UK)

namespace ToolUp.Platform.Transport

open System

/// Cross-cutting retry policy for any SDK-level operation that retries
/// on transient failure: retry expressed as data, not callbacks
/// (portability rule 3). One definition every retrying caller reaches.
///
/// Domain-specific retry policies that carry extra fields stay separate:
/// `JobRetryPolicy` (JobTypes), `WebhookRetryPolicy` (WebhookTypes),
/// `TransactionalRetryPolicy` (INotificationSink).
type RetryPolicy = {
    /// Total attempts including the first. `MaxAttempts = 1` is "no
    /// retries"; `MaxAttempts = 0` is invalid and rejected by every
    /// consumer's validators.
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
    /// The least a caller waits after a rate-limited failure (an HTTP 429),
    /// scaled by the attempt: the floor before the first retry, twice it
    /// before the second, and so on. A per-minute bucket is not refilled by
    /// a sub-second exponential pause. The floor is not capped by
    /// `MaxBackoff`; `None` treats a rate limit like any transient failure.
    RateLimitFloor: TimeSpan option
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
        RateLimitFloor = None
    }

    /// No retries; fail fast on the first failure. Use for health
    /// checks, latency-critical paths, and probes that prefer a clear
    /// error over a slow recovery.
    let noRetry: RetryPolicy = {
        MaxAttempts = 1
        InitialBackoff = TimeSpan.Zero
        MaxBackoff = TimeSpan.Zero
        Timeout = None
        RateLimitFloor = None
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
    /// internal timeout).
    let timeoutDescription (policy: RetryPolicy) : string =
        match policy.Timeout with
        | Some t -> sprintf "%d ms" (int t.TotalMilliseconds)
        | None -> "default timeout"

    /// A policy the retry loop can honour: at least one
    /// attempt, a non-negative initial backoff, and a cap no smaller than
    /// the initial backoff (a cap below it would make the backoff shrink).
    /// `Error` names the first rule broken. Pure — Fable-safe.
    let validate (policy: RetryPolicy) : Result<RetryPolicy, string> =
        if policy.MaxAttempts < 1 then
            Error $"MaxAttempts must be at least 1 (got {policy.MaxAttempts})"
        elif policy.InitialBackoff < TimeSpan.Zero then
            Error "InitialBackoff must not be negative"
        elif policy.MaxBackoff < policy.InitialBackoff then
            Error "MaxBackoff must be at least InitialBackoff"
        else
            Ok policy

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

    /// The pause after rate-limited attempt `attemptsMade` (1 = the first
    /// attempt): `RateLimitFloor` scaled by the attempt, never shorter than
    /// the policy's ordinary backoff. Without a floor, the ordinary backoff.
    let rateLimitDelay (policy: RetryPolicy) (attemptsMade: int) : TimeSpan =
        let backoff = delayFor policy (attemptsMade + 1)

        match policy.RateLimitFloor with
        | Some floor ->
            let scaled = TimeSpan.FromMilliseconds(floor.TotalMilliseconds * float attemptsMade)
            if scaled > backoff then scaled else backoff
        | None -> backoff